using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WotLK.Launcher;

internal static partial class AddonInstallServices
{
    internal const string SupportedInterface = "30403";
    internal const int MaximumCatalogBytes = 1024 * 1024;
    internal const int MaximumCatalogAddons = 20;
    internal const int MaximumAddonStateBytes = 16 * 1024 * 1024;
    internal const int MaximumCompatibleTocBytes = 1024 * 1024;

    private const string StateFileName = ".atlas-addons.json";
    private const int CopyBufferSize = 1024 * 128;
    private const int MaximumCompatibleTocLineCharacters = 16 * 1024;
    internal const int MaximumArchiveEntries = 100_000;
    private const int MaximumAddonStateJsonDepth = 32;
    internal const int MaximumAddonStateFolderCharacters = 128;
    internal const int MaximumAddonStatePathCharacters = 1024;
    internal const int MaximumAddonStatePathSegments = 64;
    internal const long MaximumExpandedArchiveSize = 2L * 1024 * 1024 * 1024;
    internal const int MaximumTokenReplacementTextFileBytes = 32 * 1024 * 1024;
    private const long MaximumPackageSize = 500L * 1024 * 1024;
    private const int MaximumUrlLength = 4096;
    internal const int MaximumRemoteRedirects = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = MaximumAddonStateJsonDepth
    };
    private static readonly JsonSerializerOptions CatalogJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
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
        ArgumentNullException.ThrowIfNull(http);
        Uri validatedCatalogUri = RequireCatalogUri(catalogUri, "URL du catalogue d'addons");
        using HttpResponseMessage response = await GetWithValidatedRedirectsAsync(
            http,
            validatedCatalogUri,
            RequireCatalogUri,
            "catalogue d'addons",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        byte[] payload = await BoundedJsonHttpContent.ReadAsync(
            response.Content,
            MaximumCatalogBytes,
            "Le catalogue d'addons",
            cancellationToken);
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions
        {
            MaxDepth = CatalogJsonOptions.MaxDepth
        });
        BoundedJsonHttpContent.RejectDuplicateProperties(document.RootElement, "Le catalogue d'addons");
        AddonCatalog catalog = document.RootElement.Deserialize<AddonCatalog>(CatalogJsonOptions)
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
                    package.Folders.Any(folder => Directory.Exists(Path.Combine(addonsDirectory, folder)))
                        ? AddonLocalStatus.DetectedUnmanaged : AddonLocalStatus.NotInstalled,
                    IsManaged: false);
                continue;
            }

            if (!allFoldersExist)
            {
                result[package.Id] = CreateInspection(
                    AddonLocalStatus.MissingFiles,
                    installed);
                continue;
            }

            var isCurrent = string.Equals(installed.Version, package.Version, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(installed.Sha256, package.EffectiveInstallHash, StringComparison.OrdinalIgnoreCase) &&
                            package.Folders.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                                .SequenceEqual(installed.Folders.OrderBy(value => value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            result[package.Id] = CreateInspection(
                isCurrent ? AddonLocalStatus.Installed : AddonLocalStatus.UpdateAvailable,
                installed);
        }

        return result;
    }

    private static AddonInspection CreateInspection(
        AddonLocalStatus status,
        InstalledAddonState installed)
    {
        return new AddonInspection(
            status,
            IsManaged: true,
            installed.Version,
            installed.Sha256,
            installed.Folders.ToArray(),
            installed.InstalledAtUtc)
        {
            HasFileManifest = IsValidFileManifest(installed)
        };
    }

    internal static async Task ApplySelectionAsync(
        HttpClient http,
        AddonCatalog catalog,
        string installRoot,
        IReadOnlyDictionary<string, bool> selection,
        IProgress<AddonTransferProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken,
        bool forceReinstall = false,
        bool allowExternalReplacement = false,
        bool resolveDependencies = true,
        IGameInstallRootLease? rootLease = null,
        IReadOnlySet<string>? forceReinstallIds = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);
        ValidateCatalog(catalog);
        IGameInstallRootLease? ownedRootLease = null;
        IGameInstallDirectoryLease? addonsLease = null;
        try
        {
            rootLease ??= ownedRootLease = GameInstallServices.AcquireGameInstallRootLease(
                installRoot,
                GameInstallRootLeaseMode.ExistingClient);
            installRoot = GameInstallServices.DemandLeaseMatchesGameRoot(
                installRoot,
                rootLease);
            DemandPlayableClientUnderLease(installRoot, rootLease);

            string addonsDirectory = GetAddonsDirectory(installRoot);
            AddonLocalInventory.EnsureNotLinked(addonsDirectory);
            if (Directory.Exists(addonsDirectory))
            {
                addonsLease = rootLease.AcquireDirectory(
                    addonsDirectory,
                    createIfMissing: false);
            }

            AddonInstallState state = LoadState(addonsDirectory, rootLease);
            HashSet<string> installIds = selection
                .Where(pair => pair.Value)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> removeIds = selection
                .Where(pair => !pair.Value)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> forcedInstallIds = forceReinstallIds is null
                ? []
                : forceReinstallIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!forcedInstallIds.IsSubsetOf(installIds))
            {
                throw new ArgumentException(
                    "Un addon forcé doit appartenir à la sélection d'installation.",
                    nameof(forceReinstallIds));
            }
            IReadOnlyList<AddonPackage> installOrder = resolveDependencies
                ? AddonDependencyPlanner.Plan(catalog, installIds)
                : catalog.Addons.Where(package => installIds.Contains(package.Id)).ToArray();
            if (installOrder.Any(package => removeIds.Contains(package.Id)))
            {
                throw new AddonPlanException("dependency-in-use", removeIds);
            }

            PreflightSelection(
                catalog,
                addonsDirectory,
                state,
                installOrder,
                removeIds,
                allowExternalReplacement,
                cancellationToken,
                rootLease);
            // Consent/dependency/path checks finish before a missing AddOns
            // directory is created. Once present, keep its identity locked
            // across every download, replacement, removal and state write.
            addonsLease ??= rootLease.AcquireDirectory(
                addonsDirectory,
                createIfMissing: true);

            // Resolve the complete transition before changing a managed addon folder.
            // This prevents a later package or state-size failure from committing an
            // earlier removal/replacement with the old state still on disk.
            AddonPackage[] removals = catalog.Addons
                .Where(package => removeIds.Contains(package.Id)
                    && state.Addons.ContainsKey(package.Id))
                .ToArray();
            List<AddonPackage> installations = [];
            foreach (AddonPackage package in installOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool allFoldersExist = package.Folders.All(folder =>
                    SafeAddonFolderExists(
                        addonsDirectory,
                        folder,
                        addonsLease));
                if (!forceReinstall
                    && !forcedInstallIds.Contains(package.Id)
                    && state.Addons.TryGetValue(package.Id, out InstalledAddonState? current)
                    && allFoldersExist
                    && string.Equals(
                        current.Version,
                        package.Version,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        current.Sha256,
                        package.EffectiveInstallHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke($"{package.Name} est déjà à jour.");
                    continue;
                }

                installations.Add(package);
            }

            if (removals.Length == 0 && installations.Count == 0)
            {
                return;
            }

            List<PreparedAddonPackage> preparedPackages = [];
            List<AddonFolderMutation> folderMutations = [];
            string transitionId = Guid.NewGuid().ToString("N");
            try
            {
                foreach (AddonPackage package in installations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new AddonTransferProgress(
                        package.Name,
                        BytesReceived: 0,
                        TotalBytes: package.Size,
                        AddonId: package.Id));
                    log?.Invoke($"Téléchargement de {package.Name} {package.Version}...");
                    preparedPackages.Add(await PreparePackageAsync(
                        http,
                        package,
                        addonsDirectory,
                        progress,
                        cancellationToken,
                        rootLease,
                        addonsLease));
                }

                folderMutations = StageFolderTransition(
                    removals,
                    state,
                    preparedPackages,
                    addonsDirectory,
                    transitionId,
                    cancellationToken,
                    rootLease,
                    addonsLease);
                Dictionary<string, Dictionary<string, InstalledAddonFile>> stagedFiles =
                    await CreateStagedFileManifestsAsync(
                        preparedPackages,
                        folderMutations,
                        addonsDirectory,
                        cancellationToken,
                        rootLease);

                AddonInstallState candidate = CloneState(state);
                foreach (AddonPackage package in removals)
                {
                    candidate.Addons.Remove(package.Id);
                }

                foreach (PreparedAddonPackage prepared in preparedPackages)
                {
                    AddonPackage package = prepared.Package;
                    candidate.Addons[package.Id] = new InstalledAddonState
                    {
                        Version = package.Version,
                        Sha256 = package.EffectiveInstallHash,
                        Folders = [.. package.Folders],
                        InstalledAtUtc = DateTimeOffset.UtcNow,
                        Files = stagedFiles[package.Id]
                    };
                }

                CommitValidatedStateTransition(
                    candidate,
                    () => ApplyFolderMutations(
                        folderMutations,
                        addonsDirectory,
                        cancellationToken,
                        rootLease,
                        addonsLease),
                    () => RollbackFolderMutations(
                        folderMutations,
                        rootLease,
                        addonsLease),
                    payload => SavePreparedState(
                        addonsDirectory,
                        payload,
                        rootLease,
                        addonsLease));

                CleanupCommittedFolderMutations(folderMutations, rootLease);
                foreach (AddonPackage package in removals)
                {
                    log?.Invoke($"{package.Name} supprimé.");
                }

                foreach (AddonPackage package in installations)
                {
                    log?.Invoke($"{package.Name} {package.Version} installé.");
                }
            }
            finally
            {
                CleanupStagedFolderMutations(folderMutations, rootLease);
                foreach (PreparedAddonPackage prepared in preparedPackages)
                {
                    TryDeleteDirectory(prepared.TempRoot, rootLease);
                }
            }
        }
        finally
        {
            addonsLease?.Dispose();
            ownedRootLease?.Dispose();
        }
    }

    private static async Task<PreparedAddonPackage> PreparePackageAsync(
        HttpClient http,
        AddonPackage package,
        string addonsDirectory,
        IProgress<AddonTransferProgress>? progress,
        CancellationToken cancellationToken,
        IGameInstallRootLease rootLease,
        IGameInstallDirectoryLease addonsLease)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var tempRoot = Path.Combine(addonsDirectory, $".atlas-work-{operationId}");
        var extractionRoot = Path.Combine(tempRoot, "extracted");
        IGameInstallDirectoryLease? tempRootLease = null;
        IGameInstallDirectoryLease? extractionRootLease = null;
        bool prepared = false;

        try
        {
            addonsLease.DemandChildDirectorySafe(tempRoot, allowMissing: true);
            tempRootLease = rootLease.AcquireDirectory(
                tempRoot,
                createIfMissing: true);
            extractionRootLease = rootLease.AcquireDirectory(
                extractionRoot,
                createIfMissing: true);

            var mainArchivePath = Path.Combine(tempRoot, "archive-0.zip");
            await DownloadArchiveAsync(
                http,
                package.Id,
                package.Name,
                package.Url,
                package.Size,
                mainArchivePath,
                progress,
                cancellationToken,
                tempRootLease);
            long expandedSize;
            using (IGameInstallReadLease archiveLease = rootLease.OpenFileForRead(mainArchivePath))
            {
                ValidateArchiveHashAndSize(
                    package.Name,
                    package.Size,
                    package.Sha256,
                    archiveLease.Stream);
                expandedSize = ExtractValidatedArchive(
                    package,
                    archiveLease.Stream,
                    extractionRoot,
                    package.StripPrefix,
                    expandedSize: 0,
                    cancellationToken,
                    rootLease);
            }

            for (var index = 0; index < package.Components.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var component = package.Components[index];
                var componentArchivePath = Path.Combine(tempRoot, $"archive-{index + 1}.zip");
                await DownloadArchiveAsync(
                    http,
                    package.Id,
                    package.Name,
                    component.Url,
                    component.Size,
                    componentArchivePath,
                    progress,
                    cancellationToken,
                    tempRootLease);
                using IGameInstallReadLease archiveLease = rootLease.OpenFileForRead(
                    componentArchivePath);
                ValidateArchiveHashAndSize(
                    package.Name,
                    component.Size,
                    component.Sha256,
                    archiveLease.Stream);
                expandedSize = ExtractValidatedArchive(
                    package,
                    archiveLease.Stream,
                    extractionRoot,
                    component.StripPrefix,
                    expandedSize,
                    cancellationToken,
                    rootLease);
            }

            _ = ApplyTokenReplacements(
                package,
                extractionRoot,
                expandedSize,
                cancellationToken,
                rootLease);
            await ValidateExtractedFoldersAsync(
                package,
                extractionRoot,
                cancellationToken,
                rootLease);
            _ = await CreateInstalledFileManifestAsync(
                extractionRoot,
                package.Folders,
                cancellationToken,
                rootLease);
            prepared = true;
            return new PreparedAddonPackage(package, tempRoot, extractionRoot);
        }
        finally
        {
            extractionRootLease?.Dispose();
            tempRootLease?.Dispose();
            if (!prepared)
            {
                TryDeleteDirectory(tempRoot, rootLease);
            }
        }
    }

    private sealed record PreparedAddonPackage(
        AddonPackage Package,
        string TempRoot,
        string ExtractionRoot);

    private sealed class AddonFolderMutation(
        string folderName,
        string targetPath,
        string backupPath)
    {
        internal string FolderName { get; } = folderName;
        internal string TargetPath { get; } = targetPath;
        internal string BackupPath { get; } = backupPath;
        internal string? PackageId { get; set; }
        internal string? IncomingPath { get; set; }
        internal bool ExistingMoved { get; set; }
        internal bool IncomingMoved { get; set; }
    }

    private static List<AddonFolderMutation> StageFolderTransition(
        IReadOnlyList<AddonPackage> removals,
        AddonInstallState state,
        IReadOnlyList<PreparedAddonPackage> preparedPackages,
        string addonsDirectory,
        string transitionId,
        CancellationToken cancellationToken,
        IGameInstallRootLease rootLease,
        IGameInstallDirectoryLease addonsLease)
    {
        List<AddonFolderMutation> mutations = [];
        Dictionary<string, AddonFolderMutation> byFolder = new(
            StringComparer.OrdinalIgnoreCase);

        AddonFolderMutation GetOrAddMutation(string folder)
        {
            ValidateFolderName(folder);
            if (byFolder.TryGetValue(folder, out AddonFolderMutation? existing))
            {
                return existing;
            }

            AddonFolderMutation created = new(
                folder,
                Path.Combine(addonsDirectory, folder),
                Path.Combine(
                    addonsDirectory,
                    $".atlas-backup-{transitionId}-{folder}"));
            byFolder.Add(folder, created);
            mutations.Add(created);
            return created;
        }

        try
        {
            foreach (AddonPackage package in removals)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!state.Addons.TryGetValue(
                        package.Id,
                        out InstalledAddonState? installed))
                {
                    continue;
                }

                foreach (string folder in installed.Folders)
                {
                    _ = GetOrAddMutation(folder);
                }
            }

            foreach (PreparedAddonPackage prepared in preparedPackages)
            {
                foreach (string folder in prepared.Package.Folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddonFolderMutation mutation = GetOrAddMutation(folder);
                    if (mutation.IncomingPath is not null)
                    {
                        throw new InvalidDataException(
                            $"Plusieurs paquets tentent de préparer le dossier {folder}.");
                    }

                    string incomingPath = Path.Combine(
                        addonsDirectory,
                        $".atlas-stage-{transitionId}-{folder}");
                    mutation.PackageId = prepared.Package.Id;
                    mutation.IncomingPath = incomingPath;

                    addonsLease.Revalidate();
                    addonsLease.DemandChildDirectorySafe(
                        incomingPath,
                        allowMissing: true);
                    addonsLease.DemandChildDirectorySafe(
                        mutation.BackupPath,
                        allowMissing: true);
                    if (Directory.Exists(incomingPath) ||
                        File.Exists(incomingPath) ||
                        Directory.Exists(mutation.BackupPath) ||
                        File.Exists(mutation.BackupPath))
                    {
                        throw new IOException(
                            "Un dossier de transition d'addon existe déjà.");
                    }

                    string source = Path.Combine(
                        prepared.ExtractionRoot,
                        folder);
                    CopyDirectoryToInstallRoot(
                        source,
                        incomingPath,
                        cancellationToken,
                        rootLease);
                }
            }

            return mutations;
        }
        catch
        {
            CleanupStagedFolderMutations(mutations, rootLease);
            throw;
        }
    }

    private static async Task<Dictionary<string, Dictionary<string, InstalledAddonFile>>>
        CreateStagedFileManifestsAsync(
            IReadOnlyList<PreparedAddonPackage> preparedPackages,
            IReadOnlyList<AddonFolderMutation> mutations,
            string addonsDirectory,
            CancellationToken cancellationToken,
            IGameInstallRootLease rootLease)
    {
        Dictionary<string, AddonFolderMutation> stagedByFolder = mutations
            .Where(mutation => mutation.IncomingPath is not null)
            .ToDictionary(
                mutation => mutation.FolderName,
                StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Dictionary<string, InstalledAddonFile>> result = new(
            StringComparer.OrdinalIgnoreCase);
        int stagedFileCount = 0;

        foreach (PreparedAddonPackage prepared in preparedPackages)
        {
            Dictionary<string, InstalledAddonFile> manifest = new(
                StringComparer.OrdinalIgnoreCase);
            foreach (string folder in prepared.Package.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!stagedByFolder.TryGetValue(
                        folder,
                        out AddonFolderMutation? mutation) ||
                    mutation.IncomingPath is null ||
                    !string.Equals(
                        mutation.PackageId,
                        prepared.Package.Id,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"La préparation du dossier {folder} est incomplète.");
                }

                string incomingPath = mutation.IncomingPath;
                string stagedFolder = Path.GetFileName(incomingPath);
                foreach (string path in EnumerateOwnedFiles(
                             addonsDirectory,
                             [stagedFolder],
                             cancellationToken,
                             rootLease))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++stagedFileCount > MaximumArchiveEntries)
                    {
                        throw new InvalidDataException(
                            "La sélection d'addons dépasse le nombre maximal de fichiers.");
                    }

                    string relativeWithinFolder = Path.GetRelativePath(
                            incomingPath,
                            path)
                        .Replace('\\', '/');
                    string relative = folder + "/" + relativeWithinFolder;
                    using IGameInstallReadLease readLease = rootLease.OpenFileForRead(
                        path);
                    manifest.Add(relative, new InstalledAddonFile
                    {
                        Size = readLease.Stream.Length,
                        Sha256 = Convert.ToHexString(
                                await SHA256.HashDataAsync(
                                    readLease.Stream,
                                    cancellationToken))
                            .ToLowerInvariant()
                    });
                }
            }

            result.Add(prepared.Package.Id, manifest);
        }

        return result;
    }

    private static AddonInstallState CloneState(AddonInstallState source)
    {
        AddonInstallState clone = new()
        {
            SchemaVersion = source.SchemaVersion,
            Addons = new Dictionary<string, InstalledAddonState>(
                StringComparer.OrdinalIgnoreCase)
        };
        foreach ((string addonId, InstalledAddonState addon) in source.Addons)
        {
            clone.Addons.Add(addonId, new InstalledAddonState
            {
                Version = addon.Version,
                Sha256 = addon.Sha256,
                Folders = [.. addon.Folders],
                InstalledAtUtc = addon.InstalledAtUtc,
                Files = addon.Files is null
                    ? null
                    : addon.Files.ToDictionary(
                        pair => pair.Key,
                        pair => new InstalledAddonFile
                        {
                            Size = pair.Value.Size,
                            Sha256 = pair.Value.Sha256
                        },
                        StringComparer.OrdinalIgnoreCase)
            });
        }

        return clone;
    }

    private static void ApplyFolderMutations(
        IReadOnlyList<AddonFolderMutation> mutations,
        string addonsDirectory,
        CancellationToken cancellationToken,
        IGameInstallRootLease rootLease,
        IGameInstallDirectoryLease addonsLease)
    {
        // Revalidate every path before the first managed directory is moved.
        foreach (AddonFolderMutation mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            addonsLease.Revalidate();
            addonsLease.DemandChildDirectorySafe(
                mutation.TargetPath,
                allowMissing: true);
            addonsLease.DemandChildDirectorySafe(
                mutation.BackupPath,
                allowMissing: true);
            AddonLocalInventory.EnsureNotLinked(mutation.TargetPath);
            if (File.Exists(mutation.TargetPath) ||
                File.Exists(mutation.BackupPath) ||
                Directory.Exists(mutation.BackupPath))
            {
                throw new IOException(
                    "La destination d'une transition d'addon n'est plus disponible.");
            }

            if (Directory.Exists(mutation.TargetPath))
            {
                foreach (string _ in EnumerateOwnedFiles(
                             addonsDirectory,
                             [mutation.FolderName],
                             cancellationToken,
                             rootLease))
                {
                }
            }

            if (mutation.IncomingPath is not null)
            {
                addonsLease.DemandChildDirectorySafe(
                    mutation.IncomingPath,
                    allowMissing: false);
                foreach (string _ in EnumerateOwnedFiles(
                             addonsDirectory,
                             [Path.GetFileName(mutation.IncomingPath)],
                             cancellationToken,
                             rootLease))
                {
                }
            }
        }

        foreach (AddonFolderMutation mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            addonsLease.Revalidate();
            if (Directory.Exists(mutation.TargetPath))
            {
                Directory.Move(mutation.TargetPath, mutation.BackupPath);
                mutation.ExistingMoved = true;
                addonsLease.DemandChildDirectorySafe(
                    mutation.BackupPath,
                    allowMissing: false);
            }

            if (mutation.IncomingPath is null)
            {
                continue;
            }

            Directory.Move(mutation.IncomingPath, mutation.TargetPath);
            mutation.IncomingMoved = true;
            addonsLease.DemandChildDirectorySafe(
                mutation.TargetPath,
                allowMissing: false);
        }
    }

    private static void RollbackFolderMutations(
        IReadOnlyList<AddonFolderMutation> mutations,
        IGameInstallRootLease rootLease,
        IGameInstallDirectoryLease addonsLease)
    {
        List<Exception> failures = [];
        foreach (AddonFolderMutation mutation in mutations.Reverse())
        {
            if (mutation.IncomingMoved)
            {
                try
                {
                    if (Directory.Exists(mutation.TargetPath))
                    {
                        DeleteDirectoryTreeNoFollow(
                            mutation.TargetPath,
                            rootLease);
                    }
                    else if (File.Exists(mutation.TargetPath))
                    {
                        throw new IOException(
                            "Un fichier empêche la restauration d'un addon.");
                    }

                    mutation.IncomingMoved = false;
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }

            if (mutation.ExistingMoved)
            {
                try
                {
                    addonsLease.Revalidate();
                    addonsLease.DemandChildDirectorySafe(
                        mutation.TargetPath,
                        allowMissing: true);
                    addonsLease.DemandChildDirectorySafe(
                        mutation.BackupPath,
                        allowMissing: false);
                    if (Directory.Exists(mutation.TargetPath) ||
                        File.Exists(mutation.TargetPath) ||
                        !Directory.Exists(mutation.BackupPath))
                    {
                        throw new IOException(
                            "La destination d'un addon ne peut pas être restaurée.");
                    }

                    Directory.Move(mutation.BackupPath, mutation.TargetPath);
                    mutation.ExistingMoved = false;
                    addonsLease.DemandChildDirectorySafe(
                        mutation.TargetPath,
                        allowMissing: false);
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
        }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "La restauration des dossiers d'addons a échoué.",
                failures);
        }
    }

    private static void CleanupCommittedFolderMutations(
        IEnumerable<AddonFolderMutation> mutations,
        IGameInstallRootLease rootLease)
    {
        foreach (AddonFolderMutation mutation in mutations)
        {
            if (mutation.ExistingMoved)
            {
                TryDeleteDirectory(mutation.BackupPath, rootLease);
            }
        }
    }

    private static void CleanupStagedFolderMutations(
        IEnumerable<AddonFolderMutation> mutations,
        IGameInstallRootLease rootLease)
    {
        foreach (AddonFolderMutation mutation in mutations)
        {
            if (!mutation.IncomingMoved && mutation.IncomingPath is not null)
            {
                TryDeleteDirectory(mutation.IncomingPath, rootLease);
            }
        }
    }

    private static async Task DownloadArchiveAsync(
        HttpClient http,
        string packageId,
        string packageName,
        string url,
        long expectedSize,
        string destinationPath,
        IProgress<AddonTransferProgress>? progress,
        CancellationToken cancellationToken,
        IGameInstallDirectoryLease destinationDirectoryLease)
    {
        Uri archiveUri = RequireArchiveUri(url, "URL d'archive d'addon");
        if (expectedSize is <= 0 or > MaximumPackageSize)
        {
            throw new InvalidDataException($"Taille d'archive invalide pour {packageName}.");
        }

        using HttpResponseMessage response = await GetWithValidatedRedirectsAsync(
            http,
            archiveUri,
            RequireArchiveUri,
            "archive d'addon",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseLength = response.Content.Headers.ContentLength;
        if (responseLength is < 0 || responseLength.HasValue && responseLength.Value != expectedSize)
        {
            throw new InvalidOperationException($"Taille distante invalide pour {packageName}.");
        }

        destinationDirectoryLease.DemandChildFileSafe(destinationPath, allowMissing: true);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            CopyBufferSize,
            useAsync: true);
        var buffer = new byte[CopyBufferSize];
        long received = 0;

        while (true)
        {
            int requested = (int)Math.Min(buffer.Length, expectedSize - received + 1L);
            var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (received > expectedSize - read)
            {
                throw new InvalidOperationException($"Taille distante invalide pour {packageName}.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new AddonTransferProgress(
                packageName,
                received,
                expectedSize,
                packageId));
        }

        if (received != expectedSize)
        {
            throw new InvalidOperationException($"Taille distante invalide pour {packageName}.");
        }
    }

    private static void ValidateArchiveHashAndSize(
        string packageName,
        long expectedSize,
        string expectedSha256,
        Stream archiveStream)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (!archiveStream.CanRead || !archiveStream.CanSeek)
        {
            throw new ArgumentException("Le flux d'archive doit être lisible et repositionnable.", nameof(archiveStream));
        }

        if (archiveStream.Length != expectedSize)
        {
            throw new InvalidOperationException($"Taille invalide pour {packageName}.");
        }

        archiveStream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(archiveStream));
        if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Signature SHA-256 invalide pour {packageName}.");
        }

        archiveStream.Position = 0;
    }

    private static long ExtractValidatedArchive(
        AddonPackage package,
        Stream archiveStream,
        string extractionRoot,
        string stripPrefix,
        long expandedSize,
        CancellationToken cancellationToken,
        IGameInstallRootLease rootLease)
    {
        var extractionPrefix = Path.GetFullPath(extractionRoot) + Path.DirectorySeparatorChar;
        var allowedFolders = package.Folders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extractedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedStripPrefix = NormalizeStripPrefix(stripPrefix);
        var ignoreUnlistedEntries = normalizedStripPrefix.Length > 0;
        if (expandedSize is < 0 or > MaximumExpandedArchiveSize)
        {
            throw new InvalidDataException("La taille décompressée cumulée des archives est invalide.");
        }

        using var archive = new ZipArchive(
            archiveStream,
            ZipArchiveMode.Read,
            leaveOpen: true);
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

            string stateRelativePath = string.Join('/', segments);
            if (segments.Length > MaximumAddonStatePathSegments ||
                stateRelativePath.Length > MaximumAddonStatePathCharacters ||
                segments.Any(segment =>
                    segment.Length > MaximumAddonStateFolderCharacters ||
                    !IsValidFolderName(segment)))
            {
                throw new InvalidDataException(
                    $"Un chemin de l'archive de {package.Name} dépasse les limites de l'état local.");
            }

            if (!allowedFolders.Contains(segments[0]))
            {
                if (ignoreUnlistedEntries)
                {
                    continue;
                }

                throw new InvalidOperationException($"L'archive de {package.Name} contient un dossier inattendu: {segments[0]}.");
            }

            if (entry.Length < 0
                || entry.Length > MaximumExpandedArchiveSize - expandedSize)
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
                using (rootLease.AcquireDirectory(
                           destinationPath,
                           createIfMissing: true))
                {
                }
                continue;
            }

            string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
            using IGameInstallDirectoryLease destinationLease = rootLease.AcquireDirectory(
                destinationDirectory,
                createIfMissing: true);
            destinationLease.DemandChildFileSafe(destinationPath, allowMissing: true);
            if (File.Exists(destinationPath))
            {
                throw new InvalidOperationException($"Le composant de {package.Name} tente de remplacer un fichier existant.");
            }

            using Stream entryStream = entry.Open();
            expandedSize = CopyArchiveEntryToFileBounded(
                entryStream,
                destinationPath,
                entry.Length,
                expandedSize,
                cancellationToken,
                destinationLease);
        }

        return expandedSize;
    }

    internal static long CopyArchiveEntryToFileBounded(
        Stream source,
        string destinationPath,
        long declaredLength,
        long expandedSize,
        CancellationToken cancellationToken,
        IGameInstallDirectoryLease? destinationDirectoryLease = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Le flux de l'entrée ZIP doit être lisible.", nameof(source));
        }

        if (declaredLength < 0
            || expandedSize < 0
            || expandedSize > MaximumExpandedArchiveSize
            || declaredLength > MaximumExpandedArchiveSize - expandedSize)
        {
            throw new InvalidDataException("La taille décompressée de l'entrée ZIP dépasse la limite autorisée.");
        }

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        destinationDirectoryLease?.DemandChildFileSafe(
            fullDestinationPath,
            allowMissing: true);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        bool destinationCreated = false;
        try
        {
            using FileStream destination = new(
                fullDestinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.SequentialScan);
            destinationCreated = true;

            long entryBytesWritten = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, 0, CopyBufferSize);
                if (read == 0)
                {
                    break;
                }

                if (read > declaredLength - entryBytesWritten
                    || read > MaximumExpandedArchiveSize - expandedSize - entryBytesWritten)
                {
                    throw new InvalidDataException(
                        "Une entrée ZIP produit davantage d'octets que sa taille déclarée.");
                }

                destination.Write(buffer, 0, read);
                entryBytesWritten += read;
            }

            if (entryBytesWritten != declaredLength)
            {
                throw new InvalidDataException(
                    "La taille réellement extraite d'une entrée ZIP ne correspond pas à sa taille déclarée.");
            }

            return checked(expandedSize + entryBytesWritten);
        }
        catch
        {
            if (destinationCreated)
            {
                try
                {
                    if (destinationDirectoryLease is not null)
                    {
                        destinationDirectoryLease.DeleteChildFile(fullDestinationPath);
                    }
                    else
                    {
                        File.Delete(fullDestinationPath);
                    }
                }
                catch (Exception cleanupError) when (cleanupError is IOException
                                                     or UnauthorizedAccessException
                                                     or InvalidDataException)
                {
                }
            }

            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long ApplyTokenReplacements(
        AddonPackage package,
        string extractionRoot,
        long expandedSize,
        CancellationToken cancellationToken,
        IGameInstallRootLease? rootLease = null)
    {
        if (package.TokenReplacements.Count == 0)
        {
            return expandedSize;
        }

        var textExtensions = new HashSet<string>([".lua", ".toc", ".xml"], StringComparer.OrdinalIgnoreCase);
        string[] textFiles = rootLease is null
            ? Directory.EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                .Where(path => textExtensions.Contains(Path.GetExtension(path)))
                .ToArray()
            : EnumerateOwnedFiles(
                    extractionRoot,
                    package.Folders,
                    cancellationToken,
                    rootLease)
                .Where(path => textExtensions.Contains(Path.GetExtension(path)))
                .ToArray();
        foreach (var filePath in textFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            expandedSize = ApplyTokenReplacementsToFileBounded(
                filePath,
                package.TokenReplacements,
                expandedSize,
                MaximumTokenReplacementTextFileBytes,
                cancellationToken,
                rootLease);
        }

        return expandedSize;
    }

    internal static long ApplyTokenReplacementsToFileBounded(
        string filePath,
        IReadOnlyDictionary<string, string> replacements,
        long expandedSize,
        int maximumTextFileBytes,
        CancellationToken cancellationToken,
        IGameInstallRootLease? rootLease = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(replacements);
        if (maximumTextFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTextFileBytes));
        }

        if (expandedSize is < 0 or > MaximumExpandedArchiveSize)
        {
            throw new InvalidDataException("La taille décompressée cumulée des archives est invalide.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        long sourceLength;
        string content;
        string fullFilePath = Path.GetFullPath(filePath);
        IGameInstallReadLease? readLease = null;
        FileStream? standaloneSource = null;
        try
        {
            Stream source;
            if (rootLease is not null)
            {
                readLease = rootLease.OpenFileForRead(fullFilePath);
                source = readLease.Stream;
            }
            else
            {
                standaloneSource = new FileStream(
                    fullFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                source = standaloneSource;
            }

            sourceLength = source.Length;
            if (sourceLength > maximumTextFileBytes)
            {
                throw new InvalidDataException(
                    $"Un fichier texte d'addon dépasse la limite de {maximumTextFileBytes} octets.");
            }

            if (sourceLength > expandedSize)
            {
                throw new InvalidDataException("La taille du fichier texte dépasse la taille extraite cumulée.");
            }

            using StreamReader reader = new(
                source,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: true);
            content = reader.ReadToEnd();
        }
        finally
        {
            readLease?.Dispose();
            standaloneSource?.Dispose();
        }

        var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        int updatedByteCount = utf8WithoutBom.GetByteCount(content);
        if (updatedByteCount > maximumTextFileBytes)
        {
            throw new InvalidDataException(
                $"Un fichier texte d'addon dépasse la limite de {maximumTextFileBytes} octets après décodage.");
        }

        string updated = content;
        bool changed = false;
        foreach (var replacement in replacements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(replacement.Key) || replacement.Value is null)
            {
                throw new InvalidDataException("Remplacement de jeton invalide dans le catalogue d'addons.");
            }

            int occurrences = CountNonOverlappingOccurrences(
                updated,
                replacement.Key,
                cancellationToken);
            if (occurrences == 0)
            {
                continue;
            }

            int tokenByteCount = utf8WithoutBom.GetByteCount(replacement.Key);
            int replacementByteCount = utf8WithoutBom.GetByteCount(replacement.Value);
            long projectedByteCount = checked(
                (long)updatedByteCount
                + (long)occurrences * (replacementByteCount - tokenByteCount));
            long projectedExpandedSize = checked(expandedSize - sourceLength + projectedByteCount);
            if (projectedByteCount < 0
                || projectedByteCount > maximumTextFileBytes
                || projectedExpandedSize is < 0 or > MaximumExpandedArchiveSize)
            {
                throw new InvalidDataException(
                    "Les remplacements de jetons dépassent la limite de taille autorisée.");
            }

            updated = updated.Replace(replacement.Key, replacement.Value, StringComparison.Ordinal);
            updatedByteCount = utf8WithoutBom.GetByteCount(updated);
            if (updatedByteCount != projectedByteCount)
            {
                throw new InvalidDataException(
                    "La taille du fichier texte remplacé ne correspond pas à la taille calculée.");
            }

            changed = true;
        }

        if (!changed)
        {
            return expandedSize;
        }

        long finalExpandedSize = checked(expandedSize - sourceLength + updatedByteCount);
        if (finalExpandedSize is < 0 or > MaximumExpandedArchiveSize)
        {
            throw new InvalidDataException(
                "Les remplacements de jetons dépassent la taille décompressée cumulée autorisée.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (rootLease is not null)
        {
            rootLease.WriteFileAtomically(
                fullFilePath,
                stream =>
                {
                    using StreamWriter writer = new(
                        stream,
                        utf8WithoutBom,
                        bufferSize: 4096,
                        leaveOpen: true);
                    writer.Write(updated);
                    writer.Flush();
                });
        }
        else
        {
            File.WriteAllText(fullFilePath, updated, utf8WithoutBom);
        }

        return finalExpandedSize;
    }

    private static int CountNonOverlappingOccurrences(
        string content,
        string token,
        CancellationToken cancellationToken)
    {
        int count = 0;
        int searchStart = 0;
        while (searchStart <= content.Length - token.Length)
        {
            int found = content.IndexOf(token, searchStart, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            count = checked(count + 1);
            searchStart = found + token.Length;
            if ((count & 0x0FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return count;
    }

    internal static async Task ValidateExtractedFoldersAsync(
        AddonPackage package,
        string extractionRoot,
        CancellationToken cancellationToken,
        IGameInstallRootLease? rootLease = null)
    {
        foreach (var folder in package.Folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderPath = Path.Combine(extractionRoot, folder);
            bool hasCompatibleToc = false;
            if (Directory.Exists(folderPath))
            {
                using IGameInstallDirectoryLease? folderLease = rootLease?.AcquireDirectory(
                    folderPath,
                    createIfMissing: false);
                foreach (string tocPath in Directory.EnumerateFiles(folderPath, "*.toc", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddonLocalInventory.EnsureNotLinked(tocPath);
                    folderLease?.DemandChildFileSafe(tocPath, allowMissing: false);
                    using IGameInstallReadLease? readLease = rootLease?.OpenFileForRead(tocPath);
                    using FileStream? standaloneSource = rootLease is null
                        ? new FileStream(
                            Path.GetFullPath(tocPath),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 4096,
                            FileOptions.Asynchronous | FileOptions.SequentialScan)
                        : null;
                    Stream source = readLease?.Stream ?? standaloneSource!;
                    if (source.Length is <= 0 or > MaximumCompatibleTocBytes)
                    {
                        throw new InvalidDataException(
                            $"Un TOC d'addon doit contenir entre 1 et {MaximumCompatibleTocBytes} octets.");
                    }

                    using StreamReader reader = new(
                        source,
                        Encoding.UTF8,
                        detectEncodingFromByteOrderMarks: true,
                        bufferSize: 4096,
                        leaveOpen: true);
                    while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        if (line.Length > MaximumCompatibleTocLineCharacters)
                        {
                            throw new InvalidDataException(
                                $"Une ligne de TOC d'addon dépasse {MaximumCompatibleTocLineCharacters} caractères.");
                        }

                        if (line.StartsWith("## Interface:", StringComparison.OrdinalIgnoreCase) &&
                            line["## Interface:".Length..]
                                .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                                .Contains(SupportedInterface, StringComparer.Ordinal))
                        {
                            hasCompatibleToc = true;
                        }
                    }
                }
            }

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
        CancellationToken cancellationToken,
        IGameInstallRootLease rootLease,
        IGameInstallDirectoryLease addonsLease)
    {
        var preparedFolders = new List<(string Prepared, string Target, string Backup)>();

        try
        {
            foreach (var folder in package.Folders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = Path.Combine(extractionRoot, folder);
                string target = Path.Combine(addonsDirectory, folder);
                AddonLocalInventory.EnsureNotLinked(target);
                string prepared = Path.Combine(
                    addonsDirectory,
                    $".atlas-stage-{operationId}-{folder}");
                string backup = Path.Combine(
                    addonsDirectory,
                    $".atlas-backup-{operationId}-{folder}");

                addonsLease.Revalidate();
                addonsLease.DemandChildDirectorySafe(prepared, allowMissing: true);
                addonsLease.DemandChildDirectorySafe(backup, allowMissing: true);
                CopyDirectoryToInstallRoot(
                    source,
                    prepared,
                    cancellationToken,
                    rootLease);
                preparedFolders.Add((prepared, target, backup));
            }

            foreach (var entry in preparedFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                addonsLease.Revalidate();
                addonsLease.DemandChildDirectorySafe(entry.Prepared, allowMissing: false);
                addonsLease.DemandChildDirectorySafe(entry.Target, allowMissing: true);
                addonsLease.DemandChildDirectorySafe(entry.Backup, allowMissing: true);
                foreach (string _ in EnumerateOwnedFiles(
                             addonsDirectory,
                             [Path.GetFileName(entry.Prepared)],
                             cancellationToken,
                             rootLease))
                {
                }

                if (Directory.Exists(entry.Target))
                {
                    foreach (string _ in EnumerateOwnedFiles(
                                 addonsDirectory,
                                 [Path.GetFileName(entry.Target)],
                                 cancellationToken,
                                 rootLease))
                    {
                    }

                    Directory.Move(entry.Target, entry.Backup);
                    addonsLease.DemandChildDirectorySafe(entry.Backup, allowMissing: false);
                }

                Directory.Move(entry.Prepared, entry.Target);
                addonsLease.DemandChildDirectorySafe(entry.Target, allowMissing: false);
            }

            foreach (var entry in preparedFolders)
            {
                TryDeleteDirectory(entry.Backup, rootLease);
            }
        }
        catch
        {
            foreach (var entry in preparedFolders.AsEnumerable().Reverse())
            {
                if (Directory.Exists(entry.Backup))
                {
                    addonsLease.DemandChildDirectorySafe(entry.Backup, allowMissing: false);
                    TryDeleteDirectory(entry.Target, rootLease);
                    addonsLease.DemandChildDirectorySafe(entry.Target, allowMissing: true);
                    Directory.Move(entry.Backup, entry.Target);
                    addonsLease.DemandChildDirectorySafe(entry.Target, allowMissing: false);
                }

                TryDeleteDirectory(entry.Prepared, rootLease);
            }

            throw;
        }
    }

    private static void RemoveManagedFolders(
        string addonsDirectory,
        IEnumerable<string> folders,
        CancellationToken cancellationToken,
        IGameInstallRootLease rootLease,
        IGameInstallDirectoryLease addonsLease)
    {
        var operationId = Guid.NewGuid().ToString("N");
        var movedFolders = new List<(string Target, string Quarantine)>();

        try
        {
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFolderName(folder);
                var target = Path.Combine(addonsDirectory, folder);
                AddonLocalInventory.EnsureNotLinked(target);
                if (!Directory.Exists(target))
                {
                    continue;
                }

                foreach (string _ in EnumerateOwnedFiles(
                             addonsDirectory,
                             [folder],
                             cancellationToken,
                             rootLease))
                {
                }

                string quarantine = Path.Combine(
                    addonsDirectory,
                    $".atlas-remove-{operationId}-{folder}");
                addonsLease.Revalidate();
                addonsLease.DemandChildDirectorySafe(target, allowMissing: false);
                addonsLease.DemandChildDirectorySafe(quarantine, allowMissing: true);
                Directory.Move(target, quarantine);
                addonsLease.DemandChildDirectorySafe(quarantine, allowMissing: false);
                movedFolders.Add((target, quarantine));
            }

            foreach (var entry in movedFolders)
            {
                TryDeleteDirectory(entry.Quarantine, rootLease);
            }
        }
        catch
        {
            foreach (var entry in movedFolders.AsEnumerable().Reverse())
            {
                if (Directory.Exists(entry.Quarantine) && !Directory.Exists(entry.Target))
                {
                    addonsLease.DemandChildDirectorySafe(
                        entry.Quarantine,
                        allowMissing: false);
                    addonsLease.DemandChildDirectorySafe(entry.Target, allowMissing: true);
                    Directory.Move(entry.Quarantine, entry.Target);
                    addonsLease.DemandChildDirectorySafe(entry.Target, allowMissing: false);
                }
            }

            throw;
        }
    }

    private static AddonInstallState LoadState(
        string addonsDirectory,
        IGameInstallRootLease? rootLease = null)
    {
        string statePath = Path.Combine(addonsDirectory, StateFileName);
        AddonLocalInventory.EnsureNotLinked(statePath);

        IGameInstallReadLease? readLease = null;
        FileStream? standalone = null;
        if (rootLease is not null)
        {
            if (!Directory.Exists(addonsDirectory))
            {
                return new AddonInstallState();
            }

            using IGameInstallDirectoryLease directoryLease = rootLease.AcquireDirectory(
                addonsDirectory,
                createIfMissing: false);
            directoryLease.DemandChildFileSafe(statePath, allowMissing: true);
            if (!File.Exists(statePath))
            {
                return new AddonInstallState();
            }

            readLease = rootLease.OpenFileForRead(statePath);
        }
        else
        {
            try
            {
                standalone = new FileStream(
                    Path.GetFullPath(statePath),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
            }
            catch (Exception error) when (error is IOException
                                          or UnauthorizedAccessException)
            {
                return new AddonInstallState();
            }
        }

        try
        {
            Stream source = readLease?.Stream ?? standalone!;
            return DeserializeValidatedState(source);
        }
        catch (Exception error) when (error is JsonException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            return new AddonInstallState();
        }
        finally
        {
            readLease?.Dispose();
            standalone?.Dispose();
        }
    }

    private static AddonInstallState DeserializeValidatedState(Stream source)
    {
        if (!source.CanRead ||
            source.Length is <= 0 or > MaximumAddonStateBytes)
        {
            throw new InvalidDataException(
                "La taille de l'état local des addons est invalide.");
        }

        if (source.CanSeek)
        {
            source.Position = 0;
        }

        using JsonDocument document = JsonDocument.Parse(source, new JsonDocumentOptions
        {
            MaxDepth = MaximumAddonStateJsonDepth
        });
        if (!HasSupportedStateContainerCounts(document.RootElement))
        {
            throw new InvalidDataException(
                "La structure de l'état local des addons est invalide.");
        }

        BoundedJsonHttpContent.RejectDuplicateProperties(
            document.RootElement,
            "L'état local des addons");
        AddonInstallState? state = document.RootElement.Deserialize<AddonInstallState>(
            JsonOptions);
        if (state is null || !IsValidLoadedState(state))
        {
            throw new InvalidDataException(
                "Le contenu de l'état local des addons est invalide.");
        }

        return state;
    }

    private static bool HasSupportedStateContainerCounts(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement schemaVersion = default;
        bool foundSchemaVersion = false;
        JsonElement addons = default;
        bool foundAddons = false;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase))
            {
                schemaVersion = property.Value;
                foundSchemaVersion = true;
                continue;
            }

            if (!string.Equals(property.Name, "addons", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            addons = property.Value;
            foundAddons = true;
        }

        if (!foundSchemaVersion ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out int schema) ||
            schema != 1 ||
            !foundAddons ||
            addons.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        int addonCount = 0;
        int cumulativeFiles = 0;
        foreach (JsonProperty addonProperty in addons.EnumerateObject())
        {
            if (++addonCount > MaximumCatalogAddons || addonProperty.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty stateProperty in addonProperty.Value.EnumerateObject())
            {
                if (!string.Equals(stateProperty.Name, "files", StringComparison.OrdinalIgnoreCase) ||
                    stateProperty.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (stateProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                foreach (JsonProperty _ in stateProperty.Value.EnumerateObject())
                {
                    if (++cumulativeFiles > MaximumArchiveEntries)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool IsValidLoadedState(AddonInstallState state)
    {
        if (state.SchemaVersion != 1 ||
            state.Addons is null ||
            state.Addons.Count > MaximumCatalogAddons)
        {
            return false;
        }

        int cumulativeFiles = 0;
        HashSet<string> ownedFolders = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string addonId, InstalledAddonState addon) in state.Addons)
        {
            if (!AddonIdRegex().IsMatch(addonId) ||
                addon is null ||
                addon.Version is not { Length: > 0 and <= 32 } ||
                string.IsNullOrWhiteSpace(addon.Version) ||
                addon.Version.Any(char.IsControl) ||
                !Sha256Regex().IsMatch(addon.Sha256 ?? "") ||
                addon.Folders is null ||
                addon.Folders.Count is 0 or > 20)
            {
                return false;
            }

            HashSet<string> addonFolders = new(StringComparer.OrdinalIgnoreCase);
            foreach (string folder in addon.Folders)
            {
                if (folder is not { Length: > 0 and <= MaximumAddonStateFolderCharacters } ||
                    !IsValidFolderName(folder) ||
                    !addonFolders.Add(folder) ||
                    !ownedFolders.Add(folder))
                {
                    return false;
                }
            }

            if (addon.Files is null)
            {
                continue;
            }

            if (addon.Files.Count > MaximumArchiveEntries - cumulativeFiles ||
                !IsValidFileManifest(addon))
            {
                return false;
            }

            cumulativeFiles += addon.Files.Count;
            foreach ((string relativePath, InstalledAddonFile file) in addon.Files)
            {
                if (relativePath is not { Length: > 0 and <= MaximumAddonStatePathCharacters } ||
                    relativePath.Contains('\\') ||
                    file is null ||
                    file.Size is < 0 or > MaximumExpandedArchiveSize ||
                    !Sha256Regex().IsMatch(file.Sha256 ?? ""))
                {
                    return false;
                }

                string[] segments = relativePath.Split('/');
                if (segments.Length is < 2 or > MaximumAddonStatePathSegments ||
                    segments.Any(segment =>
                        segment is not { Length: > 0 and <= MaximumAddonStateFolderCharacters } ||
                        !IsValidFolderName(segment)) ||
                    !addonFolders.Contains(segments[0]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    internal static void CommitValidatedStateTransition(
        AddonInstallState candidate,
        Action applyFolderMutations,
        Action rollbackFolderMutations,
        Action<byte[]> persistPreparedState)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(applyFolderMutations);
        ArgumentNullException.ThrowIfNull(rollbackFolderMutations);
        ArgumentNullException.ThrowIfNull(persistPreparedState);

        // Candidate validation, bounded serialization and an exact reader
        // round-trip all finish before the first managed folder is changed.
        byte[] payload = SerializeValidatedState(candidate);
        try
        {
            applyFolderMutations();
            persistPreparedState(payload);
        }
        catch (Exception transitionError)
        {
            try
            {
                rollbackFolderMutations();
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "La transition d'addons et sa restauration ont échoué.",
                    transitionError,
                    rollbackError);
            }

            throw;
        }
    }

    private static byte[] SerializeValidatedState(AddonInstallState candidate)
    {
        if (!IsValidLoadedState(candidate))
        {
            throw new InvalidDataException(
                "L'état final des addons dépasse les limites prises en charge.");
        }

        using MaximumLengthMemoryStream buffer = new(MaximumAddonStateBytes);
        JsonSerializer.Serialize(buffer, candidate, JsonOptions);
        byte[] payload = buffer.ToArray();
        using MemoryStream reader = new(payload, writable: false);
        _ = DeserializeValidatedState(reader);
        return payload;
    }

    private static void SavePreparedState(
        string addonsDirectory,
        byte[] payload,
        IGameInstallRootLease rootLease,
        IGameInstallDirectoryLease addonsLease)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is <= 0 or > MaximumAddonStateBytes)
        {
            throw new InvalidDataException(
                "L'état local des addons dépasse la taille maximale autorisée.");
        }

        addonsLease.Revalidate();
        string statePath = Path.Combine(addonsDirectory, StateFileName);
        addonsLease.DemandChildFileSafe(statePath, allowMissing: true);
        rootLease.WriteFileAtomically(
            statePath,
            destination => destination.Write(payload));
    }

    private sealed class MaximumLengthMemoryStream(int maximumLength)
        : MemoryStream(capacity: 4096)
    {
        private void DemandCapacity(int count)
        {
            if (count < 0 ||
                Math.Max(Length, Position + count) > maximumLength)
            {
                throw new InvalidDataException(
                    "L'état local des addons dépasse la taille maximale autorisée.");
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            DemandCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            DemandCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            DemandCapacity(1);
            base.WriteByte(value);
        }
    }

    private static void ValidateCatalog(AddonCatalog catalog)
    {
        CanonicalizeLegacyAtlasArchiveUrls(catalog);
        if (catalog.SchemaVersion != 1)
        {
            throw new InvalidOperationException("Version de catalogue d'addons non prise en charge.");
        }

        if (!string.Equals(catalog.ClientInterface, SupportedInterface, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Le catalogue ne cible pas l'interface WotLK Classic {SupportedInterface}.");
        }

        if (catalog.Addons is null || catalog.Addons.Count is 0 or > MaximumCatalogAddons)
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
                package.Version.Any(char.IsControl) ||
                package.Description is null || package.Description.Length > 300 ||
                package.Category is null || package.Category.Length is 0 or > 50 ||
                package.Author is null || package.Author.Length > 100 ||
                package.SourceUrl is null || package.SourceUrl.Length > 2048 ||
                package.SourceUrl.Length > 0 && !IsValidReferenceUrl(package.SourceUrl) ||
                !string.Equals(package.Interface, SupportedInterface, StringComparison.Ordinal) ||
                !IsValidArchiveDescriptor(package.Url, package.Size, package.Sha256) ||
                !Sha256Regex().IsMatch(package.EffectiveInstallHash) ||
                package.Folders is null || package.Folders.Count is 0 or > 20 ||
                package.Components is null || package.Components.Count > 10 ||
                package.Dependencies is null || package.Dependencies.Count > 20 ||
                package.Dependencies.Any(dependency => dependency is null || !AddonIdRegex().IsMatch(dependency)) ||
                package.KnownLimitations is null || package.KnownLimitations.Length > 2000 ||
                package.AtlasValidation is not null && package.ValidatedAtlasEvidenceUrl.Length == 0 ||
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

    private static void CanonicalizeLegacyAtlasArchiveUrls(AddonCatalog catalog)
    {
        if (catalog.Addons is null)
        {
            return;
        }

        foreach (AddonPackage? package in catalog.Addons)
        {
            if (package is null)
            {
                continue;
            }

            if (AtlasLegacyUrlCanonicalizer.TryCanonicalize(package.Url, out string packageUrl))
            {
                package.Url = packageUrl;
            }

            if (package.Components is null)
            {
                continue;
            }

            foreach (AddonPackageComponent? component in package.Components)
            {
                if (component is not null
                    && AtlasLegacyUrlCanonicalizer.TryCanonicalize(component.Url, out string componentUrl))
                {
                    component.Url = componentUrl;
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
               url is { Length: > 0 and <= MaximumUrlLength } &&
               Uri.TryCreate(url, UriKind.Absolute, out var archiveUri) &&
               IsAllowedAddonArchiveUri(archiveUri);
    }

    private static bool IsValidReferenceUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https"
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0
            && !string.IsNullOrWhiteSpace(uri.Host);
    }

    private static Uri RequireArchiveUri(string value, string label)
    {
        if (value is not { Length: > 0 and <= MaximumUrlLength }
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidDataException(label + " invalide.");
        }

        return RequireArchiveUri(uri, label);
    }

    private static Uri RequireArchiveUri(Uri uri, string label)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsAllowedAddonArchiveUri(uri))
        {
            throw new InvalidDataException(label + " invalide: origine de téléchargement non autorisée.");
        }

        return uri;
    }

    private static Uri RequireCatalogUri(Uri uri, string label)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!IsAllowedAddonCatalogUri(uri))
        {
            throw new InvalidDataException(label + " invalide: seule l'origine Atlas autorisée est acceptée.");
        }

        return uri;
    }

    internal static bool IsAllowedAddonCatalogUri(Uri uri)
        => IsStrictHttpsUri(uri)
            && string.Equals(uri.Host, "animeclub.fr", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAllowedAddonArchiveUri(Uri uri)
    {
        if (!IsStrictHttpsUri(uri))
        {
            return false;
        }

        return uri.Host.ToLowerInvariant() is
            "animeclub.fr" or
            "github.com" or
            "codeload.github.com" or
            "release-assets.githubusercontent.com" or
            "edge.forgecdn.net" or
            "mediafilez.forgecdn.net";
    }

    private static bool IsStrictHttpsUri(Uri uri)
    {
        return uri.IsAbsoluteUri
            && uri.AbsoluteUri.Length <= MaximumUrlLength
            && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443
            && !string.IsNullOrWhiteSpace(uri.Host)
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0;
    }

    private static async Task<HttpResponseMessage> GetWithValidatedRedirectsAsync(
        HttpClient http,
        Uri initialUri,
        Func<Uri, string, Uri> requireAllowedUri,
        string resourceLabel,
        CancellationToken cancellationToken)
    {
        Uri currentUri = requireAllowedUri(initialUri, "URL de " + resourceLabel);
        HashSet<string> visited = new(StringComparer.Ordinal) { currentUri.AbsoluteUri };
        int redirects = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            HttpResponseMessage response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            try
            {
                Uri responseUri = requireAllowedUri(
                    response.RequestMessage?.RequestUri ?? currentUri,
                    "URL finale de " + resourceLabel);
                if (!responseUri.Equals(currentUri))
                {
                    throw new InvalidDataException(
                        $"La redirection automatique de {resourceLabel} est refusée.");
                }

                if (!IsRedirectStatusCode(response.StatusCode))
                {
                    return response;
                }

                if (redirects >= MaximumRemoteRedirects)
                {
                    throw new InvalidDataException(
                        $"Le nombre maximal de redirections de {resourceLabel} est dépassé.");
                }

                Uri? location = response.Headers.Location;
                if (location is null
                    || !Uri.TryCreate(currentUri, location, out Uri? nextUri))
                {
                    throw new InvalidDataException(
                        $"La redirection de {resourceLabel} ne contient pas de destination valide.");
                }

                nextUri = requireAllowedUri(nextUri, "URL de redirection de " + resourceLabel);
                if (!visited.Add(nextUri.AbsoluteUri))
                {
                    throw new InvalidDataException(
                        $"Une boucle de redirection de {resourceLabel} a été détectée.");
                }

                redirects++;
                currentUri = nextUri;
            }
            catch
            {
                response.Dispose();
                throw;
            }

            response.Dispose();
        }
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

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
               folder.Length <= MaximumAddonStateFolderCharacters &&
               folder is not ("." or "..") &&
               folder.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
               string.Equals(folder, Path.GetFileName(folder), StringComparison.Ordinal);
    }

    private static void CopyDirectoryToInstallRoot(
        string source,
        string destination,
        CancellationToken cancellationToken,
        IGameInstallRootLease rootLease)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AddonLocalInventory.EnsureNotLinked(source);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "La préparation d'un addon refuse un dossier lié: " + source);
        }

        using IGameInstallDirectoryLease sourceLease = rootLease.AcquireDirectory(
            source,
            createIfMissing: false);
        using IGameInstallDirectoryLease destinationLease = rootLease.AcquireDirectory(
            destination,
            createIfMissing: true);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddonLocalInventory.EnsureNotLinked(file);
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "La préparation d'un addon refuse un fichier lié: " + file);
            }

            string target = Path.Combine(destination, Path.GetFileName(file));
            sourceLease.DemandChildFileSafe(file, allowMissing: false);
            destinationLease.DemandChildFileSafe(target, allowMissing: true);
            using IGameInstallReadLease sourceReadLease = rootLease.OpenFileForRead(file);
            rootLease.WriteFileAtomically(
                target,
                targetStream => sourceReadLease.Stream.CopyTo(targetStream));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddonLocalInventory.EnsureNotLinked(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    "La préparation d'un addon refuse un dossier lié: " + directory);
            }

            string target = Path.Combine(destination, Path.GetFileName(directory));
            sourceLease.DemandChildDirectorySafe(directory, allowMissing: false);
            destinationLease.DemandChildDirectorySafe(target, allowMissing: true);
            CopyDirectoryToInstallRoot(
                directory,
                target,
                cancellationToken,
                rootLease);
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

    private static void TryDeleteDirectory(
        string path,
        IGameInstallRootLease rootLease)
    {
        try
        {
            if (Directory.Exists(path))
            {
                DeleteDirectoryTreeNoFollow(path, rootLease);
            }
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryTreeNoFollow(
        string directory,
        IGameInstallRootLease rootLease)
    {
        string parent = Path.GetDirectoryName(directory)
            ?? throw new InvalidDataException(
                "Le dossier d'addon à supprimer n'a pas de parent.");
        using (IGameInstallDirectoryLease directoryLease = rootLease.AcquireDirectory(
                   directory,
                   createIfMissing: false))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Le nettoyage d'un addon refuse un lien de système de fichiers: "
                        + entry);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directoryLease.DemandChildDirectorySafe(entry, allowMissing: false);
                    DeleteDirectoryTreeNoFollow(entry, rootLease);
                    continue;
                }

                directoryLease.DemandChildFileSafe(entry, allowMissing: false);
                directoryLease.DeleteChildFile(entry);
            }
        }

        using IGameInstallDirectoryLease parentLease = rootLease.AcquireDirectory(
            parent,
            createIfMissing: false);
        _ = parentLease.TryDeleteChildDirectoryIfEmpty(directory);
    }

    private static void DemandPlayableClientUnderLease(
        string installRoot,
        IGameInstallRootLease rootLease)
    {
        using IGameInstallReadLease gameExecutable = rootLease.OpenFileForRead(
            GameInstallServices.GetGameExecutablePath(installRoot));
        using IGameInstallReadLease gameLauncher = rootLease.OpenFileForRead(
            GameInstallServices.GetGameLauncherPath(installRoot));
        _ = gameExecutable.Stream.Length;
        _ = gameLauncher.Stream.Length;
    }

    private static bool SafeAddonFolderExists(
        string addonsDirectory,
        string folder,
        IGameInstallDirectoryLease addonsLease)
    {
        ValidateFolderName(folder);
        string path = Path.Combine(addonsDirectory, folder);
        addonsLease.DemandChildDirectorySafe(path, allowMissing: true);
        return Directory.Exists(path);
    }
}
