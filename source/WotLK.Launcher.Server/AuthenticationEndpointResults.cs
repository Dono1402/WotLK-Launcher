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
}
