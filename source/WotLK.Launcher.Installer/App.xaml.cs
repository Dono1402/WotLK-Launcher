using System.Windows;
using WotLK.Launcher.Installer.Setup;

namespace WotLK.Launcher.Installer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        bool uninstallMode = InstallerServices.IsUninstallMode(e.Args);
        // Capture and lock the setup before base.OnStartup can raise any managed
        // startup callback capable of constructing UI or deferring source use.
        InstallerSetupSource? setupSource = uninstallMode
            ? null
            : InstallerSetupSource.CaptureCurrentProcess();
        try
        {
            base.OnStartup(e);

            if (uninstallMode)
            {
                bool quiet = e.Args.Any(arg =>
                    string.Equals(arg, "/quiet", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase));
                if (quiet)
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    int exitCode = await UninstallerWindow.RunQuietAsync();
                    Shutdown(exitCode);
                    return;
                }

                UninstallerWindow uninstaller = new();
                MainWindow = uninstaller;
                uninstaller.Show();
                return;
            }

            InstallerWizardWindow window = new(setupSource!);
            MainWindow = window;
            window.Show();
        }
        catch
        {
            setupSource?.Dispose();
            throw;
        }
    }
}
