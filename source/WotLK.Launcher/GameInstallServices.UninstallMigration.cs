using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WotLK.Launcher.Updater;

namespace WotLK.Launcher;

internal static partial class GameInstallServices
{
    private const int MaximumMigrationRegistryStringCharacters = 4096;
    private const int MaximumMigrationRegistryValueCount = 32;
    private const uint RegistryStringType = 1;
    private const uint RegistryDwordType = 4;
    private const int ErrorSuccess = 0;
    private const string MigrationPendingRegistrySubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WotLK.Client.AtlasMigrationPending";
    private const string MigrationPendingSchemaValue = "AtlasMigrationSchema";
    private const int MigrationPendingSchema = 1;

    internal static GameUninstallMigrationResult
        MigrateRegisteredGameUninstallerAtStartup()
    {
        GameUninstallMigrationResult result = MigrateRegisteredGameUninstaller(
            Environment.ProcessPath,
            new WindowsGameUninstallMigrationRegistryStore());
        WriteGameUninstallMigrationDiagnostic(result);
        return result;
    }

    internal static GameUninstallMigrationResult
        NeutralizeRegisteredGameUninstallerForPendingUpdateAtStartup()
    {
        GameUninstallMigrationResult result =
            NeutralizeRegisteredGameUninstallerForPendingUpdate(
                new WindowsGameUninstallMigrationRegistryStore());
        WriteGameUninstallMigrationDiagnostic(result);
        return result;
    }

    internal static GameUninstallMigrationResult
        NeutralizeRegisteredGameUninstallerForPendingUpdate(
            IGameUninstallMigrationRegistryStore registryStore)
    {
        ArgumentNullException.ThrowIfNull(registryStore);
        GameUninstallMigrationRegistration? registration;
        try
        {
            registration = registryStore.Read();
        }
        catch
        {
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                "registry-read-refused");
        }

        if (registration is null)
        {
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.NoRegistration,
                "registration-absent");
        }

        if (!TryRecognizeMigrationRegistration(registration, out _))
        {
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                "registration-foreign");
        }

        try
        {
            return registryStore.TryNeutralize(registration)
                ? new GameUninstallMigrationResult(
                    GameUninstallMigrationStatus.Neutralized,
                    "pending-update-neutralized")
                : new GameUninstallMigrationResult(
                    GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                    "registration-raced");
        }
        catch
        {
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                "registration-raced");
        }
    }

    internal static GameUninstallMigrationResult MigrateRegisteredGameUninstaller(
        string? currentExecutablePath,
        IGameUninstallMigrationRegistryStore registryStore,
        Action<GameUninstallMigrationFaultPoint>? fault = null)
    {
        ArgumentNullException.ThrowIfNull(registryStore);
        GameUninstallMigrationRegistration? registration;
        try
        {
            registration = registryStore.Read();
        }
        catch
        {
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                "registry-read-refused");
        }

        if (registration is null)
        {
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.NoRegistration,
                "registration-absent");
        }

        if (!TryRecognizeMigrationRegistration(
                registration,
                out GameUninstallIdentity identity))
        {
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                "registration-foreign");
        }

        bool neutralized = false;
        List<SafeFileHandle> destinationAncestorHandles = [];
        try
        {
            destinationAncestorHandles = OpenAncestorDirectoryHandles(
                identity.UninstallerPath);
            ValidateMigrationEvidence(identity);
        }
        catch
        {
            DisposeHandles(destinationAncestorHandles);
            try
            {
                neutralized = registryStore.TryNeutralize(registration);
            }
            catch
            {
                neutralized = false;
            }
            return new GameUninstallMigrationResult(
                neutralized
                    ? GameUninstallMigrationStatus.Neutralized
                    : GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                neutralized ? "evidence-refused" : "registration-raced");
        }

        FileStream? source = null;
        try
        {
            if (string.IsNullOrWhiteSpace(currentExecutablePath)
                || !Path.IsPathFullyQualified(currentExecutablePath))
            {
                throw new InvalidDataException(
                    "L'exécutable courant du launcher est invalide.");
            }

            source = OpenStableReadStream(Path.GetFullPath(currentExecutablePath));
            DemandSingleHardLink(source.SafeFileHandle, currentExecutablePath);
            ValidateStableFileLength(source, "launcher Atlas");
            string expectedSha256 = ComputeSha256(source);

            if (StableFileMatches(
                    identity.UninstallerPath,
                    source.Length,
                    expectedSha256))
            {
                if (registration.IsPendingSnapshot)
                {
                    registryStore.Register(registration, identity);
                    return new GameUninstallMigrationResult(
                        GameUninstallMigrationStatus.Migrated,
                        "registration-restored");
                }

                return new GameUninstallMigrationResult(
                    GameUninstallMigrationStatus.Current,
                    "uninstaller-current");
            }

            if (!registryStore.TryNeutralize(registration))
            {
                return new GameUninstallMigrationResult(
                    GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                    "registration-raced");
            }

            neutralized = true;
            fault?.Invoke(GameUninstallMigrationFaultPoint.AfterNeutralized);
            ReplaceFileFromStableSource(
                source,
                identity.UninstallerPath,
                expectedSha256,
                fault);
            WriteInstallMarkerAtomically(
                identity,
                registration.DisplayVersion ?? GetProductVersion());
            ValidateGameInstallMarker(
                Path.Combine(identity.InstallRoot, ClientMarkerFileName),
                identity);
            registryStore.Register(registration, identity);
            return new GameUninstallMigrationResult(
                GameUninstallMigrationStatus.Migrated,
                "uninstaller-migrated");
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or ArgumentException
            or NotSupportedException)
        {
            if (!neutralized)
            {
                try
                {
                    neutralized = registryStore.TryNeutralize(registration);
                }
                catch
                {
                    neutralized = false;
                }
            }

            return new GameUninstallMigrationResult(
                neutralized
                    ? GameUninstallMigrationStatus.Neutralized
                    : GameUninstallMigrationStatus.ForeignRegistrationPreserved,
                neutralized
                    ? exception is UnauthorizedAccessException
                        ? "write-access-refused"
                        : "migration-failed"
                    : "registration-raced");
        }
        finally
        {
            source?.Dispose();
            DisposeHandles(destinationAncestorHandles);
        }
    }

    internal static bool TryRecognizeMigrationRegistration(
        GameUninstallMigrationRegistration registration,
        out GameUninstallIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(registration);
        identity = default!;
        if (!registration.UsesOnlyLiteralBoundedValues
            || !string.Equals(
                registration.DisplayName,
                AppDisplayName,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(registration.InstallLocation)
            || string.IsNullOrWhiteSpace(registration.DisplayVersion)
            || registration.DisplayVersion.Length > 128
            || !IsCanonicalAbsolutePath(
                registration.InstallLocation,
                isDirectory: true))
        {
            return false;
        }

        try
        {
            string root = NormalizeAndValidateGameRoot(
                registration.InstallLocation);
            string uninstallerPath = Path.Combine(root, UninstallerFileName);
            string expectedUninstall = Quote(uninstallerPath)
                + " /uninstall-game";
            string expectedQuiet = expectedUninstall + " /quiet";
            if (!string.Equals(
                    registration.UninstallString,
                    expectedUninstall,
                    StringComparison.Ordinal)
                || !string.Equals(
                    registration.QuietUninstallString,
                    expectedQuiet,
                    StringComparison.Ordinal))
            {
                return false;
            }

            identity = new GameUninstallIdentity(root, uninstallerPath);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private static void ValidateMigrationEvidence(
        GameUninstallIdentity identity)
    {
        DemandNoReparsePoints(identity.InstallRoot, requireLeaf: true);
        DemandNoReparsePoints(identity.UninstallerPath, requireLeaf: true);
        string markerPath = Path.Combine(
            identity.InstallRoot,
            ClientMarkerFileName);
        ValidateGameInstallMarker(markerPath, identity);
    }

    private static bool StableFileMatches(
        string path,
        long expectedLength,
        string expectedSha256)
    {
        using FileStream stream = OpenStableReadStream(path);
        DemandSingleHardLink(stream.SafeFileHandle, path);
        return stream.Length == expectedLength
            && string.Equals(
                ComputeSha256(stream),
                expectedSha256,
                StringComparison.Ordinal);
    }

    private static void ReplaceFileFromStableSource(
        FileStream source,
        string destinationPath,
        string expectedSha256,
        Action<GameUninstallMigrationFaultPoint>? fault)
    {
        string destination = Path.GetFullPath(destinationPath);
        string temporary = destination
            + ".migration-"
            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            + ".tmp";
        try
        {
            DemandNoReparsePoints(destination, requireLeaf: true);
            DemandNoReparsePoints(temporary, requireLeaf: false);
            source.Position = 0;
            using (FileStream output = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.WriteThrough))
            {
                source.CopyTo(output, 128 * 1024);
                output.Flush(flushToDisk: true);
            }

            fault?.Invoke(GameUninstallMigrationFaultPoint.AfterCandidateCopied);
            using (FileStream candidate = OpenStableReadStream(temporary))
            {
                DemandSingleHardLink(candidate.SafeFileHandle, temporary);
                if (candidate.Length != source.Length
                    || !string.Equals(
                        ComputeSha256(candidate),
                        expectedSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Le candidat de migration WotLK est altéré.");
                }
            }

            fault?.Invoke(GameUninstallMigrationFaultPoint.BeforeAtomicReplace);
            DemandNoReparsePoints(destination, requireLeaf: true);
            new WindowsLauncherAtomicFileMover().Replace(temporary, destination);
            using FileStream installed = OpenStableReadStream(destination);
            DemandSingleHardLink(installed.SafeFileHandle, destination);
            if (installed.Length != source.Length
                || !string.Equals(
                    ComputeSha256(installed),
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Le désinstalleur WotLK migré est altéré.");
            }
        }
        finally
        {
            TryDeleteMigrationTemporaryFile(temporary);
        }
    }

    private static void WriteInstallMarkerAtomically(
        GameUninstallIdentity identity,
        string clientVersion)
    {
        GameInstallRootOwnership ownership =
            ValidateGameInstallRootForRegistration(identity.InstallRoot);
        GameInstallMarker marker = new()
        {
            InstalledAt = DateTimeOffset.Now,
            ClientVersion = clientVersion,
            InstallRoot = identity.InstallRoot,
            Uninstaller = identity.UninstallerPath,
            RegisteredApp = AppDisplayName,
            OwnershipId = ownership.OwnershipId.ToString(
                "N",
                CultureInfo.InvariantCulture)
        };
        byte[] bytes = Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(
                marker,
                InstallMarkerJsonOptions)
            + Environment.NewLine);
        if (bytes.Length <= 0 || bytes.Length > MaximumInstallMarkerBytes)
        {
            throw new InvalidDataException(
                "Le nouveau marqueur WotLK a une taille invalide.");
        }

        string markerPath = Path.Combine(
            identity.InstallRoot,
            ClientMarkerFileName);
        string temporary = markerPath
            + ".migration-"
            + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
            + ".tmp";
        try
        {
            DemandNoReparsePoints(markerPath, requireLeaf: true);
            DemandNoReparsePoints(temporary, requireLeaf: false);
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            using (FileStream verification = OpenStableReadStream(temporary))
            {
                if (verification.Length != bytes.Length
                    || !SHA256.HashData(verification).AsSpan()
                        .SequenceEqual(SHA256.HashData(bytes)))
                {
                    throw new InvalidDataException(
                        "Le nouveau marqueur WotLK est altéré.");
                }
            }

            new WindowsLauncherAtomicFileMover().Replace(
                temporary,
                markerPath);
        }
        finally
        {
            TryDeleteMigrationTemporaryFile(temporary);
        }
    }

    private static void TryDeleteMigrationTemporaryFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            DemandNoReparsePoints(path, requireLeaf: true);
            using (SafeFileHandle handle = OpenValidatedFileSystemEntry(
                       path,
                       expectDirectory: false,
                       denyDeleteSharing: true))
            {
                DemandSingleHardLink(handle, path);
            }
            File.Delete(path);
        }
        catch
        {
            // The migration never removes game data; a unique temporary is harmless.
        }
    }

    internal static bool IsCanonicalAbsolutePath(
        string path,
        bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.Length > MaximumMigrationRegistryStringCharacters
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            string candidate = isDirectory
                ? path.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                : path;
            string canonical = Path.GetFullPath(path);
            if (isDirectory)
            {
                canonical = canonical.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }

            return string.Equals(
                candidate,
                canonical,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteGameUninstallMigrationDiagnostic(
        GameUninstallMigrationResult result)
    {
        try
        {
            if (result.Status is GameUninstallMigrationStatus.NoRegistration
                or GameUninstallMigrationStatus.Current)
            {
                return;
            }

            Directory.CreateDirectory(LauncherSettings.SettingsDirectory);
            string line = result.Status switch
            {
                GameUninstallMigrationStatus.Migrated =>
                    "Migration locale du désinstalleur WotLK terminée.",
                GameUninstallMigrationStatus.Neutralized =>
                    "Entrée de désinstallation WotLK neutralisée; une réparation locale est requise.",
                _ =>
                    "Entrée de désinstallation non reconnue conservée sans modification."
            };
            File.AppendAllText(
                LauncherSettings.LauncherLogPath,
                $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // A user-level diagnostic must never block startup.
        }
    }

    private sealed class WindowsGameUninstallMigrationRegistryStore
        : IGameUninstallMigrationRegistryStore
    {
        public GameUninstallMigrationRegistration? Read()
        {
            using RegistryKey baseKey = OpenUninstallBaseKey();
            using RegistryKey? active = baseKey.OpenSubKey(
                RegistrySubKey,
                writable: false);
            if (active is not null)
            {
                // An active Apps entry is authoritative. A leftover internal
                // snapshot must not be able to resurrect a different root later.
                TryDeletePendingSnapshot(baseKey);
                return ReadActiveRegistration(active);
            }

            using RegistryKey? pending = baseKey.OpenSubKey(
                MigrationPendingRegistrySubKey,
                writable: false);
            if (pending is null)
            {
                return null;
            }

            try
            {
                return ReadPendingRegistration(pending);
            }
            catch
            {
                TryDeletePendingSnapshot(baseKey);
                return null;
            }
        }

        private static GameUninstallMigrationRegistration ReadActiveRegistration(
            RegistryKey key)
        {
            bool bounded = key.ValueCount <= MaximumMigrationRegistryValueCount;
            string? ReadRequired(string name)
            {
                if (!TryReadLiteralRegistryString(key, name, out string? value))
                {
                    bounded = false;
                    return null;
                }

                return value;
            }

            string? displayName = ReadRequired("DisplayName");
            string? displayVersion = ReadRequired("DisplayVersion");
            string? installLocation = ReadRequired("InstallLocation");
            string? uninstallString = ReadRequired("UninstallString");
            string? quietUninstallString = ReadRequired("QuietUninstallString");
            int estimatedSize = TryReadRegistryDword(
                key,
                "EstimatedSize",
                out int size)
                && size >= 0
                    ? size
                    : 0;

            return new GameUninstallMigrationRegistration(
                displayName,
                displayVersion,
                installLocation,
                uninstallString,
                quietUninstallString,
                estimatedSize,
                bounded,
                IsPendingSnapshot: false);
        }

        private static GameUninstallMigrationRegistration ReadPendingRegistration(
            RegistryKey key)
        {
            DemandPrivatePendingRegistryKey(key);
            if (key.ValueCount != 2
                || !TryReadRegistryDword(
                    key,
                    MigrationPendingSchemaValue,
                    out int schema)
                || schema != MigrationPendingSchema
                || !TryReadLiteralRegistryString(
                    key,
                    "InstallLocation",
                    out string? installLocation)
                || string.IsNullOrWhiteSpace(installLocation)
                || !IsCanonicalAbsolutePath(installLocation, isDirectory: true))
            {
                throw new InvalidDataException(
                    "Le snapshot de migration WotLK est invalide.");
            }

            string canonicalRoot = NormalizeAndValidateGameRoot(installLocation);
            string uninstallerPath = Path.Combine(canonicalRoot, UninstallerFileName);
            string uninstall = Quote(uninstallerPath) + " /uninstall-game";
            return new GameUninstallMigrationRegistration(
                AppDisplayName,
                GetProductVersion(),
                canonicalRoot,
                uninstall,
                uninstall + " /quiet",
                EstimatedSizeKb: 0,
                UsesOnlyLiteralBoundedValues: true,
                IsPendingSnapshot: true);
        }

        public bool TryNeutralize(GameUninstallMigrationRegistration expected)
        {
            using RegistryKey baseKey = OpenUninstallBaseKey();
            if (expected.IsPendingSnapshot)
            {
                using RegistryKey? pending = baseKey.OpenSubKey(
                    MigrationPendingRegistrySubKey,
                    writable: false);
                if (pending is null)
                {
                    return false;
                }

                try
                {
                    return ReadPendingRegistration(pending).Equals(expected);
                }
                catch
                {
                    TryDeletePendingSnapshot(baseKey);
                    return false;
                }
            }

            using (RegistryKey? active = baseKey.OpenSubKey(
                       RegistrySubKey,
                       writable: false))
            {
                if (active is null
                    || !ReadActiveRegistration(active).Equals(expected))
                {
                    return false;
                }
            }

            // Persist only the canonical root in a private internal key. The
            // executable path and commands are reconstructed after the final
            // launcher binary and the on-disk ownership proof are revalidated.
            bool snapshotWritten = TryWritePendingSnapshot(
                baseKey,
                expected.InstallLocation!);
            using (RegistryKey? active = baseKey.OpenSubKey(
                       RegistrySubKey,
                       writable: false))
            {
                if (active is null
                    || !ReadActiveRegistration(active).Equals(expected))
                {
                    if (snapshotWritten)
                    {
                        TryDeletePendingSnapshot(baseKey);
                    }

                    return false;
                }
            }

            baseKey.DeleteSubKeyTree(
                RegistrySubKey,
                throwOnMissingSubKey: false);
            return true;
        }

        public void Register(
            GameUninstallMigrationRegistration previous,
            GameUninstallIdentity identity)
        {
            using RegistryKey baseKey = OpenUninstallBaseKey();
            bool hasPendingSnapshot = false;
            using (RegistryKey? pending = baseKey.OpenSubKey(
                       MigrationPendingRegistrySubKey,
                       writable: false))
            {
                if (pending is not null)
                {
                    GameUninstallMigrationRegistration snapshot =
                        ReadPendingRegistration(pending);
                    if (snapshot.InstallLocation is null
                        || !SamePath(snapshot.InstallLocation, identity.InstallRoot)
                        || previous.IsPendingSnapshot && !snapshot.Equals(previous))
                    {
                        throw new IOException(
                            "Le snapshot WotLK a changé pendant la migration.");
                    }

                    hasPendingSnapshot = true;
                }
                else if (previous.IsPendingSnapshot)
                {
                    throw new IOException(
                        "Le snapshot WotLK a disparu pendant la migration.");
                }
            }

            using (RegistryKey? existing = baseKey.OpenSubKey(
                       RegistrySubKey,
                       writable: false))
            {
                if (existing is not null)
                {
                    throw new IOException(
                        "L'entrée Windows WotLK a changé pendant la migration.");
                }
            }

            string nonce = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            const string nonceName = "AtlasMigrationNonce";
            RegistryKey? key = null;
            try
            {
                key = baseKey.CreateSubKey(RegistrySubKey)
                    ?? throw new InvalidOperationException(
                        "Impossible de recréer l'entrée Windows de désinstallation WotLK.");
                key.SetValue(nonceName, nonce, RegistryValueKind.String);
                string uninstallCommand = Quote(identity.UninstallerPath)
                    + " /uninstall-game";
                key.SetValue(
                    "DisplayVersion",
                    previous.DisplayVersion ?? GetProductVersion(),
                    RegistryValueKind.String);
                key.SetValue("Publisher", Publisher, RegistryValueKind.String);
                key.SetValue(
                    "InstallLocation",
                    identity.InstallRoot,
                    RegistryValueKind.String);
                string wowExecutable = GetGameExecutablePath(identity.InstallRoot);
                key.SetValue(
                    "DisplayIcon",
                    File.Exists(wowExecutable)
                        ? wowExecutable
                        : identity.UninstallerPath,
                    RegistryValueKind.String);
                key.SetValue(
                    "UninstallString",
                    uninstallCommand,
                    RegistryValueKind.String);
                key.SetValue(
                    "QuietUninstallString",
                    uninstallCommand + " /quiet",
                    RegistryValueKind.String);
                key.SetValue(
                    "InstallDate",
                    DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    RegistryValueKind.String);
                key.SetValue(
                    "EstimatedSize",
                    previous.EstimatedSizeKb,
                    RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                if (hasPendingSnapshot)
                {
                    baseKey.DeleteSubKeyTree(
                        MigrationPendingRegistrySubKey,
                        throwOnMissingSubKey: false);
                    using RegistryKey? remaining = baseKey.OpenSubKey(
                        MigrationPendingRegistrySubKey,
                        writable: false);
                    if (remaining is not null)
                    {
                        throw new IOException(
                            "Le snapshot WotLK n'a pas pu être supprimé.");
                    }
                }

                key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
                key.DeleteValue(nonceName, throwOnMissingValue: true);
            }
            catch
            {
                key?.Dispose();
                key = null;
                using RegistryKey? partial = baseKey.OpenSubKey(
                    RegistrySubKey,
                    writable: false);
                string? currentNonce = partial is not null
                    && TryReadLiteralRegistryString(
                        partial,
                        nonceName,
                        out string? readNonce)
                            ? readNonce
                            : null;
                if (string.Equals(currentNonce, nonce, StringComparison.Ordinal))
                {
                    baseKey.DeleteSubKeyTree(
                        RegistrySubKey,
                        throwOnMissingSubKey: false);
                }

                throw;
            }
            finally
            {
                key?.Dispose();
            }
        }

        private static bool TryWritePendingSnapshot(
            RegistryKey baseKey,
            string installRoot)
        {
            try
            {
                string canonicalRoot = NormalizeAndValidateGameRoot(installRoot);
                TryDeletePendingSnapshot(baseKey);
                RegistrySecurity security = CreatePrivatePendingRegistrySecurity();
                using RegistryKey key = baseKey.CreateSubKey(
                    MigrationPendingRegistrySubKey,
                    RegistryKeyPermissionCheck.ReadWriteSubTree,
                    RegistryOptions.None,
                    security)
                    ?? throw new IOException(
                        "Impossible de créer le snapshot WotLK privé.");
                key.SetValue(
                    "InstallLocation",
                    canonicalRoot,
                    RegistryValueKind.String);
                key.SetValue(
                    MigrationPendingSchemaValue,
                    MigrationPendingSchema,
                    RegistryValueKind.DWord);
                DemandPrivatePendingRegistryKey(key);
                return ReadPendingRegistration(key).InstallLocation is string writtenRoot
                    && SamePath(writtenRoot, canonicalRoot);
            }
            catch
            {
                TryDeletePendingSnapshot(baseKey);
                return false;
            }
        }

        private static void TryDeletePendingSnapshot(RegistryKey baseKey)
        {
            try
            {
                baseKey.DeleteSubKeyTree(
                    MigrationPendingRegistrySubKey,
                    throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }

        private static RegistrySecurity CreatePrivatePendingRegistrySecurity()
        {
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException(
                    "Identité Windows introuvable pour le snapshot WotLK.");
            RegistrySecurity security = new();
            security.SetAccessRuleProtection(
                isProtected: true,
                preserveInheritance: false);
            security.SetOwner(currentUser);
            foreach (SecurityIdentifier trustee in new[]
                     {
                         currentUser,
                         new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                         new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                     })
            {
                security.AddAccessRule(new RegistryAccessRule(
                    trustee,
                    RegistryRights.FullControl,
                    InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            return security;
        }

        private static void DemandPrivatePendingRegistryKey(RegistryKey key)
        {
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new UnauthorizedAccessException(
                    "Identité Windows introuvable pour le snapshot WotLK.");
            SecurityIdentifier system = new(
                WellKnownSidType.LocalSystemSid,
                null);
            SecurityIdentifier administrators = new(
                WellKnownSidType.BuiltinAdministratorsSid,
                null);
            RegistrySecurity security = key.GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected
                || security.GetOwner(typeof(SecurityIdentifier))
                    is not SecurityIdentifier owner
                || !owner.Equals(currentUser))
            {
                throw new UnauthorizedAccessException(
                    "Le snapshot WotLK n'est pas privé.");
            }

            RegistryRights currentUserRights = 0;
            foreach (RegistryAccessRule rule in security.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: false,
                         typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference is not SecurityIdentifier sid
                    || rule.IsInherited
                    || rule.AccessControlType != AccessControlType.Allow
                    || !(sid.Equals(currentUser)
                         || sid.Equals(system)
                         || sid.Equals(administrators)))
                {
                    throw new UnauthorizedAccessException(
                        "Le snapshot WotLK autorise un principal inattendu.");
                }

                if (sid.Equals(currentUser))
                {
                    currentUserRights |= rule.RegistryRights;
                }
            }

            if ((currentUserRights & RegistryRights.FullControl)
                != RegistryRights.FullControl)
            {
                throw new UnauthorizedAccessException(
                    "Le snapshot WotLK n'est pas contrôlé par l'utilisateur courant.");
            }
        }

        private static bool TryReadLiteralRegistryString(
            RegistryKey key,
            string name,
            out string? value)
        {
            value = null;
            try
            {
                uint byteCount = 0;
                int query = RegQueryValueExW(
                    key.Handle,
                    name,
                    IntPtr.Zero,
                    out uint type,
                    IntPtr.Zero,
                    ref byteCount);
                uint maximumBytes = checked((uint)
                    ((MaximumMigrationRegistryStringCharacters + 1)
                     * sizeof(char)));
                if (query != ErrorSuccess
                    || type != RegistryStringType
                    || byteCount < sizeof(char)
                    || byteCount > maximumBytes
                    || byteCount % sizeof(char) != 0)
                {
                    return false;
                }

                byte[] data = new byte[checked((int)byteCount)];
                uint actualBytes = byteCount;
                query = RegQueryValueExWBytes(
                    key.Handle,
                    name,
                    IntPtr.Zero,
                    out type,
                    data,
                    ref actualBytes);
                if (query != ErrorSuccess
                    || type != RegistryStringType
                    || actualBytes < sizeof(char)
                    || actualBytes > data.Length
                    || actualBytes % sizeof(char) != 0)
                {
                    return false;
                }

                string decoded = Encoding.Unicode.GetString(
                    data,
                    0,
                    checked((int)actualBytes));
                if (decoded.Length == 0
                    || decoded[^1] != '\0'
                    || decoded.AsSpan(0, decoded.Length - 1).Contains('\0'))
                {
                    return false;
                }

                decoded = decoded[..^1];
                if (decoded.Length > MaximumMigrationRegistryStringCharacters)
                {
                    return false;
                }

                value = decoded;
                return true;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or OverflowException)
            {
                return false;
            }
        }

        private static bool TryReadRegistryDword(
            RegistryKey key,
            string name,
            out int value)
        {
            value = 0;
            try
            {
                uint byteCount = sizeof(int);
                byte[] data = new byte[sizeof(int)];
                int query = RegQueryValueExWBytes(
                    key.Handle,
                    name,
                    IntPtr.Zero,
                    out uint type,
                    data,
                    ref byteCount);
                if (query != ErrorSuccess
                    || type != RegistryDwordType
                    || byteCount != sizeof(int))
                {
                    return false;
                }

                value = BitConverter.ToInt32(data, 0);
                return true;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(
        SafeRegistryHandle key,
        string valueName,
        IntPtr reserved,
        out uint type,
        IntPtr data,
        ref uint dataSize);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "RegQueryValueExW",
        CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExWBytes(
        SafeRegistryHandle key,
        string valueName,
        IntPtr reserved,
        out uint type,
        [Out] byte[] data,
        ref uint dataSize);
}

internal interface IGameUninstallMigrationRegistryStore
{
    GameUninstallMigrationRegistration? Read();

    bool TryNeutralize(GameUninstallMigrationRegistration expected);

    void Register(
        GameUninstallMigrationRegistration previous,
        GameUninstallIdentity identity);
}

internal sealed record GameUninstallMigrationRegistration(
    string? DisplayName,
    string? DisplayVersion,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    int EstimatedSizeKb,
    bool UsesOnlyLiteralBoundedValues,
    bool IsPendingSnapshot = false);

internal enum GameUninstallMigrationStatus
{
    NoRegistration,
    ForeignRegistrationPreserved,
    Current,
    Migrated,
    Neutralized
}

internal sealed record GameUninstallMigrationResult(
    GameUninstallMigrationStatus Status,
    string DiagnosticCode);

internal enum GameUninstallMigrationFaultPoint
{
    AfterNeutralized,
    AfterCandidateCopied,
    BeforeAtomicReplace
}
