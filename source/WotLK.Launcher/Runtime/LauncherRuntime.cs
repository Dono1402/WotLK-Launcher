using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using WotLK.Launcher.Game;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherRuntimeDependencies
{
    internal required Func<LauncherSettings> LoadSettings { get; init; }

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

    internal Func<string, bool> HasPlayableClient { get; init; } =
        GameInstallServices.HasPlayableClient;

    internal TimeProvider VerificationTimeProvider { get; init; } = TimeProvider.System;

    internal static LauncherRuntimeDependencies CreateProduction()
    {
        return new LauncherRuntimeDependencies
        {
            LoadSettings = LauncherSettings.Load,
            CreateAuthentication = static () => new LauncherAuthService(),
            GameClientStateReader = new GameClientStateReader(),
            WriteRuntimeLog = WriteProductionLog,
            WriteLocalActionLog = WriteProductionLog,
            LocalShellService = LauncherShellService.CreateProduction(),
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
    private int _disposeState;

    internal LauncherRuntime(LauncherRuntimeDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        Settings = dependencies.LoadSettings();
        _authentication = dependencies.CreateAuthentication();
        LocalClient = dependencies.GameClientStateReader.Read(Settings);
        LauncherVersion = dependencies.GetLauncherVersion();
        Operations = new LauncherOperationCoordinator();
        _clientHttpClient = dependencies.CreateAuthorizedHttpClient(
            () => _authentication.AccessToken);
        IGameClientVerificationService verificationService =
            dependencies.CreateGameVerificationService(
                _clientHttpClient,
                dependencies.GameClientStateReader);
        IGameClientMaintenanceService maintenanceService =
            dependencies.CreateGameMaintenanceService(
                _clientHttpClient,
                dependencies.GameClientStateReader);
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
            () => dependencies.GameClientStateReader.Read(Settings));
        LocalActions = new LauncherLocalActionCoordinator(
            Settings,
            dependencies.GetLauncherLogPath(),
            dependencies.LocalShellService,
            dependencies.WriteLocalActionLog,
            dependencies.LocalActionTimeProvider);
        _sessionCoordinator = new LauncherSessionCoordinator(
            _authentication,
            Operations.ShutdownToken,
            dependencies.WriteRuntimeLog);
    }

    internal LauncherSettings Settings { get; }

    internal GameClientLocalState LocalClient { get; }

    internal string LauncherVersion { get; }

    internal ILauncherLocalActions LocalActions { get; }

    internal LauncherOperationCoordinator Operations { get; }

    internal GameRuntimeCoordinator Game { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    internal static LauncherRuntime CreateProduction()
    {
        return new LauncherRuntime(LauncherRuntimeDependencies.CreateProduction());
    }

    internal async Task<LauncherSessionRestoreResult> InitializeAsync()
    {
        Task<LauncherSessionRestoreResult> restoreTask;
        lock (_lifecycleSync)
        {
            if (IsDisposed)
            {
                return new LauncherSessionRestoreResult(
                    LauncherSessionRestoreStatus.Cancelled,
                    null);
            }

            restoreTask = _sessionCoordinator.RestoreOnceAsync();
        }

        LauncherSessionRestoreResult result = await restoreTask.ConfigureAwait(false);
        if (!IsDisposed)
        {
            Game.RefreshAuthenticationAvailability();
        }

        return result;
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
            Operations.CancelForShutdown();
        }
    }

    internal Task<bool> WaitForShutdownAsync(TimeSpan timeout)
    {
        BeginShutdown();
        return Operations.WaitForIdleAsync(timeout);
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
            Operations.CancelForShutdown();
            Game.Dispose();
            _clientHttpClient.Dispose();
            _authentication.Dispose();
            Operations.Dispose();
        }
    }
}
