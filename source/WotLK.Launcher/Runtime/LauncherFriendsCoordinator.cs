using System.Collections.Immutable;
using System.Net;
using System.Net.Http;

namespace WotLK.Launcher.Runtime;

internal sealed class LauncherFriendsCoordinator : IDisposable
{
    internal static readonly TimeSpan AutomaticRefreshInterval = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly LauncherSessionCoordinator _session;
    private readonly ILauncherAuthService _authentication;
    private readonly CancellationToken _lifetimeToken;
    private readonly Func<LauncherProfile?> _getCurrentProfile;
    private readonly Action<string> _writeLog;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly ITimer _automaticRefreshTimer;
    private readonly HashSet<Task> _inFlightTasks = [];
    private FriendsRuntimeSnapshot _currentSnapshot;
    private CancellationTokenSource? _activeCancellation;
    private long? _activeOperationId;
    private long _nextOperationId;
    private long _sequence;
    private bool _isShuttingDown;
    private bool _isAutomaticRefreshEnabled;
    private int _disposeState;

    internal LauncherFriendsCoordinator(
        LauncherSessionCoordinator session,
        ILauncherAuthService authentication,
        CancellationToken lifetimeToken,
        Func<LauncherProfile?> getCurrentProfile,
        Action<string> writeLog,
        TimeProvider? refreshTimeProvider = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _lifetimeToken = lifetimeToken;
        _getCurrentProfile = getCurrentProfile ?? throw new ArgumentNullException(nameof(getCurrentProfile));
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _currentSnapshot = CreateSessionSnapshot(_session.CurrentSnapshot, _getCurrentProfile());
        _automaticRefreshTimer = (refreshTimeProvider ?? TimeProvider.System).CreateTimer(
            static state => ((LauncherFriendsCoordinator)state!).AutomaticRefreshTimer_Tick(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        _session.SnapshotChanged += Session_SnapshotChanged;
        _lifetimeRegistration = lifetimeToken.Register(BeginShutdown);
        UpdateAutomaticRefreshState();
    }

    internal event EventHandler<FriendsRuntimeSnapshotEventArgs>? SnapshotChanged;

    internal FriendsRuntimeSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    internal FriendsActionStartResult TryRefresh()
    {
        return TryStart(
            FriendsOperationState.Refreshing,
            null,
            string.Empty,
            isAutomaticRefresh: false);
    }

    internal FriendsActionStartResult TrySendRequest(string username)
    {
        string normalized = username?.Trim() ?? string.Empty;
        if (normalized.Length is < 2 or > 32)
        {
            return FriendsActionStartResult.Rejected(FriendsActionStartStatus.InvalidRequest);
        }

        return TryStart(FriendsOperationState.SendingRequest, null, normalized);
    }

    internal FriendsActionStartResult TryAcceptRequest(uint accountId)
    {
        return accountId == 0
            ? FriendsActionStartResult.Rejected(FriendsActionStartStatus.InvalidRequest)
            : TryStart(FriendsOperationState.AcceptingRequest, accountId, string.Empty);
    }

    internal FriendsActionStartResult TryRejectRequest(uint accountId)
    {
        return accountId == 0
            ? FriendsActionStartResult.Rejected(FriendsActionStartStatus.InvalidRequest)
            : TryStart(FriendsOperationState.RejectingRequest, accountId, string.Empty);
    }

    internal FriendsActionStartResult TryCancelRequest(uint accountId)
    {
        return accountId == 0
            ? FriendsActionStartResult.Rejected(FriendsActionStartStatus.InvalidRequest)
            : TryStart(FriendsOperationState.CancellingRequest, accountId, string.Empty);
    }

    internal FriendsActionStartResult TryRemoveFriend(uint accountId)
    {
        return accountId == 0
            ? FriendsActionStartResult.Rejected(FriendsActionStartStatus.InvalidRequest)
            : TryStart(FriendsOperationState.RemovingFriend, accountId, string.Empty);
    }

    internal void BeginShutdown()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            cancellation = _activeCancellation;
        }

        UpdateAutomaticRefreshState();
        TryCancel(cancellation);
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task[] tasks;
        lock (_sync)
        {
            tasks = _inFlightTasks.ToArray();
        }
        if (tasks.Length == 0)
        {
            return true;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _session.SnapshotChanged -= Session_SnapshotChanged;
        BeginShutdown();
        _lifetimeRegistration.Dispose();
        _automaticRefreshTimer.Dispose();
    }

    private FriendsActionStartResult TryStart(
        FriendsOperationState operation,
        uint? targetAccountId,
        string targetUsername,
        bool isAutomaticRefresh = false)
    {
        FriendsRuntimeSnapshot snapshot;
        CancellationTokenSource cancellation;
        TaskCompletionSource startGate;
        Task<FriendsActionCompletion> completion;
        long operationId;
        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return FriendsActionStartResult.Rejected(FriendsActionStartStatus.ShuttingDown);
            }
            if (!_session.CurrentSnapshot.IsAuthenticated || !_currentSnapshot.IsAuthenticated)
            {
                return FriendsActionStartResult.Rejected(FriendsActionStartStatus.NotAuthenticated);
            }
            if (_activeOperationId is not null)
            {
                return FriendsActionStartResult.Rejected(FriendsActionStartStatus.Busy);
            }

            operationId = ++_nextOperationId;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            _activeOperationId = operationId;
            _activeCancellation = cancellation;
            snapshot = SetSnapshotUnsafe(_currentSnapshot with
            {
                OperationId = operationId,
                LoadState = operation == FriendsOperationState.Refreshing
                    && (!isAutomaticRefresh || _currentSnapshot.LoadState != FriendsLoadState.Loaded)
                    ? FriendsLoadState.Loading
                    : _currentSnapshot.LoadState,
                SearchState = operation == FriendsOperationState.SendingRequest
                    ? FriendsSearchState.Sending
                    : FriendsSearchState.Idle,
                OperationState = operation,
                TargetAccountId = targetAccountId,
                TargetUsername = targetUsername,
                ErrorState = FriendsRuntimeError.None,
                Notice = FriendsNoticeKind.None,
                IsAutomaticRefresh = operation == FriendsOperationState.Refreshing
                    && isAutomaticRefresh
            });
            startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = RunAfterGateAsync(
                startGate.Task,
                operationId,
                operation,
                targetAccountId,
                targetUsername,
                isAutomaticRefresh,
                cancellation);
            _inFlightTasks.Add(completion);
            _ = completion.ContinueWith(
                task => RemoveInFlight(task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        RaiseSnapshotChanged(snapshot);
        startGate.TrySetResult();
        return new FriendsActionStartResult(
            FriendsActionStartStatus.Started,
            operationId,
            completion);
    }

    private async Task<FriendsActionCompletion> RunAfterGateAsync(
        Task gate,
        long operationId,
        FriendsOperationState operation,
        uint? targetAccountId,
        string targetUsername,
        bool isAutomaticRefresh,
        CancellationTokenSource cancellation)
    {
        await gate.ConfigureAwait(false);
        return await RunOperationAsync(
            operationId,
            operation,
            targetAccountId,
            targetUsername,
            isAutomaticRefresh,
            cancellation).ConfigureAwait(false);
    }

    private async Task<FriendsActionCompletion> RunOperationAsync(
        long operationId,
        FriendsOperationState operation,
        uint? targetAccountId,
        string targetUsername,
        bool isAutomaticRefresh,
        CancellationTokenSource cancellation)
    {
        try
        {
            AtlasRequestPreparationStatus preparation = await _session
                .PrepareAuthenticatedRequestAsync(cancellation.Token)
                .ConfigureAwait(false);
            if (preparation != AtlasRequestPreparationStatus.Ready)
            {
                return preparation is AtlasRequestPreparationStatus.Cancelled
                    or AtlasRequestPreparationStatus.ShuttingDown
                    ? CompleteCancelled(operationId, cancellation)
                    : CompleteFailure(
                        operationId,
                        operation,
                        preparation == AtlasRequestPreparationStatus.AuthenticationRequired
                            ? FriendsErrorCategory.Unauthorized
                            : FriendsErrorCategory.ServiceUnavailable,
                        isAutomaticRefresh,
                        cancellation);
            }

            return operation switch
            {
                FriendsOperationState.Refreshing => await RefreshAsync(
                    operationId,
                    isAutomaticRefresh,
                    cancellation).ConfigureAwait(false),
                FriendsOperationState.SendingRequest => await SendRequestAsync(
                    operationId,
                    targetUsername,
                    cancellation).ConfigureAwait(false),
                FriendsOperationState.AcceptingRequest => await AcceptRequestAsync(
                    operationId,
                    targetAccountId!.Value,
                    cancellation).ConfigureAwait(false),
                FriendsOperationState.RejectingRequest
                    or FriendsOperationState.CancellingRequest
                    or FriendsOperationState.RemovingFriend => await RemoveRelationAsync(
                        operationId,
                        operation,
                        targetAccountId!.Value,
                        cancellation).ConfigureAwait(false),
                _ => CompleteFailure(
                    operationId,
                    operation,
                    FriendsErrorCategory.Unknown,
                    isAutomaticRefresh,
                    cancellation)
            };
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CompleteCancelled(operationId, cancellation);
        }
        catch (OperationCanceledException exception)
        {
            WriteFailureSafely(operation, FriendsErrorCategory.Timeout, exception);
            return CompleteFailure(
                operationId,
                operation,
                FriendsErrorCategory.Timeout,
                isAutomaticRefresh,
                cancellation);
        }
        catch (LauncherAuthException exception)
        {
            FriendsErrorCategory category = MapAuthFailure(operation, exception);
            WriteFailureSafely(operation, category, exception);
            if (category == FriendsErrorCategory.Unauthorized)
            {
                _session.NotifyAuthenticatedRequestUnauthorized();
            }
            return CompleteFailure(
                operationId,
                operation,
                category,
                isAutomaticRefresh,
                cancellation);
        }
        catch (HttpRequestException exception)
        {
            FriendsErrorCategory category = MapHttpFailure(exception.StatusCode);
            WriteFailureSafely(operation, category, exception);
            return CompleteFailure(
                operationId,
                operation,
                category,
                isAutomaticRefresh,
                cancellation);
        }
        catch (TimeoutException exception)
        {
            WriteFailureSafely(operation, FriendsErrorCategory.Timeout, exception);
            return CompleteFailure(
                operationId,
                operation,
                FriendsErrorCategory.Timeout,
                isAutomaticRefresh,
                cancellation);
        }
        catch (Exception exception)
        {
            WriteFailureSafely(operation, FriendsErrorCategory.Unknown, exception);
            return CompleteFailure(
                operationId,
                operation,
                FriendsErrorCategory.Unknown,
                isAutomaticRefresh,
                cancellation);
        }
    }

    private async Task<FriendsActionCompletion> RefreshAsync(
        long operationId,
        bool isAutomaticRefresh,
        CancellationTokenSource cancellation)
    {
        IReadOnlyList<LauncherFriend> result = await _authentication
            .GetFriendsAsync(cancellation.Token)
            .ConfigureAwait(false);
        FriendLists lists = Split(result, CurrentSnapshot.CurrentUserId);
        return CompleteSuccess(
            operationId,
            cancellation,
            snapshot => ApplyLists(snapshot, lists) with
            {
                LoadState = FriendsLoadState.Loaded,
                SearchState = FriendsSearchState.Idle,
                IsAutomaticRefresh = false,
                IsStale = false
            });
    }

    private async Task<FriendsActionCompletion> SendRequestAsync(
        long operationId,
        string targetUsername,
        CancellationTokenSource cancellation)
    {
        _ = await _authentication
            .SendFriendRequestAsync(targetUsername, cancellation.Token)
            .ConfigureAwait(false);
        IReadOnlyList<LauncherFriend> result = await _authentication
            .GetFriendsAsync(cancellation.Token)
            .ConfigureAwait(false);
        FriendLists lists = Split(result, CurrentSnapshot.CurrentUserId);
        bool accepted = lists.Friends.Any(friend =>
            string.Equals(friend.Username, targetUsername, StringComparison.OrdinalIgnoreCase));
        return CompleteSuccess(
            operationId,
            cancellation,
            snapshot => ApplyLists(snapshot, lists) with
            {
                LoadState = FriendsLoadState.Loaded,
                SearchState = FriendsSearchState.Succeeded,
                Notice = accepted
                    ? FriendsNoticeKind.FriendshipAccepted
                    : FriendsNoticeKind.RequestSent
            });
    }

    private async Task<FriendsActionCompletion> AcceptRequestAsync(
        long operationId,
        uint accountId,
        CancellationTokenSource cancellation)
    {
        await _authentication.AcceptFriendAsync(accountId, cancellation.Token).ConfigureAwait(false);
        return CompleteSuccess(
            operationId,
            cancellation,
            snapshot => AcceptLocally(snapshot, accountId) with
            {
                Notice = FriendsNoticeKind.RequestAccepted
            });
    }

    private async Task<FriendsActionCompletion> RemoveRelationAsync(
        long operationId,
        FriendsOperationState operation,
        uint accountId,
        CancellationTokenSource cancellation)
    {
        await _authentication.RemoveFriendAsync(accountId, cancellation.Token).ConfigureAwait(false);
        FriendsNoticeKind notice = operation switch
        {
            FriendsOperationState.RejectingRequest => FriendsNoticeKind.RequestRejected,
            FriendsOperationState.CancellingRequest => FriendsNoticeKind.RequestCancelled,
            _ => FriendsNoticeKind.FriendRemoved
        };
        return CompleteSuccess(
            operationId,
            cancellation,
            snapshot => RemoveLocally(snapshot, accountId) with { Notice = notice });
    }

    private FriendsActionCompletion CompleteSuccess(
        long operationId,
        CancellationTokenSource cancellation,
        Func<FriendsRuntimeSnapshot, FriendsRuntimeSnapshot> update)
    {
        FriendsRuntimeSnapshot? published = null;
        FriendsRuntimeSnapshot current;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(operationId) || IsStoppingUnsafe())
            {
                current = _currentSnapshot;
            }
            else
            {
                published = SetSnapshotUnsafe(update(_currentSnapshot) with
                {
                    OperationId = null,
                    OperationState = FriendsOperationState.None,
                    TargetAccountId = null,
                    TargetUsername = string.Empty,
                    ErrorState = FriendsRuntimeError.None,
                    IsAutomaticRefresh = false
                });
                current = published;
                ReleaseOperationUnsafe(operationId);
            }
        }

        cancellation.Dispose();
        RaiseSnapshotChanged(published);
        return new FriendsActionCompletion(
            published is null
                ? FriendsActionCompletionStatus.Superseded
                : FriendsActionCompletionStatus.Succeeded,
            current);
    }

    private FriendsActionCompletion CompleteFailure(
        long operationId,
        FriendsOperationState operation,
        FriendsErrorCategory category,
        bool isAutomaticRefresh,
        CancellationTokenSource cancellation)
    {
        FriendsRuntimeSnapshot? published = null;
        FriendsRuntimeSnapshot current;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(operationId) || IsStoppingUnsafe())
            {
                current = _currentSnapshot;
            }
            else
            {
                bool quietAutomaticFailure = operation == FriendsOperationState.Refreshing
                    && isAutomaticRefresh;
                published = SetSnapshotUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    LoadState = quietAutomaticFailure
                        ? _currentSnapshot.LoadState == FriendsLoadState.Loading
                            ? FriendsLoadState.Idle
                            : _currentSnapshot.LoadState
                        : operation == FriendsOperationState.Refreshing
                            ? FriendsLoadState.Failed
                            : _currentSnapshot.LoadState,
                    SearchState = operation == FriendsOperationState.SendingRequest
                        ? FriendsSearchState.Failed
                        : _currentSnapshot.SearchState,
                    OperationState = FriendsOperationState.None,
                    TargetAccountId = null,
                    TargetUsername = string.Empty,
                    ErrorState = quietAutomaticFailure
                        ? FriendsRuntimeError.None
                        : new FriendsRuntimeError(operation, category),
                    Notice = FriendsNoticeKind.None,
                    IsAutomaticRefresh = false,
                    IsStale = quietAutomaticFailure || _currentSnapshot.IsStale
                });
                current = published;
                ReleaseOperationUnsafe(operationId);
            }
        }

        cancellation.Dispose();
        RaiseSnapshotChanged(published);
        return new FriendsActionCompletion(
            published is null
                ? FriendsActionCompletionStatus.Superseded
                : FriendsActionCompletionStatus.Failed,
            current);
    }

    private FriendsActionCompletion CompleteCancelled(
        long operationId,
        CancellationTokenSource cancellation)
    {
        FriendsRuntimeSnapshot? published = null;
        FriendsRuntimeSnapshot current;
        lock (_sync)
        {
            if (IsCurrentUnsafe(operationId) && !IsStoppingUnsafe())
            {
                published = SetSnapshotUnsafe(_currentSnapshot with
                {
                    OperationId = null,
                    LoadState = _currentSnapshot.LoadState == FriendsLoadState.Loading
                        ? FriendsLoadState.Idle
                        : _currentSnapshot.LoadState,
                    SearchState = FriendsSearchState.Idle,
                    OperationState = FriendsOperationState.None,
                    TargetAccountId = null,
                    TargetUsername = string.Empty,
                    ErrorState = FriendsRuntimeError.None,
                    Notice = FriendsNoticeKind.None,
                    IsAutomaticRefresh = false
                });
                ReleaseOperationUnsafe(operationId);
            }
            current = published ?? _currentSnapshot;
        }

        cancellation.Dispose();
        RaiseSnapshotChanged(published);
        return new FriendsActionCompletion(
            published is null
                ? FriendsActionCompletionStatus.Superseded
                : FriendsActionCompletionStatus.Cancelled,
            current);
    }

    private void Session_SnapshotChanged(object? sender, AuthSessionSnapshotEventArgs e)
    {
        CancellationTokenSource? cancellation = null;
        FriendsRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (IsStoppingUnsafe())
            {
                return;
            }

            cancellation = _activeCancellation;
            _activeCancellation = null;
            _activeOperationId = null;
            snapshot = CreateSessionSnapshot(e.Snapshot, _getCurrentProfile());
            _currentSnapshot = snapshot;
        }

        TryCancel(cancellation);
        UpdateAutomaticRefreshState();
        RaiseSnapshotChanged(snapshot);
    }

    private FriendsRuntimeSnapshot CreateSessionSnapshot(
        AuthSessionSnapshot session,
        LauncherProfile? profile)
    {
        if (!session.IsAuthenticated)
        {
            return FriendsRuntimeSnapshot.SignedOut with { Sequence = ++_sequence };
        }

        return FriendsRuntimeSnapshot.SignedOut with
        {
            Sequence = ++_sequence,
            CurrentUserId = profile?.AccountId,
            IsAuthenticated = true,
            LoadState = FriendsLoadState.Idle
        };
    }

    private FriendsRuntimeSnapshot SetSnapshotUnsafe(FriendsRuntimeSnapshot snapshot)
    {
        _currentSnapshot = snapshot with { Sequence = ++_sequence };
        return _currentSnapshot;
    }

    private void ReleaseOperationUnsafe(long operationId)
    {
        if (_activeOperationId != operationId)
        {
            return;
        }

        _activeOperationId = null;
        _activeCancellation = null;
    }

    private bool IsCurrentUnsafe(long operationId)
    {
        return _activeOperationId == operationId;
    }

    private bool IsStoppingUnsafe()
    {
        return _isShuttingDown
            || Volatile.Read(ref _disposeState) != 0
            || _lifetimeToken.IsCancellationRequested;
    }

    private void RemoveInFlight(Task task)
    {
        lock (_sync)
        {
            _inFlightTasks.Remove(task);
        }
    }

    private void RaiseSnapshotChanged(FriendsRuntimeSnapshot? snapshot)
    {
        if (snapshot is null || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        try
        {
            SnapshotChanged?.Invoke(this, new FriendsRuntimeSnapshotEventArgs(snapshot));
        }
        catch
        {
            // Presentation subscribers cannot interrupt social state ownership.
        }
    }

    private void WriteFailureSafely(
        FriendsOperationState operation,
        FriendsErrorCategory category,
        Exception exception)
    {
        try
        {
            _writeLog(
                $"Amis Atlas indisponibles: operation={operation}; category={category}; "
                + $"type={exception.GetType().Name}.");
        }
        catch
        {
        }
    }

    private static FriendsRuntimeSnapshot ApplyLists(
        FriendsRuntimeSnapshot snapshot,
        FriendLists lists)
    {
        return snapshot with
        {
            Friends = lists.Friends,
            IncomingRequests = lists.Incoming,
            OutgoingRequests = lists.Outgoing
        };
    }

    private static FriendsRuntimeSnapshot AcceptLocally(
        FriendsRuntimeSnapshot snapshot,
        uint accountId)
    {
        FriendRuntimeItem? accepted = snapshot.IncomingRequests.FirstOrDefault(
            friend => friend.AccountId == accountId);
        ImmutableArray<FriendRuntimeItem> friends = snapshot.Friends
            .Where(friend => friend.AccountId != accountId)
            .ToImmutableArray();
        if (accepted is not null)
        {
            friends = friends
                .Add(accepted with { Relationship = FriendRelationship.Accepted })
                .OrderByDescending(friend => friend.IsAvailable)
                .ThenBy(friend => friend.Username, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }

        return snapshot with
        {
            Friends = friends,
            IncomingRequests = snapshot.IncomingRequests
                .Where(friend => friend.AccountId != accountId)
                .ToImmutableArray()
        };
    }

    private static FriendsRuntimeSnapshot RemoveLocally(
        FriendsRuntimeSnapshot snapshot,
        uint accountId)
    {
        return snapshot with
        {
            Friends = snapshot.Friends
                .Where(friend => friend.AccountId != accountId)
                .ToImmutableArray(),
            IncomingRequests = snapshot.IncomingRequests
                .Where(friend => friend.AccountId != accountId)
                .ToImmutableArray(),
            OutgoingRequests = snapshot.OutgoingRequests
                .Where(friend => friend.AccountId != accountId)
                .ToImmutableArray()
        };
    }

    private static FriendLists Split(
        IReadOnlyList<LauncherFriend> source,
        uint? currentUserId)
    {
        List<FriendRuntimeItem> friends = [];
        List<FriendRuntimeItem> incoming = [];
        List<FriendRuntimeItem> outgoing = [];
        foreach (LauncherFriend item in source)
        {
            if (currentUserId is uint ownAccountId && item.AccountId == ownAccountId)
            {
                continue;
            }

            FriendRelationship? relationship = ParseRelationship(item.Relationship);
            if (relationship is null)
            {
                continue;
            }

            FriendRuntimeItem friend = new(
                item.AccountId,
                item.Username,
                item.AvatarKey,
                item.Avatar,
                relationship.Value,
                item.Online,
                item.CharacterName,
                item.Level,
                item.ClassId,
                item.ZoneId,
                item.LastSeenAt,
                item.StatusMessage?.Trim() ?? string.Empty,
                item.Bio?.Trim() ?? string.Empty,
                (item.Characters ?? [])
                    .Select(character => new FriendCharacterRuntimeItem(
                        character.Name,
                        character.Level,
                        character.ClassId,
                        character.ZoneId,
                        character.Online,
                        character.LastSeenAt))
                    .ToImmutableArray(),
                item.LauncherOnline,
                item.LauncherLastSeenAt);
            switch (relationship)
            {
                case FriendRelationship.Incoming:
                    incoming.Add(friend);
                    break;
                case FriendRelationship.Outgoing:
                    outgoing.Add(friend);
                    break;
                default:
                    friends.Add(friend);
                    break;
            }
        }

        return new FriendLists(
            friends.OrderByDescending(friend => friend.IsAvailable)
                .ThenBy(friend => friend.Username, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            incoming.OrderBy(friend => friend.Username, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            outgoing.OrderBy(friend => friend.Username, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray());
    }

    private static FriendRelationship? ParseRelationship(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "accepted" => FriendRelationship.Accepted,
            "incoming" => FriendRelationship.Incoming,
            "outgoing" => FriendRelationship.Outgoing,
            _ => null
        };
    }

    private static FriendsErrorCategory MapAuthFailure(
        FriendsOperationState operation,
        LauncherAuthException exception)
    {
        if (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            return FriendsErrorCategory.Unauthorized;
        }
        if (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            return FriendsErrorCategory.Forbidden;
        }
        if (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return operation == FriendsOperationState.SendingRequest
                ? FriendsErrorCategory.UserNotFound
                : FriendsErrorCategory.RelationNotFound;
        }
        if (exception.StatusCode == HttpStatusCode.BadRequest)
        {
            return operation == FriendsOperationState.SendingRequest
                ? FriendsErrorCategory.Self
                : FriendsErrorCategory.Validation;
        }
        if (exception.StatusCode == HttpStatusCode.Conflict)
        {
            if (string.Equals(exception.Code, "FriendAlreadyPending", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("déjà en attente", StringComparison.OrdinalIgnoreCase))
            {
                return FriendsErrorCategory.AlreadyPending;
            }
            if (string.Equals(exception.Code, "FriendAlreadyExists", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("déjà partie", StringComparison.OrdinalIgnoreCase))
            {
                return FriendsErrorCategory.AlreadyFriends;
            }
            return FriendsErrorCategory.ServerRejected;
        }
        if (exception.StatusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.GatewayTimeout)
        {
            return FriendsErrorCategory.Timeout;
        }
        if (exception.StatusCode is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable)
        {
            return FriendsErrorCategory.ServiceUnavailable;
        }
        if (exception.StatusCode is HttpStatusCode statusCode && (int)statusCode >= 500)
        {
            return FriendsErrorCategory.ServiceUnavailable;
        }
        return exception.StatusCode is null
            ? FriendsErrorCategory.Unknown
            : FriendsErrorCategory.ServerRejected;
    }

    private static FriendsErrorCategory MapHttpFailure(HttpStatusCode? statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout =>
                FriendsErrorCategory.Timeout,
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable =>
                FriendsErrorCategory.ServiceUnavailable,
            _ => FriendsErrorCategory.Network
        };
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void AutomaticRefreshTimer_Tick()
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        _ = TryStart(
            FriendsOperationState.Refreshing,
            null,
            string.Empty,
            isAutomaticRefresh: true);
    }

    private void UpdateAutomaticRefreshState()
    {
        lock (_sync)
        {
            bool enabled = _currentSnapshot.IsAuthenticated && !IsStoppingUnsafe();
            if (_isAutomaticRefreshEnabled == enabled)
            {
                return;
            }

            _isAutomaticRefreshEnabled = enabled;
            try
            {
                _automaticRefreshTimer.Change(
                    enabled ? AutomaticRefreshInterval : Timeout.InfiniteTimeSpan,
                    enabled ? AutomaticRefreshInterval : Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed record FriendLists(
        ImmutableArray<FriendRuntimeItem> Friends,
        ImmutableArray<FriendRuntimeItem> Incoming,
        ImmutableArray<FriendRuntimeItem> Outgoing);
}
