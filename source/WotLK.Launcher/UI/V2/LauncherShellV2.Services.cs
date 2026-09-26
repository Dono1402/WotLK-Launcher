using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    private void ServiceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ServiceSelector.SelectedValue is not LauncherService service) return;
        if (!SelectService(service))
            ServiceSelector.SetCurrentValue(Selector.SelectedValueProperty, ShellState.SelectedService);
    }

    internal bool SelectService(LauncherService service)
    {
        if (!Enum.IsDefined(service) || IsAuthenticationRequired || !_overlayCoordinator.CanNavigate
            || !ShellState.IsNavigationEnabled) return false;
        if (ShellState.SelectedService == service) return true;

        // Keep the same account and running operations; only the visible workspace changes.
        ShellState.SelectService(service);
        NavigateTo(LauncherShellPage.Game);
        ApplyAdaptiveLayout();
        return true;
    }

    private void ApplyServiceChrome()
    {
        bool wotlk = ShellState.IsWotlkSelected;
        AddonsNavigationButton.Visibility = ShopNavigationButton.Visibility =
            wotlk ? Visibility.Visible : Visibility.Collapsed;
        // The shop remains reachable from its tab when the header has less room.
        WalletHeader.Visibility = wotlk && ActualWidth >= 1550 ? Visibility.Visible : Visibility.Collapsed;
        MinecraftBackdrop.Visibility = wotlk ? Visibility.Collapsed : Visibility.Visible;
    }
}
