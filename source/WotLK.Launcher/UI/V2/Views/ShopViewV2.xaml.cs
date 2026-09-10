using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopViewV2 : UserControl, IDisposable
{
    internal ShopUiState State { get; } = new();
    public ShopViewV2()
    {
        InitializeComponent();
        DataContext = State;
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += ViewLoaded;
        Unloaded += ViewUnloaded;
    }
    private void ViewLoaded(object sender, RoutedEventArgs e)
    {
        LauncherLocalization.LocaleChanged -= LocaleChanged;
        LauncherLocalization.LocaleChanged += LocaleChanged;
        State.RefreshLocale(); ApplyLayout();
    }
    private void ViewUnloaded(object sender, RoutedEventArgs e) => LauncherLocalization.LocaleChanged -= LocaleChanged;
    private void LocaleChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) State.RefreshLocale();
        else if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(State.RefreshLocale);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await State.RefreshAsync();
    private void Credits_Click(object sender, RoutedEventArgs e)
    {
        CreditsPanel.Visibility = CreditsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (CreditsPanel.IsVisible) CreditsPanel.BringIntoView();
    }
    internal void ResetSession() { State.ResetSession(); CreditsPanel.Visibility = Visibility.Collapsed; }
    public void Dispose() { LauncherLocalization.LocaleChanged -= LocaleChanged; State.Dispose(); }
    private void ApplyLayout()
    {
        bool compact = ActualWidth < 1250;
        bool stacked = ActualWidth < 960;
        ContentFrame.Margin = new Thickness(compact ? 24 : 64, compact ? 12 : 6, compact ? 24 : 64, 20);
        PageTitle.FontSize = compact ? 38 : 48;
        CategoryColumn.Width = new GridLength(compact ? 150 : 170);
        DetailColumn.Width = new GridLength(stacked ? 0 : compact ? 310 : 350);
        Grid.SetColumn(DetailsPanel, stacked ? 1 : 2);
        Grid.SetRow(DetailsPanel, stacked ? 1 : 0);
        DetailsPanel.Margin = stacked ? new Thickness(0, 16, 22, 0) : new Thickness(0);
    }
}
