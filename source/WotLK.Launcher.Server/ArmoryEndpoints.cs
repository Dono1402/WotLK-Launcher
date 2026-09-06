using System.Text.Json;
using System.Threading.RateLimiting;
using MySqlConnector;

namespace WotLK.Launcher.Server;

internal static class ArmoryEndpoints
{
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void MapArmoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/armory/characters", (
            HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken cancellationToken) =>
            ExecuteAsync(context, database, limiter,
                accountId => database.ListArmoryCharactersAsync(accountId, cancellationToken), cancellationToken));

        app.MapGet("/api/v1/armory/characters/{guid:long}/catalog", (
            long guid, HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken cancellationToken) =>
            ExecuteAsync(context, database, limiter,
                accountId => guid is > 0 and <= uint.MaxValue
                    ? database.GetArmoryCatalogAsync(accountId, (uint)guid, cancellationToken)
                    : Task.FromResult<ArmoryCatalog?>(null), cancellationToken));
    }

    private static async Task<IResult> ExecuteAsync<T>(
        HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter,
        Func<uint, Task<T>> read, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(context, database, cancellationToken);
        if (account is null) return Results.Unauthorized();
        // Neither account IDs, object IDs nor SQL are accepted from the caller.
        if (context.Request.Query.Count != 0) return Results.BadRequest(new { error = "armory-invalid-query" });
        using RateLimitLease lease = limiter.Acquire(account.AccountId);
        if (!lease.IsAcquired)
        {
            context.Response.Headers["Retry-After"] = "60";
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        try
        {
            T result = await read(account.AccountId);
            return result is null ? Results.NotFound() : CreateJsonResult(result);
        }
        catch (Exception exception) when (exception is MySqlException or InvalidDataException or OverflowException or InvalidCastException)
        {
            // The public response never exposes queries, schema details or credentials.
            return Results.Json(new { error = "armory-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static IResult CreateJsonResult<T>(T value)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (data.Length > MaximumResponseBytes)
            return Results.Json(new { error = "armory-response-too-large" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        return Results.Bytes(data, "application/json; charset=utf-8");
    }
}

internal sealed class ArmoryReadLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<uint> _limiter = PartitionedRateLimiter.Create<uint, uint>(accountId =>
        RateLimitPartition.GetFixedWindowLimiter(accountId, _ => new FixedWindowRateLimiterOptions
        {
            // A full first roster may require two catalog reads for each of up to fifty characters.
            PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));

    internal RateLimitLease Acquire(uint accountId) => _limiter.AttemptAcquire(accountId);
    public void Dispose() => _limiter.Dispose();
}
