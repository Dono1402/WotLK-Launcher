using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace WotLK.Launcher;

internal static partial class GameInstallServices
{
    internal const string InstallRootOwnershipMarkerFileName =
        ".atlas-wotlk-root.json";
    private const int InstallRootOwnershipSchema = 1;
    private const int MaximumInstallRootOwnershipMarkerBytes = 8 * 1024;
    private const long MaximumBuildInfoBytes = 4L * 1024L * 1024L;

    internal static GameInstallRootOwnership PrepareGameInstallRoot(
        string installRoot)
    {
        using IGameInstallRootLease lease = AcquireGameInstallRootLease(
            installRoot,
            GameInstallRootLeaseMode.PrepareInstall);
        return lease.Ownership;
    }

    internal static GameInstallRootOwnership
        ValidateGameInstallRootForRegistration(string installRoot)
    {
        using IGameInstallRootLease lease = AcquireGameInstallRootLease(
            installRoot,
            GameInstallRootLeaseMode.ExistingClient);
        return lease.Ownership;
    }

    internal static void ValidateGameRootOwnershipForUninstall(
        GameUninstallIdentity identity,
        GameInstallMarker installMarker)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(installMarker);
        if (!string.IsNullOrWhiteSpace(installMarker.OwnershipId)
            && Guid.TryParseExact(
                installMarker.OwnershipId,
                "N",
                out Guid expectedOwnershipId)
            && TryReadInstallRootOwnership(
                identity.InstallRoot,
                out GameInstallRootOwnership? ownership)
            && ownership.OwnershipId == expectedOwnershipId)
        {
            return;
        }

        // Legacy markers predate the dedicated ownership proof. They remain
        // deletable only when the immutable client sentinels identify an actual
        // WotLK tree; the next registration/migration writes the new proof.
        DemandRecognizedWotlkClientRoot(identity.InstallRoot);
    }

    internal static bool TryReadInstallRootOwnership(
        string installRoot,
        [NotNullWhen(true)]
        out GameInstallRootOwnership? ownership)
    {
        ownership = null;
        string root = NormalizeAndValidateGameRoot(installRoot);
        string markerPath = Path.Combine(
            root,
            InstallRootOwnershipMarkerFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        DemandNoReparsePoints(markerPath, requireLeaf: true);
        byte[] bytes;
        using (FileStream stream = OpenStableReadStream(markerPath))
        {
            DemandSingleHardLink(stream.SafeFileHandle, markerPath);
            if (stream.Length <= 0
                || stream.Length > MaximumInstallRootOwnershipMarkerBytes)
            {
                throw new InvalidDataException(
                    "La preuve de propriété du dossier WotLK a une taille invalide.");
            }

            bytes = new byte[checked((int)stream.Length)];
            stream.Position = 0;
            stream.ReadExactly(bytes);
        }

        GameInstallRootOwnershipMarker marker;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 6
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    "La preuve de propriété WotLK doit être un objet JSON.");
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        "La preuve de propriété WotLK contient une propriété dupliquée.");
                }
            }

            marker = document.RootElement.Deserialize<GameInstallRootOwnershipMarker>(
                    InstallMarkerJsonOptions)
                ?? throw new InvalidDataException(
                    "La preuve de propriété WotLK est vide.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "La preuve de propriété WotLK est invalide.",
                exception);
        }

        if (marker.Schema != InstallRootOwnershipSchema
            || marker.CreatedAt == default
            || string.IsNullOrWhiteSpace(marker.InstallRoot)
            || !IsCanonicalAbsolutePath(marker.InstallRoot, isDirectory: true)
            || !SamePath(marker.InstallRoot, root)
            || !Guid.TryParseExact(marker.OwnershipId, "N", out Guid ownershipId)
            || ownershipId == Guid.Empty)
        {
            throw new InvalidDataException(
                "La preuve de propriété WotLK ne correspond pas au dossier.");
        }

        ownership = new GameInstallRootOwnership(root, ownershipId);
        return true;
    }

    internal static void DemandRecognizedWotlkClientRoot(string installRoot)
    {
        string root = NormalizeAndValidateGameRoot(installRoot);
        string dataDirectory = Path.Combine(root, "Data");
        string buildInfo = Path.Combine(root, ".build.info");
        string wowExecutable = GetGameExecutablePath(root);
        DemandNoReparsePoints(dataDirectory, requireLeaf: true);
        DemandNoReparsePoints(buildInfo, requireLeaf: true);
        DemandNoReparsePoints(wowExecutable, requireLeaf: true);

        using SafeFileHandle dataHandle = OpenValidatedFileSystemEntry(
            dataDirectory,
            expectDirectory: true,
            denyDeleteSharing: true);
        using FileStream buildInfoStream = OpenStableReadStream(buildInfo);
        DemandSingleHardLink(buildInfoStream.SafeFileHandle, buildInfo);
        if (buildInfoStream.Length <= 0
            || buildInfoStream.Length > MaximumBuildInfoBytes)
        {
            throw new InvalidDataException(
                "Le fichier .build.info WotLK a une taille invalide.");
        }

        using FileStream wowStream = OpenStableReadStream(wowExecutable);
        DemandSingleHardLink(wowStream.SafeFileHandle, wowExecutable);
        ValidateStableFileLength(wowStream, "client WotLK");
    }

    internal static void DemandGameRootIsNotSensitive(
        string canonicalRoot,
        string originalPath)
    {
        foreach (string sensitiveRoot in EnumerateSensitiveGameRoots())
        {
            if (string.IsNullOrWhiteSpace(sensitiveRoot))
            {
                continue;
            }

            string canonicalSensitive;
            try
            {
                canonicalSensitive = Path.GetFullPath(sensitiveRoot)
                    .TrimEnd(Path.DirectorySeparatorChar);
            }
            catch
            {
                continue;
            }

            if (SamePath(canonicalRoot, canonicalSensitive)
                || IsPathInside(canonicalSensitive, canonicalRoot)
                || IsPathInside(canonicalRoot, canonicalSensitive))
            {
                throw new InvalidOperationException(
                    "Dossier WotLK refusé pour sécurité: " + originalPath);
            }
        }
    }

    private static IEnumerable<string> EnumerateSensitiveGameRoots()
    {
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        yield return userProfile;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty;
        yield return Path.GetTempPath();
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "Downloads");
            yield return Path.Combine(userProfile, "OneDrive");
        }

        yield return Environment.GetEnvironmentVariable("OneDrive") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("OneDriveConsumer") ?? string.Empty;
        yield return Environment.GetEnvironmentVariable("OneDriveCommercial") ?? string.Empty;
    }

    internal static GameInstallRootOwnership WriteInstallRootOwnership(
        string root,
        string markerPath,
        Action<string>? beforeRenameForTests = null)
    {
        Guid ownershipId = Guid.NewGuid();
        GameInstallRootOwnershipMarker marker = new()
        {
            Schema = InstallRootOwnershipSchema,
            CreatedAt = DateTimeOffset.UtcNow,
            InstallRoot = root,
            OwnershipId = ownershipId.ToString("N", CultureInfo.InvariantCulture)
        };
        byte[] bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(marker, InstallMarkerJsonOptions)
            + Environment.NewLine);
        if (bytes.Length <= 0 || bytes.Length > MaximumInstallRootOwnershipMarkerBytes)
        {
            throw new InvalidDataException(
                "La nouvelle preuve de propriété WotLK a une taille invalide.");
        }

        string temporary = markerPath
            + ".new-"
            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            + ".tmp";
        using FileStream stream = CreateStableReplacementFile(temporary);
        bool renamed = false;
        try
        {
            DemandNoReparsePoints(markerPath, requireLeaf: false);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            if (stream.Length != bytes.Length)
            {
                throw new InvalidDataException(
                    "La preuve de propriété WotLK écrite est incomplète.");
            }

            DemandSingleHardLink(stream.SafeFileHandle, temporary);
            beforeRenameForTests?.Invoke(temporary);

            try
            {
                ReplaceFileByHandle(
                    stream.SafeFileHandle,
                    markerPath,
                    replaceIfExists: false);
                renamed = true;
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                if (TryReadInstallRootOwnership(
                        root,
                        out GameInstallRootOwnership? concurrent))
                {
                    return concurrent;
                }

                throw;
            }

            return new GameInstallRootOwnership(root, ownershipId);
        }
        finally
        {
            if (!renamed)
            {
                TryDeleteFileByHandle(stream.SafeFileHandle, temporary);
            }
        }
    }
}

internal sealed record GameInstallRootOwnership(
    string InstallRoot,
    Guid OwnershipId);

internal sealed class GameInstallRootOwnershipMarker
{
    [JsonRequired]
    public int Schema { get; init; }

    [JsonRequired]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonRequired]
    public string InstallRoot { get; init; } = string.Empty;

    [JsonRequired]
    public string OwnershipId { get; init; } = string.Empty;
}
