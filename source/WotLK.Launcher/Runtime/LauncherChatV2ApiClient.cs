using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Runtime;

internal interface ILauncherChatV2ApiClient
{
    Task<ChatStateDto> GetStateAsync(CancellationToken cancellationToken);
    Task<ChatThreadDto> CreateThreadAsync(ChatCreateThreadRequest request, CancellationToken cancellationToken);
    Task<ChatMessagesPageDto> GetMessagesAsync(string threadId, long? beforeId, CancellationToken cancellationToken);
    Task<ChatEventsDto> GetEventsAsync(long afterEventId, int waitSeconds, CancellationToken cancellationToken);
    Task<ChatSendMessageResult> SendAsync(string threadId, ChatSendMessageRequest request, CancellationToken cancellationToken);
    Task<ChatMessageDto?> FindSentByClientIdAsync(string threadId, Guid clientMessageId, CancellationToken cancellationToken);
    Task<ChatMessageDto> EditAsync(string threadId, long messageId, ChatEditMessageRequest request, CancellationToken cancellationToken);
    Task<ChatMessageDto> DeleteAsync(string threadId, long messageId, CancellationToken cancellationToken);
    Task<ChatMessageDto> ReactAsync(string threadId, long messageId, ChatReactionRequest request, CancellationToken cancellationToken);
    Task<ChatMessageDto> PinMessageAsync(string threadId, long messageId, bool pinned, CancellationToken cancellationToken);
    Task<ChatMessageDto> RemovePreviewAsync(string threadId, long messageId, string previewId, CancellationToken cancellationToken);
    Task<ChatMessageDto> RespondToCardAsync(string threadId, long messageId, ChatCardResponseRequest request, CancellationToken cancellationToken);
    Task<ChatThreadDto> MarkReadAsync(string threadId, long throughMessageId, CancellationToken cancellationToken);
    Task SetTypingAsync(string threadId, bool isTyping, CancellationToken cancellationToken);
    Task<ChatThreadDto> UpdateThreadSelfAsync(string threadId, ChatThreadSelfRequest request, CancellationToken cancellationToken);
    Task<ChatThreadDto> UpdateThreadAsync(string threadId, ChatThreadUpdateRequest request, CancellationToken cancellationToken);
    Task<ChatThreadDto> UpdateMemberAsync(string threadId, ChatMemberRequest request, CancellationToken cancellationToken);
    Task<ChatPreferencesDto> SetPreferencesAsync(ChatPreferencesRequest request, CancellationToken cancellationToken);
    Task<ChatUploadDto> StartUploadAsync(ChatUploadRequest request, CancellationToken cancellationToken);
    Task<ChatUploadDto> GetUploadAsync(string uploadId, CancellationToken cancellationToken);
    Task<ChatUploadDto> UploadChunkAsync(string uploadId, long offset, Stream source, int length,
        IProgress<long>? progress, CancellationToken cancellationToken);
    Task<ChatUploadDto> CompleteUploadAsync(string uploadId, CancellationToken cancellationToken);
    Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken);
    Task<ChatMediaStream> OpenAttachmentAsync(string attachmentId, string? range, CancellationToken cancellationToken);
    Task<ChatMediaStream> OpenPreviewImageAsync(string url, CancellationToken cancellationToken);
    Task<ChatMediaStream> OpenLinkedMediaAsync(string url, string? range, CancellationToken cancellationToken);
    Task<ChatMediaStream> OpenAvatarAsync(string authorizedUrl, CancellationToken cancellationToken);
}

internal sealed class LauncherChatV2ApiException(HttpStatusCode statusCode, string code) : Exception(code)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal string Code { get; } = code;
    internal bool IsTransient => StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
        || (int)StatusCode >= 500;
}

internal sealed partial class LauncherChatV2ApiClient : ILauncherChatV2ApiClient
{
    internal const int MaximumResponseBytes = 8 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly Uri _baseUri;

    internal LauncherChatV2ApiClient(HttpClient client, Uri launcherApiBaseUri)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(launcherApiBaseUri);
        // Existing runtime dependencies supply /api/v1/. Both generations share
        // the same authenticated origin; do not derive an origin from message data.
        _baseUri = launcherApiBaseUri.AbsolutePath.TrimEnd('/').EndsWith("/api/v1", StringComparison.Ordinal)
            ? new Uri(launcherApiBaseUri, "../v2/chat/")
            : launcherApiBaseUri.AbsolutePath.TrimEnd('/').EndsWith("/api/v2/chat", StringComparison.Ordinal)
                ? new Uri(launcherApiBaseUri.AbsoluteUri.TrimEnd('/') + "/")
                : new Uri(launcherApiBaseUri, "chat/");
    }

    public async Task<ChatStateDto> GetStateAsync(CancellationToken cancellationToken)
    {
        ChatStateDto state = await ReadAsync<ChatStateDto>(HttpMethod.Get, "state", null, cancellationToken).ConfigureAwait(false);
        if (state.Self is null || state.Self.AccountId == 0 || state.EventCursor < 0 || state.Threads is null
            || state.Contacts is null || state.Preferences is null || state.Capabilities is null
            || state.Threads.Count > 10000 || state.Contacts.Count > 10000)
            throw InvalidResponse();
        foreach (ChatThreadDto thread in state.Threads) ValidateThread(thread);
        if (state.Threads.Select(thread => thread.Id).Distinct(StringComparer.Ordinal).Count() != state.Threads.Count)
            throw InvalidResponse();
        return state with { Preferences = state.Preferences with { MessageSoundEnabled = false } };
    }

    public async Task<ChatThreadDto> CreateThreadAsync(ChatCreateThreadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestId == Guid.Empty || request.ParticipantAccountIds is null || request.ParticipantAccountIds.Count == 0
            || request.ParticipantAccountIds.Count >= ChatLimits.MaximumGroupMembers || request.ParticipantAccountIds.Any(id => id == 0)
            || request.Title is null || request.Title.Length > 200)
            throw new ArgumentException("Invalid Messages conversation.");
        ChatThreadDto thread = await ReadAsync<ChatThreadDto>(HttpMethod.Post, "threads", request, cancellationToken).ConfigureAwait(false);
        ValidateThread(thread);
        return thread;
    }

    public async Task<ChatMessagesPageDto> GetMessagesAsync(string threadId, long? beforeId, CancellationToken cancellationToken)
    {
        if (beforeId < 0) throw new ArgumentOutOfRangeException(nameof(beforeId));
        ChatMessagesPageDto page = await ReadAsync<ChatMessagesPageDto>(HttpMethod.Get,
            ThreadPath(threadId) + "/messages?limit=50" + (beforeId is long cursor ? "&beforeId=" + Number(cursor) : ""),
            null, cancellationToken).ConfigureAwait(false);
        ValidateThread(page.Thread, threadId);
        if (page.Messages is null || page.Messages.Count > 50 || page.EventCursor < 0) throw InvalidResponse();
        foreach (ChatMessageDto message in page.Messages) ValidateMessage(message, threadId);
        if (page.Messages.Select(message => message.Id).Distinct().Count() != page.Messages.Count
            || beforeId is long before && page.Messages.Any(message => message.Id >= before)) throw InvalidResponse();
        return page;
    }

    public async Task<ChatEventsDto> GetEventsAsync(long afterEventId, int waitSeconds, CancellationToken cancellationToken)
    {
        if (afterEventId < 0 || waitSeconds is < 0 or > 25) throw new ArgumentOutOfRangeException(nameof(afterEventId));
        ChatEventsDto page = await ReadAsync<ChatEventsDto>(HttpMethod.Get,
            $"events?afterId={Number(afterEventId)}&limit=100&waitSeconds={waitSeconds}", null,
            cancellationToken, TimeSpan.FromSeconds(waitSeconds + 15)).ConfigureAwait(false);
        if (page.Events is null || page.Events.Count > 100 || page.EventCursor < 0
            || !page.RequiresResync && page.EventCursor < afterEventId) throw InvalidResponse();
        foreach (ChatEventDto item in page.Events)
        {
            if (item is null || item.Id <= afterEventId || item.Id > page.EventCursor
                || string.IsNullOrWhiteSpace(item.Kind) || item.Kind.Length > 64) throw InvalidResponse();
            if (item.Message is not null) ValidateMessage(item.Message, item.ThreadId);
            if (item.Thread is not null) ValidateThread(item.Thread, item.ThreadId);
        }
        if (page.Events.Select(item => item.Id).Distinct().Count() != page.Events.Count
            || page.HasMore && page.EventCursor == afterEventId) throw InvalidResponse();
        return page;
    }

    public async Task<ChatSendMessageResult> SendAsync(string threadId, ChatSendMessageRequest request, CancellationToken cancellationToken)
    {
        ValidateSend(request);
        ChatSendMessageResult result = await ReadAsync<ChatSendMessageResult>(HttpMethod.Post,
            ThreadPath(threadId) + "/messages", request, cancellationToken).ConfigureAwait(false);
        ValidateMessage(result.Message, threadId);
        if (result.Message.ClientMessageId != request.ClientMessageId) throw InvalidResponse();
        return result;
    }

    public Task<ChatMessageDto> EditAsync(string threadId, long messageId, ChatEditMessageRequest request, CancellationToken cancellationToken)
        => MessageMutationAsync(HttpMethod.Patch, threadId, messageId, "", request, cancellationToken);

    public async Task<ChatMessageDto?> FindSentByClientIdAsync(string threadId, Guid clientMessageId, CancellationToken cancellationToken)
    {
        if (clientMessageId == Guid.Empty) throw new ArgumentException("A Messages client identifier is required.");
        try
        {
            ChatSendMessageResult result = await ReadAsync<ChatSendMessageResult>(HttpMethod.Get,
                ThreadPath(threadId) + "/messages/by-client/" + clientMessageId.ToString("D"), null, cancellationToken).ConfigureAwait(false);
            ValidateMessage(result.Message, threadId);
            if (result.Message.ClientMessageId != clientMessageId) throw InvalidResponse();
            return result.Message;
        }
        catch (LauncherChatV2ApiException error) when (error.StatusCode == HttpStatusCode.NotFound && error.Code == "chat-not-found")
        {
            return null;
        }
    }

    public Task<ChatMessageDto> DeleteAsync(string threadId, long messageId, CancellationToken cancellationToken)
        => MessageMutationAsync(HttpMethod.Delete, threadId, messageId, "", null, cancellationToken);

    public Task<ChatMessageDto> ReactAsync(string threadId, long messageId, ChatReactionRequest request, CancellationToken cancellationToken)
        => MessageMutationAsync(HttpMethod.Put, threadId, messageId, "/reaction", request, cancellationToken);

    public Task<ChatMessageDto> PinMessageAsync(string threadId, long messageId, bool pinned, CancellationToken cancellationToken)
        => MessageMutationAsync(HttpMethod.Put, threadId, messageId, "/pin", new ChatPinRequest(pinned), cancellationToken);

    public Task<ChatMessageDto> RemovePreviewAsync(string threadId, long messageId, string previewId, CancellationToken cancellationToken)
        => MessageMutationAsync(HttpMethod.Delete, threadId, messageId, "/previews/" + Identifier(previewId), null, cancellationToken);

    public Task<ChatMessageDto> RespondToCardAsync(string threadId, long messageId, ChatCardResponseRequest request, CancellationToken cancellationToken)
        => MessageMutationAsync(HttpMethod.Put, threadId, messageId, "/card-response", request, cancellationToken);

    public async Task<ChatThreadDto> MarkReadAsync(string threadId, long throughMessageId, CancellationToken cancellationToken)
    {
        ChatThreadDto thread = await ThreadMutationAsync(HttpMethod.Post, threadId, "/read",
            new ChatReadRequest(Positive(throughMessageId)), cancellationToken).ConfigureAwait(false);
        if (thread.LastReadMessageId < throughMessageId) throw InvalidResponse();
        return thread;
    }

    public async Task SetTypingAsync(string threadId, bool isTyping, CancellationToken cancellationToken)
    {
        JsonElement result = await ReadAsync<JsonElement>(HttpMethod.Put, ThreadPath(threadId) + "/typing",
            new ChatTypingRequest(isTyping), cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("accepted", out JsonElement accepted)
            || accepted.ValueKind != JsonValueKind.True) throw InvalidResponse();
    }

    public Task<ChatThreadDto> UpdateThreadSelfAsync(string threadId, ChatThreadSelfRequest request, CancellationToken cancellationToken)
        => ThreadMutationAsync(HttpMethod.Patch, threadId, "/self", request, cancellationToken);

    public Task<ChatThreadDto> UpdateThreadAsync(string threadId, ChatThreadUpdateRequest request, CancellationToken cancellationToken)
        => ThreadMutationAsync(HttpMethod.Patch, threadId, "", request, cancellationToken);

    public Task<ChatThreadDto> UpdateMemberAsync(string threadId, ChatMemberRequest request, CancellationToken cancellationToken)
        => ThreadMutationAsync(HttpMethod.Post, threadId, "/members", request, cancellationToken);

    public async Task<ChatPreferencesDto> SetPreferencesAsync(ChatPreferencesRequest request, CancellationToken cancellationToken)
    {
        ChatPreferencesDto preferences = await ReadAsync<ChatPreferencesDto>(HttpMethod.Patch, "preferences", request, cancellationToken).ConfigureAwait(false);
        return preferences with { MessageSoundEnabled = false };
    }

    private async Task<ChatMessageDto> MessageMutationAsync(HttpMethod method, string threadId, long messageId, string suffix,
        object? request, CancellationToken cancellationToken)
    {
        ChatMessageDto message = await ReadAsync<ChatMessageDto>(method,
            ThreadPath(threadId) + "/messages/" + Number(Positive(messageId)) + suffix, request, cancellationToken).ConfigureAwait(false);
        ValidateMessage(message, threadId);
        if (message.Id != messageId) throw InvalidResponse();
        return message;
    }

    private async Task<ChatThreadDto> ThreadMutationAsync(HttpMethod method, string threadId, string suffix,
        object request, CancellationToken cancellationToken)
    {
        ChatThreadDto thread = await ReadAsync<ChatThreadDto>(method, ThreadPath(threadId) + suffix, request, cancellationToken).ConfigureAwait(false);
        ValidateThread(thread, threadId);
        return thread;
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string relative, object? payload,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
        using HttpRequestMessage request = new(method, new Uri(_baseUri, relative));
        if (payload is not null) request.Content = JsonContent.Create(payload, options: ChatJson.Options);
        using HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).ConfigureAwait(false);
        return await ReadResponseAsync<T>(response, deadline.Token).ConfigureAwait(false);
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LauncherChatV2ApiException(response.StatusCode, "chat-unauthorized");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw InvalidResponse();
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream data = new();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (data.Length + count > MaximumResponseBytes) throw InvalidResponse();
            data.Write(buffer, 0, count);
        }
        if (!response.IsSuccessStatusCode)
        {
            string error = response.StatusCode switch
            {
                HttpStatusCode.NotFound => "chat-not-found",
                HttpStatusCode.Forbidden => "chat-forbidden",
                HttpStatusCode.TooManyRequests => "chat-rate-limited",
                HttpStatusCode.Conflict => "chat-conflict",
                _ => "chat-unavailable"
            };
            try
            {
                ChatErrorDto? failure = JsonSerializer.Deserialize<ChatErrorDto>(data.GetBuffer().AsSpan(0, checked((int)data.Length)), ChatJson.Options);
                if (failure?.Error is { Length: > 0 and <= 80 } code && code.StartsWith("chat-", StringComparison.Ordinal)) error = code;
            }
            catch (JsonException) { }
            throw new LauncherChatV2ApiException(response.StatusCode, error);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(data.GetBuffer().AsSpan(0, checked((int)data.Length)), ChatJson.Options)
                ?? throw InvalidResponse();
        }
        catch (JsonException exception) { throw new InvalidDataException("Invalid Messages response.", exception); }
    }

    internal static void ValidateSend(ChatSendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClientMessageId == Guid.Empty || request.Body is null || request.Body.Length > ChatLimits.MaximumMessageCharacters
            || request.AttachmentIds is null || request.AttachmentIds.Count > ChatLimits.MaximumAttachmentsPerMessage
            || request.ReplyToMessageId <= 0
            || string.IsNullOrWhiteSpace(request.Body) && request.AttachmentIds.Count == 0 && request.Card is null)
            throw new ArgumentException("Invalid Messages content.");
        foreach (string id in request.AttachmentIds) _ = Identifier(id);
    }

    private static void ValidateThread(ChatThreadDto? thread, string? expected = null)
    {
        if (thread is null || string.IsNullOrWhiteSpace(thread.Id) || thread.Id.Length > 128
            || expected is not null && thread.Id != expected || thread.Members is null || thread.Members.Count > ChatLimits.MaximumGroupMembers
            || thread.UnreadCount < 0 || thread.LastReadMessageId < 0 || thread.PinnedMessages is null)
            throw InvalidResponse();
        foreach (ChatMemberDto member in thread.Members)
            if (member?.Profile is null || member.Profile.AccountId == 0 || member.LastReadMessageId < 0) throw InvalidResponse();
        if (thread.LastMessage is not null) ValidateMessage(thread.LastMessage, thread.Id);
        foreach (ChatMessageDto message in thread.PinnedMessages) ValidateMessage(message, thread.Id);
    }

    private static void ValidateMessage(ChatMessageDto? message, string? expectedThread = null)
    {
        if (message is null || message.Id <= 0 || string.IsNullOrWhiteSpace(message.ThreadId)
            || expectedThread is not null && message.ThreadId != expectedThread || message.ClientMessageId == Guid.Empty
            || message.Sender is null || message.Sender.AccountId == 0 || message.Body is null
            || message.Body.Length > ChatLimits.MaximumMessageCharacters || message.CreatedAt == default
            || message.Attachments is null || message.Attachments.Count > ChatLimits.MaximumAttachmentsPerMessage
            || message.LinkPreviews is null || message.Reactions is null || message.ReadByAccountIds is null)
            throw InvalidResponse();
    }

    internal static string Identifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Invalid Messages identifier.");
        return Uri.EscapeDataString(value);
    }

    private static string ThreadPath(string threadId) => "threads/" + Identifier(threadId);
    private static long Positive(long value) => value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static InvalidDataException InvalidResponse() => new("Invalid Messages response.");
}
