using System.IO;
using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShopWpfTests
{
    private static async Task VerifyPurchasesAsync(LauncherShellV2 shell, ShopViewV2 shop, string captures)
    {
        ShopSnapshot snapshot = ShopRuntimeTests.Snapshot with
        {
            CheckoutAvailable = true, EuroBalanceCents = 1000, CreditBalanceEuroCents = 1400,
            Characters = ShopRuntimeTests.Snapshot.Characters.Select(c => c with { RenamePending = false }).ToArray(),
            Purchases = new(true, []), History = []
        };
        int calls = 0;
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        shop.State.Configure(_ => Task.FromResult(snapshot));
        shop.State.ConfigurePurchases(new(async (input, _) =>
        {
            if (++calls == 1) await release.Task;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ShopOrder order = new(Guid.NewGuid().ToString("N"), input.OfferId, input.CharacterGuid, "Asteria", input.Currency,
                input.ExpectedAmountCents, "pending", null, now, now, input.IdempotencyKey);
            snapshot = snapshot with { EuroBalanceCents = snapshot.EuroBalanceCents - (input.Currency == "eur" ? input.ExpectedAmountCents : 0),
                CreditBalanceEuroCents = snapshot.CreditBalanceEuroCents - (input.Currency == "credits" ? input.ExpectedAmountCents : 0),
                Characters = snapshot.Characters.Select(c => c.Guid == input.CharacterGuid ? c with { RenamePending = true } : c).ToArray(),
                Purchases = new(true, new[] { order }.Concat(snapshot.Purchases!.Orders).ToArray()) };
            return order;
        }, (id, _) =>
        {
            ShopOrder order = snapshot.Purchases!.Orders.Single(o => o.Id == id);
            order = order with { Status = "refunded", Reason = "cancelled", UpdatedAtUtc = DateTimeOffset.UtcNow };
            snapshot = snapshot with { EuroBalanceCents = snapshot.EuroBalanceCents + (order.Currency == "eur" ? order.AmountCents : 0),
                CreditBalanceEuroCents = snapshot.CreditBalanceEuroCents + (order.Currency == "credits" ? order.AmountCents : 0),
                Characters = snapshot.Characters.Select(c => c.Guid == order.CharacterGuid ? c with { RenamePending = false } : c).ToArray(),
                Purchases = new(true, snapshot.Purchases.Orders.Select(o => o.Id == id ? order : o).ToArray()) };
            return Task.FromResult(order);
        }));
        await shop.State.RefreshAsync(); await Pump();
        ServiceButton(shop, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        ComboBox characters = Get<ComboBox>(shop, "CharacterPicker");
        ListBox currencies = Get<ListBox>(shop, "CurrencyChoices");
        Button buy = Get<Button>(shop, "PurchaseButton");
        Check(!buy.IsEnabled, "An enabled service still requires an explicit beneficiary.");
        characters.SelectedIndex = 0; currencies.SelectedIndex = 0; await Pump();
        Check(buy.IsEnabled && shop.State.SelectedAmount == "5,00 €", "The native purchase button uses the approved wallet price.");
        shell.Width = 1280; shell.Height = 760; await Pump();
        Capture(shell, Path.Combine(captures, "rename-ready-fr-1280.png"));
        buy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        buy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(calls == 1 && shop.State.IsPurchasing && !buy.IsEnabled && !characters.IsEnabled && !currencies.IsEnabled,
            "Two native click events submit once and lock recipient/currency while saving.");
        release.SetResult(true);
        for (int i = 0; i < 30 && shop.State.IsPurchasing; i++) await Pump();
        Check(!shop.State.IsPurchasing && shop.State.CanCancelPurchase && shop.State.PurchaseReceipt.Contains("Asteria")
            && Get<TextBlock>(shell.WalletControl, "EuroAmountText").Text == "5,00 €", "Native completion shows the receipt and refreshes the shared wallet header.");
        Check(characters.SelectedItem == shop.State.SelectedCharacter && characters.SelectedItem is not null
            && currencies.SelectedItem == shop.State.SelectedPrice && currencies.SelectedItem is not null,
            "Snapshot refresh preserves the actual WPF recipient and currency selection.");
        Capture(shell, Path.Combine(captures, "rename-pending-fr-1280.png"));
        Get<Button>(shop, "CancelPurchaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 30 && shop.State.IsPurchasing; i++) await Pump();
        Check(shop.State.PurchaseReceipt.Contains("Remboursé") && Get<TextBlock>(shell.WalletControl, "EuroAmountText").Text == "10,00 €",
            "Native cancellation refreshes the refunded receipt and restored wallet.");
        currencies.SelectedIndex = 1; await Pump();
        buy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 30 && shop.State.IsPurchasing; i++) await Pump();
        Check(calls == 2 && shop.State.CreditBalance == "7,00 €" && Get<TextBlock>(shell.WalletControl, "EuroAmountText").Text == "10,00 €",
            "The native credits choice debits credits without combining or changing the euro wallet.");
        ShopOrder pending = snapshot.Purchases!.Orders[0];
        snapshot = snapshot with { Purchases = new(true, snapshot.Purchases.Orders.Select(o => o.Id == pending.Id ? o with { Status = "delivered" } : o).ToArray()) };
        Get<Button>(shop, "RefreshPurchaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump();
        Check(!shop.State.CanCancelPurchase && shop.State.PurchaseReceipt.Contains("livrée"), "Delivered activations cannot be cancelled in the UI.");
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); await Pump();
        Check(shop.State.PurchaseReceipt.Contains("delivered") && shop.State.PurchaseHint.Contains("character selection"), "Delivered status and the next in-game step localize to English.");
        Capture(shell, Path.Combine(captures, "rename-delivered-en-1280.png"));
        shell.Width = 1586; shell.Height = 992; await Pump();
        Capture(shell, Path.Combine(captures, "rename-delivered-en-1586.png"));
        // A disappeared beneficiary must not hide a still-cancellable order.
        DateTimeOffset orphanTime = DateTimeOffset.UtcNow;
        ShopOrder orphan = new(Guid.NewGuid().ToString("N"), "character-rename", 101, "Asteria", "eur", 500,
            "pending", null, orphanTime, orphanTime, Guid.NewGuid().ToString("N"));
        snapshot = snapshot with { EuroBalanceCents = 500, Characters = snapshot.Characters.Where(c => c.Guid != 101).ToArray(),
            Purchases = new(true, new[] { orphan }.Concat(snapshot.Purchases.Orders).ToArray()) };
        await shop.State.RefreshPurchaseAsync(); shop.State.OpenHistory(); await Pump();
        Get<Expander>(shop.HistoryPage, "PurchaseOrdersExpander").IsExpanded = true; await Pump();
        Button cancelOrphan = Descendants(Get<ListBox>(shop.HistoryPage, "PurchaseOrdersList")).OfType<Button>()
            .Single(button => button.DataContext is ShopOrderRow row && row.Id == orphan.Id);
        Check(cancelOrphan.IsEnabled && shop.State.SelectedCharacter is null, "Order history retains cancellation after a beneficiary disappears.");
        shell.Width = 1280; shell.Height = 760; await Pump();
        Capture(shell, Path.Combine(captures, "rename-orders-en-1280.png"));
        cancelOrphan.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 30 && shop.State.IsPurchasing; i++) await Pump();
        Check(snapshot.EuroBalanceCents == 1000 && shop.State.PurchaseOrderRows.Single(row => row.Id == orphan.Id).Order.Status == "refunded",
            "Native order-history cancellation restores funds without a selectable character.");
        shop.State.ResetSession(); await Pump();
        Check(!buy.IsEnabled && !shop.State.HasPurchaseOrders && shop.State.PurchaseReceipt.Length == 0, "Logout erases purchase receipts and authorization.");
    }
}
