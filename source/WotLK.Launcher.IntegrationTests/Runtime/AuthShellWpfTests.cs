using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class AuthShellWpfTests
{
    private const double Width = 1597.6;
    private const double Height = 996.8;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    public static async Task<int> RunAsync(string? captures = null)
    {
        TaskCompletionSource<int> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            async Task ExecuteAsync()
            {
                Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string originalLocale = LauncherLocalization.CurrentLocale;
                try
                {
                    _checks = 0;
                    foreach (string resource in new[] { "UI/V2/Resources/AtlasV2.Tokens.xaml", "Assets/Icons/AtlasV2.Icons.xaml", "UI/V2/Resources/AtlasV2.Controls.xaml" })
                        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/WotLK.Launcher;component/" + resource, UriKind.Relative) });
                    List<object> cases = [];
                    foreach (string locale in new[] { LauncherLocalization.FrenchLocale, LauncherLocalization.EnglishLocale })
                        cases.Add(await ValidateAsync(locale, captures));
                    if (!string.IsNullOrWhiteSpace(captures))
                    {
                        string assembly = typeof(LauncherShellV2).Assembly.Location;
                        File.WriteAllText(Path.Combine(captures, "verification.json"), JsonSerializer.Serialize(new
                        {
                            status = "PASS", assertions = _checks, width = Width, height = Height,
                            method = "Unshown WPF shell with detached Measure/Arrange/RenderTargetBitmap content; injected managed focus events; no Show, HWND, native popup, mouse capture or OS input",
                            assembly, assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))), cases
                        }, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    Console.WriteLine($"Auth shell WPF PASS: {_checks} assertions; fixed {Width} x {Height}; FR/EN full viewport, required login/auth/logout/modal; no native window, popup, capture or OS focus.");
                    completed.SetResult(0);
                }
                catch (Exception error) { Console.Error.WriteLine(error); completed.SetResult(1); }
                finally { LauncherLocalization.SetLocale(originalLocale); application.Shutdown(); dispatcher.InvokeShutdown(); }
            }
        }) { IsBackground = true, Name = "AtlasAuthShellMemoryTests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await completed.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }

    private static async Task<object> ValidateAsync(string locale, string? captures)
    {
        LauncherLocalization.SetLocale(locale);
        ShellUiState state = LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: false);
        LauncherShellV2 shell = new(state, LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready), LauncherV2PreviewData.CreateFriends());
        Border content = (Border)shell.Content;
        IInputElement? originalFocus = Keyboard.FocusedElement;
        IInputElement? originalCapture = Mouse.Captured;
        MemoryPresentationSource memorySource = new();
        try
        {
            True(shell.Width == Width && shell.Height == Height, "Le shell conserve exactement la dimension fixe demandée.");
            True(new WindowInteropHelper(shell).Handle == IntPtr.Zero, "Construire le shell ne doit créer aucun HWND.");
            // The real shell handlers and bindings remain in use. Only its content is
            // detached for layout/rendering: no Window.Show, HwndSource or popup.
            ((LauncherLocalizationBridge)typeof(LauncherShellV2).GetField("_localizationBridge", PrivateInstance)!.GetValue(shell)!).Refresh();
            shell.Content = null;
            content.DataContext = shell;
            content.Resources.MergedDictionaries.Add(shell.Resources);
            content.Width = Width;
            content.Height = Height;
            content.UseLayoutRounding = true;
            content.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(content, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(content, TextRenderingMode.ClearType);
            memorySource.RootVisual = content;
            state.ApplySessionSnapshot(AuthSessionSnapshot.Initial);
            await LayoutAsync(content);
            True(!shell.AuthState.IsOpen && Named<Grid>(shell, "LauncherSurface").Visibility == Visibility.Hidden
                && !Named<Grid>(shell, "LauncherSurface").IsEnabled && Named<Grid>(shell, "LoginWindowChrome").Visibility == Visibility.Visible,
                "La restauration initiale de session garde la navigation inaccessible et les boutons système disponibles.");
            state.ApplySessionSnapshot(AuthSessionSnapshot.Initial with { Sequence = 1, State = LauncherSessionState.SignedOut, OperationKind = null });
            await LayoutAsync(content);
            shell.AuthenticationOverlay.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, shell.AuthenticationOverlay));
            await LayoutAsync(content);

            Grid root = Named<Grid>(shell, "ShellRoot");
            Grid surface = Named<Grid>(shell, "LauncherSurface");
            Border backdrop = Named<Border>(shell, "LoginBackdrop");
            Grid chrome = Named<Grid>(shell, "LoginWindowChrome");
            AuthOverlayViewV2 auth = shell.AuthenticationOverlay;
            Border card = (Border)auth.FindName("AuthCard");
            Border scrim = (Border)auth.FindName("Scrim");
            Button minimize = Named<Button>(shell, "LoginMinimizeWindowButton");
            Button close = Named<Button>(shell, "LoginCloseWindowButton");
            Button settings = Named<Button>(shell, "SettingsButton");
            ValidateRequired();
            Rect backdropBounds = Bounds(backdrop, content);
            Rect cardBounds = Bounds(card, content);
            string language = locale == LauncherLocalization.FrenchLocale ? "fr" : "en";
            True(Equals(((Button)auth.FindName("LoginModeButton")).Content, language == "en" ? "Sign in" : "Connexion"), "Les onglets du formulaire utilisent la langue active.");
            True(Texts(auth).Contains(language == "en" ? "Sign in to your Atlas account and continue to Arthas." : "Retrouve ton compte Atlas et continue vers Arthas."),
                "La description de connexion est traduite dans la langue active.");
            Capture(content, captures, $"login-{language}-1598.png");

            shell.AuthState.ShowRegisterCommand.Execute(null);
            await LayoutAsync(content);
            ValidateRequired();
            True(Texts(auth).Contains(language == "en" ? "Create account" : "Créer un compte"), "Le titre d’inscription est traduit.");
            True(Texts(auth).Contains(language == "en" ? "Create your Atlas account to join the Arthas realm." : "Crée ton compte Atlas pour rejoindre le royaume Arthas."),
                "La description d’inscription est traduite.");
            True(card.ActualHeight <= 620 && ((Grid)auth.FindName("RegisterForm")).Visibility == Visibility.Visible,
                "Le formulaire d’inscription reste dans la fenêtre fixe avec son contenu affiché.");
            Capture(content, captures, $"login-register-{language}-1598.png");
            shell.AuthState.ShowLoginCommand.Execute(null);
            await LayoutAsync(content);

            Invoke(shell, "CloseAuthenticationFromUser");
            True(auth.IsOpen && shell.AuthState.IsOpen, "La fermeture utilisateur ne contourne pas le login obligatoire.");
            KeyEventArgs escape = new(Keyboard.PrimaryDevice, memorySource, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            shell.RaiseEvent(escape);
            True(escape.Handled && auth.IsOpen, "La touche Échap est traitée sans fermer le login obligatoire.");
            foreach (LauncherShellPage page in Enum.GetValues<LauncherShellPage>().Where(page => page != LauncherShellPage.Game))
            {
                Invoke(shell, "NavigateTo", page);
                True(shell.CurrentPage == LauncherShellPage.Game, "Aucune route du launcher ne peut être sélectionnée avant connexion.");
            }
            True(FocusEvent(shell, settings).Handled, "Le garde-fou clavier refuse une cible dans la navigation masquée.");
            True(!FocusEvent(shell, (TextBox)auth.FindName("LoginUsernameBox")).Handled, "Le champ de connexion reste autorisé au clavier.");
            True(!FocusEvent(shell, minimize).Handled && !FocusEvent(shell, close).Handled, "Les deux boutons système restent autorisés au clavier avant connexion.");
            int trayRequests = 0;
            shell.MinimizeToTrayRequested += (_, _) => trayRequests++;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
            True(trayRequests == 1 && shell.AuthState.IsOpen, "Le bouton système conserve la réduction vers la zone de notification sans fermer le login.");

            state.ApplyAuthenticatedUser("TestAtlas");
            shell.AuthState.IsOpen = false;
            await RefreshAuthAsync();
            True(surface.Visibility == Visibility.Visible && surface.IsEnabled && surface.IsHitTestVisible,
                "La connexion rétablit visibilité et interaction de la surface du launcher.");
            True(backdrop.Visibility == Visibility.Collapsed && chrome.Visibility == Visibility.Collapsed && auth.IsFullyClosed,
                "La connexion retire complètement décor et boutons dédiés au login.");
            True(!FocusEvent(shell, settings).Handled, "Le garde-fou clavier libère la navigation après connexion.");
            Invoke(shell, "NavigateTo", LauncherShellPage.PatchNotes);
            True(shell.CurrentPage == LauncherShellPage.PatchNotes, "La navigation fonctionne de nouveau après connexion.");

            shell.AuthState.PrepareForOpen();
            ((ShellOverlayCoordinator)typeof(LauncherShellV2).GetField("_overlayCoordinator", PrivateInstance)!.GetValue(shell)!).OpenAuthentication();
            await RefreshAuthAsync();
            True(auth.CanClose && auth.Margin.Top == Named<RowDefinition>(shell, "ContentTopRow").Height.Value && auth.Margin.Top > 0,
                "Le formulaire facultatif garde sa marge sous la barre du launcher.");
            True(backdrop.Visibility == Visibility.Collapsed && chrome.Visibility == Visibility.Collapsed && ((Button)auth.FindName("CloseButton")).Visibility == Visibility.Visible,
                "Le modal facultatif expose sa fermeture et ne remet pas le décor de login.");
            True(scrim.Background is not SolidColorBrush { Color.A: 0 }, "Le modal facultatif conserve son voile de fond.");
            True(KeyboardNavigation.GetTabNavigation(card) == KeyboardNavigationMode.Cycle && FocusEvent(shell, settings).Handled,
                "Le modal facultatif contient le clavier dans son formulaire.");
            Capture(content, captures, $"login-modal-{language}-1598.png");
            ((Button)auth.FindName("CloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RefreshAuthAsync();
            True(auth.IsFullyClosed && !shell.AuthState.IsOpen && shell.CurrentPage == LauncherShellPage.PatchNotes,
                "Fermer le modal facultatif conserve la page affichée.");

            state.ApplySessionSnapshot(AuthSessionSnapshot.Initial with { Sequence = 2, State = LauncherSessionState.SignedOut, OperationKind = null });
            await RefreshAuthAsync();
            ValidateRequired();
            True(FocusEvent(shell, settings).Handled, "La déconnexion referme aussi la navigation clavier.");
            Capture(content, captures, $"login-logout-{language}-1598.png");
            True(ReferenceEquals(PresentationSource.FromVisual(content), memorySource) && new WindowInteropHelper(shell).Handle == IntPtr.Zero
                && !PresentationSource.CurrentSources.OfType<HwndSource>().Any(),
                "Tous les états ont été rendus sans HWND ni fenêtre native.");
            True(ReferenceEquals(originalFocus, Keyboard.FocusedElement) && ReferenceEquals(originalCapture, Mouse.Captured),
                "Les contrôles en mémoire ne déplacent ni le focus système ni la capture souris.");
            return new { locale, backdrop = backdropBounds.ToString(), form = cardBounds.ToString(), restoring = true, requiredLogin = true, requiredRegistration = true, authenticated = true, optionalModal = true, logout = true, nativeWindowHandle = 0,
                captures = new[] { $"login-{language}-1598.png", $"login-register-{language}-1598.png", $"login-modal-{language}-1598.png", $"login-logout-{language}-1598.png" } };

            async Task RefreshAuthAsync()
            {
                await LayoutAsync(content);
                // Same Loaded handler as the standalone in-memory Auth proof; this
                // settles the real state without starting native presentation.
                auth.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, auth));
                await LayoutAsync(content);
            }

            void ValidateRequired()
            {
                True(shell.AuthState.IsOpen && auth.IsOpen && !auth.CanClose && auth.Visibility == Visibility.Visible,
                    "Le login obligatoire est ouvert et ne peut pas être fermé.");
                True(surface.Visibility == Visibility.Hidden && !surface.IsEnabled && !surface.IsHitTestVisible,
                    "La surface complète, barre et navigation incluses, est masquée, désactivée et hors hit-test.");
                True(backdrop.Visibility == Visibility.Visible && chrome.Visibility == Visibility.Visible && auth.Margin == new Thickness(0),
                    "Le décor et le formulaire occupent la racine sans marge supérieure réservée à la navigation.");
                True(Bounds(backdrop, content) == Bounds(root, content) && Bounds(auth, content) == Bounds(root, content),
                    "Décor et couche login couvrent le même viewport complet que le shell.");
                True(backdrop.Background is ImageBrush { Stretch: Stretch.UniformToFill, ImageSource: BitmapSource },
                    "Un ImageBrush UniformToFill couvre toute la fenêtre sans bande vide.");
                True(VisibleBackgroundImages(root).Count == 1 && ReferenceEquals(VisibleBackgroundImages(root)[0], backdrop),
                    "Un seul décor de fond est réellement visible avant connexion.");
                True(scrim.Background is SolidColorBrush { Color.A: 0 }, "Le voile obligatoire est transparent, sans second fond.");
                True(((Button)auth.FindName("CloseButton")).Visibility == Visibility.Collapsed,
                    "La croix du modal est absente en login obligatoire.");
                True(card.ActualWidth == 500 && Inside(Bounds(card, content)), "Le formulaire conserve sa largeur et reste entièrement dans la fenêtre.");
                True(KeyboardNavigation.GetTabNavigation(card) == KeyboardNavigationMode.Continue
                    && KeyboardNavigation.GetTabNavigation(root) == KeyboardNavigationMode.Cycle,
                    "Le cycle Tab obligatoire inclut le formulaire et les boutons système.");
                foreach (Button button in new[] { minimize, close })
                {
                    True(button.IsVisible && button.IsEnabled && button.IsHitTestVisible && button.Focusable && Inside(Bounds(button, content)),
                        $"Les boutons système sont visibles, activés, accessibles et dans la fenêtre: {button.Name}; visible={button.IsVisible}, enabled={button.IsEnabled}, hit={button.IsHitTestVisible}, focusable={button.Focusable}, bounds={Bounds(button, content)}.");
                    Rect bounds = Bounds(button, content);
                    DependencyObject? hit = content.InputHitTest(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)) as DependencyObject;
                    True(IsWithin(hit, button), "Le hit-test réel atteint le bouton système au-dessus de la couche login.");
                }
                foreach (Point point in new[] { new Point(50, 50), new Point(400, 60), new Point(900, 60), new Point(Width - 3, Height - 3) })
                    True(!IsWithin(content.InputHitTest(point) as DependencyObject, surface), "Le hit-test ne traverse jamais le login vers les onglets ou la barre supérieure.");
            }
        }
        finally
        {
            memorySource.Dispose();
            shell.Close();
            True(new WindowInteropHelper(shell).Handle == IntPtr.Zero, "La destruction du shell ne crée pas de HWND.");
        }
    }

    // WPF visibility and InputHitTest require a connected PresentationSource.
    // This managed-only source supplies that connection with no composition
    // target, input provider, native surface or HWND.
    private sealed class MemoryPresentationSource : PresentationSource, IDisposable
    {
        private Visual? _root;
        private bool _disposed;
        internal MemoryPresentationSource() => AddSource();
        public override Visual RootVisual
        {
            get => _root!;
            set { Visual? old = _root; _root = value; RootChanged(old!, value); }
        }
        public override bool IsDisposed => _disposed;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
        public void Dispose() { RootVisual = null!; RemoveSource(); _disposed = true; }
    }

    private static KeyboardFocusChangedEventArgs FocusEvent(LauncherShellV2 shell, IInputElement target)
    {
        KeyboardFocusChangedEventArgs args = new(Keyboard.PrimaryDevice, Environment.TickCount, null, target)
        { RoutedEvent = Keyboard.PreviewGotKeyboardFocusEvent };
        Invoke(shell, "LauncherShellV2_PreviewGotKeyboardFocus", shell, args);
        return args;
    }

    private static List<FrameworkElement> VisibleBackgroundImages(DependencyObject root)
    {
        List<FrameworkElement> result = [];
        Walk(root);
        return result;
        void Walk(DependencyObject current)
        {
            if (current is UIElement { Visibility: not Visibility.Visible } or UIElement { Opacity: 0 }) return;
            if (current is Border { Background: ImageBrush } border) result.Add(border);
            if (current is Panel { Background: ImageBrush } panel) result.Add(panel);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++) Walk(VisualTreeHelper.GetChild(current, i));
        }
    }
    private static IEnumerable<string> Texts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text.Text;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (string value in Texts(VisualTreeHelper.GetChild(root, i))) yield return value;
    }
    private static async Task LayoutAsync(FrameworkElement content)
    {
        for (int i = 0; i < 3; i++)
        {
            content.Measure(new Size(Width, Height)); content.Arrange(new Rect(0, 0, Width, Height)); content.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
    private static void Capture(Visual visual, string? directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(Width), (int)Math.Ceiling(Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
    private static Rect Bounds(FrameworkElement element, Visual parent) => element.TransformToAncestor(parent).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static bool Inside(Rect bounds) => bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= Math.Ceiling(Width) && bounds.Bottom <= Math.Ceiling(Height);
    private static T Named<T>(LauncherShellV2 shell, string name) => (T)shell.FindName(name);
    private static void Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, args);
    private static bool IsWithin(DependencyObject? target, DependencyObject ancestor)
    {
        while (target is not null)
        {
            if (ReferenceEquals(target, ancestor)) return true;
            target = target is Visual ? VisualTreeHelper.GetParent(target) : LogicalTreeHelper.GetParent(target);
        }
        return false;
    }
    private static void True(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException(message); }
}
