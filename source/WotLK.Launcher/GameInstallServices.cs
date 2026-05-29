using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Microsoft.Win32;

namespace WotLK.Launcher;

internal static class GameInstallServices
{
    internal const string AppDisplayName = "WotLK Client";
    internal const string UninstallerFileName = "WotLK Uninstaller.exe";

    private const string Publisher = "WotLK";
    private const string RegistrySubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WotLK.Client";
    private const string ClientMarkerFileName = "client-install.json";
    private const string VideoDefaultsMarkerFileName = "launcher-video-defaults.json";
    private const string RealmAddress = "152.228.225.7";
    private const int SystemMetricPrimaryScreenWidth = 0;
    private const int SystemMetricPrimaryScreenHeight = 1;

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
            var confirmation = MessageBox.Show("Desinstaller WotLK Client de cet ordinateur ?", "Desinstaller WotLK Client", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
                MessageBox.Show("WotLK Client a ete desinstalle.", "Desinstallation terminee", MessageBoxButton.OK, MessageBoxImage.Information);
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

    internal static string EnsureDefaultClientConfig(string installRoot, string locale)
    {
        var root = NormalizeAndValidateGameRoot(installRoot);
        var gameLocale = LauncherSettings.NormalizeGameLocale(locale);
        EnsureLocaleRealmlist(root, gameLocale);

        var wtfDirectory = Path.Combine(root, "WTF");
        Directory.CreateDirectory(wtfDirectory);

        var configPath = Path.Combine(wtfDirectory, "Config.wtf");
        var videoDefaultsMarkerPath = Path.Combine(wtfDirectory, VideoDefaultsMarkerFileName);
        var applyDesktopResolution = !File.Exists(configPath) || !File.Exists(videoDefaultsMarkerPath);
        var keptLines = new List<string>();
        if (File.Exists(configPath))
        {
            TrySetNormalAttributes(configPath);
            foreach (var line in File.ReadAllLines(configPath, Encoding.UTF8))
            {
                var key = TryReadConfigKey(line);
                if (key is not null && IsManagedClientConfigKey(key, applyDesktopResolution))
                {
                    continue;
                }

                keptLines.Add(line);
            }
        }

        if (keptLines.Count > 0 && !string.IsNullOrWhiteSpace(keptLines[^1]))
        {
            keptLines.Add(string.Empty);
        }

        keptLines.Add($"SET locale \"{gameLocale}\"");
        keptLines.Add($"SET installLocale \"{gameLocale}\"");

        var desktopResolution = applyDesktopResolution ? TryGetPrimaryDesktopResolution() : null;
        if (!string.IsNullOrWhiteSpace(desktopResolution))
        {
            keptLines.Add($"SET gxResolution \"{desktopResolution}\"");
        }

        keptLines.Add("SET gxWindow \"1\"");
        keptLines.Add("SET gxMaximize \"1\"");
        keptLines.Add("SET gxVSync \"0\"");

        File.WriteAllLines(configPath, keptLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (applyDesktopResolution)
        {
            WriteVideoDefaultsMarker(videoDefaultsMarkerPath, desktopResolution);
        }

        return configPath;
    }

    private static string? TryReadConfigKey(string line)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("SET ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = trimmed[4..].TrimStart();
        if (rest.Length == 0)
        {
            return null;
        }

        var end = rest.IndexOfAny([' ', '\t']);
        return end < 0 ? rest : rest[..end];
    }

    private static bool IsManagedClientConfigKey(string key, bool applyDesktopResolution)
    {
        return string.Equals(key, "locale", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "installLocale", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxWindow", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxMaximize", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxVSync", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "gxRefresh", StringComparison.OrdinalIgnoreCase) ||
               (applyDesktopResolution && string.Equals(key, "gxResolution", StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureLocaleRealmlist(string installRoot, string gameLocale)
    {
        var localeDataDirectory = Path.Combine(installRoot, "Data", gameLocale);
        Directory.CreateDirectory(localeDataDirectory);

        var realmlistPath = Path.Combine(localeDataDirectory, "realmlist.wtf");
        if (!File.Exists(realmlistPath))
        {
            File.WriteAllText(realmlistPath, "set realmlist " + RealmAddress + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private static void WriteVideoDefaultsMarker(string markerPath, string? desktopResolution)
    {
        var json = $$"""
        {
          "appliedAt": "{{DateTimeOffset.Now:O}}",
          "desktopResolution": "{{EscapeJson(desktopResolution ?? string.Empty)}}"
        }
        """;
        File.WriteAllText(markerPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string? TryGetPrimaryDesktopResolution()
    {
        try
        {
            var width = GetSystemMetrics(SystemMetricPrimaryScreenWidth);
            var height = GetSystemMetrics(SystemMetricPrimaryScreenHeight);
            return width > 0 && height > 0 ? $"{width}x{height}" : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    internal static void StopRunningGameProcesses(string installRoot)
    {
        var root = NormalizeAndValidateGameRoot(installRoot);
        StopRunningWow(root);
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

    private static void TrySetNormalAttributes(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
        }
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
