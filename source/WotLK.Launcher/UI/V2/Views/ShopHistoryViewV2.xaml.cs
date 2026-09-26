using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopHistoryViewV2 : UserControl
{
    private ShopUiState? State => DataContext as ShopUiState;
    public ShopHistoryViewV2()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += (_, _) => ApplyLayout();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => State?.CloseHistory(returnToOrigin: true);
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (State is { } state) await state.RefreshAsync(); }
    private async void CancelOrder_Click(object sender, RoutedEventArgs e)
    { if (State is { } state && sender is Button { DataContext: ShopOrderRow order }) await state.CancelListedPurchaseAsync(order.Id); }
    private void ApplyLayout()
    {
        bool compact = ActualWidth < 1250;
        HistoryFrame.Margin = new Thickness(compact ? 24 : 60, compact ? 18 : 24, compact ? 24 : 60, 24);
        HistoryTitle.FontSize = compact ? 34 : 40;
    }
}
