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
    internal Action<string> CopyReference { get; set; } = Clipboard.SetText;
    internal Action<string> OpenPaymentPage { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
        try
        {
            CopyReference(row.Reference);
            State?.ReportWalletAction("Référence copiée.", "Reference copied.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            State?.ReportWalletAction("La copie a échoué. Réessaie avec le bouton Copier la référence.", "Copy failed. Try Copy reference again.");
        }
    }
    private void PayPal_Click(object sender, RoutedEventArgs e)
    {
        if(sender is not Button { DataContext:ShopTopUpRow row } || State?.PaymentUrlFor(row.Id) is not { } url
            || !ShopFundingValidation.IsPayPalMeUrl(url))return;
        try
        {
            OpenPaymentPage(url);
            State?.ReportWalletAction("Page PayPal ouverte dans ton navigateur. Le paiement reste à effectuer et à valider.",
                "PayPal opened in your browser. Payment still needs to be made and approved.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            State?.ReportWalletAction("Impossible d’ouvrir le navigateur. Réessaie avec le bouton Ouvrir PayPal.",
                "Could not open the browser. Try Open PayPal again.");
        }
    }
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string value } && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long cents))
            State?.SetWalletAmount(cents);
    }
    private void ApplyLayout()
    {
        bool compact = ActualWidth < 1250;
        WalletFrame.MaxWidth = AtlasPageLayout.MaxWidth;
        WalletFrame.Margin = AtlasPageLayout.Margin;
        WalletTitle.FontSize = AtlasPageLayout.TitleSize;
        WalletTitle.LineHeight = 58;
        WalletSummaryColumn.Width = new GridLength(compact ? 320 : 360);
        WalletSummary.Padding = new Thickness(compact ? 20 : 24);
    }
}
