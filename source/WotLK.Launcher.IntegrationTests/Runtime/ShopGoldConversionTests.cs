using System.IO;
using System.Net;
using System.Net.Http;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Presentation;

internal static class ShopGoldConversionTests
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); ++checks; }
        ShopSnapshot initial = ShopRuntimeTests.Snapshot with { CreditBalanceEuroCents = 50, EuroBalanceCents = 987, Conversions = new(true, []) };
        ShopSnapshot current = initial;
        ShopCreateGoldConversion? request = null;
        ShopGoldConversion? saved = null;
        int sends = 0, events = 0;
        using ShopUiState state = new();
        state.Configure(_ => Task.FromResult(current));
        state.ConfigureConversions(new((input, _) =>
        {
            ++sends;
            if (request is not null) Check(input == request, "An uncertain conversion must retain every request field and its key.");
            request = input;
            saved ??= new(Guid.NewGuid().ToString("N"), input.IdempotencyKey, input.CharacterGuid, "Asteria", input.OfferedCopper,
                input.ExpectedCreditCents, "completed", null, 4_235_067, 4_135_067, 50, 60, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            saved.Validate();
            current = initial with { CreditBalanceEuroCents = 60, Characters = [initial.Characters[0] with { GoldCopper = 4_135_067 }], Conversions = new(true, [saved]) };
            return sends == 1 ? Task.FromException<ShopGoldConversion>(new HttpRequestException("Lost POST reply")) : Task.FromResult(saved);
        }));
        state.CreditGranted += _ => ++events;
        await state.RefreshAsync(); state.OpenConversion(); state.ConversionGold = "10";
        Check(state.CanConvert && !state.IsConversionPreview, "The runtime converter opens only with the server capability and real actions.");
        Check(!await state.ConvertAsync() && events == 0 && state.CreditBalance == "—" && state.AvailableCopper is null,
            "An ambiguous POST cannot animate success or display stale spendable balances.");
        Check(state.CanConvert && !state.CanEditConversion && state.HasPendingConversion, "An uncertain conversion exposes safe retry and freezes its draft.");
        state.ConversionGold = "20"; state.SelectedConversionCharacter = null;
        Check(state.ConversionGold == "10" && state.SelectedConversionCharacter?.Character.Guid == 101, "Frozen inputs cannot silently alter the retry.");
        Check(await state.ConvertAsync() && sends == 2 && events == 1 && !state.HasPendingConversion && state.CanEditConversion,
            "A recovered committed receipt completes once and releases the draft.");
        Check(state.AvailableCopper == 4_135_067 && state.EuroBalance == ShopUiState.FormatEuros(987)
            && state.CreditBalance == ShopUiState.FormatEuros(60) && state.HasConversionReceipt,
            "Only the authoritative snapshot updates character gold and credits; euros remain independent.");
        await state.RefreshPurchaseAsync();
        Check(events == 1, "A repeated read cannot replay the credit animation.");
        state.ResetSession();
        Check(!state.HasPendingConversion && !state.HasConversionReceipt && !state.CanConvert, "Sign-out clears conversion state.");

        ShopGoldConversion pending = saved! with { Id = Guid.NewGuid().ToString("N"), IdempotencyKey = Guid.NewGuid().ToString("N"),
            Status = "pending", GoldBeforeCopper = null, GoldAfterCopper = null, CreditBeforeCents = null, CreditAfterCents = null };
        current = initial with { Conversions = new(false, [pending]) };
        state.Configure(_ => Task.FromResult(current)); state.ConfigureConversions(new((_, _) => throw new Exception("No POST expected")));
        await state.RefreshAsync(); state.OpenConversion();
        Check(state.HasPendingConversion && state.NeedsConversionRefresh && !state.CanConvert && state.SelectedConversionCharacter?.Character.Guid == 101,
            "A fresh launcher session recovers a pending conversion even while new conversions are paused.");
        current = current with { Conversions = new(false, [pending with { Status = "rejected", Reason = "character-online" }]) };
        await state.RefreshPurchaseAsync();
        Check(!state.HasPendingConversion && state.CanEditConversion && !state.CanConvert && events == 1,
            "A realm rejection releases the draft without creating credit.");

        TaskCompletionSource<ShopGoldConversion> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        current = initial;
        state.Configure(_ => Task.FromResult(current)); state.ConfigureConversions(new((input, _) => { request = input; return late.Task; }));
        await state.RefreshAsync(); state.OpenConversion(); state.ConversionGold = "10";
        Task<bool> sending = state.ConvertAsync();
        Check(state.IsConverting && !state.CanRefresh && !state.CanEditPurchase, "A send excludes competing launcher mutations and refreshes.");
        state.ResetSession(); state.Configure(_ => Task.FromResult(initial with { CreditBalanceEuroCents = 777 })); await state.RefreshAsync();
        late.SetResult(saved! with { IdempotencyKey = request!.IdempotencyKey });
        Check(!await sending && state.CreditBalance == ShopUiState.FormatEuros(777) && !state.HasConversionReceipt && events == 1,
            "A late previous-account response cannot change the next account's balance or receipt.");
        current = initial;
        state.Configure(_ => Task.FromResult(current));
        state.ConfigureConversions(new((_, _) => Task.FromException<ShopGoldConversion>(new ShopApiException("shop-insufficient-gold", HttpStatusCode.Conflict))));
        await state.RefreshAsync(); state.OpenConversion(); state.ConversionGold = "10";
        Check(!await state.ConvertAsync() && !state.HasPendingConversion && state.CanEditConversion && events == 1,
            "An explicit eligibility rejection clears the attempt and never announces a debit.");
        foreach (ShopGoldConversion invalid in new[] { saved! with { GoldAfterCopper = 1 }, saved! with { CreditAfterCents = 61 },
            pending with { GoldBeforeCopper = 100000 }, saved! with { Status = "pending" } })
        {
            try { invalid.Validate(); throw new Exception("Malformed receipt accepted."); }
            catch (InvalidDataException) { ++checks; }
        }
        Console.WriteLine($"Gold conversion client PASS: {checks} checks; simulated HTTP receipts, recovery, session isolation and validation.");
        return 0;
    }
}
