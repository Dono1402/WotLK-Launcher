using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class ActivityCenterPreviewTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizePreviewIsolationAndArguments();
        CharacterizePreviewData();
        await ValidateWpfLayoutsInteractionsAndCapturesAsync(captureDirectory);
        Console.WriteLine("Atlas activity center WPF preview OK (04B.1, isolated presentation only).");
        return 0;
    }

    private static void CharacterizePreviewIsolationAndArguments()
    {
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-activity=game-download"]),
            "preview-activity sans --ui-v2 doit être refusé avant composition.");
        Equal(
            LauncherStartupMode.UiV2ActivityPreview,
            App.ResolveStartupMode(["--ui-v2", "--preview-activity=game-download"]),
            "preview-activity doit posséder une branche isolée.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-activity=history", "--preview-friends=populated"]),
            "Activité et Amis ne doivent jamais composer deux previews.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-activity=history", "--preview-addons=default"]),
            "Activité et Addons ne doivent jamais composer deux previews.");
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode([]),
            "Le lancement sans argument doit ouvrir la V2 réelle.");
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode(["--ui-v2"]),
            "La V2 réelle doit rester distincte du preview Activité.");

        Dictionary<string, ActivityPreviewScenario> scenarios = new(StringComparer.OrdinalIgnoreCase)
        {
            ["idle"] = ActivityPreviewScenario.Idle,
            ["game-download"] = ActivityPreviewScenario.GameDownload,
            ["game-install"] = ActivityPreviewScenario.GameInstall,
            ["game-verify"] = ActivityPreviewScenario.GameVerify,
            ["game-repair"] = ActivityPreviewScenario.GameRepair,
            ["addon"] = ActivityPreviewScenario.Addon,
            ["addon-batch"] = ActivityPreviewScenario.AddonBatch,
            ["addon-remove"] = ActivityPreviewScenario.AddonRemove,
            ["self-update"] = ActivityPreviewScenario.SelfUpdate,
            ["error"] = ActivityPreviewScenario.Error,
            ["history"] = ActivityPreviewScenario.History,
            ["many-history"] = ActivityPreviewScenario.ManyHistory,
            ["quick-success"] = ActivityPreviewScenario.QuickSuccess,
            ["cancelling"] = ActivityPreviewScenario.Cancelling
        };
        foreach ((string argument, ActivityPreviewScenario expected) in scenarios)
        {
            Equal(
                expected,
                ActivityPreviewArguments.ResolveScenario(["--ui-v2", $"--preview-activity={argument}"]),
                $"Le scénario Activité {argument} est absent.");
        }
    }

    private static void CharacterizePreviewData()
    {
        ActivityUiState idle = ActivityPreviewData.Create(ActivityPreviewScenario.Idle);
        True(idle.Current.IsPreview && idle.Current.ShowsEmptyState,
            "L’état repos doit être explicitement vide et fictif.");

        ActivityViewState download = ActivityPreviewData.Create(ActivityPreviewScenario.GameDownload).Current;
        True(download.HasActiveOperation && download.TopBarShowsPercent,
            "Le téléchargement Jeu doit exposer une progression compacte.");
        Equal("68 %", download.TopBarPercentText, "Le pourcentage top bar est incorrect.");
        True(download.ActiveOperation!.CanUserCancel,
            "Le téléchargement Jeu doit présenter son annulation fictive.");

        ActivityViewState install = ActivityPreviewData.Create(ActivityPreviewScenario.GameInstall).Current;
        Equal("Installation", install.ActiveOperation!.ActionName,
            "L’installation client doit avoir un scénario distinct.");

        ActivityViewState verify = ActivityPreviewData.Create(ActivityPreviewScenario.GameVerify).Current;
        True(verify.TopBarIsIndeterminate && !verify.TopBarShowsPercent,
            "Verify ne doit jamais inventer de pourcentage.");
        True(!verify.ActiveOperation!.CanUserCancel
            && verify.ActiveOperation.DetailText.Contains("1 248", StringComparison.Ordinal),
            "Verify doit exposer le comptage réel et aucune annulation utilisateur.");

        ActivityViewState batch = ActivityPreviewData.Create(ActivityPreviewScenario.AddonBatch).Current;
        Equal(3, batch.PendingOperations.Length,
            "Le batch doit présenter uniquement les trois addons encore en attente.");
        Equal("1 sur 4", batch.ActiveOperation!.BatchPosition,
            "Le batch doit indiquer la position sans pourcentage global fictif.");

        ActivityViewState removal = ActivityPreviewData.Create(ActivityPreviewScenario.AddonRemove).Current;
        True(removal.ActiveOperation!.IsIndeterminate && !removal.ActiveOperation.CanUserCancel,
            "La suppression addon doit rester indéterminée et non annulable.");

        ActivityViewState history = ActivityPreviewData.Create(ActivityPreviewScenario.History).Current;
        True(!history.HasActiveOperation && history.RecentOperations.Length == 4,
            "L’historique standard doit présenter quatre résultats terminaux.");
        Equal(10, ActivityPreviewData.Create(ActivityPreviewScenario.ManyHistory).Current.RecentOperations.Length,
            "Le scénario long doit respecter la future limite de dix résultats.");
        True(ActivityPreviewData.Create(ActivityPreviewScenario.QuickSuccess).Current is
            { HasActiveOperation: false, RecentOperations.Length: 1 },
            "Un succès rapide doit aller au récent sans ouvrir une activité active.");

        ActivityUiState cancellable = ActivityPreviewData.Create(ActivityPreviewScenario.GameDownload);
        True(cancellable.RequestPreviewCancellation(),
            "Le clic fictif Annuler doit produire uniquement un état de présentation.");
        True(cancellable.Current.ActiveOperation is
            { IsCancellationRequested: true, CanUserCancel: false },
            "L’annulation doit désactiver immédiatement une seconde demande.");
        True(!cancellable.RequestPreviewCancellation(),
            "Une double annulation fictive doit être refusée immédiatement.");
    }

    private static async Task ValidateWpfLayoutsInteractionsAndCapturesAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasActivityCenterWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(75));
    }

    private static void RunWpfHarness(TaskCompletionSource completion, string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.UnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };

        _ = RunAsync();
        Dispatcher.Run();
        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }

        async Task RunAsync()
        {
            Application? application = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                await ValidateScenariosAndCapturesAsync(captureDirectory);
                await ValidateFocusClosureAndExclusivityAsync();
                await ValidateCancellationPresentationAsync();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateScenariosAndCapturesAsync(string? captureDirectory)
    {
        (ActivityPreviewScenario Scenario, double Width, double Height, string FileName)[] scenarios =
        [
            (ActivityPreviewScenario.GameDownload, 1440, 860, "01-activity-game-download-1440x860.png"),
            (ActivityPreviewScenario.AddonBatch, 1440, 860, "02-activity-addon-batch-1440x860.png"),
            (ActivityPreviewScenario.History, 1440, 860, "03-activity-history-1440x860.png"),
            (ActivityPreviewScenario.Error, 1440, 860, "04-activity-error-1440x860.png"),
            (ActivityPreviewScenario.Addon, 1080, 680, "05-activity-compact-1080x680.png"),
            (ActivityPreviewScenario.GameRepair, 1920, 1080, "06-activity-repair-1920x1080.png"),
            (ActivityPreviewScenario.Idle, 1440, 860, "07-activity-idle-1440x860.png"),
            (ActivityPreviewScenario.GameVerify, 1440, 860, "08-activity-verify-1440x860.png"),
            (ActivityPreviewScenario.SelfUpdate, 1440, 860, "09-activity-self-update-1440x860.png")
        ];
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        bool dpiReported = false;
        foreach ((ActivityPreviewScenario scenario, double width, double height, string fileName) in scenarios)
        {
            LauncherShellV2 window = CreateWindow(scenario, width, height, activate: false);
            window.Show();
            try
            {
                await DelayAndPumpAsync(240);
                DpiScale dpi = VisualTreeHelper.GetDpi(window);
                if (!dpiReported)
                {
                    dpiReported = true;
                    Console.WriteLine(
                        $"Activity WPF DPI observed: {dpi.PixelsPerInchX:0} x {dpi.PixelsPerInchY:0} "
                        + $"({dpi.DpiScaleX * 100:0}% x {dpi.DpiScaleY * 100:0}%).");
                    if (!string.IsNullOrWhiteSpace(captureDirectory))
                    {
                        True(Math.Abs(dpi.PixelsPerInchX - 120) <= 0.5,
                            "Les captures 04B.1 demandées doivent provenir d’une vraie session Windows à 125 %.");
                    }
                }

                ValidateCommonLayout(window, scenario);
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    SavePng(window, Path.Combine(captureDirectory, fileName));
                }
            }
            finally
            {
                window.Close();
                await PumpAsync(DispatcherPriority.Background);
            }
        }

        LauncherShellV2 longHistory = CreateWindow(ActivityPreviewScenario.ManyHistory, 1440, 860, activate: false);
        longHistory.Show();
        try
        {
            await DelayAndPumpAsync(240);
            Equal(10, longHistory.ActivityState.Current.RecentOperations.Length,
                "L’historique long doit contenir dix entrées.");
            True(longHistory.ActivityOverlay.ScrollHost.ScrollableHeight > 0,
                "Dix résultats doivent défiler dans la zone récente.");
            Rect activeBounds = BoundsInAncestor(Required<StackPanel>(longHistory.ActivityOverlay, "ActiveSection"), longHistory);
            Rect recentBounds = BoundsInAncestor(longHistory.ActivityOverlay.ScrollHost, longHistory);
            True(activeBounds.Bottom <= recentBounds.Top + 1,
                "L’opération active doit rester épinglée au-dessus de l’historique défilant.");
        }
        finally
        {
            longHistory.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static void ValidateCommonLayout(LauncherShellV2 window, ActivityPreviewScenario scenario)
    {
        ActivityCenterPanelV2 panel = window.ActivityOverlay;
        True(window.IsPreviewMode, "Le centre doit rester dans une fenêtre preview isolée.");
        True(!window.HasRealAuthenticationAttached && !window.HasRealAddonsAttached,
            "Le preview Activité ne doit attacher aucun service réel.");
        True(window.ActivityState.Current.IsPreview,
            "Toutes les données du centre doivent être explicitement fictives.");
        Equal(ShellOverlayKind.Activity, window.CurrentOverlay,
            "Le centre doit s’ouvrir directement dans son scénario dédié.");
        True(window.ActivityState.IsOpen && panel.Visibility == Visibility.Visible,
            "Le centre d’activité doit être visible.");
        True(panel.PanelHost.ActualWidth is >= 400 and <= 420,
            "Le panneau doit conserver une largeur de 400 à 420 DIPs.");
        Equal(ScrollBarVisibility.Disabled, panel.ScrollHost.HorizontalScrollBarVisibility,
            "Aucune barre horizontale n’est autorisée.");
        True(panel.ScrollHost.ScrollableWidth <= 0.5,
            "Le contenu récent ne doit pas déborder horizontalement.");
        True(!Descendants<Button>(Required<StackPanel>(window, "TopNavigation"))
                .Any(button => string.Equals(button.Content?.ToString(), "Actualités", StringComparison.Ordinal)),
            "L’onglet Actualités doit être retiré uniquement de la navigation V2.");
        True(Descendants<TextBlock>(Required<GameViewV2>(window, "GameView"))
                .Any(text => string.Equals(text.Text, "Lire la note de mise à jour", StringComparison.Ordinal)),
            "La carte de dernière note doit rester présente sur Jeu.");

        Button activityButton = Required<Button>(window, "ActivityButton");
        Equal("Activité", activityButton.ToolTip?.ToString(),
            "Le bouton top bar doit exposer son tooltip.");
        True(activityButton.IsVisible, "Le bouton Activité doit rester visible.");
        True(Required<Button>(window, "FriendsButton").IsVisible
            && Required<Button>(window, "SettingsButton").IsVisible
            && Required<Button>(window, "ProfileButton").IsVisible
            && Required<Button>(window, "MinimizeWindowButton").IsVisible
            && Required<Button>(window, "MaximizeWindowButton").IsVisible
            && Required<Button>(window, "CloseWindowButton").IsVisible,
            "Les actions globales et commandes de fenêtre doivent rester accessibles.");

        ActivityViewState state = window.ActivityState.Current;
        Equal(state.HasActiveOperation ? Visibility.Visible : Visibility.Collapsed,
            Required<StackPanel>(panel, "ActiveSection").Visibility,
            "La section En cours ne doit apparaître que lorsqu’elle contient une opération.");
        Equal(state.HasPendingOperations ? Visibility.Visible : Visibility.Collapsed,
            Required<StackPanel>(panel, "PendingSection").Visibility,
            "La section En attente ne doit apparaître que pour un batch.");
        Equal(state.HasRecentOperations ? Visibility.Visible : Visibility.Collapsed,
            Required<Grid>(panel, "RecentSection").Visibility,
            "La section Récent ne doit pas être affichée vide.");
        Equal(state.ShowsEmptyState ? Visibility.Visible : Visibility.Collapsed,
            Required<StackPanel>(panel, "EmptyState").Visibility,
            "L’état vide doit remplacer les sections absentes.");

        if (state.TopBarShowsPercent)
        {
            Equal(Visibility.Visible, Required<TextBlock>(window, "ActivityPercentText").Visibility,
                "Une progression déterminée doit apparaître de façon compacte dans la top bar.");
        }
        if (scenario == ActivityPreviewScenario.GameVerify)
        {
            True(Descendants<ProgressBar>(panel).Any(progress => progress.IsVisible && progress.IsIndeterminate),
                "Verify doit rendre une vraie progression indéterminée.");
            True(!Descendants<Button>(panel).Any(button =>
                    string.Equals(AutomationProperties.GetName(button), "Annuler l’opération", StringComparison.Ordinal)
                    && button.IsVisible),
                "Verify ne doit pas proposer d’annulation utilisateur.");
            True(Required<TextBlock>(window, "ActivityPercentText").Visibility == Visibility.Collapsed,
                "Verify ne doit afficher aucun faux pourcentage dans la top bar.");
        }
        if (scenario == ActivityPreviewScenario.AddonBatch)
        {
            True(Descendants<TextBlock>(panel).Any(text => text.Text == "1 sur 4"),
                "La position séquentielle du batch doit être visible.");
            True(state.PendingOperations.Select(item => item.ProductName)
                .SequenceEqual(["Deadly Boss Mods", "Details!", "Auctionator"]),
                "La file fictive doit conserver son ordre séquentiel.");
            Equal(Visibility.Collapsed, Required<TextBlock>(window, "ActivityPercentText").Visibility,
                "La top bar ne doit pas présenter la progression de Questie comme un pourcentage global du batch.");
        }
        if (scenario == ActivityPreviewScenario.Error)
        {
            True(Descendants<TextBlock>(panel).Any(text =>
                    text.Text == "Le téléchargement n’a pas pu être terminé."
                    && text.Foreground?.ToString() == "#FFEE6571"),
                "L’erreur contrôlée doit employer le ton sémantique rouge.");
            True(!Descendants<Button>(panel).Any(button =>
                    string.Equals(button.Content?.ToString(), "Réessayer", StringComparison.Ordinal)),
                "Aucun bouton Réessayer fictif ne doit être présenté.");
        }
        if (scenario == ActivityPreviewScenario.Idle)
        {
            True(Descendants<TextBlock>(panel).Any(text => text.Text == "Aucune activité récente"),
                "Le panneau vide doit expliquer clairement son état.");
            Equal(40d, activityButton.ActualWidth,
                "Le bouton au repos doit rester une simple icône.");
        }
        if (scenario == ActivityPreviewScenario.Addon)
        {
            True(Descendants<Image>(panel).Any(image => image.IsVisible && image.Source is not null),
                "L’activité addon doit utiliser son logo embarqué.");
        }

        if (window.Width <= 1080)
        {
            foreach (string controlName in new[]
            {
                "ActivityButton",
                "FriendsButton",
                "SettingsButton",
                "ProfileButton",
                "MinimizeWindowButton",
                "MaximizeWindowButton",
                "CloseWindowButton"
            })
            {
                Rect bounds = BoundsInAncestor(Required<FrameworkElement>(window, controlName), window);
                True(bounds.Left >= -0.5 && bounds.Right <= window.ActualWidth + 0.5,
                    $"{controlName} doit rester dans la top bar compacte.");
            }
        }
    }

    private static async Task ValidateFocusClosureAndExclusivityAsync()
    {
        LauncherShellV2 window = CreateWindow(ActivityPreviewScenario.GameDownload, 1440, 860, activate: true);
        window.Show();
        try
        {
            await DelayAndPumpAsync(240);
            ActivityCenterPanelV2 panel = window.ActivityOverlay;
            Button activityButton = Required<Button>(window, "ActivityButton");
            Button friendsButton = Required<Button>(window, "FriendsButton");
            Button profileButton = Required<Button>(window, "ProfileButton");

            panel.FocusFirstControl();
            await PumpAsync(DispatcherPriority.Input);
            True(panel.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject),
                "Le focus initial doit être placé dans le centre d’activité.");
            (Keyboard.FocusedElement as UIElement)?.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Previous));
            await PumpAsync(DispatcherPriority.Input);
            True(panel.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject),
                "Shift+Tab doit rester cyclique dans le panneau.");
            (Keyboard.FocusedElement as UIElement)?.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next));
            await PumpAsync(DispatcherPriority.Input);
            True(panel.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject),
                "Tab doit rester cyclique dans le panneau.");

            profileButton.Focus();
            await PumpAsync(DispatcherPriority.Input);
            True(panel.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject),
                "Le focus clavier ne doit pas passer derrière le panneau.");

            RaisePreviewKey(window, Key.Escape);
            await DelayAndPumpAsync(220);
            True(panel.IsFullyClosed && !window.ActivityState.IsOpen,
                "Échap doit complètement retirer le panneau et son voile.");
            Equal(activityButton, Keyboard.FocusedElement,
                "Le focus doit revenir au bouton Activité après fermeture.");

            RaiseClick(activityButton);
            await DelayAndPumpAsync(220);
            RaiseClick(Required<Button>(panel, "CloseButton"));
            await DelayAndPumpAsync(220);
            True(panel.IsFullyClosed,
                "Le bouton X doit fermer complètement le centre.");

            RaiseClick(activityButton);
            await DelayAndPumpAsync(220);
            RaiseClick(friendsButton);
            await DelayAndPumpAsync(220);
            Equal(ShellOverlayKind.Friends, window.CurrentOverlay,
                "Ouvrir Amis doit fermer Activité.");
            True(!window.ActivityState.IsOpen && window.FriendsState.IsOpen,
                "Activité et Amis ne doivent jamais être ouverts ensemble.");

            RaiseClick(activityButton);
            await DelayAndPumpAsync(220);
            Equal(ShellOverlayKind.Activity, window.CurrentOverlay,
                "Ouvrir Activité doit fermer Amis.");
            True(window.ActivityState.IsOpen && !window.FriendsState.IsOpen,
                "Amis et Activité ne doivent jamais être ouverts ensemble.");

            RaiseClick(profileButton);
            await DelayAndPumpAsync(220);
            Equal(ShellOverlayKind.Profile, window.CurrentOverlay,
                "Profil doit fermer Activité avant de s’ouvrir.");
            True(!window.ActivityState.IsOpen && window.ProfileState.IsOpen,
                "Profil et Activité ne doivent jamais être ouverts ensemble.");

            RaiseClick(activityButton);
            await DelayAndPumpAsync(220);
            Equal(ShellOverlayKind.Activity, window.CurrentOverlay,
                "Ouvrir Activité depuis Profil doit fermer le menu Profil.");
            Border scrim = Required<Border>(panel, "Scrim");
            RaiseMouseDown(scrim);
            await DelayAndPumpAsync(220);
            True(panel.IsFullyClosed,
                "Un clic extérieur doit fermer complètement le centre.");

            for (int index = 0; index < 6; index++)
            {
                RaiseClick(activityButton);
                await DelayAndPumpAsync(20);
            }
            await DelayAndPumpAsync(240);
            True(panel.IsFullyClosed && !window.ActivityState.IsOpen,
                "Des ouvertures et fermetures rapides ne doivent laisser aucun état intermédiaire.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateCancellationPresentationAsync()
    {
        LauncherShellV2 window = CreateWindow(ActivityPreviewScenario.GameDownload, 1440, 860, activate: false);
        window.Show();
        try
        {
            await DelayAndPumpAsync(240);
            Button cancel = Descendants<Button>(window.ActivityOverlay)
                .First(button =>
                    string.Equals(AutomationProperties.GetName(button), "Annuler l’opération", StringComparison.Ordinal)
                    && button.IsVisible);
            True(cancel.IsEnabled, "L’annulation fictive doit être disponible avant la demande.");
            RaiseClick(cancel);
            await PumpAsync(DispatcherPriority.DataBind);
            True(window.ActivityState.Current.ActiveOperation is
                { IsCancellationRequested: true, CanUserCancel: false },
                "La demande doit rester une mutation de présentation locale.");
            True(!cancel.IsEnabled && string.Equals(cancel.Content?.ToString(), "Annulation…", StringComparison.Ordinal),
                "Le bouton doit se désactiver immédiatement et afficher Annulation…");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }

        LauncherShellV2 removal = CreateWindow(ActivityPreviewScenario.AddonRemove, 1440, 860, activate: false);
        removal.Show();
        try
        {
            await DelayAndPumpAsync(240);
            True(!Descendants<Button>(removal.ActivityOverlay).Any(button =>
                    string.Equals(AutomationProperties.GetName(button), "Annuler l’opération", StringComparison.Ordinal)
                    && button.IsVisible),
                "La suppression addon ne doit afficher aucune annulation utilisateur.");
        }
        finally
        {
            removal.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static LauncherShellV2 CreateWindow(
        ActivityPreviewScenario scenario,
        double width,
        double height,
        bool activate) => new(GamePreviewScenario.Ready, scenario)
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = activate
        };

    private static void LoadV2Resources(Application application)
    {
        foreach (string path in new[]
        {
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(path, UriKind.Relative)
            });
        }
    }

    private static void SavePng(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Rect BoundsInAncestor(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement =>
        scope.FindName(name) as T
        ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void RaisePreviewKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("La source WPF du contrôle est absente.");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void RaiseMouseDown(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = target
        });
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }
}
