using System.Net;
using System.Text.Json;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Presentation;

internal static class ShopRefreshTests
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); ++checks; }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ShopOrder order = new(Guid.NewGuid().ToString("N"), "character-rename", 0, "", "credits", 700, "available", null, now, now, Guid.NewGuid().ToString("N"));
        ShopSnapshot current = ShopRuntimeTests.Snapshot with { CheckoutAvailable = true, CreditBalanceEuroCents = 10000,
            EuroBalanceCents = 2000, Purchases = new(true, [order], true), Conversions = new(true, []), History = [] };
        TaskCompletionSource<ShopSnapshot>? delayed = null;
        bool delayNext = false;
        using ShopUiState state = new();
        state.Configure(_ => delayNext ? (delayNext = false, delayed = new(TaskCreationOptions.RunContinuationsAsynchronously)).Item2.Task
            : Task.FromResult(JsonSerializer.Deserialize<ShopSnapshot>(JsonSerializer.Serialize(current))!));
        state.ConfigurePurchases(new((request, _) => Task.FromResult(order), (_, _) => throw new NotSupportedException()));
        state.ConfigureConversions(new((request, _) =>
        {
            ShopGoldConversion receipt = new(Guid.NewGuid().ToString("N"), request.IdempotencyKey, request.CharacterGuid, "Asteria",
                request.OfferedCopper, request.ExpectedCreditCents, "completed", null, 4235067, 4135067, 10000, 10010, now, now);
            current = current with { Characters = [current.Characters[0] with { GoldCopper = 4135067 }, current.Characters[1]], CreditBalanceEuroCents = 10010, Conversions = new(true, [receipt]) };
            return Task.FromResult(receipt);
        }));
        await state.RefreshAsync();
        foreach (ShopOfferRow offer in state.Offers)
        {
            state.OpenService(offer);
            Check(state.UsesAccountService && !state.ShowPurchaseCharacter && state.SelectedCharacter is null, "Every native service selects its beneficiary in game.");
            if (offer.Offer.Id != "character-rename") Check(!state.CanPurchase, "Presentation cannot activate an unavailable realm service.");
        }
        state.OpenService(state.Offers[0]);
        var offers = state.Offers; var characters = state.Characters; var prices = state.Prices;
        var orders = state.PurchaseOrderRows; var history = state.FilteredHistory; var topups = state.TopUpRequests;
        for (int i = 0; i < 3; i++) await state.PollPurchaseAsync();
        Check(ReferenceEquals(offers, state.Offers) && ReferenceEquals(characters, state.Characters) && ReferenceEquals(prices, state.Prices)
            && ReferenceEquals(orders, state.PurchaseOrderRows) && ReferenceEquals(history, state.FilteredHistory) && ReferenceEquals(topups, state.TopUpRequests),
            "Identical deserialized polls preserve ItemsSource and row identities.");
        state.OpenConversion(); state.ConversionGold = "10";
        delayNext = true; Task poll = state.PollPurchaseAsync();
        Check(state.IsPurchaseReading && state.CanEditConversion && state.CanChooseConversionCharacter && state.CanConvert,
            "A slow background read cannot disable conversion input or its action.");
        state.ConversionGold = "12";
        Check(state.ConversionGold == "12", "Typing continues during a slow poll.");
        state.ConversionGold = "10";
        ShopSnapshot stale = current;
        Check(await state.ConvertAsync(), "A conversion can supersede a background read and complete normally.");
        delayed!.SetResult(stale); await poll;
        Check(state.AvailableCopper == 4135067 && state.CreditBalance == ShopUiState.FormatEuros(10010),
            "A cancelled stale read cannot overwrite the authoritative debit and credit.");
        var sourceRow = state.SelectedConversionCharacter;
        current = current with { Characters = [current.Characters[0] with { Name = "Renamed" }, current.Characters[1]] };
        await state.PollPurchaseAsync();
        Check(ReferenceEquals(sourceRow, state.SelectedConversionCharacter) && sourceRow?.Label.StartsWith("Renamed") == true,
            "Changed character data updates its existing selected row.");
        delayNext = true; poll = state.PollPurchaseAsync(); state.ResetSession(); delayed!.SetResult(current); await poll;
        Check(!state.HasOffers && !state.HasPurchaseOrders && !state.HasCharacters, "A late poll after logout cannot restore account data.");
        state.Configure(_ => Task.FromResult(current)); await state.RefreshAsync();
        state.Configure(_ => Task.FromException<ShopSnapshot>(new UnauthorizedAccessException())); await state.PollPurchaseAsync();
        Check(!state.HasOffers && state.ShowStatus, "An expired session clears existing account data during polling.");
        Console.WriteLine($"Shop refresh PASS: {checks} checks; stable lists, uninterrupted editing, stale-read cancellation, account services and session isolation.");
        return 0;
    }
}
