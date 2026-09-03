using System.Windows;
using WotLK.Launcher.Installer.Setup;

namespace WotLK.Launcher.Installer;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (InstallerServices.IsUninstallMode(e.Args))
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

        InstallerWizardWindow window = new();
        MainWindow = window;
        window.Show();
    }
}
