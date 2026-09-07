using System.IO;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherChatWorkspace
{
    internal static bool CanDeleteFailedSend(ChatOutboxEntry entry) => entry.DeleteRequested
        || entry.Status == "failed" || entry.Status == "queued" && entry.ErrorCode.Length > 0;

    internal Task DeleteFailedSendAsync(Guid clientMessageId)
        => Track(DeleteFailedSendCoreAsync(RequireGuard(), clientMessageId));

    private async Task DeleteFailedSendCoreAsync(Guard guard, Guid clientMessageId)
    {
        await MutateLocalAsync(guard, state =>
        {
            ChatOutboxEntry entry = state.Outbox.FirstOrDefault(item => item.ClientMessageId == clientMessageId)
                ?? throw new ChatWorkspaceException("chat-not-found");
            RequireThreadUnsafe(guard, entry.ThreadId, selected: true);
            if (entry.DeleteRequested) return state;
            if (!CanDeleteFailedSend(entry)) throw new ChatWorkspaceException("chat-send-delete-not-allowed");
            _deletionProbeAfter.Remove(clientMessageId);
            // Persist the user's intent before any network operation. This UUID
            // can never enter the POST queue again, even after a restart.
            return state with { Outbox = state.Outbox.Select(item => item.ClientMessageId == clientMessageId
                ? item with { DeleteRequested = true, DeletionConfirmed = !item.WasSubmitted,
                    Status = item.WasSubmitted ? "deleting" : "cancelled", ErrorCode = "" } : item).ToArray() };
        }).ConfigureAwait(false);
        await CleanupUnreferencedUploadsAsync(guard).ConfigureAwait(false);
        KickWorkers(guard);
    }

    private bool ShouldProbeDeletionUnsafe(ChatOutboxEntry entry) => entry.DeleteRequested && !entry.DeletionConfirmed
        && (!_deletionProbeAfter.TryGetValue(entry.ClientMessageId, out DateTimeOffset after) || after <= _time.GetUtcNow());

    private async Task DrainDeletionsAsync(Guard guard)
    {
        try
        {
            // Bound a wake-up even if an old account contains many tombstones.
            for (int count = 0; count < 25; count++)
            {
                ChatOutboxEntry? entry;
                lock (_sync)
                {
                    if (!IsCurrentUnsafe(guard) || !_current.IsAvailable) return;
                    entry = _local.Outbox.Where(ShouldProbeDeletionUnsafe)
                        .OrderBy(item => item.Status == "deleting" ? 0 : 1).ThenBy(item => item.CreatedAt).FirstOrDefault();
                    if (entry is null) return;
                    _deletionProbeAfter[entry.ClientMessageId] = _time.GetUtcNow().AddSeconds(30);
                }
                try
                {
                    ChatMessageDto? deleted = await CommandAsync(guard, async token =>
                    {
                        ChatMessageDto? found = await _api.FindSentByClientIdAsync(entry.ThreadId, entry.ClientMessageId, token).ConfigureAwait(false);
                        EnsureCurrent(guard);
                        if (found is null) return null;
                        RequireDeletionIdentity(found, guard, entry);
                        if (found.DeletedAt is not null) return found;
                        ChatMessageDto result = await _api.DeleteAsync(entry.ThreadId, found.Id, token).ConfigureAwait(false);
                        RequireDeletionIdentity(result, guard, entry);
                        if (result.Id != found.Id || result.DeletedAt is null) throw new InvalidDataException("Unconfirmed Messages deletion.");
                        return result;
                    }).ConfigureAwait(false);
                    await MutateLocalAsync(guard, state => state with { Outbox = state.Outbox.Select(item =>
                        item.ClientMessageId == entry.ClientMessageId && item.ThreadId == entry.ThreadId && item.DeleteRequested
                            ? item with { Status = "cancelled", ErrorCode = "", DeletionConfirmed = deleted is not null } : item).ToArray() }).ConfigureAwait(false);
                    // A 404 removes only the failed local row. Its durable UUID
                    // tombstone remains: later events and periodic lookup can
                    // still find and delete a delayed commit without resending.
                    if (deleted is not null) ApplyMessage(guard, deleted);
                    await CleanupUnreferencedUploadsAsync(guard).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!IsCurrent(guard)) { return; }
                catch (Exception error)
                {
                    await MutateLocalAsync(guard, state => state with { Outbox = state.Outbox.Select(item =>
                        item.ClientMessageId == entry.ClientMessageId && item.DeleteRequested && item.Status == "deleting"
                            ? item with { ErrorCode = ErrorCode(error) } : item).ToArray() }).ConfigureAwait(false);
                    ApplyOperationFailure(guard, error);
                }
            }
        }
        catch (OperationCanceledException) when (!IsCurrent(guard)) { }
        catch (Exception error) { ApplyOperationFailure(guard, error); }
    }

    private static void RequireDeletionIdentity(ChatMessageDto message, Guard guard, ChatOutboxEntry entry)
    {
        if (message.Sender.AccountId != guard.OwnerAccountId || message.ThreadId != entry.ThreadId
            || message.ClientMessageId != entry.ClientMessageId)
            throw new InvalidDataException("Messages deletion belongs to another account or request.");
    }
}
