using System.Text.Json;

namespace WotLK.Launcher.Runtime;

internal sealed partial class LauncherRuntime
{
    internal async Task<JsonElement> GetFriendArmoryDataAsync(uint viewerAccountId, uint friendAccountId,
        LauncherArmoryDataRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthSessionSnapshot session = _sessionCoordinator.CurrentSnapshot;
        if (viewerAccountId == 0 || !IsArmorySessionCurrent(session, viewerAccountId))
            throw new UnauthorizedAccessException("Armory session changed.");
        if (friendAccountId == 0 || friendAccountId == viewerAccountId)
            throw new ArgumentOutOfRangeException(nameof(friendAccountId));
        try
        {
            bool refreshed = await _authentication.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!refreshed || !IsArmorySessionCurrent(session, viewerAccountId))
                throw new UnauthorizedAccessException("Armory session changed.");
            // Friendship and character ownership are checked by the authenticated server for every request.
            JsonElement data = await _armoryApi.ReadFriendAsync(friendAccountId, request, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsArmorySessionCurrent(session, viewerAccountId)) throw new UnauthorizedAccessException("Armory session changed.");
            return data;
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
