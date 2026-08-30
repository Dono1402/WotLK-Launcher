using System.IO;
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

    internal static LauncherRuntimeDependencies CreateProduction()
    {
        return new LauncherRuntimeDependencies
        {
            LoadSettings = LauncherSettings.Load,
            CreateAuthentication = static () => new LauncherAuthService(),
            GameClientStateReader = new GameClientStateReader(),
            WriteRuntimeLog = WriteProductionLog,
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
                Path.Combine(LauncherSettings.SettingsDirectory, "launcher.log"),
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
            _lifetimeCancellation.Token,
            dependencies.WriteRuntimeLog);
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
        lock (_lifecycleSync)
        {
            if (IsDisposed)
            {
                return Task.FromResult(new LauncherSessionRestoreResult(
                    LauncherSessionRestoreStatus.Cancelled,
                    null));
            }

            return _sessionCoordinator.RestoreOnceAsync();
        }
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
            _lifetimeCancellation.Cancel();
            _authentication.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }
}
