using System.IO;
using System.Text.Json;

namespace WotLK.Launcher;

public sealed class LauncherSettings
{
    public string InstallPath { get; set; } = GetDefaultInstallPath();

    public string ManifestUrl { get; set; } = GetDefaultManifestUrl();

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

        settings.InstallPath = GetDefaultInstallPath();
        settings.ManifestUrl = GetDefaultManifestUrl();
        return settings;
    }

    public void Save()
    {
        InstallPath = GetDefaultInstallPath();
        ManifestUrl = GetDefaultManifestUrl();
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
}
