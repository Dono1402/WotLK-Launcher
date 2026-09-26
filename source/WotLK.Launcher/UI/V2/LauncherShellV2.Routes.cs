using System.Windows;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private sealed record NavigationOrigin(LauncherService Service, LauncherShellPage Page, bool AtlasWallet,
        bool Wallet = false, bool Conversion = false, bool History = false, string? AddonId = null,
        string? OfferId = null, LauncherShellPage WalletReturnPage = LauncherShellPage.Game);
    private NavigationOrigin? _friendProfileOrigin;

    private NavigationOrigin CaptureNavigationOrigin() => new(ShellState.SelectedService, CurrentPage, _isAtlasWalletPage,
        ShopView.State.IsWalletOpen, ShopView.State.IsConversionOpen, ShopView.State.IsHistoryOpen,
        AddonsState.Current.IsDetailOpen ? AddonsState.Current.SelectedAddon?.Id : null,
        ShopView.State.IsServiceOpen ? ShopView.State.SelectedOffer?.Offer.Id : null, _walletReturnPage);

    private void NavigateToService(LauncherService service, LauncherShellPage page)
    {
        if (ShellState.SelectedService != service)
        {
            if (!IsSharedAtlasPage(CurrentPage)) _lastGamePages[ShellState.SelectedService] = CurrentPage;
            ShellState.SelectService(service);
            _animateNextNavigation = true;
        }
        NavigateTo(page);
    }

    private void RestoreNavigationOrigin(NavigationOrigin origin)
    {
        _isAtlasWalletPage = origin.AtlasWallet;
        _walletReturnPage = origin.WalletReturnPage;
        ShopView.SetAccountOnlyMode(origin.AtlasWallet);
        if (origin.Wallet) ShopView.State.OpenWallet();
        else if (origin.Conversion) ShopView.State.OpenConversion();
        if (origin.History) ShopView.State.OpenHistory();
        if (origin.OfferId is not null && ShopView.State.Offers.FirstOrDefault(row => row.Offer.Id == origin.OfferId) is { } offer)
            ShopView.State.OpenService(offer);
        NavigateToService(origin.Service, origin.Page);
        if (origin.Page == LauncherShellPage.Addons && origin.AddonId is not null) AddonsState.OpenDetails(origin.AddonId);
    }

    // Also used by the offscreen routed-key fixture: dismiss only the topmost layer.
    internal bool DismissServiceMenu()
    {
        if (!ServiceSelector.IsDropDownOpen) return false;
        ServiceSelector.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
        if (IsActive) ServiceSelector.Focus();
        return true;
    }
}
