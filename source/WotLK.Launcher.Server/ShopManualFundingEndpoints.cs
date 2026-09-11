using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal static class ShopManualFundingEndpoints
{
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static void MapManualFundingEndpoints(this WebApplication app, ShopManualFundingOptions options)
    {
        app.MapPost("/api/v1/shop/top-ups", (HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken token)
            => Execute(context, database, limiter, options, false, async account =>
            {
                RequireNoQuery(context);
                ShopCreateTopUp input = await ReadInput<ShopCreateTopUp>(context, token);
                return Results.Ok(await database.CreateShopTopUpAsync(account.AccountId, input, options, token));
            }, token, creating:true));
        app.MapPost("/api/v1/shop/top-ups/{id}/cancel", (string id, HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken token)
            => Execute(context, database, limiter, options, false, async account =>
            {
                RequireNoQuery(context); RequireId(id);
                // Pending requests are version 1. A cancellation cannot undo a credit.
                ShopAdminTopUp result = await database.DecideShopTopUpAsync(account.AccountId, id,
                    new("cancel", 1, ""), options, false, token);
                return Results.Ok(result.Request);
            }, token));
        app.MapGet("/api/v1/shop/admin/top-ups", (HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken token)
            => Execute(context, database, limiter, options, true, async _ =>
            {
                if (context.Request.Query.Any(pair => pair.Key is not ("status" or "before") || pair.Value.Count != 1))
                    throw new ShopFundingException("shop-invalid-query", 400);
                string? status = context.Request.Query["status"].FirstOrDefault();
                long? before = null;
                if (context.Request.Query.TryGetValue("before", out var value))
                {
                    if (!long.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed <= 0)
                        throw new ShopFundingException("shop-invalid-query", 400);
                    before = parsed;
                }
                return Results.Ok(await database.ListShopTopUpsAsync(status, before, options, token));
            }, token));
        app.MapGet("/api/v1/shop/admin/top-ups/{id}", (string id, HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken token)
            => Execute(context, database, limiter, options, true, async _ =>
            {
                RequireNoQuery(context); RequireId(id);
                return Results.Ok(await database.ReadShopTopUpAsync(id, options, token));
            }, token));
        app.MapPost("/api/v1/shop/admin/top-ups/{id}/decision", (string id, HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken token)
            => Execute(context, database, limiter, options, true, async account =>
            {
                RequireNoQuery(context); RequireId(id);
                ShopTopUpDecision input = await ReadInput<ShopTopUpDecision>(context, token);
                return Results.Ok(await database.DecideShopTopUpAsync(account.AccountId, id, input, options, true, token));
            }, token));
    }

    private static async Task<IResult> Execute(HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter,
        ShopManualFundingOptions options, bool admin, Func<AuthenticatedAccount, Task<IResult>> action, CancellationToken token, bool creating=false)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        try
        {
            AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(context, database, token);
            if (account is null) return Results.Unauthorized();
            if (!options.CanReadStorage || (creating && !options.Enabled))
                return Error("shop-funding-unavailable", 503);
            if (admin && !options.CanAdminister(account.AccountId)) return Error("shop-admin-required", 403);
            using RateLimitLease lease = limiter.Acquire(account.AccountId);
            if (!lease.IsAcquired)
            {
                context.Response.Headers.RetryAfter = "60";
                return Error("shop-rate-limited", 429);
            }
            return await action(account);
        }
        catch (ShopFundingException error) { return Error(error.Code, error.StatusCode); }
        catch (JsonException) { return Error("shop-invalid-input", 400); }
        catch (MySqlException error) when (error.Number == 1062) { return Error("shop-payment-already-used", 409); }
        catch (Exception error) when (error is MySqlException or OverflowException or InvalidDataException or InvalidCastException)
        { return Error("shop-funding-unavailable", 503); }
    }

    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
    private static void RequireNoQuery(HttpContext context)
    {
        if (context.Request.Query.Count != 0) throw new ShopFundingException("shop-invalid-query", 400);
    }
    private static void RequireId(string id)
    {
        if (!ShopFundingValidation.IsId(id)) throw new ShopFundingException("shop-invalid-request-id", 400);
    }
    private static async Task<T> ReadInput<T>(HttpContext context, CancellationToken token)
    {
        const int limit = 8192;
        if (!context.Request.HasJsonContentType()) throw new ShopFundingException("shop-json-required", 415);
        if (context.Request.ContentLength is > limit) throw new ShopFundingException("shop-input-too-large", 413);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[1024];
        int count;
        while ((count = await context.Request.Body.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > limit) throw new ShopFundingException("shop-input-too-large", 413);
            buffer.Write(chunk, 0, count);
        }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), InputJson) ?? throw new ShopFundingException("shop-invalid-input", 400);
    }
}
