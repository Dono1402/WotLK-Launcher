namespace WotLK.Launcher.Game;

internal interface IGameVerificationRuntime
{
    event EventHandler? AvailabilityChanged;

    event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

    bool CanVerify { get; }

    GameRuntimeSnapshot CurrentSnapshot { get; }

    GameVerificationStartStatus TryStartVerification();
}

internal sealed class GameVerificationCoordinator : IGameVerificationRuntime
{
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly IGameClientVerificationService _verificationService;
    private readonly LauncherSettings _settings;
    private readonly Func<bool> _isAuthenticated;
    private readonly Func<string, bool> _hasPlayableClient;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private readonly string? _installedVersion;
    private GameRuntimeSnapshot _currentSnapshot;
    private Task _activeVerification = Task.CompletedTask;
    private bool _isRunning;
    private bool _isShuttingDown;
    private long _nextSequence;
    private long _nextOperationId;
    private long _lastProgressTimestamp;

    internal GameVerificationCoordinator(
        IGameClientVerificationService verificationService,
        LauncherSettings settings,
        GameClientLocalState localState,
        Func<bool> isAuthenticated,
        CancellationToken lifetimeToken,
        Action<string> writeLog,
        Func<string, bool>? hasPlayableClient = null,
        TimeProvider? timeProvider = null)
    {
        _verificationService = verificationService
            ?? throw new ArgumentNullException(nameof(verificationService));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(localState);
        _isAuthenticated = isAuthenticated ?? throw new ArgumentNullException(nameof(isAuthenticated));
        _lifetimeToken = lifetimeToken;
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _installedVersion = localState.InstalledVersion;
        _currentSnapshot = new GameRuntimeSnapshot(
            Sequence: NextSequence(),
            OperationId: null,
            Action: localState.Action,
            UpdateKnowledge: localState.UpdateKnowledge,
            Phase: GameVerificationPhase.Stable,
            IsVerifying: false,
            CanVerify: false,
            IsPlayable: localState.IsPlayable,
            InstallPath: localState.InstallPath,
            InstalledVersion: localState.InstalledVersion,
            AvailableVersion: null,
            ProcessedFileCount: null,
            TotalFileCount: null,
            FailureCategory: null);
    }

    public event EventHandler? AvailabilityChanged;

    public event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

    public bool CanVerify
    {
        get
        {
            lock (_sync)
            {
                return CanVerifyUnsafe();
            }
        }
    }

    public GameRuntimeSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public GameVerificationStartStatus TryStartVerification()
    {
        long operationId;
        GameRuntimeSnapshot checkingSnapshot;
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_isShuttingDown || _lifetimeToken.IsCancellationRequested)
            {
                return GameVerificationStartStatus.ShuttingDown;
            }

            if (_isRunning)
            {
                return GameVerificationStartStatus.Busy;
            }

            if (!_isAuthenticated())
            {
                return GameVerificationStartStatus.Unauthenticated;
            }

            _isRunning = true;
            operationId = ++_nextOperationId;
            _lastProgressTimestamp = long.MinValue;
            checkingSnapshot = CreateSnapshotUnsafe(
                operationId,
                _currentSnapshot.Action,
                GameUpdateKnowledge.Checking,
                GameVerificationPhase.CheckingLocalClient,
                isVerifying: true,
                _currentSnapshot.IsPlayable,
                _currentSnapshot.AvailableVersion,
                processedFileCount: null,
                totalFileCount: null,
                failureCategory: null);
            _currentSnapshot = checkingSnapshot;
            _activeVerification = completion.Task;
        }

        Publish(checkingSnapshot, availabilityChanged: true);
        _ = RunVerificationAsync(operationId, completion);
        return GameVerificationStartStatus.Started;
    }

    internal void RefreshAuthenticationAvailability()
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            bool canVerify = CanVerifyUnsafe();
            if (_currentSnapshot.CanVerify != canVerify)
            {
                snapshot = _currentSnapshot with
                {
                    Sequence = NextSequence(),
                    CanVerify = canVerify
                };
                _currentSnapshot = snapshot;
            }
        }

        if (snapshot is not null)
        {
            Publish(snapshot, availabilityChanged: true);
        }
    }

    internal void BeginShutdown()
    {
        bool changed;
        lock (_sync)
        {
            changed = !_isShuttingDown;
            _isShuttingDown = true;
        }

        if (changed)
        {
            RaiseAvailabilityChanged();
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _activeVerification;
        }
    }

    private async Task RunVerificationAsync(
        long operationId,
        TaskCompletionSource completion)
    {
        try
        {
            GameClientVerificationResult result = await _verificationService.VerifyAsync(
                _settings,
                reportFileProgress: true,
                progress => ReportProgress(operationId, progress),
                _lifetimeToken);
            CompleteWithResult(operationId, result);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            CompleteAfterShutdown(operationId);
        }
        catch (Exception ex)
        {
            WriteFailureSafely(ex);
            CompleteWithFailure(operationId, ex);
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private void ReportProgress(long operationId, GameVerificationProgress progress)
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentOperationUnsafe(operationId))
            {
                return;
            }

            if (progress.Phase == GameVerificationPhase.ScanningFiles
                && !ShouldPublishFileProgressUnsafe(progress))
            {
                return;
            }

            snapshot = CreateSnapshotUnsafe(
                operationId,
                _currentSnapshot.Action,
                GameUpdateKnowledge.Checking,
                progress.Phase,
                isVerifying: true,
                _currentSnapshot.IsPlayable,
                _currentSnapshot.AvailableVersion,
                progress.ProcessedFileCount,
                progress.TotalFileCount,
                failureCategory: null);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: false);
    }

    private bool ShouldPublishFileProgressUnsafe(GameVerificationProgress progress)
    {
        if (progress.ProcessedFileCount is null || progress.TotalFileCount is null)
        {
            return true;
        }

        if (progress.ProcessedFileCount == 1
            || progress.ProcessedFileCount >= progress.TotalFileCount)
        {
            _lastProgressTimestamp = _timeProvider.GetTimestamp();
            return true;
        }

        long now = _timeProvider.GetTimestamp();
        if (_lastProgressTimestamp == long.MinValue
            || _timeProvider.GetElapsedTime(_lastProgressTimestamp, now)
                >= ProgressPublishInterval)
        {
            _lastProgressTimestamp = now;
            return true;
        }

        return false;
    }

    private void CompleteWithResult(
        long operationId,
        GameClientVerificationResult result)
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentOperationUnsafe(operationId))
            {
                return;
            }

            _isRunning = false;
            bool isPlayable = result.Action != GameAction.Install;
            snapshot = CreateSnapshotUnsafe(
                operationId,
                result.Action,
                result.UpdateKnowledge,
                GameVerificationPhase.Stable,
                isVerifying: false,
                isPlayable,
                string.IsNullOrWhiteSpace(result.AvailableVersion)
                    ? null
                    : result.AvailableVersion,
                processedFileCount: null,
                totalFileCount: null,
                failureCategory: null);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private void CompleteWithFailure(long operationId, Exception exception)
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentOperationUnsafe(operationId))
            {
                return;
            }

            bool isPlayable;
            try
            {
                isPlayable = _hasPlayableClient(_settings.InstallPath);
            }
            catch
            {
                isPlayable = _currentSnapshot.IsPlayable;
            }

            _isRunning = false;
            snapshot = CreateSnapshotUnsafe(
                operationId,
                isPlayable ? GameAction.Play : GameAction.Install,
                GameUpdateKnowledge.Unavailable,
                GameVerificationPhase.Stable,
                isVerifying: false,
                isPlayable,
                availableVersion: null,
                processedFileCount: null,
                totalFileCount: null,
                failureCategory: exception.GetType().Name);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private void CompleteAfterShutdown(long operationId)
    {
        lock (_sync)
        {
            if (_isRunning && _currentSnapshot.OperationId == operationId)
            {
                _isRunning = false;
            }
        }
    }

    private GameRuntimeSnapshot CreateSnapshotUnsafe(
        long? operationId,
        GameAction action,
        GameUpdateKnowledge knowledge,
        GameVerificationPhase phase,
        bool isVerifying,
        bool isPlayable,
        string? availableVersion,
        int? processedFileCount,
        int? totalFileCount,
        string? failureCategory)
    {
        return new GameRuntimeSnapshot(
            Sequence: NextSequence(),
            OperationId: operationId,
            Action: action,
            UpdateKnowledge: knowledge,
            Phase: phase,
            IsVerifying: isVerifying,
            CanVerify: !isVerifying && CanVerifyUnsafe(ignoreRunning: true),
            IsPlayable: isPlayable,
            InstallPath: _settings.InstallPath,
            InstalledVersion: _installedVersion,
            AvailableVersion: availableVersion,
            ProcessedFileCount: processedFileCount,
            TotalFileCount: totalFileCount,
            FailureCategory: failureCategory);
    }

    private bool CanVerifyUnsafe(bool ignoreRunning = false)
    {
        return !_isShuttingDown
            && !_lifetimeToken.IsCancellationRequested
            && (ignoreRunning || !_isRunning)
            && _isAuthenticated();
    }

    private bool IsCurrentOperationUnsafe(long operationId)
    {
        return !_isShuttingDown
            && _isRunning
            && _currentSnapshot.OperationId == operationId;
    }

    private long NextSequence()
    {
        return ++_nextSequence;
    }

    private void Publish(GameRuntimeSnapshot snapshot, bool availabilityChanged)
    {
        if (availabilityChanged)
        {
            RaiseAvailabilityChanged();
        }

        try
        {
            SnapshotChanged?.Invoke(this, new GameRuntimeSnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation subscribers must not interrupt verification.
        }
    }

    private void RaiseAvailabilityChanged()
    {
        try
        {
            AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Command subscribers are isolated from the verification lifecycle.
        }
    }

    private void WriteFailureSafely(Exception exception)
    {
        try
        {
            _writeLog(
                $"Analyse client V2 ignoree: category={exception.GetType().Name}.");
        }
        catch
        {
            // A logging failure cannot replace the stable local state.
        }
    }
}
