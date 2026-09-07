using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MySqlConnector;

namespace WotLK.Launcher.Server;

internal static class ChatEndpoints
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4
    };

    internal static void MapChatEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/chat/conversations", (HttpContext context, LauncherDatabase database,
            ChatRequestLimiter limiter, CancellationToken cancellationToken) =>
            ExecuteAsync(context, database, limiter, async accountId =>
            {
                RequireQuery(context, "beforeId", "limit");
                return await database.ListChatConversationsAsync(accountId, ReadCursor(context, "beforeId"),
                    ReadLimit(context, 100), cancellationToken);
            }, cancellationToken));

        app.MapGet("/api/v1/chat/conversations/{friendAccountId:long}/messages", (long friendAccountId,
            HttpContext context, LauncherDatabase database, ChatRequestLimiter limiter, CancellationToken cancellationToken) =>
            ExecuteAsync(context, database, limiter, async accountId =>
            {
                RequireQuery(context, "afterId", "beforeId", "limit");
                return await database.ListChatMessagesAsync(accountId, RequireFriendId(friendAccountId),
                    ReadCursor(context, "afterId"), ReadCursor(context, "beforeId"), ReadLimit(context, 50), cancellationToken);
            }, cancellationToken));

        app.MapPost("/api/v1/chat/conversations/{friendAccountId:long}/messages", (long friendAccountId,
            HttpContext context, LauncherDatabase database, ChatRequestLimiter limiter, CancellationToken cancellationToken) =>
            ExecuteAsync(context, database, limiter, async accountId =>
            {
                RequireQuery(context);
                SendChatMessageRequest request = await ReadRequestAsync<SendChatMessageRequest>(context, cancellationToken);
                return await database.SendChatMessageAsync(accountId, RequireFriendId(friendAccountId), request, cancellationToken);
            }, cancellationToken));

        app.MapPost("/api/v1/chat/conversations/{friendAccountId:long}/read", (long friendAccountId,
            HttpContext context, LauncherDatabase database, ChatRequestLimiter limiter, CancellationToken cancellationToken) =>
            ExecuteAsync(context, database, limiter, async accountId =>
            {
                RequireQuery(context);
                MarkChatReadRequest request = await ReadRequestAsync<MarkChatReadRequest>(context, cancellationToken);
                return await database.MarkChatReadAsync(accountId, RequireFriendId(friendAccountId), request.ThroughMessageId, cancellationToken);
            }, cancellationToken));

        app.MapGet("/api/v1/chat/updates", (HttpContext context, LauncherDatabase database,
            ChatRequestLimiter limiter, CancellationToken cancellationToken) =>
            ExecuteAsync(context, database, limiter, async accountId =>
            {
                RequireQuery(context, "afterId", "limit");
                return await database.PollChatUpdatesAsync(accountId, ReadCursor(context, "afterId") ?? 0,
                    ReadLimit(context, 100), cancellationToken);
            }, cancellationToken));
    }

    private static async Task<IResult> ExecuteAsync<T>(HttpContext context, LauncherDatabase database,
        ChatRequestLimiter limiter, Func<uint, Task<T>> operation, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(context, database, cancellationToken);
            if (account is null) return Results.Unauthorized();
            if (!database.ChatAvailable) throw new ChatOperationException("chat-unavailable");
            using RateLimitLease lease = limiter.Acquire(account.AccountId);
            if (!lease.IsAcquired) throw new ChatOperationException("chat-rate-limited");
            return Results.Ok(await operation(account.AccountId));
        }
        catch (ChatOperationException exception)
        {
            int status = exception.Code switch
            {
                "chat-not-friends" => StatusCodes.Status404NotFound,
                "chat-idempotency-conflict" => StatusCodes.Status409Conflict,
                "chat-rate-limited" => StatusCodes.Status429TooManyRequests,
                "chat-unavailable" => StatusCodes.Status503ServiceUnavailable,
                "chat-request-too-large" => StatusCodes.Status413PayloadTooLarge,
                _ => StatusCodes.Status400BadRequest
            };
            if (status == StatusCodes.Status429TooManyRequests) context.Response.Headers.RetryAfter = "60";
            return Results.Json(new { error = exception.Code }, statusCode: status);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or FormatException)
        {
            return Results.BadRequest(new { error = "chat-invalid-request" });
        }
        catch (Exception exception) when (exception is MySqlException or InvalidDataException or OverflowException or InvalidCastException)
        {
            return Results.Json(new { error = "chat-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<T> ReadRequestAsync<T>(HttpContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType()) throw new ChatOperationException("chat-invalid-request");
        if (context.Request.ContentLength > ChatMessageValidation.MaximumRequestBytes)
            throw new ChatOperationException("chat-request-too-large");
        using MemoryStream payload = new();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (payload.Length + count > ChatMessageValidation.MaximumRequestBytes)
                throw new ChatOperationException("chat-request-too-large");
            payload.Write(buffer, 0, count);
        }
        using JsonDocument document = JsonDocument.Parse(payload.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ChatOperationException("chat-invalid-request");
        HashSet<string> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty field in document.RootElement.EnumerateObject())
            if (!fields.Add(field.Name)) throw new ChatOperationException("chat-invalid-request");
        return document.RootElement.Deserialize<T>(RequestJson) ?? throw new ChatOperationException("chat-invalid-request");
    }

    private static void RequireQuery(HttpContext context, params string[] allowed)
    {
        foreach (var field in context.Request.Query)
            if (!allowed.Contains(field.Key, StringComparer.Ordinal) || field.Value.Count != 1)
                throw new ChatOperationException("chat-invalid-query");
    }

    private static long? ReadCursor(HttpContext context, string name)
    {
        if (!context.Request.Query.TryGetValue(name, out var value)) return null;
        if (!long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
            throw new ChatOperationException("chat-invalid-query");
        return parsed;
    }

    private static int ReadLimit(HttpContext context, int defaultValue)
    {
        long? parsed = ReadCursor(context, "limit");
        if (parsed is null) return defaultValue;
        if (parsed is < 1 or > 100) throw new ChatOperationException("chat-invalid-query");
        return (int)parsed.Value;
    }

    private static uint RequireFriendId(long id) => id is > 0 and <= uint.MaxValue
        ? (uint)id : throw new ChatOperationException("chat-not-friends");
}

internal sealed class ChatRequestLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<uint> _limiter = PartitionedRateLimiter.Create<uint, uint>(accountId =>
        RateLimitPartition.GetFixedWindowLimiter(accountId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 180, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));

    internal RateLimitLease Acquire(uint accountId) => _limiter.AttemptAcquire(accountId);
    public void Dispose() => _limiter.Dispose();
}
