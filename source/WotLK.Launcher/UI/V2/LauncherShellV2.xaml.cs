using System.ComponentModel;
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
    private readonly ProfilePreviewScenario? _initialProfilePreviewScenario;
    private readonly bool _openFriendsOnLoad;
    private readonly LauncherShellPage _initialPage;
    private IInputElement? _authFocusReturnTarget;
    private IInputElement? _avatarCropFocusReturnTarget;
    private AuthCommands? _authCommands;
    private AccountCommands? _accountCommands;
    private FriendsCommands? _friendsCommands;
    private AddonsCommands? _addonsCommands;

    public LauncherShellV2(GamePreviewScenario scenario = GamePreviewScenario.Ready)
        : this(
            LauncherV2PreviewData.CreateShell(scenario),
            LauncherV2PreviewData.CreateGame(scenario),
            AddonsPreviewData.CreateRuntimePlaceholder(),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(),
            LauncherV2PreviewData.CreateProfile(),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop(),
            authPreviewScenario: null,
            profilePreviewScenario: null,
            isPreviewMode: true,
            initialPage: LauncherShellPage.Game)
    {
    }

    public LauncherShellV2(GamePreviewScenario scenario, AddonsPreviewScenario addonsScenario)
        : this(
            LauncherV2PreviewData.CreateShell(scenario),
            LauncherV2PreviewData.CreateGame(scenario),
            AddonsPreviewData.Create(addonsScenario),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(),
            LauncherV2PreviewData.CreateProfile(),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop(),
            authPreviewScenario: null,
            profilePreviewScenario: null,
            isPreviewMode: true,
            initialPage: LauncherShellPage.Addons)
    {
    }

    public LauncherShellV2(GamePreviewScenario scenario, AuthPreviewScenario authScenario)
        : this(
            LauncherV2PreviewData.CreateShell(scenario, isAuthenticated: false),
            LauncherV2PreviewData.CreateGame(scenario),
            AddonsPreviewData.CreateRuntimePlaceholder(),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(authScenario),
            new ProfileUiState(),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop(),
            authScenario,
            profilePreviewScenario: null,
            isPreviewMode: true,
            initialPage: LauncherShellPage.Game)
    {
    }

    public LauncherShellV2(GamePreviewScenario scenario, ProfilePreviewScenario profileScenario)
        : this(
            LauncherV2PreviewData.CreateShell(scenario, isAuthenticated: true),
            LauncherV2PreviewData.CreateGame(scenario),
            AddonsPreviewData.CreateRuntimePlaceholder(),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(),
            LauncherV2PreviewData.CreateProfile(profileScenario),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop(),
            authPreviewScenario: null,
            profilePreviewScenario: profileScenario,
            isPreviewMode: true,
            initialPage: LauncherShellPage.Game)
    {
    }

    public LauncherShellV2(GamePreviewScenario scenario, SettingsPreviewScenario settingsScenario)
        : this(
            LauncherV2PreviewData.CreateShell(scenario),
            LauncherV2PreviewData.CreateGame(scenario),
            AddonsPreviewData.CreateRuntimePlaceholder(),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(),
            LauncherV2PreviewData.CreateProfile(),
            LauncherV2PreviewData.CreateSettings(settingsScenario),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop(),
            authPreviewScenario: null,
            profilePreviewScenario: null,
            isPreviewMode: true,
            initialPage: LauncherShellPage.Settings)
    {
    }

    public LauncherShellV2(GamePreviewScenario scenario, FriendsPreviewScenario friendsScenario)
        : this(
            LauncherV2PreviewData.CreateShell(scenario),
            LauncherV2PreviewData.CreateGame(scenario),
            AddonsPreviewData.CreateRuntimePlaceholder(),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(friendsScenario),
            LauncherV2PreviewData.CreateAuth(),
            LauncherV2PreviewData.CreateProfile(),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop(),
            authPreviewScenario: null,
            profilePreviewScenario: null,
            isPreviewMode: true,
            initialPage: LauncherShellPage.Game,
            openFriendsOnLoad: true)
    {
    }

    public LauncherShellV2(GamePreviewScenario scenario, AccountPreviewScenario accountScenario)
        : this(
            scenario,
            LauncherV2PreviewData.CreateAccountAvatarComposition(scenario, accountScenario))
    {
    }

    private LauncherShellV2(
        GamePreviewScenario scenario,
        AccountAvatarPreviewComposition composition)
        : this(
            composition.Shell,
            LauncherV2PreviewData.CreateGame(scenario),
            AddonsPreviewData.CreateRuntimePlaceholder(),
            LauncherV2PreviewData.CreateDashboard(scenario),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateAuth(),
            composition.Profile,
            LauncherV2PreviewData.CreateSettings(),
            composition.Account,
            composition.Crop,
            authPreviewScenario: null,
            profilePreviewScenario: null,
            isPreviewMode: true,
            initialPage: LauncherShellPage.Account)
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
            new ProfileUiState())
    {
    }

    internal LauncherShellV2(
        ShellUiState shellState,
        GameUiState gameState,
        DashboardUiState dashboardState,
        FriendsUiState friendsState,
        ProfileUiState profileState)
        : this(
            shellState,
            gameState,
            dashboardState,
            friendsState,
            profileState,
            SettingsUiState.Empty)
    {
    }

    internal LauncherShellV2(
        ShellUiState shellState,
        GameUiState gameState,
        DashboardUiState dashboardState,
        FriendsUiState friendsState,
        ProfileUiState profileState,
        SettingsUiState settingsState)
        : this(
            shellState,
            gameState,
            dashboardState,
            friendsState,
            profileState,
            settingsState,
            new AccountUiState(AccountUiState.Empty.Current),
            new AvatarCropUiState(AvatarCropUiState.Empty.Current))
    {
    }

    internal LauncherShellV2(
        ShellUiState shellState,
        GameUiState gameState,
        DashboardUiState dashboardState,
        FriendsUiState friendsState,
        ProfileUiState profileState,
        SettingsUiState settingsState,
        AccountUiState accountState,
        AvatarCropUiState avatarCropState)
        : this(
            shellState,
            gameState,
            AddonsPreviewData.CreateRuntimePlaceholder(),
            dashboardState,
            friendsState,
            new AuthUiState(),
            profileState,
            settingsState,
            accountState,
            avatarCropState,
            authPreviewScenario: null,
            profilePreviewScenario: null,
            isPreviewMode: false,
            initialPage: LauncherShellPage.Game)
    {
    }

    internal LauncherShellV2(
        ShellUiState shellState,
        GameUiState gameState,
        AddonsUiState addonsState,
        DashboardUiState dashboardState,
        FriendsUiState friendsState,
        ProfileUiState profileState,
        SettingsUiState settingsState,
        AccountUiState accountState,
        AvatarCropUiState avatarCropState)
        : this(
            shellState,
            gameState,
            addonsState,
            dashboardState,
            friendsState,
            new AuthUiState(),
            profileState,
            settingsState,
            accountState,
            avatarCropState,
            authPreviewScenario: null,
            profilePreviewScenario: null,
            isPreviewMode: false,
            initialPage: LauncherShellPage.Game)
    {
    }

    private LauncherShellV2(
        ShellUiState shellState,
        GameUiState gameState,
        AddonsUiState addonsState,
        DashboardUiState dashboardState,
        FriendsUiState friendsState,
        AuthUiState authState,
        ProfileUiState profileState,
        SettingsUiState settingsState,
        AccountUiState accountState,
        AvatarCropUiState avatarCropState,
        AuthPreviewScenario? authPreviewScenario,
        ProfilePreviewScenario? profilePreviewScenario,
        bool isPreviewMode,
        LauncherShellPage initialPage,
        bool openFriendsOnLoad = false)
    {
        ShellState = shellState ?? throw new ArgumentNullException(nameof(shellState));
        GameState = gameState ?? throw new ArgumentNullException(nameof(gameState));
        AddonsState = addonsState ?? throw new ArgumentNullException(nameof(addonsState));
        DashboardState = dashboardState ?? throw new ArgumentNullException(nameof(dashboardState));
        FriendsState = friendsState ?? throw new ArgumentNullException(nameof(friendsState));
        AuthState = authState ?? throw new ArgumentNullException(nameof(authState));
        ProfileState = profileState ?? throw new ArgumentNullException(nameof(profileState));
        SettingsState = settingsState ?? throw new ArgumentNullException(nameof(settingsState));
        AccountState = accountState ?? throw new ArgumentNullException(nameof(accountState));
        AvatarCropState = avatarCropState ?? throw new ArgumentNullException(nameof(avatarCropState));
        _overlayCoordinator = new ShellOverlayCoordinator(
            FriendsState,
            AuthState,
            ProfileState,
            AvatarCropState);
        _initialAuthPreviewScenario = authPreviewScenario;
        _initialProfilePreviewScenario = profilePreviewScenario;
        _initialPage = initialPage;
        _openFriendsOnLoad = openFriendsOnLoad;
        IsPreviewMode = isPreviewMode;

        InitializeComponent();
        Title = isPreviewMode ? "Atlas Launcher · Prévisualisation V2" : "Atlas Launcher";
        DataContext = this;

        SizeChanged += LauncherShellV2_SizeChanged;
        StateChanged += LauncherShellV2_StateChanged;
        PreviewKeyDown += LauncherShellV2_PreviewKeyDown;
        PreviewMouseDown += LauncherShellV2_PreviewMouseDown;
        PreviewGotKeyboardFocus += LauncherShellV2_PreviewGotKeyboardFocus;
        AccountState.PropertyChanged += AccountState_PropertyChanged;
        AddonsState.PropertyChanged += AddonsState_PropertyChanged;
        Loaded += LauncherShellV2_Loaded;
        Closed += LauncherShellV2_Closed;
    }

    public ShellUiState ShellState { get; }

    public GameUiState GameState { get; }

    public AddonsUiState AddonsState { get; }

    public DashboardUiState DashboardState { get; }

    public FriendsUiState FriendsState { get; }

    public AuthUiState AuthState { get; }

    public ProfileUiState ProfileState { get; }

    public SettingsUiState SettingsState { get; }

    public AccountUiState AccountState { get; }

    public AvatarCropUiState AvatarCropState { get; }

    public bool IsSettingsNavigationEnabled => IsPreviewMode || SettingsState.Current.IsRuntimeConnected;

    public bool IsAccountNavigationEnabled => IsPreviewMode
        ? AccountState.Current.IsPreview
        : AccountState.Current.IsRuntimeConnected;

    public bool IsAccountPreviewAvailable => IsPreviewMode && AccountState.Current.IsPreview;

    internal bool IsPreviewMode { get; }

    internal bool HasRealAuthenticationAttached => _authCommands is not null;

    internal bool HasRealAddonsAttached => _addonsCommands is not null;

    internal ShellOverlayKind CurrentOverlay => _overlayCoordinator.Current;

    internal LauncherShellPage CurrentPage { get; private set; } = LauncherShellPage.Game;

    internal AuthOverlayViewV2 AuthenticationOverlay => AuthOverlay;

    internal FriendsDrawerV2 FriendsOverlay => FriendsDrawer;

    internal ProfileMenuV2 ProfileOverlay => ProfileMenu;

    internal AddonsViewV2 AddonsPage => AddonsView;

    internal SettingsViewV2 SettingsPage => SettingsView;

    internal AccountViewV2 AccountPage => AccountView;

    internal AvatarCropOverlayV2 AvatarCropPreviewOverlay => AvatarCropOverlay;

    internal void ShowGamePageForSettingsOperation()
    {
        if (!IsPreviewMode && SettingsState.Current.IsRuntimeConnected)
        {
            NavigateTo(LauncherShellPage.Game);
        }
    }

    internal void AttachAuthentication(AuthCommands commands)
    {
        if (IsPreviewMode)
        {
            throw new InvalidOperationException("Le preview ne peut pas recevoir l’authentification réelle.");
        }

        _authCommands = commands ?? throw new ArgumentNullException(nameof(commands));
        AuthOverlay.SubmissionRequested += AuthOverlay_SubmissionRequested;
    }

    internal void AttachAccount(AccountCommands commands)
    {
        if (IsPreviewMode)
        {
            throw new InvalidOperationException("Le preview ne peut pas recevoir les commandes Compte réelles.");
        }

        _accountCommands = commands ?? throw new ArgumentNullException(nameof(commands));
        AccountView.AttachCommands(commands);
    }

    internal void AttachFriends(FriendsCommands commands)
    {
        if (IsPreviewMode)
        {
            throw new InvalidOperationException("Le preview ne peut pas recevoir les commandes Amis réelles.");
        }

        _friendsCommands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    internal void AttachAddons(AddonsCommands commands)
    {
        if (IsPreviewMode)
        {
            throw new InvalidOperationException("Le preview ne peut pas recevoir les commandes Addons réelles.");
        }

        _addonsCommands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    internal void OpenAuthenticationForPendingPlay()
    {
        if (IsPreviewMode)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(new Action(OpenAuthenticationForPendingPlay));
            return;
        }

        _authFocusReturnTarget = GameView.PrimaryActionFocusTarget;
        if (!AuthState.IsOpen)
        {
            AuthState.PrepareForOpen();
        }

        _overlayCoordinator.OpenAuthentication();
        FriendsButton.Focusable = false;
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

    internal void OpenProfileForPreview(ProfilePreviewScenario scenario)
    {
        if (!IsPreviewMode)
        {
            return;
        }

        _overlayCoordinator.OpenProfilePreview();
        ProfileButton.Focusable = false;
        ProfileMenu.FocusFirstControl();
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
        NavigateTo(_initialPage);
        if (_initialAuthPreviewScenario is AuthPreviewScenario scenario)
        {
            OpenAuthenticationForPreview(scenario);
        }
        else if (_initialProfilePreviewScenario is ProfilePreviewScenario profileScenario)
        {
            OpenProfileForPreview(profileScenario);
        }
        else if (_openFriendsOnLoad)
        {
            OpenFriendsDrawerForPreview();
        }

        if (AvatarCropState.IsOpen)
        {
            _avatarCropFocusReturnTarget = AccountView.AvatarActionFocusTarget;
            AvatarCropOverlay.FocusFirstControl();
        }
    }

    private void LauncherShellV2_Closed(object? sender, EventArgs e)
    {
        Loaded -= LauncherShellV2_Loaded;
        SizeChanged -= LauncherShellV2_SizeChanged;
        StateChanged -= LauncherShellV2_StateChanged;
        PreviewKeyDown -= LauncherShellV2_PreviewKeyDown;
        PreviewMouseDown -= LauncherShellV2_PreviewMouseDown;
        PreviewGotKeyboardFocus -= LauncherShellV2_PreviewGotKeyboardFocus;
        AccountState.PropertyChanged -= AccountState_PropertyChanged;
        AddonsState.PropertyChanged -= AddonsState_PropertyChanged;
        AuthOverlay.SubmissionRequested -= AuthOverlay_SubmissionRequested;
        ProfileMenu.ManageAccountRequested -= ProfileMenu_ManageAccountRequested;
        _authCommands = null;
        _accountCommands = null;
        _friendsCommands = null;
        _addonsCommands = null;
        AccountView.DetachFromShell();
        AuthOverlay.DetachFromShell();
        AuthOverlay.State = null;
        AuthOverlay.IsOpen = false;
        ProfileMenu.DetachFromShell();
        ProfileMenu.State = null;
        ProfileMenu.IsOpen = false;
        FriendsDrawer.State = null;
        FriendsDrawer.IsOpen = false;
        AvatarCropOverlay.DetachFromShell();
        AvatarCropOverlay.State = null;
        AvatarCropOverlay.IsOpen = false;
        AddonsView.State = null;
        SettingsView.State = null;
        AccountView.State = null;
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
            if (FriendsState.IsOpen)
            {
                _friendsCommands?.Refresh();
            }
        }
    }

    private void GameNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_overlayCoordinator.Current == ShellOverlayKind.None
            && (IsPreviewMode || SettingsState.Current.IsRuntimeConnected))
        {
            NavigateTo(LauncherShellPage.Game);
        }
    }

    private void AddonsNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellState.IsNavigationEnabled
            || _overlayCoordinator.Current != ShellOverlayKind.None)
        {
            return;
        }
        if (!IsPreviewMode && !ShellState.IsAuthenticated)
        {
            if (!ShellState.IsSessionRestoring && !AuthState.IsOpen)
            {
                _authFocusReturnTarget = AddonsNavigationButton;
                AuthState.PrepareForOpen();
                _overlayCoordinator.OpenAuthentication();
                FriendsButton.Focusable = false;
            }
            return;
        }

        NavigateTo(LauncherShellPage.Addons);
        _addonsCommands?.RefreshCatalog();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsSettingsNavigationEnabled && _overlayCoordinator.Current == ShellOverlayKind.None)
        {
            NavigateTo(LauncherShellPage.Settings);
        }
    }

    private void FriendsDrawer_CloseRequested(object? sender, EventArgs e)
    {
        _overlayCoordinator.CloseFriends();
    }

    private void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShellState.IsAuthenticated)
        {
            if (_overlayCoordinator.TryToggleProfile())
            {
                ProfileButton.Focusable = !ProfileState.IsOpen;
                if (ProfileState.IsOpen)
                {
                    ProfileMenu.FocusFirstControl();
                }
            }

            return;
        }

        if (IsPreviewMode)
        {
            OpenAuthenticationForPreview(AuthPreviewScenario.Login);
            return;
        }

        if (ShellState.IsSessionRestoring || AuthState.IsOpen || ProfileState.Current.IsLoggingOut)
        {
            return;
        }

        _authFocusReturnTarget = ProfileButton;
        AuthState.PrepareForOpen();
        _overlayCoordinator.OpenAuthentication();
        FriendsButton.Focusable = false;
    }

    private void ProfileMenu_CloseRequested(object? sender, EventArgs e)
    {
        _overlayCoordinator.CloseProfile();
    }

    private void ProfileMenu_ManageAccountRequested(object? sender, EventArgs e)
    {
        if (!IsAccountNavigationEnabled)
        {
            return;
        }

        _overlayCoordinator.CloseProfile();
        NavigateTo(LauncherShellPage.Account);
        _accountCommands?.RefreshProfile();
    }

    private async void AccountView_ModifyAvatarRequested(object? sender, EventArgs e)
    {
        if (IsPreviewMode)
        {
            if (!IsAccountPreviewAvailable || !_overlayCoordinator.TryOpenAvatarCrop())
            {
                return;
            }

            _avatarCropFocusReturnTarget = AccountView.AvatarActionFocusTarget;
            AvatarCropOverlay.FocusFirstControl();
            return;
        }

        if (_accountCommands is null || !await _accountCommands.SelectAvatarAsync())
        {
            return;
        }

        if (!_overlayCoordinator.TryOpenAvatarCrop())
        {
            _accountCommands.CancelUploadOrCloseCrop();
            return;
        }

        _avatarCropFocusReturnTarget = AccountView.AvatarActionFocusTarget;
        AvatarCropOverlay.FocusFirstControl();
    }

    private void AccountView_RemoveAvatarRequested(object? sender, EventArgs e)
    {
        if (!IsPreviewMode)
        {
            _accountCommands?.ShowDeleteConfirmation();
        }
    }

    private void AccountView_ConfirmAvatarDeleteRequested(object? sender, EventArgs e)
    {
        _accountCommands?.ConfirmDelete();
    }

    private void AvatarCropOverlay_CloseRequested(object? sender, EventArgs e)
    {
        if (IsPreviewMode)
        {
            _overlayCoordinator.CloseAvatarCrop();
            return;
        }

        _accountCommands?.CancelUploadOrCloseCrop();
    }

    private void AvatarCropOverlay_UploadRequested(object? sender, EventArgs e)
    {
        _accountCommands?.TryStartUpload();
    }

    private void AvatarCropOverlay_Closed(object? sender, EventArgs e)
    {
        if (AvatarCropState.IsOpen || _overlayCoordinator.Current != ShellOverlayKind.None)
        {
            _avatarCropFocusReturnTarget = null;
            return;
        }

        if (_avatarCropFocusReturnTarget is not null)
        {
            FocusManager.SetFocusedElement(this, _avatarCropFocusReturnTarget);
            Keyboard.Focus(_avatarCropFocusReturnTarget);
        }

        _avatarCropFocusReturnTarget = null;
    }

    private void AuthOverlay_CloseRequested(object? sender, EventArgs e)
    {
        CloseAuthenticationFromUser();
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
        if (_overlayCoordinator.Current != ShellOverlayKind.None)
        {
            _authFocusReturnTarget = null;
            return;
        }

        if (_authFocusReturnTarget is not null)
        {
            FocusManager.SetFocusedElement(this, _authFocusReturnTarget);
            Keyboard.Focus(_authFocusReturnTarget);
        }

        _authFocusReturnTarget = null;
    }

    private void FriendsDrawer_Closed(object? sender, EventArgs e)
    {
        FriendsButton.Focusable = true;
        if (FriendsState.IsOpen || _overlayCoordinator.Current != ShellOverlayKind.None)
        {
            return;
        }

        Keyboard.Focus(FriendsButton);
    }

    private void ProfileMenu_Closed(object? sender, EventArgs e)
    {
        ProfileButton.Focusable = true;
        if (ProfileState.IsOpen || _overlayCoordinator.Current != ShellOverlayKind.None)
        {
            return;
        }

        Keyboard.Focus(ProfileButton);
    }

    private void LauncherShellV2_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            && CurrentPage == LauncherShellPage.Addons
            && AddonsView.TryCloseTopLayer())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AccountView.TryCloseSensitiveEditor())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AccountView.TryCancelDeleteConfirmation())
        {
            _accountCommands?.CancelDeleteConfirmation();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AvatarCropState.IsOpen)
        {
            if (IsPreviewMode)
            {
                _overlayCoordinator.CloseAvatarCrop();
            }
            else
            {
                _accountCommands?.CancelUploadOrCloseCrop();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && AuthState.IsOpen)
        {
            CloseAuthenticationFromUser();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && FriendsState.IsOpen)
        {
            FriendsState.IsOpen = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ProfileState.IsOpen)
        {
            _overlayCoordinator.CloseProfile();
            e.Handled = true;
        }
    }

    private void LauncherShellV2_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ProfileState.IsOpen)
        {
            return;
        }

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (ProfileMenu.ContainsTarget(source)
            || FindAncestor<Button>(source) is Button button && ReferenceEquals(button, ProfileButton))
        {
            return;
        }

        _overlayCoordinator.CloseProfile();
    }

    private void LauncherShellV2_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (AccountView.IsSensitiveEditorOpen)
        {
            if (!AccountView.ContainsSensitiveEditorFocus(e.NewFocus as DependencyObject))
            {
                e.Handled = true;
                AccountView.FocusSensitiveEditor();
            }

            return;
        }

        if (AccountView.IsDeleteConfirmationOpen)
        {
            if (!AccountView.ContainsDeleteConfirmationFocus(e.NewFocus as DependencyObject))
            {
                e.Handled = true;
                AccountView.FocusDeleteConfirmation();
            }

            return;
        }

        if (AvatarCropState.IsOpen)
        {
            if (!AvatarCropOverlay.ContainsKeyboardFocusTarget(e.NewFocus as DependencyObject))
            {
                e.Handled = true;
                AvatarCropOverlay.FocusFirstControl();
            }

            return;
        }

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

    private void CloseAuthenticationFromUser()
    {
        _authCommands?.CancelCurrent();
        _overlayCoordinator.CloseAuthentication();
    }

    private void NavigateTo(LauncherShellPage page)
    {
        if (page == LauncherShellPage.Settings && !IsSettingsNavigationEnabled)
        {
            return;
        }

        if (page == LauncherShellPage.Account && !IsAccountNavigationEnabled)
        {
            return;
        }

        if (CurrentPage == LauncherShellPage.Account && page != LauncherShellPage.Account)
        {
            AccountView.OnNavigatedAway();
        }

        if (CurrentPage == LauncherShellPage.Addons && page != LauncherShellPage.Addons)
        {
            AddonsView.OnNavigatedAway();
        }

        CurrentPage = page;
        bool showGame = page == LauncherShellPage.Game;
        bool showAddons = page == LauncherShellPage.Addons;
        bool showSettings = page == LauncherShellPage.Settings;
        bool showAccount = page == LauncherShellPage.Account;
        GameView.Visibility = showGame ? Visibility.Visible : Visibility.Collapsed;
        AddonsView.Visibility = showAddons ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        AccountView.Visibility = showAccount ? Visibility.Visible : Visibility.Collapsed;
        GameNavigationButton.Tag = showGame ? "Active" : null;
        AddonsNavigationButton.Tag = showAddons ? "Active" : null;
        SettingsButton.Tag = showSettings ? "Active" : null;
        if (showAddons)
        {
            if (AddonsView.ListHost.Items.Count > 0)
            {
                AddonsView.ListHost.ScrollIntoView(AddonsView.ListHost.Items[0]);
            }
        }
        else if (showSettings)
        {
            SettingsView.ScrollHost.ScrollToTop();
        }
        else if (showAccount)
        {
            AccountView.ScrollHost.ScrollToTop();
        }
    }

    private void AccountState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsPreviewMode
            && CurrentPage == LauncherShellPage.Account
            && !IsAccountNavigationEnabled)
        {
            NavigateTo(LauncherShellPage.Game);
        }
    }

    private void AddonsState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsPreviewMode
            && CurrentPage == LauncherShellPage.Addons
            && !AddonsState.Current.IsRuntimeConnected)
        {
            NavigateTo(LauncherShellPage.Game);
        }
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
