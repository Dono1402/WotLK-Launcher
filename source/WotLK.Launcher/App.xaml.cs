using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Validation;
using WotLK.Launcher.Updater;

namespace WotLK.Launcher;

internal enum LauncherStartupMode
{
    Legacy,
    UiV2,
    UiV2Preview,
    UiV2AuthPreview,
    UiV2ProfilePreview,
    UiV2SettingsPreview,
    UiV2FriendsPreview,
    UiV2AccountPreview,
    UiV2AddonsPreview,
    UiV2ActivityPreview,
    InvalidArguments,
    GrantGameDirectoryAccess,
    UninstallGame
}

public partial class App : Application
{
    private LauncherSingleInstanceGate? _singleInstanceGate;
    private LauncherTrayController? _trayController;
    private int _pendingActivationRequest;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (LauncherUpdateCommandLine.TryParseHelper(
                e.Args,
                out bool recovery,
                out string transactionPath,
                out int requesterProcessId))
        {
            base.OnStartup(e);
            Shutdown(LauncherUpdateHelperRunner.Run(
                recovery,
                transactionPath,
                requesterProcessId));
            return;
        }

        string[] applicationArguments = LauncherUpdateCommandLine.ApplicationArguments(e.Args);
        LauncherStartupMode startupMode = ResolveStartupMode(applicationArguments);
        if (UsesSingleInstance(startupMode))
        {
            string identity = LauncherSingleInstanceGate.CurrentIdentity;
            if (!LauncherSingleInstanceGate.TryAcquire(identity, out LauncherSingleInstanceGate? gate))
            {
                _ = LauncherSingleInstanceGate.SignalExisting(identity);
                base.OnStartup(e);
                Shutdown(0);
                return;
            }

            _singleInstanceGate = gate;
            gate!.ActivationRequested += SingleInstanceGate_ActivationRequested;
        }

        LauncherUpdateStartupSession? updateStartup = startupMode is
            LauncherStartupMode.Legacy or LauncherStartupMode.UiV2
                ? LauncherUpdateStartupSession.Begin(
                    e.Args,
                    recoverInterruptedTransactions: true)
                : null;

        base.OnStartup(e);

        if (startupMode == LauncherStartupMode.InvalidArguments)
        {
            MessageBox.Show(
                "Les modes --legacy et --ui-v2 ne peuvent pas être combinés. "
                + "Les prévisualisations nécessitent --ui-v2 et ne peuvent pas être combinées.",
                "Prévisualisation Atlas Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        if (UsesV2Window(startupMode))
        {
            try
            {
                ManropeFontValidator.ValidateOrThrow();
                LoadV2Resources();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Atlas Launcher ne peut pas afficher l'interface V2.\n\n{ex.Message}",
                    "Erreur de validation Manrope",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        if (startupMode == LauncherStartupMode.GrantGameDirectoryAccess)
        {
            Shutdown(GameDirectoryAccess.RunGrantAccess(e.Args));
            return;
        }

        if (startupMode == LauncherStartupMode.UninstallGame)
        {
            Shutdown(GameInstallServices.RunGameUninstall(e.Args));
            return;
        }

        DispatchInteractiveStartup(
            startupMode,
            () => StartLegacy(updateStartup),
            () => StartRuntimeV2(updateStartup),
            previewMode => StartV2Preview(previewMode, e.Args));
    }

    internal static LauncherStartupMode ResolveStartupMode(IEnumerable<string> arguments)
    {
        string[] args = arguments as string[] ?? arguments.ToArray();
        bool useLegacy = args.Any(argument =>
            string.Equals(argument, "--legacy", StringComparison.OrdinalIgnoreCase));
        bool useUiV2 = args.Any(argument =>
            string.Equals(argument, "--ui-v2", StringComparison.OrdinalIgnoreCase));
        bool useGamePreview = args.Any(argument =>
            argument.StartsWith("--preview-state=", StringComparison.OrdinalIgnoreCase));
        bool useAuthPreview = AuthPreviewArguments.IsRequested(args);
        bool useProfilePreview = ProfilePreviewArguments.IsRequested(args);
        bool useSettingsPreview = SettingsPreviewArguments.IsRequested(args);
        bool useFriendsPreview = FriendsPreviewArguments.IsRequested(args);
        bool useAccountPreview = AccountPreviewArguments.IsRequested(args);
        bool useAddonsPreview = AddonsPreviewArguments.IsRequested(args);
        bool useActivityPreview = ActivityPreviewArguments.IsRequested(args);
        int dedicatedPreviewCount = (useAuthPreview ? 1 : 0)
            + (useProfilePreview ? 1 : 0)
            + (useSettingsPreview ? 1 : 0)
            + (useFriendsPreview ? 1 : 0)
            + (useAccountPreview ? 1 : 0)
            + (useAddonsPreview ? 1 : 0)
            + (useActivityPreview ? 1 : 0);
        if ((useLegacy && useUiV2)
            || ((useGamePreview || dedicatedPreviewCount > 0) && !useUiV2)
            || dedicatedPreviewCount > 1)
        {
            return LauncherStartupMode.InvalidArguments;
        }

        if (useUiV2)
        {
            if (useAuthPreview)
            {
                return LauncherStartupMode.UiV2AuthPreview;
            }

            if (useProfilePreview)
            {
                return LauncherStartupMode.UiV2ProfilePreview;
            }

            if (useSettingsPreview)
            {
                return LauncherStartupMode.UiV2SettingsPreview;
            }

            if (useFriendsPreview)
            {
                return LauncherStartupMode.UiV2FriendsPreview;
            }

            if (useAccountPreview)
            {
                return LauncherStartupMode.UiV2AccountPreview;
            }

            if (useAddonsPreview)
            {
                return LauncherStartupMode.UiV2AddonsPreview;
            }

            if (useActivityPreview)
            {
                return LauncherStartupMode.UiV2ActivityPreview;
            }

            return useGamePreview ? LauncherStartupMode.UiV2Preview : LauncherStartupMode.UiV2;
        }

        if (GameDirectoryAccess.IsGrantAccessMode(args))
        {
            return LauncherStartupMode.GrantGameDirectoryAccess;
        }

        if (GameInstallServices.IsGameUninstallMode(args))
        {
            return LauncherStartupMode.UninstallGame;
        }

        return useLegacy
            ? LauncherStartupMode.Legacy
            : LauncherStartupMode.UiV2;
    }

    internal static void DispatchInteractiveStartup(
        LauncherStartupMode startupMode,
        Action startLegacy,
        Action startRuntimeV2,
        Action<LauncherStartupMode> startV2Preview)
    {
        ArgumentNullException.ThrowIfNull(startLegacy);
        ArgumentNullException.ThrowIfNull(startRuntimeV2);
        ArgumentNullException.ThrowIfNull(startV2Preview);

        switch (startupMode)
        {
            case LauncherStartupMode.Legacy:
                startLegacy();
                return;
            case LauncherStartupMode.UiV2:
                startRuntimeV2();
                return;
            case LauncherStartupMode.UiV2Preview:
            case LauncherStartupMode.UiV2AuthPreview:
            case LauncherStartupMode.UiV2ProfilePreview:
            case LauncherStartupMode.UiV2SettingsPreview:
            case LauncherStartupMode.UiV2FriendsPreview:
            case LauncherStartupMode.UiV2AccountPreview:
            case LauncherStartupMode.UiV2AddonsPreview:
            case LauncherStartupMode.UiV2ActivityPreview:
                startV2Preview(startupMode);
                return;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(startupMode),
                    startupMode,
                    "Le mode ne crée pas de fenêtre interactive.");
        }
    }

    internal static bool UsesV2Window(LauncherStartupMode startupMode) => startupMode is
        LauncherStartupMode.UiV2
        or LauncherStartupMode.UiV2Preview
        or LauncherStartupMode.UiV2AuthPreview
        or LauncherStartupMode.UiV2ProfilePreview
        or LauncherStartupMode.UiV2SettingsPreview
        or LauncherStartupMode.UiV2FriendsPreview
        or LauncherStartupMode.UiV2AccountPreview
        or LauncherStartupMode.UiV2AddonsPreview
        or LauncherStartupMode.UiV2ActivityPreview;

    internal static bool UsesSingleInstance(LauncherStartupMode startupMode) => startupMode is
        LauncherStartupMode.Legacy or LauncherStartupMode.UiV2;

    private void StartLegacy(LauncherUpdateStartupSession? updateStartup)
    {
        var window = new MainWindow(
            LegacyMainWindowDependencies.CreateProduction(
                updateStartup?.RecoveryOccurred == true));
        MainWindow = window;
        window.Show();
        ActivatePendingPrimaryWindow();
        ScheduleUpdateReadyConfirmation(window, updateStartup);
    }

    private void StartV2Preview(
        LauncherStartupMode startupMode,
        IEnumerable<string> arguments)
    {
        string[] args = arguments as string[] ?? arguments.ToArray();
        string previewLocale = args.Any(argument => string.Equals(
            argument,
            "--ui-v2-language=en",
            StringComparison.OrdinalIgnoreCase))
            ? LauncherLocalization.EnglishLocale
            : LauncherLocalization.FrenchLocale;
        LauncherLocalization.SetLocale(previewLocale);
        GamePreviewScenario previewScenario = LauncherV2PreviewData.ResolveScenario(args);
        LauncherShellV2 previewWindow = startupMode switch
        {
            LauncherStartupMode.UiV2AuthPreview => new LauncherShellV2(
                previewScenario,
                AuthPreviewArguments.ResolveScenario(args)),
            LauncherStartupMode.UiV2ProfilePreview => new LauncherShellV2(
                previewScenario,
                ProfilePreviewArguments.ResolveScenario(args)),
            LauncherStartupMode.UiV2SettingsPreview => new LauncherShellV2(
                previewScenario,
                SettingsPreviewArguments.ResolveScenario(args)),
            LauncherStartupMode.UiV2FriendsPreview => new LauncherShellV2(
                previewScenario,
                FriendsPreviewArguments.ResolveScenario(args)),
            LauncherStartupMode.UiV2AccountPreview => new LauncherShellV2(
                previewScenario,
                AccountPreviewArguments.ResolveScenario(args)),
            LauncherStartupMode.UiV2AddonsPreview => new LauncherShellV2(
                previewScenario,
                AddonsPreviewArguments.ResolveScenario(args)),
            LauncherStartupMode.UiV2ActivityPreview => new LauncherShellV2(
                previewScenario,
                ActivityPreviewArguments.ResolveScenario(args)),
            LauncherStartupMode.UiV2Preview => new LauncherShellV2(previewScenario),
            _ => throw new ArgumentOutOfRangeException(
                nameof(startupMode),
                startupMode,
                "Le mode demandé n'est pas une prévisualisation V2.")
        };
        ApplyV2PreviewOptions(previewWindow, args);
        MainWindow = previewWindow;
        previewWindow.Show();
    }

    private void StartRuntimeV2(LauncherUpdateStartupSession? updateStartup)
    {
        LauncherRuntime runtime;
        try
        {
            runtime = LauncherRuntime.CreateProduction(
                updateStartup?.RecoveryOccurred == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Atlas Launcher ne peut pas lire l'installation locale.\n\n{ex.Message}",
                "Erreur de démarrage V2",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        LauncherLocalization.SetLocale(runtime.Settings.InterfaceLocale);

        ShellUiState shellState = LauncherV2RuntimePresentation.CreateShell(runtime);
        GameUiState gameState = LauncherV2RuntimePresentation.CreateGame(runtime.LocalClient);
        AddonsUiState addonsState = new(AddonsStateAdapter.Project(
            runtime.Addons.CurrentSnapshot));
        DashboardUiState dashboardState = LauncherV2RuntimePresentation.CreateDashboard();
        ProfileUiState profileState = new();
        SettingsUiState settingsState = new(SettingsStateAdapter.CreateInitialView(
            runtime.SettingsRuntime.CurrentSnapshot,
            runtime.Game.CurrentSnapshot,
            runtime.Dashboard.CurrentSnapshot,
            runtime.LauncherVersion,
            LauncherSettings.LauncherLogPath,
            selfUpdate: runtime.SelfUpdate.CurrentSnapshot));
        AccountUiState accountState = new(AccountStateAdapter.Project(
            runtime.Account.CurrentSnapshot,
            avatarImage: null));
        FriendsUiState friendsState = new(FriendsStateAdapter.Project(
            runtime.Friends.CurrentSnapshot));
        ActivityUiState activityState = new(ActivityStateAdapter.Project(
            runtime.Activity.CurrentSnapshot));
        AvatarCropUiState avatarCropState = new(AvatarCropUiState.Empty.Current);
        GameCommands gameCommands = LauncherV2RuntimePresentation.ConnectLocalActions(
            gameState,
            runtime.LocalActions);
        LauncherShellV2 window = new(
            shellState,
            gameState,
            addonsState,
            dashboardState,
            friendsState,
            profileState,
            settingsState,
            accountState,
            avatarCropState,
            activityState);
        LauncherTrayController? trayController = null;
        EventHandler? minimizeToTrayHandler = null;
        try
        {
            LauncherTrayController createdTray = new(
                window,
                new WindowsLauncherTrayIconHost(),
                window.Close);
            trayController = createdTray;
            _trayController = createdTray;
            minimizeToTrayHandler = (_, _) =>
            {
                if (runtime.Settings.MinimizeToTrayOnClose)
                {
                    createdTray.HideInTray();
                }
                else
                {
                    window.Close();
                }
            };
            window.MinimizeToTrayRequested += minimizeToTrayHandler;
        }
        catch (Exception exception)
        {
            runtime.WriteRuntimeDiagnostic(
                $"Zone de notification indisponible: category={exception.GetType().Name}.");
        }
        AddonsCommands addonsCommands = new(
            runtime.Addons,
            addonsState,
            window,
            () => runtime.Settings.InstallPath);
        window.AttachAddons(addonsCommands);
        AddonsStateAdapter addonsStateAdapter = new(
            addonsState,
            runtime.Addons,
            window.Dispatcher);
        ActivityStateAdapter activityStateAdapter = new(
            activityState,
            runtime.Activity,
            window.Dispatcher);
        ActivityCancelCommand activityCancelCommand = new(
            runtime.Operations,
            activityState);
        window.AttachActivity(activityCancelCommand);
        GameVerificationCommand verificationCommand = new(runtime.Game);
        gameState.AttachVerifyCommand(verificationCommand.Command);
        SettingsCommands settingsCommands = new(
            settingsState,
            runtime.SettingsRuntime,
            runtime.LocalActions,
            window,
            runtime.WriteRuntimeDiagnostic,
            verifyRepairCommand: verificationCommand.Command,
            showGameForRepair: window.ShowGamePageForSettingsOperation,
            selfUpdate: runtime.SelfUpdateEnabled ? runtime.SelfUpdate : null);
        SettingsStateAdapter settingsStateAdapter = new(
            settingsState,
            runtime.SettingsRuntime,
            runtime.Game,
            runtime.Dashboard,
            runtime.LauncherVersion,
            LauncherSettings.LauncherLogPath,
            window.Dispatcher,
            runtime.SelfUpdate);
        AuthCommands authCommands = new(runtime);
        window.AttachAuthentication(authCommands);
        FriendsCommands friendsCommands = new(
            runtime.Friends,
            friendsState,
            window.Dispatcher);
        window.AttachFriends(friendsCommands);
        FriendsStateAdapter friendsStateAdapter = new(
            friendsState,
            runtime.Friends,
            window.Dispatcher,
            runtime.AvatarImages);
        LauncherFriendsNotificationCoordinator? friendsNotificationCoordinator =
            trayController is null
                ? null
                : new LauncherFriendsNotificationCoordinator(
                    runtime.Friends,
                    runtime.SettingsRuntime,
                    trayController,
                    runtime.WriteRuntimeDiagnostic);
        AccountStateAdapter accountStateAdapter = new(
            accountState,
            avatarCropState,
            shellState,
            profileState,
            runtime.Account,
            runtime.AvatarImages,
            window.Dispatcher);
        AccountCommands accountCommands = new(
            runtime.Account,
            accountState,
            avatarCropState,
            new AvatarFileSelectionService(new WindowsAvatarFilePicker()),
            window.Dispatcher);
        window.AttachAccount(accountCommands);
        AuthStateAdapter authStateAdapter = new(
            window.AuthState,
            shellState,
            gameState,
            runtime.Session,
            window.Dispatcher);
        LogoutCommand logoutCommand = new(runtime.Profile);
        profileState.AttachLogoutCommand(logoutCommand.Command);
        ProfileStateAdapter profileStateAdapter = new(
            profileState,
            gameState,
            runtime.Profile,
            window.Dispatcher);
        PrimaryActionCommand primaryActionCommand = new(
            runtime.Game,
            window.OpenAuthenticationForPendingPlay);
        gameState.AttachPrimaryActionCommand(primaryActionCommand.Command);
        GameStateAdapter gameStateAdapter = new(
            gameState,
            runtime.Game,
            window.Dispatcher);
        DashboardStateAdapter dashboardStateAdapter = new(
            dashboardState,
            runtime.Dashboard,
            window.Dispatcher);
        bool allowClose = false;
        bool shutdownStarted = false;
        int presentationDisposed = 0;
        EventHandler playStartedHandler = (_, _) =>
        {
            if (!runtime.Settings.CloseLauncherOnGameStart
                || runtime.IsDisposed
                || shutdownStarted)
            {
                return;
            }

            _ = window.Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(() =>
                {
                    if (!shutdownStarted && window.IsVisible)
                    {
                        if (trayController is not null)
                        {
                            trayController.HideInTray();
                        }
                        else
                        {
                            window.WindowState = WindowState.Minimized;
                        }
                    }
                }));
        };
        runtime.Game.PlayStarted += playStartedHandler;

        void DisposePresentation()
        {
            if (Interlocked.Exchange(ref presentationDisposed, 1) != 0)
            {
                return;
            }

            gameStateAdapter.Dispose();
            dashboardStateAdapter.Dispose();
            authStateAdapter.Dispose();
            profileStateAdapter.Dispose();
            friendsStateAdapter.Dispose();
            friendsNotificationCoordinator?.Dispose();
            addonsStateAdapter.Dispose();
            activityStateAdapter.Dispose();
            accountStateAdapter.Dispose();
            settingsStateAdapter.Dispose();
            settingsCommands.Dispose();
            authCommands.Dispose();
            friendsCommands.Dispose();
            addonsCommands.Dispose();
            activityCancelCommand.Dispose();
            accountCommands.Dispose();
            logoutCommand.Dispose();
            primaryActionCommand.Dispose();
            verificationCommand.Dispose();
            gameCommands.Dispose();
            runtime.Game.PlayStarted -= playStartedHandler;
            gameState.ClearNotification();
        }

        RoutedEventHandler? loadedHandler = null;
        loadedHandler = async (_, _) =>
        {
            window.Loaded -= loadedHandler;
            LauncherSessionRestoreResult result = await runtime.InitializeAsync();
            if (!runtime.IsDisposed
                && !runtime.Operations.IsShuttingDown
                && window.IsVisible)
            {
                if (result.Status == LauncherSessionRestoreStatus.Restored)
                {
                    _ = runtime.Game.TryStartVerification();
                }
            }
        };
        window.Loaded += loadedHandler;
        CancelEventHandler? closingHandler = null;
        closingHandler = async (_, args) =>
        {
            if (allowClose)
            {
                return;
            }

            args.Cancel = true;
            if (shutdownStarted)
            {
                return;
            }

            shutdownStarted = true;
            runtime.BeginShutdown();
            await runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(18));
            DisposePresentation();
            allowClose = true;
            await window.Dispatcher.InvokeAsync(window.Close, DispatcherPriority.Send);
        };
        window.Closing += closingHandler;
        window.Closed += (_, _) =>
        {
            window.Loaded -= loadedHandler;
            window.Closing -= closingHandler;
            if (minimizeToTrayHandler is not null)
            {
                window.MinimizeToTrayRequested -= minimizeToTrayHandler;
            }
            trayController?.Dispose();
            if (ReferenceEquals(_trayController, trayController))
            {
                _trayController = null;
            }
            DisposePresentation();
            runtime.Dispose();
        };

        MainWindow = window;
        window.Show();
        ActivatePendingPrimaryWindow();
        ScheduleUpdateReadyConfirmation(window, updateStartup);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayController?.Dispose();
        _trayController = null;
        if (_singleInstanceGate is not null)
        {
            _singleInstanceGate.ActivationRequested -= SingleInstanceGate_ActivationRequested;
            _singleInstanceGate.Dispose();
            _singleInstanceGate = null;
        }

        base.OnExit(e);
    }

    private void SingleInstanceGate_ActivationRequested(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _pendingActivationRequest, 1);
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Send,
                new Action(ActivatePendingPrimaryWindow));
        }
    }

    private void ActivatePendingPrimaryWindow()
    {
        Window? window = MainWindow;
        if (window is null || !window.IsLoaded)
        {
            return;
        }

        if (Interlocked.Exchange(ref _pendingActivationRequest, 0) == 0)
        {
            return;
        }

        if (_trayController is not null)
        {
            _trayController.RestoreWindow();
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        _ = window.Activate();
        window.Focus();
    }

    private static void ScheduleUpdateReadyConfirmation(
        Window window,
        LauncherUpdateStartupSession? updateStartup)
    {
        if (updateStartup is null)
        {
            return;
        }

        _ = ConfirmAsync();
        async Task ConfirmAsync()
        {
            try
            {
                await updateStartup.ConfirmReadyAsync(() =>
                {
                    if (window.Dispatcher.HasShutdownStarted
                        || window.Dispatcher.HasShutdownFinished)
                    {
                        return false;
                    }

                    return window.Dispatcher.CheckAccess()
                        ? window.IsVisible
                        : window.Dispatcher.Invoke(() => window.IsVisible);
                });
            }
            catch
            {
                // A failed recovery handshake must not crash normal launcher startup.
            }
        }
    }

    private void LoadV2Resources()
    {
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];

        foreach (string resourcePath in resourcePaths)
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(resourcePath, UriKind.Relative)
            });
        }
    }

    private static void ApplyV2PreviewOptions(LauncherShellV2 window, IEnumerable<string> arguments)
    {
        foreach (string argument in arguments)
        {
            const string sizePrefix = "--ui-v2-size=";
            if (argument.StartsWith(sizePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string[] dimensions = argument[sizePrefix.Length..].Split(['x', 'X']);
                if (dimensions.Length == 2
                    && double.TryParse(dimensions[0], out double width)
                    && double.TryParse(dimensions[1], out double height))
                {
                    window.Width = Math.Max(window.MinWidth, width);
                    window.Height = Math.Max(window.MinHeight, height);
                }
            }

            if (string.Equals(argument, "--ui-v2-friends-open", StringComparison.OrdinalIgnoreCase))
            {
                window.SetFriendsDrawerOpenForPreview();
            }
        }
    }
}
