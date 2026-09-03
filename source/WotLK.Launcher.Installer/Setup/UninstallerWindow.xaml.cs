using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace WotLK.Launcher.Installer.Setup;

public partial class UninstallerWindow : Window
{
    private readonly UninstallerEngine _engine;
    private readonly InstallerLog _log;
    private readonly string _installRoot;
    private bool _isRemoving;
    private bool _completed;

    public UninstallerWindow()
    {
        InstallerManropeValidator.ValidateOrThrow();
        _installRoot = GetCurrentInstallRoot();
        (_engine, _log) = CreateEngine(_installRoot);
        InitializeComponent();
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    internal static async Task<int> RunQuietAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string root = GetCurrentInstallRoot();
            (UninstallerEngine engine, InstallerLog log) = CreateEngine(root);
            using (log)
            {
                UninstallResult result = await engine.UninstallAsync(root, cancellationToken);
                return result.Status == UninstallStatus.Completed ? 0 : 2;
            }
        }
        catch
        {
            return 1;
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_completed)
        {
            Close();
            return;
        }

        if (_isRemoving)
        {
            return;
        }

        _isRemoving = true;
        CloseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = "Désinstallation…";
        RemovalProgress.Visibility = Visibility.Visible;
        StatusPanel.Visibility = Visibility.Collapsed;
        try
        {
            UninstallResult result = await _engine.UninstallAsync(_installRoot, CancellationToken.None);
            if (result.Status == UninstallStatus.LauncherRunning)
            {
                ShowRetry(result.Message);
                return;
            }

            _completed = true;
            TitleText.Text = "Atlas Launcher est désinstallé";
            MessageText.Text = "Le launcher a été retiré. Tes données Atlas et ton client WoW ont été conservés.";
            RemovalProgress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
            PrimaryButton.IsEnabled = true;
            PrimaryButton.Content = "Fermer";
            PrimaryButton.Focus();
        }
        catch (Exception exception)
        {
            _log.Error("Erreur affichée par le désinstalleur", exception);
            TitleText.Text = "Désinstallation interrompue";
            MessageText.Text = "Atlas Launcher n'a pas pu être entièrement retiré.";
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Ferme Atlas Launcher et vérifie les autorisations Windows, puis réessaie.";
            ShowRetry(StatusText.Text);
        }
        finally
        {
            _isRemoving = false;
        }
    }

    private void ShowRetry(string message)
    {
        RemovalProgress.Visibility = Visibility.Collapsed;
        StatusPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
        CloseButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        PrimaryButton.IsEnabled = true;
        PrimaryButton.Content = "Réessayer";
        PrimaryButton.Focus();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRemoving)
        {
            Close();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRemoving)
        {
            e.Cancel = true;
            return;
        }

        _log.Dispose();
    }

    private static string GetCurrentInstallRoot()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Impossible de localiser le désinstalleur.");
        return Path.GetDirectoryName(Path.GetFullPath(executable))
            ?? throw new InvalidOperationException("Le dossier du désinstalleur est invalide.");
    }

    private static (UninstallerEngine Engine, InstallerLog Log) CreateEngine(string root)
    {
        InstallerEnvironment production = InstallerEnvironment.CreateProduction();
        AtlasInstallState state = UninstallerEngine.ReadState(root);
        InstallerEnvironment environment;
        if (state.IsTestInstallation)
        {
            bool guarded = root.Contains("Atlas Launcher 04D2 Test", StringComparison.OrdinalIgnoreCase)
                && state.RegistrySubKey.Contains("AtlasLauncher.04D2.Test.", StringComparison.Ordinal)
                && state.DesktopShortcutPath.Contains("04D2 Test", StringComparison.OrdinalIgnoreCase)
                && state.StartMenuShortcutPath.Contains("04D2 Test", StringComparison.OrdinalIgnoreCase)
                && state.InstallerLogPath.Contains("Atlas Launcher 04D2 Test", StringComparison.OrdinalIgnoreCase);
            if (!guarded)
            {
                throw new InvalidDataException("L'identité de l'installation de test est invalide.");
            }

            environment = production with
            {
                DefaultInstallPath = root,
                DesktopShortcutPath = state.DesktopShortcutPath,
                StartMenuShortcutPath = state.StartMenuShortcutPath,
                RegistrySubKey = state.RegistrySubKey,
                DetectionRegistrySubKeys = [state.RegistrySubKey],
                LogPath = state.InstallerLogPath,
                IsTest = true,
                AllowedTestInstallRoots = [root]
            };
        }
        else
        {
            environment = production with { DefaultInstallPath = root };
        }

        InstallerLog log = new(environment.LogPath);
        WindowsInstallerRegistry registry = new(log);
        WindowsInstallerShortcutService shortcuts = new();
        WindowsInstallerProcessInspector processes = new(log);
        WindowsInstallerSystemActions actions = new();
        return (
            new UninstallerEngine(environment, registry, shortcuts, processes, actions, log),
            log);
    }
}
