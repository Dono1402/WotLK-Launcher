namespace WotLK.Launcher.Server;

internal static class AtlasRequestAuthentication
{
    internal static async Task<AuthenticatedAccount?> AuthenticateAsync(
        HttpContext context,
        LauncherDatabase database,
        CancellationToken cancellationToken)
    {
        string? token = ReadBearer(context);
        return token is null
            ? null
            : await database.AuthenticateAsync(token, cancellationToken);
    }

    internal static string? ReadBearer(HttpContext context)
    {
        string authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[prefix.Length..].Trim()
            : null;
    }
}
