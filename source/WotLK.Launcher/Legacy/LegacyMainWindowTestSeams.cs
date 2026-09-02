using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Updater;

namespace WotLK.Launcher;

internal enum LegacyStartupEvent
{
    ComponentsInitialized,
    AuthenticationCreated,
    AuthorizedHttpClientCreated,
    SettingsLoaded,
    SettingsSaved,
    GameDirectoryPrepared,
    LauncherUpdateTimerCreated,
    FriendRefreshTimerCreated,
    ToastTimerCreated,
    InitialGameActionSet,
    GamePageSelected,
    LauncherUpdateCheckScheduled,
    LoadedSubscribed,
    LauncherUpdateTimerStarted,
    FriendRefreshTimerStarted,
    SessionRestoreStarted,
    SessionRestoreCompleted,
    InitialRemoteAnalysisStarted,
    InitialRemoteAnalysisCompleted,
    LauncherUpdateTimerTick,
    FriendRefreshTimerTick,
    WindowClosing,
    OperationCancellationRequested,
    WindowDisposed
}

internal interface ILegacyStartupObserver
{
    void Record(LegacyStartupEvent startupEvent);
}

internal sealed class NullLegacyStartupObserver : ILegacyStartupObserver
{
    internal static NullLegacyStartupObserver Instance { get; } = new();

    private NullLegacyStartupObserver()
    {
    }

    public void Record(LegacyStartupEvent startupEvent)
    {
    }
}

internal interface ILegacyDispatcherTimer
{
    event EventHandler? Tick;

    TimeSpan Interval { get; }

    bool IsEnabled { get; }

    void Start();

    void Stop();
}

internal sealed class LegacyDispatcherTimer : ILegacyDispatcherTimer
{
    private readonly DispatcherTimer _timer;

    internal LegacyDispatcherTimer(TimeSpan interval, DispatcherPriority priority)
    {
        _timer = new DispatcherTimer(priority)
        {
            Interval = interval
        };
    }

    public event EventHandler? Tick
    {
        add => _timer.Tick += value;
        remove => _timer.Tick -= value;
    }

    public TimeSpan Interval => _timer.Interval;

    public bool IsEnabled => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();
}

internal sealed class LegacyMainWindowDependencies
{
    internal required Func<ILauncherAuthService> CreateAuthentication { get; init; }

    internal required Func<Func<string?>, HttpClient> CreateAuthorizedHttpClient { get; init; }

    internal required Func<LauncherSettings> LoadSettings { get; init; }

    internal required Action<LauncherSettings> SaveSettings { get; init; }

    internal required Action<string> PrepareGameDirectory { get; init; }

    internal required Func<string, bool> HasPlayableClient { get; init; }

    internal required Func<string, bool> IsGameRunning { get; init; }

    internal required Func<string, string, string> EnsureDefaultClientConfig { get; init; }

    internal required Action<GameTicket, string> WriteGameSingleSignOn { get; init; }

    internal required Func<ProcessStartInfo, Process?> StartGameProcess { get; init; }

    internal required Func<TimeSpan, DispatcherPriority, ILegacyDispatcherTimer> CreateTimer { get; init; }

    internal required Action<string> PersistLogLine { get; init; }

    internal IGameInstallPlatform? GameInstallPlatform { get; init; }

    internal IGameClientMaintenanceService? GameClientMaintenanceService { get; init; }

    internal ILauncherSelfUpdateFinalizer LauncherSelfUpdateFinalizer { get; init; } =
        WotLK.Launcher.Updater.LauncherSelfUpdateFinalizer.CreateProduction();

    internal LauncherOperationCoordinator OperationCoordinator { get; init; } = new();

    internal ILegacyStartupObserver StartupObserver { get; init; } = NullLegacyStartupObserver.Instance;

    internal static LegacyMainWindowDependencies CreateProduction()
    {
        return new LegacyMainWindowDependencies
        {
            CreateAuthentication = static () => new LauncherAuthService(),
            CreateAuthorizedHttpClient = static accessTokenProvider =>
                new HttpClient(new AtlasAuthorizationHandler(accessTokenProvider))
                {
                    Timeout = TimeSpan.FromMinutes(30)
                },
            LoadSettings = LauncherSettings.Load,
            SaveSettings = static settings => settings.Save(),
            PrepareGameDirectory = GameDirectoryAccess.PrepareElevatedSession,
            HasPlayableClient = GameInstallServices.HasPlayableClient,
            IsGameRunning = GameInstallServices.IsGameRunning,
            EnsureDefaultClientConfig = GameInstallServices.EnsureDefaultClientConfig,
            WriteGameSingleSignOn = GameSingleSignOn.Write,
            StartGameProcess = Process.Start,
            CreateTimer = static (interval, priority) => new LegacyDispatcherTimer(interval, priority),
            PersistLogLine = static line =>
            {
                Directory.CreateDirectory(LauncherSettings.SettingsDirectory);
                File.AppendAllText(
                    LauncherSettings.LauncherLogPath,
                    line,
                    new UTF8Encoding(false));
            }
        };
    }
}

internal sealed record LegacyMainWindowSnapshot(
    GameAction GameAction,
    string UpdateButtonLabel,
    bool UpdateButtonEnabled,
    string HomeButtonLabel,
    bool HomeButtonEnabled,
    string HomeClientStatus,
    bool VerifyButtonEnabled,
    bool AddonsNavigationEnabled,
    bool LauncherSelfUpdateEnabled,
    double Progress,
    string ProgressText,
    bool HasActiveOperation,
    bool IsRefreshingGameAction,
    string StatusText,
    string LogText,
    string ToastTitle,
    string ToastMessage,
    bool IsToastVisible);

internal sealed record LegacyLocalPathSnapshot(
    string InstallPath,
    string LauncherLogPath);
