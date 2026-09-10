using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopViewV2 : UserControl, IDisposable
{
    internal ShopUiState State { get; } = new();
    internal ShopConversionViewV2 ConversionPage => ConversionView;
    internal event EventHandler? ConversionRequested;
    public ShopViewV2()
    {
        InitializeComponent();
        DataContext = State;
        SizeChanged += (_, _) => ApplyLayout();
        PageScroll.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) ApplyLayout(); };
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
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(ShopViewV2), new PropertyMetadata(420d));
    public static readonly DependencyProperty CardImageHeightProperty = DependencyProperty.Register(nameof(CardImageHeight), typeof(double), typeof(ShopViewV2), new PropertyMetadata(235d));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); private set => SetValue(CardWidthProperty, value); }
    public double CardImageHeight { get => (double)GetValue(CardImageHeightProperty); private set => SetValue(CardImageHeightProperty, value); }
    private void Service_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ShopOfferRow row })
        {
            State.OpenService(row);
            ServiceScroll.ScrollToTop();
            ServiceBackButton.Focus();
        }
    }
    private void ServiceBack_Click(object sender, RoutedEventArgs e) => CloseService();
    internal void CloseService()
    {
        ShopOfferRow? selected = State.SelectedOffer;
        State.CloseService();
        if (selected is not null && ProductList.ItemContainerGenerator.ContainerFromItem(selected) is FrameworkElement item)
            item.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await State.RefreshAsync();
    private void Credits_Click(object sender, RoutedEventArgs e)
    {
        ConversionRequested?.Invoke(this, EventArgs.Empty);
    }
    internal void ResetSession() => State.ResetSession();
    public void Dispose() { LauncherLocalization.LocaleChanged -= LocaleChanged; State.Dispose(); }
    private void ApplyLayout()
    {
        bool compact = ActualWidth < 1250;
        double inset = compact ? 24 : 60;
        ContentFrame.Margin = new Thickness(inset, 12, inset, 24);
        ServiceFrame.Margin = new Thickness(inset, 18, inset, 30);
        PageTitle.FontSize = compact ? 48 : 60;
        DetailColumn.Width = new GridLength(compact ? 340 : 400);
        // Account for the actual viewport, including its vertical scrollbar.
        double viewport = PageScroll.ViewportWidth > 0 ? PageScroll.ViewportWidth : ActualWidth - SystemParameters.VerticalScrollBarWidth;
        double available = Math.Min(1700, Math.Max(600, viewport - inset * 2));
        int columns = ActualWidth >= 1320 ? 3 : 2;
        CardWidth = Math.Floor((available - (columns - 1) * 24) / columns);
        CardImageHeight = (CardWidth - 2) * 9 / 16;
        CoinsColumn.Width = new GridLength(compact ? 150 : 214);
        ConversionCoins.Width = compact ? 140 : 190;
    }
}
