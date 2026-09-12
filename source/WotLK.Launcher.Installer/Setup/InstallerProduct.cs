using System.Text.Json;
using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal static class InstallerProduct
{
    internal const string Name = "Atlas Launcher";
    internal const string Version = "1.7.1";
    internal const string Publisher = "AnimeClub";
    internal const string LauncherFileName = "WotLK.Launcher.exe";
    internal const string UninstallerFileName = "Uninstall.exe";
    internal const string InstallStateFileName = ".atlas-install.json";
    internal const string RegistryKeyName = "AtlasLauncher";
    internal const string RegistryRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string RegistrySubKey = RegistryRoot + @"\" + RegistryKeyName;
    internal static string PayloadSha256 => InstallerPayloadBuildMetadata.Sha256;
    internal const long FreeSpaceMargin = 64L * 1024 * 1024;
    internal const int MaximumLegacySettingsBytes = 64 * 1024;

    internal static readonly string[] LegacyRegistrySubKeys =
    [
        RegistryRoot + @"\WotLK.Launcher",
        RegistryRoot + @"\AnimaClub.WotLK.Launcher"
    ];

    internal static string GetDefaultInstallPath()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            throw new InvalidOperationException("Windows n'a pas fourni le dossier Program Files.");
        }

        return Path.Combine(programFiles, Name);
    }

    internal static string GetDesktopShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        Name + ".lnk");

    internal static string GetStartMenuShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Name,
        Name + ".lnk");

    internal static IReadOnlyList<string> DiscoverWoWInstallRoots() =>
        DiscoverWoWInstallRoots(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData));

    internal static IReadOnlyList<string> DiscoverWoWInstallRoots(
        string localApplicationDataRoot)
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "WotLK")
        };

        foreach (string productDirectory in new[] { "WotLK Launcher", Name })
        {
            string settingsPath = Path.Combine(
                localApplicationDataRoot,
                productDirectory,
                "settings.json");
            try
            {
                using FileStream stream = new(
                    settingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                if (stream.Length <= 0
                    || stream.Length > MaximumLegacySettingsBytes)
                {
                    continue;
                }

                byte[] bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                using JsonDocument document = JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 8
                    });
                if (document.RootElement.TryGetProperty("InstallPath", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString())
                    && value.GetString()!.Length <= 4096)
                {
                    roots.Add(Path.GetFullPath(value.GetString()!));
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or FileNotFoundException
                or DirectoryNotFoundException
                or JsonException
                or ArgumentException
                or NotSupportedException)
            {
                // A malformed legacy setting must not prevent setup from starting.
            }
        }

        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).ToArray();
    }
}

internal sealed record InstallerEnvironment(
    string DefaultInstallPath,
    string DesktopShortcutPath,
    string StartMenuShortcutPath,
    string RegistrySubKey,
    IReadOnlyList<string> DetectionRegistrySubKeys,
    string SetupExecutablePath,
    string LogPath,
    string WindowsDirectory,
    IReadOnlyList<string> WoWInstallRoots,
    bool IsTest,
    IReadOnlyList<string> AllowedTestInstallRoots)
{
    internal static InstallerEnvironment CreateProduction(string? setupExecutablePath = null)
    {
        string setupPath = setupExecutablePath
            ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Impossible de localiser l'exécutable Atlas Launcher.");

        return new InstallerEnvironment(
            InstallerProduct.GetDefaultInstallPath(),
            InstallerProduct.GetDesktopShortcutPath(),
            InstallerProduct.GetStartMenuShortcutPath(),
            InstallerProduct.RegistrySubKey,
            [InstallerProduct.RegistrySubKey, .. InstallerProduct.LegacyRegistrySubKeys],
            Path.GetFullPath(setupPath),
            string.Empty,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            InstallerProduct.DiscoverWoWInstallRoots(),
            IsTest: false,
            AllowedTestInstallRoots: Array.Empty<string>());
    }

    internal void DemandAllowedDestination(string destination)
    {
        if (!IsTest)
        {
            string expected = InstallerProduct.GetDefaultInstallPath();
            if (!SamePath(DefaultInstallPath, expected)
                || !SamePath(destination, expected))
            {
                throw new InvalidOperationException(
                    "L'installation par machine est limitée au dossier Atlas Launcher de Program Files.");
            }

            return;
        }

        string fullDestination = Normalize(destination);
        bool allowed = AllowedTestInstallRoots.Any(root => IsSameOrChild(fullDestination, Normalize(root)));
        if (!allowed
            || !RegistrySubKey.Contains("AtlasLauncher.04D2.Test.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Le garde-fou 04D.2 a refusé une écriture hors de l'installation de test.");
        }
    }

    internal static bool IsSameOrChild(string candidate, string root)
    {
        string normalizedCandidate = Normalize(candidate);
        string normalizedRoot = Normalize(root);
        return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SamePath(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

internal sealed record AtlasInstallState(
    int SchemaVersion,
    string ProductVersion,
    string InstallLocation,
    string LauncherPath,
    string UninstallerPath,
    bool DesktopShortcutCreated,
    string DesktopShortcutPath,
    bool StartMenuShortcutCreated,
    string StartMenuShortcutPath,
    string RegistrySubKey,
    DateTimeOffset InstalledAtUtc,
    bool IsTestInstallation = false);
