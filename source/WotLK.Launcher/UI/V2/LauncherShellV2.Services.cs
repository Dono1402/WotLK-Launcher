using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private ResourceDictionary? _minecraftChrome;
    private readonly Dictionary<string, Brush> _atlasChrome = new();
    private bool? _chromeIsMinecraft;
    private bool? _headerIsWotlk;
    private int _headerMotionGeneration;
    private readonly Dictionary<LauncherService, LauncherShellPage> _lastGamePages = new();

    // Presentation only: the existing survival server's identity, independent of WotLK's runtime.
    // No Minecraft launch or status client is connected yet; commands remain disabled and metrics unknown.
    public GameUiState MinecraftGameState { get; } = new()
    {
        ServiceLabel = "MINECRAFT",
        RealmLabel = "Vanilla Survival",
        ServerEditionLabel = "Java 26.2 · Survie",
        Title = "Bienvenue dans\nl’Overworld",
        Subtitle = "Ton aventure cubique commence ici.",
        Motto = "E X P L O R E R\nC O N S T R U I R E\nS U R V I V R E\nE N S E M B L E",
        IsPrimaryActionEnabled = false,
        PrimaryActionUnavailableReason = "Le lancement de Minecraft n’est pas encore configuré.",
        AvailabilityNotice = "Le lancement depuis Atlas sera disponible prochainement.",
        IsClientReady = false,
        ClientVersion = string.Empty,
        InstallPath = string.Empty,
        ClientStatus = "Non configuré",
        IsOptionsEnabled = false,
        IsVerifyEnabled = false,
        IsRetryEnabled = false
    };

    public DashboardUiState MinecraftDashboardState { get; } = new();

    private void SyncMinecraftPatchNotes()
    {
        DashboardViewState notes = DashboardState.Current;
        // Only launcher release notes are shared. Never copy WotLK status, population or latency.
        MinecraftDashboardState.ApplyView(DashboardViewState.Initial with
        {
            RealmStatusWideLabel = "Suivi Minecraft bientôt disponible",
            RealmToolTip = "Le suivi du serveur Minecraft n’est pas encore connecté.",
            GatewayLatencyToolTip = "La latence du serveur Minecraft est indisponible.",
            LatestPatchNoteVersion = notes.LatestPatchNoteVersion,
            LatestPatchNoteTitle = notes.LatestPatchNoteTitle,
            LatestPatchNoteSummary = notes.LatestPatchNoteSummary,
            LatestPatchNoteMetaText = notes.LatestPatchNoteMetaText,
            HasPatchNote = notes.HasPatchNote,
            IsStale = notes.IsStale,
            CanOpenLatestPatchNote = notes.CanOpenLatestPatchNote,
            PatchNotes = notes.PatchNotes
        });
    }
    private void ServiceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ServiceSelector.SelectedValue is not LauncherService service) return;
        if (!SelectService(service))
            ServiceSelector.SetCurrentValue(Selector.SelectedValueProperty, ShellState.SelectedService);
    }

    internal bool SelectService(LauncherService service)
    {
        if (!Enum.IsDefined(service) || IsAuthenticationRequired || !_overlayCoordinator.CanNavigate
            || !ShellState.IsNavigationEnabled) return false;
        if (ShellState.SelectedService == service) return true;

        LauncherService previousService = ShellState.SelectedService;
        bool sharedPage = IsSharedAtlasPage(CurrentPage);
        if (!sharedPage) _lastGamePages[previousService] = CurrentPage;
        ShellState.SelectService(service);
        LauncherShellPage next = sharedPage ? CurrentPage
            : _lastGamePages.GetValueOrDefault(service, LauncherShellPage.Game);
        _animateNextNavigation = true;
        NavigateTo(next);
        AutomationProperties.SetHelpText(ServiceSelector, ShellState.GameName);
        UIElementAutomationPeer.FromElement(ServiceSelector)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        return true;
    }

    private bool IsSharedAtlasPage(LauncherShellPage page) => page is LauncherShellPage.Settings
        or LauncherShellPage.PatchNotes or LauncherShellPage.Chat or LauncherShellPage.Account
        || (page == LauncherShellPage.Shop && _isAtlasWalletPage);

    private void ApplyServiceChrome()
    {
        bool wotlk = ShellState.IsWotlkSelected;
        WalletHeader.Visibility = Visibility.Visible;
        SettingsView.IsWotlkContext = wotlk;
        ProfileMenu.IsWotlkContext = wotlk;
        ApplyContextTabs(wotlk);
        ApplyChromePalette(!wotlk && CurrentPage == LauncherShellPage.Game);
    }

    private void ApplyChromePalette(bool minecraft)
    {
        if (_chromeIsMinecraft == minecraft) return;
        _minecraftChrome ??= new ResourceDictionary
        {
            Source = new Uri("/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.MinecraftChrome.xaml", UriKind.Relative)
        };
        foreach (string key in _minecraftChrome.Keys)
        {
            if (!_atlasChrome.ContainsKey(key)) _atlasChrome[key] = ((Brush)TitleBar.FindResource(key)).Clone();
            Brush current = (Brush)TitleBar.FindResource(key);
            Brush target = minecraft ? (Brush)_minecraftChrome[key] : _atlasChrome[key];
            Brush next = target.Clone();
            if (_chromeIsMinecraft is not null && AnimateServices)
            {
                if (current is SolidColorBrush from && next is SolidColorBrush to)
                    to.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from.Color, to.Color, SceneDuration));
                else if (current is GradientBrush oldGradient && next is GradientBrush newGradient)
                    for (int i = 0; i < Math.Min(oldGradient.GradientStops.Count, newGradient.GradientStops.Count); i++)
                        newGradient.GradientStops[i].BeginAnimation(GradientStop.ColorProperty,
                            new ColorAnimation(oldGradient.GradientStops[i].Color, newGradient.GradientStops[i].Color, SceneDuration));
            }
            TitleBar.Resources[key] = next;
        }
        _chromeIsMinecraft = minecraft;
    }

    private void ApplyContextTabs(bool wotlk)
    {
        if (_headerIsWotlk == wotlk) return;
        int generation = ++_headerMotionGeneration;
        bool animate = _headerIsWotlk is not null && AnimateServices;
        _headerIsWotlk = wotlk;
        foreach (Button tab in new[] { AddonsNavigationButton, ShopNavigationButton })
        {
            double from = tab.Visibility == Visibility.Visible ? tab.ActualWidth : 0;
            double opacity = tab.Visibility == Visibility.Visible ? tab.Opacity : 0;
            if (tab.IsKeyboardFocusWithin && IsActive) ServiceSelector.Focus();
            tab.IsHitTestVisible = tab.Focusable = wotlk && !_isServiceTransitioning;
            tab.BeginAnimation(WidthProperty, null);
            tab.BeginAnimation(OpacityProperty, null);
            tab.ClearValue(WidthProperty);
            if (!animate)
            {
                tab.Visibility = wotlk ? Visibility.Visible : Visibility.Collapsed;
                tab.Opacity = 1;
                continue;
            }
            tab.Visibility = Visibility.Visible;
            tab.Opacity = opacity;
            tab.Measure(new Size(double.PositiveInfinity, tab.Height));
            double width = wotlk ? tab.DesiredSize.Width : 0;
            tab.Width = width;
            var resize = new DoubleAnimation(from, width, SceneDuration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop };
            resize.Completed += (_, _) =>
            {
                if (generation != _headerMotionGeneration) return;
                tab.Visibility = wotlk ? Visibility.Visible : Visibility.Collapsed;
                tab.BeginAnimation(WidthProperty, null);
                tab.ClearValue(WidthProperty);
                tab.BeginAnimation(OpacityProperty, null);
                tab.Opacity = 1;
                UpdateHeaderNavigationSpacing();
            };
            tab.BeginAnimation(WidthProperty, resize);
            tab.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, wotlk ? 1 : 0,
                TimeSpan.FromMilliseconds(wotlk ? 140 : 65))
            { BeginTime = wotlk ? TimeSpan.FromMilliseconds(160) : TimeSpan.Zero });
        }
    }
}
