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
            AtlasLoginOutcome.AtlasProfileRequired => Results.Json(
                new AtlasAuthErrorResponse(
                    AtlasAuthErrorCodes.ProfileRequiredMessage,
                    AtlasAuthErrorCodes.ProfileRequired),
                statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Unauthorized()
        };
    }

    internal static IResult FromEnrollment(AtlasEnrollmentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            AtlasEnrollmentOutcome.Succeeded when result.Response is not null =>
                Results.Ok(result.Response),
            AtlasEnrollmentOutcome.AlreadyEnrolled => Results.Json(
                new AtlasAuthErrorResponse(
                    AtlasAuthErrorCodes.AlreadyEnrolledMessage,
                    AtlasAuthErrorCodes.AlreadyEnrolled),
                statusCode: StatusCodes.Status409Conflict),
            AtlasEnrollmentOutcome.NotEligible => Results.Json(
                new AtlasAuthErrorResponse(
                    AtlasAuthErrorCodes.EnrollmentNotAllowedMessage,
                    AtlasAuthErrorCodes.EnrollmentNotAllowed),
                statusCode: StatusCodes.Status403Forbidden),
            AtlasEnrollmentOutcome.EmailAlreadyUsed => Results.Json(
                new AtlasAuthErrorResponse(
                    AtlasAuthErrorCodes.EmailAlreadyUsedMessage,
                    AtlasAuthErrorCodes.EmailAlreadyUsed),
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Unauthorized()
        };
    }
}
