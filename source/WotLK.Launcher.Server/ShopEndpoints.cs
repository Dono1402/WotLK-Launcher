using System.Threading.RateLimiting;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal static class ShopEndpoints
{
    internal static void MapShopEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/shop", async (HttpContext context, LauncherDatabase database,
            ShopCatalog catalog, ArmoryReadLimiter limiter, CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            AuthenticatedAccount? account = await AtlasRequestAuthentication.AuthenticateAsync(context, database, cancellationToken);
            if (account is null) return Results.Unauthorized();
            if (context.Request.Query.Count != 0) return Results.BadRequest(new { error = "shop-invalid-query" });
            using RateLimitLease lease = limiter.Acquire(account.AccountId);
            if (!lease.IsAcquired)
            {
                context.Response.Headers["Retry-After"] = "60";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            try
            {
                ShopSnapshot snapshot = catalog.CreateSnapshot(await database.ListShopCharactersAsync(account.AccountId, cancellationToken));
                snapshot.Validate();
                return Results.Json(snapshot);
            }
            catch (Exception error) when (error is MySqlException or InvalidDataException or OverflowException or InvalidCastException)
            {
                return Results.Json(new { error = "shop-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        // No purchase, wallet mutation or payment callback is exposed by this browsing milestone.
    }
}
