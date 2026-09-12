using System.Text.Json;
using System.Threading.RateLimiting;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal static class ShopGoldConversionEndpoints
{
    internal static void MapShopGoldConversionEndpoints(this WebApplication app, ShopGoldConversionOptions options)
    {
        app.MapPost("/api/v1/shop/conversions", (HttpContext context, LauncherDatabase database,
            ShopCatalog catalog, ArmoryReadLimiter limiter, CancellationToken token) =>
            Execute(context, database, limiter, options, async account =>
                await database.CreateShopGoldConversionAsync(account,
                    await ShopManualFundingEndpoints.ReadInput<ShopCreateGoldConversion>(context, token),
                    catalog, options, token), token));
        app.MapGet("/api/v1/shop/conversions/{id}", (string id, HttpContext context, LauncherDatabase database,
            ArmoryReadLimiter limiter, CancellationToken token) =>
            Execute(context, database, limiter, options, async account =>
            {
                if (!ShopFundingValidation.IsId(id)) throw new ShopFundingException("shop-invalid-conversion", 400);
                return await database.ReadShopGoldConversionAsync(account, id, options, token)
                    ?? throw new ShopFundingException("shop-conversion-not-found", 404);
            }, token));
    }

    private static async Task<IResult> Execute(HttpContext context, LauncherDatabase database,
        ArmoryReadLimiter limiter, ShopGoldConversionOptions options,
        Func<uint, Task<ShopGoldConversion>> action, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(context, database, token);
            if (account is null) return Results.Unauthorized();
            if (!options.CanReadStorage) throw new ShopFundingException("shop-conversion-unavailable", 503);
            if (context.Request.Query.Count != 0) throw new ShopFundingException("shop-invalid-query", 400);
            using RateLimitLease lease = limiter.Acquire(account.AccountId);
            if (!lease.IsAcquired)
            {
                context.Response.Headers.RetryAfter = "60";
                throw new ShopFundingException("shop-rate-limited", 429);
            }
            ShopGoldConversion result = await action(account.AccountId);
            result.Validate();
            return Results.Ok(result);
        }
        catch (ShopFundingException error) { return Error(error.Code, error.StatusCode); }
        catch (JsonException) { return Error("shop-invalid-input", 400); }
        catch (MySqlException error) when (error.Number == 1062) { return Error("shop-conversion-pending", 409); }
        catch (Exception error) when (error is MySqlException or InvalidDataException or OverflowException or InvalidCastException)
        { return Error("shop-conversion-unavailable", 503); }
    }

    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}
