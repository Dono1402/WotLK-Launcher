using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace WotLK.Launcher.Installer.Setup;

public partial class InstallerWizardWindow : Window
{
    private bool _allowClose;

    internal InstallerWizardWindow(InstallerPreviewScenario scenario)
    {
        InstallerManropeValidator.ValidateOrThrow();
        State = new InstallerWizardUiState(InstallerWizardPreviewData.Create(scenario));
        InitializeComponent();
        DataContext = State;
        State.PropertyChanged += State_PropertyChanged;
        Loaded += Window_Loaded;
        SizeChanged += Window_SizeChanged;
    }

    internal InstallerWizardUiState State { get; }

    internal bool IsPreviewMode => State.IsPreview;

    internal int SystemEffectCount => 0;

    internal void CloseForTest()
    {
        _allowClose = true;
        Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyResponsiveLayout();
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

        WizardScrollHost.ScrollToTop();
        Dispatcher.BeginInvoke(() => PrimaryButton.Focus());
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (State.Current.Step == InstallerWizardStep.Completed)
        {
            _allowClose = true;
            Close();
            return;
        }

        State.MoveNext();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => State.MoveBack();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!State.Current.CanCancel)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void Browse_Click(object sender, RoutedEventArgs e) =>
        State.SelectPreviewFolder();

    private void DesktopShortcut_Click(object sender, RoutedEventArgs e) =>
        State.ToggleDesktopShortcut();

    private void StartMenuShortcut_Click(object sender, RoutedEventArgs e) =>
        State.ToggleStartMenuShortcut();

    private void LaunchAfterInstall_Click(object sender, RoutedEventArgs e) =>
        State.ToggleLaunchAfterInstall();

    private void InstallPathTextBox_LostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e) =>
        State.SetPreviewPath(InstallPathTextBox.Text);

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!State.Current.CanCloseWindow)
        {
            return;
        }

        _allowClose = true;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !State.Current.CanCancel)
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

        State.PropertyChanged -= State_PropertyChanged;
        Loaded -= Window_Loaded;
        SizeChanged -= Window_SizeChanged;
    }
}
