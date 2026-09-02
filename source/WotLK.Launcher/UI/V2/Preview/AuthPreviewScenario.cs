namespace WotLK.Launcher.UI.V2.Preview;

public enum AuthPreviewScenario
{
    Login,
    Register,
    Loading,
    LoginError,
    RegisterError,
    RegisterValidation,
    EmailWarning,
    ServiceUnavailable
}

internal static class AuthPreviewArguments
{
    private const string PreviewAuthArgument = "--preview-auth";
    private const string PreviewAuthPrefix = "--preview-auth=";

    internal static bool IsRequested(IEnumerable<string> arguments)
    {
        return arguments.Any(argument =>
            string.Equals(argument, PreviewAuthArgument, StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith(PreviewAuthPrefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static AuthPreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        string? argument = arguments.FirstOrDefault(value =>
            string.Equals(value, PreviewAuthArgument, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(PreviewAuthPrefix, StringComparison.OrdinalIgnoreCase));

        if (argument is null || string.Equals(argument, PreviewAuthArgument, StringComparison.OrdinalIgnoreCase))
        {
            return AuthPreviewScenario.Login;
        }

        string normalized = argument[PreviewAuthPrefix.Length..]
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "register" => AuthPreviewScenario.Register,
            "loading" => AuthPreviewScenario.Loading,
            "loginerror" => AuthPreviewScenario.LoginError,
            "registererror" => AuthPreviewScenario.RegisterError,
            "registervalidation" => AuthPreviewScenario.RegisterValidation,
            "emailwarning" => AuthPreviewScenario.EmailWarning,
            "serviceunavailable" => AuthPreviewScenario.ServiceUnavailable,
            _ => AuthPreviewScenario.Login
        };
    }
}
