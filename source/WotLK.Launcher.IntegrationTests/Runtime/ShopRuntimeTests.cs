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
            Check(snapshot.Offers.Single().Prices.SequenceEqual(new ShopPrice[] { new("eur", 500), new("gold", 3_000_000) }), "Approved prices: 5 EUR or 300 gold, no implied credit conversion.");
            Check(!snapshot.CheckoutAvailable && snapshot.CreditBalanceEuroCents is null, "Checkout closed and wallet unknown.");
            Check(new ShopCatalog(600).CreateSnapshot([]).CatalogRevision != snapshot.CatalogRevision, "Price change changes revision.");
            foreach (ShopSnapshot invalid in new[]
            {
                snapshot with { SchemaVersion = 2 }, snapshot with { CreditBalanceEuroCents = -1 }, snapshot with { Offers = null! },
                snapshot with { Characters = [snapshot.Characters[0], snapshot.Characters[0]] },
                snapshot with { Characters = [snapshot.Characters[1] with { GoldCopper = 300 }] },
                snapshot with { Offers = [snapshot.Offers[0] with { Prices = [new("eur", -500)] }] },
                snapshot with { Offers = [snapshot.Offers[0] with { Prices = [new("eur", 500), new("eur", 600)] }] },
                snapshot with { Offers = [snapshot.Offers[0] with { Name = null! }] }
            }) Throws<InvalidDataException>(invalid.Validate);

            Check(snapshot.GoldConversion.Quote(2_120_000) == new ShopGoldConversionQuote(265, 2_120_000, 0), "212 gold yields exactly EUR 2.65.");
            Check(snapshot.GoldConversion.Quote(4_000_000) == new ShopGoldConversionQuote(500, 4_000_000, 0), "400 gold yields EUR 5.00.");
            Check(snapshot.GoldConversion.Quote(10_000) == new ShopGoldConversionQuote(1, 8_000, 2_000), "1 gold yields one cent; 20 silver stays on the character.");
            Check(snapshot.GoldConversion.Quote(7_999) == new ShopGoldConversionQuote(0, 0, 7_999), "Sub-cent gold is never consumed.");
            ShopGoldConversionQuote maximum = snapshot.GoldConversion.Quote(uint.MaxValue);
            Check(maximum.DebitedCopper + (long)maximum.RemainingCopper == uint.MaxValue && maximum.RemainingCopper < 8_000, "Conversion conserves all copper up to the game money limit.");

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
            Check(state.ConversionCredit == "2,65 €" && state.ConversionDebit == "212 po", "Arbitrary amount preview follows the server rate.");
            state.ConversionGold = "0,8";
            Check(state.ConversionCredit == "0,01 €", "Silver precision is supported.");
            foreach (string invalid in new[] { "", "-1", "NaN", "1e3", "212,00001", "429496.7296", "1 000", "1,2.3" })
            { state.ConversionGold = invalid; Check(state.ConversionQuote is null, "Invalid amount rejected without floating-point rounding."); }
            state.Configure(_ => Task.FromResult(snapshot with { CreditBalanceEuroCents = 265 })); await state.RefreshAsync();
            Check(state.CreditBalance == "2,65 €", "Atlas balance is denominated in euro cents.");
            state.Configure(_ => Task.FromResult(snapshot));
            state.SelectedCharacter = state.Characters[0];
            state.SelectedPrice = state.Prices.Single(p => p.Price.Currency == "gold");
            Check(state.Summary.Contains("Asteria") && state.Summary.Contains("300 po"), "Summary uses selected character and gold price.");
            Check(state.CharacterHint.Contains("423 po 50 pa 67 pc"), "Offline gold remains in copper precision.");
            await state.RefreshAsync();
            Check(state.SelectedCharacter?.Character.Guid == 101 && state.SelectedPrice?.Price.Currency == "gold", "Refresh preserves existing beneficiary and currency.");
            state.SelectedCharacter = state.Characters[1];
            Check(state.CharacterHint.Contains("En ligne") && !state.CharacterHint.Contains("4 235"), "Online character never reuses offline gold.");
            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); state.RefreshLocale();
            Check(state.OfferName == "Name change" && state.SelectedCharacter?.Character.Guid == 202 && state.PriceLabel == "300 gold", "Language change preserves selection and translates currency.");
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
            Console.WriteLine($"Shop runtime PASS: {_checks} assertions; approved prices, response bounds, unknown balances, errors, selections, localization and late responses. Fake HTTP only.");
            return 0;
        }
        finally { LauncherLocalization.SetLocale(locale); }
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
