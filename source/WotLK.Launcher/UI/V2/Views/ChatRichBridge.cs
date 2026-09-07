using System.Text.Json;

namespace WotLK.Launcher.UI.V2.Views;

public sealed class ChatRichActionEventArgs(
    string requestId, Guid sessionId, uint ownerAccountId, long sequence, JsonElement action) : EventArgs
{
    public string RequestId { get; } = requestId;
    public Guid SessionId { get; } = sessionId;
    public uint OwnerAccountId { get; } = ownerAccountId;
    public long Sequence { get; } = sequence;
    // The complete validated envelope, with action and payload properties.
    public JsonElement Action { get; } = action;
}

public sealed class ChatFilesAddedEventArgs(
    string requestId, Guid sessionId, uint ownerAccountId, long sequence,
    string conversationId, IReadOnlyList<string> paths) : EventArgs
{
    public string RequestId { get; } = requestId;
    public Guid SessionId { get; } = sessionId;
    public uint OwnerAccountId { get; } = ownerAccountId;
    public long Sequence { get; } = sequence;
    public string ConversationId { get; } = conversationId;
    public IReadOnlyList<string> Paths { get; } = paths;
}
