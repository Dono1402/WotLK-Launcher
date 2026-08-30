using System.Reflection;
using WotLK.Launcher.Game;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherRuntimeDependencies
{
    internal required Func<LauncherSettings> LoadSettings { get; init; }

    internal required Func<ILauncherAuthService> CreateAuthentication { get; init; }

    internal required GameClientStateReader GameClientStateReader { get; init; }

    internal required Func<string> GetLauncherVersion { get; init; }

    internal static LauncherRuntimeDependencies CreateProduction()
    {
        return new LauncherRuntimeDependencies
        {
            LoadSettings = LauncherSettings.Load,
            CreateAuthentication = static () => new LauncherAuthService(),
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = static () =>
            {
                Version? version = Assembly.GetExecutingAssembly().GetName().Version;
                return "v" + (version?.ToString(3) ?? "0.0.0");
            }
        };
    }
}

internal sealed class LauncherRuntime : IDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ILauncherAuthService _authentication;
    private readonly LauncherSessionCoordinator _sessionCoordinator;
    private int _disposeState;

    internal LauncherRuntime(LauncherRuntimeDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        Settings = dependencies.LoadSettings();
        _authentication = dependencies.CreateAuthentication();
        LocalClient = dependencies.GameClientStateReader.Read(Settings);
        LauncherVersion = dependencies.GetLauncherVersion();
        _sessionCoordinator = new LauncherSessionCoordinator(
            _authentication,
            _lifetimeCancellation.Token);
    }

    internal LauncherSettings Settings { get; }

    internal GameClientLocalState LocalClient { get; }

    internal string LauncherVersion { get; }

    internal bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    internal static LauncherRuntime CreateProduction()
    {
        return new LauncherRuntime(LauncherRuntimeDependencies.CreateProduction());
    }

    internal Task<LauncherSessionRestoreResult> InitializeAsync()
    {
        if (IsDisposed)
        {
            return Task.FromResult(new LauncherSessionRestoreResult(
                LauncherSessionRestoreStatus.Cancelled,
                null));
        }

        return _sessionCoordinator.RestoreOnceAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        _authentication.Dispose();
        _lifetimeCancellation.Dispose();
    }
}
