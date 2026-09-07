using System.IO;
using System.Net;
using System.Net.Http;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherChatWorkspace
{
    internal Task AddFilesAsync(string threadId, IReadOnlyList<string> nativePaths)
    {
        Guard guard = RequireGuard();
        lock (_sync) RequireThreadUnsafe(guard, threadId, selected: true, canSend: true);
        if (nativePaths is null || nativePaths.Count is 0 or > ChatLimits.MaximumAttachmentsPerMessage)
            throw new ChatWorkspaceException("chat-too-many-attachments");
        return Track(AddFilesCoreAsync(guard, threadId, nativePaths.ToArray()));
    }

    private async Task AddFilesCoreAsync(Guard guard, string threadId, IReadOnlyList<string> paths)
    {
        List<ChatLocalUpload> additions = [];
        bool committed = false;
        try
        {
        foreach (string path in paths)
        {
            EnsureCurrent(guard);
            ChatSelectedFile file;
            try { file = await _files.InspectAsync(guard.OwnerAccountId, path, guard.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (ChatWorkspaceException) { throw; }
            catch { throw new ChatWorkspaceException("chat-file-unavailable"); }
            additions.Add(new ChatLocalUpload { LocalId = "local-" + Guid.NewGuid().ToString("N"), ThreadId = threadId,
                SourcePath = file.SourcePath, FileName = file.FileName, ContentType = file.ContentType, Size = file.Size,
                LastWriteAt = file.LastWriteAt, Sha256 = file.Sha256 });
            EnsureCurrent(guard);
        }
        await MutateLocalAsync(guard, state =>
        {
            lock (_sync) RequireThreadUnsafe(guard, threadId, canSend: true);
            ChatWorkspaceDraft draft = state.Drafts.FirstOrDefault(item => item.ThreadId == threadId)
                ?? new ChatWorkspaceDraft { ThreadId = threadId };
            if (draft.AttachmentIds.Count + additions.Count > ChatLimits.MaximumAttachmentsPerMessage)
                throw new ChatWorkspaceException("chat-too-many-attachments");
            ChatWorkspaceDraft next = draft with { AttachmentIds = draft.AttachmentIds.Concat(additions.Select(upload => upload.LocalId)).ToArray(),
                UpdatedAt = _time.GetUtcNow() };
            return state with { Uploads = state.Uploads.Concat(additions).ToArray(),
                Drafts = state.Drafts.Where(item => item.ThreadId != threadId).Append(next).ToArray() };
        }).ConfigureAwait(false);
        committed = true;
        KickWorkers(guard);
        }
        finally
        {
            if (!committed && additions.Count > 0)
            {
                // Save may have committed just as the account changed. Retain
                // files already present in that account's durable document.
                ChatWorkspaceLocalState? persisted = null;
                try { persisted = await _store.LoadAsync<ChatWorkspaceLocalState>(guard.OwnerAccountId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) { LogFailure("chat-local-storage", error); }
                foreach (ChatLocalUpload upload in additions.Where(upload => persisted?.Uploads.Any(item => item.SourcePath == upload.SourcePath) != true))
                {
                    try { await _files.DeleteStagedAsync(guard.OwnerAccountId, upload.SourcePath, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception error) { LogFailure("chat-file-unavailable", error); }
                }
            }
        }
    }

    internal Task CancelUploadAsync(string threadId, string uploadId)
        => Track(CancelUploadCoreAsync(RequireGuard(), threadId, uploadId, remove: false));

    internal Task RemoveAttachmentAsync(string threadId, string uploadId)
        => Track(CancelUploadCoreAsync(RequireGuard(), threadId, uploadId, remove: true));

    private async Task CancelUploadCoreAsync(Guard guard, string threadId, string uploadId, bool remove)
    {
        ChatLocalUpload upload;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            RequireThreadUnsafe(guard, threadId, selected: true);
            upload = _local.Uploads.FirstOrDefault(item => item.ThreadId == threadId && (item.LocalId == uploadId || item.Attachment?.Id == uploadId))
                ?? throw new ChatWorkspaceException("chat-attachment-not-found");
            if (_local.Outbox.Any(item => item.WasSubmitted && item.Status != "sent"
                && item.AttachmentIds.Any(id => id == upload.LocalId || id == upload.Attachment?.Id)))
                throw new ChatWorkspaceException("chat-send-already-started");
            cancellation = _uploadCancellations.GetValueOrDefault(upload.LocalId);
        }
        if (cancellation is not null) TryCancel(cancellation);
        await MutateLocalAsync(guard, state => state with
        {
            Uploads = state.Uploads.Select(item => item.LocalId == upload.LocalId && item.Status != "complete"
                ? item with { Status = "cancelled", ErrorCode = "" } : item).ToArray(),
            Drafts = remove ? state.Drafts.Select(draft => draft.ThreadId == threadId
                ? draft with { AttachmentIds = draft.AttachmentIds.Where(id => id != upload.LocalId && id != upload.Attachment?.Id).ToArray(),
                    UpdatedAt = _time.GetUtcNow() } : draft).ToArray() : state.Drafts,
            Outbox = state.Outbox.Select(item =>
            {
                if (item.WasSubmitted || item.Status is "sent" or "cancelled"
                    || !item.AttachmentIds.Any(id => id == upload.LocalId || id == upload.Attachment?.Id)) return item;
                if (!remove) return item with { Status = "failed", ErrorCode = "chat-attachment-cancelled" };
                string[] remaining = item.AttachmentIds.Where(id => id != upload.LocalId && id != upload.Attachment?.Id).ToArray();
                bool empty = string.IsNullOrWhiteSpace(item.Body) && remaining.Length == 0 && item.Card is null;
                return item with { AttachmentIds = remaining, Status = empty ? "cancelled" : "queued", ErrorCode = "" };
            }).ToArray()
        }).ConfigureAwait(false);
        if (upload.ServerUploadId is string serverId && upload.Status != "complete")
        {
            try { await EnsureAuthenticatedAsync(guard).ConfigureAwait(false); await _api.AbortUploadAsync(serverId, guard.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!IsCurrent(guard)) { }
            catch (Exception error) { LogFailure(ErrorCode(error), error); }
        }
        await CleanupUnreferencedUploadsAsync(guard).ConfigureAwait(false);
        KickWorkers(guard);
    }

    internal Task RetryUploadAsync(string threadId, string uploadId)
        => Track(RetryUploadCoreAsync(RequireGuard(), threadId, uploadId));

    private async Task RetryUploadCoreAsync(Guard guard, string threadId, string uploadId)
    {
        lock (_sync) RequireThreadUnsafe(guard, threadId, selected: true, canSend: true);
        await MutateLocalAsync(guard, state =>
        {
            ChatLocalUpload upload = state.Uploads.FirstOrDefault(item => item.ThreadId == threadId && item.LocalId == uploadId)
                ?? throw new ChatWorkspaceException("chat-attachment-not-found");
            if (upload.Status is "complete" or "uploading") return state;
            if (upload.SourceReleased) throw new ChatWorkspaceException("chat-file-unavailable");
            return state with { Uploads = state.Uploads.Select(item => item.LocalId == upload.LocalId
                ? item with { Status = "queued", ErrorCode = "", ServerUploadId = upload.Status == "cancelled" ? null : item.ServerUploadId,
                    Offset = upload.Status == "cancelled" ? 0 : item.Offset } : item).ToArray() };
        }).ConfigureAwait(false);
        KickWorkers(guard);
    }

    private async Task DrainUploadsAsync(Guard guard)
    {
        try
        {
            while (IsCurrent(guard))
            {
                ChatLocalUpload? upload;
                lock (_sync)
                {
                    if (!IsCurrentUnsafe(guard) || !_current.IsAvailable) return;
                    upload = _local.Uploads.FirstOrDefault(item => item.Status == "queued"
                        && _current.State.Threads.Any(thread => thread.Id == item.ThreadId && thread.CanSend));
                }
                if (upload is null) return;
                using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
                lock (_sync)
                {
                    EnsureCurrentUnsafe(guard);
                    _uploadCancellations[upload.LocalId] = cancellation;
                }
                try
                {
                    await ChangeUploadAsync(guard, upload.LocalId, item => item.Status == "queued"
                        ? item with { Status = "uploading", ErrorCode = "" } : item).ConfigureAwait(false);
                    await TransferUploadAsync(guard, upload.LocalId, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!IsCurrent(guard)) { return; }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    await ChangeUploadAsync(guard, upload.LocalId, item => item.Status is "cancelled" or "complete" ? item
                        : item with { Status = "cancelled", ErrorCode = "" }).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    bool transient = error is HttpRequestException or TimeoutException or OperationCanceledException
                        || error is LauncherChatV2ApiException api && api.IsTransient;
                    string code = error is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
                        ? "chat-file-unavailable" : ErrorCode(error);
                    await ChangeUploadAsync(guard, upload.LocalId, item => item.Status is "cancelled" or "complete" ? item
                        : item with { Status = transient ? "queued" : "failed", ErrorCode = code }).ConfigureAwait(false);
                    if (transient) { HandleFailure(guard, error); return; }
                    ApplyOperationFailure(guard, new ChatWorkspaceException(code));
                }
                finally
                {
                    lock (_sync)
                    {
                        if (_uploadCancellations.TryGetValue(upload.LocalId, out CancellationTokenSource? current)
                            && ReferenceEquals(current, cancellation)) _uploadCancellations.Remove(upload.LocalId);
                        _uploadProgress.Remove(upload.LocalId);
                    }
                    if (IsCurrent(guard)) await CleanupUnreferencedUploadsAsync(guard).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error) { ApplyOperationFailure(guard, error); }
    }

    private async Task TransferUploadAsync(Guard guard, string localId, CancellationToken cancellationToken)
    {
        ChatLocalUpload upload = CurrentUpload(guard, localId);
        if (upload.Status != "uploading") return;
        await EnsureAuthenticatedAsync(guard).ConfigureAwait(false);
        ChatUploadDto? remote = null;
        if (upload.ServerUploadId is string id)
        {
            try { remote = await _api.GetUploadAsync(id, cancellationToken).ConfigureAwait(false); }
            catch (LauncherChatV2ApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        }
        if (remote?.IsComplete == true)
        {
            await CompleteLocalUploadAsync(guard, localId, remote).ConfigureAwait(false);
            return;
        }
        await using Stream input = await _files.OpenVerifiedAsync(upload, cancellationToken).ConfigureAwait(false);
        if (!input.CanSeek || input.Length != upload.Size) throw new ChatWorkspaceException("chat-file-changed");
        cancellationToken.ThrowIfCancellationRequested();
        if (remote is null)
        {
            remote = await _api.StartUploadAsync(new ChatUploadRequest(upload.FileName, upload.ContentType, upload.Size), cancellationToken).ConfigureAwait(false);
            await ChangeUploadAsync(guard, localId, item => item.Status == "cancelled" ? item
                : item with { ServerUploadId = remote.Id, Offset = remote.Offset }).ConfigureAwait(false);
        }
        if (remote.Size != upload.Size || remote.Offset < 0 || remote.Offset > upload.Size)
            throw new ChatWorkspaceException("chat-upload-conflict");
        input.Position = remote.Offset;
        while (remote.Offset < upload.Size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrent(guard);
            if (CurrentUpload(guard, localId).Status == "cancelled") throw new OperationCanceledException(cancellationToken);
            long start = remote.Offset;
            int length = checked((int)Math.Min(ChatLimits.UploadChunkBytes, upload.Size - start));
            remote = await _api.UploadChunkAsync(remote.Id, start, input, length,
                new InlineProgress(value => PublishUploadProgress(guard, localId, start + value)), cancellationToken).ConfigureAwait(false);
            await ChangeUploadAsync(guard, localId, item => item.Status == "cancelled" ? item
                : item with { Offset = remote.Offset, ServerUploadId = remote.Id }).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        ChatUploadDto completed = await _api.CompleteUploadAsync(remote.Id, cancellationToken).ConfigureAwait(false);
        await CompleteLocalUploadAsync(guard, localId, completed).ConfigureAwait(false);
    }

    private async Task CompleteLocalUploadAsync(Guard guard, string localId, ChatUploadDto remote)
    {
        if (!remote.IsComplete || remote.Attachment is null) throw new ChatWorkspaceException("chat-upload-incomplete");
        ChatLocalUpload upload = CurrentUpload(guard, localId);
        if (remote.Size != upload.Size || remote.Attachment.Size != upload.Size
            || remote.Attachment.Sha256 is { Length: > 0 } sha && !string.Equals(sha, upload.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ChatWorkspaceException("chat-file-changed");
        await MutateLocalAsync(guard, state => state with
        {
            Uploads = state.Uploads.Select(item => item.LocalId == localId && item.Status != "cancelled"
                ? item with { Status = "complete", ErrorCode = "", Offset = item.Size, ServerUploadId = remote.Id, Attachment = remote.Attachment }
                : item).ToArray(),
            Outbox = state.Outbox.Select(item => item.Status == "failed" && item.ErrorCode == "chat-attachment-failed"
                && item.AttachmentIds.Contains(localId) ? item with { Status = "queued", ErrorCode = "" } : item).ToArray()
        }).ConfigureAwait(false);
        KickWorkers(guard);
    }

    private ChatLocalUpload CurrentUpload(Guard guard, string localId)
    {
        lock (_sync)
        {
            EnsureCurrentUnsafe(guard);
            return _local.Uploads.FirstOrDefault(item => item.LocalId == localId)
                ?? throw new ChatWorkspaceException("chat-attachment-not-found");
        }
    }

    private Task ChangeUploadAsync(Guard guard, string localId, Func<ChatLocalUpload, ChatLocalUpload> change)
        => MutateLocalAsync(guard, state => state with
        { Uploads = state.Uploads.Select(upload => upload.LocalId == localId ? change(upload) : upload).ToArray() });

    private void PublishUploadProgress(Guard guard, string localId, long offset)
    {
        ChatWorkspaceSnapshot? snapshot = null;
        lock (_sync)
        {
            if (!IsCurrentUnsafe(guard) || !_local.Uploads.Any(upload => upload.LocalId == localId && upload.Status == "uploading")) return;
            _uploadProgress[localId] = offset;
            DateTimeOffset now = _time.GetUtcNow();
            if (now - _lastProgressPublishAt >= TimeSpan.FromMilliseconds(100))
            {
                _lastProgressPublishAt = now;
                snapshot = SetSnapshotUnsafe(_current);
            }
        }
        Publish(snapshot);
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private async Task CleanupUnreferencedUploadsAsync(Guard guard)
    {
        await _localGate.WaitAsync(guard.Token).ConfigureAwait(false);
        try
        {
            ChatLocalUpload[] unused;
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                bool Referenced(ChatLocalUpload upload) => _local.Drafts.Any(draft => draft.AttachmentIds.Any(id => id == upload.LocalId || id == upload.Attachment?.Id))
                    || _local.Outbox.Any(entry => entry.Status is not ("sent" or "cancelled")
                        && entry.AttachmentIds.Any(id => id == upload.LocalId || id == upload.Attachment?.Id));
                unused = _local.Uploads.Where(upload => !upload.SourceReleased && !Referenced(upload)).ToArray();
                foreach (ChatLocalUpload upload in unused)
                    if (_uploadCancellations.TryGetValue(upload.LocalId, out CancellationTokenSource? cancellation)) TryCancel(cancellation);
                unused = unused.Where(upload => !_uploadCancellations.ContainsKey(upload.LocalId)).ToArray();
            }
            if (unused.Length == 0) return;
            HashSet<string> deleted = [];
            foreach (ChatLocalUpload upload in unused)
            {
                try { await _files.DeleteStagedAsync(guard.OwnerAccountId, upload.SourcePath, guard.Token).ConfigureAwait(false); deleted.Add(upload.LocalId); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { LogFailure("chat-file-unavailable", error); }
            }
            if (deleted.Count == 0) return;
            ChatWorkspaceLocalState next;
            lock (_sync)
            {
                EnsureCurrentUnsafe(guard);
                next = _local with { Uploads = _local.Uploads.Select(upload => deleted.Contains(upload.LocalId)
                    ? upload with { SourceReleased = true, Status = upload.Status == "complete" ? "complete" : "cancelled" } : upload).ToArray() };
            }
            await _store.SaveAsync(guard.OwnerAccountId, next, guard.Token).ConfigureAwait(false);
            ChatWorkspaceSnapshot snapshot;
            lock (_sync) { EnsureCurrentUnsafe(guard); _local = next; snapshot = SetSnapshotUnsafe(_current); }
            Publish(snapshot);
        }
        finally { _localGate.Release(); }
    }
}
