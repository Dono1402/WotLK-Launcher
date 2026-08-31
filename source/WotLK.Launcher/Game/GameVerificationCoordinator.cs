using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.Game;

internal interface IGameVerificationRuntime
{
    event EventHandler? AvailabilityChanged;

    event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

    bool CanVerify { get; }

    GameRuntimeSnapshot CurrentSnapshot { get; }

    GameVerificationStartStatus TryStartVerification();
}

internal sealed class GameVerificationCoordinator : IGameVerificationRuntime, IDisposable
{
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly IGameClientVerificationService _verificationService;
    private readonly LauncherOperationCoordinator _operations;
    private readonly LauncherSettings _settings;
    private readonly Func<bool> _isAuthenticated;
    private readonly Func<string, bool> _hasPlayableClient;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private readonly string? _installedVersion;
    private GameRuntimeSnapshot _currentSnapshot;
    private Task _activeVerification = Task.CompletedTask;
    private LauncherOperationLease? _activeLease;
    private long _nextSequence;
    private long _lastProgressTimestamp;
    private int _disposeState;

    internal GameVerificationCoordinator(
        IGameClientVerificationService verificationService,
        LauncherOperationCoordinator operations,
        LauncherSettings settings,
        GameClientLocalState localState,
        Func<bool> isAuthenticated,
        Action<string> writeLog,
        Func<string, bool>? hasPlayableClient = null,
        TimeProvider? timeProvider = null)
    {
        _verificationService = verificationService
            ?? throw new ArgumentNullException(nameof(verificationService));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(localState);
        _isAuthenticated = isAuthenticated ?? throw new ArgumentNullException(nameof(isAuthenticated));
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
        _operations.StateChanged += Operations_StateChanged;
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
        bool isPlayable;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
            {
                return GameVerificationStartStatus.ShuttingDown;
            }

            if (!_isAuthenticated())
            {
                return GameVerificationStartStatus.Unauthenticated;
            }

            isPlayable = _currentSnapshot.IsPlayable;
        }

        LauncherOperationStartResult start = _operations.TryBegin(
            LauncherOperationKind.Verify,
            canUserCancel: false,
            clientIsPlayable: isPlayable);
        if (!start.IsStarted)
        {
            return start.Status switch
            {
                LauncherOperationStartStatus.ShuttingDown =>
                    GameVerificationStartStatus.ShuttingDown,
                LauncherOperationStartStatus.RejectedByCompatibility =>
                    GameVerificationStartStatus.RejectedByCompatibility,
                _ => GameVerificationStartStatus.Busy
            };
        }

        LauncherOperationLease lease = start.Lease!;
        GameRuntimeSnapshot checkingSnapshot;
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _activeLease = lease;
            _lastProgressTimestamp = long.MinValue;
            checkingSnapshot = CreateSnapshotUnsafe(
                lease.OperationId,
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
        _ = RunVerificationAsync(lease, completion);
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
        _operations.CancelForShutdown();
    }

    internal Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _activeVerification;
        }
    }

    private async Task RunVerificationAsync(
        LauncherOperationLease lease,
        TaskCompletionSource completion)
    {
        try
        {
            GameClientVerificationResult result = await _verificationService.VerifyAsync(
                _settings,
                reportFileProgress: true,
                progress => ReportProgress(lease, progress),
                lease.CancellationToken);
            CompleteWithResult(lease, result);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            WriteFailureSafely(ex);
            CompleteWithFailure(lease, ex);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeLease, lease))
                {
                    _activeLease = null;
                }
            }

            lease.Complete();
            completion.TrySetResult();
        }
    }

    private void ReportProgress(
        LauncherOperationLease lease,
        GameVerificationProgress progress)
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentOperationUnsafe(lease))
            {
                return;
            }

            if (progress.Phase == GameVerificationPhase.ScanningFiles
                && !ShouldPublishFileProgressUnsafe(progress))
            {
                return;
            }

            snapshot = CreateSnapshotUnsafe(
                lease.OperationId,
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
        LauncherOperationLease lease,
        GameClientVerificationResult result)
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentOperationUnsafe(lease))
            {
                return;
            }

            bool isPlayable = result.Action != GameAction.Install;
            snapshot = CreateSnapshotUnsafe(
                lease.OperationId,
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

    private void CompleteWithFailure(
        LauncherOperationLease lease,
        Exception exception)
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentOperationUnsafe(lease))
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

            snapshot = CreateSnapshotUnsafe(
                lease.OperationId,
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
            CanVerify: !isVerifying && CanVerifyUnsafe(),
            IsPlayable: isPlayable,
            InstallPath: _settings.InstallPath,
            InstalledVersion: _installedVersion,
            AvailableVersion: availableVersion,
            ProcessedFileCount: processedFileCount,
            TotalFileCount: totalFileCount,
            FailureCategory: failureCategory);
    }

    private bool CanVerifyUnsafe()
    {
        return Volatile.Read(ref _disposeState) == 0
            && _isAuthenticated()
            && _operations.CanBegin(
                LauncherOperationKind.Verify,
                _currentSnapshot.IsPlayable);
    }

    private bool IsCurrentOperationUnsafe(LauncherOperationLease lease)
    {
        return ReferenceEquals(_activeLease, lease)
            && _currentSnapshot.OperationId == lease.OperationId
            && lease.IsCurrent;
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

    private void Operations_StateChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposeState) == 0)
        {
            RefreshAuthenticationAvailability();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _operations.StateChanged -= Operations_StateChanged;
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
