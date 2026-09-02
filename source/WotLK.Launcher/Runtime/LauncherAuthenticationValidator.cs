using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace WotLK.Launcher.Runtime;

internal readonly record struct LauncherAuthInputValidation(bool IsValid, string Message)
{
    internal static LauncherAuthInputValidation Valid { get; } = new(true, string.Empty);
}

internal static partial class LauncherAuthenticationValidator
{
    internal static LauncherAuthInputValidation Login(string username, bool hasPassword)
    {
        return string.IsNullOrWhiteSpace(username) || !hasPassword
            ? new LauncherAuthInputValidation(
                false,
                "Renseigne ton nom d’utilisateur et ton mot de passe.")
            : LauncherAuthInputValidation.Valid;
    }

    internal static LauncherAuthInputValidation Register(
        string username,
        string email,
        int passwordLength,
        bool hasConfirmation,
        bool passwordsMatch)
    {
        if (string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(email)
            || passwordLength == 0
            || !hasConfirmation)
        {
            return new LauncherAuthInputValidation(false, "Tous les champs sont obligatoires.");
        }

        if (!UsernamePattern().IsMatch(username.Trim()))
        {
            return new LauncherAuthInputValidation(
                false,
                "Le nom doit contenir 3 à 20 lettres, chiffres ou underscores.");
        }

        if (!new EmailAddressAttribute().IsValid(email.Trim()))
        {
            return new LauncherAuthInputValidation(false, "Adresse e-mail invalide.");
        }

        if (passwordLength is < 10 or > 128)
        {
            return new LauncherAuthInputValidation(
                false,
                "Le mot de passe doit contenir entre 10 et 128 caractères.");
        }

        return passwordsMatch
            ? LauncherAuthInputValidation.Valid
            : new LauncherAuthInputValidation(
                false,
                "Les deux mots de passe ne correspondent pas.");
    }

    internal static LauncherAuthInputValidation EnrollExisting(
        string username,
        string email,
        bool hasCurrentPassword,
        int currentPasswordLength)
    {
        if (string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(email)
            || !hasCurrentPassword)
        {
            return new LauncherAuthInputValidation(false, "Tous les champs sont obligatoires.");
        }

        if (username.Trim().Length > 32)
        {
            return new LauncherAuthInputValidation(false, "Le nom du compte WoW est invalide.");
        }

        if (!new EmailAddressAttribute().IsValid(email.Trim()))
        {
            return new LauncherAuthInputValidation(false, "Adresse e-mail invalide.");
        }

        return currentPasswordLength <= 128
            ? LauncherAuthInputValidation.Valid
            : new LauncherAuthInputValidation(false, "Le mot de passe actuel est invalide.");
    }

    [GeneratedRegex("^[A-Za-z0-9_]{3,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
