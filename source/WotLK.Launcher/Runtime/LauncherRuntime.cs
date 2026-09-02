using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using WotLK.Launcher.Account;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Game;
using WotLK.Launcher.Updater;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherRuntimeDependencies
{
    internal required Func<LauncherSettings> LoadSettings { get; init; }

    internal Action<LauncherSettings> SaveSettings { get; init; } = static _ => { };

    internal required Func<ILauncherAuthService> CreateAuthentication { get; init; }

    internal required GameClientStateReader GameClientStateReader { get; init; }

    internal required Func<string> GetLauncherVersion { get; init; }

    internal Action<string> WriteRuntimeLog { get; init; } = static _ => { };

    internal Action<string> WriteLocalActionLog { get; init; } = static _ => { };

    internal ILauncherShellService LocalShellService { get; init; } =
        LauncherShellService.CreateProduction();

    internal Func<string> GetLauncherLogPath { get; init; } =
        static () => LauncherSettings.LauncherLogPath;

    internal TimeProvider LocalActionTimeProvider { get; init; } = TimeProvider.System;

    internal Func<Func<string?>, HttpClient> CreateAuthorizedHttpClient { get; init; } =
        static accessTokenProvider => new HttpClient(
            new AtlasAuthorizationHandler(accessTokenProvider))
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

    internal Func<HttpClient, GameClientStateReader, IGameClientVerificationService>
        CreateGameVerificationService { get; init; } =
        static (httpClient, stateReader) =>
        {
            InstalledManifestStore manifestStore = new();
            GameFileVerifier verifier = new(manifestStore, stateReader);
            return new GameClientVerificationService(
                new GameManifestClient(httpClient),
                verifier,
                manifestStore);
        };

    internal Func<HttpClient, GameClientStateReader, IGameClientMaintenanceService>
        CreateGameMaintenanceService { get; init; } =
        static (httpClient, stateReader) =>
        {
            InstalledManifestStore manifestStore = new();
            GameFileVerifier verifier = new(manifestStore, stateReader);
            return new GameClientMaintenanceService(
                new GameManifestClient(httpClient),
                verifier,
                manifestStore,
                new GameFileTransferService(httpClient),
                new GameFileCleanupService(verifier),
                new GameInstallPlatformAdapter());
        };

    internal Func<HttpClient, IAddonManagementService> CreateAddonManagementService { get; init; } =
        static httpClient => new LegacyAddonManagementService(httpClient);

    internal Func<TimeSpan, ILauncherSelfUpdateTimer> CreateLauncherSelfUpdateTimer { get; init; } =
        static interval => new DispatcherLauncherSelfUpdateTimer(interval);

    internal Func<HttpClient, ILauncherSelfUpdateClient> CreateLauncherSelfUpdateClient { get; init; } =
        static httpClient => new LauncherSelfUpdateHttpClient(httpClient);

    internal ILauncherSelfUpdateFinalizer LauncherSelfUpdateFinalizer { get; init; } =
        WotLK.Launcher.Updater.LauncherSelfUpdateFinalizer.CreateProduction();

    internal Action RequestApplicationShutdown { get; init; } = RequestProductionShutdown;

    internal bool SelfUpdateRecoveryOccurred { get; init; }

    internal Func<IGameLaunchSession, IGameLaunchService> CreateGameLaunchService { get; init; } =
        static session => new GameLaunchService(
            session,
            new ProductionGameLaunchPlatform(),
            new ProductionGameProcessStarter());

    internal Func<string, bool> HasPlayableClient { get; init; } =
        GameInstallServices.HasPlayableClient;

    internal Func<string, bool> IsGameRunning { get; init; } =
        GameInstallServices.IsGameRunning;

    internal TimeProvider VerificationTimeProvider { get; init; } = TimeProvider.System;

    internal TimeProvider AddonsTimeProvider { get; init; } = TimeProvider.System;

    internal TimeProvider DashboardTimeProvider { get; init; } = TimeProvider.System;

    internal TimeProvider AccountTimeProvider { get; init; } = TimeProvider.System;

    internal TimeProvider FriendsTimeProvider { get; init; } = TimeProvider.System;

    internal Uri AvatarApiBaseUri { get; init; } = AtlasNetwork.LauncherApiBaseUri;

    internal Func<string> GetAvatarCacheRoot { get; init; } = static () => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Atlas Launcher",
        "cache",
        "avatars");

    internal Func<HttpClient, Uri, IAvatarMediaClient> CreateAvatarMediaClient { get; init; } =
        static (httpClient, apiBaseUri) => new AvatarMediaClient(httpClient, apiBaseUri);

    internal Func<IAvatarMediaClient, string, CancellationToken, Action, AvatarImageCache>
        CreateAvatarImageCache { get; init; } =
        static (mediaClient, root, lifetimeToken, onUnauthorized) =>
            new AvatarImageCache(mediaClient, root, lifetimeToken, onUnauthorized);

    internal static LauncherRuntimeDependencies CreateProduction(
        bool selfUpdateRecoveryOccurred = false)
    {
        return new LauncherRuntimeDependencies
        {
            LoadSettings = LauncherSettings.Load,
            SaveSettings = static settings => settings.Save(),
            CreateAuthentication = static () => new LauncherAuthService(),
            GameClientStateReader = new GameClientStateReader(),
            WriteRuntimeLog = WriteProductionLog,
            WriteLocalActionLog = WriteProductionLog,
            LocalShellService = LauncherShellService.CreateProduction(),
            SelfUpdateRecoveryOccurred = selfUpdateRecoveryOccurred,
            CreateAuthorizedHttpClient = static accessTokenProvider => new HttpClient(
                new AtlasAuthorizationHandler(accessTokenProvider))
            {
                Timeout = TimeSpan.FromMinutes(30)
            },
            GetLauncherVersion = static () =>
            {
                Version? version = Assembly.GetExecutingAssembly().GetName().Version;
                return "v" + (version?.ToString(3) ?? "0.0.0");
            }
        };
    }

    private static void RequestProductionShutdown()
    {
        System.Windows.Application application = System.Windows.Application.Current;
        if (application.Dispatcher.CheckAccess())
        {
            application.Shutdown();
            return;
        }

        _ = application.Dispatcher.BeginInvoke(new Action(application.Shutdown));
    }

    private static void WriteProductionLog(string message)
    {
        try
        {
            Directory.CreateDirectory(LauncherSettings.SettingsDirectory);
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(
                LauncherSettings.LauncherLogPath,
                line,
                new UTF8Encoding(false));
        }
        catch
        {
            // Runtime diagnostics must never interrupt launcher startup or shutdown.
        }
    }
}

internal sealed class LauncherRuntime : IDisposable
{
    private readonly object _lifecycleSync = new();
    private readonly ILauncherAuthService _authentication;
    private readonly HttpClient _clientHttpClient;
    private readonly LauncherSessionCoordinator _sessionCoordinator;
    private readonly Action<string> _writeRuntimeLog;
    private Task<LauncherSessionRestoreResult>? _initializeTask;
    private int _disposeState;

    internal LauncherRuntime(LauncherRuntimeDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        Settings = dependencies.LoadSettings();
        _writeRuntimeLog = dependencies.WriteRuntimeLog;
        _authentication = dependencies.CreateAuthentication();
        LocalClient = dependencies.GameClientStateReader.Read(Settings);
        LauncherVersion = dependencies.GetLauncherVersion();
        Operations = new LauncherOperationCoordinator();
        _sessionCoordinator = new LauncherSessionCoordinator(
            _authentication,
            Operations.ShutdownToken,
            dependencies.WriteRuntimeLog);
        _clientHttpClient = dependencies.CreateAuthorizedHttpClient(
            () => _authentication.AccessToken);
        AvatarMedia = dependencies.CreateAvatarMediaClient(
            _clientHttpClient,
            dependencies.AvatarApiBaseUri);
        AvatarImages = dependencies.CreateAvatarImageCache(
            AvatarMedia,
            dependencies.GetAvatarCacheRoot(),
            Operations.ShutdownToken,
            _sessionCoordinator.NotifyAuthenticatedRequestUnauthorized);
        IGameClientVerificationService verificationService =
            dependencies.CreateGameVerificationService(
                _clientHttpClient,
                dependencies.GameClientStateReader);
        IGameClientMaintenanceService maintenanceService =
            dependencies.CreateGameMaintenanceService(
                _clientHttpClient,
                dependencies.GameClientStateReader);
        IGameLaunchService launchService = dependencies.CreateGameLaunchService(
            _sessionCoordinator);
        Addons = new LauncherAddonsCoordinator(
            dependencies.CreateAddonManagementService(_clientHttpClient),
            new LauncherAddonsSessionContext(_sessionCoordinator),
            Operations,
            Settings,
            dependencies.HasPlayableClient,
            dependencies.IsGameRunning,
            dependencies.WriteRuntimeLog,
            dependencies.AddonsTimeProvider);
        Game = new GameRuntimeCoordinator(
            verificationService,
            Operations,
            Settings,
            LocalClient,
            () => _authentication.IsAuthenticated,
            dependencies.WriteRuntimeLog,
            dependencies.HasPlayableClient,
            dependencies.VerificationTimeProvider,
            maintenanceService,
            () => dependencies.GameClientStateReader.Read(Settings),
            launchService,
            () => _sessionCoordinator.CurrentSnapshot.State);
        SelfUpdate = new LauncherSelfUpdateCoordinator(
            Operations,
            dependencies.CreateLauncherSelfUpdateClient(_clientHttpClient),
            dependencies.LauncherSelfUpdateFinalizer,
            dependencies.CreateLauncherSelfUpdateTimer(
                LauncherSelfUpdateCoordinator.CheckInterval),
            Settings.AutomaticLauncherUpdates,
            LauncherVersion,
            dependencies.SelfUpdateRecoveryOccurred,
            writeLog: dependencies.WriteRuntimeLog,
            requestShutdown: dependencies.RequestApplicationShutdown);
        Activity = new LauncherActivityCoordinator(Operations, Game, Addons, SelfUpdate);
        SettingsRuntime = new LauncherSettingsCoordinator(
            Settings,
            Operations,
            dependencies.SaveSettings,
            changeKind =>
            {
                bool pathChanged = changeKind == LauncherSettingsChangeKind.InstallPath;
                bool refreshed = Game.RefreshLocalSettings(pathChanged);
                if (pathChanged)
                {
                    Addons.RefreshLocalState();
                }
                if (pathChanged && refreshed && _authentication.IsAuthenticated)
                {
                    _ = Game.TryStartVerification();
                }
            },
            dependencies.WriteRuntimeLog);
        LocalActions = new LauncherLocalActionCoordinator(
            Settings,
            dependencies.GetLauncherLogPath(),
            dependencies.LocalShellService,
            dependencies.WriteLocalActionLog,
            dependencies.LocalActionTimeProvider);
        Dashboard = new LauncherDashboardCoordinator(
            _authentication,
            Operations.ShutdownToken,
            dependencies.WriteRuntimeLog,
            dependencies.DashboardTimeProvider);
        Profile = new LauncherProfileCoordinator(
            _sessionCoordinator,
            Operations,
            Game,
            Dashboard);
        Account = new LauncherAccountCoordinator(
            _sessionCoordinator,
            _authentication,
            Operations,
            AvatarMedia,
            AvatarImages,
            () => _authentication.Session?.Profile,
            dependencies.WriteRuntimeLog,
            dependencies.AccountTimeProvider);
        Friends = new LauncherFriendsCoordinator(
            _sessionCoordinator,
            _authentication,
            Operations.ShutdownToken,
            () => _authentication.Session?.Profile,
            dependencies.WriteRuntimeLog,
            dependencies.FriendsTimeProvider);
        SelfUpdate.ScheduleInitialCheck();
        SelfUpdate.StartPeriodicChecks();
    }

    internal LauncherSettings Settings { get; }

    internal GameClientLocalState LocalClient { get; }

    internal string LauncherVersion { get; }

    internal ILauncherLocalActions LocalActions { get; }

    internal LauncherSettingsCoordinator SettingsRuntime { get; }

    internal LauncherOperationCoordinator Operations { get; }

    internal GameRuntimeCoordinator Game { get; }

    internal LauncherAddonsCoordinator Addons { get; }

    internal LauncherSelfUpdateCoordinator SelfUpdate { get; }

    internal LauncherActivityCoordinator Activity { get; }

    internal LauncherDashboardCoordinator Dashboard { get; }

    internal LauncherProfileCoordinator Profile { get; }

    internal IAvatarMediaClient AvatarMedia { get; }

    internal AvatarImageCache AvatarImages { get; }

    internal LauncherAccountCoordinator Account { get; }

    internal LauncherFriendsCoordinator Friends { get; }

    internal LauncherSessionCoordinator Session => _sessionCoordinator;

    internal void WriteRuntimeDiagnostic(string message)
    {
        try
        {
            _writeRuntimeLog(message);
        }
        catch
        {
            // Presentation diagnostics cannot interrupt the runtime.
        }
    }

    internal bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    internal static LauncherRuntime CreateProduction(bool selfUpdateRecoveryOccurred = false)
    {
        return new LauncherRuntime(
            LauncherRuntimeDependencies.CreateProduction(selfUpdateRecoveryOccurred));
    }

    internal Task<LauncherSessionRestoreResult> InitializeAsync()
    {
        lock (_lifecycleSync)
        {
            if (IsDisposed)
            {
                return Task.FromResult(new LauncherSessionRestoreResult(
                    LauncherSessionRestoreStatus.Cancelled,
                    null));
            }

            return _initializeTask ??= InitializeCoreAsync();
        }
    }

    private async Task<LauncherSessionRestoreResult> InitializeCoreAsync()
    {
        LauncherSessionRestoreResult result = await _sessionCoordinator
            .RestoreOnceAsync()
            .ConfigureAwait(false);
        if (!IsDisposed)
        {
            Game.RefreshAuthenticationAvailability();
            await Dashboard.InitializeAfterSessionRestoreAsync(result).ConfigureAwait(false);
        }

        return result;
    }

    internal LauncherSessionStartResult TryLogin(string username, string password)
    {
        return ObserveInteractiveAuthentication(
            _sessionCoordinator.TryLogin(username, password));
    }

    internal LauncherSessionStartResult TryRegister(
        string username,
        string email,
        string password,
        string passwordConfirmation)
    {
        return ObserveInteractiveAuthentication(
            _sessionCoordinator.TryRegister(
                username,
                email,
                password,
                passwordConfirmation));
    }

    internal bool CancelInteractiveAuthentication()
    {
        bool authenticationCancelled = _sessionCoordinator.CancelInteractiveAttempt();
        bool pendingPlayCancelled = Game.CancelPendingPlayAuthentication();
        return authenticationCancelled || pendingPlayCancelled;
    }

    internal void BeginShutdown()
    {
        lock (_lifecycleSync)
        {
            if (IsDisposed)
            {
                return;
            }

            LocalActions.BeginShutdown();
            SelfUpdate.BeginShutdown();
            SettingsRuntime.BeginShutdown();
            Dashboard.BeginShutdown();
            _sessionCoordinator.BeginShutdown();
            Addons.BeginShutdown();
            Game.BeginShutdown();
            Account.BeginShutdown();
            Friends.BeginShutdown();
        }
    }

    internal async Task<bool> WaitForShutdownAsync(TimeSpan timeout)
    {
        BeginShutdown();
        Task<bool> operations = Operations.WaitForIdleAsync(timeout);
        Task<bool> selfUpdate = SelfUpdate.WaitForIdleAsync(timeout);
        Task<bool> dashboard = Dashboard.WaitForIdleAsync(timeout);
        Task<bool> session = _sessionCoordinator.WaitForIdleAsync(timeout);
        Task<bool> addons = Addons.WaitForIdleAsync(timeout);
        Task<bool> profile = Profile.WaitForIdleAsync(timeout);
        Task<bool> account = Account.WaitForIdleAsync(timeout);
        Task<bool> friends = Friends.WaitForIdleAsync(timeout);
        bool[] results = await Task.WhenAll(
            operations,
            selfUpdate,
            dashboard,
            session,
            addons,
            profile,
            account,
            friends).ConfigureAwait(false);
        return results.All(result => result);
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposeState != 0)
            {
                return;
            }

            Volatile.Write(ref _disposeState, 1);
            LocalActions.BeginShutdown();
            SelfUpdate.BeginShutdown();
            SettingsRuntime.BeginShutdown();
            Dashboard.BeginShutdown();
            _sessionCoordinator.BeginShutdown();
            Addons.BeginShutdown();
            Game.BeginShutdown();
            Account.BeginShutdown();
            Friends.BeginShutdown();
            Friends.Dispose();
            Account.Dispose();
            Profile.Dispose();
            Activity.Dispose();
            SelfUpdate.Dispose();
            SettingsRuntime.Dispose();
            Dashboard.Dispose();
            Addons.Dispose();
            Game.Dispose();
            _sessionCoordinator.Dispose();
            AvatarImages.Dispose();
            _clientHttpClient.Dispose();
            _authentication.Dispose();
            Operations.Dispose();
        }
    }

    private LauncherSessionStartResult ObserveInteractiveAuthentication(
        LauncherSessionStartResult start)
    {
        if (!start.IsStarted || start.Completion is null)
        {
            return start;
        }

        return start with
        {
            Completion = ObserveInteractiveAuthenticationAsync(start.Completion)
        };
    }

    private async Task<LauncherSessionCompletion> ObserveInteractiveAuthenticationAsync(
        Task<LauncherSessionCompletion> completion)
    {
        LauncherSessionCompletion result = await completion.ConfigureAwait(false);
        if (result.Status != LauncherSessionCompletionStatus.Succeeded || IsDisposed)
        {
            return result;
        }

        Game.RefreshAuthenticationAvailability();
        Game.ResumePendingPlayAfterAuthentication();
        await Dashboard.RefreshAfterAuthenticationAsync().ConfigureAwait(false);
        return result;
    }
}
