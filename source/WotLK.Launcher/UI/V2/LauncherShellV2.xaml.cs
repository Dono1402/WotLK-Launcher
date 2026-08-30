using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2 : Window
{
    public LauncherShellV2()
    {
        ShellState = LauncherV2PreviewData.CreateShell();
        GameState = LauncherV2PreviewData.CreateGame();
        FriendsState = LauncherV2PreviewData.CreateFriends();

        InitializeComponent();
        DataContext = this;

        SizeChanged += LauncherShellV2_SizeChanged;
        StateChanged += LauncherShellV2_StateChanged;
        PreviewKeyDown += LauncherShellV2_PreviewKeyDown;
        PreviewGotKeyboardFocus += LauncherShellV2_PreviewGotKeyboardFocus;
        Loaded += (_, _) => ApplyAdaptiveLayout();
    }

    public ShellUiState ShellState { get; }

    public GameUiState GameState { get; }

    public FriendsUiState FriendsState { get; }

    internal void SetFriendsDrawerOpenForPreview()
    {
        if (!IsLoaded)
        {
            Loaded += OpenFriendsDrawerForPreviewOnLoaded;
            return;
        }

        OpenFriendsDrawerForPreview();
    }

    private void OpenFriendsDrawerForPreviewOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OpenFriendsDrawerForPreviewOnLoaded;
        OpenFriendsDrawerForPreview();
    }

    private void OpenFriendsDrawerForPreview()
    {
        FriendsState.IsOpen = true;
        FriendsDrawer.IsOpen = true;
        FriendsButton.Focusable = false;
    }

    private void ApplyAdaptiveLayout()
    {
        AdaptiveLayoutMode mode = AdaptiveLayoutClassifier.FromWidth(ActualWidth);
        ShellState.LayoutMode = mode;

        bool wide = mode == AdaptiveLayoutMode.Wide;
        bool stacked = mode == AdaptiveLayoutMode.Stacked;
        ProductGameName.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ProductDivider.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        FriendsButtonText.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        VersionText.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        RealmStatusText.SetCurrentValue(
            TextBlock.TextProperty,
            wide ? $"Arthas {ShellState.RealmStatus.ToLowerInvariant()}" : ShellState.RealmStatus);
        TopNavigation.Margin = mode == AdaptiveLayoutMode.Wide
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(0);
        RealmStatusChip.Padding = stacked
            ? new Thickness(9, 0, 9, 0)
            : new Thickness(11, 0, 11, 0);
    }

    private void LauncherShellV2_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyAdaptiveLayout();
    }

    private void LauncherShellV2_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button can be released before WPF enters its native move loop.
        }
    }

    private void FriendsButton_Click(object sender, RoutedEventArgs e)
    {
        FriendsState.IsOpen = !FriendsState.IsOpen;
        FriendsButton.Focusable = !FriendsState.IsOpen;
    }

    private void FriendsDrawer_CloseRequested(object? sender, EventArgs e)
    {
        FriendsState.IsOpen = false;
    }

    private void FriendsDrawer_Closed(object? sender, EventArgs e)
    {
        if (FriendsState.IsOpen)
        {
            return;
        }

        FriendsButton.Focusable = true;
        Keyboard.Focus(FriendsButton);
    }

    private void LauncherShellV2_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && FriendsState.IsOpen)
        {
            FriendsState.IsOpen = false;
            e.Handled = true;
        }
    }

    private void LauncherShellV2_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!FriendsState.IsOpen || FriendsDrawer.ContainsKeyboardFocusTarget(e.NewFocus as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        FriendsDrawer.FocusFirstControl();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private static T? FindAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current switch
            {
                Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(current),
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return null;
    }
}
