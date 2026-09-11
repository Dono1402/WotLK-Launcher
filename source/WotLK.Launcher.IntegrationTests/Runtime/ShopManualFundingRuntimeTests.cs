using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class ShopManualFundingRuntimeTests
{
    internal static async Task<int> VerifyAsync()
    {
        int checks = 0;
        string locale = LauncherLocalization.CurrentLocale;
        try
        {
            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            ShopSnapshot snapshot = ShopPreviewData.Create() with
            {
                EuroBalanceCents = 0, History = [],
                ManualFunding = new(true, true, 100, 5000, 10000, 0, 0, [])
            };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ShopTopUp request = new("11111111111111111111111111111111", now, now, 1000, "pending", "https://paypal.me/AtlasFixture/10.00EUR");
            ShopAdminTopUp detail = new(request, 42, 1, null, null, 0, []);
            ShopFundingActions actions = new(
                (_, _) => Task.FromResult(request), (_, _) => Task.FromResult(request),
                (_, _, _) => Task.FromResult(new ShopAdminTopUpPage([detail], null)),
                (_, _) => Task.FromResult(detail), (_, _, _) => Task.FromResult(detail));

            // An uncertain create must reuse its operation key, and the server's
            // refreshed balance remains the only authority after a successful response.
            using (ShopUiState state = new())
            {
                List<ShopCreateTopUp> attempts = [];
                state.Configure(_ => Task.FromResult(snapshot));
                state.ConfigureFunding(actions with { Create = (input, _) =>
                {
                    attempts.Add(input);
                    return Task.FromException<ShopTopUp>(new HttpRequestException("fixture timeout"));
                } });
                await state.RefreshAsync(); state.OpenWallet(); state.WalletAmount = "10";
                await state.CreateTopUpAsync(); await state.CreateTopUpAsync();
                Check(attempts.Count == 2 && attempts[0] == attempts[1] && state.EuroBalance == "0,00 €"
                    && state.HasFundingNotice && !state.IsFundingBusy, "Retries reuse one operation key and never credit optimistically.");
                state.WalletAmount = "20"; await state.CreateTopUpAsync();
                Check(attempts[2].IdempotencyKey != attempts[0].IdempotencyKey && attempts[2].AmountCents == 2000,
                    "Changing the amount creates a distinct operation.");
                state.ConfigureFunding(actions);
                state.WalletAmount = "10"; await state.CreateTopUpAsync();
                Check(state.EuroBalance == "0,00 €" && state.TopUpRequests.Count == 0,
                    "A create response alone cannot invent a balance or a request absent from the server snapshot.");
                snapshot = snapshot with { ManualFunding = snapshot.ManualFunding! with { Requests = [request] } };
                await state.RefreshAsync();
                Check(state.HasPendingTopUp && !state.CanBeginWalletPayment && !state.CanEditWalletDraft
                    && state.PaymentUrlFor(request.Id) == request.PaymentUrl, "A pending server request locks the draft and exposes only its payment link.");
                snapshot = snapshot with { ManualFunding = snapshot.ManualFunding! with { Available = false } };
                await state.RefreshAsync();
                Check(!state.ManualFundingAvailable && state.PaymentUrlFor(request.Id) is null && state.CanAdministerFunding,
                    "Pausing payment creation disables payment links while retaining reconciliation capabilities.");
            }

            snapshot = snapshot with { ManualFunding = snapshot.ManualFunding! with { Available = true, Requests = [] } };
            using (ShopUiState state = new())
            {
                TaskCompletionSource<ShopTopUp> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationToken operationToken = default;
                state.Configure(_ => Task.FromResult(snapshot));
                state.ConfigureFunding(actions with { Create = (_, token) => { operationToken = token; return completion.Task; } });
                await state.RefreshAsync(); state.OpenWallet(); state.WalletAmount = "10";
                Task pending = state.CreateTopUpAsync();
                Check(state.IsFundingBusy && !state.CanRefresh, "A mutation blocks duplicate actions and overlapping snapshot reads.");
                state.ResetSession(); completion.SetResult(request); await pending;
                Check(operationToken.IsCancellationRequested && !state.IsFundingBusy && state.TopUpRequests.Count == 0
                    && !state.HasFundingNotice && state.EuroBalance == "—" && !state.CanAdministerFunding,
                    "Logout cancels pending funding and rejects a late response from the old session.");
            }

            using (ShopUiState state = new())
            {
                TaskCompletionSource<ShopAdminTopUpPage> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                state.Configure(_ => Task.FromResult(snapshot));
                state.ConfigureFunding(actions with { List = (_, _, _) => completion.Task });
                await state.RefreshAsync(); Task pending = state.OpenAdminFundingAsync();
                state.CloseAdminFunding(); completion.SetResult(new([detail], null)); await pending;
                Check(state.AdminTopUps.Count == 0 && !state.HasAdminSelection && !state.IsShopAdminOpen,
                    "A late administrator list cannot repopulate a closed page.");
                state.ConfigureFunding(actions); await state.OpenAdminFundingAsync();
                TaskCompletionSource<ShopAdminTopUp> lateDetail = new(TaskCreationOptions.RunContinuationsAsynchronously);
                state.ConfigureFunding(actions with { Read = (_, _) => lateDetail.Task });
                state.AdminSearchReference = request.Reference; pending = state.SearchAdminTopUpAsync();
                state.ResetSession(); lateDetail.SetResult(detail); await pending;
                Check(!state.HasAdminSelection && state.AdminTransactionId.Length == 0 && state.AdminAudit.Length == 0,
                    "An administrator detail arriving after logout cannot restore private information.");
                state.Configure(_ => Task.FromResult(snapshot)); state.ConfigureFunding(actions);
                await state.RefreshAsync(); await state.OpenAdminFundingAsync();
                state.ConfigureFunding(actions with { Read = (_, _) => Task.FromException<ShopAdminTopUp>(new ShopApiException("shop-admin-required", HttpStatusCode.Forbidden)) });
                state.AdminSearchReference = request.Reference; await state.SearchAdminTopUpAsync();
                Check(!state.IsShopAdminOpen && state.AdminTopUps.Count == 0 && !state.HasAdminSelection,
                    "A server permission denial closes and clears the administration page.");
            }

            using (ShopUiState state = new())
            {
                state.Configure(_ => Task.FromResult(snapshot));
                state.ConfigureFunding(actions with { Create = (_, _) => Task.FromException<ShopTopUp>(new UnauthorizedAccessException()) });
                await state.RefreshAsync(); state.OpenWallet(); state.WalletAmount = "10"; await state.CreateTopUpAsync();
                Check(!state.CanAdministerFunding && state.EuroBalance == "—" && state.FundingNotice.Contains("Reconnectez"),
                    "An expired funding session removes balances and requires sign-in.");
            }

            foreach (string body in new[] { "[]", "null", "true", "not-json", "{\"error\":\"shop-payment-already-used\"}" })
            {
                using HttpClient http = new(new ErrorResponse(body));
                LauncherShopApiClient client = new(http, new Uri("https://fixture.invalid/api/v1/"));
                try { await client.CreateTopUpAsync(new(Guid.NewGuid().ToString("N"), 1000), CancellationToken.None); throw new InvalidOperationException("Expected an HTTP error."); }
                catch (ShopApiException error)
                {
                    Check(error.StatusCode == HttpStatusCode.Conflict && error.Code == (body.StartsWith('{') ? "shop-payment-already-used" : "shop-request-failed"),
                        "Unexpected proxy error bodies remain ordinary typed HTTP failures.");
                }
            }
            return checks;
        }
        finally { LauncherLocalization.SetLocale(locale); }
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    }

    private sealed class ErrorResponse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
