using System.IO;

namespace WotLK.Launcher.Game;

internal interface IGameFileCleanupService
{
    IReadOnlyList<string> FindRemovedFiles(
        string installRoot,
        LauncherManifest manifest);

    int DeleteRemovedFiles(
        string installRoot,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken);
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
        LauncherManifest manifest)
    {
        return _fileVerifier.FindRemovedFiles(installRoot, manifest);
    }

    public int DeleteRemovedFiles(
        string installRoot,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        int deletedCount = 0;
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        string root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);

        foreach (string relativePath in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = GamePathPolicy.GetSafeTargetPath(installRoot, relativePath);
            if (!File.Exists(target))
            {
                continue;
            }

            DeleteFileWithRetry(target, cancellationToken);
            deletedCount++;

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
            TryDeleteDirectoryIfEmpty(directory);
        }

        return deletedCount;
    }

    private void DeleteFileWithRetry(
        string path,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < _retryPolicy.DeleteAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
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

    private static void TryDeleteDirectoryIfEmpty(string directory)
    {
        try
        {
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
