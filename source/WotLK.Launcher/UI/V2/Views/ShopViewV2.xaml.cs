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
        bool stacked = ActualWidth < 1480;
        ContentFrame.Margin = new Thickness(compact ? 24 : 40, 0, 24, 16);
        PageTitle.FontSize = compact ? 48 : 60;
        CategoryColumn.Width = new GridLength(compact ? 170 : 196);
        DetailColumn.Width = new GridLength(stacked ? 0 : 384);
        DetailGap.Width = new GridLength(stacked ? 0 : 20);
        Grid.SetColumn(DetailsPanel, stacked ? 2 : 4);
        Grid.SetRow(DetailsPanel, stacked ? 1 : 0);
        DetailsPanel.MaxWidth = stacked ? 440 : double.PositiveInfinity;
        DetailsPanel.HorizontalAlignment = stacked ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        DetailsPanel.Margin = new Thickness(0);
        CataloguePanel.Margin = new Thickness(0, 46, 0, stacked ? 20 : 46);
        MottoPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CoinsColumn.Width = new GridLength(compact ? 150 : 214);
        ConversionCoins.Width = compact ? 140 : 190;
    }
}
