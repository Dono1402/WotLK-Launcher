using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    internal ShopViewV2 ShopPage => ShopView;
    internal ShopConversionViewV2 ConversionPage => ShopView.ConversionPage;
    internal WalletBalanceV2 WalletControl => WalletHeader;
    private bool _shopHeaderLoading;
    private bool _shopHeaderRequested;

    private void InitializeShopPresentation()
    {
        WalletHeader.DataContext = ShopView.State;
        ShopView.ConversionRequested += OpenShopConversion;
        ShopView.State.CreditGranted += ShopCreditGranted;
        ShopView.State.PropertyChanged += ShopPresentationChanged;
    }
    private void DisposeShopPresentation()
    {
        ShopView.ConversionRequested -= OpenShopConversion;
        ShopView.State.CreditGranted -= ShopCreditGranted;
        ShopView.State.PropertyChanged -= ShopPresentationChanged;
        WalletHeader.CloseMenu(); WalletHeader.StopAnimation(); ShopCreditFlightLayer.Children.Clear();
    }
    private void ShopPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ShopView.State.HasOffers) { WalletHeader.CloseMenu(); WalletHeader.StopAnimation(); ShopCreditFlightLayer.Children.Clear(); }
    }
    private void OpenShopConversion(object? sender, EventArgs e)
    {
        if (IsAuthenticationRequired || !_overlayCoordinator.CanNavigate) return;
        DismissNavigationPanels(); WalletHeader.CloseMenu(); ShopView.State.OpenConversion();
    }
    private async Task RefreshShopHeaderAsync()
    {
        if (IsPreviewMode || _shopHeaderLoading || _shopHeaderRequested || IsAuthenticationRequired || !ShellState.IsAuthenticated || !ShopView.State.CanRefresh) return;
        _shopHeaderLoading = _shopHeaderRequested = true;
        try { await ShopView.State.RefreshAsync(); }
        finally
        {
            _shopHeaderLoading = false;
            // A different session may have opened while the old read was cancelled.
            if (!_shopHeaderRequested) _ = RefreshShopHeaderAsync();
        }
    }
    private void ShopCreditGranted(ShopCreditChange change)
    {
        WalletHeader.AnimateCredit(change);
        ShopCreditFlightLayer.Children.Clear();
        if (!SystemParameters.ClientAreaAnimation) return;
        Point target = WalletHeader.TranslatePoint(new Point(WalletHeader.ActualWidth / 2, WalletHeader.ActualHeight / 2), ShopCreditFlightLayer);
        Point origin = ConversionPage.CreditOrigin ?? new Point(ActualWidth / 2 + 120, ActualHeight / 2);
        Border credit = new() { Background = new SolidColorBrush(Color.FromRgb(52, 43, 18)), BorderBrush = new SolidColorBrush(Color.FromRgb(255, 211, 104)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 8, 16, 8),
            Child = new TextBlock { Text = "+" + ShopUiState.FormatEuros(change.AfterCents - change.BeforeCents), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(255, 224, 150)) } };
        ShopCreditFlightLayer.Children.Add(credit);
        credit.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Point start = new(origin.X - credit.DesiredSize.Width / 2, origin.Y - credit.DesiredSize.Height / 2);
        Point end = new(target.X - credit.DesiredSize.Width / 2, target.Y - credit.DesiredSize.Height / 2);
        Canvas.SetLeft(credit, start.X); Canvas.SetTop(credit, start.Y);
        credit.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(start.X, end.X, TimeSpan.FromMilliseconds(850)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        credit.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(start.Y, end.Y, TimeSpan.FromMilliseconds(850)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        DoubleAnimation fade = new(1, 0, TimeSpan.FromMilliseconds(230)) { BeginTime = TimeSpan.FromMilliseconds(720) };
        fade.Completed += (_, _) => ShopCreditFlightLayer.Children.Remove(credit);
        credit.BeginAnimation(OpacityProperty, fade);
    }

    internal void AttachShop(Func<CancellationToken, Task<ShopSnapshot>> read)
    {
        if (IsPreviewMode) throw new InvalidOperationException("A preview cannot use the real shop.");
        ShopView.State.Configure(read);
        _ = RefreshShopHeaderAsync();
    }

    internal void PrepareShopPreview()
    {
        if (!IsPreviewMode) throw new InvalidOperationException("Shop examples are restricted to explicit previews.");
        ShopView.State.ConfigurePreview(ShopPreviewData.Create());
        Loaded += OpenPreview;
        async void OpenPreview(object sender, RoutedEventArgs e)
        {
            Loaded -= OpenPreview;
            NavigateTo(LauncherShellPage.Shop);
            await ShopView.State.RefreshAsync();
        }
    }

    private async void ShopNavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ShellState.IsNavigationEnabled || !_overlayCoordinator.CanNavigate || IsAuthenticationRequired) return;
        ShopView.State.CloseConversion();
        NavigateTo(LauncherShellPage.Shop);
        await ShopView.State.RefreshAsync();
    }
}
