using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopViewV2 : UserControl, IDisposable
{
    internal ShopUiState State { get; } = new();
    internal ShopConversionViewV2 ConversionPage => ConversionView;
    internal ShopWalletViewV2 WalletPage => WalletView;
    internal ShopHistoryViewV2 HistoryPage => HistoryView;
    internal ShopAdminViewV2 AdminPage => AdminView;
    internal void SetAccountOnlyMode(bool accountOnly)
    {
        State.AccountOnlyMode = accountOnly;
        if (accountOnly) PageScroll.Visibility = Visibility.Collapsed;
        else PageScroll.ClearValue(VisibilityProperty);
    }
    internal event EventHandler? ConversionRequested;
    internal event EventHandler? HistoryRequested;
    internal event EventHandler? AdminRequested;
    private readonly System.Windows.Threading.DispatcherTimer _orderPoll = new() { Interval = TimeSpan.FromSeconds(5) };
    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode), typeof(AdaptiveLayoutMode), typeof(ShopViewV2),
        new PropertyMetadata(AdaptiveLayoutMode.Wide, (target, _) => ((ShopViewV2)target).ApplyLayout()));
    public AdaptiveLayoutMode LayoutMode { get => (AdaptiveLayoutMode)GetValue(LayoutModeProperty); set => SetValue(LayoutModeProperty, value); }
    public ShopViewV2()
    {
        InitializeComponent();
        foreach (FrameworkElement panel in new FrameworkElement[] { PageScroll, ServiceFrame, ConversionView, WalletView, HistoryView, AdminView })
            AtlasMotion.AttachReveal(panel);
        DataContext = State;
        WalletView.HistoryRequested += HistoryRequestedFromWallet;
        WalletView.AdminRequested += AdminRequestedFromWallet;
        SizeChanged += (_, _) => ApplyLayout();
        PageScroll.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) ApplyLayout(); };
        Loaded += ViewLoaded;
        Unloaded += ViewUnloaded;
        _orderPoll.Tick += PollOrders;
    }
    private void ViewLoaded(object sender, RoutedEventArgs e)
    {
        LauncherLocalization.LocaleChanged -= LocaleChanged;
        LauncherLocalization.LocaleChanged += LocaleChanged;
        State.RefreshLocale(); ApplyLayout();
        _orderPoll.Start();
    }
    private void ViewUnloaded(object sender, RoutedEventArgs e) { LauncherLocalization.LocaleChanged -= LocaleChanged; _orderPoll.Stop(); }
    private async void PollOrders(object? sender, EventArgs e)
    {
        if (IsVisible && (State.NeedsPurchaseRefresh || State.NeedsConversionRefresh)) await State.PollPurchaseAsync();
    }
    private void LocaleChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) State.RefreshLocale();
        else if (!Dispatcher.HasShutdownStarted) _ = Dispatcher.BeginInvoke(State.RefreshLocale);
    }
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(ShopViewV2), new PropertyMetadata(320d));
    public static readonly DependencyProperty CardImageHeightProperty = DependencyProperty.Register(nameof(CardImageHeight), typeof(double), typeof(ShopViewV2), new PropertyMetadata(178.875d));
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
    private async void Purchase_Click(object sender, RoutedEventArgs e) => await State.PurchaseAsync();
    private async void CancelPurchase_Click(object sender, RoutedEventArgs e) => await State.CancelPurchaseAsync();
    private async void RefreshPurchase_Click(object sender, RoutedEventArgs e) => await State.RefreshPurchaseAsync();
    private void Funding_Click(object sender, RoutedEventArgs e) => State.PrepareServiceFunding();
    private void History_Click(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke(this, EventArgs.Empty);
    private void HistoryRequestedFromWallet(object? sender, EventArgs e) => HistoryRequested?.Invoke(this, EventArgs.Empty);
    private void AdminRequestedFromWallet(object? sender, EventArgs e) => AdminRequested?.Invoke(this, EventArgs.Empty);
    private void Credits_Click(object sender, RoutedEventArgs e)
    {
        ConversionRequested?.Invoke(this, EventArgs.Empty);
    }
    internal void ResetSession() => State.ResetSession();
    public void Dispose() { _orderPoll.Stop(); _orderPoll.Tick -= PollOrders; LauncherLocalization.LocaleChanged -= LocaleChanged; WalletView.HistoryRequested -= HistoryRequestedFromWallet; WalletView.AdminRequested -= AdminRequestedFromWallet; State.Dispose(); }
    private void ApplyLayout()
    {
        if (!IsInitialized) return;
        bool compact = ActualWidth < 1250;
        double inset = AtlasPageLayout.Margin.Left;
        ContentFrame.Margin = AtlasPageLayout.Margin;
        ServiceFrame.Margin = AtlasPageLayout.Margin;
        ServiceFrame.MaxWidth = ServiceFooter.MaxWidth = AtlasPageLayout.MaxWidth;
        ServiceFooter.Margin = new Thickness(inset, 8, inset, 18);
        PageTitle.FontSize = AtlasPageLayout.TitleSize;
        PageDescription.FontSize = AtlasPageLayout.SubtitleSize;
        DetailColumn.Width = new GridLength(compact ? 380 : 440);
        // Account for the actual viewport, including its vertical scrollbar.
        double viewport = PageScroll.ViewportWidth > 0 ? PageScroll.ViewportWidth : ActualWidth - SystemParameters.VerticalScrollBarWidth;
        double available = Math.Min(ContentFrame.MaxWidth, Math.Max(600, viewport - inset * 2));
        int columns = available >= 1000 ? 4 : available >= 720 ? 3 : 2;
        CardWidth = Math.Floor((available - (columns - 1) * 18) / columns);
        CardImageHeight = (CardWidth - 2) * 9 / 16;
        MoreConversionButton.Width = compact ? 260 : 280;
    }
}
