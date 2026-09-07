using System.IO;
using System.Net;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

/// <summary>Owns the authenticated Messages workspace, independent of the selected page.</summary>
internal sealed partial class LauncherChatWorkspace : IDisposable
{
    private readonly object _sync = new();
    private readonly LauncherSessionCoordinator _session;
    private readonly ILauncherAuthService _authentication;
    private readonly ILauncherChatV2ApiClient _api;
    private readonly IChatWorkspaceStore _store;
    private readonly IChatAttachmentFileSource _files;
    private readonly TimeProvider _time;
    private readonly Action<string> _writeLog;
    private readonly CancellationToken _lifetimeToken;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private readonly SemaphoreSlim _localGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly HashSet<Task> _inFlight = [];
    private readonly Dictionary<string, CancellationTokenSource> _uploadCancellations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _uploadProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _threadAccessGenerations = new(StringComparer.Ordinal);
    private DateTimeOffset _lastProgressPublishAt;
    private CancellationTokenSource _sessionCancellation;
    private ChatWorkspaceSnapshot _current = new();
    private ChatWorkspaceLocalState _local = new();
    private long _sequence;
    private long _authSequence;
    private long _selectionVersion;
    private long _eventCursor;
    private long _preferencesVersion;
    private long _workerWakeVersion;
    private long _accessGeneration;
    private long _visibleThrough;
    private bool _viewActive;
    private bool _storageReady;
    private bool _started;
    private bool _stopping;
    private Task? _sessionLoop;
    private Task? _refreshTask;
    private Task? _outboxTask;
    private Task? _uploadTask;
    private Task? _readTask;
    private Task? _preferencesTask;
    private DateTimeOffset _lastTypingAt;
    private string? _lastTypingThread;
    private bool _lastTypingValue;
    private int _disposed;

    internal LauncherChatWorkspace(LauncherSessionCoordinator session, ILauncherAuthService authentication,
        ILauncherChatV2ApiClient api, IChatWorkspaceStore store, CancellationToken lifetimeToken,
        Action<string> writeLog, TimeProvider? timeProvider = null, IChatAttachmentFileSource? files = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _files = files ?? new ChatAttachmentFileSource(Path.Combine(LauncherSettings.SettingsDirectory, "messages-v2"),
            AtlasNetwork.LauncherApiBaseUri);
        _lifetimeToken = lifetimeToken;
        _writeLog = writeLog ?? throw new ArgumentNullException(nameof(writeLog));
        _time = timeProvider ?? TimeProvider.System;
        _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        ResetSession(_session.CurrentSnapshot);
        _session.SnapshotChanged += SessionChanged;
        _lifetimeRegistration = lifetimeToken.Register(BeginShutdown);
    }

    internal event EventHandler<ChatWorkspaceSnapshotEventArgs>? SnapshotChanged;
    internal ChatWorkspaceSnapshot CurrentSnapshot { get { lock (_sync) return _current; } }

    internal void Start()
    {
        lock (_sync)
        {
            if (_started || _stopping) return;
            _started = true;
            StartSessionLoopUnsafe();
        }
    }

    internal bool AcceptsAction(Guid sessionId, uint ownerAccountId, long sequence)
    {
        lock (_sync) return CaptureGuardUnsafe() is not null && sessionId == _current.SessionId
            && ownerAccountId == _current.OwnerAccountId && sequence >= 0 && sequence <= _current.Sequence;
    }

    internal Task RefreshAsync()
    {
        lock (_sync)
        {
            if (CaptureGuardUnsafe() is not Guard guard || !_started) return Task.CompletedTask;
            if (_refreshTask is { IsCompleted: false }) return _refreshTask;
            _refreshTask = TrackUnsafe(Task.Run(() => RefreshSafelyAsync(guard)));
            return _refreshTask;
        }
    }

    internal void SetViewActive(bool active)
    {
        bool refresh;
        lock (_sync)
        {
            refresh = active && !_viewActive;
            _viewActive = active;
            if (!active) _visibleThrough = 0;
        }
        if (refresh) _ = RefreshAsync();
    }

    internal void RefreshPresentation()
    {
        ChatWorkspaceSnapshot snapshot;
        lock (_sync) { if (_stopping) return; snapshot = SetSnapshotUnsafe(_current); }
        Publish(snapshot);
    }

    private void SessionChanged(object? sender, AuthSessionSnapshotEventArgs args) => ResetSession(args.Snapshot);

    private void ResetSession(AuthSessionSnapshot session)
    {
        CancellationTokenSource previous;
        ChatWorkspaceSnapshot snapshot;
        lock (_sync)
        {
            if (_stopping) return;
            previous = _sessionCancellation;
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
            _authSequence = session.Sequence;
            uint owner = session.IsAuthenticated ? _authentication.Session?.Profile.AccountId ?? 0 : 0;
            _storageReady = false;
            _local = new();
            _eventCursor = _visibleThrough = 0;
            _selectionVersion++;
            _preferencesVersion++;
            _workerWakeVersion++;
            _accessGeneration++;
            _threadAccessGenerations.Clear();
            _lastTypingAt = default;
            _lastTypingThread = null;
            _lastTypingValue = false;
            _sessionLoop = _refreshTask = _outboxTask = _uploadTask = _readTask = _preferencesTask = null;
            _uploadCancellations.Clear();
            _uploadProgress.Clear();
            _lastProgressPublishAt = default;
            snapshot = SetSnapshotUnsafe(new ChatWorkspaceSnapshot
            {
                SessionId = owner == 0 ? Guid.Empty : Guid.NewGuid(),
                OwnerAccountId = owner,
                IsLoading = owner > 0,
                State = new ChatStateDto { Self = new ChatProfileDto
                {
                    AccountId = owner,
                    Username = owner == 0 ? "" : _authentication.Session?.Profile.Username ?? ""
                } }
            });
            StartSessionLoopUnsafe();
        }
        TryCancel(previous);
        previous.Dispose();
        Publish(snapshot);
    }

    private void StartSessionLoopUnsafe()
    {
        if (!_started || _stopping || _sessionLoop is { IsCompleted: false } || CaptureGuardUnsafe() is not Guard guard) return;
        _sessionLoop = TrackUnsafe(Task.Run(() => RunSessionAsync(guard)));
    }

    private async Task RunSessionAsync(Guard guard)
    {
        try
        {
            await LoadLocalStateAsync(guard).ConfigureAwait(false);
            bool initialized = false;
            int failures = 0;
            DateTimeOffset lastDirectoryRefresh = default;
            while (IsCurrent(guard))
            {
                try
                {
                    if (!initialized)
                    {
                        await RefreshStateCoreAsync(guard, initializeCursor: true).ConfigureAwait(false);
                        initialized = true;
                        lastDirectoryRefresh = _time.GetUtcNow();
                    }
                    KickWorkers(guard);
                    long cursor;
                    lock (_sync) { EnsureCurrentUnsafe(guard); cursor = _eventCursor; }
                    await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
                    ChatEventsDto events = await _api.GetEventsAsync(cursor, 25, guard.Token).ConfigureAwait(false);
                    EnsureCurrent(guard);
                    if (events.RequiresResync)
                    {
                        await ResyncAsync(guard).ConfigureAwait(false);
                    }
                    else await ApplyEventsAsync(guard, events).ConfigureAwait(false);
                    failures = 0;
                    if (_time.GetUtcNow() - lastDirectoryRefresh >= TimeSpan.FromSeconds(30))
                    {
                        await RefreshStateCoreAsync(guard, initializeCursor: false).ConfigureAwait(false);
                        lastDirectoryRefresh = _time.GetUtcNow();
                    }
                    // A server that returns immediately without an event must not create a busy loop.
                    if (events.Events.Count == 0 && !events.HasMore)
                        await Task.Delay(TimeSpan.FromMilliseconds(150), _time, guard.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!IsCurrent(guard)) { break; }
                catch (LauncherChatV2ApiException error) when (!initialized
                    && error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
                {
                    Update(guard, current => current with { IsAvailable = false, IsLoading = false,
                        IsLegacyFallback = true, ErrorCode = "chat-v2-unavailable" });
                    return;
                }
                catch (Exception error)
                {
                    if (!HandleFailure(guard, error)) break;
                    failures = Math.Min(failures + 1, 5);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << failures)), _time, guard.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error) { HandleFailure(guard, error); }
    }

    private async Task LoadLocalStateAsync(Guard guard)
    {
        await _localGate.WaitAsync(guard.Token).ConfigureAwait(false);
        try
        {
            EnsureCurrent(guard);
            ChatWorkspaceLocalState loaded = await _store.LoadAsync<ChatWorkspaceLocalState>(guard.OwnerAccountId, guard.Token)
                .ConfigureAwait(false) ?? new();
            ValidateLocalState(loaded);
            loaded = loaded with
            {
                Preferences = loaded.Preferences with { MessageSoundEnabled = false },
                Outbox = loaded.Outbox.Select(item => item.Status == "sending" ? item with { Status = "queued" } : item).ToArray(),
                Uploads = loaded.Uploads.Select(item => item.Status is "uploading" or "preparing"
                    ? item with { Status = "queued" } : item).ToArray()
            };
            ChatWorkspaceSnapshot snapshot;
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                _local = loaded;
                _storageReady = true;
                snapshot = SetSnapshotUnsafe(_current with { State = _current.State with { Preferences = loaded.Preferences } });
            }
            Publish(snapshot);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            Update(guard, current => current with { IsLoading = false, ErrorCode = "chat-local-storage" });
            LogFailure("chat-local-storage", error);
            throw new ChatWorkspaceException("chat-local-storage");
        }
        finally { _localGate.Release(); }
    }

    private async Task RefreshSafelyAsync(Guard guard)
    {
        try
        {
            bool storageReady;
            lock (_sync) storageReady = _storageReady;
            if (!storageReady) return;
            await RefreshStateCoreAsync(guard, initializeCursor: false).ConfigureAwait(false);
            lock (_sync) if (IsCurrentUnsafe(guard)) StartSessionLoopUnsafe();
            KickWorkers(guard);
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error) { HandleFailure(guard, error); }
    }

    private async Task RefreshStateCoreAsync(Guard guard, bool initializeCursor)
    {
        long accessGeneration;
        long preferencesVersion;
        lock (_sync) { EnsureCurrentUnsafe(guard); accessGeneration = _accessGeneration; preferencesVersion = _preferencesVersion; }
        await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
        ChatStateDto state = await _api.GetStateAsync(guard.Token).ConfigureAwait(false);
        EnsureCurrent(guard);
        if (state.Self.AccountId != guard.OwnerAccountId) throw new InvalidDataException("Messages state belongs to another account.");
        ChatWorkspaceSnapshot snapshot;
        lock (_sync)
        {
            EnsureCurrentUnsafe(guard);
            // A concurrent event has already installed a newer directory. In
            // particular, do not drop a just-created thread or restore old preferences.
            if (state.EventCursor < _eventCursor || accessGeneration != _accessGeneration) return;
            ChatPreferencesDto preferences = _local.HasPreferences && (_local.PreferencesPending || preferencesVersion != _preferencesVersion)
                ? _local.Preferences : state.Preferences;
            if (initializeCursor) _eventCursor = state.EventCursor;
            IReadOnlyList<ChatThreadDto> threads = state.Threads.Select(incoming =>
            {
                ChatThreadDto? existing = _current.State.Threads.FirstOrDefault(thread => thread.Id == incoming.Id);
                return existing is not null && existing.Version > incoming.Version ? existing : incoming;
            }).ToArray();
            bool hasSelection = _current.SelectedThreadId is not string selected || threads.Any(thread => thread.Id == selected);
            snapshot = SetSnapshotUnsafe(_current with
            {
                State = state with { Threads = threads, Preferences = preferences with { MessageSoundEnabled = false }, EventCursor = _eventCursor },
                IsAvailable = true, IsLoading = false, IsLegacyFallback = false, ErrorCode = "",
                SelectedThreadId = hasSelection ? _current.SelectedThreadId : null,
                Messages = hasSelection ? _current.Messages : [],
                HasEarlier = hasSelection && _current.HasEarlier
            });
        }
        Publish(snapshot);
        await PersistRemotePreferencesAsync(guard, state.Preferences, preferencesVersion).ConfigureAwait(false);
    }

    private async Task ResyncAsync(Guard guard)
    {
        await RefreshStateCoreAsync(guard, initializeCursor: true).ConfigureAwait(false);
        string? selected;
        lock (_sync) selected = IsCurrentUnsafe(guard) ? _current.SelectedThreadId : null;
        if (selected is not null) await OpenThreadCoreAsync(guard, selected).ConfigureAwait(false);
    }

    private async Task EnsureAuthenticatedAsync(Guard guard)
    {
        EnsureCurrent(guard);
        bool authenticated = await _authentication.EnsureFreshAsync(guard.Token).ConfigureAwait(false);
        EnsureCurrent(guard);
        if (!authenticated)
        {
            _session.NotifyAuthenticatedRequestUnauthorized(guard.AuthSequence, guard.Token);
            throw new LauncherChatV2ApiException(HttpStatusCode.Unauthorized, "chat-unauthorized");
        }
    }

    private async Task MutateLocalAsync(Guard guard, Func<ChatWorkspaceLocalState, ChatWorkspaceLocalState> mutate)
    {
        await _localGate.WaitAsync(guard.Token).ConfigureAwait(false);
        try
        {
            ChatWorkspaceLocalState next;
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                if (!_storageReady) throw new ChatWorkspaceException("chat-local-storage");
                next = mutate(_local);
                if (ReferenceEquals(next, _local)) return;
            }
            await _store.SaveAsync(guard.OwnerAccountId, next, guard.Token).ConfigureAwait(false);
            ChatWorkspaceSnapshot snapshot;
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                _local = next;
                snapshot = SetSnapshotUnsafe(_current);
            }
            Publish(snapshot);
        }
        catch (OperationCanceledException) { throw; }
        catch (ChatWorkspaceException) { throw; }
        catch (Exception error)
        {
            LogFailure("chat-local-storage", error);
            Update(guard, current => current with { ErrorCode = "chat-local-storage" });
            throw new ChatWorkspaceException("chat-local-storage");
        }
        finally { _localGate.Release(); }
    }

    private ChatWorkspaceSnapshot SetSnapshotUnsafe(ChatWorkspaceSnapshot snapshot)
    {
        ChatPreferencesDto preferences = _local.HasPreferences ? _local.Preferences : snapshot.State.Preferences;
        return _current = snapshot with
        {
            Sequence = ++_sequence,
            State = snapshot.State with { Preferences = preferences with { MessageSoundEnabled = false }, EventCursor = _eventCursor },
            Drafts = _local.Drafts.ToArray(),
            Draft = _local.Drafts.FirstOrDefault(draft => draft.ThreadId == snapshot.SelectedThreadId),
            Outbox = _local.Outbox.ToArray(),
            Uploads = _local.Uploads.Select(upload => new ChatWorkspaceUpload
            {
                LocalId = upload.LocalId, ThreadId = upload.ThreadId, FileName = upload.FileName,
                ContentType = upload.ContentType, Size = upload.Size,
                Offset = upload.Status == "uploading" && _uploadProgress.TryGetValue(upload.LocalId, out long progress)
                    ? Math.Max(upload.Offset, Math.Min(upload.Size, progress)) : upload.Offset,
                Status = upload.Status, ErrorCode = upload.ErrorCode, Attachment = upload.Attachment
            }).ToArray(),
            Typing = snapshot.Typing.Where(typing => typing.ExpiresAt > _time.GetUtcNow()).ToArray()
        };
    }

    private void Update(Guard guard, Func<ChatWorkspaceSnapshot, ChatWorkspaceSnapshot> update)
    {
        ChatWorkspaceSnapshot? snapshot = null;
        lock (_sync) if (IsCurrentUnsafe(guard)) snapshot = SetSnapshotUnsafe(update(_current));
        Publish(snapshot);
    }

    private void Publish(ChatWorkspaceSnapshot? snapshot)
    {
        if (snapshot is not null && Volatile.Read(ref _disposed) == 0)
            SnapshotChanged?.Invoke(this, new ChatWorkspaceSnapshotEventArgs(snapshot));
    }

    private Guard RequireGuard()
    {
        lock (_sync) return CaptureGuardUnsafe() ?? throw new ChatWorkspaceException("chat-unauthorized");
    }

    private Guard? CaptureGuardUnsafe() => !_stopping && _current.OwnerAccountId > 0 && _session.CurrentSnapshot.IsAuthenticated
        ? new Guard(_authSequence, _current.OwnerAccountId, _current.SessionId, _sessionCancellation.Token) : null;

    private bool IsCurrentUnsafe(Guard guard) => !_stopping && !guard.Token.IsCancellationRequested
        && _current.OwnerAccountId == guard.OwnerAccountId && _current.SessionId == guard.SessionId
        && _session.CurrentSnapshot.Sequence == guard.AuthSequence && _session.CurrentSnapshot.IsAuthenticated
        && _authentication.Session?.Profile.AccountId == guard.OwnerAccountId;

    private bool IsCurrent(Guard guard) { lock (_sync) return IsCurrentUnsafe(guard); }
    private void EnsureCurrent(Guard guard) { lock (_sync) EnsureCurrentUnsafe(guard); }
    private void EnsureCurrentUnsafe(Guard guard)
    {
        if (!IsCurrentUnsafe(guard)) throw new OperationCanceledException(guard.Token);
    }

    private bool HandleFailure(Guard guard, Exception error)
    {
        if (!IsCurrent(guard)) return false;
        string code = ErrorCode(error);
        if (code == "chat-unauthorized")
        {
            _session.NotifyAuthenticatedRequestUnauthorized(guard.AuthSequence, guard.Token);
            return false;
        }
        Update(guard, current => current with { IsAvailable = false, IsLoading = false,
            IsLoadingEarlier = false, ErrorCode = code });
        LogFailure(code, error);
        return true;
    }

    private static string ErrorCode(Exception error) => error switch
    {
        LauncherChatV2ApiException api => api.Code,
        ChatWorkspaceException workspace => workspace.Code,
        OperationCanceledException or TimeoutException => "chat-timeout",
        UnauthorizedAccessException or LauncherAuthException { StatusCode: HttpStatusCode.Unauthorized } => "chat-unauthorized",
        _ => "chat-unavailable"
    };

    private void LogFailure(string code, Exception error)
    {
        try { _writeLog($"Messages Atlas : {code}; category={error.GetType().Name}."); }
        catch { }
    }

    private Task Track(Task task) { lock (_sync) return TrackUnsafe(task); }
    private Task<T> Track<T>(Task<T> task) { lock (_sync) { TrackUnsafe(task); return task; } }
    private Task TrackUnsafe(Task task)
    {
        _inFlight.Add(task);
        _ = task.ContinueWith(completed => { lock (_sync) _inFlight.Remove(completed); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    internal void BeginShutdown()
    {
        CancellationTokenSource cancellation;
        lock (_sync)
        {
            if (_stopping) return;
            _stopping = true;
            cancellation = _sessionCancellation;
        }
        TryCancel(cancellation);
    }

    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_sync) pending = _inFlight.ToArray();
        try { await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return true; }
        catch { return true; } // Callers receive operation errors; shutdown still observes their completion.
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.SnapshotChanged -= SessionChanged;
        BeginShutdown();
        _lifetimeRegistration.Dispose();
        _sessionCancellation.Dispose();
    }

    private static void ValidateLocalState(ChatWorkspaceLocalState state)
    {
        if (state.Drafts is null || state.Outbox is null || state.Uploads is null || state.Preferences is null
            || state.Drafts.Count > 10000 || state.Outbox.Count > 10000 || state.Uploads.Count > 10000)
            throw new InvalidDataException("Invalid Messages workspace.");
        foreach (ChatWorkspaceDraft draft in state.Drafts)
            if (draft is null || string.IsNullOrWhiteSpace(draft.ThreadId) || draft.Body is null
                || draft.Body.Length > ChatLimits.MaximumMessageCharacters || draft.AttachmentIds is null
                || draft.AttachmentIds.Count > ChatLimits.MaximumAttachmentsPerMessage)
                throw new InvalidDataException("Invalid Messages draft.");
        foreach (ChatOutboxEntry item in state.Outbox)
            if (item is null || item.ClientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(item.ThreadId)
                || item.Body is null || item.Body.Length > ChatLimits.MaximumMessageCharacters || item.AttachmentIds is null
                || item.AttachmentIds.Count > ChatLimits.MaximumAttachmentsPerMessage)
                throw new InvalidDataException("Invalid Messages outbox.");
        foreach (ChatLocalUpload upload in state.Uploads)
            if (upload is null || string.IsNullOrWhiteSpace(upload.LocalId) || string.IsNullOrWhiteSpace(upload.ThreadId)
                || upload.Size is <= 0 or > ChatLimits.MaximumAttachmentBytes || !Path.IsPathFullyQualified(upload.SourcePath)
                || upload.Offset < 0 || upload.Offset > upload.Size)
                throw new InvalidDataException("Invalid Messages attachment metadata.");
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed record Guard(long AuthSequence, uint OwnerAccountId, Guid SessionId, CancellationToken Token);
}
