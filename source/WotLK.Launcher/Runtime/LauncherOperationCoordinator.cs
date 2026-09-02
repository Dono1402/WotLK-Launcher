namespace WotLK.Launcher.Runtime;

internal enum LauncherOperationKind
{
    Verify,
    GameRepair,
    GameInstall,
    GameUpdate,
    Addons,
    LauncherAutoUpdate,
    AvatarUpload,
    AvatarDelete,
    AccountEmailChange,
    AccountEmailVerification,
    AccountPasswordChange,
    AccountSessionRevoke,
    Logout,
    Play
}

internal enum LauncherOperationStartStatus
{
    Started,
    Busy,
    ShuttingDown,
    RejectedByCompatibility
}

internal readonly record struct LauncherOperationStartResult(
    LauncherOperationStartStatus Status,
    LauncherOperationLease? Lease)
{
    internal bool IsStarted => Status == LauncherOperationStartStatus.Started
        && Lease is not null;
}

internal sealed class LauncherOperationCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private LauncherOperationLease? _maintenanceLease;
    private LauncherOperationLease? _playLease;
    private long _nextOperationId;
    private long _activitySequence;
    private LauncherOperationActivitySnapshot _activitySnapshot =
        LauncherOperationActivitySnapshot.Initial;
    private bool _isShuttingDown;
    private int _disposeState;
    private int _shutdownCancellationDisposeState;

    internal event EventHandler? StateChanged;

    internal event EventHandler<LauncherOperationActivitySnapshotEventArgs>? ActivityChanged;

    internal CancellationToken ShutdownToken => _shutdownCancellation.Token;

    internal LauncherOperationActivitySnapshot CurrentActivitySnapshot
    {
        get
        {
            lock (_sync)
            {
                return _activitySnapshot;
            }
        }
    }

    internal bool IsShuttingDown
    {
        get
        {
            lock (_sync)
            {
                return _isShuttingDown;
            }
        }
    }

    internal bool IsIdle
    {
        get
        {
            lock (_sync)
            {
                return _maintenanceLease is null && _playLease is null;
            }
        }
    }

    internal bool HasActiveUserCancellableOperation
    {
        get
        {
            lock (_sync)
            {
                return _maintenanceLease?.CanUserCancel == true;
            }
        }
    }

    internal LauncherOperationKind? ActiveMaintenanceKind
    {
        get
        {
            lock (_sync)
            {
                return _maintenanceLease?.Kind;
            }
        }
    }

    internal bool CanBegin(
        LauncherOperationKind kind,
        bool clientIsPlayable = false)
    {
        lock (_sync)
        {
            return GetMaintenanceStartStatusUnsafe(kind, clientIsPlayable)
                == LauncherOperationStartStatus.Started;
        }
    }

    internal LauncherOperationStartResult TryBegin(
        LauncherOperationKind kind,
        bool canUserCancel,
        bool clientIsPlayable = false,
        LauncherOperationType? operationType = null)
    {
        LauncherOperationLease? lease = null;
        LauncherOperationActivitySnapshot? activitySnapshot = null;
        LauncherOperationStartStatus status;
        lock (_sync)
        {
            status = GetMaintenanceStartStatusUnsafe(kind, clientIsPlayable);
            LauncherOperationType resolvedType = operationType ?? ToDefaultOperationType(kind);
            if (status == LauncherOperationStartStatus.Started
                && !IsOperationTypeCompatible(kind, resolvedType))
            {
                status = LauncherOperationStartStatus.RejectedByCompatibility;
            }
            if (status == LauncherOperationStartStatus.Started)
            {
                lease = CreateLeaseUnsafe(
                    kind,
                    resolvedType,
                    canUserCancel,
                    isPlayLease: false);
                _maintenanceLease = lease;
                activitySnapshot = CaptureActivitySnapshotUnsafe();
            }
        }

        if (lease is not null)
        {
            RaiseStateChanged(activitySnapshot);
        }

        return new LauncherOperationStartResult(status, lease);
    }

    internal LauncherOperationStartResult TryBeginPlay(bool clientIsPlayable)
    {
        LauncherOperationLease? lease = null;
        LauncherOperationStartStatus status;
        lock (_sync)
        {
            status = GetPlayStartStatusUnsafe(clientIsPlayable);
            if (status == LauncherOperationStartStatus.Started)
            {
                lease = CreateLeaseUnsafe(
                    LauncherOperationKind.Play,
                    LauncherOperationType.Play,
                    canUserCancel: false,
                    isPlayLease: true);
                _playLease = lease;
            }
        }

        if (lease is not null)
        {
            RaiseStateChanged(activitySnapshot: null);
        }

        return new LauncherOperationStartResult(status, lease);
    }

    internal bool CanBeginPlay(bool clientIsPlayable)
    {
        lock (_sync)
        {
            return GetPlayStartStatusUnsafe(clientIsPlayable)
                == LauncherOperationStartStatus.Started;
        }
    }

    internal bool CancelFromUser()
    {
        LauncherOperationLease? lease;
        lock (_sync)
        {
            lease = _maintenanceLease;
        }

        return lease?.CancelFromUser() == true;
    }

    internal bool CancelForShutdown()
    {
        LauncherOperationLease? maintenanceLease;
        LauncherOperationLease? playLease;
        bool changed;
        LauncherOperationActivitySnapshot? activitySnapshot = null;
        lock (_sync)
        {
            changed = !_isShuttingDown;
            _isShuttingDown = true;
            maintenanceLease = _maintenanceLease;
            playLease = _playLease;
        }

        if (changed)
        {
            try
            {
                _shutdownCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            maintenanceLease?.CancelForShutdown();
            playLease?.CancelForShutdown();
            lock (_sync)
            {
                activitySnapshot = CaptureActivitySnapshotUnsafe();
            }
            RaiseStateChanged(activitySnapshot);
        }

        return changed && (maintenanceLease is not null || playLease is not null);
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        DateTimeOffset deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTimeOffset.MaxValue
            : DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            Task[] activeTasks;
            lock (_sync)
            {
                activeTasks = GetActiveCompletionTasksUnsafe();
            }

            if (activeTasks.Length == 0)
            {
                return true;
            }

            TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await Task.WhenAll(activeTasks).WaitAsync(remaining).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        CancelForShutdown();
        TryDisposeShutdownCancellation();
    }

    internal bool IsCurrent(LauncherOperationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            return !_isShuttingDown && IsCurrentUnsafe(lease);
        }
    }

    internal bool TryInvoke(LauncherOperationLease lease, Action callback)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(callback);
        lock (_sync)
        {
            if (_isShuttingDown || !IsCurrentUnsafe(lease))
            {
                return false;
            }

            callback();
            return true;
        }
    }

    internal bool CancelLeaseFromUser(LauncherOperationLease lease)
    {
        LauncherOperationActivitySnapshot? activitySnapshot = null;
        bool cancelled;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(lease) || !lease.CanUserCancel)
            {
                return false;
            }

            cancelled = lease.RequestCancellation(LauncherOperationCancellationReason.User);
            if (cancelled && !lease.IsPlayLease)
            {
                activitySnapshot = CaptureActivitySnapshotUnsafe();
            }
        }

        if (activitySnapshot is not null)
        {
            RaiseActivityChanged(activitySnapshot);
        }
        return cancelled;
    }

    internal bool CancelLeaseForShutdown(LauncherOperationLease lease)
    {
        lock (_sync)
        {
            if (!IsCurrentUnsafe(lease))
            {
                return false;
            }

            return lease.RequestCancellation(LauncherOperationCancellationReason.Shutdown);
        }
    }

    internal void NotifyLeaseCancellationAvailabilityChanged(LauncherOperationLease lease)
    {
        LauncherOperationActivitySnapshot? activitySnapshot = null;
        lock (_sync)
        {
            if (!lease.IsPlayLease && IsCurrentUnsafe(lease))
            {
                activitySnapshot = CaptureActivitySnapshotUnsafe();
            }
        }

        if (activitySnapshot is not null)
        {
            RaiseActivityChanged(activitySnapshot);
        }
    }

    internal void Release(LauncherOperationLease lease)
    {
        bool released = false;
        LauncherOperationActivitySnapshot? activitySnapshot = null;
        lock (_sync)
        {
            if (lease.IsPlayLease)
            {
                if (ReferenceEquals(_playLease, lease)
                    && _playLease.OperationId == lease.OperationId)
                {
                    _playLease = null;
                    released = true;
                }
            }
            else if (ReferenceEquals(_maintenanceLease, lease)
                     && _maintenanceLease.OperationId == lease.OperationId)
            {
                _maintenanceLease = null;
                released = true;
                activitySnapshot = CaptureActivitySnapshotUnsafe();
            }
        }

        if (released)
        {
            RaiseStateChanged(activitySnapshot);
            TryDisposeShutdownCancellation();
        }
    }

    private LauncherOperationStartStatus GetMaintenanceStartStatusUnsafe(
        LauncherOperationKind kind,
        bool clientIsPlayable)
    {
        if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
        {
            return LauncherOperationStartStatus.ShuttingDown;
        }

        if (kind == LauncherOperationKind.Play)
        {
            return LauncherOperationStartStatus.RejectedByCompatibility;
        }

        if (_maintenanceLease is not null)
        {
            return LauncherOperationStartStatus.Busy;
        }

        if (_playLease is not null
            && (kind != LauncherOperationKind.Verify || !clientIsPlayable))
        {
            return LauncherOperationStartStatus.RejectedByCompatibility;
        }

        return LauncherOperationStartStatus.Started;
    }

    private LauncherOperationStartStatus GetPlayStartStatusUnsafe(bool clientIsPlayable)
    {
        if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
        {
            return LauncherOperationStartStatus.ShuttingDown;
        }

        if (!clientIsPlayable)
        {
            return LauncherOperationStartStatus.RejectedByCompatibility;
        }

        if (_playLease is not null)
        {
            return LauncherOperationStartStatus.Busy;
        }

        return _maintenanceLease is not null
               && _maintenanceLease.Kind != LauncherOperationKind.Verify
            ? LauncherOperationStartStatus.RejectedByCompatibility
            : LauncherOperationStartStatus.Started;
    }

    private LauncherOperationLease CreateLeaseUnsafe(
        LauncherOperationKind kind,
        LauncherOperationType operationType,
        bool canUserCancel,
        bool isPlayLease)
    {
        return new LauncherOperationLease(
            this,
            ++_nextOperationId,
            kind,
            operationType,
            canUserCancel,
            isPlayLease,
            CancellationTokenSource.CreateLinkedTokenSource(_shutdownCancellation.Token));
    }

    private bool IsCurrentUnsafe(LauncherOperationLease lease)
    {
        LauncherOperationLease? current = lease.IsPlayLease
            ? _playLease
            : _maintenanceLease;
        return ReferenceEquals(current, lease)
            && current.OperationId == lease.OperationId;
    }

    private Task[] GetActiveCompletionTasksUnsafe()
    {
        if (_maintenanceLease is null && _playLease is null)
        {
            return [];
        }

        if (_maintenanceLease is not null && _playLease is not null)
        {
            return [_maintenanceLease.Completion, _playLease.Completion];
        }

        return [_maintenanceLease?.Completion ?? _playLease!.Completion];
    }

    private LauncherOperationActivitySnapshot CaptureActivitySnapshotUnsafe()
    {
        _activitySnapshot = new LauncherOperationActivitySnapshot(
            Sequence: ++_activitySequence,
            OperationId: _maintenanceLease?.OperationId,
            OperationType: _maintenanceLease?.OperationType,
            IsActive: _maintenanceLease is not null,
            CanUserCancel: _maintenanceLease?.CanUserCancel == true,
            IsShuttingDown: _isShuttingDown);
        return _activitySnapshot;
    }

    private void RaiseStateChanged(LauncherOperationActivitySnapshot? activitySnapshot)
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Operation ownership must not depend on presentation subscribers.
        }

        RaiseActivityChanged(activitySnapshot);
    }

    private void RaiseActivityChanged(LauncherOperationActivitySnapshot? activitySnapshot)
    {
        if (activitySnapshot is null)
        {
            return;
        }

        try
        {
            ActivityChanged?.Invoke(
                this,
                new LauncherOperationActivitySnapshotEventArgs(activitySnapshot));
        }
        catch
        {
            // Activity observers must not affect operation ownership.
        }
    }

    private static LauncherOperationType ToDefaultOperationType(LauncherOperationKind kind) =>
        kind switch
        {
            LauncherOperationKind.Verify => LauncherOperationType.GameVerify,
            LauncherOperationKind.GameRepair => LauncherOperationType.GameRepair,
            LauncherOperationKind.GameInstall => LauncherOperationType.GameInstall,
            LauncherOperationKind.GameUpdate => LauncherOperationType.GameUpdate,
            LauncherOperationKind.Addons => LauncherOperationType.AddonSynchronization,
            LauncherOperationKind.LauncherAutoUpdate => LauncherOperationType.LauncherAutoUpdate,
            LauncherOperationKind.AvatarUpload => LauncherOperationType.AvatarUpload,
            LauncherOperationKind.AvatarDelete => LauncherOperationType.AvatarDelete,
            LauncherOperationKind.AccountEmailChange => LauncherOperationType.AccountEmailChange,
            LauncherOperationKind.AccountEmailVerification => LauncherOperationType.AccountEmailVerification,
            LauncherOperationKind.AccountPasswordChange => LauncherOperationType.AccountPasswordChange,
            LauncherOperationKind.AccountSessionRevoke => LauncherOperationType.AccountSessionRevoke,
            LauncherOperationKind.Logout => LauncherOperationType.Logout,
            LauncherOperationKind.Play => LauncherOperationType.Play,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static bool IsOperationTypeCompatible(
        LauncherOperationKind kind,
        LauncherOperationType operationType) => kind switch
        {
            LauncherOperationKind.Verify => operationType == LauncherOperationType.GameVerify,
            LauncherOperationKind.GameRepair => operationType == LauncherOperationType.GameRepair,
            LauncherOperationKind.GameInstall => operationType == LauncherOperationType.GameInstall,
            LauncherOperationKind.GameUpdate => operationType == LauncherOperationType.GameUpdate,
            LauncherOperationKind.Addons => operationType is
                LauncherOperationType.AddonInstall
                or LauncherOperationType.AddonUpdate
                or LauncherOperationType.AddonRepair
                or LauncherOperationType.AddonRemove
                or LauncherOperationType.AddonBatchUpdate
                or LauncherOperationType.AddonSynchronization,
            LauncherOperationKind.LauncherAutoUpdate =>
                operationType == LauncherOperationType.LauncherAutoUpdate,
            LauncherOperationKind.AvatarUpload => operationType == LauncherOperationType.AvatarUpload,
            LauncherOperationKind.AvatarDelete => operationType == LauncherOperationType.AvatarDelete,
            LauncherOperationKind.AccountEmailChange =>
                operationType == LauncherOperationType.AccountEmailChange,
            LauncherOperationKind.AccountEmailVerification =>
                operationType == LauncherOperationType.AccountEmailVerification,
            LauncherOperationKind.AccountPasswordChange =>
                operationType == LauncherOperationType.AccountPasswordChange,
            LauncherOperationKind.AccountSessionRevoke =>
                operationType == LauncherOperationType.AccountSessionRevoke,
            LauncherOperationKind.Logout => operationType == LauncherOperationType.Logout,
            LauncherOperationKind.Play => operationType == LauncherOperationType.Play,
            _ => false
        };

    private void TryDisposeShutdownCancellation()
    {
        if (Volatile.Read(ref _disposeState) == 0 || !IsIdle)
        {
            return;
        }

        if (Interlocked.Exchange(ref _shutdownCancellationDisposeState, 1) == 0)
        {
            _shutdownCancellation.Dispose();
        }
    }
}

internal sealed class LauncherOperationLease : IDisposable
{
    private readonly LauncherOperationCoordinator _owner;
    private readonly CancellationTokenSource _cancellation;
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completionState;
    private int _cancellationState;
    private int _userCancellationDisabled;
    private readonly bool _initiallyUserCancellable;

    internal LauncherOperationLease(
        LauncherOperationCoordinator owner,
        long operationId,
        LauncherOperationKind kind,
        LauncherOperationType operationType,
        bool canUserCancel,
        bool isPlayLease,
        CancellationTokenSource cancellation)
    {
        _owner = owner;
        OperationId = operationId;
        Kind = kind;
        OperationType = operationType;
        _initiallyUserCancellable = canUserCancel;
        IsPlayLease = isPlayLease;
        _cancellation = cancellation;
    }

    internal long OperationId { get; }

    internal LauncherOperationKind Kind { get; }

    internal LauncherOperationType OperationType { get; }

    internal bool CanUserCancel => _initiallyUserCancellable
        && Volatile.Read(ref _completionState) == 0
        && Volatile.Read(ref _userCancellationDisabled) == 0
        && Volatile.Read(ref _cancellationState) == 0;

    internal LauncherOperationCancellationReason CancellationReason =>
        (LauncherOperationCancellationReason)Volatile.Read(ref _cancellationState);

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal bool IsCurrent => Volatile.Read(ref _completionState) == 0
        && _owner.IsCurrent(this);

    internal bool IsPlayLease { get; }

    internal Task Completion => _completion.Task;

    internal bool CancelFromUser()
    {
        return Volatile.Read(ref _completionState) == 0
            && _owner.CancelLeaseFromUser(this);
    }

    internal bool CancelForShutdown()
    {
        return Volatile.Read(ref _completionState) == 0
            && _owner.CancelLeaseForShutdown(this);
    }

    internal bool DisableUserCancellation()
    {
        bool changed = _initiallyUserCancellable
            && Volatile.Read(ref _completionState) == 0
            && Interlocked.Exchange(ref _userCancellationDisabled, 1) == 0;
        if (changed)
        {
            _owner.NotifyLeaseCancellationAvailabilityChanged(this);
        }

        return changed;
    }

    internal bool TryInvoke(Action callback)
    {
        return Volatile.Read(ref _completionState) == 0
            && _owner.TryInvoke(this, callback);
    }

    internal bool TryInvoke(long operationId, Action callback)
    {
        return operationId == OperationId && TryInvoke(callback);
    }

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completionState, 1) != 0)
        {
            return;
        }

        _owner.Release(this);
        _completion.TrySetResult();
        _cancellation.Dispose();
    }

    public void Dispose()
    {
        Complete();
    }

    internal bool RequestCancellation(LauncherOperationCancellationReason reason)
    {
        if (reason == LauncherOperationCancellationReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (Interlocked.CompareExchange(
                ref _cancellationState,
                (int)reason,
                (int)LauncherOperationCancellationReason.None)
            != (int)LauncherOperationCancellationReason.None)
        {
            return false;
        }

        try
        {
            _cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
