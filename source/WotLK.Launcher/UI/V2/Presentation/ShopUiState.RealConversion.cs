using System.Net;
using System.IO;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed record ShopConversionActions(Func<ShopCreateGoldConversion, CancellationToken, Task<ShopGoldConversion>> Create);

internal sealed partial class ShopUiState
{
    private ShopConversionActions? _conversionActions;
    private ShopCreateGoldConversion? _conversionAttempt;
    private ShopGoldConversion? _conversionRecord;
    private CancellationTokenSource? _conversionPending;
    private long _conversionSession;
    private string? _conversionAnnouncedId;
    private ShopText? _conversionNotice;
    public bool IsConverting { get; private set; }
    public bool HasPendingConversion => _conversionAttempt is not null || _conversionRecord?.Status == "pending";
    public bool CanEditConversion => !IsConverting && !HasPendingConversion && !IsLoading && !IsBlockingPurchaseRead;
    public bool CanChooseConversionCharacter => CanEditConversion && HasCharacters;
    private bool RealConversionAvailable => _conversionActions is not null && _snapshot?.Conversions?.Available == true;
    internal bool NeedsConversionRefresh => CanRefresh && HasPendingConversion;

    internal void ConfigureConversions(ShopConversionActions actions) { ResetConversions(); _conversionActions = actions; Changed(); }

    internal async Task<bool> ConvertAsync()
    {
        if (IsConversionPreview) return TryConvertPreview();
        if (!CanConvert || _conversionActions is null) return false;
        CancelBackgroundPurchaseRead();
        _conversionAttempt ??= new(Guid.NewGuid().ToString("N"), _conversionCharacter!.Character.Guid,
            RequestedCopper!.Value, ConversionQuote!.CreditEuroCents, _snapshot!.CatalogRevision);
        long session = _conversionSession;
        using CancellationTokenSource pending = new(); _conversionPending = pending;
        IsConverting = true; _conversionNotice = null; _lastConversion = null; Changed();
        try
        {
            ShopGoldConversion result = await _conversionActions.Create(_conversionAttempt, pending.Token);
            result.Validate();
            if (session != _conversionSession || pending.IsCancellationRequested) return false;
            if (result.IdempotencyKey != _conversionAttempt.IdempotencyKey
                || result.CharacterGuid != _conversionAttempt.CharacterGuid || result.OfferedCopper != _conversionAttempt.OfferedCopper
                || result.CreditEuroCents != _conversionAttempt.ExpectedCreditCents)
                throw new InvalidDataException("Conversion receipt does not match the submitted request.");
            _conversionRecord = result; _conversionAttempt = null;
            InvalidateConversionBalances(result.CharacterGuid);
            // The request alone never grants credit. Read the realm's committed result.
            await RefreshPurchaseAsync(pending.Token);
            for (int i = 0; i < 10 && _conversionRecord?.Status == "pending"; ++i)
            {
                await Task.Delay(1000, pending.Token);
                if (session != _conversionSession) return false;
                await RefreshPurchaseAsync(pending.Token);
            }
            return session == _conversionSession && _conversionRecord?.Status == "completed";
        }
        catch (Exception error) when (FundingError(error))
        {
            if (session != _conversionSession || pending.IsCancellationRequested) return false;
            if (error is UnauthorizedAccessException or LauncherAuthException { StatusCode: HttpStatusCode.Unauthorized })
            { ResetSession(); _status = "unauthorized"; return false; }
            bool definitive = error is ShopApiException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests };
            if (!definitive && _conversionAttempt is { } uncertain) InvalidateConversionBalances(uncertain.CharacterGuid);
            if (definitive) _conversionAttempt = null;
            _conversionNotice = error is ShopApiException api ? ConversionError(api.Code) : null;
            _conversionNotice ??= new("Le résultat reste à confirmer. Réessayez ou actualisez : la même conversion sera reprise sans second débit.",
                "The result still needs confirmation. Retry or refresh: the same conversion will resume without a second debit.");
            return false;
        }
        finally
        {
            if (ReferenceEquals(_conversionPending, pending)) _conversionPending = null;
            if (session == _conversionSession) { IsConverting = false; Changed(); }
        }
    }

    private void InvalidateConversionBalances(uint guid)
    {
        if (_snapshot is { } snapshot)
            _snapshot = snapshot with { CreditBalanceEuroCents = null,
                Characters = snapshot.Characters.Select(c => c.Guid == guid ? c with { GoldCopper = null } : c).ToArray() };
        foreach (ShopCharacterRow row in Characters.Where(c => c.Character.Guid == guid)) row.Update(row.Character with { GoldCopper = null });
        foreach (ShopPriceRow row in Prices) row.UpdateBalance(BalanceFor(row.Price.Currency));
    }

    private void ApplyGoldConversions(ShopSnapshot snapshot)
    {
        IReadOnlyList<ShopGoldConversion> requests = snapshot.Conversions?.Requests ?? [];
        string? key = _conversionAttempt?.IdempotencyKey ?? _conversionRecord?.IdempotencyKey;
        ShopGoldConversion? result = key is null ? requests.FirstOrDefault(c => c.Status == "pending")
            : requests.FirstOrDefault(c => c.IdempotencyKey == key);
        if (result is null) return;
        _conversionRecord = result; _conversionAttempt = null;
        if (result.Status == "pending")
        {
            _conversionCharacterId = result.CharacterGuid;
            _conversionCharacter = Characters.FirstOrDefault(c => c.Character.Guid == result.CharacterGuid);
            _conversionGold = FormatGoldNumber(result.OfferedCopper);
            _conversionNotice = null;
        }
        else if (result.Status == "rejected")
            _conversionNotice = ConversionError(result.Reason ?? "") ?? new("Conversion refusée. Actualisez les soldes avant un nouvel essai.", "Conversion rejected. Refresh balances before trying again.");
        else if (_conversionAnnouncedId != result.Id)
        {
            _conversionAnnouncedId = result.Id; _conversionGold = ""; _conversionNotice = null;
            _lastConversion = new(result.CreditBeforeCents!.Value, result.CreditAfterCents!.Value, result.CharacterGuid, result.OfferedCopper);
            CreditGranted?.Invoke(_lastConversion);
        }
    }

    private static ShopText? ConversionError(string code) => code switch
    {
        "shop-character-online" or "character-online" => new("Déconnectez tous les personnages de ce compte, puis actualisez et réessayez.", "Log out of all characters on this account, then refresh and retry."),
        "shop-insufficient-gold" or "insufficient-gold" => new("Ce personnage ne possède plus assez d’or. Actualisez le solde.", "This character no longer has enough gold. Refresh the balance."),
        "shop-character-unavailable" or "character-unavailable" => new("Ce personnage n’est plus disponible sur ce compte.", "This character is no longer available on this account."),
        "shop-price-changed" or "rate-changed" => new("Le taux a changé. Actualisez et vérifiez le nouveau montant.", "The rate changed. Refresh and check the new amount."),
        "shop-wallet-debt" or "wallet-debt" => new("Un solde à régulariser bloque la conversion.", "An outstanding balance prevents conversion."),
        "shop-credit-limit" or "credit-limit" => new("Le plafond des Crédits Atlas serait dépassé, remboursements éventuels compris.", "The Atlas credit limit would be exceeded, including potential refunds."),
        "shop-conversion-pending" => new("Une conversion est déjà en attente sur ce compte. Actualisez son suivi.", "A conversion is already pending on this account. Refresh its status."),
        "shop-conversion-limit" => new("La limite quotidienne de conversions est atteinte.", "The daily conversion limit has been reached."),
        "request-expired" => new("Le royaume n’a pas traité la demande à temps. Aucun or n’a été retiré. Vous pouvez réessayer.", "The realm did not process the request in time. No gold was removed. You can retry."),
        "shop-conversion-unavailable" => new("La conversion est temporairement indisponible. Actualisez ou réessayez plus tard.", "Conversion is temporarily unavailable. Refresh or retry later."),
        _ => null
    };

    private void ResetConversions()
    {
        ++_conversionSession; _conversionPending?.Cancel(); _conversionPending = null;
        IsConverting = false; _conversionAttempt = null; _conversionRecord = null;
        _conversionNotice = null; _conversionAnnouncedId = null;
    }
}
