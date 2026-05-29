using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;

namespace WotLK.Launcher.Installer;

internal static class InstallerServices
{
    internal const string AppDisplayName = "WotLK Launcher";
    internal const string LauncherFileName = "WotLK Launcher.exe";
    internal const string UninstallerFileName = "WotLK Launcher Uninstaller.exe";

    private const string Publisher = "WotLK";
    private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WotLK.Launcher";

    private static string LegacyRegistrySubKey => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" +
        Encoding.UTF8.GetString(Convert.FromBase64String("QW5pbWFDbHViLldvdExLLkxhdW5jaGVy"));

    internal static bool IsUninstallMode(IEnumerable<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/remove", StringComparison.OrdinalIgnoreCase));
    }

    internal static int RunUninstall(string[] args)
    {
        var quiet = args.Any(arg =>
            string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase));

        if (!quiet)
        {
            var confirmation = MessageBox.Show(
                "Désinstaller WotLK Launcher de cet ordinateur ?",
                "Désinstaller WotLK Launcher",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmation != MessageBoxResult.Yes)
            {
                return 0;
            }
        }

        try
        {
            Uninstall();

            if (!quiet)
            {
                MessageBox.Show(
                    "WotLK Launcher a été désinstallé.",
                    "Désinstallation terminée",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                MessageBox.Show(
                    ex.Message,
                    "Erreur de désinstallation",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return 1;
        }
    }

    internal static string GetInstallRoot()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        return Path.Combine(programFilesX86, "WotLK Launcher");
    }

    internal static string GetGameInstallRoot()
    {
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(programFilesX86))
        {
            programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }

        return Path.Combine(programFilesX86, "WotLK");
    }

    internal static string GetRegisteredInstallRoot()
    {
        using var baseKey = OpenUninstallBaseKey();
        var installLocation = ReadInstallLocation(baseKey, RegistrySubKey) ?? ReadInstallLocation(baseKey, LegacyRegistrySubKey);

        return string.IsNullOrWhiteSpace(installLocation)
            ? GetInstallRoot()
            : installLocation;
    }

    internal static string GetLauncherPath(string installRoot)
    {
        return Path.Combine(installRoot, LauncherFileName);
    }

    internal static string GetUninstallerPath(string installRoot)
    {
        return Path.Combine(installRoot, UninstallerFileName);
    }

    internal static string CopySelfAsUninstaller(string installRoot)
    {
        var sourceExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExe) || !File.Exists(sourceExe))
        {
            throw new InvalidOperationException("Impossible de retrouver l'exécutable de l'installer.");
        }

        var uninstallerExe = GetUninstallerPath(installRoot);
        if (SamePath(sourceExe, uninstallerExe))
        {
            return uninstallerExe;
        }

        var tempUninstaller = uninstallerExe + ".tmp";
        File.Copy(sourceExe, tempUninstaller, overwrite: true);

        if (File.Exists(uninstallerExe))
        {
            DeleteFileIfExistsWithRetry(uninstallerExe);
        }

        File.Move(tempUninstaller, uninstallerExe);
        return uninstallerExe;
    }

    internal static void RegisterInstalledApp(string installRoot, string launcherExe, string uninstallerExe)
    {
        using var baseKey = OpenUninstallBaseKey();
        baseKey.DeleteSubKeyTree(LegacyRegistrySubKey, throwOnMissingSubKey: false);
        using var key = baseKey.CreateSubKey(RegistrySubKey)
            ?? throw new InvalidOperationException("Impossible de créer l'entrée Windows de désinstallation.");

        var uninstallCommand = Quote(uninstallerExe) + " /uninstall";
        var quietUninstallCommand = Quote(uninstallerExe) + " /uninstall /quiet";

        key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", GetProductVersion(), RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", launcherExe, RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", quietUninstallCommand, RegistryValueKind.String);
        key.SetValue("EstimatedSize", EstimateDirectorySizeKb(installRoot), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    internal static void UnregisterInstalledApp()
    {
        using var baseKey = OpenUninstallBaseKey();
        baseKey.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false);
        baseKey.DeleteSubKeyTree(LegacyRegistrySubKey, throwOnMissingSubKey: false);
    }

    internal static void WriteInstallMarker(string installRoot, string launcherExe, string uninstallerExe)
    {
        var marker = Path.Combine(installRoot, "install.json");
        var json = $$"""
        {
          "installedAt": "{{DateTimeOffset.Now:O}}",
          "launcher": "{{EscapeJsonPath(launcherExe)}}",
          "uninstaller": "{{EscapeJsonPath(uninstallerExe)}}",
          "registeredApp": "{{AppDisplayName}}"
        }
        """;
        File.WriteAllText(marker, json);
    }

    internal static void SanitizeLauncherSettings()
    {
        var settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WotLK Launcher");
        var settingsPath = Path.Combine(settingsDirectory, "settings.json");
        var manifestUrl = "http://152.228.225.7/wotlk/manifest.json";

        if (File.Exists(settingsPath))
        {
            try
            {
                var existing = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
                var existingManifestUrl = existing?["ManifestUrl"]?.GetValue<string>();
                if (TryKeepExistingManifestUrl(existingManifestUrl, out var safeManifestUrl))
                {
                    manifestUrl = safeManifestUrl;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        Directory.CreateDirectory(settingsDirectory);
        var sanitized = new JsonObject
        {
            ["InstallPath"] = GetGameInstallRoot(),
            ["ManifestUrl"] = manifestUrl
        };
        var json = sanitized.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(settingsPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string? ReadInstallLocation(RegistryKey baseKey, string subKey)
    {
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        return key?.GetValue("InstallLocation") as string;
    }

    private static bool TryKeepExistingManifestUrl(string? value, out string manifestUrl)
    {
        manifestUrl = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "152.228.225.7", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!uri.AbsolutePath.EndsWith("/wotlk/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        manifestUrl = uri.ToString();
        return true;
    }

    internal static void CreateStartMenuShortcut(string targetExe, string workingDirectory)
    {
        var shortcutPath = GetStartMenuShortcutPath();
        CreateShortcut(shortcutPath, targetExe, workingDirectory);
    }

    internal static void CreateDesktopShortcut(string targetExe, string workingDirectory)
    {
        var shortcutPath = GetDesktopShortcutPath();
        CreateShortcut(shortcutPath, targetExe, workingDirectory);
    }

    private static void Uninstall()
    {
        var installRoot = Path.GetFullPath(GetRegisteredInstallRoot());
        var launcherExe = GetLauncherPath(installRoot);
        var uninstallerExe = GetUninstallerPath(installRoot);
        var marker = Path.Combine(installRoot, "install.json");

        StopRunningLauncher(launcherExe);

        DeleteFileIfExists(GetStartMenuShortcutPath());
        DeleteFileIfExists(GetDesktopShortcutPath());
        DeleteFileIfExistsWithRetry(launcherExe);
        DeleteFileIfExists(marker);

        UnregisterInstalledApp();

        var currentExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentExe) && SamePath(currentExe, uninstallerExe))
        {
            ScheduleSelfDelete(currentExe, installRoot);
        }
        else
        {
            DeleteFileIfExistsWithRetry(uninstallerExe);
            DeleteDirectoryIfEmpty(installRoot);
        }
    }

    private static RegistryKey OpenUninstallBaseKey()
    {
        var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
        return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
    }

    private static string GetProductVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static string GetStartMenuShortcutPath()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        return Path.Combine(programs, "Programs", "WotLK Launcher.lnk");
    }

    private static string GetDesktopShortcutPath()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktop, "WotLK Launcher.lnk");
    }

    private static void CreateShortcut(string shortcutPath, string targetExe, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell indisponible pour créer le raccourci.");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("Impossible de créer WScript.Shell.");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetExe;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = targetExe;
        shortcut.Description = AppDisplayName;
        shortcut.Save();
    }

    private static int EstimateDirectorySizeKb(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            return 0;
        }

        var bytes = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

        return (int)Math.Min(int.MaxValue, Math.Max(1, bytes / 1024));
    }

    private static void StopRunningLauncher(string launcherExe)
    {
        var processName = Path.GetFileNameWithoutExtension(launcherExe);
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || !ProcessMatchesPath(process, launcherExe))
                    {
                        continue;
                    }

                    if (process.CloseMainWindow() && process.WaitForExit(5000))
                    {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                }
                catch
                {
                    // If the process exits while we inspect it, the retrying delete below is enough.
                }
            }
        }
    }

    private static bool ProcessMatchesPath(Process process, string expectedPath)
    {
        try
        {
            var modulePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(modulePath) && SamePath(modulePath, expectedPath);
        }
        catch
        {
            return true;
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void DeleteFileIfExistsWithRetry(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                DeleteFileIfExists(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(500);
            }
        }

        throw new IOException("Impossible de supprimer le fichier après plusieurs essais: " + path, lastError);
    }

    private static void DeleteDirectoryIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static void ScheduleSelfDelete(string currentExe, string installRoot)
    {
        var quotedExe = QuoteForCmd(currentExe);
        var quotedInstallRoot = QuoteForCmd(installRoot);
        var command = "/C for /L %i in (1,1,300) do @(" +
                      "del /F /Q " + quotedExe + " >NUL 2>NUL && " +
                      "(rmdir " + quotedInstallRoot + " >NUL 2>NUL & exit /B 0) & " +
                      "timeout /T 1 /NOBREAK >NUL" +
                      ")";

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = command,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false
        });
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value)
    {
        return "\"" + value + "\"";
    }

    private static string QuoteForCmd(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string EscapeJsonPath(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
