using System.IO;
using System.Text.Json;

namespace WotLK.Launcher.Game;

internal sealed class GameClientStateReader
{
    private readonly Func<string, bool> _hasPlayableClient;

    internal GameClientStateReader(Func<string, bool>? hasPlayableClient = null)
    {
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
    }

    internal GameClientLocalState Read(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string installPath = LauncherSettings.NormalizeInstallPath(settings.InstallPath);
        bool isPlayable = _hasPlayableClient(installPath);
        return new GameClientLocalState(
            installPath,
            LauncherSettings.NormalizeGameLocale(settings.GameLocale),
            isPlayable,
            ReadInstalledVersion(installPath),
            GameUpdateKnowledge.Unknown);
    }

    internal string? ReadInstalledVersion(
        string installPath,
        IGameInstallRootLease? rootLease = null)
    {
        if (rootLease is not null)
        {
            installPath = GameInstallServices.DemandLeaseMatchesGameRoot(
                installPath,
                rootLease);
        }

        string markerPath = Path.Combine(installPath, GameInstallServices.ClientMarkerFileName);
        IGameInstallReadLease? readLease = null;
        FileStream? standalone = null;
        if (rootLease is not null)
        {
            using IGameInstallDirectoryLease rootDirectory = rootLease.AcquireDirectory(
                installPath,
                createIfMissing: false);
            rootDirectory.DemandChildFileSafe(markerPath, allowMissing: true);
            if (!File.Exists(markerPath))
            {
                return null;
            }

            readLease = rootLease.OpenFileForRead(markerPath);
        }
        else
        {
            try
            {
                standalone = new FileStream(
                    markerPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 16 * 1024,
                    FileOptions.SequentialScan);
            }
            catch (Exception exception) when (exception is IOException
                                               or UnauthorizedAccessException)
            {
                return null;
            }
        }

        try
        {
            Stream source = readLease?.Stream ?? standalone!;
            if (source.Length is <= 0 or > GameInstallServices.MaximumInstallMarkerBytes)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(
                source,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            BoundedJsonHttpContent.RejectDuplicateProperties(
                document.RootElement,
                "Le marqueur local du client WotLK");
            if (!document.RootElement.TryGetProperty("clientVersion", out JsonElement versionElement))
            {
                return null;
            }

            string? version = versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;
            return version is { Length: > 0 and <= 128 }
                   && !version.Any(char.IsControl)
                ? version
                : null;
        }
        catch (Exception ex) when (ex is JsonException
                                      or InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            readLease?.Dispose();
            standalone?.Dispose();
        }
    }
}
