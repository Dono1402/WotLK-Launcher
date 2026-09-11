using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShopWpfTests
{
    private static async Task VerifyManualFundingAsync(LauncherShellV2 shell,ShopViewV2 shop,string directory)
    {
        shell.Width=1597.6; shell.Height=996.8;
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        ShopUiState state=shop.State; ShopWalletViewV2 wallet=shop.WalletPage; ShopAdminViewV2 admin=shop.AdminPage;
        Check(state.IsFundingPreview && state.ManualFundingAvailable && !state.CanPurchase && !state.CanConvert,"Offline funding preview has no game fulfillment.");
        Get<Button>(shell.WalletControl,"EuroWalletButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(state.IsWalletOpen && Get<Button>(wallet,"WalletAdminButton").IsVisible,"The configured demo administrator can reach the management page from the wallet.");
        Get<TextBox>(wallet,"WalletAmountInput").Text="20"; await Pump();
        Check(Get<ListBox>(wallet,"PaymentMethodPicker").SelectedItem==state.PaymentMethods.Single(row=>row.Id=="paypal")
            && state.PaymentMethods.Single(row=>row.Id=="card").IsSelectable==false,"PayPal is selected; unimplemented providers cannot be selected.");
        Check(Get<Button>(wallet,"WalletPayButton").IsEnabled,"A valid amount enables creation of a request.");
        Capture(shell,Path.Combine(directory,"funding-wallet-ready-fr.png"));
        Get<Button>(wallet,"WalletPayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        string id=state.TopUpRequests.Single().Id;
        Check(state.HasPendingTopUp && state.EuroBalance=="0,00 €" && !Get<Button>(wallet,"WalletPayButton").IsEnabled,
            "Native creation leaves the wallet unchanged and prevents a second pending request.");
        Check(Get<ItemsControl>(wallet,"TopUpRequestsList").Items.Count==1 && state.PaymentUrlFor(id) is null,
            "The pending reference is displayed and the offline preview has no external payment URL.");
        Capture(shell,Path.Combine(directory,"funding-wallet-pending-fr.png"));
        Get<Button>(wallet,"WalletAdminButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(state.IsShopAdminOpen && admin.IsVisible && !wallet.IsVisible && !Get<ScrollViewer>(shop,"PageScroll").IsVisible,"Administration is exclusive with all customer pages.");
        ListBox requests=Get<ListBox>(admin,"AdminRequestList");
        requests.SelectedItem=state.AdminTopUps.Single(row=>row.Id==id); await Pump();
        Check(state.HasAdminSelection && state.IsApprovalAction && !Get<Button>(admin,"AdminSubmitButton").IsEnabled,"Approval requires an explicit provider reference, amount, note and verification.");
        Get<TextBox>(admin,"AdminTransactionInput").Text="DEMO000000000042";
        Get<TextBox>(admin,"AdminReceivedInput").Text="19";
        Get<TextBox>(admin,"AdminNoteInput").Text="Paiement fictif biens et services reçu, référence Atlas vérifiée.";
        Get<CheckBox>(admin,"AdminPaymentVerifiedCheck").IsChecked=true; await Pump();
        Check(!Get<Button>(admin,"AdminSubmitButton").IsEnabled,"A mismatched amount cannot be approved.");
        Get<TextBox>(admin,"AdminReceivedInput").Text="20"; await Pump();
        foreach(string field in new[]{"AdminTransactionInput","AdminReceivedInput"})
        {
            TextBox input=Get<TextBox>(admin,field);
            ScrollContentPresenter viewport=Descendants(input).OfType<ScrollContentPresenter>().Single();
            Rect character=input.GetRectFromCharacterIndex(0);
            Point top=input.TranslatePoint(character.TopLeft,viewport);
            Check(!character.IsEmpty && top.Y>=-1 && top.Y+character.Height<=viewport.ActualHeight+1,
                $"The complete text line in {field} fits its native content viewport without vertical clipping.");
        }
        Check(Get<Button>(admin,"AdminSubmitButton").IsEnabled,"The complete review enables explicit approval.");
        Capture(shell,Path.Combine(directory,"funding-admin-approval-fr.png"));
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); await Pump();
        Check(state.AdminReceivedAmount=="20" && state.AdminPaymentVerified && Get<Button>(admin,"AdminSubmitButton").Content.ToString()=="Approve and credit",
            "Switching language preserves review fields and renders the English action.");
        Capture(shell,Path.Combine(directory,"funding-admin-approval-en.png"));
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); await Pump();
        Get<Button>(admin,"AdminSubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(state.EuroBalance=="20,00 €" && state.TopUpRequests.Single().Request.Status=="credited" && !state.HasAdminSelection,
            "Approval refreshes server-backed state and clears the old decision form.");
        Get<Button>(admin,"AdminBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Capture(shell,Path.Combine(directory,"funding-wallet-credited-fr.png"));
        Get<Button>(wallet,"WalletAdminButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Get<ComboBox>(admin,"AdminStatusPicker").SelectedItem=state.AdminStatusFilters.Single(row=>row.Id=="credited"); await Pump();
        requests.SelectedItem=state.AdminTopUps.Single(row=>row.Id==id); await Pump();
        Check(state.IsDisputeAction,"A credited request offers dispute management.");
        Get<TextBox>(admin,"AdminCaseInput").Text="PP-DEMO-20260911";
        Get<TextBox>(admin,"AdminNoteInput").Text="Litige fictif ouvert après livraison. Solde concerné à geler."; await Pump();
        Check(Get<Button>(admin,"AdminSubmitButton").IsEnabled,"A documented dispute can be recorded.");
        Capture(shell,Path.Combine(directory,"funding-admin-dispute-fr.png"));
        Get<Button>(admin,"AdminSubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(state.EuroBalance=="0,00 €" && state.HeldFunds=="20,00 €" && state.HasHeldFunds,"The disputed amount is held, not silently removed.");
        HwndSource hwnd=(HwndSource)PresentationSource.FromVisual(shell);
        shell.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,hwnd,Environment.TickCount,Key.Escape) { RoutedEvent=Keyboard.PreviewKeyDownEvent }); await Pump();
        Check(state.IsWalletOpen && !state.IsShopAdminOpen,"Escape returns administration to the wallet.");
        Capture(shell,Path.Combine(directory,"funding-wallet-held-fr.png"));
        Get<Button>(wallet,"WalletAdminButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Get<ComboBox>(admin,"AdminStatusPicker").SelectedItem=state.AdminStatusFilters.Single(row=>row.Id=="disputed"); await Pump();
        requests.SelectedItem=state.AdminTopUps.Single(row=>row.Id==id); await Pump();
        Get<ComboBox>(admin,"AdminActionPicker").SelectedItem=state.AdminActions.Single(row=>row.Id=="refund-confirmed");
        Get<TextBox>(admin,"AdminNoteInput").Text="Remboursement fictif constaté dans PayPal. Correction du portefeuille uniquement."; await Pump();
        Check(!Get<Button>(admin,"AdminSubmitButton").IsEnabled,"Refund registration requires explicit external-refund confirmation.");
        Get<CheckBox>(admin,"AdminRefundVerifiedCheck").IsChecked=true; await Pump();
        Check(Get<Button>(admin,"AdminSubmitButton").IsEnabled && state.AdminAudit.Contains("PP-DEMO-20260911"),"The refund form retains the dispute audit.");
        Capture(shell,Path.Combine(directory,"funding-admin-refund-fr.png"));
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); await Pump();
        Capture(shell,Path.Combine(directory,"funding-admin-refund-en.png"));
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); await Pump();
        Check(Get<ScrollViewer>(admin,"AdminDetailScroll").ScrollableWidth==0,"The administration has no horizontal scrolling at the actual launcher size.");
        Button submit=Get<Button>(admin,"AdminSubmitButton");
        Rect bounds=new(submit.TranslatePoint(new Point(),shop),submit.RenderSize);
        Check(bounds.Bottom<=shop.ActualHeight && bounds.Right<=shop.ActualWidth,"The decision button stays visible outside the scrolling form.");
        Get<Button>(admin,"AdminSubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(state.EuroBalance=="0,00 €" && !state.HasHeldFunds && state.TopUpRequests.Single().Request.Status=="refunded",
            "A confirmed refund clears the related hold and updates the request status.");
        Get<Button>(admin,"AdminBackButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Get<Button>(wallet,"WalletHistoryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(state.HistoryRows.Count==2 && state.HistoryRows.Any(row=>row.Transaction.Kind=="payment-reversal"),"Both the original delivery and payment reversal remain in history.");
        Capture(shell,Path.Combine(directory,"funding-history-reversal-fr.png"));
        state.ResetSession(); await Pump();
        Get<Button>(wallet,"WalletAdminButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(!state.IsShopAdminOpen && !state.CanAdministerFunding && state.AdminTopUps.Count==0 && state.TopUpRequests.Count==0
            && state.AdminTransactionId.Length==0 && state.AdminAudit.Length==0 && !state.CanRefresh,"Logout clears all private funding data and disconnects the offline fixture.");
    }
}
