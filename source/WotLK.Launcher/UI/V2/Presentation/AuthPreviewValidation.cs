using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal readonly record struct AuthFormValidation(bool IsValid, string Message)
{
    internal static AuthFormValidation Valid { get; } = new(true, string.Empty);
}

internal static partial class AuthPreviewValidation
{
    internal static AuthFormValidation Login(string username, bool hasPassword)
    {
        LauncherAuthInputValidation result = LauncherAuthenticationValidator.Login(
            username,
            hasPassword);
        return new AuthFormValidation(result.IsValid, result.Message);
    }

    internal static AuthFormValidation Register(
        string username,
        string email,
        int passwordLength,
        bool hasConfirmation,
        bool passwordsMatch)
    {
        LauncherAuthInputValidation result = LauncherAuthenticationValidator.Register(
            username,
            email,
            passwordLength,
            hasConfirmation,
            passwordsMatch);
        return new AuthFormValidation(result.IsValid, result.Message);
    }
}
