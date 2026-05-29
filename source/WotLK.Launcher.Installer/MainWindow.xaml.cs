using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace WotLK.Launcher.Installer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InstallPathBox.Text = InstallerServices.GetInstallRoot();
        AppendStartupLog();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        Progress.Value = 0;
        var closeAfterInstall = false;

        try
        {
            await Task.Run(InstallLauncher);
            Progress.Value = 100;
            AppendLog("Installation terminee.");

            if (LaunchAfterInstallBox.IsChecked == true)
            {
                LaunchInstalledLauncher();
                closeAfterInstall = true;
            }
        }
        catch (Exception ex)
        {
            AppendLog("Erreur: " + ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "Erreur d'installation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (closeAfterInstall)
            {
                Close();
            }
            else
            {
                InstallButton.IsEnabled = true;
            }
        }
    }

    private void InstallLauncher()
    {
        var installPath = InstallPathBox.Dispatcher.Invoke(() => InstallPathBox.Text.Trim());
        if (string.IsNullOrWhiteSpace(installPath))
        {
            throw new InvalidOperationException("Le dossier d'installation est introuvable.");
        }

        var installRoot = Path.GetFullPath(installPath);
        var targetExe = InstallerServices.GetLauncherPath(installRoot);
        var tempExe = targetExe + ".tmp";

        Dispatcher.Invoke(() => AppendLog("Création du dossier: " + installRoot));
        Directory.CreateDirectory(installRoot);
        Dispatcher.Invoke(() => Progress.Value = 18);

        Dispatcher.Invoke(() => AppendLog("Extraction du launcher..."));
        ExtractLauncher(tempExe);
        Dispatcher.Invoke(() => Progress.Value = 58);

        if (File.Exists(targetExe))
        {
            File.Delete(targetExe);
        }

        File.Move(tempExe, targetExe);
        Dispatcher.Invoke(() => Progress.Value = 70);

        Dispatcher.Invoke(() => AppendLog("Création du raccourci menu Démarrer..."));
        InstallerServices.CreateStartMenuShortcut(targetExe, installRoot);

        if (DesktopShortcutBox.Dispatcher.Invoke(() => DesktopShortcutBox.IsChecked == true))
        {
            Dispatcher.Invoke(() => AppendLog("Création du raccourci bureau..."));
            InstallerServices.CreateDesktopShortcut(targetExe, installRoot);
        }

        Dispatcher.Invoke(() => Progress.Value = 82);
        Dispatcher.Invoke(() => AppendLog("Création du désinstalleur..."));
        var uninstallerExe = InstallerServices.CopySelfAsUninstaller(installRoot);

        Dispatcher.Invoke(() => Progress.Value = 92);
        Dispatcher.Invoke(() => AppendLog("Inscription dans les applications Windows..."));
        InstallerServices.RegisterInstalledApp(installRoot, targetExe, uninstallerExe);
        InstallerServices.WriteInstallMarker(installRoot, targetExe, uninstallerExe);

        Dispatcher.Invoke(() => AppendLog("Nettoyage de l'ancienne configuration launcher..."));
        try
        {
            InstallerServices.SanitizeLauncherSettings();
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => AppendLog("Nettoyage configuration ignoré: " + ex.Message));
        }

        Dispatcher.Invoke(() => Progress.Value = 100);
    }

    private static void ExtractLauncher(string destinationPath)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("Payload.WotLK.Launcher.exe", StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new InvalidOperationException("Payload launcher introuvable dans l'installer.");
        }

        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Impossible d'ouvrir le payload launcher.");
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        resource.CopyTo(output);
    }

    private void LaunchInstalledLauncher()
    {
        var targetExe = InstallerServices.GetLauncherPath(Path.GetFullPath(InstallPathBox.Text.Trim()));
        Process.Start(new ProcessStartInfo
        {
            FileName = targetExe,
            WorkingDirectory = Path.GetDirectoryName(targetExe),
            UseShellExecute = true
        });
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }

        DragMove();
    }

    private void AppendStartupLog()
    {
        LogBox.AppendText("[18:49:24] Prêt à installer le launcher." + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private void AppendLog(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }
}
