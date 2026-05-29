using System.Windows;

namespace WotLK.Launcher.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (InstallerServices.IsUninstallMode(e.Args))
        {
            var exitCode = InstallerServices.RunUninstall(e.Args);
            Shutdown(exitCode);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
