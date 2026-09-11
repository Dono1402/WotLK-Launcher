using System.Text.Json;
using System.Threading.RateLimiting;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal static class ShopPurchaseEndpoints
{
    internal static void MapShopPurchaseEndpoints(this WebApplication app, ShopPurchaseOptions options)
    {
        app.MapPost("/api/v1/shop/orders", (HttpContext context, LauncherDatabase database, ShopCatalog catalog, ArmoryReadLimiter limiter, CancellationToken token)
            => Execute(context, database, limiter, options, async account =>
                await database.CreateShopOrderAsync(account, await ShopManualFundingEndpoints.ReadInput<ShopCreateOrder>(context, token), catalog, options, token), token));
        app.MapPost("/api/v1/shop/orders/{id}/cancel", (string id, HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter, CancellationToken token)
            => Execute(context, database, limiter, options, async account =>
            {
                if (!ShopFundingValidation.IsId(id)) throw new ShopFundingException("shop-invalid-order", 400);
                return await database.RefundShopOrderAsync(account, id, true, token);
            }, token));
    }

    private static async Task<IResult> Execute(HttpContext context, LauncherDatabase database, ArmoryReadLimiter limiter,
        ShopPurchaseOptions options, Func<uint, Task<ShopOrder>> action, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(context, database, token);
            if (account is null) return Results.Unauthorized();
            if (!options.CanReadStorage) throw new ShopFundingException("shop-delivery-unavailable", 503);
            if (context.Request.Query.Count != 0) throw new ShopFundingException("shop-invalid-query", 400);
            using RateLimitLease lease = limiter.Acquire(account.AccountId);
            if (!lease.IsAcquired) { context.Response.Headers.RetryAfter = "60"; throw new ShopFundingException("shop-rate-limited", 429); }
            ShopOrder result = await action(account.AccountId); result.Validate();
            return Results.Ok(result);
        }
        catch (ShopFundingException error) { return Error(error.Code, error.StatusCode); }
        catch (JsonException) { return Error("shop-invalid-input", 400); }
        catch (MySqlException error) when (error.Number == 1062) { return Error("shop-rename-already-pending", 409); }
        catch (Exception error) when (error is MySqlException or InvalidDataException or OverflowException or InvalidCastException)
        { return Error("shop-order-unavailable", 503); }
    }
    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}

internal sealed class ShopOrderRefundWorker(LauncherDatabase database, ShopPurchaseOptions options, ILogger<ShopOrderRefundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!options.CanReadStorage) continue;
            try { await database.ReconcileShopOrdersAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError(error, "Shop order refunds will be retried."); }
        }
    }
}
