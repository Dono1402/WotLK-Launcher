using System.IO;

namespace WotLK.Launcher.Installer.Setup;

internal static class InstallerUpdateResidueCleanup
{
    private static readonly HashSet<string> HelperFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "updater.exe", "helper-accepted.json", "committed.json"
    };

    internal static void RemoveOwnedFiles(string installRoot, bool isTest)
    {
        string root = Path.GetFullPath(installRoot);
        DemandDirectory(root, isTest);
        string helperRoot = Path.Combine(root, ".atlas-self-update");
        if (Directory.Exists(helperRoot))
        {
            DemandDirectory(helperRoot, isTest);
            foreach (string directory in Directory.EnumerateDirectories(helperRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
                DemandDirectory(directory, isTest);
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    if (IsHelperFile(Path.GetFileName(file))) DeleteOwnedFile(file);
                }
                DeleteEmpty(directory, isTest);
            }
            DeleteEmpty(helperRoot, isTest);
        }
        string prefix = InstallerProduct.LauncherFileName + ".atlas-";
        foreach (string file in Directory.EnumerateFiles(root, prefix + "*", SearchOption.TopDirectoryOnly))
        {
            string suffix = Path.GetFileName(file)[prefix.Length..];
            int separator = suffix.IndexOf('.');
            if (separator != 32 || !Guid.TryParseExact(suffix[..separator], "N", out _)) continue;
            if (suffix[separator..] is ".new" or ".backup") DeleteOwnedFile(file);
        }
    }

    private static bool IsHelperFile(string name)
    {
        if (HelperFiles.Contains(name)) return true;
        foreach (string signal in new[] { "helper-accepted.json", "committed.json" })
        {
            string prefix = "." + signal + ".";
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(name[prefix.Length..^4], "N", out _)) return true;
        }
        return false;
    }

    private static void DemandDirectory(string path, bool isTest)
    {
        InstallerPathValidator.DemandNoReparsePoints(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if (!isTest) InstallerProtectedPathSecurity.DemandTrustedDirectory(path);
    }

    private static void DeleteOwnedFile(string path)
    {
        InstallerPathValidator.DemandNoReparsePoints(path);
        File.Delete(path);
    }

    private static void DeleteEmpty(string path, bool isTest)
    {
        DemandDirectory(path, isTest);
        if (!Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path, recursive: false);
    }
}
