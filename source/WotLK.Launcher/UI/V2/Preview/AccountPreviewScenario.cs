namespace WotLK.Launcher.UI.V2.Preview;

public enum AccountPreviewScenario
{
    Profile,
    Fallback,
    AvatarChanged,
    AvatarDeleted,
    Crop,
    Uploading,
    UploadError,
    Removing,
    Security,
    Sessions,
    PasswordChange,
    PasswordError,
    EmailUnverified,
    EmailChange,
    SessionRevoke,
    SessionRevokeError
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
            "avatar" => AccountPreviewScenario.Profile,
            "fallback" or "initial" => AccountPreviewScenario.Fallback,
            "changed" or "avatar-changed" => AccountPreviewScenario.AvatarChanged,
            "deleted" or "avatar-deleted" => AccountPreviewScenario.AvatarDeleted,
            "crop" or "recadrage" => AccountPreviewScenario.Crop,
            "uploading" or "upload" => AccountPreviewScenario.Uploading,
            "upload-error" or "error" => AccountPreviewScenario.UploadError,
            "removing" or "delete" or "suppression" => AccountPreviewScenario.Removing,
            "security" or "securite" => AccountPreviewScenario.Security,
            "sessions" => AccountPreviewScenario.Sessions,
            "password-change" => AccountPreviewScenario.PasswordChange,
            "password-error" => AccountPreviewScenario.PasswordError,
            "email-unverified" => AccountPreviewScenario.EmailUnverified,
            "email-change" => AccountPreviewScenario.EmailChange,
            "session-revoke" => AccountPreviewScenario.SessionRevoke,
            "session-revoke-error" => AccountPreviewScenario.SessionRevokeError,
            _ => AccountPreviewScenario.Profile
        };
    }
}
