using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WotLK.Launcher;

internal static partial class AddonInstallServices
{
    internal const string SupportedInterface = "30403";

    private const string StateFileName = ".atlas-addons.json";
    private const int CopyBufferSize = 1024 * 128;
    private const int MaximumArchiveEntries = 100_000;
    private const long MaximumExpandedArchiveSize = 2L * 1024 * 1024 * 1024;
    private const long MaximumPackageSize = 500L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex AddonIdRegex();

    [GeneratedRegex("^@[a-z0-9-]+@$", RegexOptions.CultureInvariant)]
    private static partial Regex ReplacementTokenRegex();

    internal static string GetAddonsDirectory(string installRoot)
    {
        return Path.Combine(GameInstallServices.GetClassicDirectoryPath(installRoot), "Interface", "AddOns");
    }

    internal static async Task<AddonCatalog> LoadCatalogAsync(HttpClient http, Uri catalogUri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(catalogUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var catalog = await JsonSerializer.DeserializeAsync<AddonCatalog>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Le catalogue d'addons est vide.");

        ValidateCatalog(catalog);
        return catalog;
    }

    internal static IReadOnlyDictionary<string, AddonInspection> Inspect(AddonCatalog catalog, string installRoot)
    {
        var addonsDirectory = GetAddonsDirectory(installRoot);
        var state = LoadState(addonsDirectory);
        var result = new Dictionary<string, AddonInspection>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in catalog.Addons)
        {
            var allFoldersExist = package.Folders.All(folder => Directory.Exists(Path.Combine(addonsDirectory, folder)));
            if (!state.Addons.TryGetValue(package.Id, out var installed))
            {
                result[package.Id] = new AddonInspection(
                    allFoldersExist ? AddonLocalStatus.DetectedUnmanaged : AddonLocalStatus.NotInstalled,
                    IsManaged: false);
                continue;
            }

            if (!allFoldersExist)
            {
                result[package.Id] = new AddonInspection(AddonLocalStatus.MissingFiles, IsManaged: true);
                continue;
            }

            var isCurrent = string.Equals(installed.Version, package.Version, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(installed.Sha256, package.EffectiveInstallHash, StringComparison.OrdinalIgnoreCase) &&
                            package.Folders.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                                .SequenceEqual(installed.Folders.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            result[package.Id] = new AddonInspection(
                isCurrent ? AddonLocalStatus.Installed : AddonLocalStatus.UpdateAvailable,
                IsManaged: true);
        }

        return result;
    }

    internal static async Task ApplySelectionAsync(
        HttpClient http,
        AddonCatalog catalog,
        string installRoot,
        IReadOnlyDictionary<string, bool> selection,
        IProgress<AddonTransferProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!GameInstallServices.HasPlayableClient(installRoot))
        {
            throw new InvalidOperationException("Installe d'abord le client WotLK avant de gérer ses addons.");
        }

        var addonsDirectory = GetAddonsDirectory(installRoot);
        Directory.CreateDirectory(addonsDirectory);
        var state = LoadState(addonsDirectory);

        foreach (var package in catalog.Addons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shouldInstall = selection.TryGetValue(package.Id, out var selected) && selected;

            if (!shouldInstall)
            {
                if (!state.Addons.TryGetValue(package.Id, out var installed))
                {
                    continue;
                }

                log?.Invoke($"Suppression de {package.Name}...");
                RemoveManagedFolders(addonsDirectory, installed.Folders);
                state.Addons.Remove(package.Id);
                SaveState(addonsDirectory, state);
                log?.Invoke($"{package.Name} supprimé.");
                continue;
            }

            var allFoldersExist = package.Folders.All(folder => Directory.Exists(Path.Combine(addonsDirectory, folder)));
            if (state.Addons.TryGetValue(package.Id, out var current) &&
                allFoldersExist &&
                string.Equals(current.Version, package.Version, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.Sha256, package.EffectiveInstallHash, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"{package.Name} est déjà à jour.");
                continue;
            }

            log?.Invoke($"Téléchargement de {package.Name} {package.Version}...");
            await InstallPackageAsync(http, package, addonsDirectory, progress, cancellationToken);

            state.Addons[package.Id] = new InstalledAddonState
            {
                Version = package.Version,
                Sha256 = package.EffectiveInstallHash,
                Folders = [.. package.Folders],
                InstalledAtUtc = DateTimeOffset.UtcNow
            };
            SaveState(addonsDirectory, state);
            log?.Invoke($"{package.Name} {package.Version} installé.");
        }
    }

    private static async Task InstallPackageAsync(
        HttpClient http,
        AddonPackage package,
        string addonsDirectory,
        IProgress<AddonTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.Combine(Path.GetTempPath(), "WotLKLauncher", "Addons", operationId);
        var extractionRoot = Path.Combine(tempRoot, "extracted");
        Directory.CreateDirectory(extractionRoot);

        try
        {
            var mainArchivePath = Path.Combine(tempRoot, "archive-0.zip");
            await DownloadArchiveAsync(http, package.Name, package.Url, package.Size, mainArchivePath, progress, cancellationToken);
            ValidateArchiveHashAndSize(package.Name, package.Size, package.Sha256, mainArchivePath);
            ExtractValidatedArchive(package, mainArchivePath, extractionRoot, package.StripPrefix, cancellationToken);

            for (var index = 0; index < package.Components.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var component = package.Components[index];
                var componentArchivePath = Path.Combine(tempRoot, $"archive-{index + 1}.zip");
                await DownloadArchiveAsync(http, package.Name, component.Url, component.Size, componentArchivePath, progress, cancellationToken);
                ValidateArchiveHashAndSize(package.Name, component.Size, component.Sha256, componentArchivePath);
                ExtractValidatedArchive(package, componentArchivePath, extractionRoot, component.StripPrefix, cancellationToken);
            }

            ApplyTokenReplacements(package, extractionRoot);
            ValidateExtractedFolders(package, extractionRoot);
            InstallExtractedFolders(package, extractionRoot, addonsDirectory, operationId, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task DownloadArchiveAsync(
        HttpClient http,
        string packageName,
        string url,
        long expectedSize,
        string destinationPath,
        IProgress<AddonTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseLength = response.Content.Headers.ContentLength;
        if (responseLength is > 0 && expectedSize > 0 && responseLength.Value != expectedSize)
        {
            throw new InvalidOperationException($"Taille distante invalide pour {packageName}.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
        var buffer = new byte[CopyBufferSize];
        long received = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new AddonTransferProgress(packageName, received, expectedSize));
        }
    }

    private static void ValidateArchiveHashAndSize(string packageName, long expectedSize, string expectedSha256, string archivePath)
    {
        var fileInfo = new FileInfo(archivePath);
        if (fileInfo.Length != expectedSize)
        {
            throw new InvalidOperationException($"Taille invalide pour {packageName}.");
        }

        using var stream = File.OpenRead(archivePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Signature SHA-256 invalide pour {packageName}.");
        }
    }

    private static void ExtractValidatedArchive(
        AddonPackage package,
        string archivePath,
        string extractionRoot,
        string stripPrefix,
        CancellationToken cancellationToken)
    {
        var extractionPrefix = Path.GetFullPath(extractionRoot) + Path.DirectorySeparatorChar;
        var allowedFolders = package.Folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedStripPrefix = NormalizeStripPrefix(stripPrefix);
        var ignoreUnlistedEntries = normalizedStripPrefix.Length > 0;
        long expandedSize = 0;

        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumArchiveEntries)
        {
            throw new InvalidOperationException($"L'archive de {package.Name} contient trop de fichiers.");
        }

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedName = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            var archiveSegments = normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (archiveSegments.Length == 0 || archiveSegments.Any(segment => segment is "." or ".." || segment.Contains(':')))
            {
                throw new InvalidOperationException($"Chemin invalide dans l'archive de {package.Name}.");
            }

            if (normalizedStripPrefix.Length > 0)
            {
                if (string.Equals(normalizedName.TrimEnd('/'), normalizedStripPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var prefixWithSeparator = normalizedStripPrefix + "/";
                if (!normalizedName.StartsWith(prefixWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                normalizedName = normalizedName[prefixWithSeparator.Length..];
            }

            var segments = normalizedName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                continue;
            }

            if (!allowedFolders.Contains(segments[0]))
            {
                if (ignoreUnlistedEntries)
                {
                    continue;
                }

                throw new InvalidOperationException($"L'archive de {package.Name} contient un dossier inattendu: {segments[0]}.");
            }

            expandedSize = checked(expandedSize + entry.Length);
            if (expandedSize > MaximumExpandedArchiveSize)
            {
                throw new InvalidOperationException($"L'archive décompressée de {package.Name} est trop volumineuse.");
            }

            var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixFileType == 0xA000)
            {
                throw new InvalidOperationException($"Lien symbolique refusé dans l'archive de {package.Name}.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(extractionRoot, Path.Combine(segments)));
            if (!destinationPath.StartsWith(extractionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Chemin hors destination dans l'archive de {package.Name}.");
            }


            if (!extractedPaths.Add(destinationPath))
            {
                throw new InvalidOperationException($"Chemin dupliqué dans l'archive de {package.Name}.");
            }

            if (entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (File.Exists(destinationPath))
            {
                throw new InvalidOperationException($"Le composant de {package.Name} tente de remplacer un fichier existant.");
            }

            entry.ExtractToFile(destinationPath, overwrite: false);
        }
    }

    private static void ApplyTokenReplacements(AddonPackage package, string extractionRoot)
    {
        if (package.TokenReplacements.Count == 0)
        {
            return;
        }

        var textExtensions = new HashSet<string>([".lua", ".toc", ".xml"], StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                     .Where(path => textExtensions.Contains(Path.GetExtension(path))))
        {
            var content = File.ReadAllText(filePath, Encoding.UTF8);
            var updated = content;
            foreach (var replacement in package.TokenReplacements)
            {
                updated = updated.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
            }

            if (!string.Equals(content, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(filePath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
    }

    private static void ValidateExtractedFolders(AddonPackage package, string extractionRoot)
    {
        foreach (var folder in package.Folders)
        {
            var folderPath = Path.Combine(extractionRoot, folder);
            var hasCompatibleToc = Directory.Exists(folderPath) &&
                Directory.EnumerateFiles(folderPath, "*.toc", SearchOption.TopDirectoryOnly).Any(tocPath =>
                    File.ReadLines(tocPath).Any(line =>
                        line.StartsWith("## Interface:", StringComparison.OrdinalIgnoreCase) &&
                        line["## Interface:".Length..]
                            .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                            .Contains(SupportedInterface, StringComparer.Ordinal)));

            if (!hasCompatibleToc)
            {
                throw new InvalidOperationException($"Le dossier {folder} ne contient pas de TOC compatible {SupportedInterface}.");
            }
        }
    }

    private static void InstallExtractedFolders(
        AddonPackage package,
        string extractionRoot,
        string addonsDirectory,
        string operationId,
        CancellationToken cancellationToken)
    {
        var preparedFolders = new List<(string Prepared, string Target, string Backup)>();

        try
        {
            foreach (var folder in package.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(extractionRoot, folder);
                var target = Path.Combine(addonsDirectory, folder);
                var prepared = Path.Combine(addonsDirectory, $".atlas-stage-{operationId}-{folder}");
                var backup = Path.Combine(addonsDirectory, $".atlas-backup-{operationId}-{folder}");

                CopyDirectory(source, prepared, cancellationToken);
                preparedFolders.Add((prepared, target, backup));
            }

            foreach (var entry in preparedFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(entry.Target))
                {
                    Directory.Move(entry.Target, entry.Backup);
                }

                Directory.Move(entry.Prepared, entry.Target);
            }

            foreach (var entry in preparedFolders)
            {
                TryDeleteDirectory(entry.Backup);
            }
        }
        catch
        {
            foreach (var entry in preparedFolders.AsEnumerable().Reverse())
            {
                if (Directory.Exists(entry.Backup))
                {
                    TryDeleteDirectory(entry.Target);
                    Directory.Move(entry.Backup, entry.Target);
                }

                TryDeleteDirectory(entry.Prepared);
            }

            throw;
        }
    }

    private static void RemoveManagedFolders(string addonsDirectory, IEnumerable<string> folders)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var movedFolders = new List<(string Target, string Quarantine)>();

        try
        {
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ValidateFolderName(folder);
                var target = Path.Combine(addonsDirectory, folder);
                if (!Directory.Exists(target))
                {
                    continue;
                }

                var quarantine = Path.Combine(addonsDirectory, $".atlas-remove-{operationId}-{folder}");
                Directory.Move(target, quarantine);
                movedFolders.Add((target, quarantine));
            }

            foreach (var entry in movedFolders)
            {
                TryDeleteDirectory(entry.Quarantine);
            }
        }
        catch
        {
            foreach (var entry in movedFolders.AsEnumerable().Reverse())
            {
                if (Directory.Exists(entry.Quarantine) && !Directory.Exists(entry.Target))
                {
                    Directory.Move(entry.Quarantine, entry.Target);
                }
            }

            throw;
        }
    }

    private static AddonInstallState LoadState(string addonsDirectory)
    {
        var statePath = Path.Combine(addonsDirectory, StateFileName);
        if (!File.Exists(statePath))
        {
            return new AddonInstallState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<AddonInstallState>(File.ReadAllText(statePath, Encoding.UTF8), JsonOptions)
                ?? new AddonInstallState();
            var sanitizedAddons = new Dictionary<string, InstalledAddonState>(StringComparer.OrdinalIgnoreCase);
            if (state.Addons is not null)
            {
                foreach (var entry in state.Addons)
                {
                    if (!AddonIdRegex().IsMatch(entry.Key) ||
                        entry.Value is null ||
                        entry.Value.Folders is null ||
                        entry.Value.Folders.Count is 0 or > 20 ||
                        entry.Value.Folders.Any(folder => !IsValidFolderName(folder)))
                    {
                        continue;
                    }

                    sanitizedAddons[entry.Key] = entry.Value;
                }
            }

            state.Addons = sanitizedAddons;
            return state;
        }
        catch (JsonException)
        {
            return new AddonInstallState();
        }
    }

    private static void SaveState(string addonsDirectory, AddonInstallState state)
    {
        Directory.CreateDirectory(addonsDirectory);
        var statePath = Path.Combine(addonsDirectory, StateFileName);
        var tempPath = statePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tempPath, statePath, overwrite: true);
    }

    private static void ValidateCatalog(AddonCatalog catalog)
    {
        if (catalog.SchemaVersion != 1)
        {
            throw new InvalidOperationException("Version de catalogue d'addons non prise en charge.");
        }

        if (!string.Equals(catalog.ClientInterface, SupportedInterface, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Le catalogue ne cible pas l'interface WotLK Classic {SupportedInterface}.");
        }

        if (catalog.Addons is null || catalog.Addons.Count is 0 or > 20)
        {
            throw new InvalidOperationException("Aucun addon n'est disponible dans le catalogue.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in catalog.Addons)
        {
            if (package is null ||
                string.IsNullOrWhiteSpace(package.Id) || !AddonIdRegex().IsMatch(package.Id) || !ids.Add(package.Id) ||
                string.IsNullOrWhiteSpace(package.Name) || package.Name.Length > 50 ||
                string.IsNullOrWhiteSpace(package.Version) || package.Version.Length > 32 ||
                package.Description is null || package.Description.Length > 300 ||
                !string.Equals(package.Interface, SupportedInterface, StringComparison.Ordinal) ||
                !IsValidArchiveDescriptor(package.Url, package.Size, package.Sha256) ||
                !Sha256Regex().IsMatch(package.EffectiveInstallHash) ||
                package.Folders is null || package.Folders.Count is 0 or > 20 ||
                package.Components is null || package.Components.Count > 10 ||
                package.TokenReplacements is null || package.TokenReplacements.Count > 10)
            {
                throw new InvalidOperationException("Entrée invalide dans le catalogue d'addons.");
            }

            _ = NormalizeStripPrefix(package.StripPrefix);

            foreach (var component in package.Components)
            {
                if (component is null ||
                    string.IsNullOrWhiteSpace(component.Name) || component.Name.Length > 80 ||
                    !IsValidArchiveDescriptor(component.Url, component.Size, component.Sha256))
                {
                    throw new InvalidOperationException($"Composant invalide pour {package.Name}.");
                }

                _ = NormalizeStripPrefix(component.StripPrefix);
            }

            foreach (var replacement in package.TokenReplacements)
            {
                if (!ReplacementTokenRegex().IsMatch(replacement.Key) || replacement.Value is null || replacement.Value.Length > 128)
                {
                    throw new InvalidOperationException($"Remplacement de jeton invalide pour {package.Name}.");
                }
            }

            foreach (var folder in package.Folders)
            {
                ValidateFolderName(folder);
                if (!ownedFolders.Add(folder))
                {
                    throw new InvalidOperationException($"Le dossier {folder} appartient à plusieurs addons du catalogue.");
                }
            }
        }
    }

    private static void ValidateFolderName(string folder)
    {
        if (!IsValidFolderName(folder))
        {
            throw new InvalidOperationException("Nom de dossier addon invalide.");
        }
    }

    private static bool IsValidArchiveDescriptor(string url, long size, string sha256)
    {
        return size is > 0 and <= MaximumPackageSize &&
               !string.IsNullOrWhiteSpace(sha256) && Sha256Regex().IsMatch(sha256) &&
               Uri.TryCreate(url, UriKind.Absolute, out var archiveUri) &&
               archiveUri.Scheme is "http" or "https";
    }

    private static string NormalizeStripPrefix(string? stripPrefix)
    {
        var normalized = (stripPrefix ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
        {
            throw new InvalidOperationException("Préfixe d'archive invalide dans le catalogue d'addons.");
        }

        return string.Join('/', segments);
    }

    private static bool IsValidFolderName(string folder)
    {
        return !string.IsNullOrWhiteSpace(folder) &&
               folder is not ("." or "..") &&
               folder.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               string.Equals(folder, Path.GetFileName(folder), StringComparison.Ordinal);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
