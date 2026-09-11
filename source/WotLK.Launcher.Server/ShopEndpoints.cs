using System.Threading.RateLimiting;
using MySqlConnector;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal static class ShopEndpoints
{
    internal static void MapShopEndpoints(this WebApplication app)
    {
        ShopManualFundingOptions fundingOptions = app.Services.GetService<ShopManualFundingOptions>() ?? new();
        app.MapManualFundingEndpoints(fundingOptions);
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
                if (fundingOptions.CanReadStorage)
                {
                    ShopFundingRead funding = await database.ReadShopFundingAsync(account.AccountId, fundingOptions, cancellationToken);
                    snapshot = snapshot with { EuroBalanceCents = funding.AvailableCents, CreditBalanceEuroCents = funding.CreditCents,
                        History = funding.History, ManualFunding = funding.Funding };
                }
                snapshot.Validate();
                return Results.Json(snapshot);
            }
            catch (Exception error) when (error is MySqlException or InvalidDataException or OverflowException or InvalidCastException)
            {
                return Results.Json(new { error = "shop-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        // Character purchases, gold conversion and automatic payment callbacks remain closed.
    }
}
