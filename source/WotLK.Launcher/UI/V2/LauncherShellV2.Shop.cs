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
    private bool _isAtlasWalletPage;
    private bool _walletReturnPending;
    private LauncherShellPage _walletReturnPage = LauncherShellPage.Game;

    private void InitializeShopPresentation()
    {
        WalletHeader.DataContext = ShopView.State;
        ShopView.ConversionRequested += OpenShopConversion;
        ShopView.HistoryRequested += OpenShopHistory;
        ShopView.AdminRequested += OpenShopAdmin;
        WalletHeader.CreditsRequested += OpenShopConversion;
        WalletHeader.WalletRequested += OpenShopWallet;
        ShopView.State.CreditGranted += ShopCreditGranted;
        ShopView.State.PropertyChanged += ShopPresentationChanged;
    }
    private void DisposeShopPresentation()
    {
        ShopView.ConversionRequested -= OpenShopConversion;
        ShopView.HistoryRequested -= OpenShopHistory;
        ShopView.AdminRequested -= OpenShopAdmin;
        WalletHeader.CreditsRequested -= OpenShopConversion;
        WalletHeader.WalletRequested -= OpenShopWallet;
        ShopView.State.CreditGranted -= ShopCreditGranted;
        ShopView.State.PropertyChanged -= ShopPresentationChanged;
        WalletHeader.StopAnimation();
    }
    private void ShopPresentationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ShopView.State.HasOffers) { WalletHeader.StopAnimation(); }
        if (!_isAtlasWalletPage || _walletReturnPending) return;
        _walletReturnPending = true;
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            _walletReturnPending = false;
            var state = ShopView.State;
            if (!_isAtlasWalletPage || CurrentPage != LauncherShellPage.Shop || state.IsWalletOpen
                || state.IsConversionOpen || state.IsHistoryOpen || state.IsShopAdminOpen) return;
            _isAtlasWalletPage = false;
            ShopView.SetAccountOnlyMode(false);
            LauncherShellPage target = _walletReturnPage;
            if (!ShellState.IsWotlkSelected && target is LauncherShellPage.Addons or LauncherShellPage.Armory or LauncherShellPage.Shop)
                target = LauncherShellPage.Game;
            NavigateTo(target);
        }));
    }
    private void EnterAtlasWallet()
    {
        if (CurrentPage == LauncherShellPage.Shop && !_isAtlasWalletPage) return;
        if (!_isAtlasWalletPage) _walletReturnPage = CurrentPage;
        _isAtlasWalletPage = true;
        ShopView.SetAccountOnlyMode(true);
    }
    private async void OpenShopConversion(object? sender, EventArgs e)
    {
        if (!ShellState.IsNavigationEnabled || IsAuthenticationRequired || !_overlayCoordinator.CanNavigate) return;
        bool enteringShop = CurrentPage != LauncherShellPage.Shop;
        EnterAtlasWallet();
        ShopView.State.OpenConversion(); NavigateTo(LauncherShellPage.Shop);
        if (enteringShop && !IsPreviewMode) await ShopView.State.RefreshAsync();
    }
    private async void OpenShopWallet(object? sender, EventArgs e)
    {
        if (!ShellState.IsNavigationEnabled || IsAuthenticationRequired || !_overlayCoordinator.CanNavigate) return;
        bool enteringShop = CurrentPage != LauncherShellPage.Shop;
        EnterAtlasWallet();
        ShopView.State.OpenWallet(); NavigateTo(LauncherShellPage.Shop);
        if (enteringShop && !IsPreviewMode) await ShopView.State.RefreshAsync();
    }
    private void OpenShopHistory(object? sender, EventArgs e)
    {
        if (!ShellState.IsNavigationEnabled || IsAuthenticationRequired || !_overlayCoordinator.CanNavigate) return;
        NavigateTo(LauncherShellPage.Shop); ShopView.State.OpenHistory();
    }
    private async void OpenShopAdmin(object? sender, EventArgs e)
    {
        if (!ShellState.IsNavigationEnabled || IsAuthenticationRequired || !_overlayCoordinator.CanNavigate) return;
        NavigateTo(LauncherShellPage.Shop); await ShopView.State.OpenAdminFundingAsync();
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

    internal void AttachShop(Func<CancellationToken, Task<ShopSnapshot>> read, ShopFundingActions? funding = null, ShopPurchaseActions? purchases = null, ShopConversionActions? conversions = null)
    {
        if (IsPreviewMode) throw new InvalidOperationException("A preview cannot use the real shop.");
        ShopView.State.Configure(read);
        if(funding is not null)ShopView.State.ConfigureFunding(funding);
        if(purchases is not null)ShopView.State.ConfigurePurchases(purchases);
        if(conversions is not null)ShopView.State.ConfigureConversions(conversions);
        _ = RefreshShopHeaderAsync();
    }

    internal void PrepareShopPreview(bool manualFunding = false)
    {
        if (!IsPreviewMode) throw new InvalidOperationException("Shop examples are restricted to explicit previews.");
        if(manualFunding)
        {
            ShopFundingPreview funding=new();
            ShopView.State.Configure(funding.ReadSnapshot);
            ShopView.State.ConfigureFunding(funding.Actions,preview:true);
        }
        else ShopView.State.ConfigurePreview(ShopPreviewData.Create());
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
        _isAtlasWalletPage = false;
        ShopView.SetAccountOnlyMode(false);
        ShopView.State.CloseConversion(); ShopView.State.CloseService(); ShopView.State.CloseWallet(); ShopView.State.CloseHistory(); ShopView.State.CloseAdminFunding();
        NavigateTo(LauncherShellPage.Shop);
        await ShopView.State.RefreshAsync();
    }
}
