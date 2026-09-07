using System.Net;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherChatWorkspace
{
    private async Task ApplyEventsAsync(Guard guard, ChatEventsDto page)
    {
        foreach (ChatEventDto item in page.Events.OrderBy(item => item.Id))
        {
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                if (item.Id <= _eventCursor) continue;
            }
            if (item.Kind == "access" && item.ThreadId is string inaccessible)
                await InvalidateThreadAsync(guard, inaccessible).ConfigureAwait(false);
            else
            {
                if (item.Thread is not null) ApplyThread(guard, item.Thread);
                if (item.Message is not null)
                {
                    bool known;
                    lock (_sync) known = IsCurrentUnsafe(guard) && _current.State.Threads.Any(thread => thread.Id == item.Message.ThreadId);
                    if (!known) await RefreshStateCoreAsync(guard, initializeCursor: false).ConfigureAwait(false);
                    ApplyMessage(guard, item.Message);
                    if (item.Message.Sender.AccountId == guard.OwnerAccountId)
                        await ConfirmOutboxMessageAsync(guard, item.Message).ConfigureAwait(false);
                }
                if (item.Preferences is not null) await PersistRemotePreferencesAsync(guard, item.Preferences).ConfigureAwait(false);
                if (item.Typing is ChatTypingDto typing && typing.AccountId != guard.OwnerAccountId)
                    Update(guard, current => current with { Typing = current.Typing.Where(existing =>
                        existing.ThreadId != typing.ThreadId || existing.AccountId != typing.AccountId).Append(typing).ToArray() });
            }
            lock (_sync) { EnsureCurrentUnsafe(guard); _eventCursor = Math.Max(_eventCursor, item.Id); }
        }
        lock (_sync) { EnsureCurrentUnsafe(guard); _eventCursor = Math.Max(_eventCursor, page.EventCursor); }
        Update(guard, current => current with { IsAvailable = true, IsLoading = false, IsLegacyFallback = false,
            ErrorCode = current.ErrorCode is "chat-unavailable" or "chat-timeout" or "chat-rate-limited" ? "" : current.ErrorCode });
        KickWorkers(guard);
    }

    private void ApplyThread(Guard guard, ChatThreadDto thread, long? accessGeneration = null, bool requireKnown = false)
    {
        Update(guard, current => accessGeneration is long generation && _threadAccessGenerations.GetValueOrDefault(thread.Id) != generation
            || requireKnown && !current.State.Threads.Any(item => item.Id == thread.Id) ? current
            : current with { State = current.State with { Threads = MergeThread(current.State.Threads, thread) } });
    }

    private void ApplyMessage(Guard guard, ChatMessageDto message, long? accessGeneration = null)
    {
        Update(guard, current =>
        {
            if (!current.State.Threads.Any(thread => thread.Id == message.ThreadId)
                || accessGeneration is long generation && _threadAccessGenerations.GetValueOrDefault(message.ThreadId) != generation) return current;
            return current with
            {
                Messages = current.SelectedThreadId == message.ThreadId ? MergeMessages(current.Messages, [message]) : current.Messages,
                State = current.State with { Threads = current.State.Threads.Select(thread =>
                {
                    if (thread.Id != message.ThreadId) return thread;
                    ChatMessageDto? last = thread.LastMessage;
                    bool useLast = last is null || message.Id > last.Id || message.Id == last.Id && message.Version >= last.Version;
                    IReadOnlyList<ChatMessageDto> pins = message.IsPinned && message.DeletedAt is null
                        ? MergeMessages(thread.PinnedMessages, [message])
                        : thread.PinnedMessages.Where(pin => pin.Id != message.Id).ToArray();
                    return thread with { LastMessage = useLast ? message : last, PinnedMessages = pins };
                }).ToArray() }
            };
        });
    }

    private async Task ConfirmOutboxMessageAsync(Guard guard, ChatMessageDto message)
    {
        if (message.Sender.AccountId != guard.OwnerAccountId) return;
        await MutateLocalAsync(guard, state =>
        {
            ChatOutboxEntry? entry = state.Outbox.FirstOrDefault(item => item.ClientMessageId == message.ClientMessageId
                && item.ThreadId == message.ThreadId && item.Status != "sent");
            return entry is null ? state : state with { Outbox = state.Outbox.Select(item => ReferenceEquals(item, entry)
                ? item with { Status = "sent", ErrorCode = "", WasSubmitted = true } : item).ToArray() };
        }).ConfigureAwait(false);
        await CleanupUnreferencedUploadsAsync(guard).ConfigureAwait(false);
    }

    private async Task InvalidateThreadAsync(Guard guard, string threadId)
    {
        List<CancellationTokenSource> cancellations;
        lock (_sync)
        {
            EnsureCurrentUnsafe(guard);
            _threadAccessGenerations[threadId] = ++_accessGeneration;
            cancellations = _local.Uploads.Where(upload => upload.ThreadId == threadId)
                .Select(upload => _uploadCancellations.GetValueOrDefault(upload.LocalId)).OfType<CancellationTokenSource>().ToList();
            if (_current.SelectedThreadId == threadId) { _selectionVersion++; _visibleThrough = 0; }
        }
        foreach (CancellationTokenSource cancellation in cancellations) TryCancel(cancellation);
        Update(guard, current => current with
        {
            State = current.State with { Threads = current.State.Threads.Where(thread => thread.Id != threadId).ToArray() },
            SelectedThreadId = current.SelectedThreadId == threadId ? null : current.SelectedThreadId,
            Messages = current.SelectedThreadId == threadId ? [] : current.Messages,
            HasEarlier = current.SelectedThreadId != threadId && current.HasEarlier,
            IsLoading = false, IsLoadingEarlier = false,
            Typing = current.Typing.Where(typing => typing.ThreadId != threadId).ToArray()
        });
        await MutateLocalAsync(guard, state => state with
        {
            Outbox = state.Outbox.Select(item => item.ThreadId == threadId && item.Status is not ("sent" or "cancelled")
                ? item with { Status = "failed", ErrorCode = "chat-forbidden" } : item).ToArray(),
            Uploads = state.Uploads.Select(item => item.ThreadId == threadId && item.Status is not ("complete" or "cancelled")
                ? item with { Status = "failed", ErrorCode = "chat-forbidden" } : item).ToArray()
        }).ConfigureAwait(false);
    }

    private void KickWorkers(Guard guard, bool wake = true)
    {
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard) || !_current.IsAvailable || !_storageReady) return;
            if (wake) _workerWakeVersion++;
            if (_preferencesTask is not { IsCompleted: false } && _local.PreferencesPending)
                StartWorkerUnsafe(guard, "preferences", () => FlushPreferencesAsync(guard));
            if (_uploadTask is not { IsCompleted: false } && _local.Uploads.Any(upload => upload.Status == "queued"))
                StartWorkerUnsafe(guard, "uploads", () => DrainUploadsAsync(guard));
            if (_outboxTask is not { IsCompleted: false } && _local.Outbox.Any(item => item.Status is "queued" or "uploading"))
                StartWorkerUnsafe(guard, "outbox", () => DrainOutboxAsync(guard));
        }
    }

    private void StartWorkerUnsafe(Guard guard, string kind, Func<Task> action)
    {
        long wakeVersion = _workerWakeVersion;
        Task task = TrackUnsafe(Task.Run(action));
        if (kind == "preferences") _preferencesTask = task;
        else if (kind == "uploads") _uploadTask = task;
        else _outboxTask = task;
        _ = task.ContinueWith(completed =>
        {
            lock (_sync)
            {
                if (!IsCurrentUnsafe(guard)) return;
                if (kind == "preferences" && ReferenceEquals(_preferencesTask, completed)) _preferencesTask = null;
                else if (kind == "uploads" && ReferenceEquals(_uploadTask, completed)) _uploadTask = null;
                else if (kind == "outbox" && ReferenceEquals(_outboxTask, completed)) _outboxTask = null;
                if (_workerWakeVersion != wakeVersion) KickWorkers(guard, wake: false);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainOutboxAsync(Guard guard)
    {
        HashSet<Guid> waiting = [];
        try
        {
            while (IsCurrent(guard))
            {
                ChatOutboxEntry? entry;
                lock (_sync)
                {
                    if (!IsCurrentUnsafe(guard) || !_current.IsAvailable) return;
                    entry = _local.Outbox.Where(item => item.Status is "queued" or "uploading")
                        .OrderBy(item => item.CreatedAt).GroupBy(item => item.ThreadId)
                        .Select(group => group.First()).FirstOrDefault(item => !waiting.Contains(item.ClientMessageId));
                }
                if (entry is null) return;
                bool ready = false;
                bool cancelled = false;
                await MutateLocalAsync(guard, state =>
                {
                    ChatOutboxEntry? current = state.Outbox.FirstOrDefault(item => item.ClientMessageId == entry.ClientMessageId);
                    if (current is null || current.Status is not ("queued" or "uploading")) { cancelled = true; return state; }
                    lock (_sync)
                    {
                        ChatThreadDto? thread = _current.State.Threads.FirstOrDefault(item => item.Id == current.ThreadId);
                        if (thread?.CanSend != true)
                            return state with { Outbox = state.Outbox.Select(item => item.ClientMessageId == entry.ClientMessageId
                                ? item with { Status = "failed", ErrorCode = "chat-cannot-send" } : item).ToArray() };
                    }
                    List<ChatLocalUpload> attachments = current.AttachmentIds.Select(id => state.Uploads.FirstOrDefault(upload =>
                        upload.LocalId == id || upload.Attachment?.Id == id)).OfType<ChatLocalUpload>().ToList();
                    if (attachments.Count != current.AttachmentIds.Count || attachments.Any(upload => upload.Status is "failed" or "cancelled"))
                        return state with { Outbox = state.Outbox.Select(item => item.ClientMessageId == entry.ClientMessageId
                            ? item with { Status = "failed", ErrorCode = "chat-attachment-failed" } : item).ToArray() };
                    if (attachments.Any(upload => upload.Status != "complete" || upload.Attachment is null))
                        return state with { Outbox = state.Outbox.Select(item => item.ClientMessageId == entry.ClientMessageId
                            ? item with { Status = "uploading", ErrorCode = "" } : item).ToArray() };
                    ready = true;
                    return state;
                }).ConfigureAwait(false);
                if (cancelled) continue;
                if (!ready) { waiting.Add(entry.ClientMessageId); continue; }
                try
                {
                    long sendAccessGeneration = 0;
                    ChatSendMessageResult result = await CommandAsync(guard, async token =>
                    {
                        ChatSendMessageRequest? request = null;
                        await MutateLocalAsync(guard, state =>
                        {
                            ChatOutboxEntry? current = state.Outbox.FirstOrDefault(item => item.ClientMessageId == entry.ClientMessageId);
                            if (current is null || current.Status is not ("queued" or "uploading")) return state;
                            RequireThreadUnsafe(guard, current.ThreadId, canSend: true);
                            sendAccessGeneration = _threadAccessGenerations.GetValueOrDefault(current.ThreadId);
                            ChatLocalUpload[] attachments = current.AttachmentIds.Select(id => state.Uploads.FirstOrDefault(upload =>
                                upload.LocalId == id || upload.Attachment?.Id == id)).OfType<ChatLocalUpload>().ToArray();
                            if (attachments.Length != current.AttachmentIds.Count || attachments.Any(upload => upload.Status != "complete" || upload.Attachment is null))
                                throw new ChatWorkspaceException("chat-attachment-failed");
                            request = new ChatSendMessageRequest { ClientMessageId = current.ClientMessageId, Body = current.Body,
                                ReplyToMessageId = current.ReplyToMessageId, AttachmentIds = attachments.Select(upload => upload.Attachment!.Id).ToArray(), Card = current.Card };
                            return state with { Outbox = state.Outbox.Select(item => item.ClientMessageId == entry.ClientMessageId
                                ? item with { Status = "sending", ErrorCode = "", WasSubmitted = true } : item).ToArray() };
                        }).ConfigureAwait(false);
                        if (request is null) throw new ChatWorkspaceException("chat-send-cancelled");
                        return await _api.SendAsync(entry.ThreadId, request, token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                    if (result.Message.Sender.AccountId != guard.OwnerAccountId)
                        throw new ChatWorkspaceException("chat-invalid-response");
                    await ConfirmOutboxMessageAsync(guard, result.Message).ConfigureAwait(false);
                    ApplyMessage(guard, result.Message, sendAccessGeneration);
                }
                catch (OperationCanceledException) when (!IsCurrent(guard)) { return; }
                catch (Exception error)
                {
                    bool transient = error is System.Net.Http.HttpRequestException or TimeoutException or OperationCanceledException
                        || error is LauncherChatV2ApiException api && api.IsTransient;
                    await MutateLocalAsync(guard, state => state with { Outbox = state.Outbox.Select(item =>
                        item.ClientMessageId == entry.ClientMessageId && item.Status is not ("sent" or "cancelled")
                            ? item with { Status = transient ? "queued" : "failed", ErrorCode = ErrorCode(error) } : item).ToArray() }).ConfigureAwait(false);
                    if (ErrorCode(error) != "chat-send-cancelled") ApplyOperationFailure(guard, error);
                    if (transient) return;
                }
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error) { ApplyOperationFailure(guard, error); }
    }

    private static IReadOnlyList<ChatThreadDto> MergeThread(IReadOnlyList<ChatThreadDto> existing, ChatThreadDto incoming)
    {
        ChatThreadDto? previous = existing.FirstOrDefault(thread => thread.Id == incoming.Id);
        ChatThreadDto merged = previous is not null && previous.Version > incoming.Version ? previous : incoming;
        if (previous is not null)
        {
            long lastRead = Math.Max(previous.LastReadMessageId, incoming.LastReadMessageId);
            merged = merged with { LastReadMessageId = lastRead,
                UnreadCount = merged.LastMessage is not null && merged.LastMessage.Id <= lastRead ? 0 : merged.UnreadCount };
        }
        return existing.Where(thread => thread.Id != incoming.Id).Append(merged).ToArray();
    }

    private static IReadOnlyList<ChatMessageDto> MergeMessages(IReadOnlyList<ChatMessageDto> existing, IEnumerable<ChatMessageDto> incoming)
    {
        Dictionary<long, ChatMessageDto> messages = existing.ToDictionary(message => message.Id);
        foreach (ChatMessageDto message in incoming)
        {
            if (!messages.TryGetValue(message.Id, out ChatMessageDto? previous) || message.Version >= previous.Version)
                messages[message.Id] = message;
        }
        return messages.Values.OrderBy(message => message.Id).ToArray();
    }
}
