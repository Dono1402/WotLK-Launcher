using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherRuntime
{
    internal Task<ShopSnapshot> GetShopAsync(CancellationToken token) => CallShopAsync(_shopApi.ReadAsync,token);
    internal Task<ShopOrder> CreateShopOrderAsync(ShopCreateOrder input,CancellationToken token) => CallShopAsync(ct=>_shopApi.CreateOrderAsync(input,ct),token);
    internal Task<ShopOrder> CancelShopOrderAsync(string id,CancellationToken token) => CallShopAsync(ct=>_shopApi.CancelOrderAsync(id,ct),token);
    internal Task<ShopTopUp> CreateShopTopUpAsync(ShopCreateTopUp input,CancellationToken token) => CallShopAsync(ct=>_shopApi.CreateTopUpAsync(input,ct),token);
    internal Task<ShopTopUp> CancelShopTopUpAsync(string id,CancellationToken token) => CallShopAsync(ct=>_shopApi.CancelTopUpAsync(id,ct),token);
    internal Task<ShopAdminTopUpPage> ListShopTopUpsAsync(string? status,long? before,CancellationToken token) => CallShopAsync(ct=>_shopApi.ListTopUpsAsync(status,before,ct),token);
    internal Task<ShopAdminTopUp> ReadShopTopUpAsync(string id,CancellationToken token) => CallShopAsync(ct=>_shopApi.ReadTopUpAsync(id,ct),token);
    internal Task<ShopAdminTopUp> DecideShopTopUpAsync(string id,ShopTopUpDecision input,CancellationToken token) => CallShopAsync(ct=>_shopApi.DecideTopUpAsync(id,input,ct),token);

    private async Task<T> CallShopAsync<T>(Func<CancellationToken,Task<T>> operation,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthSessionSnapshot session = _sessionCoordinator.CurrentSnapshot;
        uint account = _authentication.Session?.Profile.AccountId ?? 0;
        if (account == 0 || !IsArmorySessionCurrent(session, account))
            throw new UnauthorizedAccessException("Shop session changed.");
        try
        {
            bool refreshed = await _authentication.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!refreshed || !IsArmorySessionCurrent(session, account))
                throw new UnauthorizedAccessException("Shop session changed.");
            T result = await operation(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsArmorySessionCurrent(session, account)) throw new UnauthorizedAccessException("Shop session changed.");
            return result;
        }
        catch (Exception error) when (error is UnauthorizedAccessException
            or LauncherAuthException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sessionCoordinator.NotifyAuthenticatedRequestUnauthorized(session.Sequence, cancellationToken);
            throw;
        }
    }
}
