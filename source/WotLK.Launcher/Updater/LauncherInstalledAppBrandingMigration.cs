using System.Diagnostics;
using System.IO;

namespace WotLK.Launcher.Updater;

// The first update still runs the previous executable's elevated helper. Repair
// its legacy publisher on the next normal public startup, after update commit.
internal static class LauncherInstalledAppBrandingMigration
{
    internal const string RefreshSwitch = "--atlas-refresh-installed-branding";

    internal static void TryRefreshAtStartup(bool hasPendingUpdate) =>
        RefreshWhenEligible(LauncherBuildFlavor.IsLocalClient, hasPendingUpdate, RefreshInstalledPublisher);

    internal static void RefreshWhenEligible(bool isLocalClient, bool hasPendingUpdate, Action refresh)
    {
        if (isLocalClient || hasPendingUpdate)
        {
            return;
        }

        try
        {
            refresh();
        }
        catch (Exception exception)
        {
            // A cancelled UAC prompt or inaccessible registration cannot block login.
            Trace.WriteLine("Atlas publisher refresh deferred: " + exception.GetType().Name);
        }
    }

    private static void RefreshInstalledPublisher()
    {
        string? executable = GetInstalledExecutable();
        if (executable is null)
        {
            return;
        }

        WindowsLauncherInstalledAppVersionRegistry registry =
            WindowsLauncherInstalledAppVersionRegistry.CreateProduction();
        LauncherInstalledAppVersionSyncResult result = registry.SynchronizePublisher(
            Path.GetDirectoryName(executable)!, executable, writable: false);
        if (result.Status != LauncherInstalledAppVersionSyncStatus.NeedsUpdate)
        {
            return;
        }

        if (LauncherUpdateSecurity.IsCurrentProcessElevated())
        {
            _ = RunElevated();
            return;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            Arguments = RefreshSwitch,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        using Process? process = Process.Start(startInfo);
    }

    internal static int RunElevated()
    {
        // In particular, a local build must never alter the installed public entry.
        if (!CanRunElevated(LauncherBuildFlavor.IsLocalClient,
                LauncherUpdateSecurity.IsCurrentProcessElevated()))
        {
            return 2;
        }

        try
        {
            string? executable = GetInstalledExecutable();
            if (executable is null)
            {
                return 2;
            }

            LauncherInstalledAppVersionSyncResult result =
                WindowsLauncherInstalledAppVersionRegistry.CreateProduction().SynchronizePublisher(
                    Path.GetDirectoryName(executable)!, executable, writable: true);
            return result.Status is LauncherInstalledAppVersionSyncStatus.Updated
                or LauncherInstalledAppVersionSyncStatus.AlreadyCurrent ? 0 : 2;
        }
        catch (Exception exception)
        {
            Trace.WriteLine("Atlas publisher refresh failed: " + exception.GetType().Name);
            return 2;
        }
    }

    internal static bool CanRunElevated(bool isLocalClient, bool isElevated) =>
        !isLocalClient && isElevated;

    private static string? GetInstalledExecutable()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.ProcessPath))
        {
            return null;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
        {
            return null;
        }

        string expected = Path.Combine(programFiles, "Atlas Launcher", "WotLK.Launcher.exe");
        string executable = Path.GetFullPath(Environment.ProcessPath);
        if (!string.Equals(executable, expected, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        LauncherUpdateElevationSecurity.ValidateNoReparsePoints(executable);
        return executable;
    }
}
