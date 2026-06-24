using System.IO;
using System.Text.Json;

namespace WotLK.Launcher;

public sealed class LauncherSettings
{
    public string InstallPath { get; set; } = GetDefaultInstallPath();

    public string ManifestUrl { get; set; } = GetDefaultManifestUrl();

    public string GameLocale { get; set; } = GetDefaultGameLocale();

    public static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WotLK Launcher");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

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
        return settings;
    }

    public void Save()
    {
        InstallPath = NormalizeInstallPath(InstallPath);
        ManifestUrl = GetDefaultManifestUrl();
        GameLocale = NormalizeGameLocale(GameLocale);
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static string GetDefaultManifestUrl()
    {
        return "http://152.228.225.7/wotlk/manifest.json";
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
}
