using System.Windows;
using System.ComponentModel;
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
        WalletHeader.StopAnimation();
    }
    private void ShopPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ShopView.State.HasOffers) { WalletHeader.StopAnimation(); }
    }
    private void OpenShopConversion(object? sender, EventArgs e)
    {
        if (IsAuthenticationRequired || !_overlayCoordinator.CanNavigate) return;
        DismissNavigationPanels(); ShopView.State.OpenConversion();
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
        ShopView.State.CloseConversion(); ShopView.State.CloseService();
        NavigateTo(LauncherShellPage.Shop);
        await ShopView.State.RefreshAsync();
    }
}
