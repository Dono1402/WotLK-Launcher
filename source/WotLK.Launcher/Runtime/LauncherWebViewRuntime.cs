using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;

namespace WotLK.Launcher.Runtime;

internal static class LauncherWebViewRuntime
{
    private static readonly SemaphoreSlim InstallGate = new(1, 1);
    private static readonly Version MinimumVersion = new(146, 0, 3856, 0);

    internal static string? InstalledVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { return null; }
    }

    internal static bool IsSupported(string? value)
        => Version.TryParse(value?.Split(' ')[0], out Version? version) && version >= MinimumVersion;

    internal static async Task EnsureAvailableAsync(LauncherArmoryLocalConfiguration configuration,
        CancellationToken cancellationToken, Action? onInstalling = null,
        Func<string?>? getVersion = null, Func<string, Task<int>>? install = null)
    {
        if (!configuration.IsPackaged) return;
        getVersion ??= InstalledVersion;
        if (IsSupported(getVersion())) return;
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            if (IsSupported(getVersion())) return;
            string path = configuration.WebViewInstallerPath ?? string.Empty;
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new InvalidOperationException("The bundled Microsoft WebView2 installer is missing.");
            cancellationToken.ThrowIfCancellationRequested();
            onInstalling?.Invoke();
            // Installation is started by the player's launcher, only when the required component is absent.
            // Once started, changing pages does not kill a Microsoft installer midway through its work.
            int exitCode = await (install ?? InstallAsync)(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSupported(getVersion()))
                throw new InvalidOperationException($"Microsoft WebView2 installation did not provide a compatible runtime ({exitCode}).");
        }
        finally { InstallGate.Release(); }
    }

    private static async Task<int> InstallAsync(string path)
    {
        ProcessStartInfo start = new(path)
        {
            UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(path)!
        };
        start.ArgumentList.Add("/silent");
        start.ArgumentList.Add("/install");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Microsoft WebView2 installation could not start.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        return process.ExitCode;
    }
}
