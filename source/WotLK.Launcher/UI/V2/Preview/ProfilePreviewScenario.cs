namespace WotLK.Launcher.UI.V2.Preview;

public enum ProfilePreviewScenario
{
    SignedIn,
    EmailUnverified,
    LoggingOut,
    LogoutError
}

internal static class ProfilePreviewArguments
{
    private const string Prefix = "--preview-profile=";

    internal static bool IsRequested(IEnumerable<string> arguments)
    {
        return arguments.Any(argument =>
            argument.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
    }

    internal static ProfilePreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        string? argument = arguments.FirstOrDefault(value =>
            value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));
        string value = argument is null
            ? string.Empty
            : argument[Prefix.Length..]
                .Trim()
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
        return value switch
        {
            "emailunverified" => ProfilePreviewScenario.EmailUnverified,
            "loggingout" => ProfilePreviewScenario.LoggingOut,
            "logouterror" => ProfilePreviewScenario.LogoutError,
            _ => ProfilePreviewScenario.SignedIn
        };
    }
}
