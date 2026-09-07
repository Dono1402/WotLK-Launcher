using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal sealed record ChatWorkspaceDraft
{
    public string ThreadId { get; init; } = "";
    public string Body { get; init; } = "";
    public long? ReplyToMessageId { get; init; }
    public IReadOnlyList<string> AttachmentIds { get; init; } = [];
    public ChatCardDto? Card { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed record ChatOutboxEntry
{
    public Guid ClientMessageId { get; init; }
    public string ThreadId { get; init; } = "";
    public string Body { get; init; } = "";
    public long? ReplyToMessageId { get; init; }
    public IReadOnlyList<string> AttachmentIds { get; init; } = [];
    public ChatCardDto? Card { get; init; }
    public string Status { get; init; } = "queued";
    public string ErrorCode { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public bool WasSubmitted { get; init; }
}

// SourcePath and the fingerprint remain solely in the encrypted native store.
internal sealed record ChatLocalUpload
{
    public string LocalId { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public long Size { get; init; }
    public DateTimeOffset LastWriteAt { get; init; }
    public string Sha256 { get; init; } = "";
    public bool SourceReleased { get; init; }
    public string? ServerUploadId { get; init; }
    public long Offset { get; init; }
    public string Status { get; init; } = "queued";
    public string ErrorCode { get; init; } = "";
    public ChatAttachmentDto? Attachment { get; init; }
}

internal sealed record ChatWorkspaceUpload
{
    public string LocalId { get; init; } = "";
    public string ThreadId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "application/octet-stream";
    public long Size { get; init; }
    public long Offset { get; init; }
    public string Status { get; init; } = "queued";
    public string ErrorCode { get; init; } = "";
    public ChatAttachmentDto? Attachment { get; init; }
}

internal sealed record ChatWorkspaceLocalState
{
    public IReadOnlyList<ChatWorkspaceDraft> Drafts { get; init; } = [];
    public IReadOnlyList<ChatOutboxEntry> Outbox { get; init; } = [];
    public IReadOnlyList<ChatLocalUpload> Uploads { get; init; } = [];
    public ChatPreferencesDto Preferences { get; init; } = new();
    public bool HasPreferences { get; init; }
    public bool PreferencesPending { get; init; }
}

internal sealed record ChatWorkspaceSnapshot
{
    public int Protocol { get; init; } = 2;
    public Guid SessionId { get; init; }
    public uint OwnerAccountId { get; init; }
    public long Sequence { get; init; }
    public ChatStateDto State { get; init; } = new();
    public string? SelectedThreadId { get; init; }
    public IReadOnlyList<ChatMessageDto> Messages { get; init; } = [];
    public bool HasEarlier { get; init; }
    public bool IsLoading { get; init; }
    public bool IsLoadingEarlier { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsLegacyFallback { get; init; }
    public string ErrorCode { get; init; } = "";
    public IReadOnlyList<ChatWorkspaceDraft> Drafts { get; init; } = [];
    public ChatWorkspaceDraft? Draft { get; init; }
    public IReadOnlyList<ChatOutboxEntry> Outbox { get; init; } = [];
    public IReadOnlyList<ChatWorkspaceUpload> Uploads { get; init; } = [];
    public IReadOnlyList<ChatTypingDto> Typing { get; init; } = [];
}

internal sealed class ChatWorkspaceSnapshotEventArgs(ChatWorkspaceSnapshot snapshot) : EventArgs
{
    internal ChatWorkspaceSnapshot Snapshot { get; } = snapshot;
}

internal sealed class ChatWorkspaceException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
