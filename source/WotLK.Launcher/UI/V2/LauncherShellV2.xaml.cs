using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2 : Window
{
    private readonly ShellOverlayCoordinator _overlayCoordinator;
    private readonly AuthPreviewScenario? _initialAuthPreviewScenario;
    private IInputElement? _authFocusReturnTarget;
    private AuthCommands? _authCommands;

    public LauncherShellV2(GamePreviewScenario scenario = GamePreviewScenario.Ready)
        : this(
            LauncherV2PreviewData.CreateShell(scenario),
            LauncherV2PreviewData.CreateGame(scenario),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(),
            authPreviewScenario: null,
            isPreviewMode: true)
    {
    }

    public LauncherShellV2(GamePreviewScenario scenario, AuthPreviewScenario authScenario)
        : this(
            LauncherV2PreviewData.CreateShell(scenario, isAuthenticated: false),
            LauncherV2PreviewData.CreateGame(scenario),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(authScenario),
            authScenario,
            isPreviewMode: true)
    {
    }

    internal LauncherShellV2(
        ShellUiState shellState,
        GameUiState gameState,
        DashboardUiState dashboardState,
        FriendsUiState friendsState)
        : this(
            shellState,
            gameState,
            dashboardState,
            friendsState,
            new AuthUiState(),
            authPreviewScenario: null,
            isPreviewMode: false)
    {
    }

    private LauncherShellV2(
        ShellUiState shellState,
        GameUiState gameState,
        DashboardUiState dashboardState,
        FriendsUiState friendsState,
        AuthUiState authState,
        AuthPreviewScenario? authPreviewScenario,
        bool isPreviewMode)
    {
        ShellState = shellState ?? throw new ArgumentNullException(nameof(shellState));
        GameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        DashboardState = dashboardState ?? throw new ArgumentNullException(nameof(dashboardState));
        FriendsState = friendsState ?? throw new ArgumentNullException(nameof(friendsState));
        AuthState = authState ?? throw new ArgumentNullException(nameof(authState));
        _overlayCoordinator = new ShellOverlayCoordinator(FriendsState, AuthState);
        _initialAuthPreviewScenario = authPreviewScenario;
        IsPreviewMode = isPreviewMode;

        InitializeComponent();
        Title = isPreviewMode ? "Atlas Launcher · Prévisualisation V2" : "Atlas Launcher";
        DataContext = this;

        SizeChanged += LauncherShellV2_SizeChanged;
        StateChanged += LauncherShellV2_StateChanged;
        PreviewKeyDown += LauncherShellV2_PreviewKeyDown;
        PreviewGotKeyboardFocus += LauncherShellV2_PreviewGotKeyboardFocus;
        Loaded += LauncherShellV2_Loaded;
        Closed += LauncherShellV2_Closed;
    }

    public ShellUiState ShellState { get; }

    public GameUiState GameState { get; }

    public DashboardUiState DashboardState { get; }

    public FriendsUiState FriendsState { get; }

    public AuthUiState AuthState { get; }

    internal bool IsPreviewMode { get; }

    internal bool HasRealAuthenticationAttached => _authCommands is not null;

    internal ShellOverlayKind CurrentOverlay => _overlayCoordinator.Current;

    internal AuthOverlayViewV2 AuthenticationOverlay => AuthOverlay;

    internal FriendsDrawerV2 FriendsOverlay => FriendsDrawer;

    internal void AttachAuthentication(AuthCommands commands)
    {
        if (IsPreviewMode)
        {
            throw new InvalidOperationException("Le preview ne peut pas recevoir l’authentification réelle.");
        }

        _authCommands = commands ?? throw new ArgumentNullException(nameof(commands));
        AuthOverlay.SubmissionRequested += AuthOverlay_SubmissionRequested;
    }

    internal void OpenAuthenticationForPreview(AuthPreviewScenario scenario)
    {
        if (!IsPreviewMode)
        {
            return;
        }

        _authFocusReturnTarget = ProfileButton;
        AuthState.ApplyPreviewScenario(scenario);
        AuthOverlay.PreparePreviewScenario(scenario);
        _overlayCoordinator.OpenAuthentication();
        FriendsButton.Focusable = false;
    }

    internal void SetFriendsDrawerOpenForPreview()
    {
        if (!IsLoaded)
        {
            Loaded += OpenFriendsDrawerForPreviewOnLoaded;
            return;
        }

        OpenFriendsDrawerForPreview();
    }

    private void OpenFriendsDrawerForPreviewOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OpenFriendsDrawerForPreviewOnLoaded;
        OpenFriendsDrawerForPreview();
    }

    private void OpenFriendsDrawerForPreview()
    {
        if (_overlayCoordinator.TryToggleFriends() && FriendsState.IsOpen)
        {
            FriendsDrawer.IsOpen = true;
            FriendsButton.Focusable = false;
        }
    }

    private void LauncherShellV2_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAdaptiveLayout();
        if (_initialAuthPreviewScenario is AuthPreviewScenario scenario)
        {
            OpenAuthenticationForPreview(scenario);
        }
    }

    private void LauncherShellV2_Closed(object? sender, EventArgs e)
    {
        Loaded -= LauncherShellV2_Loaded;
        SizeChanged -= LauncherShellV2_SizeChanged;
        StateChanged -= LauncherShellV2_StateChanged;
        PreviewKeyDown -= LauncherShellV2_PreviewKeyDown;
        PreviewGotKeyboardFocus -= LauncherShellV2_PreviewGotKeyboardFocus;
        AuthOverlay.SubmissionRequested -= AuthOverlay_SubmissionRequested;
        _authCommands = null;
        AuthOverlay.DetachFromShell();
        AuthOverlay.State = null;
        AuthOverlay.IsOpen = false;
        DataContext = null;
        AuthState.Dispose();
    }

    private void ApplyAdaptiveLayout()
    {
        AdaptiveLayoutMode mode = AdaptiveLayoutClassifier.FromWidth(ActualWidth);
        ShellState.LayoutMode = mode;

        bool wide = mode == AdaptiveLayoutMode.Wide;
        bool stacked = mode == AdaptiveLayoutMode.Stacked;
        ProductGameName.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ProductDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        FriendsButtonText.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        VersionText.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        DashboardState.SetWideRealmLabel(wide);
        TopNavigation.Margin = mode == AdaptiveLayoutMode.Wide
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(0);
        RealmStatusChip.Padding = stacked
            ? new Thickness(9, 0, 9, 0)
            : new Thickness(11, 0, 11, 0);
    }

    private void LauncherShellV2_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyAdaptiveLayout();
    }

    private void LauncherShellV2_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button can be released before WPF enters its native move loop.
        }
    }

    private void FriendsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayCoordinator.TryToggleFriends())
        {
            FriendsButton.Focusable = !FriendsState.IsOpen;
        }
    }

    private void FriendsDrawer_CloseRequested(object? sender, EventArgs e)
    {
        _overlayCoordinator.CloseFriends();
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPreviewMode)
        {
            OpenAuthenticationForPreview(AuthPreviewScenario.Login);
            return;
        }

        if (ShellState.IsAuthenticated || ShellState.IsSessionRestoring || AuthState.IsOpen)
        {
            return;
        }

        _authFocusReturnTarget = ProfileButton;
        AuthState.PrepareForOpen();
        _overlayCoordinator.OpenAuthentication();
        FriendsButton.Focusable = false;
    }

    private void AuthOverlay_CloseRequested(object? sender, EventArgs e)
    {
        _authCommands?.CancelCurrent();
        _overlayCoordinator.CloseAuthentication();
    }

    private void AuthOverlay_SubmissionRequested(
        object? sender,
        AuthSubmissionRequestedEventArgs e)
    {
        e.StartStatus = _authCommands?.TrySubmit(e.Request)
            ?? LauncherSessionStartStatus.ShuttingDown;
    }

    private void AuthOverlay_Closed(object? sender, EventArgs e)
    {
        FriendsButton.Focusable = true;
        if (_authFocusReturnTarget is not null)
        {
            FocusManager.SetFocusedElement(this, _authFocusReturnTarget);
            Keyboard.Focus(_authFocusReturnTarget);
        }

        _authFocusReturnTarget = null;
    }

    private void FriendsDrawer_Closed(object? sender, EventArgs e)
    {
        if (FriendsState.IsOpen)
        {
            return;
        }

        FriendsButton.Focusable = true;
        Keyboard.Focus(FriendsButton);
    }

    private void LauncherShellV2_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && AuthState.IsOpen)
        {
            _overlayCoordinator.CloseAuthentication();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && FriendsState.IsOpen)
        {
            FriendsState.IsOpen = false;
            e.Handled = true;
        }
    }

    private void LauncherShellV2_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (AuthState.IsOpen)
        {
            if (!AuthOverlay.ContainsKeyboardFocusTarget(e.NewFocus as DependencyObject))
            {
                e.Handled = true;
                AuthOverlay.FocusFirstControl();
            }

            return;
        }

        if (!FriendsState.IsOpen || FriendsDrawer.ContainsKeyboardFocusTarget(e.NewFocus as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        FriendsDrawer.FocusFirstControl();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return null;
    }
}
