using System.ComponentModel;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;

namespace WotLK.Launcher.Installer.Setup;

public partial class InstallerWizardWindow : Window
{
    private readonly InstallerWizardRuntime? _runtime;
    private bool _allowClose;
    private bool _initialized;
    private InstallerWizardStep _lastStep;
    private InstallerNoticeKind _lastNotice;

    public InstallerWizardWindow()
    {
        InstallerManropeValidator.ValidateOrThrow();
        _runtime = InstallerWizardRuntime.CreateProduction();
        State = _runtime.State;
        InitializeWindow();
    }

    internal InstallerWizardWindow(InstallerPreviewScenario scenario)
    {
        InstallerManropeValidator.ValidateOrThrow();
        State = new InstallerWizardUiState(InstallerWizardPreviewData.Create(scenario));
        InitializeWindow();
    }

    internal InstallerWizardUiState State { get; }

    internal bool IsPreviewMode => State.IsPreview;

    internal int SystemEffectCount => _runtime?.SystemEffectCount ?? 0;

    internal void CloseForTest()
    {
        _allowClose = true;
        Close();
    }

    private void InitializeWindow()
    {
        _lastStep = State.Current.Step;
        _lastNotice = State.Current.Notice;
        InitializeComponent();
        DataContext = State;
        State.PropertyChanged += State_PropertyChanged;
        Loaded += Window_Loaded;
        SizeChanged += Window_SizeChanged;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
        if (!_initialized)
        {
            _initialized = true;
            _runtime?.Initialize();
        }

        PrimaryButton.Focus();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout();

    private void ApplyResponsiveLayout()
    {
        bool compact = ActualWidth > 0 && ActualWidth < 1120;
        SidebarColumn.Width = new GridLength(compact ? 250 : 292);
        ContentFrame.Margin = compact
            ? new Thickness(34, 28, 34, 24)
            : new Thickness(50, 38, 50, 30);
    }

    private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(InstallerWizardUiState.Current))
        {
            return;
        }

        bool blockingNoticeChanged = State.Current.Notice != _lastNotice
            && (State.Current.Notice is InstallerNoticeKind.ExistingInstallation or InstallerNoticeKind.InstallError
                || _lastNotice is InstallerNoticeKind.ExistingInstallation or InstallerNoticeKind.InstallError);
        bool pageChanged = State.Current.Step != _lastStep || blockingNoticeChanged;
        _lastStep = State.Current.Step;
        _lastNotice = State.Current.Notice;
        if (pageChanged)
        {
            WizardScrollHost.ScrollToTop();
            Dispatcher.BeginInvoke(() => PrimaryButton.Focus());
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (State.Current.Step == InstallerWizardStep.Completed)
        {
            _allowClose = true;
            if (_runtime is not null)
            {
                Hide();
                _runtime.FinishAndLaunchIfRequested();
            }

            Close();
            return;
        }

        if (State.IsPreview)
        {
            State.MoveNext();
            return;
        }

        await _runtime!.MoveNextAsync();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (State.IsPreview)
        {
            State.MoveBack();
        }
        else
        {
            _runtime!.MoveBack();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!State.Current.CanCancel || !ConfirmCancellation())
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (State.IsPreview)
        {
            State.SelectPreviewFolder();
            return;
        }

        OpenFolderDialog dialog = new()
        {
            Title = "Choisir le dossier d'installation d'Atlas Launcher",
            InitialDirectory = Directory.Exists(State.Current.InstallPath)
                ? State.Current.InstallPath
                : Path.GetDirectoryName(State.Current.InstallPath),
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
        {
            _runtime!.SetInstallPath(dialog.FolderName);
        }
    }

    private void DesktopShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (State.IsPreview)
        {
            State.ToggleDesktopShortcut();
        }
        else
        {
            _runtime!.ToggleDesktopShortcut();
        }
    }

    private void StartMenuShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (State.IsPreview)
        {
            State.ToggleStartMenuShortcut();
        }
        else
        {
            _runtime!.ToggleStartMenuShortcut();
        }
    }

    private void LaunchAfterInstall_Click(object sender, RoutedEventArgs e)
    {
        if (State.IsPreview)
        {
            State.ToggleLaunchAfterInstall();
        }
        else
        {
            _runtime!.ToggleLaunchAfterInstall();
        }
    }

    private void InstallPathTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!State.IsPreview && _initialized && InstallPathTextBox.Text != State.Current.InstallPath)
        {
            _runtime!.SetInstallPath(InstallPathTextBox.Text);
        }
    }

    private void InstallPathTextBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (State.IsPreview)
        {
            State.SetPreviewPath(InstallPathTextBox.Text);
        }
    }

    private void OpenInstalledApps_Click(object sender, RoutedEventArgs e) =>
        _runtime?.OpenInstalledApps();

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!State.Current.CanCloseWindow || !ConfirmCancellation())
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
        {
            DragMove();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !State.Current.CanCancel || !ConfirmCancellation())
        {
            return;
        }

        e.Handled = true;
        _allowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && !State.Current.CanCloseWindow)
        {
            e.Cancel = true;
            return;
        }

        if (!_allowClose && !ConfirmCancellation())
        {
            e.Cancel = true;
            return;
        }

        State.PropertyChanged -= State_PropertyChanged;
        Loaded -= Window_Loaded;
        SizeChanged -= Window_SizeChanged;
        _runtime?.Dispose();
    }

    private bool ConfirmCancellation()
    {
        if (State.IsPreview || State.Current.Step == InstallerWizardStep.Completed)
        {
            return true;
        }

        return MessageBox.Show(
                this,
                "Annuler l'installation d'Atlas Launcher ?",
                "Installation d'Atlas Launcher",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No)
            == MessageBoxResult.Yes;
    }
}
