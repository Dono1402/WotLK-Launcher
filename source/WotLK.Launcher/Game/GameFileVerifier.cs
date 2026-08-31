using System.IO;
using System.Security.Cryptography;

namespace WotLK.Launcher.Game;

internal interface IGameFileVerifier
{
    Task<GameFileComparisonResult> FindMissingOrChangedFilesAsync(
        string installRoot,
        LauncherManifest manifest,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken);

    IReadOnlyList<string> FindRemovedFiles(
        string installRoot,
        LauncherManifest manifest);
}

internal sealed class GameFileVerifier : IGameFileVerifier
{
    private static readonly string[] RetiredAddonDirectories =
    [
        "Interface/AddOns/UnBot",
        "Interface/AddOns/MultiBot"
    ];

    private readonly IInstalledManifestStore _manifestStore;
    private readonly GameClientStateReader _clientStateReader;
    private readonly Func<string, bool> _hasPlayableClient;

    internal GameFileVerifier(
        IInstalledManifestStore manifestStore,
        GameClientStateReader clientStateReader,
        Func<string, bool>? hasPlayableClient = null)
    {
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _clientStateReader = clientStateReader ?? throw new ArgumentNullException(nameof(clientStateReader));
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
    }

    public async Task<GameFileComparisonResult> FindMissingOrChangedFilesAsync(
        string installRoot,
        LauncherManifest manifest,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        LauncherManifest? cachedManifest = _manifestStore.Load(installRoot);
        if (cachedManifest is not null && cachedManifest.Files.Count > 0)
        {
            return new GameFileComparisonResult(
                CompareManifestFiles(manifest, cachedManifest),
                GameFileComparisonSource.ManifestHistory,
                0,
                manifest.Files.Count);
        }

        string? installedVersion = _clientStateReader.ReadInstalledVersion(installRoot);
        if (!string.IsNullOrWhiteSpace(manifest.Version)
            && string.Equals(installedVersion, manifest.Version, StringComparison.OrdinalIgnoreCase)
            && _hasPlayableClient(installRoot))
        {
            _manifestStore.Save(installRoot, manifest);
            return new GameFileComparisonResult(
                [],
                GameFileComparisonSource.InstalledVersion,
                0,
                manifest.Files.Count);
        }

        List<LauncherFile> missingOrChanged = [];
        int checkedCount = 0;
        foreach (LauncherFile file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedCount++;
            reportProgress?.Invoke(new GameVerificationProgress(
                GameVerificationPhase.ScanningFiles,
                checkedCount,
                manifest.Files.Count));

            string target = GamePathPolicy.GetSafeTargetPath(installRoot, file.Path);
            if (!File.Exists(target) || new FileInfo(target).Length != file.Size)
            {
                missingOrChanged.Add(file);
                continue;
            }

            try
            {
                string localHash = await ComputeSha256Async(target, cancellationToken);
                if (!string.Equals(localHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    missingOrChanged.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missingOrChanged.Add(file);
            }
        }

        return new GameFileComparisonResult(
            missingOrChanged,
            GameFileComparisonSource.FileSystem,
            checkedCount,
            manifest.Files.Count);
    }

    public IReadOnlyList<string> FindRemovedFiles(
        string installRoot,
        LauncherManifest manifest)
    {
        HashSet<string> remotePaths = manifest.Files
            .Select(file => GamePathPolicy.NormalizeManifestPath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> removedPaths = new(StringComparer.OrdinalIgnoreCase);

        LauncherManifest? cachedManifest = _manifestStore.Load(installRoot);
        if (cachedManifest is not null && cachedManifest.Files.Count > 0)
        {
            foreach (LauncherFile cachedFile in cachedManifest.Files)
            {
                string key = GamePathPolicy.NormalizeManifestPath(cachedFile.Path);
                if (!remotePaths.Contains(key))
                {
                    removedPaths.Add(cachedFile.Path);
                }
            }
        }

        foreach (string retiredDirectory in RetiredAddonDirectories)
        {
            AddRetiredDirectoryFilesIfAbsent(
                installRoot,
                remotePaths,
                removedPaths,
                retiredDirectory);
        }

        return removedPaths.ToList();
    }

    internal static List<LauncherFile> CompareManifestFiles(
        LauncherManifest remoteManifest,
        LauncherManifest installedManifest)
    {
        Dictionary<string, LauncherFile> installedFiles = installedManifest.Files
            .GroupBy(
                file => GamePathPolicy.NormalizeManifestPath(file.Path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        List<LauncherFile> missingOrChanged = [];
        foreach (LauncherFile remoteFile in remoteManifest.Files)
        {
            string key = GamePathPolicy.NormalizeManifestPath(remoteFile.Path);
            if (!installedFiles.TryGetValue(key, out LauncherFile? installedFile)
                || installedFile.Size != remoteFile.Size
                || !string.Equals(
                    installedFile.Sha256,
                    remoteFile.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                missingOrChanged.Add(remoteFile);
            }
        }

        return missingOrChanged;
    }

    internal static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 256,
            useAsync: true);
        using SHA256 sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void AddRetiredDirectoryFilesIfAbsent(
        string installRoot,
        HashSet<string> remotePaths,
        HashSet<string> removedPaths,
        string relativeDirectory)
    {
        string normalizedPrefix = GamePathPolicy
            .NormalizeManifestPath(relativeDirectory)
            .TrimEnd('/') + "/";
        if (remotePaths.Any(path => path.StartsWith(
                normalizedPrefix,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        string directory = GamePathPolicy.GetSafeTargetPath(installRoot, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(
                     directory,
                     "*",
                     SearchOption.AllDirectories))
        {
            removedPaths.Add(Path.GetRelativePath(installRoot, file).Replace('\\', '/'));
        }
    }
}
