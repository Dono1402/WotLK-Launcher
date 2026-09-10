using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopWalletViewV2 : UserControl
{
    private ShopUiState? State => DataContext as ShopUiState;
    public ShopWalletViewV2()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += (_, _) => ApplyLayout();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => State?.CloseWallet();
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long cents))
            State?.SetWalletAmount(cents);
    }
    private void ApplyLayout()
    {
        bool compact = ActualWidth < 1250;
        WalletFrame.Margin = new Thickness(compact ? 24 : 60, compact ? 18 : 24, compact ? 24 : 60, 24);
        WalletTitle.FontSize = compact ? 34 : 40;
        WalletTitle.LineHeight = compact ? 44 : 50;
        WalletSummaryColumn.Width = new GridLength(compact ? 320 : 360);
        WalletSummary.Padding = new Thickness(compact ? 20 : 24);
    }
}
