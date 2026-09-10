using System.IO;

namespace WotLK.Launcher.Game;

internal interface IGameFileCleanupService
{
    IReadOnlyList<string> FindRemovedFiles(
        string installRoot,
        LauncherManifest manifest,
        IGameInstallRootLease? rootLease = null);

    int DeleteRemovedFiles(
        string installRoot,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken,
        IGameInstallRootLease? rootLease = null);
}
internal sealed record GameFileCleanupRetryPolicy(
    int DeleteAttempts,
    TimeSpan DeleteDelay)
{
    internal static GameFileCleanupRetryPolicy Legacy { get; } = new(
        DeleteAttempts: 12,
        DeleteDelay: TimeSpan.FromMilliseconds(250));
}

internal sealed class GameFileCleanupService : IGameFileCleanupService
{
    private readonly IGameFileVerifier _fileVerifier;
    private readonly GameFileCleanupRetryPolicy _retryPolicy;
    private readonly Action<TimeSpan> _delay;

    internal GameFileCleanupService(
        IGameFileVerifier fileVerifier,
        GameFileCleanupRetryPolicy? retryPolicy = null,
        Action<TimeSpan>? delay = null)
    {
        _fileVerifier = fileVerifier ?? throw new ArgumentNullException(nameof(fileVerifier));
        _retryPolicy = retryPolicy ?? GameFileCleanupRetryPolicy.Legacy;
        if (_retryPolicy.DeleteAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryPolicy));
        }

        _delay = delay ?? Thread.Sleep;
    }

    public IReadOnlyList<string> FindRemovedFiles(
        string installRoot,
        LauncherManifest manifest,
        IGameInstallRootLease? rootLease = null)
    {
        return _fileVerifier.FindRemovedFiles(installRoot, manifest, rootLease);
    }

    public int DeleteRemovedFiles(
        string installRoot,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken,
        IGameInstallRootLease? rootLease = null)
    {
        int deletedCount = 0;
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (rootLease is not null)
        {
            root = GameInstallServices.DemandLeaseMatchesGameRoot(
                root,
                rootLease);
        }

        foreach (string relativePath in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = GamePathPolicy.GetSafeTargetPath(installRoot, relativePath);
            string parent = Path.GetDirectoryName(target)
                ?? throw new InvalidDataException("Le fichier obsolète n’a pas de dossier parent.");
            IGameInstallDirectoryLease? parentLease = null;
            if (rootLease is not null)
            {
                try
                {
                    parentLease = rootLease.AcquireDirectory(
                        parent,
                        createIfMissing: false);
                }
                catch (DirectoryNotFoundException)
                {
                    // A cached manifest can legitimately mention a retired
                    // file whose whole directory is already gone. Treat it as
                    // absent while still propagating reparse/hard-link errors.
                    continue;
                }
            }

            using (parentLease)
            {
                if (parentLease is not null)
                {
                    parentLease.DemandChildFileSafe(target, allowMissing: true);
                }

                if (!File.Exists(target))
                {
                    continue;
                }

                if (DeleteFileWithRetry(target, cancellationToken, parentLease))
                {
                    deletedCount++;
                }
            }

            string? currentDirectory = Path.GetDirectoryName(target);
            while (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                string normalizedDirectory = Path.GetFullPath(currentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(
                        normalizedDirectory,
                        root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                directories.Add(normalizedDirectory);
                currentDirectory = Path.GetDirectoryName(normalizedDirectory);
            }
        }

        foreach (string directory in directories.OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteDirectoryIfEmpty(directory, rootLease);
        }

        return deletedCount;
    }

    private bool DeleteFileWithRetry(
        string path,
        CancellationToken cancellationToken,
        IGameInstallDirectoryLease? parentLease)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < _retryPolicy.DeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (parentLease is not null)
                {
                    parentLease.Revalidate();
                    parentLease.DemandChildFileSafe(path, allowMissing: true);
                    if (!File.Exists(path))
                    {
                        return false;
                    }

                    parentLease.DeleteChildFile(path);
                }
                else
                {
                    if (!File.Exists(path))
                    {
                        return false;
                    }

                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                _delay(_retryPolicy.DeleteDelay);
            }
        }

        throw new IOException(
            "Impossible de supprimer le fichier obsolete: " + path,
            lastError);
    }

    private static void TryDeleteDirectoryIfEmpty(
        string directory,
        IGameInstallRootLease? rootLease)
    {
        try
        {
            if (rootLease is not null)
            {
                string parent = Path.GetDirectoryName(directory)
                    ?? throw new InvalidDataException(
                        "Le dossier WotLK obsolète n’a pas de parent.");
                using IGameInstallDirectoryLease parentLease = rootLease.AcquireDirectory(
                    parent,
                    createIfMissing: false);
                _ = parentLease.TryDeleteChildDirectoryIfEmpty(directory);
                return;
            }

            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
