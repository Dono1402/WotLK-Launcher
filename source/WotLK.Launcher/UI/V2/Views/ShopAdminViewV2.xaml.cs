using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopAdminViewV2 : UserControl
{
    private ShopUiState? State=>DataContext as ShopUiState;
    public ShopAdminViewV2() { InitializeComponent(); }
    private void Back_Click(object sender,RoutedEventArgs e)=>State?.CloseAdminFunding(returnToWallet:true);
    private async void Refresh_Click(object sender,RoutedEventArgs e) { if(State is { } state)await state.RefreshAdminTopUpsAsync(); }
    private async void Next_Click(object sender,RoutedEventArgs e) { if(State is { } state)await state.RefreshAdminTopUpsAsync(nextPage:true); }
    private async void Search_Click(object sender,RoutedEventArgs e) { if(State is { } state)await state.SearchAdminTopUpAsync(); }
    private async void Submit_Click(object sender,RoutedEventArgs e) { if(State is { } state)await state.SubmitAdminDecisionAsync(); }
}
