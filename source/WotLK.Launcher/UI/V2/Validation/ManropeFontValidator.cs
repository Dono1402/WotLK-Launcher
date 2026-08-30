using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;

namespace WotLK.Launcher.UI.V2.Validation;

internal static class ManropeFontValidator
{
    private const string AssemblyName = "WotLK.Launcher";
    private const string ResourceFolder = "Assets/Fonts/";

    private static readonly FontAsset[] RequiredFonts =
    [
        new("Manrope-Regular.ttf", "2d9a9960fd191a7f1d9060768818074dd2b76ba84a64a35efd2c22bf39030903", 400, "Regular"),
        new("Manrope-Medium.ttf", "42133571c5f19b6d5ded5e3935a92c1dd40721fd8ca2529719eabfa58c123aec", 500, "Medium"),
        new("Manrope-SemiBold.ttf", "e80a2c37e47ec09bff5a9960257bf9001e87d5fb7bf35719ef4958763a0c70ac", 600, "SemiBold"),
        new("Manrope-Bold.ttf", "2da33eb378d59e6314ed7afdfa837cdcb60e41ac8b1f5d3c4909471b95fcf7d9", 700, "Bold"),
        new("Manrope-ExtraBold.ttf", "2522cd61b754100a42a53024148a134c63efc5d114447474a7f13519966cfad5", 800, "ExtraBold")
    ];

    public static void ValidateOrThrow()
    {
        foreach (FontAsset font in RequiredFonts)
        {
            ValidateResourceHash(font);
        }

        Uri fontFolderUri = new(
            $"pack://application:,,,/{AssemblyName};component/{ResourceFolder}",
            UriKind.Absolute);
        ICollection<FontFamily> families = Fonts.GetFontFamilies(fontFolderUri);
        ICollection<Typeface> typefaces = Fonts.GetTypefaces(fontFolderUri);

        FontFamily[] manropeFamilies = families
            .Where(IsManropeFamily)
            .ToArray();
        if (manropeFamilies.Length == 0)
        {
            throw new InvalidOperationException(
                "La famille Manrope n'a pas été trouvée dans les ressources WPF embarquées.");
        }

        HashSet<int> embeddedWeights = typefaces
            .Where(typeface => IsManropeFamily(typeface.FontFamily))
            .Select(typeface => typeface.Weight.ToOpenTypeWeight())
            .ToHashSet();

        string[] missingWeights = RequiredFonts
            .Where(font => !embeddedWeights.Contains(font.OpenTypeWeight))
            .Select(font => $"{font.DisplayName} ({font.OpenTypeWeight})")
            .ToArray();
        if (missingWeights.Length > 0)
        {
            throw new InvalidOperationException(
                $"Graisses Manrope absentes des typefaces WPF : {string.Join(", ", missingWeights)}.");
        }
    }

    private static void ValidateResourceHash(FontAsset font)
    {
        Uri resourceUri = new(
            $"pack://application:,,,/{AssemblyName};component/{ResourceFolder}{font.FileName}",
            UriKind.Absolute);
        System.Windows.Resources.StreamResourceInfo? resource = Application.GetResourceStream(resourceUri);
        if (resource is null)
        {
            throw new InvalidOperationException(
                $"La ressource WPF {font.FileName} est absente ou n'est pas déclarée en Resource.");
        }

        using Stream stream = resource.Stream;
        string actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actualHash, font.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"L'empreinte SHA-256 de {font.FileName} est invalide. Attendue : {font.Sha256}. Obtenue : {actualHash}.");
        }
    }

    private static bool IsManropeFamily(FontFamily family)
    {
        return family.FamilyNames.Values.Any(name =>
                   name.Contains("Manrope", StringComparison.OrdinalIgnoreCase))
               || family.Source.Contains("Manrope", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record FontAsset(
        string FileName,
        string Sha256,
        int OpenTypeWeight,
        string DisplayName);
}
