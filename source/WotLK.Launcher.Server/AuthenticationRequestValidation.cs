using System.ComponentModel.DataAnnotations;

namespace WotLK.Launcher.Server;

internal static class AuthenticationRequestValidation
{
    internal static string? ExistingEnrollment(EnrollExistingAccountRequest request)
    {
        string username = request.Username?.Trim() ?? string.Empty;
        if (username.Length is < 1 or > 32)
            return "Renseigne le nom de ton compte WoW.";
        if (string.IsNullOrEmpty(request.CurrentPassword) || request.CurrentPassword.Length > 128)
            return "Renseigne le mot de passe actuel de ton compte WoW.";
        string email = request.Email?.Trim() ?? string.Empty;
        if (!new EmailAddressAttribute().IsValid(email))
            return "Adresse e-mail invalide.";
        return null;
    }
}
