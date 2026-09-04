using System.Text.Json;
using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal static class InstallerProduct
{
    internal const string Name = "Atlas Launcher";
    internal const string Version = "1.2.0";
    internal const string Publisher = "AnimeClub";
    internal const string LauncherFileName = "WotLK.Launcher.exe";
    internal const string UninstallerFileName = "Uninstall.exe";
    internal const string InstallStateFileName = ".atlas-install.json";
    internal const string RegistryKeyName = "AtlasLauncher";
    internal const string RegistryRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    internal const string RegistrySubKey = RegistryRoot + @"\" + RegistryKeyName;
    internal static string PayloadSha256 => InstallerPayloadBuildMetadata.Sha256;
    internal const long FreeSpaceMargin = 64L * 1024 * 1024;

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

    internal static string GetLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Name,
        "Installer",
        "install.log");

    internal static string GetDesktopShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Name + ".lnk");

    internal static string GetStartMenuShortcutPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        Name,
        Name + ".lnk");

    internal static IReadOnlyList<string> DiscoverWoWInstallRoots()
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
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                productDirectory,
                "settings.json");
            try
            {
                if (!File.Exists(settingsPath))
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (document.RootElement.TryGetProperty("InstallPath", out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    roots.Add(Path.GetFullPath(value.GetString()!));
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
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
    internal static InstallerEnvironment CreateProduction()
    {
        string setupPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Impossible de localiser AtlasLauncherSetup.exe.");

        return new InstallerEnvironment(
            InstallerProduct.GetDefaultInstallPath(),
            InstallerProduct.GetDesktopShortcutPath(),
            InstallerProduct.GetStartMenuShortcutPath(),
            InstallerProduct.RegistrySubKey,
            [InstallerProduct.RegistrySubKey, .. InstallerProduct.LegacyRegistrySubKeys],
            Path.GetFullPath(setupPath),
            InstallerProduct.GetLogPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            InstallerProduct.DiscoverWoWInstallRoots(),
            IsTest: false,
            AllowedTestInstallRoots: Array.Empty<string>());
    }

    internal void DemandAllowedDestination(string destination)
    {
        if (!IsTest)
        {
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
    string InstallerLogPath,
    bool IsTestInstallation = false);
