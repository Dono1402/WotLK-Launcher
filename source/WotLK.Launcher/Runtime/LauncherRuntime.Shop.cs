using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherRuntime
{
    internal async Task<ShopSnapshot> GetShopAsync(CancellationToken cancellationToken)
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
            ShopSnapshot snapshot = await _shopApi.ReadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsArmorySessionCurrent(session, account)) throw new UnauthorizedAccessException("Shop session changed.");
            return snapshot;
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
