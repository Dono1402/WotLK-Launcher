using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace WotLK.Launcher;

internal static class GameInstallServices
{
    internal const string AppDisplayName = "WotLK";
    internal const string UninstallerFileName = "WotLK Uninstaller.exe";

    private const string Publisher = "WotLK";
    private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WotLK.Client";
    private const string ClientMarkerFileName = "client-install.json";

    internal static bool IsGameUninstallMode(IEnumerable<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, "/uninstall-game", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--uninstall-game", StringComparison.OrdinalIgnoreCase));
    }

    internal static int RunGameUninstall(string[] args)
    {
        var quiet = args.Any(arg =>
            string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase));

        if (!quiet)
        {
            var confirmation = MessageBox.Show("Desinstaller le client WotLK de cet ordinateur ?", "Desinstaller WotLK", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return 0;
            }
        }

        try
        {
            UninstallGame();
            if (!quiet)
            {
                MessageBox.Show("WotLK a ete desinstalle.", "Desinstallation terminee", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                MessageBox.Show(ex.Message, "Erreur de desinstallation", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return 1;
        }
    }

    internal static string RegisterInstalledGame(string installRoot, string clientVersion)
    {
        var root = NormalizeAndValidateGameRoot(installRoot);
        Directory.CreateDirectory(root);
        var uninstallerExe = CopySelfAsGameUninstaller(root);
        WriteInstallMarker(root, clientVersion, uninstallerExe);
        RegisterInstalledApp(root, clientVersion, uninstallerExe);
        return uninstallerExe;
    }

    private static string CopySelfAsGameUninstaller(string installRoot)
    {
        var sourceExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(sourceExe) || !File.Exists(sourceExe))
        {
            throw new InvalidOperationException("Impossible de retrouver l'executable du launcher.");
        }

        var uninstallerExe = Path.Combine(installRoot, UninstallerFileName);
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

    private static void RegisterInstalledApp(string installRoot, string clientVersion, string uninstallerExe)
    {
        using var baseKey = OpenUninstallBaseKey();
        using var key = baseKey.CreateSubKey(RegistrySubKey) ?? throw new InvalidOperationException("Impossible de creer l'entree Windows de desinstallation WotLK.");
        var wowExe = Path.Combine(installRoot, "Wow.exe");
        var uninstallCommand = Quote(uninstallerExe) + " /uninstall-game";
        var quietUninstallCommand = Quote(uninstallerExe) + " /uninstall-game /quiet";

        key.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", string.IsNullOrWhiteSpace(clientVersion) ? GetProductVersion() : clientVersion, RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", File.Exists(wowExe) ? wowExe : uninstallerExe, RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", quietUninstallCommand, RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
        key.SetValue("EstimatedSize", EstimateDirectorySizeKb(installRoot), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void WriteInstallMarker(string installRoot, string clientVersion, string uninstallerExe)
    {
        var marker = Path.Combine(installRoot, ClientMarkerFileName);
        var json = $$"""
        {
          "installedAt": "{{DateTimeOffset.Now:O}}",
          "clientVersion": "{{EscapeJson(clientVersion)}}",
          "installRoot": "{{EscapeJson(installRoot)}}",
          "uninstaller": "{{EscapeJson(uninstallerExe)}}",
          "registeredApp": "{{AppDisplayName}}"
        }
        """;
        File.WriteAllText(marker, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void UninstallGame()
    {
        var installRoot = NormalizeAndValidateGameRoot(GetRegisteredInstallRoot());
        StopRunningWow(installRoot);
        UnregisterInstalledApp();
        var currentExe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(currentExe) && IsPathInside(installRoot, currentExe))
        {
            ScheduleDirectoryDelete(installRoot);
            return;
        }
        DeleteDirectoryWithRetry(installRoot);
    }

    private static string GetRegisteredInstallRoot()
    {
        using var baseKey = OpenUninstallBaseKey();
        using var key = baseKey.OpenSubKey(RegistrySubKey, writable: false);
        var installLocation = key?.GetValue("InstallLocation") as string;
        return string.IsNullOrWhiteSpace(installLocation) ? LauncherSettings.GetDefaultInstallPath() : installLocation;
    }

    private static void UnregisterInstalledApp()
    {
        using var baseKey = OpenUninstallBaseKey();
        baseKey.DeleteSubKeyTree(RegistrySubKey, throwOnMissingSubKey: false);
    }

    private static RegistryKey OpenUninstallBaseKey()
    {
        var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
        return RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
    }

    private static string NormalizeAndValidateGameRoot(string installRoot)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        var expected = Path.GetFullPath(LauncherSettings.GetDefaultInstallPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(root, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Dossier WotLK refuse pour securite: " + installRoot);
        }
        return root;
    }

    private static void StopRunningWow(string installRoot)
    {
        foreach (var process in Process.GetProcessesByName("Wow"))
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || !ProcessMatchesInstallRoot(process, installRoot))
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
                }
            }
        }
    }

    private static bool ProcessMatchesInstallRoot(Process process, string installRoot)
    {
        try
        {
            var modulePath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(modulePath) && IsPathInside(installRoot, modulePath);
        }
        catch
        {
            return true;
        }
    }

    private static void DeleteDirectoryWithRetry(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            return;
        }
        Exception? lastError = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            try
            {
                ClearFileAttributes(installRoot);
                Directory.Delete(installRoot, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(1000);
            }
        }
        throw new IOException("Impossible de supprimer le dossier WotLK apres plusieurs essais: " + installRoot, lastError);
    }

    private static void ClearFileAttributes(string installRoot)
    {
        foreach (var file in Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
    }

    private static void DeleteFileIfExistsWithRetry(string path)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(500);
            }
        }
        throw new IOException("Impossible de supprimer le fichier apres plusieurs essais: " + path, lastError);
    }

    private static void ScheduleDirectoryDelete(string installRoot)
    {
        var quotedInstallRoot = QuoteForCmd(installRoot);
        var command = "/C for /L %i in (1,1,300) do @(" +
                      "rmdir /S /Q " + quotedInstallRoot + " >NUL 2>NUL && exit /B 0 & " +
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

    private static int EstimateDirectorySizeKb(string installRoot)
    {
        if (!Directory.Exists(installRoot))
        {
            return 0;
        }
        var bytes = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        return (int)Math.Min(int.MaxValue, Math.Max(1, bytes / 1024));
    }

    private static bool IsPathInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProductVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private static string Quote(string value) => "\"" + value + "\"";
    private static string QuoteForCmd(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
