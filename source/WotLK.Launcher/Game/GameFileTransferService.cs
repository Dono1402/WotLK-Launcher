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
        CancellationToken cancellationToken);
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
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(file);

        if (Uri.TryCreate(file.Url, UriKind.Absolute, out Uri? absoluteUri))
        {
            return absoluteUri;
        }

        string baseUrl = string.IsNullOrWhiteSpace(manifest.BaseUrl)
            ? throw new InvalidOperationException("baseUrl manquant dans le manifeste.")
            : manifest.BaseUrl.TrimEnd('/') + "/";

        string relativeUrl = string.IsNullOrWhiteSpace(file.Url)
            ? "files/" + EscapeRelativeUrl(file.Path)
            : file.Url.TrimStart('/');

        return new Uri(new Uri(baseUrl), relativeUrl);
    }

    public async Task DownloadAsync(
        long operationId,
        Uri uri,
        string targetPath,
        long expectedSize,
        string expectedSha256,
        Action<GameFileTransferProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        // v1.1.0 performs one HTTP request. Its retries only cover final replacement.
        using HttpResponseMessage response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        string targetDirectory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException("Chemin cible invalide.");
        Directory.CreateDirectory(targetDirectory);
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
                    int read = await remote.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    reportProgress?.Invoke(new GameFileTransferProgress(
                        operationId,
                        written,
                        expectedSize >= 0 ? expectedSize : null));
                }

                if (expectedSize >= 0 && written != expectedSize)
                {
                    throw new InvalidOperationException(
                        $"Taille invalide pour {Path.GetFileName(targetPath)}: " +
                        $"{GameTransferFormatting.FormatBytes(written)} recu, " +
                        $"{GameTransferFormatting.FormatBytes(expectedSize)} attendu.");
                }
            }

            string downloadedHash = await GameFileVerifier.ComputeSha256Async(
                tempPath,
                cancellationToken);
            if (!string.Equals(
                    downloadedHash,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Hash invalide apres telechargement: " + Path.GetFileName(targetPath));
            }

            await MoveDownloadedFileWithRetryAsync(
                tempPath,
                targetPath,
                cancellationToken);
        }
        catch
        {
            DeleteFileIfExists(tempPath);
            throw;
        }
    }

    private async Task MoveDownloadedFileWithRetryAsync(
        string tempPath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < _retryPolicy.ReplacementAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(targetPath))
                {
                    TrySetNormalAttributes(targetPath);
                }

                File.Move(tempPath, targetPath, overwrite: true);
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

    private static string EscapeRelativeUrl(string path)
    {
        return string.Join(
            "/",
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
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
