using System.Globalization;
using System.Text;

namespace WotLK.Launcher.Server;

public sealed record SendChatMessageRequest(Guid ClientMessageId, string Body);
public sealed record MarkChatReadRequest(long ThroughMessageId);

public sealed record LauncherChatMessage(
    long Id, Guid ClientMessageId, uint SenderAccountId, uint RecipientAccountId,
    string SenderUsername, string Body, string Origin, DateTimeOffset CreatedAt);

public sealed record LauncherChatConversation(
    uint FriendAccountId, string FriendUsername, LauncherChatMessage LastMessage,
    int UnreadCount, long LastReadMessageId, long FriendLastReadMessageId);

public sealed record LauncherChatConversations(
    IReadOnlyList<LauncherChatConversation> Conversations, long LastMessageId,
    int UnreadCount, bool HasMore);

public sealed record LauncherChatMessages(
    uint FriendAccountId, string FriendUsername, IReadOnlyList<LauncherChatMessage> Messages,
    bool HasMore, long LastReadMessageId, long FriendLastReadMessageId);

public sealed record LauncherChatUpdates(
    IReadOnlyList<LauncherChatMessage> Messages, long LastMessageId, bool HasMore);

public sealed record LauncherChatSendResult(LauncherChatMessage Message, bool IsDuplicate);
public sealed record LauncherChatReadResult(long LastReadMessageId);

internal sealed class ChatOperationException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal static class ChatMessageValidation
{
    internal const int MaximumCharacters = 1000;
    internal const int MaximumUtf8Bytes = 4000;
    internal const int MessagesPerMinute = 30;
    internal const int MaximumRequestBytes = 8192;

    internal static string Normalize(string? body)
    {
        if (body is null) throw new ChatOperationException("chat-invalid-message");
        string value = body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (value.Length is 0 or > MaximumCharacters)
            throw new ChatOperationException("chat-invalid-message");
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > MaximumUtf8Bytes)
                throw new ChatOperationException("chat-invalid-message");
        }
        catch (EncoderFallbackException)
        {
            throw new ChatOperationException("chat-invalid-message");
        }
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if ((category == UnicodeCategory.Control && rune.Value is not ('\n' or '\t'))
                || rune.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
                throw new ChatOperationException("chat-invalid-message");
        }
        return value;
    }
}
