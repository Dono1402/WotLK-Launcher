using System.Windows;

namespace WotLK.Launcher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (GameDirectoryAccess.IsGrantAccessMode(e.Args))
        {
            Shutdown(GameDirectoryAccess.RunGrantAccess(e.Args));
            return;
        }

        if (GameInstallServices.IsGameUninstallMode(e.Args))
        {
            Shutdown(GameInstallServices.RunGameUninstall(e.Args));
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
