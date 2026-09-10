using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Server;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class ShopRuntimeTests
{
    private static int _checks;
    internal static ShopSnapshot Snapshot => new ShopCatalog().CreateSnapshot(
        [new(101, "Asteria", 80, false, 4_235_067), new(202, "Boréal", 70, true, null)]);

    internal static async Task<int> RunAsync()
    {
        string locale = LauncherLocalization.CurrentLocale;
        try
        {
            _checks = 0;
            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            ShopSnapshot snapshot = Snapshot;
            snapshot.Validate();
            Check(snapshot.Offers.Single().Prices.SequenceEqual(new ShopPrice[] { new("credits", 500), new("eur", 500) }), "Approved prices: EUR 5 from either Atlas credits or the euro wallet.");
            Check(!snapshot.CheckoutAvailable && snapshot.CreditBalanceEuroCents is null && snapshot.EuroBalanceCents is null, "Checkout closed and both wallets unknown.");
            Check(new ShopCatalog(600).CreateSnapshot([]).CatalogRevision != snapshot.CatalogRevision, "Price change changes revision.");
            foreach (ShopSnapshot invalid in new[]
            {
                snapshot with { SchemaVersion = 1 }, snapshot with { CreditBalanceEuroCents = -1 }, snapshot with { EuroBalanceCents = -1 }, snapshot with { Offers = null! },
                snapshot with { CreditBalanceEuroCents = ShopSnapshot.MaximumBalanceCents + 1 }, snapshot with { EuroBalanceCents = ShopSnapshot.MaximumBalanceCents + 1 },
                snapshot with { Offers = [snapshot.Offers[0] with { Prices = [new("gold", 3_000_000)] }] },
                snapshot with { Characters = [snapshot.Characters[0], snapshot.Characters[0]] },
                snapshot with { Characters = [snapshot.Characters[1] with { GoldCopper = 300 }] },
                snapshot with { Offers = [snapshot.Offers[0] with { Prices = [new("eur", -500)] }] },
                snapshot with { Offers = [snapshot.Offers[0] with { Prices = [new("eur", 500), new("eur", 600)] }] },
                snapshot with { Offers = [snapshot.Offers[0] with { Name = null! }] }
            }) Throws<InvalidDataException>(invalid.Validate);

            Check(snapshot.GoldConversion.CopperPerEuroCent == 10_000
                && ShopPreviewData.Create().GoldConversion == snapshot.GoldConversion, "Server and preview both use 100 gold per euro.");
            foreach (uint gold in new uint[] { 1, 10, 80, 100, 212, 400, 500, 429_496 })
                Check(snapshot.GoldConversion.Quote(gold * 10_000) == new ShopGoldConversionQuote(gold, gold * 10_000, 0),
                    $"{gold} whole gold gives exactly {gold} cents and consumes no fractional gold.");
            Check(snapshot.GoldConversion.Quote(9_999) == new ShopGoldConversionQuote(0, 0, 9_999), "Silver and copper alone are never converted.");
            ShopGoldConversionQuote maximum = snapshot.GoldConversion.Quote(uint.MaxValue);
            Check(maximum.DebitedCopper + (long)maximum.RemainingCopper == uint.MaxValue && maximum.RemainingCopper < 10_000,
                "Conversion preserves all fractional gold up to the game money limit.");

            Callback handler = new();
            using HttpClient http = new(handler);
            LauncherShopApiClient client = new(http, new Uri("https://shop.fixture.invalid/api/v1/"));
            handler.Read = request =>
            {
                Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v1/shop" && request.RequestUri.Query == "", "Fixed read route; no account override or mutation.");
                return Json(snapshot);
            };
            Check((await client.ReadAsync(default)).Offers.Count == 1, "Client reads the server contract.");
            handler.Read = _ => new(HttpStatusCode.Unauthorized);
            await ThrowsAsync<UnauthorizedAccessException>(() => client.ReadAsync(default));
            handler.Read = _ => new(HttpStatusCode.NotFound);
            await ThrowsAsync<HttpRequestException>(() => client.ReadAsync(default));
            handler.Read = _ => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', ShopSnapshot.MaximumResponseBytes + 1)) };
            await ThrowsAsync<InvalidDataException>(() => client.ReadAsync(default));
            handler.Read = _ => new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[ShopSnapshot.MaximumResponseBytes + 1])) };
            await ThrowsAsync<InvalidDataException>(() => client.ReadAsync(default));
            handler.Read = _ => new(HttpStatusCode.OK) { Content = new StringContent("null") };
            await ThrowsAsync<InvalidDataException>(() => client.ReadAsync(default));
            handler.Read = _ => Json(snapshot with { SchemaVersion = 99 });
            await ThrowsAsync<InvalidDataException>(() => client.ReadAsync(default));
            handler.Read = _ => new(HttpStatusCode.OK) { Content = new StringContent("{") };
            await ThrowsAsync<JsonException>(() => client.ReadAsync(default));

            using ShopUiState state = new();
            state.Configure(_ => Task.FromResult(snapshot));
            await state.RefreshAsync();
            Check(state.HasOffers && !state.ShowStatus && state.SelectedCharacter is null && state.CreditBalance == "—", "Ready view has explicit beneficiary selection and unknown wallet.");
            state.ConversionGold = "212";
            Check(state.ConversionCredit == "2,12 €" && state.ConversionDebit == "212", "Numeric-only amounts follow the server rate.");
            state.ConversionGold = "0,8";
            Check(state.ConversionQuote is null, "Gold entry excludes silver and copper.");
            foreach (string invalid in new[] { "", "-1", "NaN", "1e3", "212,00001", "429496.7296", "1 000", "1,2.3" })
            { state.ConversionGold = invalid; Check(state.ConversionQuote is null, "Invalid amount rejected without floating-point rounding."); }
            state.Configure(_ => Task.FromResult(snapshot with { CreditBalanceEuroCents = 265 })); await state.RefreshAsync();
            Check(state.CreditBalance == "2,65 €", "Atlas balance is denominated in euro cents.");
            state.Configure(_ => Task.FromResult(snapshot));
            state.SelectedCharacter = state.Characters[0];
            state.SelectedPrice = state.Prices.Single(p => p.Price.Currency == "eur");
            Check(state.Summary.Contains("Asteria") && state.Summary.Contains("5,00 €"), "Summary uses selected character and wallet price.");
            Check(state.CharacterHint.EndsWith("423"), "Offline gold is displayed as whole gold coins.");
            await state.RefreshAsync();
            Check(state.SelectedCharacter?.Character.Guid == 101 && state.SelectedPrice?.Price.Currency == "eur", "Refresh preserves existing beneficiary and currency.");
            state.SelectedCharacter = state.Characters[1];
            Check(state.CharacterHint.Contains("En ligne") && !state.CharacterHint.Contains("4 235"), "Online character never reuses offline gold.");
            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); state.RefreshLocale();
            Check(state.OfferName == "Name change" && state.SelectedCharacter?.Character.Guid == 202 && state.PriceLabel == "Euro wallet · 5.00 €", "Language change preserves selection and translates the wallet.");
            Check(!state.CanPurchase, "Browsing milestone never initiates a purchase.");
            state.Configure(_ => Task.FromResult(snapshot with { Characters = [] })); await state.RefreshAsync();
            Check(!state.HasCharacters && state.SelectedCharacter is null, "Removed character clears beneficiary.");
            state.Configure(_ => Task.FromResult(snapshot with { Offers = [] })); await state.RefreshAsync();
            Check(!state.HasSelection && state.ShowStatus, "Empty catalog clears stale detail.");
            foreach (Exception error in new Exception[] { new HttpRequestException("fixture", null, HttpStatusCode.NotFound), new UnauthorizedAccessException(), new LauncherAuthException("fixture", HttpStatusCode.Unauthorized), new TaskCanceledException(), new JsonException(), new IOException("fixture stream interrupted") })
            {
                state.Configure(_ => Task.FromException<ShopSnapshot>(error)); await state.RefreshAsync();
                Check(state.ShowStatus && !state.IsLoading && !state.HasOffers, "Network/auth/format failure clears sensitive state and leaves retry possible.");
            }
            TaskCompletionSource<ShopSnapshot> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Configure(_ => late.Task); Task pending = state.RefreshAsync();
            state.ResetSession();
            state.Configure(_ => Task.FromResult(snapshot with { Characters = [new(303, "Newaccount", 20, false, 0)] }));
            await state.RefreshAsync(); late.SetResult(snapshot); await pending;
            Check(state.Characters.Single().Character.Guid == 303, "Late account response cannot replace the reconnected account.");
            state.ResetSession(); Check(!state.HasOffers && !state.HasCharacters && state.CreditBalance == "—", "Logout removes every account value.");
            await VerifyConversionAsync(snapshot);
            Console.WriteLine($"Shop runtime PASS: {_checks} assertions; two wallets, character gold limits, numeric precision, preview credit/debit conservation, production gate, response bounds and account isolation. Fake HTTP only.");
            return 0;
        }
        finally { LauncherLocalization.SetLocale(locale); }
    }

    private static async Task VerifyConversionAsync(ShopSnapshot catalog)
    {
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        ShopSnapshot preview = catalog with { CreditBalanceEuroCents = 265, EuroBalanceCents = 1000,
            Characters = [catalog.Characters[0], new(202, "Boréal", 70, false, 1_208_000), new(303, "Elune", 80, true, null), new(404, "Empty", 1, false, 0)] };
        using ShopUiState state = new();
        state.ConfigurePreview(preview); await state.RefreshAsync();
        int notifications = 0; ShopCreditChange? granted = null;
        state.CreditGranted += change => { notifications++; granted = change; };
        state.OpenConversion();
        Check(state.SelectedConversionCharacter?.Character.Guid == 101 && state.SelectedCharacter is null, "Converter defaults to an eligible source independently of the purchase beneficiary.");
        state.ConversionGold = "424";
        Check(!state.CanConvert && !state.TryConvertPreview(), "Overdraw cannot credit or debit anything.");
        state.SetConversionPercent(100);
        Check(state.ConversionGold == "423" && state.ConversionCredit == "4,23 €" && state.ConversionGoldAfter == "0", "Max converts all whole gold and leaves the original silver and copper.");
        state.SelectedConversionCharacter = state.Characters[1];
        Check(state.ConversionGold == "120" && state.ConversionMaximum == "120", "Changing to a poorer character clamps the amount to that character's maximum.");
        state.SetConversionPercent(25);
        Check(state.ConversionGold == "30" && state.RequestedCopper <= state.AvailableCopper, "Percent shortcuts use whole gold coins.");
        state.SelectedConversionCharacter = state.Characters[2];
        Check(!state.CanPreviewConversion && !state.CanConvert && state.ConversionMaximum == "—", "Online gold is unknown and cannot be spent.");
        state.SelectedConversionCharacter = state.Characters[3]; state.SetConversionPercent(100);
        Check(!state.HasConvertibleGold && !state.CanConvert && state.ConversionGold == "0", "Empty characters cannot convert.");
        state.SelectedConversionCharacter = state.Characters[0];
        foreach (string invalid in new[] { "-1", "+1", "1e3", "1 000", "NaN", "212 po", "1,2.3", "1.00001", "0,8", "423.5067", "\n212", "212\n" })
        {
            Check(!ShopUiState.IsNumericGoldInput(invalid), "Letters, signs, exponent, whitespace and excess precision cannot be typed or pasted.");
            state.ConversionGold = invalid; Check(!state.CanConvert, "Invalid externally assigned text is also rejected.");
        }
        foreach (string valid in new[] { "", "212", "0", "423" }) Check(ShopUiState.IsNumericGoldInput(valid), "Numeric entry permits editing whole gold only.");
        state.ConversionGold = "0,7999"; Check(!state.CanConvert, "A sub-cent amount cannot convert.");
        state.ConversionGold = "212";
        Check(state.CanConvert && state.ConversionBalanceAfter == "4,77 €" && state.ConversionGoldAfter == "211", "Quote previews the exact resulting balances.");
        Check(state.TryConvertPreview() && state.IsConversionOpen && !state.TryConvertPreview(), "One click commits exactly once and stays in the converter.");
        Check(notifications == 1 && granted == new ShopCreditChange(265, 477, 101, 2_120_000), "Animation is notified only after a successful credit.");
        Check(state.CreditBalance == "4,77 €" && state.EuroBalance == "10,00 €", "Only Atlas credits increase; the euro wallet is unchanged.");
        Check(state.HasConversionReceipt && state.ConversionCredit == "2,12 €" && state.ConversionBalanceBefore == "2,65 €"
            && state.ConversionBalanceAfter == "4,77 €" && state.ConversionGoldAfter == "211", "The retained receipt describes the completed conversion.");
        state.ConversionGold = "100";
        Check(!state.HasConversionReceipt && state.CanConvert && state.ConversionCredit == "1,00 €", "Another amount clears the receipt and prepares a fresh conversion.");
        await state.RefreshAsync(); state.OpenConversion();
        Check(state.AvailableCopper == 2_115_067 && state.Characters[1].Character.GoldCopper == 1_208_000 && state.CreditBalance == "4,77 €", "Refresh preserves the demo ledger and only the selected character was debited.");
        state.Configure(_ => Task.FromResult(preview)); await state.RefreshAsync(); state.OpenConversion(); state.ConversionGold = "212";
        Check(state.HasValidConversionAmount && !state.CanConvert && !state.TryConvertPreview() && notifications == 1, "Real API mode never enables the in-memory demo or fires a credit animation.");
        foreach (uint gold in new uint[] { 1, 10 })
        {
            state.ConfigurePreview(preview); await state.RefreshAsync(); state.OpenConversion(); state.ConversionGold = gold.ToString();
            Check(state.TryConvertPreview() && state.AvailableCopper == preview.Characters[0].GoldCopper - gold * 10_000
                && state.CreditBalance == ShopUiState.FormatEuros(265 + gold) && state.IsConversionOpen,
                "Reported small conversions debit the full whole-gold input, add the exact cents and stay on the result.");
        }
        state.ConfigurePreview(preview with { Characters = [new(505, "Silver", 1, false, 9_999)] });
        await state.RefreshAsync(); state.OpenConversion(); state.SetConversionPercent(100);
        Check(state.ConversionMaximum == "0" && state.ConversionGold == "0" && !state.HasConvertibleGold && !state.CanConvert,
            "Silver and copper alone do not enable conversion or round up to gold.");
        state.ConfigurePreview(preview with { CreditBalanceEuroCents = ShopSnapshot.MaximumBalanceCents });
        Check(!state.HasCharacters && !state.CanConvert, "Entering preview clears all previous account data before loading.");
        await state.RefreshAsync(); state.OpenConversion(); state.ConversionGold = "212";
        Check(!state.CanConvert, "Wallet ceiling is checked before crediting.");
        state.ConfigurePreview(preview); await state.RefreshAsync(); state.OpenConversion(); state.ConversionGold = "212";
        state.ResetSession(); await state.RefreshAsync();
        Check(!state.IsConversionOpen && !state.CanConvert && !state.CanRefresh && state.EuroBalance == "—" && state.ConversionGold == "", "Logout ends the preview capability, clears both wallets and cancels conversion.");
    }

    private static HttpResponseMessage Json(ShopSnapshot value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Callback : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, HttpResponseMessage> Read = _ => throw new InvalidOperationException();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Read(request));
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
}
