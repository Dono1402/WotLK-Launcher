using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotLK.Launcher;

public sealed class LauncherSettings
{
    private static readonly object StorageLock = new();
    [JsonIgnore]
    internal string? RecoveryNotice { get; private set; }
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

    public static LauncherSettings Load() => LoadFrom(SettingsPath);

    internal static LauncherSettings LoadFrom(string path)
    {
        lock (StorageLock)
        {
            LauncherSettings settings;
            try
            {
                settings = Read(path) ?? (File.Exists(path + ".bak")
                    ? throw new JsonException("Primary settings are missing.") : new LauncherSettings());
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                settings = Read(path + ".bak") ?? throw new IOException(
                    "Les réglages sont illisibles et aucune sauvegarde valide n’est disponible.", error);
                settings.RecoveryNotice = "Les réglages ont été récupérés depuis leur sauvegarde locale. Vérifiez vos préférences.";
            }
            settings.Normalize();
            return settings;
        }
    }

    private static LauncherSettings? Read(string path) => !File.Exists(path) ? null
        : JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(path))
            ?? throw new JsonException("Settings must contain an object.");

    private void Normalize()
    {
        InstallPath = NormalizeInstallPath(InstallPath);
        ManifestUrl = GetDefaultManifestUrl();
        GameLocale = NormalizeGameLocale(GameLocale);
        InterfaceLocale = NormalizeInterfaceLocale(InterfaceLocale);
    }

    public void Save() => SaveTo(SettingsPath);

    internal void SaveTo(string path)
    {
        lock (StorageLock)
        {
            Normalize();
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, this, new JsonSerializerOptions { WriteIndented = true });
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path))
                {
                    bool valid;
                    try { valid = Read(path) is not null; }
                    catch (JsonException) { valid = false; }
                    // Preserve damaged bytes separately; never rotate them over the last good backup.
                    string backup = valid ? path + ".bak" : path + ".corrupt-" + Guid.NewGuid().ToString("N");
                    File.Replace(temporary, path, backup);
                }
                else
                {
                    if (!File.Exists(path + ".bak")) File.Copy(temporary, path + ".bak");
                    File.Move(temporary, path);
                }
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
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
