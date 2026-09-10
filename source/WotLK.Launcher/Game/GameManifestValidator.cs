using System.IO;

namespace WotLK.Launcher.Game;

internal static class GameManifestValidator
{
    internal const int MaximumManifestBytes = 8 * 1024 * 1024;
    internal const int MaximumFileCount = 50_000;
    internal const long MaximumFileBytes = 64L * 1024 * 1024 * 1024;
    internal const long MaximumTotalBytes = 512L * 1024 * 1024 * 1024;

    private const int MaximumVersionLength = 128;
    private const int MaximumPathLength = 1024;
    private const int MaximumPathSegmentLength = 255;
    private const int MaximumUrlLength = 4096;

    private static readonly HashSet<string> ReservedDeviceNames = new(
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).Select(index => "COM" + index))
            .Concat(Enumerable.Range(1, 9).Select(index => "LPT" + index)),
        StringComparer.OrdinalIgnoreCase);

    internal static void Validate(LauncherManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.Version)
            || manifest.Version.Length > MaximumVersionLength
            || manifest.Version.Any(char.IsControl))
        {
            throw new InvalidDataException("Version invalide dans le manifeste.");
        }

        if (manifest.Files is null || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("Le manifeste ne contient aucun fichier.");
        }

        if (manifest.Files.Count > MaximumFileCount)
        {
            throw new InvalidDataException("Le manifeste contient trop de fichiers.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.BaseUrl))
        {
            _ = RequireHttpsUri(manifest.BaseUrl, "baseUrl du manifeste");
        }

        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (LauncherFile? file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Entrée de fichier absente dans le manifeste.");
            string normalizedPath = ValidatePath(file.Path);
            if (!paths.Add(normalizedPath))
            {
                throw new InvalidDataException("Chemin de fichier dupliqué dans le manifeste: " + file.Path);
            }

            if (file.Size is < 0 or > MaximumFileBytes)
            {
                throw new InvalidDataException("Taille de fichier invalide dans le manifeste: " + file.Path);
            }

            totalBytes = checked(totalBytes + file.Size);
            if (totalBytes > MaximumTotalBytes)
            {
                throw new InvalidDataException("La taille totale du manifeste dépasse la limite autorisée.");
            }

            if (!IsSha256(file.Sha256))
            {
                throw new InvalidDataException("SHA-256 invalide dans le manifeste: " + file.Path);
            }

            if ((file.Url?.Length ?? 0) > MaximumUrlLength)
            {
                throw new InvalidDataException("URL de fichier trop longue dans le manifeste: " + file.Path);
            }

            _ = ResolveFileUri(manifest, file);
        }
    }

    internal static Uri ResolveFileUri(LauncherManifest manifest, LauncherFile file)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(file);

        if (!string.IsNullOrWhiteSpace(manifest.BaseUrl))
        {
            _ = RequireHttpsUri(manifest.BaseUrl, "baseUrl du manifeste");
        }

        if (!string.IsNullOrWhiteSpace(file.Url)
            && Uri.TryCreate(file.Url, UriKind.Absolute, out Uri? absoluteUri))
        {
            return RequireHttpsUri(absoluteUri, "URL absolue de fichier");
        }

        if (string.IsNullOrWhiteSpace(manifest.BaseUrl))
        {
            throw new InvalidDataException("baseUrl manquant dans le manifeste.");
        }

        Uri baseUri = RequireHttpsUri(manifest.BaseUrl.TrimEnd('/') + "/", "baseUrl du manifeste");
        string relativeUrl = string.IsNullOrWhiteSpace(file.Url)
            ? "files/" + EscapeRelativeUrl(file.Path)
            : file.Url.TrimStart('/');
        if (!Uri.TryCreate(baseUri, relativeUrl, out Uri? resolved))
        {
            throw new InvalidDataException("URL de fichier invalide dans le manifeste: " + file.Path);
        }

        return RequireHttpsUri(resolved, "URL de fichier");
    }

    internal static Uri RequireHttpsUri(Uri uri, string label)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsAllowedRemoteUri(uri))
        {
            throw new InvalidDataException(
                label + " invalide: seule l'origine https://animeclub.fr:443 est autorisée.");
        }

        return uri;
    }

    internal static bool IsAllowedRemoteUri(Uri uri)
    {
        return uri.IsAbsoluteUri
            && uri.AbsoluteUri.Length <= MaximumUrlLength
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, "animeclub.fr", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0;
    }

    internal static Uri RequireHttpsUri(string? value, string label)
    {
        if (value is not { Length: > 0 and <= MaximumUrlLength }
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidDataException(label + " invalide.");
        }

        return RequireHttpsUri(uri, label);
    }

    internal static bool IsSha256(string? value)
    {
        return value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');
    }

    private static string ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength)
        {
            throw new InvalidDataException("Chemin invalide dans le manifeste.");
        }

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.EndsWith('/') || normalized.Contains("//", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Chemin invalide dans le manifeste: " + path);
        }

        string[] segments = normalized.Split('/');
        foreach (string segment in segments)
        {
            string deviceStem = segment.Split('.')[0].TrimEnd(' ', '.');
            if (segment is "." or ".."
                || segment.Length == 0
                || segment.Length > MaximumPathSegmentLength
                || segment.EndsWith('.')
                || segment.EndsWith(' ')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || ReservedDeviceNames.Contains(deviceStem))
            {
                throw new InvalidDataException("Chemin invalide dans le manifeste: " + path);
            }
        }

        return string.Join('/', segments);
    }

    private static string EscapeRelativeUrl(string path)
    {
        return string.Join(
            "/",
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }
}
