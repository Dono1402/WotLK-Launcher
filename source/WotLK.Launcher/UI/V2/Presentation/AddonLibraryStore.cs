using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WotLK.Launcher.UI.V2.Presentation;

public sealed record AddonSelectionProfile(string Name, ImmutableArray<string> AddonIds);

public sealed record AddonLibraryPreferences(
    ImmutableArray<string> FavoriteIds,
    ImmutableArray<AddonSelectionProfile> Profiles)
{
    public static AddonLibraryPreferences Empty { get; } = new([], []);
}

public interface IAddonLibraryStore
{
    AddonLibraryPreferences Load(string clientRoot);
    void Save(string clientRoot, AddonLibraryPreferences preferences);
}

/// <summary>Stores selections and favorites only. Game settings and addon files are never included.</summary>
public sealed class JsonAddonLibraryStore(string storageDirectory) : IAddonLibraryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly string _storageDirectory = Path.GetFullPath(storageDirectory);

    public AddonLibraryPreferences Load(string clientRoot)
    {
        string path = GetPath(clientRoot);
        if (!File.Exists(path)) return AddonLibraryPreferences.Empty;
        if (new FileInfo(path).Length > AddonSelectionJson.MaximumJsonLength)
            throw new InvalidDataException("Le fichier de sélections est trop volumineux.");
        LibraryFile? file = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(path), JsonOptions);
        if (file is null || file.Version != 1)
            throw new InvalidDataException("Le format des préférences d’addons n’est pas reconnu.");
        return new(
            AddonSelectionJson.NormalizeIds(file.FavoriteIds ?? []),
            (file.Profiles ?? []).Take(30)
                .Where(profile => profile is not null && !string.IsNullOrWhiteSpace(profile.Name))
                .Select(profile => new AddonSelectionProfile(
                    AddonSelectionJson.NormalizeName(profile.Name),
                    AddonSelectionJson.NormalizeIds(profile.AddonIds ?? [])))
                .DistinctBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray());
    }

    public void Save(string clientRoot, AddonLibraryPreferences preferences)
    {
        string path = GetPath(clientRoot);
        LibraryFile file = new(1,
            AddonSelectionJson.NormalizeIds(preferences.FavoriteIds).ToArray(),
            preferences.Profiles.Take(30).Select(profile => new ProfileFile(
                AddonSelectionJson.NormalizeName(profile.Name),
                AddonSelectionJson.NormalizeIds(profile.AddonIds).ToArray())).ToArray());
        string json = JsonSerializer.Serialize(file, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > AddonSelectionJson.MaximumJsonLength)
            throw new InvalidDataException("Le fichier de sélections est trop volumineux.");
        Directory.CreateDirectory(_storageDirectory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string GetPath(string clientRoot)
    {
        string normalized = NormalizeRoot(clientRoot);
        if (normalized.Length == 0) throw new ArgumentException("Le dossier du client est requis.", nameof(clientRoot));
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(_storageDirectory, digest + ".json");
    }

    internal static string NormalizeRoot(string? clientRoot) => string.IsNullOrWhiteSpace(clientRoot)
        ? string.Empty
        : Path.GetFullPath(clientRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();

    private sealed record LibraryFile(int Version, string[] FavoriteIds, ProfileFile[] Profiles);
    private sealed record ProfileFile(string Name, string[] AddonIds);
}

public static class AddonSelectionJson
{
    public const int MaximumJsonLength = 256 * 1024;
    private const string Format = "atlas-addon-selection";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Export(AddonSelectionProfile profile) => JsonSerializer.Serialize(
        new SelectionFile(Format, 1, NormalizeName(profile.Name), NormalizeIds(profile.AddonIds).ToArray()), JsonOptions);

    public static AddonSelectionProfile Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength)
            throw new InvalidDataException("Le fichier de sélection est vide ou trop volumineux.");
        SelectionFile? file = JsonSerializer.Deserialize<SelectionFile>(json, JsonOptions);
        if (file is null || file.Format != Format || file.Version != 1 || file.AddonIds is null)
            throw new InvalidDataException("Ce fichier n’est pas une sélection d’addons Atlas compatible.");
        return new(NormalizeName(file.Name), NormalizeIds(file.AddonIds));
    }

    internal static string NormalizeName(string? value)
    {
        string name = value?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl))
            throw new InvalidDataException("Le nom du profil doit contenir de 1 à 60 caractères.");
        return name;
    }

    internal static ImmutableArray<string> NormalizeIds(IEnumerable<string> values)
    {
        string[] source = values.Take(257).ToArray();
        if (source.Length > 256) throw new InvalidDataException("Une sélection ne peut pas dépasser 256 addons.");
        if (source.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100
            || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))))
            throw new InvalidDataException("La sélection contient un identifiant d’addon invalide.");
        return source.Select(id => id.ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private sealed record SelectionFile(string Format, int Version, string Name, string[] AddonIds);
}
