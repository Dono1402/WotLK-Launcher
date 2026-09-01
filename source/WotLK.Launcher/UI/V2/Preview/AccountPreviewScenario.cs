namespace WotLK.Launcher.UI.V2.Preview;

public enum AccountPreviewScenario
{
    Profile,
    Fallback,
    Crop,
    Uploading,
    UploadError,
    Removing,
    Security,
    Sessions
}

internal static class AccountPreviewArguments
{
    private const string Argument = "--preview-account";

    internal static bool IsRequested(IEnumerable<string> arguments)
    {
        return arguments.Any(value =>
            string.Equals(value, Argument, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase));
    }

    internal static AccountPreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        string? value = arguments
            .FirstOrDefault(argument => argument.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase))?
            [(Argument.Length + 1)..];

        return value?.Trim().ToLowerInvariant() switch
        {
            "fallback" or "initial" => AccountPreviewScenario.Fallback,
            "crop" or "recadrage" => AccountPreviewScenario.Crop,
            "uploading" or "upload" => AccountPreviewScenario.Uploading,
            "upload-error" or "error" => AccountPreviewScenario.UploadError,
            "removing" or "delete" or "suppression" => AccountPreviewScenario.Removing,
            "security" or "securite" => AccountPreviewScenario.Security,
            "sessions" => AccountPreviewScenario.Sessions,
            _ => AccountPreviewScenario.Profile
        };
    }
}
