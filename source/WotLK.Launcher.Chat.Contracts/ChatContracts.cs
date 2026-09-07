using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotLK.Launcher.Chat;

public static class ChatLimits
{
    public const long MaximumAttachmentBytes = 500_000_000;
    public const int MaximumAttachmentsPerMessage = 10;
    public const int MaximumMessageCharacters = 1000;
    public const int MaximumGroupMembers = 50;
    public const int UploadChunkBytes = 4 * 1024 * 1024;
    public const int MaximumPreviewsPerMessage = 10;
}

public static class ChatJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
        options.Converters.Add(new ChatIdJsonConverter());
        return options;
    }
}

// Message identifiers and cursors are decimal strings on the wire: JavaScript
// numbers cannot represent every Int64, including identifiers in older history.
public sealed class ChatIdJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long number)) return number;
        if (reader.TokenType == JsonTokenType.String && long.TryParse(reader.GetString(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) return value;
        throw new JsonException("Invalid chat identifier.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
}

public sealed record ChatProfileDto
{
    public uint AccountId { get; init; }
    public string Username { get; init; } = "";
    public string? AvatarUrl { get; init; }
    public string? AvatarVersion { get; init; }
    public string Presence { get; init; } = "offline";
    public string? CharacterName { get; init; }
    public string? CharacterClass { get; init; }
    public string? ZoneName { get; init; }
    public uint? CharacterGuid { get; init; }
}

public sealed record ChatMemberDto
{
    public ChatProfileDto Profile { get; init; } = new();
    public string Role { get; init; } = "member";
    public string Status { get; init; } = "active";
    public DateTimeOffset JoinedAt { get; init; }
    public long LastReadMessageId { get; init; }
}

public sealed record ChatPreferencesDto
{
    public bool DoNotDisturb { get; init; }
    public bool ShareReadReceipts { get; init; } = true;
    public bool ShareTyping { get; init; } = true;
    public bool MessageSoundEnabled { get; init; }
}

public sealed record ChatThreadDto
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "direct";
    public string Title { get; init; } = "";
    public string? AvatarAttachmentId { get; init; }
    public string? AvatarUrl { get; init; }
    public IReadOnlyList<ChatMemberDto> Members { get; init; } = [];
    public ChatMessageDto? LastMessage { get; init; }
    public int UnreadCount { get; init; }
    public long LastReadMessageId { get; init; }
    public bool IsPinned { get; init; }
    public bool IsArchived { get; init; }
    public bool CanSend { get; init; }
    public bool CanManage { get; init; }
    public bool IsInvited { get; init; }
    public long Version { get; init; }
    public IReadOnlyList<ChatMessageDto> PinnedMessages { get; init; } = [];
}

public sealed record ChatAttachmentDto
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public string Kind { get; init; } = "document";
    public long Size { get; init; }
    public string Url { get; init; } = "";
    public string? ThumbnailUrl { get; init; }
    public string? Sha256 { get; init; }
}

public sealed record ChatLinkPreviewDto
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Kind { get; init; } = "link";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ImageUrl { get; init; }
    public string? EmbedUrl { get; init; }
    public string? Provider { get; init; }
    public bool CanRemove { get; init; } = true;
    public bool IsRemoved { get; init; }
}

public sealed record ChatReactionDto
{
    public string Emoji { get; init; } = "";
    public IReadOnlyList<uint> AccountIds { get; init; } = [];
    public int Count => AccountIds.Count;
}

public sealed record ChatReplyDto
{
    public long MessageId { get; init; }
    public string SenderUsername { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsDeleted { get; init; }
}

public sealed record ChatCardResponseDto
{
    public uint AccountId { get; init; }
    public string Username { get; init; } = "";
    public string Status { get; init; } = "joining";
    public string Role { get; init; } = "damage";
}

public sealed record ChatCardDto
{
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? ReferenceId { get; init; }
    public string? Url { get; init; }
    public string? ImageUrl { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ChatCardResponseDto> Responses { get; init; } = [];
}

public sealed record ChatMessageDto
{
    public long Id { get; init; }
    public string ThreadId { get; init; } = "";
    public Guid ClientMessageId { get; init; }
    public ChatProfileDto Sender { get; init; } = new();
    public string Body { get; init; } = "";
    public string Origin { get; init; } = "launcher";
    public string? SenderCharacterName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? EditedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public long Version { get; init; }
    public ChatReplyDto? ReplyTo { get; init; }
    public IReadOnlyList<ChatAttachmentDto> Attachments { get; init; } = [];
    public IReadOnlyList<ChatLinkPreviewDto> LinkPreviews { get; init; } = [];
    public IReadOnlyList<ChatReactionDto> Reactions { get; init; } = [];
    public bool IsPinned { get; init; }
    public IReadOnlyList<uint> ReadByAccountIds { get; init; } = [];
    public ChatCardDto? Card { get; init; }
}

public sealed record ChatStateDto
{
    public ChatProfileDto Self { get; init; } = new();
    public IReadOnlyList<ChatThreadDto> Threads { get; init; } = [];
    public IReadOnlyList<ChatProfileDto> Contacts { get; init; } = [];
    public ChatPreferencesDto Preferences { get; init; } = new();
    public long EventCursor { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed record ChatMessagesPageDto
{
    public ChatThreadDto Thread { get; init; } = new();
    public IReadOnlyList<ChatMessageDto> Messages { get; init; } = [];
    public bool HasEarlier { get; init; }
    public long EventCursor { get; init; }
}

public sealed record ChatTypingDto
{
    public string ThreadId { get; init; } = "";
    public uint AccountId { get; init; }
    public string Username { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed record ChatEventDto
{
    public long Id { get; init; }
    public string Kind { get; init; } = "";
    public string? ThreadId { get; init; }
    public ChatMessageDto? Message { get; init; }
    public ChatThreadDto? Thread { get; init; }
    public ChatPreferencesDto? Preferences { get; init; }
    public ChatTypingDto? Typing { get; init; }
}

public sealed record ChatEventsDto
{
    public IReadOnlyList<ChatEventDto> Events { get; init; } = [];
    public long EventCursor { get; init; }
    public bool HasMore { get; init; }
    public bool RequiresResync { get; init; }
}

public sealed record ChatCreateThreadRequest
{
    public Guid RequestId { get; init; }
    public bool IsGroup { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<uint> ParticipantAccountIds { get; init; } = [];
}

public sealed record ChatSendMessageRequest
{
    public Guid ClientMessageId { get; init; }
    public string Body { get; init; } = "";
    public long? ReplyToMessageId { get; init; }
    public IReadOnlyList<string> AttachmentIds { get; init; } = [];
    public ChatCardDto? Card { get; init; }
}

public sealed record ChatSendMessageResult
{
    public ChatMessageDto Message { get; init; } = new();
    public bool IsDuplicate { get; init; }
}

public sealed record ChatEditMessageRequest(string Body, long? ExpectedVersion = null);
public sealed record ChatReactionRequest(string Emoji, bool Active);
public sealed record ChatPinRequest(bool Pinned);
public sealed record ChatReadRequest(long ThroughMessageId);
public sealed record ChatTypingRequest(bool IsTyping);
public sealed record ChatCardResponseRequest(string Status, string Role);
public sealed record ChatThreadSelfRequest(bool? IsPinned = null, bool? IsArchived = null);
public sealed record ChatThreadUpdateRequest(string? Title = null, string? AvatarAttachmentId = null, long? ExpectedVersion = null);
public sealed record ChatMemberRequest(uint AccountId, string Action, string? Role = null);

public sealed record ChatPreferencesRequest
{
    public bool? DoNotDisturb { get; init; }
    public bool? ShareReadReceipts { get; init; }
    public bool? ShareTyping { get; init; }
}

public sealed record ChatUploadRequest(string FileName, string ContentType, long Size);

public sealed record ChatUploadDto
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long Size { get; init; }
    public long Offset { get; init; }
    public bool IsComplete { get; init; }
    public ChatAttachmentDto? Attachment { get; init; }
}

public sealed record ChatErrorDto(string Error);
