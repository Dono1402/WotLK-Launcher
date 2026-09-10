using System.IO;
using System.Text;
using System.Text.Json;

namespace WotLK.Launcher.Game;

internal interface IInstalledManifestStore
{
    string GetPath(string installRoot);

    LauncherManifest? Load(
        string installRoot,
        IGameInstallRootLease? rootLease = null);

    void Save(
        string installRoot,
        LauncherManifest manifest,
        IGameInstallRootLease? rootLease = null);
}

internal sealed class InstalledManifestStore : IInstalledManifestStore
{
    internal const string CacheFileName = "client-manifest-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24
    };

    private readonly Func<string, bool> _canWrite;

    internal InstalledManifestStore(Func<string, bool>? canWrite = null)
    {
        _canWrite = canWrite ?? GameDirectoryAccess.CanWrite;
    }

    public string GetPath(string installRoot)
    {
        return Path.Combine(installRoot, CacheFileName);
    }

    public LauncherManifest? Load(
        string installRoot,
        IGameInstallRootLease? rootLease = null)
    {
        if (rootLease is not null)
        {
            installRoot = GameInstallServices.DemandLeaseMatchesGameRoot(
                installRoot,
                rootLease);
        }

        string historyPath = GetPath(installRoot);
        IGameInstallReadLease? readLease = null;
        FileStream? standalone = null;
        if (rootLease is not null)
        {
            using IGameInstallDirectoryLease rootDirectory = rootLease.AcquireDirectory(
                installRoot,
                createIfMissing: false);
            rootDirectory.DemandChildFileSafe(historyPath, allowMissing: true);
            if (!File.Exists(historyPath))
            {
                return null;
            }

            // Acquire the stable, single-link handle outside the cache parsing
            // catch below. A corrupt cache is recoverable; a reparse point or
            // hard link is a filesystem-integrity failure and must be refused.
            readLease = rootLease.OpenFileForRead(historyPath);
        }
        else
        {
            try
            {
                standalone = new FileStream(
                    historyPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 128 * 1024,
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
            Stream stream = readLease?.Stream ?? standalone!;
            byte[] payload = ReadBoundedManifest(stream);
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = JsonOptions.MaxDepth
            });
            BoundedJsonHttpContent.RejectDuplicateProperties(
                document.RootElement,
                "Le cache local du manifeste");
            LauncherManifest manifest = document.RootElement.Deserialize<LauncherManifest>(JsonOptions)
                ?? throw new InvalidDataException("Le cache local du manifeste est vide.");
            GameManifestValidator.Validate(manifest);
            return manifest;
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

    public void Save(
        string installRoot,
        LauncherManifest manifest,
        IGameInstallRootLease? rootLease = null)
    {
        if (rootLease is not null)
        {
            installRoot = GameInstallServices.DemandLeaseMatchesGameRoot(
                installRoot,
                rootLease);
        }

        if (!_canWrite(installRoot))
        {
            return;
        }

        GameManifestValidator.Validate(manifest);
        JsonSerializerOptions options = new(JsonOptions)
        {
            WriteIndented = true
        };
        byte[] payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(manifest, options) + Environment.NewLine);
        if (payload.Length > GameManifestValidator.MaximumManifestBytes)
        {
            throw new InvalidDataException("Le cache local du manifeste est trop volumineux.");
        }

        string path = GetPath(installRoot);
        if (rootLease is not null)
        {
            rootLease.WriteFileAtomically(path, stream => stream.Write(payload));
            return;
        }

        Directory.CreateDirectory(installRoot);
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 128 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static byte[] ReadBoundedManifest(Stream source)
    {
        if (source.CanSeek
            && (source.Length <= 0
                || source.Length > GameManifestValidator.MaximumManifestBytes))
        {
            throw new InvalidDataException("Le cache local du manifeste a une taille invalide.");
        }

        using MemoryStream payload = new();
        byte[] buffer = new byte[128 * 1024];
        while (true)
        {
            int remaining = GameManifestValidator.MaximumManifestBytes
                + 1
                - checked((int)payload.Length);
            int read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                break;
            }

            payload.Write(buffer, 0, read);
            if (payload.Length > GameManifestValidator.MaximumManifestBytes)
            {
                throw new InvalidDataException("Le cache local du manifeste est trop volumineux.");
            }
        }

        if (payload.Length == 0)
        {
            throw new InvalidDataException("Le cache local du manifeste est vide.");
        }

        return payload.ToArray();
    }
}
