namespace WotLK.Launcher.Server;

internal static class AuthenticationEndpointResults
{
    internal static IResult FromLogin(AtlasLoginResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            AtlasLoginOutcome.Succeeded when result.Response is not null =>
                Results.Ok(result.Response),
            AtlasLoginOutcome.AtlasAccountUnavailable => Results.Json(
                new AtlasAuthErrorResponse(
                    AtlasAuthErrorCodes.AccountUnavailableMessage,
                    AtlasAuthErrorCodes.AccountUnavailable),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Unauthorized()
        };
    }

    internal static async Task<IResult> FromRefreshAsync(
        LauncherDatabase.RefreshSessionResult result,
        Func<string, Task> revokeHermesAsync)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(revokeHermesAsync);

        if (result.RevocationReason is not null
            && result.RevokedUsername is { } revokedUsername)
        {
            await revokeHermesAsync(revokedUsername);
        }

        return result.Response is null
            ? Results.Unauthorized()
            : Results.Ok(result.Response);
    }
}

internal static class AuthenticationResponseHeaders
{
    internal static void Apply(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
    }
}
