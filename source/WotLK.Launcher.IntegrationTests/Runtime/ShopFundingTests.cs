using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class ShopFundingTests
{
    internal static async Task<int> VerifyAsync()
    {
        int checks = 0;
        string locale = LauncherLocalization.CurrentLocale;
        using ShopUiState state = new();
        ShopSnapshot preview = ShopPreviewData.Create();
        try
        {
            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            await Load(preview);
            Select("character-rename", 101, "credits");
            Check(state.Prices[0].AvailableLabel == "Disponible : 10,00 €" && state.Prices[0].MissingCents == 0
                && state.Prices[1].AvailableLabel == "Disponible : 2,65 €" && state.Prices[1].MissingCents == 435,
                "Both currency choices show their own balance and exact deficit.");
            state.PrepareServiceFunding();
            Check(state.IsConversionOpen && !state.IsServiceOpen && state.HasFundingReturn && state.ConversionGold == "423"
                && state.SelectedCharacter?.Character.Guid == 101, "The requested deficit is capped to available whole gold without changing the beneficiary.");
            Check(state.FundingConversionHint.Contains("4,35 €") && state.FundingContextText.Contains("Changement de nom")
                && state.FundingBackLabel == "Retour au service", "Partial funding identifies the target service and still-needed credits.");
            state.ConversionGold = "212";
            Check(state.ConvertAction == "Convertir 212 po" && state.ConversionReceiveText == "Vous recevrez 2,12 € de Crédits Atlas.",
                "The conversion action names the debit and the exact resulting currency.");
            Check(state.TryConvertPreview() && state.IsConversionOpen && state.ConversionReceiveText.Contains("Vous avez reçu 2,12 €"),
                "A completed conversion keeps its receipt on screen.");
            Check(state.Prices.Single(p => p.Price.Currency == "credits").MissingCents == 223 && state.FundingConversionHint.Contains("2,23 €"),
                "Credit choices and funding goal update after the actual preview transfer.");
            state.CloseConversion(returnToService: true);
            Check(state.IsServiceOpen && !state.HasFundingReturn && state.SelectedPrice?.Price.Currency == "credits"
                && state.SelectedCharacter?.Character.Guid == 101 && state.HasServiceFundingAction && state.CreditBalance == "4,77 €",
                "Returning from partial funding restores the exact service, currency and beneficiary.");

            ShopSnapshot richSource = preview with { Characters = preview.Characters.Select(c => c.Guid == 101 ? c with { GoldCopper = 9_000_000 } : c).ToArray() };
            await Load(richSource);
            Select("character-rename", 303, "credits"); // Online beneficiary, distinct offline source.
            state.PrepareServiceFunding();
            Check(state.SelectedCharacter?.Character.Guid == 303 && state.SelectedConversionCharacter?.Character.Guid == 101 && state.ConversionGold == "435",
                "Funding can use another offline character while preserving the chosen beneficiary.");
            Check(state.TryConvertPreview() && state.CreditBalance == "7,00 €" && state.FundingConversionHint.Contains("nécessaires sont disponibles"),
                "An affordable suggested conversion reaches the exact required credit balance.");
            await state.RefreshAsync(); state.CloseConversion(returnToService: true);
            Check(state.IsServiceOpen && state.SelectedCharacter?.Character.Guid == 303 && state.SelectedPrice?.MissingCents == 0
                && !state.HasServiceFundingAction && !state.CanPurchase, "Return after refresh uses the new balance without enabling checkout or switching the recipient.");

            await Load(preview); Select("character-level-70", 303, "eur");
            Check(state.ServiceFundingAction == "Ajouter les 50,00 € manquants" && state.CanPrepareServiceFunding,
                "Wallet funding asks only for the service deficit.");
            state.PrepareServiceFunding();
            Check(state.IsWalletOpen && state.WalletAmount == "50" && state.WalletBalanceAfter == "60,00 €"
                && state.FundingContextText.Contains("Elune") && !state.CanBeginWalletPayment, "The wallet draft is prefilled without creating a real payment.");
            state.OpenHistory(); state.CloseHistory(returnToOrigin: true);
            Check(state.IsWalletOpen && state.HasFundingReturn && state.WalletAmount == "50", "A history detour keeps the service funding context and amount.");
            state.CloseWallet(returnToService: true);
            Check(state.IsServiceOpen && state.SelectedOffer?.Offer.Id == "character-level-70" && state.SelectedCharacter?.Character.Guid == 303
                && state.SelectedPrice?.Price.Currency == "eur" && state.EuroBalance == "10,00 €" && state.HistoryRows.Count == 3,
                "Returning from a draft restores the selection and does not fabricate funding or journal entries.");
            state.PrepareServiceFunding();
            state.Configure(_ => Task.FromResult(preview with { EuroBalanceCents = 6000 })); await state.RefreshAsync(); state.CloseWallet(returnToService: true);
            Check(state.IsServiceOpen && state.SelectedPrice?.MissingCents == 0 && state.EuroBalance == "60,00 €",
                "A fresh server balance is used when returning from a completed external funding flow.");

            await Load(preview); Select("character-level-70", 101, "eur");
            Check(state.EligibilityTitle == "Niveau 70 déjà atteint" && !state.CanPrepareServiceFunding,
                "A level 80 character is not invited to fund a level 70 boost.");
            Select("character-rename", 303, "eur");
            Check(state.EligibilityTitle == "Personnage en ligne" && state.EligibilityDescription.Contains("Déconnectez"),
                "An online name-change beneficiary sees the relevant prerequisite.");
            Select("character-race-change", 101, "eur");
            Check(state.EligibilityTitle == "Éligibilité à confirmer" && state.OfferPreserved.Contains("faction reste inchangée"),
                "Unknown race eligibility is not presented as approved, while confirmed preservation is shown.");
            state.SelectedCharacter = null;
            Check(!state.CanPrepareServiceFunding && state.EligibilityTitle == "Personnage à choisir", "Funding preparation requires an explicitly chosen recipient.");

            await Load(preview with { EuroBalanceCents = null, CreditBalanceEuroCents = null }); Select("character-rename", 101, "credits");
            Check(state.Prices.All(p => p.MissingCents is null && p.BalanceStatus == "Solde indisponible") && !state.HasServiceFundingAction,
                "Unknown balances do not fabricate a zero balance or a payable deficit.");
            await Load(preview with { EuroBalanceCents = 300, CreditBalanceEuroCents = 1000 }); Select("character-rename", 101, "eur");
            Check(state.SelectedPrice?.MissingCents == 200, "The two balances cannot be silently combined to fund a service.");
            await Load(preview with { EuroBalanceCents = 0 }); Select("character-rename", 101, "eur");
            Check(state.SelectedPrice?.MissingCents == 500 && state.SelectedPrice.AvailableLabel == "Disponible : 0,00 €",
                "An explicitly empty balance remains distinct from an unknown balance.");

            await Load(preview with { CreditBalanceEuroCents = 699, GoldConversion = new(10001) }); Select("character-rename", 101, "credits");
            state.PrepareServiceFunding();
            Check(state.ConversionGold == "2" && state.ConversionQuote?.CreditEuroCents == 1,
                "Non-integral gold requirements round up to whole gold using the supplied server rate.");
            await Load(preview with { GoldConversion = new(uint.MaxValue) }); Select("character-race-change", 101, "credits"); state.PrepareServiceFunding();
            Check(state.ConversionGold == "423" && !state.CanConvert, "Large rates cannot overflow or prefill more than the available gold.");
            await Load(preview with { Characters = [new(303, "Elune", 60, true, null)] }); Select("character-rename", 303, "credits"); state.PrepareServiceFunding();
            Check(!state.CanConvert && state.ConversionGold == "" && state.ConversionHint.Contains("connecté"),
                "An all-online roster gives a useful reason instead of fabricating a convertible source.");

            await Load(preview); Select("character-race-change", 101, "eur"); state.PrepareServiceFunding();
            ShopSnapshot repriced = preview with { Offers = preview.Offers.Select(o => o.Id == "character-race-change"
                ? o with { Prices = [new("eur", 2500), new("credits", 3000)] } : o).ToArray() };
            state.Configure(_ => Task.FromResult(repriced)); await state.RefreshAsync(); state.CloseWallet(returnToService: true);
            Check(state.IsServiceOpen && state.SelectedAmount == "25,00 €" && state.ShowServicePriceChange,
                "A changed service price is explicitly reported on return and never silently charged.");
            foreach (ShopSnapshot changed in new[]
            {
                preview with { Offers = preview.Offers.Where(o => o.Id != "character-race-change").ToArray() },
                preview with { Offers = preview.Offers.Select(o => o.Id == "character-race-change" ? o with { Prices = [new("credits", 3000)] } : o).ToArray() }
            })
            {
                await Load(preview); Select("character-race-change", 101, "eur"); state.PrepareServiceFunding();
                state.Configure(_ => Task.FromResult(changed)); await state.RefreshAsync(); state.CloseWallet(returnToService: true);
                Check(!state.IsServiceOpen && !state.HasFundingReturn, "A removed offer or currency cannot redirect the user into another purchase.");
            }
            await Load(preview); Select("character-race-change", 101, "eur"); state.PrepareServiceFunding();
            state.Configure(_ => Task.FromResult(preview with { Characters = preview.Characters.Where(c => c.Guid != 101).ToArray() }));
            await state.RefreshAsync(); state.CloseWallet(returnToService: true);
            Check(state.IsServiceOpen && state.SelectedCharacter is null && !state.CanPrepareServiceFunding,
                "A removed recipient returns to an unselected field rather than assigning somebody else.");

            await Load(preview); Select("character-race-change", 101, "eur"); state.PrepareServiceFunding(); state.CloseWallet();
            Check(!state.HasFundingReturn && !state.IsServiceOpen, "Ordinary navigation clears the funding return target.");
            Select("character-race-change", 101, "eur"); state.PrepareServiceFunding(); state.OpenConversion();
            Check(!state.HasFundingReturn && state.FundingBackLabel == state.BackToShopLabel, "A generic wallet shortcut does not reuse an old service goal.");
            state.CloseConversion(); Select("character-race-change", 101, "eur"); state.PrepareServiceFunding();
            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale); state.RefreshLocale();
            Check(state.FundingBackLabel == "Back to service" && state.FundingContextText.Contains("Race change"), "Funding context follows the active locale.");
            state.CloseWallet(returnToService: true);
            Check(state.SelectedPrice?.BalanceStatus == "Missing 10.00 €" && state.Prices[0].CurrencyLabel == "Wallet", "Price balances and deficits remain localized after a return.");
            state.PrepareServiceFunding(); state.ResetSession(); state.CloseWallet(returnToService: true);
            Check(!state.HasFundingReturn && !state.IsServiceOpen && state.FundingContextText == "" && state.Prices.Count == 0,
                "Logout removes every funding target and its account data.");
            await Load(preview); Select("character-race-change", 101, "eur"); state.PrepareServiceFunding();
            state.Configure(_ => Task.FromException<ShopSnapshot>(new UnauthorizedAccessException())); await state.RefreshAsync(); state.CloseWallet(returnToService: true);
            Check(!state.HasFundingReturn && !state.IsServiceOpen && !state.HasOffers, "Authentication failure cannot restore an old service target.");
            await Load(preview); Select("character-race-change", 101, "eur"); state.PrepareServiceFunding();
            TaskCompletionSource<ShopSnapshot> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Configure(_ => late.Task); Task pending = state.RefreshAsync(); state.ResetSession();
            state.Configure(_ => Task.FromResult(preview with { Characters = [] })); await state.RefreshAsync(); late.SetResult(preview); await pending;
            state.CloseWallet(returnToService: true);
            Check(!state.HasFundingReturn && !state.IsServiceOpen && state.Characters.Count == 0,
                "A late previous-account response cannot recreate its funding selection.");

            state.Configure(_ => Task.FromException<ShopSnapshot>(new HttpRequestException("fixture", null, HttpStatusCode.ServiceUnavailable))); await state.RefreshAsync();
            Check(state.ShowRetry && state.CanRefresh, "A failed automatic load exposes an explicit retry.");
            state.Configure(_ => Task.FromResult(preview)); await state.RefreshAsync(); Check(!state.ShowRetry, "Retry disappears after recovery.");
            Check(JsonSerializer.Deserialize<ShopSnapshot>(JsonSerializer.Serialize(preview))!.Offers.All(o => o.Preserved is not null),
                "Service preservation details survive the shared JSON contract.");
            try { (preview with { Offers = [preview.Offers[0] with { Preserved = new("", "") }] }).Validate(); throw new InvalidOperationException("Invalid preserved text accepted."); }
            catch (InvalidDataException) { checks++; }
            return checks;
        }
        finally { LauncherLocalization.SetLocale(locale); }

        async Task Load(ShopSnapshot snapshot) { state.ConfigurePreview(snapshot); await state.RefreshAsync(); }
        void Select(string offerId, uint characterGuid, string currency)
        {
            state.OpenService(state.Offers.Single(o => o.Offer.Id == offerId));
            state.SelectedCharacter = state.Characters.Single(c => c.Character.Guid == characterGuid);
            state.SelectedPrice = state.Prices.Single(p => p.Price.Currency == currency);
        }
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
    }
}
