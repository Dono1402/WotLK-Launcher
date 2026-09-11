using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ShopWalletViewV2 : UserControl
{
    private ShopUiState? State => DataContext as ShopUiState;
    internal event EventHandler? HistoryRequested;
    internal event EventHandler? AdminRequested;
    public ShopWalletViewV2()
    {
        InitializeComponent();
        SizeChanged += (_, _) => ApplyLayout();
        Loaded += (_, _) => ApplyLayout();
    }
    private void Back_Click(object sender, RoutedEventArgs e) => State?.CloseWallet(returnToService: true);
    private void History_Click(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke(this, EventArgs.Empty);
    private void Admin_Click(object sender, RoutedEventArgs e) => AdminRequested?.Invoke(this, EventArgs.Empty);
    private async void CreateTopUp_Click(object sender, RoutedEventArgs e) { if(State is { } state) await state.CreateTopUpAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if(State is { } state) await state.RefreshAsync(); }
    private async void CancelTopUp_Click(object sender, RoutedEventArgs e)
    { if(State is { } state && sender is Button { DataContext:ShopTopUpRow row })await state.CancelTopUpAsync(row); }
    private void CopyReference_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button { DataContext:ShopTopUpRow row })return;
        try { Clipboard.SetText(row.Reference); } catch(System.Runtime.InteropServices.COMException) { }
    }
    private void PayPal_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button { DataContext:ShopTopUpRow row } || State?.PaymentUrlFor(row.Id) is not { } url
            || !ShopFundingValidation.IsPayPalMeUrl(url))return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute=true }); }
        catch(System.ComponentModel.Win32Exception) { }
    }
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
