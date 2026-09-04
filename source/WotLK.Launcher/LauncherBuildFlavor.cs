using System.IO;

namespace WotLK.Launcher;

internal static class LauncherBuildFlavor
{
#if ATLAS_LOCAL_CLIENT
    internal const bool IsLocalClient = true;
    internal const bool IsSelfUpdateEnabled = false;
    internal const string SettingsDirectoryName = "Atlas Launcher Local";
#else
    internal const bool IsLocalClient = false;
    internal const bool IsSelfUpdateEnabled = true;
    internal const string SettingsDirectoryName = "WotLK Launcher";
#endif

    internal static string FormatVersion(Version? version)
    {
        string value = "v" + (version?.ToString(3) ?? "0.0.0");
#if ATLAS_LOCAL_CLIENT
        return value + "-local";
#else
        return value;
#endif
    }

    internal static string GetAvatarCacheRoot()
    {
#if ATLAS_LOCAL_CLIENT
        return Path.Combine(LauncherSettings.SettingsDirectory, "cache", "avatars");
#else
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Atlas Launcher",
            "cache",
            "avatars");
#endif
    }
}
