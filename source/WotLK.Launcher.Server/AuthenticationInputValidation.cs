using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace WotLK.Launcher.Server;

internal sealed record AuthenticationInputRejection(int StatusCode, string? Error)
{
    internal IResult ToResult()
        => Error is null
            ? Results.StatusCode(StatusCode)
            : Results.Json(new { error = Error }, statusCode: StatusCode);
}

internal static class AuthenticationInputValidation
{
    internal const int MaximumEmailLength = 254;
    internal const int MaximumDeviceNameLength = 128;
    internal const long MaximumEmailVerificationFormBytes = 4096;

    private const string RegistrationUsernameError =
        "Le nom d'utilisateur doit contenir 3 à 20 lettres, chiffres ou underscores.";
    private const string RegistrationEmailError = "Adresse e-mail invalide.";
    private const string RegistrationPasswordError =
        "Le mot de passe doit contenir entre 10 et 128 caractères.";
    private const string CurrentPasswordError = "Le mot de passe actuel est obligatoire.";
    private const string NewPasswordError =
        "Le nouveau mot de passe doit contenir entre 10 et 128 caractères.";
    private const string FriendUsernameError =
        "Saisis un nom d'utilisateur Atlas valide.";
    private const string DeviceNameError =
        "Le nom de l'appareil ne peut pas dépasser 128 caractères.";
    private const string SocialProfileError =
        "Le profil social envoyé est invalide.";
    private const string StatusMessageError =
        "Le statut ne peut pas dépasser 80 caractères.";
    private const string BiographyError =
        "La bio ne peut pas dépasser 280 caractères.";
    private const string AvatarRequestError =
        "La demande de changement d'avatar est invalide.";
    private const string UnknownAvatarError = "Avatar inconnu.";

    internal static AuthenticationInputRejection? Registration(RegisterRequest? request)
    {
        string username = request?.Username?.Trim() ?? string.Empty;
        if (!Regex.IsMatch(
                username,
                "^[A-Za-z0-9_]{3,20}$",
                RegexOptions.CultureInvariant))
        {
            return BadRequest(RegistrationUsernameError);
        }

        if (!IsValidEmail(request?.Email))
            return BadRequest(RegistrationEmailError);
        if (request?.Password is not { Length: >= 10 and <= 128 })
            return BadRequest(RegistrationPasswordError);
        return null;
    }

    internal static AuthenticationInputRejection? ChangeEmail(ChangeEmailRequest? request)
        => IsValidEmail(request?.Email)
            ? null
            : BadRequest(RegistrationEmailError);

    internal static AuthenticationInputRejection? DeviceName(string? deviceName)
    {
        return deviceName?.Trim().Length > MaximumDeviceNameLength
            ? BadRequest(DeviceNameError)
            : null;
    }

    internal static bool IsBoundedEmailVerificationForm(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.HasFormContentType
            && request.ContentLength is > 0 and <= MaximumEmailVerificationFormBytes;
    }

    internal static AuthenticationInputRejection? Login(LoginRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.Username)
            || request.Username.Length > 64
            || string.IsNullOrEmpty(request.Password)
            || request.Password.Length > 128)
        {
            return new AuthenticationInputRejection(
                StatusCodes.Status401Unauthorized,
                null);
        }

        return DeviceName(request.DeviceName);
    }

    internal static AuthenticationInputRejection? Refresh(
        RefreshRequest? request,
        out string refreshToken)
    {
        refreshToken = request?.RefreshToken ?? string.Empty;
        return TokenService.IsRefreshToken(refreshToken)
            ? null
            : new AuthenticationInputRejection(
                StatusCodes.Status401Unauthorized,
                null);
    }

    internal static void Logout(
        LogoutRequest? request,
        out string? refreshToken)
    {
        // A bearer-authenticated logout remains valid with a JSON null body.
        // Ignore malformed optional refresh proof and let the access proof win.
        refreshToken = TokenService.IsRefreshToken(request?.RefreshToken)
            ? request!.RefreshToken
            : null;
    }

    internal static AuthenticationInputRejection? Password(ChangePasswordRequest? request)
    {
        if (string.IsNullOrEmpty(request?.CurrentPassword)
            || request.CurrentPassword.Length > 128)
        {
            return BadRequest(CurrentPasswordError);
        }

        if (request.NewPassword is not { Length: >= 10 and <= 128 })
            return BadRequest(NewPasswordError);
        return null;
    }

    internal static AuthenticationInputRejection? Friend(
        CreateFriendRequest? request,
        out string username)
    {
        username = request?.Username?.Trim() ?? string.Empty;
        return username.Length is >= 2 and <= 32
            ? null
            : BadRequest(FriendUsernameError);
    }

    internal static AuthenticationInputRejection? SocialProfile(
        UpdateSocialProfileRequest? request,
        out string statusMessage,
        out string bio)
    {
        statusMessage = request?.StatusMessage?.Trim() ?? string.Empty;
        bio = request?.Bio?.Trim() ?? string.Empty;
        if (request is null)
            return BadRequest(SocialProfileError);
        if (statusMessage.Length > 80)
            return BadRequest(StatusMessageError);
        if (bio.Length > 280)
            return BadRequest(BiographyError);
        return null;
    }

    internal static AuthenticationInputRejection? Avatar(
        ChangeAvatarRequest? request,
        out string? avatarKey)
    {
        avatarKey = string.IsNullOrWhiteSpace(request?.AvatarKey)
            ? null
            : request.AvatarKey.Trim().ToLowerInvariant();
        if (request is null)
            return BadRequest(AvatarRequestError);

        string[] allowed = ["gold", "ice", "emerald", "crimson"];
        return avatarKey is null || allowed.Contains(avatarKey, StringComparer.Ordinal)
            ? null
            : BadRequest(UnknownAvatarError);
    }

    private static AuthenticationInputRejection BadRequest(string error)
        => new(StatusCodes.Status400BadRequest, error);

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        string normalized = email.Trim();
        return normalized.Length <= MaximumEmailLength
            && new EmailAddressAttribute().IsValid(normalized);
    }
}
