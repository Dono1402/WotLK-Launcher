using System.Collections.Immutable;
using System.IO;
using System.Net;

namespace WotLK.Launcher.Runtime;

internal sealed record ChatRuntimeSnapshot
{
    internal long Sequence { get; init; }
    internal Guid SessionId { get; init; }
    internal uint OwnerAccountId { get; init; }
    internal ImmutableArray<LauncherChatConversation> Conversations { get; init; } = [];
    internal int UnreadCount { get; init; }
    internal uint? SelectedFriendAccountId { get; init; }
    internal string SelectedFriendUsername { get; init; } = string.Empty;
    internal ImmutableArray<LauncherChatMessage> Messages { get; init; } = [];
    internal bool IsAvailable { get; init; }
    internal bool IsLoading { get; init; }
    internal bool IsLoadingEarlier { get; init; }
    internal bool IsSending { get; init; }
    internal bool HasEarlier { get; init; }
    internal bool CanSend { get; init; }
    internal string ErrorCode { get; init; } = string.Empty;
}

internal sealed class ChatRuntimeSnapshotEventArgs(ChatRuntimeSnapshot snapshot) : EventArgs
{
    internal ChatRuntimeSnapshot Snapshot { get; } = snapshot;
}

internal sealed record ChatSendCompletion(Guid ClientMessageId, Guid SessionId, bool Success, string ErrorCode);

internal sealed class LauncherChatCoordinator : IDisposable
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private readonly object _sync = new();
    private readonly LauncherSessionCoordinator _session;
    private readonly ILauncherAuthService _authentication;
    private readonly LauncherFriendsCoordinator _friends;
    private readonly ILauncherChatApiClient _api;
    private readonly CancellationToken _lifetimeToken;
    private readonly Action<string> _writeLog;
    private readonly ITimer _timer;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly SemaphoreSlim _networkGate = new(1, 1);
    private readonly HashSet<Task> _inFlight = [];
    private readonly Dictionary<Guid, uint> _sending = [];
    private readonly HashSet<uint> _blockedFriends = [];
    private CancellationTokenSource _sessionCancellation;
    private ChatRuntimeSnapshot _current = new();
    private HashSet<uint>? _knownFriends;
    private Task? _pollTask;
    private long _sequence;
    private long _authSequence;
    private long _selectionVersion;
    private long _cursor;
    private long _selectedLastRead;
    private long _threadVisibleThrough;
    private bool _selectionReady;
    private bool _initialized;
    private bool _started;
    private bool _viewActive;
    private bool _threadAtBottom;
    private bool _stopping;
    private int _disposed;

    internal LauncherChatCoordinator(LauncherSessionCoordinator session, ILauncherAuthService authentication,
        LauncherFriendsCoordinator friends, ILauncherChatApiClient api, CancellationToken lifetimeToken,
        Action<string> writeLog, TimeProvider? timeProvider = null)
    {
        _session = session;
        _authentication = authentication;
        _friends = friends;
        _api = api;
        _lifetimeToken = lifetimeToken;
        _writeLog = writeLog;
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _timer = (timeProvider ?? TimeProvider.System).CreateTimer(static state =>
            _ = ((LauncherChatCoordinator)state!).RefreshAsync(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ResetSession(_session.CurrentSnapshot);
        _session.SnapshotChanged += Session_SnapshotChanged;
        _friends.SnapshotChanged += Friends_SnapshotChanged;
        _lifetimeRegistration = lifetimeToken.Register(BeginShutdown);
    }

    internal event EventHandler<ChatRuntimeSnapshotEventArgs>? SnapshotChanged;
    internal ChatRuntimeSnapshot CurrentSnapshot { get { lock (_sync) return _current; } }

    internal void Start()
    {
        lock (_sync)
        {
            if (_stopping || _started) return;
            _started = true;
            UpdateTimerUnsafe();
        }
    }

    internal void SetPollingEnabled(bool enabled)
    {
        lock (_sync)
        {
            if (_stopping || _started == enabled) return;
            _started = enabled;
            UpdateTimerUnsafe();
        }
    }

    internal Task RefreshAsync()
    {
        lock (_sync)
        {
            if (_stopping || CaptureGuardUnsafe() is not RequestGuard guard) return Task.CompletedTask;
            if (_pollTask is { IsCompleted: false }) return _pollTask;
            _pollTask = Task.Run(() => RunRequestAsync(guard, PollCoreAsync));
            TrackUnsafe(_pollTask);
            return _pollTask;
        }
    }

    internal Task OpenConversationAsync(uint accountId, string username)
    {
        RequestGuard guard;
        long selection;
        ChatRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (_stopping || accountId == 0 || accountId == _current.OwnerAccountId
                || CaptureGuardUnsafe() is not RequestGuard currentGuard
                || (_knownFriends is not null && !_knownFriends.Contains(accountId))) return Task.CompletedTask;
            guard = currentGuard;
            selection = ++_selectionVersion;
            _selectionReady = false;
            _selectedLastRead = 0;
            snapshot = SetSnapshotUnsafe(_current with
            {
                SelectedFriendAccountId = accountId,
                SelectedFriendUsername = username ?? string.Empty,
                Messages = [], HasEarlier = false, IsLoading = true, IsLoadingEarlier = false, ErrorCode = string.Empty
            });
        }
        Publish(snapshot);
        return Track(RunRequestAsync(guard, async context =>
        {
            bool initialized;
            lock (_sync) initialized = _initialized;
            if (!initialized) await RefreshConversationsCoreAsync(context, initializeCursor: true).ConfigureAwait(false);
            await LoadSelectionCoreAsync(context, selection).ConfigureAwait(false);
        }, selection));
    }

    internal Task LoadEarlierAsync(uint accountId, long beforeId)
    {
        RequestGuard guard;
        long selection;
        ChatRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (CaptureGuardUnsafe() is not RequestGuard currentGuard || !_current.HasEarlier || _current.IsLoadingEarlier
                || _current.IsLoading || _current.SelectedFriendAccountId != accountId || beforeId <= 0
                || _current.Messages.IsDefaultOrEmpty || _current.Messages.Min(message => message.Id) != beforeId)
                return Task.CompletedTask;
            guard = currentGuard;
            selection = _selectionVersion;
            snapshot = SetSnapshotUnsafe(_current with { IsLoadingEarlier = true, ErrorCode = string.Empty });
        }
        Publish(snapshot);
        return Track(RunRequestAsync(guard, async context =>
        {
            if (!IsSelectionCurrent(context, selection, accountId)) return;
            LauncherChatMessages page = await _api.GetMessagesAsync(accountId, beforeId, context.Token).ConfigureAwait(false);
            ValidateThread(page.Messages, context.OwnerAccountId, accountId);
            Update(context, current => current with
            {
                Messages = Merge(current.Messages, page.Messages), HasEarlier = page.HasMore,
                IsLoadingEarlier = false, IsAvailable = true, ErrorCode = string.Empty
            }, selection);
        }, selection));
    }

    internal Task<ChatSendCompletion> SendAsync(uint accountId, Guid clientMessageId, string body, Guid sessionId)
    {
        RequestGuard guard;
        ChatRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (CaptureGuardUnsafe() is not RequestGuard currentGuard || currentGuard.SessionId != sessionId
                || _current.SelectedFriendAccountId != accountId || !_current.CanSend
                || _sending.ContainsValue(accountId) || clientMessageId == Guid.Empty
                || string.IsNullOrWhiteSpace(body) || body.Length > 1000)
                return Task.FromResult(new ChatSendCompletion(clientMessageId, sessionId, false, "chat-unavailable"));
            guard = currentGuard;
            _sending.Add(clientMessageId, accountId);
            snapshot = SetSnapshotUnsafe(_current with { ErrorCode = string.Empty });
        }
        Publish(snapshot);
        return Track(SendCoreAsync(guard, accountId, clientMessageId, body));
    }

    internal void SetViewActive(bool active)
    {
        bool refresh;
        lock (_sync)
        {
            refresh = active && !_viewActive;
            _viewActive = active;
        }
        if (refresh) _ = RefreshAsync();
    }

    internal void SetThreadAtBottom(Guid sessionId, uint? friendAccountId, bool atBottom, long throughMessageId)
    {
        RequestGuard? readGuard = null;
        lock (_sync)
        {
            if (_stopping || sessionId != _current.SessionId || friendAccountId != _current.SelectedFriendAccountId) return;
            long through = atBottom ? Math.Max(0, throughMessageId) : 0;
            bool changed = _threadAtBottom != atBottom || _threadVisibleThrough != through;
            _threadAtBottom = atBottom;
            _threadVisibleThrough = through;
            if (changed && atBottom && _viewActive && _selectionReady && through > _selectedLastRead)
                readGuard = CaptureGuardUnsafe();
        }
        if (readGuard is { } guard)
            _ = Track(Task.Run(() => RunRequestAsync(guard, MarkVisibleReadCoreAsync)));
    }

    private async Task<ChatSendCompletion> SendCoreAsync(RequestGuard guard, uint accountId, Guid clientMessageId, string body)
    {
        bool succeeded = false;
        string errorCode = "chat-cancelled";
        await RunRequestAsync(guard, async context =>
        {
            lock (_sync)
            {
                if (_knownFriends is not null && !_knownFriends.Contains(accountId))
                    throw new LauncherChatApiException(HttpStatusCode.Forbidden, "chat-not-friends");
            }
            LauncherChatSendResult result = await _api.SendAsync(accountId, clientMessageId, body, context.Token).ConfigureAwait(false);
            ValidateThread([result.Message], context.OwnerAccountId, accountId);
            if (result.Message.SenderAccountId != context.OwnerAccountId) throw new InvalidDataException("Invalid chat sender.");
            lock (_sync)
            {
                if (!IsCurrentUnsafe(context)) return;
                succeeded = true;
                errorCode = string.Empty;
            }
            Update(context, current => current with
            {
                Messages = current.SelectedFriendAccountId == accountId ? Merge(current.Messages, [result.Message]) : current.Messages,
                IsAvailable = true, ErrorCode = string.Empty
            });
            // The accepted send stays successful even if the following read-only refresh fails.
            try { await RefreshConversationsCoreAsync(context, initializeCursor: false).ConfigureAwait(false); }
            catch (LauncherChatApiException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
            { _session.NotifyAuthenticatedRequestUnauthorized(context.AuthSequence, context.Token); }
            catch (Exception error) when (error is not OperationCanceledException) { LogFailure(ErrorCode(error)); }
        }, onFailure: code => errorCode = code, targetAccountId: accountId).ConfigureAwait(false);
        ChatRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (IsCurrentUnsafe(guard))
            {
                _sending.Remove(clientMessageId);
                snapshot = SetSnapshotUnsafe(_current);
            }
        }
        Publish(snapshot);
        return new(clientMessageId, guard.SessionId, succeeded, errorCode);
    }

    private async Task PollCoreAsync(RequestGuard guard)
    {
        bool initialized;
        lock (_sync) initialized = _initialized;
        if (initialized)
        {
            for (int pageIndex = 0; pageIndex < 10; pageIndex++)
            {
                long after;
                lock (_sync) { if (!IsCurrentUnsafe(guard)) return; after = _cursor; }
                LauncherChatUpdates updates = await _api.GetUpdatesAsync(after, guard.Token).ConfigureAwait(false);
                ValidateOwner(updates.Messages, guard.OwnerAccountId);
                lock (_sync)
                {
                    if (!IsCurrentUnsafe(guard)) return;
                    _cursor = updates.LastMessageId;
                }
                Update(guard, current => current with
                {
                    Messages = current.SelectedFriendAccountId is uint friend
                        ? Merge(current.Messages, updates.Messages.Where(message => BelongsTo(message, guard.OwnerAccountId, friend)))
                        : current.Messages,
                    IsAvailable = true, ErrorCode = string.Empty
                });
                if (!updates.HasMore) break;
            }
        }
        await RefreshConversationsCoreAsync(guard, initializeCursor: !initialized).ConfigureAwait(false);
        long? selectionToLoad;
        lock (_sync) selectionToLoad = IsCurrentUnsafe(guard) && _current.SelectedFriendAccountId is not null && !_selectionReady
            ? _selectionVersion : null;
        if (selectionToLoad is long selection) await LoadSelectionCoreAsync(guard, selection).ConfigureAwait(false);
        await MarkVisibleReadCoreAsync(guard).ConfigureAwait(false);
    }

    private async Task RefreshConversationsCoreAsync(RequestGuard guard, bool initializeCursor)
    {
        List<LauncherChatConversation> all = [];
        long? before = null;
        long initialCursor = 0;
        int unreadCount = 0;
        for (int pageIndex = 0; ; pageIndex++)
        {
            EnsureCurrent(guard);
            LauncherChatConversations page = await _api.GetConversationsAsync(before, guard.Token).ConfigureAwait(false);
            if (pageIndex == 0) { initialCursor = page.LastMessageId; unreadCount = page.UnreadCount; }
            foreach (LauncherChatConversation conversation in page.Conversations)
            {
                if (conversation.FriendAccountId == guard.OwnerAccountId
                    || !BelongsTo(conversation.LastMessage, guard.OwnerAccountId, conversation.FriendAccountId))
                    throw new InvalidDataException("Invalid chat conversation.");
            }
            all.AddRange(page.Conversations);
            if (!page.HasMore) break;
            if (pageIndex >= 19 || page.Conversations.Count == 0) throw new InvalidDataException("Invalid chat pagination.");
            long next = page.Conversations.Min(conversation => conversation.LastMessage.Id);
            if (before is long previous && next >= previous) throw new InvalidDataException("Invalid chat cursor.");
            before = next;
        }
        ChatRuntimeSnapshot? snapshot;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard)) return;
            if (initializeCursor) _cursor = initialCursor;
            _initialized = true;
            snapshot = SetSnapshotUnsafe(_current with
            {
                Conversations = all.Where(conversation => _knownFriends is null || _knownFriends.Contains(conversation.FriendAccountId))
                    .DistinctBy(conversation => conversation.FriendAccountId).OrderByDescending(conversation => conversation.LastMessage.Id).ToImmutableArray(),
                UnreadCount = unreadCount, IsAvailable = true, ErrorCode = string.Empty
            });
        }
        Publish(snapshot);
    }

    private async Task LoadSelectionCoreAsync(RequestGuard guard, long selection)
    {
        uint accountId;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard) || selection != _selectionVersion || _current.SelectedFriendAccountId is not uint friend) return;
            accountId = friend;
        }
        LauncherChatMessages page = await _api.GetMessagesAsync(accountId, null, guard.Token).ConfigureAwait(false);
        ValidateThread(page.Messages, guard.OwnerAccountId, accountId);
        ChatRuntimeSnapshot? snapshot;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard) || selection != _selectionVersion) return;
            _selectionReady = true;
            _blockedFriends.Remove(accountId);
            _selectedLastRead = page.LastReadMessageId;
            snapshot = SetSnapshotUnsafe(_current with
            {
                SelectedFriendUsername = page.FriendUsername,
                Messages = Merge([], page.Messages), HasEarlier = page.HasMore,
                IsLoading = false, IsLoadingEarlier = false, IsAvailable = true, ErrorCode = string.Empty
            });
        }
        Publish(snapshot);
        await MarkVisibleReadCoreAsync(guard).ConfigureAwait(false);
    }

    private async Task MarkVisibleReadCoreAsync(RequestGuard guard)
    {
        uint accountId;
        long through;
        long selection;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard) || !_viewActive || !_threadAtBottom || !_selectionReady
                || _current.SelectedFriendAccountId is not uint friend || _current.Messages.IsDefaultOrEmpty) return;
            accountId = friend;
            // A poll may publish newer data before the dispatcher scrolls to it.
            // Only the cursor acknowledged by the rendered viewport is readable.
            through = Math.Min(_threadVisibleThrough, _current.Messages.Max(message => message.Id));
            if (through <= _selectedLastRead) return;
            selection = _selectionVersion;
        }
        LauncherChatReadResult result;
        try { result = await _api.MarkReadAsync(accountId, through, guard.Token).ConfigureAwait(false); }
        catch (LauncherChatApiException error) when (error.Code == "chat-not-friends")
        {
            BlockFriend(guard, accountId);
            return;
        }
        ChatRuntimeSnapshot? snapshot;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard) || selection != _selectionVersion) return;
            _selectedLastRead = Math.Max(_selectedLastRead, result.LastReadMessageId);
            int cleared = _current.Conversations.Where(conversation => conversation.FriendAccountId == accountId
                && conversation.LastMessage.Id <= result.LastReadMessageId).Sum(conversation => conversation.UnreadCount);
            snapshot = SetSnapshotUnsafe(_current with
            {
                Conversations = _current.Conversations.Select(conversation => conversation.FriendAccountId == accountId
                    ? conversation with
                    {
                        LastReadMessageId = result.LastReadMessageId,
                        UnreadCount = conversation.LastMessage.Id <= result.LastReadMessageId ? 0 : conversation.UnreadCount
                    } : conversation).ToImmutableArray(),
                UnreadCount = Math.Max(0, _current.UnreadCount - cleared)
            });
        }
        Publish(snapshot);
    }

    private async Task RunRequestAsync(RequestGuard guard, Func<RequestGuard, Task> action, long? selection = null,
        Action<string>? onFailure = null, uint? targetAccountId = null)
    {
        bool entered = false;
        try
        {
            await _networkGate.WaitAsync(guard.Token).ConfigureAwait(false);
            entered = true;
            EnsureCurrent(guard);
            bool refreshed = await _authentication.EnsureFreshAsync(guard.Token).ConfigureAwait(false);
            if (!refreshed)
            {
                _session.NotifyAuthenticatedRequestUnauthorized(guard.AuthSequence, guard.Token);
                throw new LauncherChatApiException(HttpStatusCode.Unauthorized, "chat-unauthorized");
            }
            EnsureCurrent(guard);
            await action(guard).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (guard.Token.IsCancellationRequested || !IsCurrent(guard))
        {
            onFailure?.Invoke("chat-cancelled");
        }
        catch (Exception error)
        {
            string code = ErrorCode(error);
            onFailure?.Invoke(code);
            if (code == "chat-unauthorized")
                _session.NotifyAuthenticatedRequestUnauthorized(guard.AuthSequence, guard.Token);
            else if (IsCurrent(guard))
            {
                if (code == "chat-not-friends")
                {
                    uint? affected = targetAccountId;
                    lock (_sync)
                    {
                        if (affected is null && selection == _selectionVersion) affected = _current.SelectedFriendAccountId;
                    }
                    if (affected is uint friend) BlockFriend(guard, friend);
                }
                Update(guard, current => current with
                {
                    IsAvailable = code is not ("chat-unavailable" or "chat-timeout" or "chat-request-failed"),
                    IsLoading = false, IsLoadingEarlier = false, ErrorCode = code
                }, selection);
                LogFailure(code);
            }
        }
        finally { if (entered) _networkGate.Release(); }
    }

    private void Session_SnapshotChanged(object? sender, AuthSessionSnapshotEventArgs args) => ResetSession(args.Snapshot);

    private void ResetSession(AuthSessionSnapshot session)
    {
        CancellationTokenSource previous;
        ChatRuntimeSnapshot snapshot;
        lock (_sync)
        {
            if (_stopping) return;
            previous = _sessionCancellation;
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            _authSequence = session.Sequence;
            uint accountId = session.IsAuthenticated ? _authentication.Session?.Profile.AccountId ?? 0 : 0;
            _initialized = _selectionReady = false;
            _cursor = _selectedLastRead = 0;
            _selectionVersion++;
            _sending.Clear();
            _blockedFriends.Clear();
            _knownFriends = ReadKnownFriends(accountId);
            snapshot = SetSnapshotUnsafe(new ChatRuntimeSnapshot
            {
                OwnerAccountId = accountId, SessionId = accountId > 0 ? Guid.NewGuid() : Guid.Empty
            });
            UpdateTimerUnsafe();
        }
        TryCancel(previous);
        previous.Dispose();
        Publish(snapshot);
    }

    private void Friends_SnapshotChanged(object? sender, FriendsRuntimeSnapshotEventArgs args)
    {
        ChatRuntimeSnapshot? snapshot = null;
        lock (_sync)
        {
            if (_stopping || args.Snapshot.CurrentUserId != _current.OwnerAccountId
                || args.Snapshot.LoadState != FriendsLoadState.Loaded) return;
            _knownFriends = args.Snapshot.Friends.Select(friend => friend.AccountId).ToHashSet();
            ChatRuntimeSnapshot next = _current with
            {
                Conversations = _current.Conversations.Where(conversation => _knownFriends.Contains(conversation.FriendAccountId)).ToImmutableArray()
            };
            if (next.SelectedFriendAccountId is uint selected && !_knownFriends.Contains(selected))
            {
                _selectionVersion++;
                _selectionReady = false;
                next = next with
                {
                    SelectedFriendAccountId = null, SelectedFriendUsername = string.Empty, Messages = [],
                    IsLoading = false, IsLoadingEarlier = false, HasEarlier = false, ErrorCode = string.Empty
                };
            }
            snapshot = SetSnapshotUnsafe(next);
        }
        Publish(snapshot);
    }

    private HashSet<uint>? ReadKnownFriends(uint ownerAccountId)
    {
        FriendsRuntimeSnapshot friends = _friends.CurrentSnapshot;
        return friends.CurrentUserId == ownerAccountId && friends.LoadState == FriendsLoadState.Loaded
            ? friends.Friends.Select(friend => friend.AccountId).ToHashSet() : null;
    }

    private void BlockFriend(RequestGuard guard, uint accountId)
    {
        ChatRuntimeSnapshot? snapshot;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard)) return;
            _blockedFriends.Add(accountId);
            _knownFriends?.Remove(accountId);
            ChatRuntimeSnapshot next = _current with
            {
                Conversations = _current.Conversations.Where(conversation => conversation.FriendAccountId != accountId).ToImmutableArray()
            };
            if (next.SelectedFriendAccountId == accountId)
            {
                _selectionVersion++;
                _selectionReady = false;
                next = next with
                {
                    SelectedFriendAccountId = null, SelectedFriendUsername = string.Empty, Messages = [],
                    IsLoading = false, IsLoadingEarlier = false, HasEarlier = false
                };
            }
            snapshot = SetSnapshotUnsafe(next);
        }
        Publish(snapshot);
    }

    private RequestGuard? CaptureGuardUnsafe() => !_stopping && _current.OwnerAccountId > 0 && _session.CurrentSnapshot.IsAuthenticated
        ? new(_authSequence, _current.OwnerAccountId, _current.SessionId, _sessionCancellation.Token) : null;
    private bool IsCurrentUnsafe(RequestGuard guard) => !_stopping && !guard.Token.IsCancellationRequested
        && _current.SessionId == guard.SessionId && _current.OwnerAccountId == guard.OwnerAccountId
        && _session.CurrentSnapshot.Sequence == guard.AuthSequence && _session.CurrentSnapshot.IsAuthenticated
        && _authentication.Session?.Profile.AccountId == guard.OwnerAccountId;
    private bool IsCurrent(RequestGuard guard) { lock (_sync) return IsCurrentUnsafe(guard); }
    private void EnsureCurrent(RequestGuard guard) { if (!IsCurrent(guard)) throw new OperationCanceledException(guard.Token); }
    private bool IsSelectionCurrent(RequestGuard guard, long selection, uint accountId)
    { lock (_sync) return IsCurrentUnsafe(guard) && selection == _selectionVersion && _current.SelectedFriendAccountId == accountId; }

    private void Update(RequestGuard guard, Func<ChatRuntimeSnapshot, ChatRuntimeSnapshot> update, long? selection = null)
    {
        ChatRuntimeSnapshot? snapshot;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard) || (selection is long version && version != _selectionVersion)) return;
            snapshot = SetSnapshotUnsafe(update(_current));
        }
        Publish(snapshot);
    }

    private ChatRuntimeSnapshot SetSnapshotUnsafe(ChatRuntimeSnapshot snapshot)
    {
        if (snapshot.SessionId != _current.SessionId || snapshot.OwnerAccountId != _current.OwnerAccountId
            || snapshot.SelectedFriendAccountId != _current.SelectedFriendAccountId)
        {
            _threadAtBottom = false;
            _threadVisibleThrough = 0;
        }
        return _current = snapshot with
        {
            Sequence = ++_sequence,
            IsSending = snapshot.SelectedFriendAccountId is uint target && _sending.ContainsValue(target),
            CanSend = _selectionReady && snapshot.IsAvailable && snapshot.OwnerAccountId > 0
                && snapshot.SelectedFriendAccountId is uint friend && friend != snapshot.OwnerAccountId
                && !_blockedFriends.Contains(friend)
                && (_knownFriends is null || _knownFriends.Contains(friend))
        };
    }

    private void Publish(ChatRuntimeSnapshot? snapshot)
    {
        if (snapshot is not null && Volatile.Read(ref _disposed) == 0)
            SnapshotChanged?.Invoke(this, new(snapshot));
    }

    private void UpdateTimerUnsafe() => _timer.Change(_started && !_stopping && _current.OwnerAccountId > 0 ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
        _started && !_stopping && _current.OwnerAccountId > 0 ? PollInterval : Timeout.InfiniteTimeSpan);

    private Task Track(Task task) { lock (_sync) TrackUnsafe(task); return task; }
    private Task<T> Track<T>(Task<T> task) { lock (_sync) TrackUnsafe(task); return task; }
    private void TrackUnsafe(Task task)
    {
        _inFlight.Add(task);
        _ = task.ContinueWith(completed => { lock (_sync) _inFlight.Remove(completed); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_sync) tasks = _inFlight.ToArray();
        try { await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    internal void BeginShutdown()
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_stopping) return;
            _stopping = true;
            cancellation = _sessionCancellation;
            UpdateTimerUnsafe();
        }
        TryCancel(cancellation);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.SnapshotChanged -= Session_SnapshotChanged;
        _friends.SnapshotChanged -= Friends_SnapshotChanged;
        BeginShutdown();
        _timer.Dispose();
        _lifetimeRegistration.Dispose();
        _sessionCancellation.Dispose();
    }

    private static ImmutableArray<LauncherChatMessage> Merge(ImmutableArray<LauncherChatMessage> existing, IEnumerable<LauncherChatMessage> added)
        => existing.Concat(added).GroupBy(message => message.Id).Select(group => group.Last()).OrderBy(message => message.Id).ToImmutableArray();
    private static bool BelongsTo(LauncherChatMessage message, uint owner, uint friend)
        => (message.SenderAccountId == owner && message.RecipientAccountId == friend)
            || (message.SenderAccountId == friend && message.RecipientAccountId == owner);
    private static void ValidateThread(IEnumerable<LauncherChatMessage> messages, uint owner, uint friend)
    { if (messages.Any(message => !BelongsTo(message, owner, friend))) throw new InvalidDataException("Invalid chat participants."); }
    private static void ValidateOwner(IEnumerable<LauncherChatMessage> messages, uint owner)
    { if (messages.Any(message => message.SenderAccountId != owner && message.RecipientAccountId != owner)) throw new InvalidDataException("Invalid chat owner."); }
    private static string ErrorCode(Exception error) => error switch
    {
        LauncherChatApiException api => api.Code,
        UnauthorizedAccessException or LauncherAuthException { StatusCode: HttpStatusCode.Unauthorized } => "chat-unauthorized",
        OperationCanceledException or TimeoutException => "chat-timeout",
        _ => "chat-unavailable"
    };
    private void LogFailure(string code) { try { _writeLog($"Messagerie Atlas : {code}."); } catch { } }
    private static void TryCancel(CancellationTokenSource cancellation) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
    private sealed record RequestGuard(long AuthSequence, uint OwnerAccountId, Guid SessionId, CancellationToken Token);
}
