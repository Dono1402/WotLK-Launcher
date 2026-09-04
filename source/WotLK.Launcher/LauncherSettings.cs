using System.IO;
using System.Text.Json;

namespace WotLK.Launcher;

public sealed class LauncherSettings
{
    public string InstallPath { get; set; } = GetDefaultInstallPath();

    public string ManifestUrl { get; set; } = GetDefaultManifestUrl();

    public string GameLocale { get; set; } = GetDefaultGameLocale();

    public string InterfaceLocale { get; set; } = GetDefaultInterfaceLocale();

    public bool AutomaticLauncherUpdates { get; set; } = true;

    public bool CloseLauncherOnGameStart { get; set; }

    public bool StartWithWindows { get; set; }

    public bool MinimizeToTrayOnClose { get; set; } = true;

    public bool FriendPresenceNotifications { get; set; } = true;

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LauncherBuildFlavor.SettingsDirectoryName);

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static string LauncherLogPath => Path.Combine(SettingsDirectory, "launcher.log");

    public static LauncherSettings Load()
    {
        LauncherSettings settings;
        if (!File.Exists(SettingsPath))
        {
            settings = new LauncherSettings();
        }
        else
        {
            var json = File.ReadAllText(SettingsPath);
            settings = JsonSerializer.Deserialize<LauncherSettings>(json) ?? new LauncherSettings();
        }

        settings.InstallPath = NormalizeInstallPath(settings.InstallPath);
        settings.ManifestUrl = GetDefaultManifestUrl();
        settings.GameLocale = NormalizeGameLocale(settings.GameLocale);
        settings.InterfaceLocale = NormalizeInterfaceLocale(settings.InterfaceLocale);
        return settings;
    }

    public void Save()
    {
        InstallPath = NormalizeInstallPath(InstallPath);
        ManifestUrl = GetDefaultManifestUrl();
        GameLocale = NormalizeGameLocale(GameLocale);
        InterfaceLocale = NormalizeInterfaceLocale(InterfaceLocale);
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static string GetDefaultManifestUrl()
    {
        return "https://animeclub.fr/wotlk/manifest.json";
    }

    public static string GetDefaultInstallPath()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        return Path.Combine(programFilesX86, "WotLK");
    }

    public static string NormalizeInstallPath(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return GetDefaultInstallPath();
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(installPath.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GetDefaultInstallPath();
        }
    }

    public static string GetDefaultGameLocale()
    {
        return "frFR";
    }

    public static string NormalizeGameLocale(string? locale)
    {
        return string.Equals(locale, "enUS", StringComparison.OrdinalIgnoreCase) ? "enUS" : "frFR";
    }

    public static string GetDefaultInterfaceLocale() => "fr-FR";

    public static string NormalizeInterfaceLocale(string? locale)
    {
        return locale?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true
            ? "en-US"
            : "fr-FR";
    }
}
