using System.IO;
using System.Net.Http;

namespace WotLK.Launcher.Game;

internal interface IGameFileTransferService
{
    Uri BuildFileUri(LauncherManifest manifest, LauncherFile file);

    Task DownloadAsync(
        long operationId,
        Uri uri,
        string targetPath,
        long expectedSize,
        string expectedSha256,
        Action<GameFileTransferProgress>? reportProgress,
        CancellationToken cancellationToken,
        IGameInstallRootLease? rootLease = null);
}

internal sealed record GameFileTransferRetryPolicy(
    int ReplacementAttempts,
    TimeSpan ReplacementDelay)
{
    internal static GameFileTransferRetryPolicy Legacy { get; } = new(
        ReplacementAttempts: 60,
        ReplacementDelay: TimeSpan.FromSeconds(1));
}

internal sealed class GameFileTransferService : IGameFileTransferService
{
    internal const int LegacyHttpAttemptCount = 1;

    private const int BufferSize = 1024 * 128;
    private readonly HttpClient _httpClient;
    private readonly GameFileTransferRetryPolicy _retryPolicy;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    internal GameFileTransferService(
        HttpClient httpClient,
        GameFileTransferRetryPolicy? retryPolicy = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _retryPolicy = retryPolicy ?? GameFileTransferRetryPolicy.Legacy;
        if (_retryPolicy.ReplacementAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryPolicy));
        }

        _delayAsync = delayAsync ?? Task.Delay;
    }

    public Uri BuildFileUri(LauncherManifest manifest, LauncherFile file)
    {
        return GameManifestValidator.ResolveFileUri(manifest, file);
    }

    public async Task DownloadAsync(
        long operationId,
        Uri uri,
        string targetPath,
        long expectedSize,
        string expectedSha256,
        Action<GameFileTransferProgress>? reportProgress,
        CancellationToken cancellationToken,
        IGameInstallRootLease? rootLease = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        _ = GameManifestValidator.RequireHttpsUri(uri, "URL de téléchargement du client");
        if (expectedSize is < 0 or > GameManifestValidator.MaximumFileBytes)
        {
            throw new InvalidDataException("Taille de téléchargement du client invalide.");
        }

        if (!GameManifestValidator.IsSha256(expectedSha256))
        {
            throw new InvalidDataException("SHA-256 de téléchargement du client invalide.");
        }

        string targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Chemin cible invalide.");
        // For production maintenance the directory chain is acquired before
        // the network wait and held until replacement/cleanup completes.
        using IGameInstallDirectoryLease? targetDirectoryLease =
            rootLease?.AcquireDirectory(
                targetDirectory,
                createIfMissing: true);
        targetDirectoryLease?.DemandChildFileSafe(targetPath, allowMissing: true);

        // v1.1.0 performs one HTTP request. Its retries only cover final replacement.
        using HttpResponseMessage response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        Uri responseUri = GameManifestValidator.RequireHttpsUri(
            response.RequestMessage?.RequestUri ?? uri,
            "URL finale de téléchargement du client");
        if (!responseUri.Equals(uri))
        {
            throw new InvalidDataException(
                "La redirection automatique du téléchargement du client est refusée.");
        }
        response.EnsureSuccessStatusCode();

        long? responseLength = response.Content.Headers.ContentLength;
        if (responseLength is < 0 || responseLength.HasValue && responseLength.Value != expectedSize)
        {
            throw new InvalidOperationException(
                $"Taille invalide pour {Path.GetFileName(targetPath)}: réponse distante inattendue.");
        }

        if (targetDirectoryLease is null)
        {
            Directory.CreateDirectory(targetDirectory);
        }

        string tempPath = Path.Combine(
            targetDirectory,
            "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".download");

        try
        {
            await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (FileStream local = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                BufferSize,
                useAsync: true))
            {
                byte[] buffer = new byte[BufferSize];
                long written = 0;

                while (true)
                {
                    int requested = (int)Math.Min(buffer.Length, expectedSize - written + 1L);
                    int read = await remote.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    if (written > expectedSize - read)
                    {
                        throw new InvalidOperationException(
                            $"Taille invalide pour {Path.GetFileName(targetPath)}: " +
                            $"plus de {GameTransferFormatting.FormatBytes(expectedSize)} recus.");
                    }

                    await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    reportProgress?.Invoke(new GameFileTransferProgress(
                        operationId,
                        written,
                        expectedSize,
                        GameFileTransferStage.Downloading));
                }

                if (written != expectedSize)
                {
                    throw new InvalidOperationException(
                        $"Taille invalide pour {Path.GetFileName(targetPath)}: " +
                        $"{GameTransferFormatting.FormatBytes(written)} recu, " +
                        $"{GameTransferFormatting.FormatBytes(expectedSize)} attendu.");
                }
            }

            targetDirectoryLease?.DemandChildFileSafe(tempPath, allowMissing: false);
            using IGameInstallFileReplacementLease? replacementLease =
                rootLease?.OpenFileForReplacement(tempPath);
            string downloadedHash;
            if (replacementLease is not null)
            {
                downloadedHash = await ComputeSha256Async(
                    replacementLease.Stream,
                    cancellationToken);
            }
            else
            {
                downloadedHash = await GameFileVerifier.ComputeSha256Async(
                    tempPath,
                    cancellationToken);
            }
            if (!string.Equals(
                    downloadedHash,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Hash invalide apres telechargement: " + Path.GetFileName(targetPath));
            }

            reportProgress?.Invoke(new GameFileTransferProgress(
                operationId,
                expectedSize,
                expectedSize,
                GameFileTransferStage.Applying));
            await MoveDownloadedFileWithRetryAsync(
                tempPath,
                targetPath,
                cancellationToken,
                targetDirectoryLease,
                replacementLease);
            reportProgress?.Invoke(new GameFileTransferProgress(
                operationId,
                expectedSize,
                expectedSize,
                GameFileTransferStage.Completed));
        }
        catch
        {
            DeleteFileIfExists(tempPath, targetDirectoryLease);
            throw;
        }
    }

    private async Task MoveDownloadedFileWithRetryAsync(
        string tempPath,
        string targetPath,
        CancellationToken cancellationToken,
        IGameInstallDirectoryLease? targetDirectoryLease = null,
        IGameInstallFileReplacementLease? replacementLease = null)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < _retryPolicy.ReplacementAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                targetDirectoryLease?.Revalidate();
                if (replacementLease is not null)
                {
                    replacementLease.ReplaceFile(targetPath);
                    return;
                }

                targetDirectoryLease?.DemandChildFileSafe(tempPath, allowMissing: false);
                targetDirectoryLease?.DemandChildFileSafe(targetPath, allowMissing: true);
                if (File.Exists(targetPath))
                {
                    if (targetDirectoryLease is null)
                    {
                        TrySetNormalAttributes(targetPath);
                    }
                    else
                    {
                        targetDirectoryLease.NormalizeChildFileAttributes(targetPath);
                    }
                }

                File.Move(tempPath, targetPath, overwrite: true);
                targetDirectoryLease?.DemandChildFileSafe(targetPath, allowMissing: false);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await _delayAsync(_retryPolicy.ReplacementDelay, cancellationToken);
            }
        }

        throw new IOException(
            "Impossible de remplacer " + Path.GetFileName(targetPath) +
            ". Ferme le jeu ou tout programme qui utilise le dossier WotLK, puis relance l'installation.",
            lastError);
    }

    private static void DeleteFileIfExists(
        string path,
        IGameInstallDirectoryLease? parentLease = null)
    {
        try
        {
            if (parentLease is not null)
            {
                parentLease.DemandChildFileSafe(path, allowMissing: true);
                if (File.Exists(path))
                {
                    parentLease.DeleteChildFile(path);
                }

                return;
            }

            if (File.Exists(path))
            {
                TrySetNormalAttributes(path);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static async Task<string> ComputeSha256Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using System.Security.Cryptography.SHA256 sha =
            System.Security.Cryptography.SHA256.Create();
        byte[] digest = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void TrySetNormalAttributes(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
        }
    }
}

internal static class GameTransferFormatting
{
    internal static string FormatBytes(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        double value = Math.Max(bytes, 0);
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.##} {1}",
            value,
            units[unit]);
    }
}
