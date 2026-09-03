using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal enum InstallerPathError
{
    None,
    Invalid,
    Network,
    DriveRoot,
    ProtectedLocation,
    WowClient,
    ForeignFiles,
    Inaccessible,
    InsufficientSpace
}

internal sealed record InstallerPathValidationResult(
    bool IsValid,
    string? FullPath,
    InstallerPathError Error,
    string Message,
    long RequiredBytes,
    long? AvailableBytes)
{
    internal static InstallerPathValidationResult Invalid(
        InstallerPathError error,
        string message,
        long requiredBytes,
        long? availableBytes = null,
        string? fullPath = null) =>
        new(false, fullPath, error, message, requiredBytes, availableBytes);
}

internal interface IInstallerDriveSpace
{
    bool TryGetAvailableBytes(string fullPath, out long availableBytes, out DriveType driveType);
}

internal sealed class WindowsInstallerDriveSpace : IInstallerDriveSpace
{
    public bool TryGetAvailableBytes(string fullPath, out long availableBytes, out DriveType driveType)
    {
        availableBytes = 0;
        driveType = DriveType.Unknown;
        try
        {
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            DriveInfo drive = new(root);
            driveType = drive.DriveType;
            if (!drive.IsReady)
            {
                return false;
            }

            availableBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException)
        {
            return false;
        }
    }
}

internal interface IInstallerAccessProbe
{
    bool CanWrite(string fullPath);
}

internal sealed class WindowsInstallerAccessProbe : IInstallerAccessProbe
{
    public bool CanWrite(string fullPath)
    {
        string? probeDirectory = FindExistingDirectory(fullPath);
        if (probeDirectory is null)
        {
            return false;
        }

        string probe = Path.Combine(probeDirectory, $".atlas-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            TryDelete(probe);
            return false;
        }
    }

    private static string? FindExistingDirectory(string path)
    {
        string? candidate = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(candidate) && !Directory.Exists(candidate))
        {
            candidate = Path.GetDirectoryName(candidate);
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class InstallerPathValidator
{
    private readonly InstallerEnvironment _environment;
    private readonly IInstallerDriveSpace _driveSpace;
    private readonly IInstallerAccessProbe _accessProbe;

    internal InstallerPathValidator(
        InstallerEnvironment environment,
        IInstallerDriveSpace? driveSpace = null,
        IInstallerAccessProbe? accessProbe = null)
    {
        _environment = environment;
        _driveSpace = driveSpace ?? new WindowsInstallerDriveSpace();
        _accessProbe = accessProbe ?? new WindowsInstallerAccessProbe();
    }

    internal InstallerPathValidationResult Validate(string? path, long requiredBytes)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || !Path.IsPathFullyQualified(path))
        {
            return Invalid(InstallerPathError.Invalid, "Choisis un chemin local absolu.", requiredBytes);
        }

        string fullPath;
        try
        {
            fullPath = InstallerEnvironment.Normalize(path.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return Invalid(InstallerPathError.Invalid, "Ce chemin n'est pas valide sous Windows.", requiredBytes);
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"//", StringComparison.Ordinal))
        {
            return Invalid(
                InstallerPathError.Network,
                "Sélectionne un disque local. Les chemins réseau ne sont pas pris en charge.",
                requiredBytes,
                fullPath: fullPath);
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)
            || InstallerEnvironment.SamePath(fullPath, root))
        {
            return Invalid(
                InstallerPathError.DriveRoot,
                "La racine d'un disque ne peut pas servir de dossier d'installation.",
                requiredBytes,
                fullPath: fullPath);
        }

        if (IsProtectedLocation(fullPath))
        {
            return Invalid(
                InstallerPathError.ProtectedLocation,
                "Choisis un dossier dédié à Atlas Launcher, en dehors des dossiers système Windows.",
                requiredBytes,
                fullPath: fullPath);
        }

        if (IsWoWLocation(fullPath))
        {
            return Invalid(
                InstallerPathError.WowClient,
                "Atlas Launcher et le client WoW doivent être installés dans deux dossiers séparés.",
                requiredBytes,
                fullPath: fullPath);
        }

        bool containsFiles;
        try
        {
            containsFiles = Directory.Exists(fullPath)
                && Directory.EnumerateFileSystemEntries(fullPath).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Invalid(
                InstallerPathError.Inaccessible,
                "Windows refuse l'accès à cet emplacement. Choisis un autre dossier.",
                requiredBytes,
                fullPath: fullPath);
        }

        if (containsFiles)
        {
            return Invalid(
                InstallerPathError.ForeignFiles,
                "Ce dossier contient déjà des fichiers. Choisis un dossier vide dédié à Atlas Launcher.",
                requiredBytes,
                fullPath: fullPath);
        }

        if (!_driveSpace.TryGetAvailableBytes(fullPath, out long availableBytes, out DriveType driveType)
            || driveType == DriveType.Network)
        {
            return Invalid(
                InstallerPathError.Network,
                "Le disque sélectionné n'est pas un disque local disponible.",
                requiredBytes,
                fullPath: fullPath);
        }

        if (availableBytes < requiredBytes)
        {
            return Invalid(
                InstallerPathError.InsufficientSpace,
                $"Libère {FormatBytes(requiredBytes - availableBytes)} supplémentaires ou sélectionne un autre disque.",
                requiredBytes,
                availableBytes,
                fullPath);
        }

        if (!_accessProbe.CanWrite(fullPath))
        {
            return Invalid(
                InstallerPathError.Inaccessible,
                "Windows refuse l'accès à cet emplacement. Choisis un autre dossier.",
                requiredBytes,
                availableBytes,
                fullPath);
        }

        return new InstallerPathValidationResult(
            true,
            fullPath,
            InstallerPathError.None,
            string.Empty,
            requiredBytes,
            availableBytes);
    }

    internal static string FormatBytes(long bytes)
    {
        const double mega = 1024d * 1024d;
        const double giga = 1024d * 1024d * 1024d;
        return bytes >= giga
            ? $"{bytes / giga:0.#} Go"
            : $"{Math.Ceiling(bytes / mega):0} Mo";
    }

    private bool IsProtectedLocation(string fullPath)
    {
        string windows = InstallerEnvironment.Normalize(_environment.WindowsDirectory);
        if (InstallerEnvironment.IsSameOrChild(fullPath, windows))
        {
            return true;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return (!string.IsNullOrWhiteSpace(programFiles)
                && InstallerEnvironment.SamePath(fullPath, programFiles))
            || (!string.IsNullOrWhiteSpace(programFilesX86)
                && InstallerEnvironment.SamePath(fullPath, programFilesX86))
            || (!string.IsNullOrWhiteSpace(commonData)
                && InstallerEnvironment.SamePath(fullPath, commonData));
    }

    private bool IsWoWLocation(string fullPath)
    {
        foreach (string root in _environment.WoWInstallRoots)
        {
            try
            {
                if (InstallerEnvironment.IsSameOrChild(fullPath, root))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
            }
        }

        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(fullPath, "Wow.exe"))
            || Directory.Exists(Path.Combine(fullPath, "_classic_"))
            || (Directory.Exists(Path.Combine(fullPath, "Interface", "AddOns"))
                && Directory.Exists(Path.Combine(fullPath, "Data")));
    }

    private static InstallerPathValidationResult Invalid(
        InstallerPathError error,
        string message,
        long requiredBytes,
        long? availableBytes = null,
        string? fullPath = null) =>
        InstallerPathValidationResult.Invalid(
            error,
            message,
            requiredBytes,
            availableBytes,
            fullPath);
}
