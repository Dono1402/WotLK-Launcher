using System.IO;
using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ShopWpfTests
{
    private static async Task VerifyAccountServicesAsync(LauncherShellV2 shell, ShopViewV2 shop, string captures)
    {
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        ShopSnapshot snapshot = ShopRuntimeTests.Snapshot with
        {
            CheckoutAvailable = true, EuroBalanceCents = 1500, CreditBalanceEuroCents = 1400,
            Offers = ShopServiceCatalog.CreateOffers(accountServices: true),
            Characters = ShopRuntimeTests.Snapshot.Characters.Select(c => c with { Online = true, GoldCopper = null }).ToArray(),
            Purchases = new(true, [], AccountServices: true), History = []
        };
        shop.State.Configure(_ => Task.FromResult(snapshot));
        int sends = 0;
        shop.State.ConfigurePurchases(new((input, _) =>
        {
            sends++;
            Check(input.CharacterGuid == 0, "The account-service button submits no beneficiary.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ShopOrder order = new(Guid.NewGuid().ToString("N"), input.OfferId, 0, "", input.Currency,
                input.ExpectedAmountCents, "available", null, now, now, input.IdempotencyKey);
            snapshot = snapshot with { EuroBalanceCents = snapshot.EuroBalanceCents - input.ExpectedAmountCents,
                Purchases = new(true, new[] { order }.Concat(snapshot.Purchases!.Orders).ToArray(), AccountServices: true) };
            return Task.FromResult(order);
        }, (id, _) =>
        {
            ShopOrder order = snapshot.Purchases!.Orders.Single(o => o.Id == id) with { Status = "refunded", Reason = "cancelled" };
            snapshot = snapshot with { EuroBalanceCents = snapshot.EuroBalanceCents + order.AmountCents,
                Purchases = snapshot.Purchases with { Orders = snapshot.Purchases.Orders.Select(o => o.Id == id ? order : o).ToArray() } };
            return Task.FromResult(order);
        }));
        await shop.State.RefreshAsync(); shop.State.OpenService(shop.State.Offers.Single(o => o.Offer.Id == "character-rename")); await Pump();
        Button buy = Get<Button>(shop, "PurchaseButton");
        Check(Get<ComboBox>(shop, "CharacterPicker").Visibility == Visibility.Collapsed && buy.IsEnabled,
            "The account mode hides beneficiary selection and permits purchase while characters are online.");
        Check(Get<TextBlock>(shop, "SummaryText").Text.Contains("compte"), "The footer clearly identifies purchase for the account.");
        shell.Width = 1280; shell.Height = 760; await Pump();
        Capture(shell, Path.Combine(captures, "account-service-ready-fr-1280.png"));
        buy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 30 && shop.State.IsPurchasing; i++) await Pump();
        Check(sends == 1 && shop.State.AvailableServiceCount == 1 && buy.IsEnabled && shop.State.CanCancelPurchase,
            "A successful account purchase shows stock and permits a separate additional purchase.");
        Check(Get<TextBlock>(shop, "EligibilityTitleText").Text.Contains("1") && shop.State.PurchaseReceipt.Contains("compte"),
            "The WPF receipt and stock count identify the available account service.");
        Capture(shell, Path.Combine(captures, "account-service-available-fr-1280.png"));
        ShopOrder available = snapshot.Purchases!.Orders[0];
        snapshot = snapshot with { Purchases = snapshot.Purchases with { Orders = [available with {
            Status = "consumed", CharacterGuid = 101, CharacterName = "Asteria", AppliedName = "Selene" }] } };
        await shop.State.RefreshPurchaseAsync(); await Pump();
        Check(shop.State.AvailableServiceCount == 0 && !shop.State.CanCancelPurchase && shop.State.PurchaseReceipt.Contains("Asteria → Selene"),
            "A consumed receipt removes the available service and records the applied name without offering cancellation.");
        LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); await Pump();
        Check(shop.State.PurchaseReceipt.Contains("Service used") && shop.State.PurchaseHint.Contains("keep playing"),
            "The account service flow is translated into English.");
        shell.Width = 1586; shell.Height = 992; await Pump();
        Capture(shell, Path.Combine(captures, "account-service-used-en-1586.png"));
        snapshot = snapshot with { Characters = [] }; await shop.State.RefreshPurchaseAsync(); await Pump();
        Check(buy.IsEnabled && shop.State.SelectedCharacter is null, "An account with no remaining characters can still buy an unassigned service.");
        buy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 30 && shop.State.IsPurchasing; i++) await Pump();
        Get<Button>(shop, "CancelPurchaseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (int i = 0; i < 30 && shop.State.IsPurchasing; i++) await Pump();
        Check(shop.State.AvailableServiceCount == 0 && snapshot.EuroBalanceCents == 1000,
            "WPF cancellation restores the unused service's funds even without any characters.");
        shop.State.ResetSession(); await Pump();
        Check(!buy.IsEnabled && !shop.State.HasPurchaseOrders, "Logout removes all account-service receipts and stock.");
    }
}
