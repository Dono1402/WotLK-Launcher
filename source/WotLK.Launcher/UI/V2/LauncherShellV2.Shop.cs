using System.Windows;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

namespace WotLK.Launcher.UI.V2;

public partial class LauncherShellV2
{
    internal ShopViewV2 ShopPage => ShopView;

    internal void AttachShop(Func<CancellationToken, Task<ShopSnapshot>> read)
    {
        if (IsPreviewMode) throw new InvalidOperationException("A preview cannot use the real shop.");
        ShopView.State.Configure(read);
    }

    internal void PrepareShopPreview()
    {
        if (!IsPreviewMode) throw new InvalidOperationException("Shop examples are restricted to explicit previews.");
        ShopView.State.Configure(_ => Task.FromResult(ShopPreviewData.Create()));
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
        NavigateTo(LauncherShellPage.Shop);
        await ShopView.State.RefreshAsync();
    }
}
