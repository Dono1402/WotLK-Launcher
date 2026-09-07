using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherChatWorkspace
{
    internal Task OpenThreadAsync(string threadId)
        => Track(OpenThreadCoreAsync(RequireGuard(), threadId));

    private async Task OpenThreadCoreAsync(Guard guard, string threadId)
    {
        _ = LauncherChatV2ApiClient.Identifier(threadId);
        long selection;
        bool invited;
        ChatWorkspaceSnapshot snapshot;
        lock (_sync)
        {
            ChatThreadDto thread = RequireThreadUnsafe(guard, threadId);
            selection = ++_selectionVersion;
            _visibleThrough = 0;
            invited = thread.IsInvited;
            snapshot = SetSnapshotUnsafe(_current with { SelectedThreadId = threadId,
                Messages = [], HasEarlier = false, IsLoading = !invited, IsLoadingEarlier = false, ErrorCode = "" });
        }
        Publish(snapshot);
        if (invited) return;
        try
        {
            await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
            ChatMessagesPageDto page = await _api.GetMessagesAsync(threadId, null, guard.Token).ConfigureAwait(false);
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                if (_selectionVersion != selection || _current.SelectedThreadId != threadId) return;
                snapshot = SetSnapshotUnsafe(_current with
                {
                    State = _current.State with { Threads = MergeThread(_current.State.Threads, page.Thread) },
                    Messages = MergeMessages(page.Messages, _current.Messages), HasEarlier = page.HasEarlier,
                    IsLoading = false, IsLoadingEarlier = false, IsAvailable = true, ErrorCode = ""
                });
            }
            Publish(snapshot);
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error)
        {
            lock (_sync) if (IsCurrentUnsafe(guard) && _selectionVersion != selection) return;
            ApplyOperationFailure(guard, error);
            if (error is LauncherChatV2ApiException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound })
                await InvalidateThreadAsync(guard, threadId).ConfigureAwait(false);
            throw;
        }
    }

    internal Task OpenDirectThreadAsync(uint accountId)
    {
        Guard guard = RequireGuard();
        if (accountId == 0 || accountId == guard.OwnerAccountId) throw new ChatWorkspaceException("chat-invalid-request");
        string? existing;
        lock (_sync)
        {
            EnsureCurrentUnsafe(guard);
            existing = _current.State.Threads.FirstOrDefault(thread => thread.Kind == "direct"
                && thread.Members.Any(member => member.Profile.AccountId == accountId))?.Id;
        }
        return existing is not null ? Track(OpenThreadCoreAsync(guard, existing))
            : Track(CreateAndOpenThreadAsync(guard, new ChatCreateThreadRequest
            { RequestId = Guid.NewGuid(), ParticipantAccountIds = [accountId] }));
    }

    internal Task<ChatThreadDto> CreateThreadAsync(ChatCreateThreadRequest request)
        => Track(CreateAndOpenThreadAsync(RequireGuard(), request));

    private async Task<ChatThreadDto> CreateAndOpenThreadAsync(Guard guard, ChatCreateThreadRequest request)
    {
        long accessGeneration;
        lock (_sync) { EnsureCurrentUnsafe(guard); accessGeneration = _accessGeneration; }
        if (request.RequestId == Guid.Empty) request = request with { RequestId = Guid.NewGuid() };
        ChatThreadDto thread = await CommandAsync(guard, token => _api.CreateThreadAsync(request, token)).ConfigureAwait(false);
        long threadAccessGeneration;
        lock (_sync)
        {
            EnsureCurrentUnsafe(guard);
            if (_threadAccessGenerations.GetValueOrDefault(thread.Id) > accessGeneration) throw new ChatWorkspaceException("chat-forbidden");
            threadAccessGeneration = _threadAccessGenerations.GetValueOrDefault(thread.Id);
        }
        ApplyThread(guard, thread, threadAccessGeneration);
        await OpenThreadCoreAsync(guard, thread.Id).ConfigureAwait(false);
        return thread;
    }

    internal Task LoadEarlierAsync(string threadId, long beforeId)
        => Track(LoadEarlierCoreAsync(RequireGuard(), threadId, beforeId));

    private async Task LoadEarlierCoreAsync(Guard guard, string threadId, long beforeId)
    {
        long selection;
        lock (_sync)
        {
            RequireThreadUnsafe(guard, threadId, selected: true);
            if (!_current.HasEarlier || _current.IsLoadingEarlier || _current.IsLoading || beforeId <= 0
                || _current.Messages.Count == 0 || _current.Messages.Min(message => message.Id) != beforeId) return;
            selection = _selectionVersion;
        }
        Update(guard, current => current with { IsLoadingEarlier = true, ErrorCode = "" });
        try
        {
            await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
            ChatMessagesPageDto page = await _api.GetMessagesAsync(threadId, beforeId, guard.Token).ConfigureAwait(false);
            Update(guard, current => _selectionVersion == selection && current.SelectedThreadId == threadId
                ? current with { Messages = MergeMessages(current.Messages, page.Messages), HasEarlier = page.HasEarlier,
                    IsLoadingEarlier = false, IsAvailable = true } : current);
        }
        catch (Exception error)
        {
            lock (_sync) if (IsCurrentUnsafe(guard) && selection != _selectionVersion) return;
            ApplyOperationFailure(guard, error);
            throw;
        }
    }

    internal Task SaveDraftAsync(ChatWorkspaceDraft draft)
    {
        Guard guard = RequireGuard();
        lock (_sync) RequireThreadUnsafe(guard, draft.ThreadId, selected: true);
        if (draft.Body is null || draft.Body.Length > ChatLimits.MaximumMessageCharacters || draft.ReplyToMessageId <= 0)
            throw new ChatWorkspaceException("chat-invalid-message");
        return Track(MutateLocalAsync(guard, state =>
        {
            ChatWorkspaceDraft? previous = state.Drafts.FirstOrDefault(item => item.ThreadId == draft.ThreadId);
            ChatWorkspaceDraft next = draft with
            {
                AttachmentIds = previous?.AttachmentIds ?? [], UpdatedAt = _time.GetUtcNow()
            };
            return state with { Drafts = state.Drafts.Where(item => item.ThreadId != draft.ThreadId).Append(next).ToArray() };
        }));
    }

    internal Task SetDraftCardAsync(string threadId, ChatCardDto? card)
    {
        Guard guard = RequireGuard();
        lock (_sync) RequireThreadUnsafe(guard, threadId, selected: true, canSend: true);
        return Track(MutateLocalAsync(guard, state =>
        {
            // Read the latest draft after acquiring the persistence gate. A
            // renderer text save may still be in progress when a card is chosen.
            RequireThreadUnsafe(guard, threadId, selected: true, canSend: true);
            ChatWorkspaceDraft draft = state.Drafts.FirstOrDefault(item => item.ThreadId == threadId)
                ?? new ChatWorkspaceDraft { ThreadId = threadId };
            ChatWorkspaceDraft next = draft with { Card = card, UpdatedAt = _time.GetUtcNow() };
            return state with { Drafts = state.Drafts.Where(item => item.ThreadId != threadId).Append(next).ToArray() };
        }));
    }

    internal Task<ChatOutboxEntry> QueueSendAsync(string threadId, ChatSendMessageRequest request)
    {
        Guard guard = RequireGuard();
        lock (_sync) RequireThreadUnsafe(guard, threadId, selected: true, canSend: true);
        if (request.ClientMessageId == Guid.Empty) request = request with { ClientMessageId = Guid.NewGuid() };
        request = request with { Body = request.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim(),
            AttachmentIds = request.AttachmentIds.ToArray() };
        LauncherChatV2ApiClient.ValidateSend(request);
        return Track(QueueSendCoreAsync(guard, threadId, request));
    }

    private async Task<ChatOutboxEntry> QueueSendCoreAsync(Guard guard, string threadId, ChatSendMessageRequest request)
    {
        ChatOutboxEntry? queued = null;
        await MutateLocalAsync(guard, state =>
        {
            lock (_sync) RequireThreadUnsafe(guard, threadId, canSend: true);
            ChatOutboxEntry? duplicate = state.Outbox.FirstOrDefault(item => item.ClientMessageId == request.ClientMessageId);
            if (duplicate is not null)
            {
                if (duplicate.ThreadId != threadId || duplicate.Body != request.Body || duplicate.ReplyToMessageId != request.ReplyToMessageId
                    || !duplicate.AttachmentIds.SequenceEqual(request.AttachmentIds) || !SameCard(duplicate.Card, request.Card))
                    throw new ChatWorkspaceException("chat-idempotency-conflict");
                queued = duplicate;
                return state;
            }
            foreach (string attachmentId in request.AttachmentIds)
            {
                ChatLocalUpload? upload = state.Uploads.FirstOrDefault(item => item.LocalId == attachmentId || item.Attachment?.Id == attachmentId);
                if (upload is null || upload.ThreadId != threadId || upload.Status == "cancelled")
                    throw new ChatWorkspaceException("chat-invalid-attachment");
            }
            if (state.Outbox.Count(item => item.Status is not ("sent" or "cancelled")) >= 1000)
                throw new ChatWorkspaceException("chat-outbox-full");
            queued = new ChatOutboxEntry { ClientMessageId = request.ClientMessageId, ThreadId = threadId,
                Body = request.Body, ReplyToMessageId = request.ReplyToMessageId, AttachmentIds = request.AttachmentIds,
                Card = request.Card, CreatedAt = _time.GetUtcNow() };
            ChatWorkspaceDraft? draft = state.Drafts.FirstOrDefault(item => item.ThreadId == threadId);
            bool matchingDraft = draft is not null && draft.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() == request.Body
                && draft.ReplyToMessageId == request.ReplyToMessageId && SameCard(draft.Card, request.Card);
            return state with
            {
                Outbox = state.Outbox.Where(item => item.Status != "sent").Append(queued).ToArray(),
                Drafts = matchingDraft ? state.Drafts.Where(item => item.ThreadId != threadId).ToArray() : state.Drafts
            };
        }).ConfigureAwait(false);
        KickWorkers(guard);
        return queued ?? throw new ChatWorkspaceException("chat-send-failed");
    }

    internal Task CancelSendAsync(Guid clientMessageId)
        => Track(CancelSendCoreAsync(RequireGuard(), clientMessageId));

    private async Task CancelSendCoreAsync(Guard guard, Guid clientMessageId)
    {
        await MutateLocalAsync(guard, state =>
        {
            ChatOutboxEntry entry = state.Outbox.FirstOrDefault(item => item.ClientMessageId == clientMessageId)
                ?? throw new ChatWorkspaceException("chat-not-found");
            if (entry.WasSubmitted || entry.Status is "sending" or "sent")
                throw new ChatWorkspaceException("chat-send-already-started");
            return state with { Outbox = state.Outbox.Select(item => item.ClientMessageId == clientMessageId
                ? item with { Status = "cancelled", ErrorCode = "" } : item).ToArray() };
        }).ConfigureAwait(false);
        await CleanupUnreferencedUploadsAsync(guard).ConfigureAwait(false);
    }

    internal Task RetrySendAsync(Guid clientMessageId)
        => Track(RetrySendCoreAsync(RequireGuard(), clientMessageId));

    private async Task RetrySendCoreAsync(Guard guard, Guid clientMessageId)
    {
        await MutateLocalAsync(guard, state =>
        {
            ChatOutboxEntry entry = state.Outbox.FirstOrDefault(item => item.ClientMessageId == clientMessageId)
                ?? throw new ChatWorkspaceException("chat-not-found");
            lock (_sync) RequireThreadUnsafe(guard, entry.ThreadId, canSend: true);
            if (entry.Status is "sent" or "sending" or "cancelled") throw new ChatWorkspaceException("chat-send-not-retryable");
            return state with { Outbox = state.Outbox.Select(item => item.ClientMessageId == clientMessageId
                ? item with { Status = "queued", ErrorCode = "" } : item).ToArray() };
        }).ConfigureAwait(false);
        KickWorkers(guard);
    }

    internal Task<ChatMessageDto> EditMessageAsync(string threadId, long messageId, ChatEditMessageRequest request)
        => MutateMessageAsync(threadId, messageId, token => _api.EditAsync(threadId, messageId, request, token));
    internal Task<ChatMessageDto> DeleteMessageAsync(string threadId, long messageId)
        => MutateMessageAsync(threadId, messageId, token => _api.DeleteAsync(threadId, messageId, token));
    internal Task<ChatMessageDto> ReactAsync(string threadId, long messageId, ChatReactionRequest request)
        => MutateMessageAsync(threadId, messageId, token => _api.ReactAsync(threadId, messageId, request, token));
    internal Task<ChatMessageDto> PinMessageAsync(string threadId, long messageId, bool pinned)
        => MutateMessageAsync(threadId, messageId, token => _api.PinMessageAsync(threadId, messageId, pinned, token));
    internal Task<ChatMessageDto> RespondToCardAsync(string threadId, long messageId, ChatCardResponseRequest request)
        => MutateMessageAsync(threadId, messageId, token => _api.RespondToCardAsync(threadId, messageId, request, token));

    internal Task<ChatMessageDto> RemovePreviewAsync(string threadId, long messageId, string previewId)
    {
        lock (_sync)
        {
            ChatMessageDto? message = _current.Messages.FirstOrDefault(item => item.Id == messageId && item.ThreadId == threadId);
            ChatLinkPreviewDto? preview = message?.LinkPreviews.FirstOrDefault(item => item.Id == previewId);
            if (preview is null || !preview.CanRemove || preview.Kind == "video")
                throw new ChatWorkspaceException("chat-preview-not-removable");
        }
        return MutateMessageAsync(threadId, messageId, token => _api.RemovePreviewAsync(threadId, messageId, previewId, token));
    }

    private Task<ChatMessageDto> MutateMessageAsync(string threadId, long messageId, Func<CancellationToken, Task<ChatMessageDto>> action)
    {
        Guard guard = RequireGuard();
        long accessGeneration;
        lock (_sync)
        {
            RequireThreadUnsafe(guard, threadId, selected: true);
            accessGeneration = _threadAccessGenerations.GetValueOrDefault(threadId);
            if (!_current.Messages.Any(message => message.Id == messageId && message.ThreadId == threadId)
                && !_current.State.Threads.Any(thread => thread.Id == threadId && thread.PinnedMessages.Any(message => message.Id == messageId)))
                throw new ChatWorkspaceException("chat-message-not-loaded");
        }
        return Track(ExecuteAsync());
        async Task<ChatMessageDto> ExecuteAsync()
        {
            ChatMessageDto message = await CommandAsync(guard, action).ConfigureAwait(false);
            lock (_sync) RequireUnchangedThreadAccessUnsafe(guard, threadId, accessGeneration);
            ApplyMessage(guard, message, accessGeneration);
            return message;
        }
    }

    internal Task<ChatThreadDto> UpdateThreadSelfAsync(string threadId, ChatThreadSelfRequest request)
        => MutateThreadAsync(threadId, token => _api.UpdateThreadSelfAsync(threadId, request, token));
    internal Task<ChatThreadDto> UpdateThreadAsync(string threadId, ChatThreadUpdateRequest request)
        => MutateThreadAsync(threadId, token => _api.UpdateThreadAsync(threadId, request, token));
    internal Task<ChatThreadDto> UpdateMemberAsync(string threadId, ChatMemberRequest request)
        => MutateThreadAsync(threadId, token => _api.UpdateMemberAsync(threadId, request, token));

    private Task<ChatThreadDto> MutateThreadAsync(string threadId, Func<CancellationToken, Task<ChatThreadDto>> action)
    {
        Guard guard = RequireGuard();
        long accessGeneration;
        lock (_sync) { RequireThreadUnsafe(guard, threadId); accessGeneration = _threadAccessGenerations.GetValueOrDefault(threadId); }
        return Track(ExecuteAsync());
        async Task<ChatThreadDto> ExecuteAsync()
        {
            ChatThreadDto thread = await CommandAsync(guard, action).ConfigureAwait(false);
            lock (_sync) RequireUnchangedThreadAccessUnsafe(guard, threadId, accessGeneration);
            ChatMemberDto? self = thread.Members.FirstOrDefault(member => member.Profile.AccountId == guard.OwnerAccountId);
            if (self is null || self.Status is "left" or "removed" or "declined")
                await InvalidateThreadAsync(guard, threadId).ConfigureAwait(false);
            else
            {
                ApplyThread(guard, thread, accessGeneration, requireKnown: true);
                if (!thread.IsInvited)
                {
                    bool reload;
                    lock (_sync) reload = IsCurrentUnsafe(guard) && _current.SelectedThreadId == threadId && _current.Messages.Count == 0;
                    if (reload) await OpenThreadCoreAsync(guard, threadId).ConfigureAwait(false);
                }
            }
            return thread;
        }
    }

    internal Task SetPreferencesAsync(ChatPreferencesRequest request)
        => Track(SetPreferencesCoreAsync(RequireGuard(), request));

    private async Task SetPreferencesCoreAsync(Guard guard, ChatPreferencesRequest request)
    {
        await MutateLocalAsync(guard, state =>
        {
            ChatPreferencesDto current = state.HasPreferences ? state.Preferences : CurrentSnapshot.State.Preferences;
            lock (_sync) _preferencesVersion++;
            return state with { HasPreferences = true, PreferencesPending = true, Preferences = current with
            {
                DoNotDisturb = request.DoNotDisturb ?? current.DoNotDisturb,
                ShareReadReceipts = request.ShareReadReceipts ?? current.ShareReadReceipts,
                ShareTyping = request.ShareTyping ?? current.ShareTyping,
                MessageSoundEnabled = false
            } };
        }).ConfigureAwait(false);
        KickWorkers(guard);
    }

    private async Task PersistRemotePreferencesAsync(Guard guard, ChatPreferencesDto preferences, long? expectedVersion = null)
    {
        await MutateLocalAsync(guard, state =>
        {
            if (expectedVersion is long version && _preferencesVersion != version || state.PreferencesPending
                || state.HasPreferences && state.Preferences == (preferences with { MessageSoundEnabled = false })) return state;
            _preferencesVersion++;
            return state with { Preferences = preferences with { MessageSoundEnabled = false }, HasPreferences = true };
        });
    }

    private async Task FlushPreferencesAsync(Guard guard)
    {
        try
        {
            while (IsCurrent(guard))
            {
                ChatPreferencesDto preferences;
                long version;
                lock (_sync)
                {
                    if (!IsCurrentUnsafe(guard) || !_local.PreferencesPending || !_current.IsAvailable) return;
                    preferences = _local.Preferences;
                    version = _preferencesVersion;
                }
                ChatPreferencesDto accepted = await CommandAsync(guard, token => _api.SetPreferencesAsync(new ChatPreferencesRequest
                {
                    DoNotDisturb = preferences.DoNotDisturb, ShareReadReceipts = preferences.ShareReadReceipts,
                    ShareTyping = preferences.ShareTyping
                }, token)).ConfigureAwait(false);
                await MutateLocalAsync(guard, state =>
                {
                    lock (_sync) if (_preferencesVersion != version) return state;
                    _preferencesVersion++;
                    return state with { Preferences = accepted with { MessageSoundEnabled = false },
                        HasPreferences = true, PreferencesPending = false };
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error) { ApplyOperationFailure(guard, error); }
    }

    internal Task MarkReadAsync(string threadId, long throughMessageId)
    {
        lock (_sync)
        {
            Guard guard = RequireGuard();
            RequireThreadUnsafe(guard, threadId, selected: true);
            if (!_viewActive || _current.IsLoading || throughMessageId <= 0 || _current.Messages.Count == 0
                || throughMessageId > _current.Messages.Max(message => message.Id)) return Task.CompletedTask;
            _visibleThrough = Math.Max(_visibleThrough, throughMessageId);
            if (_readTask is { IsCompleted: false }) return _readTask;
            _readTask = TrackUnsafe(Task.Run(() => ReadVisibleAsync(guard)));
            return _readTask;
        }
    }

    private async Task ReadVisibleAsync(Guard guard)
    {
        try
        {
            while (IsCurrent(guard))
            {
                string threadId;
                long through;
                long accessGeneration;
                lock (_sync)
                {
                    if (!IsCurrentUnsafe(guard) || !_viewActive || _current.SelectedThreadId is not string selected) return;
                    ChatThreadDto thread = RequireThreadUnsafe(guard, selected);
                    through = _visibleThrough;
                    if (through <= thread.LastReadMessageId) return;
                    threadId = selected;
                    accessGeneration = _threadAccessGenerations.GetValueOrDefault(threadId);
                }
                // Always clear our private unread cursor. Sharing it is governed by
                // the server preference, never by withholding this acknowledgement.
                ChatThreadDto read = await CommandAsync(guard, token => _api.MarkReadAsync(threadId, through, token)).ConfigureAwait(false);
                if (read.LastReadMessageId < through) throw new ChatWorkspaceException("chat-invalid-response");
                lock (_sync) RequireUnchangedThreadAccessUnsafe(guard, threadId, accessGeneration);
                ApplyThread(guard, read, accessGeneration, requireKnown: true);
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error) { ApplyOperationFailure(guard, error); }
    }

    internal Task SetTypingAsync(string threadId, bool isTyping)
    {
        Guard guard = RequireGuard();
        lock (_sync)
        {
            RequireThreadUnsafe(guard, threadId, selected: true);
            if (!_current.IsAvailable || isTyping && (!_viewActive || !_current.State.Preferences.ShareTyping)) return Task.CompletedTask;
            DateTimeOffset now = _time.GetUtcNow();
            if (_lastTypingThread == threadId && _lastTypingValue == isTyping
                && now - _lastTypingAt < TimeSpan.FromSeconds(2)) return Task.CompletedTask;
            _lastTypingThread = threadId;
            _lastTypingValue = isTyping;
            _lastTypingAt = now;
        }
        return Track(SendTypingAsync());
        async Task SendTypingAsync()
        {
            try { await EnsureAuthenticatedAsync(guard).ConfigureAwait(false); await _api.SetTypingAsync(threadId, isTyping, guard.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!IsCurrent(guard)) { }
            catch (Exception error) { if (ErrorCode(error) == "chat-unauthorized") HandleFailure(guard, error); }
        }
    }

    private async Task<T> CommandAsync<T>(Guard guard, Func<CancellationToken, Task<T>> action)
    {
        await _commandGate.WaitAsync(guard.Token).ConfigureAwait(false);
        try
        {
            await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
            T result = await action(guard.Token).ConfigureAwait(false);
            EnsureCurrent(guard);
            return result;
        }
        catch (Exception error) { ApplyOperationFailure(guard, error); throw; }
        finally { _commandGate.Release(); }
    }

    private void ApplyOperationFailure(Guard guard, Exception error)
    {
        if (!IsCurrent(guard)) return;
        if (ErrorCode(error) == "chat-unauthorized" || error is HttpRequestException or TimeoutException or OperationCanceledException
            || error is LauncherChatV2ApiException api && (api.IsTransient || api.StatusCode == HttpStatusCode.Unauthorized))
            HandleFailure(guard, error);
        else if (ErrorCode(error) != "chat-send-cancelled") Update(guard, current => current with { ErrorCode = ErrorCode(error), IsLoading = false, IsLoadingEarlier = false });
    }

    private ChatThreadDto RequireThreadUnsafe(Guard guard, string threadId, bool selected = false, bool canSend = false)
    {
        EnsureCurrentUnsafe(guard);
        if (selected && _current.SelectedThreadId != threadId) throw new ChatWorkspaceException("chat-selection-changed");
        ChatThreadDto thread = _current.State.Threads.FirstOrDefault(item => item.Id == threadId)
            ?? throw new ChatWorkspaceException("chat-forbidden");
        if (canSend && !thread.CanSend) throw new ChatWorkspaceException("chat-cannot-send");
        return thread;
    }

    private void RequireUnchangedThreadAccessUnsafe(Guard guard, string threadId, long accessGeneration)
    {
        RequireThreadUnsafe(guard, threadId);
        if (_threadAccessGenerations.GetValueOrDefault(threadId) != accessGeneration) throw new ChatWorkspaceException("chat-forbidden");
    }

    private static bool SameCard(ChatCardDto? first, ChatCardDto? second)
        => first is null && second is null || first is not null && second is not null
            && JsonSerializer.Serialize(first, ChatJson.Options) == JsonSerializer.Serialize(second, ChatJson.Options);
}
