using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
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
                "Les modes de prévisualisation nécessitent --ui-v2 et ne peuvent pas être combinés.",
                "Prévisualisation Atlas Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(2);
            return;
        }

        if (startupMode is LauncherStartupMode.UiV2
            or LauncherStartupMode.UiV2Preview
            or LauncherStartupMode.UiV2AuthPreview
            or LauncherStartupMode.UiV2ProfilePreview
            or LauncherStartupMode.UiV2SettingsPreview
            or LauncherStartupMode.UiV2FriendsPreview
            or LauncherStartupMode.UiV2AccountPreview
            or LauncherStartupMode.UiV2AddonsPreview
            or LauncherStartupMode.UiV2ActivityPreview)
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

            if (startupMode is LauncherStartupMode.UiV2Preview
                or LauncherStartupMode.UiV2AuthPreview
                or LauncherStartupMode.UiV2ProfilePreview
                or LauncherStartupMode.UiV2SettingsPreview
                or LauncherStartupMode.UiV2FriendsPreview
                or LauncherStartupMode.UiV2AccountPreview
                or LauncherStartupMode.UiV2AddonsPreview
                or LauncherStartupMode.UiV2ActivityPreview)
            {
                GamePreviewScenario previewScenario = LauncherV2PreviewData.ResolveScenario(e.Args);
                LauncherShellV2 previewWindow = startupMode switch
                {
                    LauncherStartupMode.UiV2AuthPreview => new LauncherShellV2(
                        previewScenario,
                        AuthPreviewArguments.ResolveScenario(e.Args)),
                    LauncherStartupMode.UiV2ProfilePreview => new LauncherShellV2(
                        previewScenario,
                        ProfilePreviewArguments.ResolveScenario(e.Args)),
                    LauncherStartupMode.UiV2SettingsPreview => new LauncherShellV2(
                        previewScenario,
                        SettingsPreviewArguments.ResolveScenario(e.Args)),
                    LauncherStartupMode.UiV2FriendsPreview => new LauncherShellV2(
                        previewScenario,
                        FriendsPreviewArguments.ResolveScenario(e.Args)),
                    LauncherStartupMode.UiV2AccountPreview => new LauncherShellV2(
                        previewScenario,
                        AccountPreviewArguments.ResolveScenario(e.Args)),
                    LauncherStartupMode.UiV2AddonsPreview => new LauncherShellV2(
                        previewScenario,
                        AddonsPreviewArguments.ResolveScenario(e.Args)),
                    LauncherStartupMode.UiV2ActivityPreview => new LauncherShellV2(
                        previewScenario,
                        ActivityPreviewArguments.ResolveScenario(e.Args)),
                    _ => new LauncherShellV2(previewScenario)
                };
                ApplyV2PreviewOptions(previewWindow, e.Args);
                MainWindow = previewWindow;
                previewWindow.Show();
                return;
            }

            StartRuntimeV2(updateStartup);
            return;
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

        var window = new MainWindow(
            LegacyMainWindowDependencies.CreateProduction(
                updateStartup?.RecoveryOccurred == true));
        MainWindow = window;
        window.Show();
        ScheduleUpdateReadyConfirmation(window, updateStartup);
    }

    internal static LauncherStartupMode ResolveStartupMode(IEnumerable<string> arguments)
    {
        string[] args = arguments as string[] ?? arguments.ToArray();
        bool useUiV2 = args.Any(argument =>
            string.Equals(argument, "--ui-v2", StringComparison.OrdinalIgnoreCase));
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
        if ((dedicatedPreviewCount > 0 && !useUiV2)
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

            bool usePreview = args.Any(argument =>
                argument.StartsWith("--preview-state=", StringComparison.OrdinalIgnoreCase));
            return usePreview ? LauncherStartupMode.UiV2Preview : LauncherStartupMode.UiV2;
        }

        if (GameDirectoryAccess.IsGrantAccessMode(args))
        {
            return LauncherStartupMode.GrantGameDirectoryAccess;
        }

        return GameInstallServices.IsGameUninstallMode(args)
            ? LauncherStartupMode.UninstallGame
            : LauncherStartupMode.Legacy;
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
            selfUpdate: runtime.SelfUpdate);
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
        RefreshDashboardCommand refreshDashboardCommand = new(runtime.Dashboard);
        dashboardState.AttachRefreshCommand(refreshDashboardCommand.Command);
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
                        window.Close();
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
            refreshDashboardCommand.Dispose();
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
            await runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(3));
            DisposePresentation();
            allowClose = true;
            await window.Dispatcher.InvokeAsync(window.Close, DispatcherPriority.Send);
        };
        window.Closing += closingHandler;
        window.Closed += (_, _) =>
        {
            window.Loaded -= loadedHandler;
            window.Closing -= closingHandler;
            DisposePresentation();
            runtime.Dispose();
        };

        MainWindow = window;
        window.Show();
        ScheduleUpdateReadyConfirmation(window, updateStartup);
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
