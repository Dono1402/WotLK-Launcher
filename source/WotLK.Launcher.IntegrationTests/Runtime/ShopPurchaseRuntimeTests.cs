using System.IO;
using System.Net.Http;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Presentation;

internal static class ShopPurchaseRuntimeTests
{
    internal static async Task<int> VerifyAsync()
    {
        int checks = 0;
        ShopSnapshot snapshot = ShopRuntimeTests.Snapshot with { CheckoutAvailable = true, EuroBalanceCents = 10000, CreditBalanceEuroCents = 10000,
            Purchases = new(true, []), Characters = ShopRuntimeTests.Snapshot.Characters.Select(c => c with { RenamePending = false }).ToArray() };
        using ShopUiState state = new();
        int reads = 0;
        state.Configure(_ => ++reads == 1 ? Task.FromResult(snapshot) : throw new HttpRequestException("Fixture: snapshot unavailable after commit"));
        state.ConfigurePurchases(new((input, _) => Task.FromResult(Receipt(input)), (_, _) => throw new InvalidOperationException("Unexpected cancellation")));
        await state.RefreshAsync(); SelectRename();
        Check(state.CanPurchase, "Funded, eligible rename is enabled.");
        state.SelectedOffer = state.Offers.Single(o => o.Offer.Id == "character-race-change");
        Check(!state.CanPurchase, "Global checkout availability never enables another service.");
        state.SelectedOffer = state.Offers.Single(o => o.Offer.Id == "character-rename");
        await state.PurchaseAsync();
        Check(state.HasPurchaseOrders && state.CanCancelPurchase && state.EuroBalance == "—" && state.SelectedPrice?.MissingCents is null,
            "A confirmed receipt survives a failed snapshot refresh; the affected balance becomes unknown instead of displaying stale funds.");
        Check(!state.CanPurchase, "A missing follow-up read cannot unlock another purchase of the same entitlement.");

        TaskCompletionSource<ShopOrder> delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShopCreateOrder? sent = null;
        state.Configure(_ => Task.FromResult(snapshot));
        state.ConfigurePurchases(new((input, _) => { sent = input; return delayed.Task; }, (_, _) => throw new InvalidOperationException()));
        await state.RefreshAsync(); SelectRename();
        Task purchase = state.PurchaseAsync();
        Check(state.IsPurchasing && !state.CanPurchase, "An in-flight mutation disables further requests.");
        state.ResetSession(); delayed.SetResult(Receipt(sent!)); await purchase;
        Check(!state.HasOffers && !state.HasPurchaseOrders && state.EuroBalance == "—" && state.PurchaseReceipt.Length == 0,
            "A late purchase response cannot restore any data from a closed account session.");

        TaskCompletionSource<ShopSnapshot> delayedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        reads = 0;
        state.Configure(_ => ++reads == 1 ? Task.FromResult(snapshot) : delayedRead.Task);
        state.ConfigurePurchases(new((_, _) => throw new InvalidOperationException("Read/purchase overlap"), (_, _) => throw new InvalidOperationException()));
        await state.RefreshAsync(); SelectRename();
        Task refresh = state.RefreshPurchaseAsync();
        Check(state.IsPurchaseReading && !state.CanPurchase && !state.CanRefresh, "An order-status read cannot overlap a purchase or another refresh.");
        await state.PurchaseAsync(); state.ResetSession(); delayedRead.SetResult(snapshot); await refresh;
        Check(!state.HasOffers && !state.HasPurchaseOrders && !state.IsPurchaseReading, "A late status read is ignored after logout, even if the transport ignored cancellation.");
        try { new ShopPurchases(true, [null!]).Validate(); throw new Exception("Malformed order list accepted"); }
        catch (InvalidDataException) { checks++; }
        checks += await VerifyAccountServicesAsync();
        return checks;

        void SelectRename()
        { state.OpenService(state.Offers.Single(o => o.Offer.Id == "character-rename")); state.SelectedCharacter = state.Characters.Single(c => c.Character.Guid == 101); }
        static ShopOrder Receipt(ShopCreateOrder input)
        { DateTimeOffset now = DateTimeOffset.UtcNow; return new(Guid.NewGuid().ToString("N"), input.OfferId, input.CharacterGuid, "Asteria", input.Currency, input.ExpectedAmountCents, "pending", null, now, now, input.IdempotencyKey); }
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
    }

    private static async Task<int> VerifyAccountServicesAsync()
    {
        int checks = 0, reads = 0;
        ShopSnapshot snapshot = ShopRuntimeTests.Snapshot with { CheckoutAvailable = true, EuroBalanceCents = 1000,
            Purchases = new(true, [], AccountServices: true), Characters = [] };
        using ShopUiState state = new();
        state.Configure(_ => ++reads == 1 ? Task.FromResult(snapshot) : throw new HttpRequestException("Fixture: read unavailable after commit"));
        state.ConfigurePurchases(new((input, _) =>
        {
            Check(input.CharacterGuid == 0, "Account purchases send no implicit beneficiary.");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ShopOrder(Guid.NewGuid().ToString("N"), input.OfferId, 0, "", input.Currency,
                input.ExpectedAmountCents, "available", null, now, now, input.IdempotencyKey));
        }, (_, _) => throw new InvalidOperationException()));
        await state.RefreshAsync(); state.OpenService(state.Offers.Single(o => o.Offer.Id == "character-rename"));
        Check(state.CanPurchase && !state.ShowPurchaseCharacter, "A funded account can buy without any characters.");
        await state.PurchaseAsync();
        Check(state.UsesAccountService && state.AvailableServiceCount == 1 && state.CanCancelPurchase && !state.CanPurchase
            && state.EuroBalance == "—", "A failed refresh retains the account-mode receipt and makes the debited wallet unknown.");
        snapshot = snapshot with { EuroBalanceCents = 0 };
        state.Configure(_ => Task.FromResult(snapshot)); await state.RefreshAsync();
        state.OpenService(state.Offers.Single(o => o.Offer.Id == "character-rename"));
        Check(state.CanPrepareServiceFunding && state.SelectedCharacter is null, "An unfunded account can prepare a top-up without selecting a character.");
        state.PrepareServiceFunding();
        Check(state.IsWalletOpen && state.HasFundingReturn && state.FundingContextText.Length > 0,
            "Funding navigation preserves the account service and its price without inventing a beneficiary.");
        return checks;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
    }
}
