using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WotLK.Launcher.Installer.Setup;

internal enum ExistingInstallationStatus
{
    None,
    Installed,
    StaleRegistration
}

internal sealed record ExistingInstallation(
    ExistingInstallationStatus Status,
    string? InstallLocation,
    string Message,
    string? RegistrySubKey);

internal sealed record InstalledApplicationRegistration(
    string RegistrySubKey,
    string InstallLocation,
    string LauncherPath,
    string UninstallerPath,
    long EstimatedSizeKiB);

internal interface IInstallerRegistry
{
    ExistingInstallation Detect(IReadOnlyList<string> registrySubKeys, IEnumerable<string> fallbackPaths);

    void Register(InstalledApplicationRegistration registration);

    void Unregister(string registrySubKey);

    IReadOnlyDictionary<string, object?> Read(string registrySubKey);
}

internal sealed class WindowsInstallerRegistry : IInstallerRegistry
{
    private static readonly string[] LauncherCandidates =
    [
        InstallerProduct.LauncherFileName,
        "WotLK Launcher.exe",
        "Atlas Launcher.exe",
        "AtlasLauncher.exe"
    ];

    private readonly InstallerLog _log;

    internal WindowsInstallerRegistry(InstallerLog log)
    {
        _log = log;
    }

    public ExistingInstallation Detect(
        IReadOnlyList<string> registrySubKeys,
        IEnumerable<string> fallbackPaths)
    {
        ExistingInstallation? stale = null;
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (string subKey in registrySubKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                using RegistryKey? key = machine.OpenSubKey(subKey, writable: false);
                if (key is null)
                {
                    continue;
                }

                string? installLocation = key.GetValue("InstallLocation") as string;
                string? displayIcon = NormalizeDisplayIcon(key.GetValue("DisplayIcon") as string);
                string? launcher = FindLauncher(installLocation, displayIcon);
                if (launcher is not null)
                {
                    string location = Path.GetDirectoryName(launcher)!;
                    return new ExistingInstallation(
                        ExistingInstallationStatus.Installed,
                        location,
                        "Une installation existante est encore présente dans Windows.",
                        subKey);
                }

                stale ??= new ExistingInstallation(
                    ExistingInstallationStatus.StaleRegistration,
                    installLocation,
                    "Windows contient une ancienne entrée Atlas Launcher, mais son exécutable est absent. "
                    + "Supprime cette entrée depuis Applications installées, puis réessaie.",
                    subKey);
                _log.Warning($"Entrée de désinstallation obsolète détectée ({view}, {subKey}).");
            }
        }

        foreach (string fallbackPath in fallbackPaths)
        {
            string? launcher = FindLauncher(fallbackPath, displayIcon: null);
            if (launcher is null)
            {
                continue;
            }

            return new ExistingInstallation(
                ExistingInstallationStatus.Installed,
                Path.GetDirectoryName(launcher),
                "Un exécutable Atlas Launcher existant a été détecté.",
                RegistrySubKey: null);
        }

        return stale ?? new ExistingInstallation(
            ExistingInstallationStatus.None,
            InstallLocation: null,
            Message: string.Empty,
            RegistrySubKey: null);
    }

    public void Register(InstalledApplicationRegistration registration)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey key = machine.CreateSubKey(registration.RegistrySubKey, writable: true)
            ?? throw new InvalidOperationException(
                "Windows n'a pas pu créer l'entrée Applications installées.");

        string uninstall = Quote(registration.UninstallerPath) + " --uninstall";
        string quietUninstall = Quote(registration.UninstallerPath) + " --uninstall --quiet";
        key.SetValue("DisplayName", InstallerProduct.Name, RegistryValueKind.String);
        key.SetValue("DisplayVersion", InstallerProduct.Version, RegistryValueKind.String);
        key.SetValue("Publisher", InstallerProduct.Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", registration.InstallLocation, RegistryValueKind.String);
        key.SetValue("DisplayIcon", registration.LauncherPath, RegistryValueKind.String);
        key.SetValue("UninstallString", uninstall, RegistryValueKind.String);
        key.SetValue("QuietUninstallString", quietUninstall, RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
        key.SetValue(
            "EstimatedSize",
            checked((int)Math.Min(int.MaxValue, Math.Max(1, registration.EstimatedSizeKiB))),
            RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public void Unregister(string registrySubKey)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        machine.DeleteSubKeyTree(registrySubKey, throwOnMissingSubKey: false);
    }

    public IReadOnlyDictionary<string, object?> Read(string registrySubKey)
    {
        using RegistryKey machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey? key = machine.OpenSubKey(registrySubKey, writable: false);
        if (key is null)
        {
            return new Dictionary<string, object?>();
        }

        return key.GetValueNames().ToDictionary(
            name => name,
            name => key.GetValue(name),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindLauncher(string? installLocation, string? displayIcon)
    {
        if (!string.IsNullOrWhiteSpace(displayIcon) && File.Exists(displayIcon))
        {
            return Path.GetFullPath(displayIcon);
        }

        if (string.IsNullOrWhiteSpace(installLocation))
        {
            return null;
        }

        string fullLocation;
        try
        {
            fullLocation = Path.GetFullPath(installLocation);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }

        return LauncherCandidates
            .Select(name => Path.Combine(fullLocation, name))
            .FirstOrDefault(File.Exists);
    }

    private static string? NormalizeDisplayIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string candidate = value.Trim().Trim('"');
        int comma = candidate.LastIndexOf(',');
        if (comma > 1 && int.TryParse(candidate[(comma + 1)..], out _))
        {
            candidate = candidate[..comma].Trim().Trim('"');
        }

        return candidate;
    }

    private static string Quote(string value) => $"\"{value}\"";
}

internal sealed record InstallerShortcut(
    string TargetPath,
    string WorkingDirectory,
    string IconLocation);

internal interface IInstallerShortcutService
{
    void Create(string shortcutPath, string targetPath, string workingDirectory);

    bool DeleteIfOwned(string shortcutPath, string expectedTargetPath);

    InstallerShortcut? Read(string shortcutPath);
}

internal sealed class WindowsInstallerShortcutService : IInstallerShortcutService
{
    public void Create(string shortcutPath, string targetPath, string workingDirectory)
    {
        string fullShortcut = Path.GetFullPath(shortcutPath);
        string fullTarget = Path.GetFullPath(targetPath);
        string fullWorkingDirectory = Path.GetFullPath(workingDirectory);
        InstallerShortcut? existing = Read(fullShortcut);
        if (existing is not null
            && !InstallerEnvironment.SamePath(existing.TargetPath, fullTarget))
        {
            throw new IOException(
                $"Le raccourci {fullShortcut} existe déjà et appartient à une autre application.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullShortcut)!);
        object? shell = null;
        object? shortcut = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Le service de raccourcis Windows est indisponible.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Le service de raccourcis Windows n'a pas démarré.");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(fullShortcut);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = fullTarget;
            dynamicShortcut.WorkingDirectory = fullWorkingDirectory;
            dynamicShortcut.IconLocation = fullTarget + ",0";
            dynamicShortcut.Description = InstallerProduct.Name;
            dynamicShortcut.Save();
        }
        finally
        {
            ReleaseCom(shortcut);
            ReleaseCom(shell);
        }
    }

    public bool DeleteIfOwned(string shortcutPath, string expectedTargetPath)
    {
        InstallerShortcut? shortcut = Read(shortcutPath);
        if (shortcut is null
            || !InstallerEnvironment.SamePath(shortcut.TargetPath, expectedTargetPath))
        {
            return false;
        }

        File.SetAttributes(shortcutPath, FileAttributes.Normal);
        File.Delete(shortcutPath);
        string? parent = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(parent)
            && Directory.Exists(parent)
            && !Directory.EnumerateFileSystemEntries(parent).Any()
            && string.Equals(
                Path.GetFileName(parent),
                InstallerProduct.Name,
                StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(parent);
        }

        return true;
    }

    public InstallerShortcut? Read(string shortcutPath)
    {
        string fullShortcut = Path.GetFullPath(shortcutPath);
        if (!File.Exists(fullShortcut))
        {
            return null;
        }

        object? shell = null;
        object? shortcut = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Le service de raccourcis Windows est indisponible.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Le service de raccourcis Windows n'a pas démarré.");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(fullShortcut);
            dynamic dynamicShortcut = shortcut;
            return new InstallerShortcut(
                (string)dynamicShortcut.TargetPath,
                (string)dynamicShortcut.WorkingDirectory,
                (string)dynamicShortcut.IconLocation);
        }
        finally
        {
            ReleaseCom(shortcut);
            ReleaseCom(shell);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal interface IInstallerProcessInspector
{
    IReadOnlyList<int> FindByExactPath(string executablePath);
}

internal sealed class WindowsInstallerProcessInspector : IInstallerProcessInspector
{
    private readonly InstallerLog _log;

    internal WindowsInstallerProcessInspector(InstallerLog log)
    {
        _log = log;
    }

    public IReadOnlyList<int> FindByExactPath(string executablePath)
    {
        string expected = Path.GetFullPath(executablePath);
        string processName = Path.GetFileNameWithoutExtension(expected);
        List<int> matches = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    string? actual = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(actual)
                        && InstallerEnvironment.SamePath(actual, expected))
                    {
                        matches.Add(process.Id);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                    _log.Warning(
                        $"Processus {process.Id} ignoré : son chemin exact n'est pas accessible.");
                }
            }
        }

        return matches;
    }
}

internal interface IInstallerSystemActions
{
    void OpenInstalledApps();

    void LaunchUnelevated(string executablePath, string workingDirectory);

    void ScheduleSelfDelete(string uninstallerPath, string installRoot, int processId);
}

internal sealed class WindowsInstallerSystemActions : IInstallerSystemActions
{
    public void OpenInstalledApps()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:appsfeatures",
            UseShellExecute = true
        });
    }

    public void LaunchUnelevated(string executablePath, string workingDirectory) =>
        InstallerUnelevatedProcessLauncher.Launch(executablePath, workingDirectory);

    public void ScheduleSelfDelete(string uninstallerPath, string installRoot, int processId)
    {
        string script = BuildSelfDeleteScript(uninstallerPath, installRoot, processId);
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = powershell,
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList =
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-WindowStyle",
                "Hidden",
                "-EncodedCommand",
                encoded
            }
        });
    }

    internal static string BuildSelfDeleteScript(
        string uninstallerPath,
        string installRoot,
        int processId)
    {
        string escapedExe = EscapePowerShellLiteral(Path.GetFullPath(uninstallerPath));
        string escapedRoot = EscapePowerShellLiteral(Path.GetFullPath(installRoot));
        return "$ErrorActionPreference='Stop';"
            + "Set-Location -LiteralPath $env:TEMP;"
            + "$deadline=[DateTime]::UtcNow.AddMinutes(5);"
            + $"while(Get-Process -Id {processId} -ErrorAction SilentlyContinue){{"
            + "if([DateTime]::UtcNow -ge $deadline){exit 2};"
            + "Start-Sleep -Milliseconds 100};"
            + "$removed=$false;"
            + "for($attempt=0;$attempt -lt 100 -and -not $removed;$attempt++){"
            + $"try{{Remove-Item -LiteralPath '{escapedExe}' -Force -ErrorAction Stop;$removed=$true}}"
            + "catch{Start-Sleep -Milliseconds 100}};"
            + "if(-not $removed){exit 3};"
            + $"if((Test-Path -LiteralPath '{escapedRoot}') -and "
            + $"@(Get-ChildItem -LiteralPath '{escapedRoot}' -Force).Count -eq 0)"
            + $"{{Remove-Item -LiteralPath '{escapedRoot}' -Force}}";
    }

    internal static bool IsCurrentProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");
}
