using System.IO;

namespace WotLK.Launcher.Game;

internal enum GameManagedFileStatus
{
    Valid,
    Missing,
    SizeMismatch,
    HashMismatch,
    InvalidPath,
    ReadError
}

internal enum GameManagedFileReadFailure
{
    None,
    Io,
    Permission
}

internal sealed record GameManagedFileVerification(
    LauncherFile File,
    GameManagedFileStatus Status,
    GameManagedFileReadFailure ReadFailure = GameManagedFileReadFailure.None);

internal sealed record GameFullVerificationProgress(
    string CurrentFile,
    int ProcessedFileCount,
    int TotalFileCount);

internal sealed record GameFullVerificationResult(
    IReadOnlyList<GameManagedFileVerification> Files)
{
    internal IReadOnlyList<LauncherFile> RepairFiles => Files
        .Where(result => result.Status is GameManagedFileStatus.Missing
            or GameManagedFileStatus.SizeMismatch
            or GameManagedFileStatus.HashMismatch)
        .Select(result => result.File)
        .ToList();

    internal IReadOnlyList<GameManagedFileVerification> BlockingFailures => Files
        .Where(result => result.Status is GameManagedFileStatus.InvalidPath
            or GameManagedFileStatus.ReadError)
        .ToList();
}

internal interface IGameFullFileVerifier
{
    Task<GameFullVerificationResult> VerifyAllAsync(
        string installRoot,
        LauncherManifest manifest,
        Action<GameFullVerificationProgress>? reportProgress,
        CancellationToken cancellationToken);
}

internal sealed class GameFullFileVerifier : IGameFullFileVerifier
{
    private readonly Func<string, CancellationToken, Task<string>> _computeSha256Async;

    internal GameFullFileVerifier(
        Func<string, CancellationToken, Task<string>>? computeSha256Async = null)
    {
        _computeSha256Async = computeSha256Async
            ?? GameFileVerifier.ComputeSha256Async;
    }

    public async Task<GameFullVerificationResult> VerifyAllAsync(
        string installRoot,
        LauncherManifest manifest,
        Action<GameFullVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        List<GameManagedFileVerification> results = [];
        for (int index = 0; index < manifest.Files.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LauncherFile file = manifest.Files[index];
            GameManagedFileVerification result = await VerifyOneAsync(
                installRoot,
                file,
                cancellationToken);
            results.Add(result);
            reportProgress?.Invoke(new GameFullVerificationProgress(
                file.Path,
                index + 1,
                manifest.Files.Count));
        }

        return new GameFullVerificationResult(results);
    }

    private async Task<GameManagedFileVerification> VerifyOneAsync(
        string installRoot,
        LauncherFile file,
        CancellationToken cancellationToken)
    {
        string target;
        try
        {
            target = GamePathPolicy.GetSafeTargetPath(installRoot, file.Path);
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            return new GameManagedFileVerification(
                file,
                GameManagedFileStatus.InvalidPath);
        }

        try
        {
            _ = File.GetAttributes(target);
            if (new FileInfo(target).Length != file.Size)
            {
                return new GameManagedFileVerification(
                    file,
                    GameManagedFileStatus.SizeMismatch);
            }

            string localHash = await _computeSha256Async(target, cancellationToken);
            return new GameManagedFileVerification(
                file,
                string.Equals(localHash, file.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? GameManagedFileStatus.Valid
                    : GameManagedFileStatus.HashMismatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                   or DirectoryNotFoundException)
        {
            return new GameManagedFileVerification(
                file,
                GameManagedFileStatus.Missing);
        }
        catch (UnauthorizedAccessException)
        {
            return new GameManagedFileVerification(
                file,
                GameManagedFileStatus.ReadError,
                GameManagedFileReadFailure.Permission);
        }
        catch (IOException)
        {
            return new GameManagedFileVerification(
                file,
                GameManagedFileStatus.ReadError,
                GameManagedFileReadFailure.Io);
        }
    }
}
