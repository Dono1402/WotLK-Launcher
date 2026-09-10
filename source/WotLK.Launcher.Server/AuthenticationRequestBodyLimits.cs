using Microsoft.AspNetCore.Http.Features;

namespace WotLK.Launcher.Server;

internal static class AuthenticationRequestBodyLimits
{
    internal const long MaximumJsonBodyBytes = 8 * 1024;

    private static readonly HashSet<string> LimitedRoutes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "POST /api/v1/accounts",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/refresh",
        "POST /api/v1/auth/logout",
        "PATCH /api/v1/me/email",
        "PATCH /api/v1/me/social-profile",
        "POST /api/v1/me/password",
        "PATCH /api/v1/me/avatar",
        "POST /api/v1/friends/requests"
    };

    internal static bool TryConfigure(HttpContext context, out long maximumBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        string path = context.Request.Path.Value ?? string.Empty;
        if (path.Length > 1) path = path.TrimEnd('/');
        string route = context.Request.Method + " " + path;
        if (!LimitedRoutes.Contains(route))
        {
            maximumBodyBytes = 0;
            return false;
        }

        maximumBodyBytes = MaximumJsonBodyBytes;
        IHttpMaxRequestBodySizeFeature? feature =
            context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = maximumBodyBytes;
        return true;
    }

    internal static IApplicationBuilder UseAtlasAuthenticationRequestBodyLimits(
        this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (TryConfigure(context, out long maximumBodyBytes)
                && context.Request.ContentLength is long declaredLength
                && declaredLength > maximumBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            await next(context);
        });
    }
}
