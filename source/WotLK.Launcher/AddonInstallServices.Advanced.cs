using System.IO;
using System.Security.Cryptography;

namespace WotLK.Launcher;

internal static partial class AddonInstallServices
{
    internal static void ValidatePlan(AddonCatalog catalog, string installRoot, IEnumerable<string> installIds,
        IEnumerable<string> removalIds, bool allowExternalReplacement, CancellationToken cancellationToken = default)
    {
        if (!GameInstallServices.HasPlayableClient(installRoot))
            throw new InvalidOperationException("Installe d'abord le client WotLK avant de gérer ses addons.");
        string addonsDirectory = GetAddonsDirectory(installRoot);
        AddonLocalInventory.EnsureNotLinked(addonsDirectory);
        AddonInstallState state = LoadState(addonsDirectory);
        IReadOnlyList<AddonPackage> order = AddonDependencyPlanner.Plan(catalog, installIds);
        HashSet<string> removals = removalIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (order.Any(package => removals.Contains(package.Id))) throw new AddonPlanException("dependency-in-use", removals);
        PreflightSelection(catalog, addonsDirectory, state, order, removals, allowExternalReplacement, cancellationToken);
    }

    private static void PreflightSelection(AddonCatalog catalog, string addonsDirectory, AddonInstallState state,
        IReadOnlyList<AddonPackage> installOrder, HashSet<string> removeIds, bool allowExternalReplacement, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (AddonPackage package in installOrder)
            foreach (string folder in package.Folders) ValidateFolderName(folder);
        AddonPackage[] external = installOrder.Where(package => !state.Addons.ContainsKey(package.Id)
            && package.Folders.Any(folder => Directory.Exists(Path.Combine(addonsDirectory, folder)) || File.Exists(Path.Combine(addonsDirectory, folder)))).ToArray();
        if (!allowExternalReplacement && external.Length != 0)
            throw new AddonPlanException("external-replacement-required", external.Select(package => package.Id));

        HashSet<string> actualRemovals = removeIds.Where(state.Addons.ContainsKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> installedIds = state.Addons.Keys.Concat(installOrder.Select(package => package.Id))
            .Concat(catalog.Addons.Where(package => package.Folders.Any(folder => Directory.Exists(Path.Combine(addonsDirectory, folder)))).Select(package => package.Id));
        AddonPackage[] blocked = AddonDependencyPlanner.FindBlockingDependents(catalog, installedIds, actualRemovals).ToArray();
        if (blocked.Length != 0) throw new AddonPlanException("dependency-in-use", blocked.Select(package => package.Id));

        if (actualRemovals.Count != 0)
        {
            HashSet<string> removedFolders = actualRemovals.SelectMany(id => state.Addons[id].Folders).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<ManualAddonInstallation> inventory = AddonLocalInventory.Read(catalog, addonsDirectory, includeCatalogFolders: true);
            ManualAddonInstallation[] unreadable = inventory.Where(addon => !removedFolders.Contains(addon.Folder) && addon.InspectionError.Length != 0).ToArray();
            if (unreadable.Length != 0)
                throw new AddonPlanException("dependency-inspection-failed", unreadable.Select(addon => addon.Id));
            Dictionary<string, ManualAddonInstallation> byFolder = inventory.ToDictionary(addon => addon.Folder, StringComparer.OrdinalIgnoreCase);
            bool UsesRemoved(string folder, HashSet<string> seen)
            {
                if (removedFolders.Contains(folder)) return true;
                if (!seen.Add(folder) || !byFolder.TryGetValue(folder, out ManualAddonInstallation? addon)) return false;
                return addon.Dependencies.Any(required => UsesRemoved(required, seen));
            }
            string[] manualBlockers = inventory.Where(addon => !removedFolders.Contains(addon.Folder)
                    && addon.Dependencies.Any(required => UsesRemoved(required, new(StringComparer.OrdinalIgnoreCase))))
                .Select(addon => catalog.Addons.FirstOrDefault(package => package.Folders.Contains(addon.Folder, StringComparer.OrdinalIgnoreCase))?.Id ?? addon.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (manualBlockers.Length != 0) throw new AddonPlanException("dependency-in-use", manualBlockers);
        }

        string[] targetFolders = actualRemovals.SelectMany(id => state.Addons[id].Folders)
            .Concat(installOrder.SelectMany(package => package.Folders)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string folder in targetFolders)
        {
            ValidateFolderName(folder);
            string target = Path.Combine(addonsDirectory, folder);
            AddonLocalInventory.EnsureNotLinked(target);
            if (File.Exists(target)) throw new IOException("An addon folder cannot replace an existing file.");
        }
        // Check descendants too: moving a directory must not carry an unchecked
        // junction into a backup or recursive cleanup operation.
        foreach (string _ in EnumerateOwnedFiles(addonsDirectory, targetFolders, cancellationToken)) { }
    }

    internal static IReadOnlyList<ManualAddonInstallation> InspectManualAddons(AddonCatalog catalog, string installRoot)
        => AddonLocalInventory.Read(catalog, GetAddonsDirectory(installRoot));

    internal static Task<AddonVerificationResult> VerifyAsync(AddonCatalog catalog, string installRoot,
        string addonId, CancellationToken cancellationToken)
        => Task.Run(() => VerifyCoreAsync(catalog, installRoot, addonId, cancellationToken), cancellationToken);

    private static async Task<AddonVerificationResult> VerifyCoreAsync(AddonCatalog catalog, string installRoot,
        string addonId, CancellationToken cancellationToken)
    {
        AddonPackage package = catalog.Addons.FirstOrDefault(package => string.Equals(package.Id, addonId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AddonPlanException("addon-not-found", [addonId]);
        string addonsDirectory = GetAddonsDirectory(installRoot);
        AddonLocalInventory.EnsureNotLinked(addonsDirectory);
        AddonInstallState state = LoadState(addonsDirectory);
        if (!state.Addons.TryGetValue(package.Id, out InstalledAddonState? installed))
            return new(package.Id, package.Folders.Any(folder => Directory.Exists(Path.Combine(addonsDirectory, folder)))
                ? AddonVerificationStatus.Unmanaged : AddonVerificationStatus.NotInstalled, 0, [], [], [], DateTimeOffset.UtcNow);
        if (!IsValidFileManifest(installed))
            return new(package.Id, AddonVerificationStatus.LegacyUnverified, 0, [], [], [], DateTimeOffset.UtcNow);
        List<string> missing = [], modified = [];
        int checkedFiles = 0;
        Dictionary<string, InstalledAddonFile> expected = new(installed.Files!, StringComparer.OrdinalIgnoreCase);
        foreach ((string relative, InstalledAddonFile file) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(addonsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            AddonLocalInventory.EnsureNotLinked(path);
            if (!File.Exists(path)) { missing.Add(relative); continue; }
            checkedFiles++;
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
            if (input.Length != file.Size || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                modified.Add(relative);
        }
        string[] extra = EnumerateOwnedFiles(addonsDirectory, installed.Folders, cancellationToken)
            .Select(path => Path.GetRelativePath(addonsDirectory, path).Replace('\\', '/'))
            .Where(relative => !expected.ContainsKey(relative)).ToArray();
        return new(package.Id, missing.Count + modified.Count + extra.Length == 0 ? AddonVerificationStatus.Verified : AddonVerificationStatus.NeedsRepair,
            checkedFiles, missing, modified, extra, DateTimeOffset.UtcNow);
    }

    private static bool IsValidFileManifest(InstalledAddonState state)
    {
        if (state.Files is null || state.Files.Count is 0 or > MaximumArchiveEntries) return false;
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long totalSize = 0;
        foreach ((string relative, InstalledAddonFile file) in state.Files)
        {
            if (file is null || file.Size < 0 || file.Size > MaximumExpandedArchiveSize || !Sha256Regex().IsMatch(file.Sha256 ?? "")) return false;
            totalSize += file.Size;
            if (totalSize > MaximumExpandedArchiveSize) return false;
            string[] pieces = relative.Split('/');
            if (pieces.Length < 2 || pieces.Any(piece => !IsValidFolderName(piece)) || relative.Contains('\\')
                || !state.Folders.Contains(pieces[0], StringComparer.OrdinalIgnoreCase) || !paths.Add(relative)) return false;
        }
        return state.Folders.All(folder => paths.Any(path => path.StartsWith(folder + '/', StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<Dictionary<string, InstalledAddonFile>> CreateFileManifestAsync(
        string root, IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        Dictionary<string, InstalledAddonFile> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateOwnedFiles(root, folders, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true);
            result.Add(Path.GetRelativePath(root, path).Replace('\\', '/'), new()
            {
                Size = input.Length,
                Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken)).ToLowerInvariant()
            });
        }
        return result;
    }

    private static IEnumerable<string> EnumerateOwnedFiles(string root, IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        int files = 0;
        Stack<string> pending = new(folders.Select(folder => Path.Combine(root, folder)));
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddonLocalInventory.EnsureNotLinked(directory);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddonLocalInventory.EnsureNotLinked(path);
                if (++files > MaximumArchiveEntries) throw new IOException("Addon inventory exceeds the supported file count.");
                yield return path;
            }
            foreach (string child in Directory.EnumerateDirectories(directory)) pending.Push(child);
        }
    }
}
