using System.IO;
using System.Net;
using System.Net.Http;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.Game;

internal interface IGameVerificationRuntime
{
    event EventHandler? AvailabilityChanged;

    event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

    bool CanVerify { get; }

    GameRuntimeSnapshot CurrentSnapshot { get; }

    GameVerificationStartStatus TryStartVerification();

    GameVerificationStartStatus TryStartFullRepair();
}

internal interface IGamePrimaryActionRuntime
{
    event EventHandler? AvailabilityChanged;

    event EventHandler? PlayAuthenticationRequired;

    event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

    bool CanExecutePrimaryAction { get; }

    GameRuntimeSnapshot CurrentSnapshot { get; }

    GamePrimaryActionStatus TryExecutePrimaryAction();
}

internal sealed class GameRuntimeCoordinator :
    IGameVerificationRuntime,
    IGamePrimaryActionRuntime,
    IDisposable
{
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _sync = new();
    private readonly IGameClientVerificationService _verificationService;
    private readonly IGameClientMaintenanceService? _maintenanceService;
    private readonly IGameLaunchService? _launchService;
    private readonly LauncherOperationCoordinator _operations;
    private readonly LauncherSettings _settings;
    private readonly Func<bool> _isAuthenticated;
    private readonly Func<LauncherSessionState> _getSessionState;
    private readonly Func<string, bool> _hasPlayableClient;
    private readonly Func<GameClientLocalState> _readLocalState;
    private readonly Action<string> _writeLog;
    private readonly TimeProvider _timeProvider;
    private string? _installedVersion;
    private GameRuntimeSnapshot _currentSnapshot;
    private Task _activeOperation = Task.CompletedTask;
    private LauncherOperationLease? _activeLease;
    private Task _activePlayOperation = Task.CompletedTask;
    private LauncherOperationLease? _activePlayLease;
    private TaskCompletionSource? _activePlayCompletion;
    private GameAction? _activeMaintenanceAction;
    private bool _repairDetectedChanges;
    private long _nextSequence;
    private long _lastProgressTimestamp;
    private GameClientMaintenancePhase? _lastMaintenancePhase;
    private int _suppressOperationAvailabilityRefresh;
    private int _disposeState;

    internal GameRuntimeCoordinator(
        IGameClientVerificationService verificationService,
        LauncherOperationCoordinator operations,
        LauncherSettings settings,
        GameClientLocalState localState,
        Func<bool> isAuthenticated,
        Action<string> writeLog,
        Func<string, bool>? hasPlayableClient = null,
        TimeProvider? timeProvider = null,
        IGameClientMaintenanceService? maintenanceService = null,
        Func<GameClientLocalState>? readLocalState = null,
        IGameLaunchService? launchService = null,
        Func<LauncherSessionState>? getSessionState = null)
    {
        _verificationService = verificationService
            ?? throw new ArgumentNullException(nameof(verificationService));
        _maintenanceService = maintenanceService;
        _launchService = launchService;
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(localState);
        _isAuthenticated = isAuthenticated ?? throw new ArgumentNullException(nameof(isAuthenticated));
        _getSessionState = getSessionState ?? (() => _isAuthenticated()
            ? LauncherSessionState.Authenticated
            : LauncherSessionState.SignedOut);
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
        _readLocalState = readLocalState ?? (() => new GameClientStateReader(
            _hasPlayableClient).Read(_settings));
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
            FailureCategory: null,
            GameLocale: localState.GameLocale);
        _operations.StateChanged += Operations_StateChanged;
        _currentSnapshot = RecalculateAvailabilityUnsafe(_currentSnapshot);
    }

    public event EventHandler? AvailabilityChanged;

    public event EventHandler? PlayAuthenticationRequired;

    internal event EventHandler? PlayStarted;

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

    public bool CanExecutePrimaryAction
    {
        get
        {
            lock (_sync)
            {
                return CanExecutePrimaryActionUnsafe();
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
            _activeOperation = completion.Task;
        }

        Publish(checkingSnapshot, availabilityChanged: true);
        _ = RunVerificationAsync(lease, completion);
        return GameVerificationStartStatus.Started;
    }

    public GameVerificationStartStatus TryStartFullRepair()
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

            if (_maintenanceService is null || !IsRepairPathValidUnsafe())
            {
                return GameVerificationStartStatus.RejectedByCompatibility;
            }

            isPlayable = _currentSnapshot.IsPlayable;
        }

        LauncherOperationStartResult start = _operations.TryBegin(
            LauncherOperationKind.GameRepair,
            canUserCancel: true,
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
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        GameRuntimeSnapshot preparingSnapshot;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
            {
                lease.CancelForShutdown();
                lease.Complete();
                return GameVerificationStartStatus.ShuttingDown;
            }

            _activeLease = lease;
            _activeMaintenanceAction = _currentSnapshot.Action;
            _repairDetectedChanges = false;
            _lastProgressTimestamp = long.MinValue;
            _lastMaintenancePhase = null;
            preparingSnapshot = CreateMaintenanceSnapshotUnsafe(
                lease,
                _currentSnapshot.Action,
                GameClientMaintenancePhase.LoadingManifest,
                availableVersion: _currentSnapshot.AvailableVersion,
                currentFile: null,
                processedFileCount: null,
                totalFileCount: null,
                downloadedBytes: null,
                totalBytes: null,
                bytesPerSecond: null,
                remaining: null);
            _currentSnapshot = preparingSnapshot;
            _activeOperation = completion.Task;
        }

        Publish(preparingSnapshot, availabilityChanged: true);
        _ = RunMaintenanceAsync(
            lease,
            preparingSnapshot.Action,
            completion,
            isRepair: true);
        return GameVerificationStartStatus.Started;
    }

    public GamePrimaryActionStatus TryExecutePrimaryAction()
    {
        GameAction action;
        bool retryRepair;
        bool startPlay;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
            {
                return GamePrimaryActionStatus.ShuttingDown;
            }

            if (_currentSnapshot.IsMaintenanceActive)
            {
                if (_activeLease?.CanUserCancel != true)
                {
                    return GamePrimaryActionStatus.Busy;
                }

                _operations.CancelFromUser();
                return GamePrimaryActionStatus.CancelRequested;
            }

            action = _currentSnapshot.RetryAction ?? _currentSnapshot.Action;
            retryRepair = _currentSnapshot.RetryOperationKind
                == LauncherOperationKind.GameRepair;
            startPlay = !retryRepair && action == GameAction.Play;
            if (!startPlay && _maintenanceService is null)
            {
                return GamePrimaryActionStatus.Unsupported;
            }

            if (!startPlay && !_isAuthenticated())
            {
                return GamePrimaryActionStatus.Unauthenticated;
            }
        }

        if (startPlay)
        {
            return TryStartPlay();
        }

        if (retryRepair)
        {
            return TryStartFullRepair() switch
            {
                GameVerificationStartStatus.Started => GamePrimaryActionStatus.Started,
                GameVerificationStartStatus.Unauthenticated => GamePrimaryActionStatus.Unauthenticated,
                GameVerificationStartStatus.ShuttingDown => GamePrimaryActionStatus.ShuttingDown,
                _ => GamePrimaryActionStatus.Busy
            };
        }

        LauncherOperationKind kind = action == GameAction.Install
            ? LauncherOperationKind.GameInstall
            : LauncherOperationKind.GameUpdate;
        LauncherOperationStartResult start = _operations.TryBegin(
            kind,
            canUserCancel: true,
            clientIsPlayable: action == GameAction.Update);
        if (!start.IsStarted)
        {
            return start.Status switch
            {
                LauncherOperationStartStatus.ShuttingDown =>
                    GamePrimaryActionStatus.ShuttingDown,
                _ => GamePrimaryActionStatus.Busy
            };
        }

        LauncherOperationLease lease = start.Lease!;
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        GameRuntimeSnapshot preparingSnapshot;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
            {
                lease.CancelForShutdown();
                lease.Complete();
                return GamePrimaryActionStatus.ShuttingDown;
            }

            _activeLease = lease;
            _activeMaintenanceAction = action;
            _lastProgressTimestamp = long.MinValue;
            _lastMaintenancePhase = null;
            preparingSnapshot = CreateMaintenanceSnapshotUnsafe(
                lease,
                action,
                GameClientMaintenancePhase.LoadingManifest,
                availableVersion: _currentSnapshot.AvailableVersion,
                currentFile: null,
                processedFileCount: null,
                totalFileCount: null,
                downloadedBytes: null,
                totalBytes: null,
                bytesPerSecond: null,
                remaining: null);
            _currentSnapshot = preparingSnapshot;
            _activeOperation = completion.Task;
        }

        Publish(preparingSnapshot, availabilityChanged: true);
        _ = RunMaintenanceAsync(lease, action, completion, isRepair: false);
        return GamePrimaryActionStatus.Started;
    }

    internal bool ResumePendingPlayAfterAuthentication()
    {
        LauncherOperationLease? lease;
        GameRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0
                || _operations.IsShuttingDown
                || _getSessionState() != LauncherSessionState.Authenticated
                || _activePlayLease is null
                || !_currentSnapshot.IsPlayPendingAuthentication
                || !_activePlayLease.IsCurrent)
            {
                return false;
            }

            lease = _activePlayLease;
            snapshot = CreatePlaySnapshotUnsafe(
                lease.OperationId,
                GameLaunchPhase.RequestingTicket,
                isPendingAuthentication: false,
                failureCategory: null,
                outcome: null);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
        _ = RunPlayAsync(lease);
        return true;
    }

    internal bool CancelPendingPlayAuthentication()
    {
        LauncherOperationLease? lease;
        lock (_sync)
        {
            if (_activePlayLease is null
                || !_currentSnapshot.IsPlayPendingAuthentication)
            {
                return false;
            }

            lease = _activePlayLease;
        }

        CompletePlayAttempt(
            lease,
            new GameLaunchResult(
                lease.OperationId,
                GameLaunchOutcome.Cancelled,
                GameLaunchFailureCategory.Cancelled));
        return true;
    }

    private GamePrimaryActionStatus TryStartPlay()
    {
        LauncherSessionState sessionState;
        bool isAuthenticated;
        bool isPlayable;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
            {
                return GamePrimaryActionStatus.ShuttingDown;
            }

            if (_launchService is null)
            {
                return GamePrimaryActionStatus.Unsupported;
            }

            isPlayable = _currentSnapshot.IsPlayable
                && _currentSnapshot.Action == GameAction.Play;
            if (!isPlayable)
            {
                return GamePrimaryActionStatus.Unsupported;
            }

            if (_activePlayLease is not null)
            {
                return GamePrimaryActionStatus.Busy;
            }

            sessionState = _getSessionState();
            isAuthenticated = _isAuthenticated();
            if (sessionState == LauncherSessionState.Restoring)
            {
                return GamePrimaryActionStatus.Busy;
            }
        }

        LauncherOperationStartResult start;
        Interlocked.Increment(ref _suppressOperationAvailabilityRefresh);
        try
        {
            start = _operations.TryBeginPlay(isPlayable);
        }
        finally
        {
            Interlocked.Decrement(ref _suppressOperationAvailabilityRefresh);
        }

        if (!start.IsStarted)
        {
            return start.Status switch
            {
                LauncherOperationStartStatus.ShuttingDown =>
                    GamePrimaryActionStatus.ShuttingDown,
                _ => GamePrimaryActionStatus.Busy
            };
        }

        LauncherOperationLease lease = start.Lease!;
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bool waitForAuthentication = sessionState is LauncherSessionState.SignedOut
            || !isAuthenticated && sessionState != LauncherSessionState.Unavailable;
        GameRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
            {
                lease.CancelForShutdown();
                lease.Complete();
                return GamePrimaryActionStatus.ShuttingDown;
            }

            _activePlayLease = lease;
            _activePlayCompletion = completion;
            _activePlayOperation = completion.Task;
            snapshot = CreatePlaySnapshotUnsafe(
                lease.OperationId,
                waitForAuthentication
                    ? GameLaunchPhase.WaitingForAuthentication
                    : GameLaunchPhase.RequestingTicket,
                waitForAuthentication,
                failureCategory: null,
                outcome: null);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
        if (waitForAuthentication)
        {
            RaisePlayAuthenticationRequired();
            return GamePrimaryActionStatus.Unauthenticated;
        }

        if (sessionState == LauncherSessionState.Unavailable && !isAuthenticated)
        {
            CompletePlayAttempt(
                lease,
                new GameLaunchResult(
                    lease.OperationId,
                    GameLaunchOutcome.NetworkUnavailable,
                    GameLaunchFailureCategory.Network));
            return GamePrimaryActionStatus.AuthenticationUnavailable;
        }

        _ = RunPlayAsync(lease);
        return GamePrimaryActionStatus.Started;
    }

    private async Task RunPlayAsync(LauncherOperationLease lease)
    {
        GameLaunchResult result;
        try
        {
            result = await _launchService!.LaunchAsync(
                new GameLaunchRequest(
                    lease.OperationId,
                    _settings.InstallPath,
                    _settings.GameLocale),
                progress => ReportPlayProgress(lease, progress),
                lease.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = new GameLaunchResult(
                lease.OperationId,
                GameLaunchOutcome.Unknown,
                GameLaunchFailureCategory.Unknown,
                exception);
        }

        if (result.AttemptId != lease.OperationId)
        {
            return;
        }

        if (result.Outcome == GameLaunchOutcome.AuthenticationRequired)
        {
            MovePlayToAuthenticationWait(lease, result);
            return;
        }

        CompletePlayAttempt(lease, result);
    }

    private void ReportPlayProgress(
        LauncherOperationLease lease,
        GameLaunchProgress progress)
    {
        if (progress.AttemptId != lease.OperationId)
        {
            return;
        }

        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentPlayUnsafe(lease)
                || progress.Phase is GameLaunchPhase.Idle
                    or GameLaunchPhase.WaitingForAuthentication
                    or GameLaunchPhase.Started
                    or GameLaunchPhase.Failed)
            {
                return;
            }

            snapshot = CreatePlaySnapshotUnsafe(
                lease.OperationId,
                progress.Phase,
                isPendingAuthentication: false,
                failureCategory: null,
                outcome: null);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private void MovePlayToAuthenticationWait(
        LauncherOperationLease lease,
        GameLaunchResult result)
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentPlayUnsafe(lease))
            {
                return;
            }

            snapshot = CreatePlaySnapshotUnsafe(
                lease.OperationId,
                GameLaunchPhase.WaitingForAuthentication,
                isPendingAuthentication: true,
                result.FailureCategory,
                result.Outcome);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
        RaisePlayAuthenticationRequired();
    }

    private void CompletePlayAttempt(
        LauncherOperationLease lease,
        GameLaunchResult result)
    {
        TaskCompletionSource? completion;
        lock (_sync)
        {
            if (!ReferenceEquals(_activePlayLease, lease)
                || _currentSnapshot.PlayAttemptId != lease.OperationId)
            {
                return;
            }

            _activePlayLease = null;
            completion = _activePlayCompletion;
            _activePlayCompletion = null;
        }

        Interlocked.Increment(ref _suppressOperationAvailabilityRefresh);
        try
        {
            lease.Complete();
        }
        finally
        {
            Interlocked.Decrement(ref _suppressOperationAvailabilityRefresh);
        }

        GameRuntimeSnapshot? snapshot = null;
        if (Volatile.Read(ref _disposeState) == 0 && !_operations.IsShuttingDown)
        {
            lock (_sync)
            {
                GameLaunchPhase phase = result.Outcome == GameLaunchOutcome.Cancelled
                    ? GameLaunchPhase.Idle
                    : result.Outcome is GameLaunchOutcome.Started
                        or GameLaunchOutcome.AlreadyRunning
                            ? GameLaunchPhase.Started
                            : GameLaunchPhase.Failed;
                snapshot = CreatePlaySnapshotUnsafe(
                    lease.OperationId,
                    phase,
                    isPendingAuthentication: false,
                    result.FailureCategory,
                    result.Outcome);
                _currentSnapshot = snapshot;
                snapshot = RecalculateAvailabilityUnsafe(snapshot);
                _currentSnapshot = snapshot;
            }
        }

        if (snapshot is not null)
        {
            Publish(snapshot, availabilityChanged: true);
        }

        if (result.Outcome is not (GameLaunchOutcome.Started
            or GameLaunchOutcome.AlreadyRunning
            or GameLaunchOutcome.Cancelled))
        {
            WritePlayFailureSafely(result);
        }

        completion?.TrySetResult();
        if (result.Outcome == GameLaunchOutcome.Started
            && Volatile.Read(ref _disposeState) == 0
            && !_operations.IsShuttingDown)
        {
            RaisePlayStarted();
        }
    }

    private GameRuntimeSnapshot CreatePlaySnapshotUnsafe(
        long attemptId,
        GameLaunchPhase phase,
        bool isPendingAuthentication,
        GameLaunchFailureCategory? failureCategory,
        GameLaunchOutcome? outcome)
    {
        string? unavailableReason = phase switch
        {
            GameLaunchPhase.WaitingForAuthentication => "Connexion requise",
            GameLaunchPhase.RequestingTicket => "Demande du ticket en cours",
            GameLaunchPhase.PreparingSso => "Préparation de la connexion en cours",
            GameLaunchPhase.StartingProcess => "Lancement du jeu en cours",
            _ => null
        };
        return _currentSnapshot with
        {
            Sequence = NextSequence(),
            PlayAttemptId = attemptId,
            PlayLaunchPhase = phase,
            IsPlayPendingAuthentication = isPendingAuthentication,
            PlayFailureCategory = failureCategory,
            LastPlayOutcome = outcome,
            CanPrimaryAction = false,
            PrimaryActionUnavailableReason = unavailableReason
        };
    }

    private bool IsCurrentPlayUnsafe(LauncherOperationLease lease)
    {
        return ReferenceEquals(_activePlayLease, lease)
            && _currentSnapshot.PlayAttemptId == lease.OperationId
            && lease.IsCurrent;
    }

    private void RaisePlayAuthenticationRequired()
    {
        try
        {
            PlayAuthenticationRequired?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Presentation cannot own the pending Play lease.
        }
    }

    private void RaisePlayStarted()
    {
        try
        {
            PlayStarted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Window lifecycle subscribers cannot alter a successful launch result.
        }
    }

    private async Task RunMaintenanceAsync(
        LauncherOperationLease lease,
        GameAction action,
        TaskCompletionSource completion,
        bool isRepair)
    {
        GameClientMaintenanceResult? result = null;
        Exception? failure = null;
        bool cancelled = false;
        bool repairDetectedChanges = false;
        GameAction terminalAction = action;
        try
        {
            GameClientMaintenanceRequest request = new(
                _settings.InstallPath,
                _settings.ManifestUrl,
                _settings.GameLocale);
            result = isRepair
                ? await _maintenanceService!.VerifyAndRepairAsync(
                    request,
                    lease,
                    progress => ReportMaintenanceProgress(lease, progress))
                : await _maintenanceService!.InstallOrUpdateAsync(
                    request,
                    lease,
                    progress => ReportMaintenanceProgress(lease, progress));
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            WriteMaintenanceFailureSafely(ex);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeLease, lease))
                {
                    terminalAction = _activeMaintenanceAction ?? action;
                    repairDetectedChanges = _repairDetectedChanges;
                    _activeLease = null;
                    _activeMaintenanceAction = null;
                    _repairDetectedChanges = false;
                }
            }

            Interlocked.Increment(ref _suppressOperationAvailabilityRefresh);
            try
            {
                lease.Complete();
            }
            finally
            {
                Interlocked.Decrement(ref _suppressOperationAvailabilityRefresh);
            }
        }

        try
        {
            if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
            {
                return;
            }

            if (result is not null)
            {
                CompleteMaintenanceSuccess(lease.OperationId, result);
            }
            else if (cancelled)
            {
                CompleteMaintenanceCancellation(
                    lease.OperationId,
                    terminalAction,
                    isRepair,
                    repairDetectedChanges);
            }
            else if (failure is not null)
            {
                CompleteMaintenanceFailure(
                    lease.OperationId,
                    terminalAction,
                    failure,
                    isRepair ? LauncherOperationKind.GameRepair : null);
            }
        }
        finally
        {
            completion.TrySetResult();
        }
    }

    private void ReportMaintenanceProgress(
        LauncherOperationLease lease,
        GameClientMaintenanceProgress progress)
    {
        if (progress.OperationId != lease.OperationId)
        {
            return;
        }

        if (progress.Phase is GameClientMaintenancePhase.CacheSaved
            or GameClientMaintenancePhase.Registering
            or GameClientMaintenancePhase.RegistrationCompleted
            or GameClientMaintenancePhase.Completed)
        {
            lease.DisableUserCancellation();
        }

        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentOperationUnsafe(lease)
                || !ShouldPublishMaintenanceProgressUnsafe(progress))
            {
                return;
            }

            if (lease.Kind == LauncherOperationKind.GameRepair
                && progress.Phase == GameClientMaintenancePhase.ComparisonCompleted)
            {
                _repairDetectedChanges = progress.MissingOrChangedFileCount > 0
                    || progress.RemovedFileCount > 0;
                _activeMaintenanceAction = !_currentSnapshot.IsPlayable
                    ? GameAction.Install
                    : _repairDetectedChanges
                        ? GameAction.Update
                        : GameAction.Play;
            }

            GameAction action = _activeMaintenanceAction ?? _currentSnapshot.Action;
            bool cleanupPhase = progress.Phase is GameClientMaintenancePhase.Cleaning
                or GameClientMaintenancePhase.CleanupCompleted;
            bool finalizationPhase = progress.Phase is GameClientMaintenancePhase.CacheSaved
                or GameClientMaintenancePhase.Registering
                or GameClientMaintenancePhase.RegistrationCompleted
                or GameClientMaintenancePhase.Completed;
            bool downloadStart = progress.Phase == GameClientMaintenancePhase.DownloadingStarted;
            int? processedFileCount = progress.ProcessedFileCount
                ?? (progress.Phase == GameClientMaintenancePhase.Cleaning
                    ? 0
                    : progress.Phase == GameClientMaintenancePhase.CleanupCompleted
                        ? progress.DeletedFileCount
                        : finalizationPhase
                            ? null
                            : _currentSnapshot.ProcessedFileCount);
            int? totalFileCount = progress.TotalFileCount
                ?? (cleanupPhase
                    ? progress.RemovedFileCount
                    : finalizationPhase
                        ? null
                        : _currentSnapshot.TotalFileCount);
            snapshot = CreateMaintenanceSnapshotUnsafe(
                lease,
                action,
                progress.Phase,
                progress.AvailableVersion ?? _currentSnapshot.AvailableVersion,
                finalizationPhase || cleanupPhase || downloadStart
                    ? progress.CurrentFile
                    : progress.CurrentFile ?? _currentSnapshot.CurrentFile,
                processedFileCount,
                totalFileCount,
                finalizationPhase || cleanupPhase
                    ? null
                    : downloadStart
                        ? 0
                        : progress.DownloadedBytes ?? _currentSnapshot.DownloadedBytes,
                finalizationPhase || cleanupPhase
                    ? null
                    : progress.TotalBytes ?? _currentSnapshot.TotalBytes,
                finalizationPhase || cleanupPhase || downloadStart
                    ? progress.BytesPerSecond
                    : progress.BytesPerSecond ?? _currentSnapshot.BytesPerSecond,
                finalizationPhase || cleanupPhase || downloadStart
                    ? progress.Remaining
                    : progress.Remaining ?? _currentSnapshot.Remaining);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private bool ShouldPublishMaintenanceProgressUnsafe(
        GameClientMaintenanceProgress progress)
    {
        bool phaseChanged = _lastMaintenancePhase != progress.Phase;
        if (phaseChanged)
        {
            _lastMaintenancePhase = progress.Phase;
            _lastProgressTimestamp = _timeProvider.GetTimestamp();
            return true;
        }

        bool terminalProgress = progress.DownloadedBytes is long downloaded
            && progress.TotalBytes is long total
            && total > 0
            && downloaded >= total;
        terminalProgress |= progress.ProcessedFileCount is int processed
            && progress.TotalFileCount is int fileTotal
            && fileTotal > 0
            && processed >= fileTotal;
        if (terminalProgress)
        {
            _lastProgressTimestamp = _timeProvider.GetTimestamp();
            return true;
        }

        if (progress.Phase is not (GameClientMaintenancePhase.Downloading
            or GameClientMaintenancePhase.ScanningFiles
            or GameClientMaintenancePhase.FullVerification
            or GameClientMaintenancePhase.RepairDownloading
            or GameClientMaintenancePhase.RepairApplying))
        {
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

    internal void RefreshAuthenticationAvailability()
    {
        GameRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            GameRuntimeSnapshot refreshed = RecalculateAvailabilityUnsafe(_currentSnapshot);
            if (refreshed.CanVerify != _currentSnapshot.CanVerify
                || refreshed.CanPrimaryAction != _currentSnapshot.CanPrimaryAction
                || refreshed.CanUserCancel != _currentSnapshot.CanUserCancel
                || !string.Equals(
                    refreshed.PrimaryActionUnavailableReason,
                    _currentSnapshot.PrimaryActionUnavailableReason,
                    StringComparison.Ordinal))
            {
                snapshot = refreshed with { Sequence = NextSequence() };
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
        LauncherOperationLease? pendingLease = null;
        TaskCompletionSource? pendingCompletion = null;
        lock (_sync)
        {
            if (_activePlayLease is not null
                && _currentSnapshot.IsPlayPendingAuthentication)
            {
                pendingLease = _activePlayLease;
                pendingCompletion = _activePlayCompletion;
                _activePlayLease = null;
                _activePlayCompletion = null;
            }
        }

        pendingLease?.CancelForShutdown();
        pendingLease?.Complete();
        pendingCompletion?.TrySetResult();
        _operations.CancelForShutdown();
    }

    internal Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return Task.WhenAll(_activeOperation, _activePlayOperation);
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

    private GameRuntimeSnapshot CreateMaintenanceSnapshotUnsafe(
        LauncherOperationLease lease,
        GameAction action,
        GameClientMaintenancePhase phase,
        string? availableVersion,
        string? currentFile,
        int? processedFileCount,
        int? totalFileCount,
        long? downloadedBytes,
        long? totalBytes,
        double? bytesPerSecond,
        TimeSpan? remaining)
    {
        GameRuntimeSnapshot snapshot = new(
            Sequence: NextSequence(),
            OperationId: lease.OperationId,
            Action: action,
            UpdateKnowledge: lease.Kind == LauncherOperationKind.GameRepair
                && phase == GameClientMaintenancePhase.ComparisonCompleted
                    ? GameUpdateKnowledge.Known
                    : _currentSnapshot.UpdateKnowledge,
            Phase: GameVerificationPhase.Stable,
            IsVerifying: false,
            CanVerify: false,
            IsPlayable: _currentSnapshot.IsPlayable,
            InstallPath: _settings.InstallPath,
            InstalledVersion: _installedVersion,
            AvailableVersion: availableVersion,
            ProcessedFileCount: processedFileCount,
            TotalFileCount: totalFileCount,
            FailureCategory: null,
            GameLocale: _settings.GameLocale,
            OperationKind: lease.Kind,
            MaintenancePhase: phase,
            CanPrimaryAction: lease.CanUserCancel,
            CanUserCancel: lease.CanUserCancel,
            DownloadedBytes: downloadedBytes,
            TotalBytes: totalBytes,
            BytesPerSecond: bytesPerSecond,
            Remaining: remaining,
            CurrentFile: currentFile,
            ErrorCategory: null,
            ErrorTitle: null,
            ErrorSummary: null,
            RetryAction: null,
            PrimaryActionUnavailableReason: lease.CanUserCancel
                ? null
                : "Finalisation en cours");
        return snapshot;
    }

    private void CompleteMaintenanceSuccess(
        long operationId,
        GameClientMaintenanceResult result)
    {
        GameClientLocalState local = ReadLocalStateSafely();
        GameRuntimeSnapshot snapshot;
        lock (_sync)
        {
            _installedVersion = local.InstalledVersion ?? result.AvailableVersion;
            snapshot = new GameRuntimeSnapshot(
                Sequence: NextSequence(),
                OperationId: operationId,
                Action: GameAction.Play,
                UpdateKnowledge: GameUpdateKnowledge.Known,
                Phase: GameVerificationPhase.Stable,
                IsVerifying: false,
                CanVerify: false,
                IsPlayable: true,
                InstallPath: _settings.InstallPath,
                InstalledVersion: _installedVersion,
                AvailableVersion: result.AvailableVersion,
                ProcessedFileCount: null,
                TotalFileCount: null,
                FailureCategory: null,
                GameLocale: _settings.GameLocale);
            _currentSnapshot = snapshot;
            snapshot = RecalculateAvailabilityUnsafe(snapshot);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private void CompleteMaintenanceCancellation(
        long operationId,
        GameAction action,
        bool isRepair,
        bool repairDetectedChanges)
    {
        GameClientLocalState local = ReadLocalStateSafely();
        GameRuntimeSnapshot snapshot;
        lock (_sync)
        {
            _installedVersion = local.InstalledVersion;
            GameAction resolvedAction = isRepair
                ? !local.IsPlayable
                    ? GameAction.Install
                    : repairDetectedChanges
                        ? GameAction.Update
                        : GameAction.Play
                : action;
            snapshot = new GameRuntimeSnapshot(
                Sequence: NextSequence(),
                OperationId: operationId,
                Action: resolvedAction,
                UpdateKnowledge: isRepair && repairDetectedChanges
                    ? GameUpdateKnowledge.Known
                    : _currentSnapshot.UpdateKnowledge,
                Phase: GameVerificationPhase.Stable,
                IsVerifying: false,
                CanVerify: false,
                IsPlayable: local.IsPlayable,
                InstallPath: _settings.InstallPath,
                InstalledVersion: _installedVersion,
                AvailableVersion: _currentSnapshot.AvailableVersion,
                ProcessedFileCount: null,
                TotalFileCount: null,
                FailureCategory: null,
                GameLocale: _settings.GameLocale);
            _currentSnapshot = snapshot;
            snapshot = RecalculateAvailabilityUnsafe(snapshot);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private void CompleteMaintenanceFailure(
        long operationId,
        GameAction action,
        Exception exception,
        LauncherOperationKind? retryOperationKind)
    {
        GameClientLocalState local = ReadLocalStateSafely();
        GameRuntimeErrorCategory category = ClassifyMaintenanceFailure(exception);
        GameRuntimeSnapshot snapshot;
        lock (_sync)
        {
            _installedVersion = local.InstalledVersion;
            snapshot = new GameRuntimeSnapshot(
                Sequence: NextSequence(),
                OperationId: operationId,
                Action: action,
                UpdateKnowledge: _currentSnapshot.UpdateKnowledge,
                Phase: GameVerificationPhase.Stable,
                IsVerifying: false,
                CanVerify: false,
                IsPlayable: local.IsPlayable,
                InstallPath: _settings.InstallPath,
                InstalledVersion: _installedVersion,
                AvailableVersion: _currentSnapshot.AvailableVersion,
                ProcessedFileCount: null,
                TotalFileCount: null,
                FailureCategory: exception.GetType().Name,
                GameLocale: _settings.GameLocale,
                ErrorCategory: category,
                ErrorTitle: retryOperationKind == LauncherOperationKind.GameRepair
                    ? "Réparation interrompue"
                    : action == GameAction.Install
                        ? "Installation interrompue"
                        : "Mise à jour interrompue",
                ErrorSummary: GetUserFacingFailureSummary(category),
                RetryAction: action,
                RetryOperationKind: retryOperationKind);
            _currentSnapshot = snapshot;
            snapshot = RecalculateAvailabilityUnsafe(snapshot);
            _currentSnapshot = snapshot;
        }

        Publish(snapshot, availabilityChanged: true);
    }

    private GameClientLocalState ReadLocalStateSafely()
    {
        try
        {
            return _readLocalState();
        }
        catch (Exception ex)
        {
            try
            {
                _writeLog($"Relecture locale impossible: {ex.GetType().Name}.");
            }
            catch
            {
            }

            lock (_sync)
            {
                return new GameClientLocalState(
                    _settings.InstallPath,
                    _settings.GameLocale,
                    _currentSnapshot.IsPlayable,
                    _installedVersion,
                    _currentSnapshot.UpdateKnowledge);
            }
        }
    }

    private static GameRuntimeErrorCategory ClassifyMaintenanceFailure(Exception exception)
    {
        if (exception is TaskCanceledException)
        {
            return GameRuntimeErrorCategory.Network;
        }

        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode is HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden
                ? GameRuntimeErrorCategory.Unauthorized
                : GameRuntimeErrorCategory.Network;
        }

        if (exception is UnauthorizedAccessException)
        {
            return GameRuntimeErrorCategory.Permission;
        }

        if (exception is InvalidDataException)
        {
            return GameRuntimeErrorCategory.Integrity;
        }

        if (exception is IOException ioException)
        {
            return ioException.Message.Contains("verrou", StringComparison.OrdinalIgnoreCase)
                || ioException.Message.Contains("Ferme le jeu", StringComparison.OrdinalIgnoreCase)
                || ioException.Message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
                ? GameRuntimeErrorCategory.LockedFile
                : GameRuntimeErrorCategory.Disk;
        }

        if (exception is InvalidOperationException invalidOperation
            && (invalidOperation.Message.Contains("Hash invalide", StringComparison.OrdinalIgnoreCase)
                || invalidOperation.Message.Contains("Taille invalide", StringComparison.OrdinalIgnoreCase)))
        {
            return GameRuntimeErrorCategory.Integrity;
        }

        return exception is InvalidOperationException
            ? GameRuntimeErrorCategory.Platform
            : GameRuntimeErrorCategory.Unknown;
    }

    private static string GetUserFacingFailureSummary(GameRuntimeErrorCategory category)
    {
        return category switch
        {
            GameRuntimeErrorCategory.Network =>
                "Atlas est temporairement indisponible. Vérifie ta connexion puis réessaie.",
            GameRuntimeErrorCategory.Unauthorized =>
                "Ta session n’est plus valide. Une reconnexion sera nécessaire.",
            GameRuntimeErrorCategory.Permission =>
                "Atlas n’a pas l’autorisation d’écrire dans le dossier du jeu.",
            GameRuntimeErrorCategory.LockedFile =>
                "Un fichier du jeu est utilisé. Ferme WoW puis réessaie.",
            GameRuntimeErrorCategory.Disk =>
                "Le disque n’a pas pu terminer l’opération. Vérifie l’espace disponible.",
            GameRuntimeErrorCategory.Integrity =>
                "Un fichier téléchargé est invalide. Relance la mise à jour.",
            GameRuntimeErrorCategory.Platform =>
                "Windows n’a pas pu finaliser l’installation.",
            _ => "L’opération n’a pas pu être terminée. Consulte le diagnostic."
        };
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
        bool preserveActivePlay = _currentSnapshot.IsPlayActive;
        GameRuntimeSnapshot snapshot = new(
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
            FailureCategory: failureCategory,
            GameLocale: _settings.GameLocale,
            OperationKind: isVerifying ? LauncherOperationKind.Verify : null,
            CanPrimaryAction: false,
            CanUserCancel: false,
            PrimaryActionUnavailableReason: isVerifying
                ? "Vérification en cours"
                : null,
            PlayAttemptId: preserveActivePlay
                ? _currentSnapshot.PlayAttemptId
                : null,
            PlayLaunchPhase: preserveActivePlay
                ? _currentSnapshot.PlayLaunchPhase
                : GameLaunchPhase.Idle,
            IsPlayPendingAuthentication: preserveActivePlay
                && _currentSnapshot.IsPlayPendingAuthentication,
            PlayFailureCategory: preserveActivePlay
                ? _currentSnapshot.PlayFailureCategory
                : null,
            LastPlayOutcome: preserveActivePlay
                ? _currentSnapshot.LastPlayOutcome
                : null);
        return RecalculateAvailabilityUnsafe(snapshot);
    }

    private bool CanVerifyUnsafe()
    {
        return Volatile.Read(ref _disposeState) == 0
            && _isAuthenticated()
            && _maintenanceService is not null
            && IsRepairPathValidUnsafe()
            && _operations.CanBegin(
                LauncherOperationKind.GameRepair,
                _currentSnapshot.IsPlayable);
    }

    private bool CanExecutePrimaryActionUnsafe()
    {
        if (Volatile.Read(ref _disposeState) != 0 || _operations.IsShuttingDown)
        {
            return false;
        }

        if (_currentSnapshot.IsMaintenanceActive)
        {
            return _activeLease?.CanUserCancel == true;
        }

        if (_currentSnapshot.IsPlayActive || _activePlayLease is not null)
        {
            return false;
        }

        bool retryRepair = _currentSnapshot.RetryOperationKind
            == LauncherOperationKind.GameRepair;
        GameAction action = _currentSnapshot.RetryAction ?? _currentSnapshot.Action;
        if (!retryRepair && action == GameAction.Play)
        {
            return _launchService is not null
                && _currentSnapshot.IsPlayable
                && _getSessionState() != LauncherSessionState.Restoring
                && _operations.CanBeginPlay(clientIsPlayable: true);
        }

        if (_maintenanceService is null || !_isAuthenticated())
        {
            return false;
        }

        if (retryRepair)
        {
            return IsRepairPathValidUnsafe()
                && _operations.CanBegin(
                    LauncherOperationKind.GameRepair,
                    _currentSnapshot.IsPlayable);
        }

        LauncherOperationKind kind = action == GameAction.Install
            ? LauncherOperationKind.GameInstall
            : LauncherOperationKind.GameUpdate;
        return _operations.CanBegin(kind, _currentSnapshot.IsPlayable);
    }

    private GameRuntimeSnapshot RecalculateAvailabilityUnsafe(GameRuntimeSnapshot snapshot)
    {
        bool canVerify = CanVerifyUnsafe();
        bool canPrimaryAction = CanExecutePrimaryActionUnsafe();
        bool canUserCancel = snapshot.IsMaintenanceActive
            && _activeLease?.CanUserCancel == true;
        string? unavailableReason = null;
        bool retryRepair = snapshot.RetryOperationKind
            == LauncherOperationKind.GameRepair;
        GameAction action = snapshot.RetryAction ?? snapshot.Action;

        if (snapshot.IsFinalizing)
        {
            unavailableReason = "Finalisation en cours";
        }
        else if (snapshot.IsPlayActive)
        {
            unavailableReason = snapshot.PlayLaunchPhase switch
            {
                GameLaunchPhase.WaitingForAuthentication => "Connexion requise",
                GameLaunchPhase.RequestingTicket => "Demande du ticket en cours",
                GameLaunchPhase.PreparingSso => "Préparation de la connexion en cours",
                GameLaunchPhase.StartingProcess => "Lancement du jeu en cours",
                _ => "Lancement en cours"
            };
        }
        else if (snapshot.IsVerifying)
        {
            unavailableReason = "Vérification en cours";
        }
        else if (retryRepair && !IsRepairPathValidUnsafe())
        {
            unavailableReason = "Client local indisponible";
        }
        else if (!retryRepair && action == GameAction.Play && _launchService is null)
        {
            unavailableReason = "Le lancement sera reconnecté ultérieurement";
        }
        else if (!retryRepair
                 && action == GameAction.Play
                 && _getSessionState() == LauncherSessionState.Restoring)
        {
            unavailableReason = "Restauration de la session en cours";
        }
        else if (_maintenanceService is null)
        {
            unavailableReason = "Maintenance indisponible";
        }
        else if (!_isAuthenticated())
        {
            unavailableReason = "Connexion requise";
        }
        else if (!canPrimaryAction && !canUserCancel)
        {
            unavailableReason = "Une autre opération est en cours";
        }

        return snapshot with
        {
            CanVerify = canVerify,
            CanPrimaryAction = canPrimaryAction,
            CanUserCancel = canUserCancel,
            PrimaryActionUnavailableReason = unavailableReason
        };
    }

    private bool IsRepairPathValidUnsafe()
    {
        if (!_currentSnapshot.IsPlayable
            || string.IsNullOrWhiteSpace(_settings.InstallPath)
            || !Path.IsPathFullyQualified(_settings.InstallPath))
        {
            return false;
        }

        try
        {
            return Directory.Exists(Path.GetFullPath(_settings.InstallPath));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
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
        if (Volatile.Read(ref _disposeState) == 0
            && Volatile.Read(ref _suppressOperationAvailabilityRefresh) == 0
            && !_operations.IsShuttingDown)
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
        PlayAuthenticationRequired = null;
        PlayStarted = null;
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

    private void WriteMaintenanceFailureSafely(Exception exception)
    {
        try
        {
            GameRuntimeErrorCategory category = ClassifyMaintenanceFailure(exception);
            _writeLog(
                $"Maintenance client échouée: {category} "
                + $"({exception.GetType().Name}, HRESULT=0x{exception.HResult:X8}).");
        }
        catch
        {
            // Diagnostics must not replace the original maintenance result.
        }
    }

    private void WritePlayFailureSafely(GameLaunchResult result)
    {
        try
        {
            _writeLog(
                $"Lancement du jeu échoué: attempt={result.AttemptId}; "
                + $"outcome={result.Outcome}; category={result.FailureCategory}; "
                + $"exception={result.Failure?.GetType().Name ?? "none"}.");
        }
        catch
        {
            // Logging cannot replace the launch result or trigger another attempt.
        }
    }
}
