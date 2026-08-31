namespace WotLK.Launcher.Runtime;

internal enum LauncherOperationKind
{
    Verify,
    GameRepair,
    GameInstall,
    GameUpdate,
    Addons,
    LauncherAutoUpdate,
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
    private bool _isShuttingDown;
    private int _disposeState;
    private int _shutdownCancellationDisposeState;

    internal event EventHandler? StateChanged;

    internal CancellationToken ShutdownToken => _shutdownCancellation.Token;

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
        bool clientIsPlayable = false)
    {
        LauncherOperationLease? lease = null;
        LauncherOperationStartStatus status;
        lock (_sync)
        {
            status = GetMaintenanceStartStatusUnsafe(kind, clientIsPlayable);
            if (status == LauncherOperationStartStatus.Started)
            {
                lease = CreateLeaseUnsafe(kind, canUserCancel, isPlayLease: false);
                _maintenanceLease = lease;
            }
        }

        if (lease is not null)
        {
            RaiseStateChanged();
        }

        return new LauncherOperationStartResult(status, lease);
    }

    internal LauncherOperationStartResult TryBeginPlay(bool clientIsPlayable)
    {
        LauncherOperationLease? lease = null;
        LauncherOperationStartStatus status;
        lock (_sync)
        {
            if (_isShuttingDown || Volatile.Read(ref _disposeState) != 0)
            {
                status = LauncherOperationStartStatus.ShuttingDown;
            }
            else if (!clientIsPlayable)
            {
                status = LauncherOperationStartStatus.RejectedByCompatibility;
            }
            else if (_playLease is not null)
            {
                status = LauncherOperationStartStatus.Busy;
            }
            else if (_maintenanceLease is not null
                     && _maintenanceLease.Kind != LauncherOperationKind.Verify)
            {
                status = LauncherOperationStartStatus.RejectedByCompatibility;
            }
            else
            {
                status = LauncherOperationStartStatus.Started;
                lease = CreateLeaseUnsafe(
                    LauncherOperationKind.Play,
                    canUserCancel: false,
                    isPlayLease: true);
                _playLease = lease;
            }
        }

        if (lease is not null)
        {
            RaiseStateChanged();
        }

        return new LauncherOperationStartResult(status, lease);
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
            RaiseStateChanged();
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
        lock (_sync)
        {
            if (!IsCurrentUnsafe(lease) || !lease.CanUserCancel)
            {
                return false;
            }

            return lease.RequestCancellation();
        }
    }

    internal bool CancelLeaseForShutdown(LauncherOperationLease lease)
    {
        lock (_sync)
        {
            if (!IsCurrentUnsafe(lease))
            {
                return false;
            }

            return lease.RequestCancellation();
        }
    }

    internal void Release(LauncherOperationLease lease)
    {
        bool released = false;
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
            }
        }

        if (released)
        {
            RaiseStateChanged();
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

    private LauncherOperationLease CreateLeaseUnsafe(
        LauncherOperationKind kind,
        bool canUserCancel,
        bool isPlayLease)
    {
        return new LauncherOperationLease(
            this,
            ++_nextOperationId,
            kind,
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

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // Operation ownership must not depend on presentation subscribers.
        }
    }

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
        bool canUserCancel,
        bool isPlayLease,
        CancellationTokenSource cancellation)
    {
        _owner = owner;
        OperationId = operationId;
        Kind = kind;
        _initiallyUserCancellable = canUserCancel;
        IsPlayLease = isPlayLease;
        _cancellation = cancellation;
    }

    internal long OperationId { get; }

    internal LauncherOperationKind Kind { get; }

    internal bool CanUserCancel => _initiallyUserCancellable
        && Volatile.Read(ref _userCancellationDisabled) == 0;

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
        return _initiallyUserCancellable
            && Volatile.Read(ref _completionState) == 0
            && Interlocked.Exchange(ref _userCancellationDisabled, 1) == 0;
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

    internal bool RequestCancellation()
    {
        if (Interlocked.Exchange(ref _cancellationState, 1) != 0)
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
