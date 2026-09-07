using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace WotLK.Launcher.Runtime;

internal sealed record LauncherChatMessage(long Id, Guid ClientMessageId, uint SenderAccountId, uint RecipientAccountId,
    string SenderUsername, string Body, string Origin, DateTimeOffset CreatedAt);
internal sealed record LauncherChatConversation(uint FriendAccountId, string FriendUsername, LauncherChatMessage LastMessage,
    int UnreadCount, long LastReadMessageId, long FriendLastReadMessageId);
internal sealed record LauncherChatConversations(IReadOnlyList<LauncherChatConversation> Conversations, long LastMessageId,
    int UnreadCount, bool HasMore);
internal sealed record LauncherChatMessages(uint FriendAccountId, string FriendUsername, IReadOnlyList<LauncherChatMessage> Messages,
    bool HasMore, long LastReadMessageId, long FriendLastReadMessageId);
internal sealed record LauncherChatUpdates(IReadOnlyList<LauncherChatMessage> Messages, long LastMessageId, bool HasMore);
internal sealed record LauncherChatSendResult(LauncherChatMessage Message, bool IsDuplicate);
internal sealed record LauncherChatReadResult(long LastReadMessageId);

internal interface ILauncherChatApiClient
{
    Task<LauncherChatConversations> GetConversationsAsync(long? beforeId, CancellationToken cancellationToken);
    Task<LauncherChatMessages> GetMessagesAsync(uint accountId, long? beforeId, CancellationToken cancellationToken);
    Task<LauncherChatUpdates> GetUpdatesAsync(long afterId, CancellationToken cancellationToken);
    Task<LauncherChatSendResult> SendAsync(uint accountId, Guid clientMessageId, string body, CancellationToken cancellationToken);
    Task<LauncherChatReadResult> MarkReadAsync(uint accountId, long throughMessageId, CancellationToken cancellationToken);
}

internal sealed class LauncherChatApiException(HttpStatusCode statusCode, string code) : Exception(code)
{
    internal HttpStatusCode StatusCode { get; } = statusCode;
    internal string Code { get; } = code;
}

internal sealed class LauncherChatApiClient(HttpClient client, Uri apiBaseUri) : ILauncherChatApiClient
{
    internal const int MaximumResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    public async Task<LauncherChatConversations> GetConversationsAsync(long? beforeId, CancellationToken cancellationToken)
    {
        ValidateCursor(beforeId);
        LauncherChatConversations result = await ReadAsync<LauncherChatConversations>(HttpMethod.Get,
            "chat/conversations?limit=100" + Cursor("beforeId", beforeId), null, cancellationToken).ConfigureAwait(false);
        if (result.Conversations is null || result.Conversations.Count > 100 || result.LastMessageId < 0 || result.UnreadCount < 0)
            throw InvalidResponse();
        foreach (LauncherChatConversation conversation in result.Conversations)
        {
            if (conversation is null || conversation.FriendAccountId == 0 || string.IsNullOrWhiteSpace(conversation.FriendUsername)
                || conversation.FriendUsername.Length > 64 || conversation.UnreadCount < 0
                || conversation.LastReadMessageId < 0 || conversation.FriendLastReadMessageId < 0)
                throw InvalidResponse();
            ValidateMessage(conversation.LastMessage);
        }
        return result;
    }

    public async Task<LauncherChatMessages> GetMessagesAsync(uint accountId, long? beforeId, CancellationToken cancellationToken)
    {
        ValidateAccount(accountId);
        ValidateCursor(beforeId);
        LauncherChatMessages result = await ReadAsync<LauncherChatMessages>(HttpMethod.Get,
            $"chat/conversations/{accountId}/messages?limit=50" + Cursor("beforeId", beforeId), null, cancellationToken).ConfigureAwait(false);
        if (result.FriendAccountId != accountId || string.IsNullOrWhiteSpace(result.FriendUsername)
            || result.FriendUsername.Length > 64 || result.LastReadMessageId < 0 || result.FriendLastReadMessageId < 0)
            throw InvalidResponse();
        ValidateMessages(result.Messages, 50);
        if (beforeId is long before && result.Messages.Any(message => message.Id >= before)) throw InvalidResponse();
        return result;
    }

    public async Task<LauncherChatUpdates> GetUpdatesAsync(long afterId, CancellationToken cancellationToken)
    {
        ValidateCursor(afterId);
        LauncherChatUpdates result = await ReadAsync<LauncherChatUpdates>(HttpMethod.Get,
            $"chat/updates?afterId={afterId}&limit=100", null, cancellationToken).ConfigureAwait(false);
        ValidateMessages(result.Messages, 100);
        if (result.LastMessageId < afterId || result.Messages.Any(message => message.Id <= afterId || message.Id > result.LastMessageId)
            || (result.HasMore && result.LastMessageId == afterId))
            throw InvalidResponse();
        return result;
    }

    public async Task<LauncherChatSendResult> SendAsync(uint accountId, Guid clientMessageId, string body, CancellationToken cancellationToken)
    {
        ValidateAccount(accountId);
        if (clientMessageId == Guid.Empty || string.IsNullOrWhiteSpace(body) || body.Length > 1000)
            throw new ArgumentException("Invalid chat message.");
        LauncherChatSendResult result = await ReadAsync<LauncherChatSendResult>(HttpMethod.Post,
            $"chat/conversations/{accountId}/messages", new { clientMessageId, body }, cancellationToken).ConfigureAwait(false);
        ValidateMessage(result.Message);
        if (result.Message.ClientMessageId != clientMessageId || result.Message.RecipientAccountId != accountId || result.Message.Body != body)
            throw InvalidResponse();
        return result;
    }

    public async Task<LauncherChatReadResult> MarkReadAsync(uint accountId, long throughMessageId, CancellationToken cancellationToken)
    {
        ValidateAccount(accountId);
        if (throughMessageId <= 0) throw new ArgumentOutOfRangeException(nameof(throughMessageId));
        LauncherChatReadResult result = await ReadAsync<LauncherChatReadResult>(HttpMethod.Post,
            $"chat/conversations/{accountId}/read", new { throughMessageId }, cancellationToken).ConfigureAwait(false);
        if (result.LastReadMessageId < throughMessageId) throw InvalidResponse();
        return result;
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string relative, object? payload, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpRequestMessage request = new(method, new Uri(apiBaseUri, relative));
        if (payload is not null) request.Content = JsonContent.Create(payload, options: Json);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new LauncherChatApiException(response.StatusCode, "chat-unauthorized");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw InvalidResponse();
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using MemoryStream bytes = new();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + read > MaximumResponseBytes) throw InvalidResponse();
            bytes.Write(buffer, 0, read);
        }
        if (!response.IsSuccessStatusCode)
        {
            string code = response.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable => "chat-unavailable",
                HttpStatusCode.Forbidden => "chat-not-friends",
                HttpStatusCode.TooManyRequests => "chat-rate-limited",
                _ => "chat-request-failed"
            };
            try
            {
                using JsonDocument error = JsonDocument.Parse(bytes.ToArray());
                if (error.RootElement.ValueKind == JsonValueKind.Object
                    && error.RootElement.TryGetProperty("error", out JsonElement property) && property.ValueKind == JsonValueKind.String
                    && property.GetString() is string returnedCode && returnedCode.StartsWith("chat-", StringComparison.Ordinal) && returnedCode.Length <= 64)
                    code = returnedCode;
            }
            catch (JsonException) { }
            throw new LauncherChatApiException(response.StatusCode, code);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(bytes.ToArray(), Json) ?? throw InvalidResponse();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid chat response.", exception);
        }
    }

    private static void ValidateAccount(uint accountId)
    {
        if (accountId == 0) throw new ArgumentOutOfRangeException(nameof(accountId));
    }

    private static void ValidateCursor(long? cursor)
    {
        if (cursor < 0) throw new ArgumentOutOfRangeException(nameof(cursor));
    }

    private static string Cursor(string name, long? value) => value is long cursor
        ? $"&{name}={cursor.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : string.Empty;

    private static void ValidateMessages(IReadOnlyList<LauncherChatMessage>? messages, int maximum)
    {
        if (messages is null || messages.Count > maximum) throw InvalidResponse();
        foreach (LauncherChatMessage message in messages) ValidateMessage(message);
        if (messages.Select(message => message.Id).Distinct().Count() != messages.Count) throw InvalidResponse();
    }

    private static void ValidateMessage(LauncherChatMessage? message)
    {
        if (message is null || message.Id <= 0 || message.ClientMessageId == Guid.Empty || message.SenderAccountId == 0
            || message.RecipientAccountId == 0 || message.SenderAccountId == message.RecipientAccountId
            || string.IsNullOrWhiteSpace(message.SenderUsername) || message.SenderUsername.Length > 64
            || string.IsNullOrWhiteSpace(message.Body) || message.Body.Length > 1000
            || string.IsNullOrWhiteSpace(message.Origin) || message.Origin.Length > 32 || message.CreatedAt == default)
            throw InvalidResponse();
    }

    private static InvalidDataException InvalidResponse() => new("Invalid chat response.");
}
