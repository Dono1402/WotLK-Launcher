using System.IO;

namespace WotLK.Launcher.Runtime;

internal interface ILauncherLocalActions
{
    event EventHandler? AvailabilityChanged;

    bool CanOpenGameFolder { get; }

    bool CanOpenDiagnostic { get; }

    LauncherLocalActionResult OpenGameFolder();

    LauncherLocalActionResult OpenDiagnostic();

    void BeginShutdown();
}

internal sealed class LauncherLocalActionCoordinator : ILauncherLocalActions
{
    private static readonly TimeSpan RepeatProtection = TimeSpan.FromMilliseconds(500);

    private readonly LauncherSettings _settings;
    private readonly string _launcherLogPath;
    private readonly ILauncherShellService _shellService;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private readonly LocalActionGate _gameFolderGate = new();
    private readonly LocalActionGate _diagnosticGate = new();
    private int _shutdownState;

    internal LauncherLocalActionCoordinator(
        LauncherSettings settings,
        string launcherLogPath,
        ILauncherShellService shellService,
        Action<string> writeLog,
        TimeProvider? timeProvider = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launcherLogPath = launcherLogPath ?? throw new ArgumentNullException(nameof(launcherLogPath));
        _shellService = shellService ?? throw new ArgumentNullException(nameof(shellService));
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler? AvailabilityChanged;

    public bool CanOpenGameFolder => !IsShuttingDown && _gameFolderGate.IsIdle;

    public bool CanOpenDiagnostic => !IsShuttingDown && _diagnosticGate.IsIdle;

    private bool IsShuttingDown => Volatile.Read(ref _shutdownState) != 0;

    public LauncherLocalActionResult OpenGameFolder()
    {
        return Execute(
            LauncherLocalAction.OpenGameFolder,
            _gameFolderGate,
            () => _shellService.OpenFolder(
                LauncherLocalAction.OpenGameFolder,
                _settings.InstallPath));
    }

    public LauncherLocalActionResult OpenDiagnostic()
    {
        return Execute(
            LauncherLocalAction.OpenDiagnostic,
            _diagnosticGate,
            OpenDiagnosticCore);
    }

    public void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) == 0)
        {
            NotifyAvailabilityChanged();
        }
    }

    private LauncherLocalActionResult OpenDiagnosticCore()
    {
        try
        {
            if (File.Exists(_launcherLogPath))
            {
                return _shellService.SelectFile(
                    LauncherLocalAction.OpenDiagnostic,
                    _launcherLogPath);
            }

            string? directory = Path.GetDirectoryName(_launcherLogPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                return _shellService.OpenFolder(
                    LauncherLocalAction.OpenDiagnostic,
                    directory);
            }

            return new LauncherLocalActionResult(
                LauncherLocalAction.OpenDiagnostic,
                LauncherLocalActionStatus.Unavailable,
                LauncherLocalFailureCategory.NoJournal,
                "Aucun journal n'est encore disponible.");
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return new LauncherLocalActionResult(
                LauncherLocalAction.OpenDiagnostic,
                LauncherLocalActionStatus.Failed,
                LauncherLocalFailureCategory.InvalidPath,
                "Le chemin du journal n'est pas valide.",
                ex.GetType().Name);
        }
    }

    private LauncherLocalActionResult Execute(
        LauncherLocalAction action,
        LocalActionGate gate,
        Func<LauncherLocalActionResult> execute)
    {
        if (IsShuttingDown)
        {
            return LauncherLocalActionResult.ShuttingDown(action);
        }

        if (!gate.TryEnter(_timeProvider, RepeatProtection))
        {
            return LauncherLocalActionResult.Busy(action);
        }

        NotifyAvailabilityChanged();
        try
        {
            if (IsShuttingDown)
            {
                return LauncherLocalActionResult.ShuttingDown(action);
            }

            LauncherLocalActionResult result;
            try
            {
                result = execute();
            }
            catch (Exception ex)
            {
                result = new LauncherLocalActionResult(
                    action,
                    LauncherLocalActionStatus.Failed,
                    LauncherLocalFailureCategory.ShellLaunchFailed,
                    action == LauncherLocalAction.OpenGameFolder
                        ? "Impossible d'ouvrir le dossier du jeu."
                        : "Impossible d'ouvrir le journal du launcher.",
                    ex.GetType().Name);
            }

            LogFailure(result);
            return result;
        }
        finally
        {
            gate.Exit();
            NotifyAvailabilityChanged();
        }
    }

    private void LogFailure(LauncherLocalActionResult result)
    {
        if (result.Status is not (LauncherLocalActionStatus.Failed
            or LauncherLocalActionStatus.Unavailable)
            || result.FailureCategory == LauncherLocalFailureCategory.NoJournal)
        {
            return;
        }

        try
        {
            string exception = string.IsNullOrWhiteSpace(result.ExceptionType)
                ? "none"
                : result.ExceptionType;
            _writeLog(
                $"Action locale V2 refusée: operation={result.Action}; "
                + $"category={result.FailureCategory}; exception={exception}.");
        }
        catch
        {
            // A diagnostic failure must not turn a local shell action into a second failure.
        }
    }

    private void NotifyAvailabilityChanged()
    {
        try
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // The local action remains independent from presentation subscribers.
        }
    }

    private sealed class LocalActionGate
    {
        private readonly object _sync = new();
        private bool _isRunning;
        private bool _hasStarted;
        private long _lastStartedTimestamp;

        internal bool IsIdle
        {
            get
            {
                lock (_sync)
                {
                    return !_isRunning;
                }
            }
        }

        internal bool TryEnter(TimeProvider timeProvider, TimeSpan repeatProtection)
        {
            lock (_sync)
            {
                if (_isRunning)
                {
                    return false;
                }

                long now = timeProvider.GetTimestamp();
                if (_hasStarted
                    && timeProvider.GetElapsedTime(_lastStartedTimestamp, now) < repeatProtection)
                {
                    return false;
                }

                _isRunning = true;
                _hasStarted = true;
                _lastStartedTimestamp = now;
                return true;
            }
        }

        internal void Exit()
        {
            lock (_sync)
            {
                _isRunning = false;
            }
        }
    }
}
