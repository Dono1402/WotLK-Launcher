using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using WotLK.Launcher.Shop.Contracts;
using WotLK.Launcher.UI.V2.Localization;

namespace WotLK.Launcher.UI.V2.Presentation;

internal abstract class ShopLocalizedRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void RefreshLocale() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

internal sealed class ShopOfferRow(ShopOffer offer) : ShopLocalizedRow
{
    public ShopOffer Offer { get; } = offer;
    public string Name => ShopUiState.Text(Offer.Name);
    public string Description => ShopUiState.Text(Offer.Description);
    public string Tagline => ShopUiState.Text(Offer.Tagline ?? Offer.Description);
    public string WalletPrice => AmountFor("eur");
    public string CreditPrice => AmountFor("credits");
    public string WalletLabel => ShopUiState.L("Portefeuille", "Wallet");
    public string CreditsLabel => ShopUiState.L("Crédits Atlas", "Atlas credits");
    private string AmountFor(string currency) => Offer.Prices.FirstOrDefault(p => p.Currency == currency) is { } price ? ShopUiState.FormatEuros(price.Amount) : "—";
    public bool HasPrice => Offer.Prices.Count != 0;
    public string Category => ShopUiState.L("Service de personnage", "Character service");
    public string Artwork => "/WotLK.Launcher;component/Assets/Shop/" + (Offer.Id switch
    {
        "character-rename" => "Service_name_change.png",
        "character-level-70" => "Service_level_70.png",
        "character-faction-change" => "Service_faction_change.png",
        "character-race-change" => "Service_race_change.png",
        _ => "Service_a_venir.png"
    });
    public string Price => Offer.Prices.Count == 0 ? ShopUiState.L("Tarif à venir", "Price to be announced")
        : string.Join(ShopUiState.L(" ou ", " or "), Offer.Prices.Select(ShopUiState.FormatPrice));
}
internal sealed class ShopCharacterRow(ShopCharacter character) : ShopLocalizedRow
{
    public ShopCharacter Character { get; private set; } = character;
    internal void Update(ShopCharacter character)
    {
        if (character.Guid != Character.Guid) throw new InvalidOperationException("A shop row cannot change its character identity.");
        Character = character; RefreshLocale();
    }
    public string Label => $"{Character.Name} - {ShopUiState.L("Niv.", "Lv.")} {Character.Level}";
}
internal sealed class ShopPriceRow(ShopPrice price, long? balanceCents = null) : ShopLocalizedRow
{
    private long? _balanceCents = balanceCents;
    public ShopPrice Price { get; } = price;
    public string Label => ShopUiState.FormatPrice(Price);
    public string Amount => ShopUiState.FormatEuros(Price.Amount);
    public bool IsCredits => Price.Currency == "credits";
    public string CurrencyLabel => IsCredits ? ShopUiState.L("Crédits Atlas", "Atlas credits") : ShopUiState.L("Portefeuille", "Wallet");
    public string CurrencyColor => IsCredits ? "#EDD18B" : "#A9DCFA";
    public string AvailableLabel => ShopUiState.L("Disponible : ", "Available: ") + (_balanceCents is long balance ? ShopUiState.FormatEuros(balance) : "—");
    public long? MissingCents => _balanceCents is long balance ? Math.Max(0, Price.Amount - balance) : null;
    public string BalanceStatus => MissingCents is null ? ShopUiState.L("Solde indisponible", "Balance unavailable")
        : MissingCents == 0 ? ShopUiState.L("Solde suffisant", "Enough funds")
        : ShopUiState.L("Il manque ", "Missing ") + ShopUiState.FormatEuros(MissingCents.Value);
    public string StatusColor => MissingCents == 0 ? "#A0DFCE" : "#B8CCDB";
    public string AccessibleLabel => CurrencyLabel + " · " + Amount + " · " + AvailableLabel + " · " + BalanceStatus;
    internal void UpdateBalance(long? balance) { _balanceCents = balance; RefreshLocale(); }
}

internal sealed partial class ShopUiState : INotifyPropertyChanged, IDisposable
{
    private Func<CancellationToken, Task<ShopSnapshot>>? _read;
    private CancellationTokenSource? _pending;
    private long _generation;
    private ShopSnapshot? _snapshot;
    private ShopOfferRow? _offer;
    private ShopCharacterRow? _character;
    private ShopPriceRow? _price;
    private string _status = "unavailable";
    private bool _disposed;
    private string _conversionGold = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<ShopOfferRow> Offers { get; private set; } = [];
    public IReadOnlyList<ShopCharacterRow> Characters { get; private set; } = [];
    public IReadOnlyList<ShopPriceRow> Prices { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public bool CanRefresh => !IsLoading && !IsConverting && !IsFundingBusy && !IsPurchasing && !IsPurchaseReading && _read is not null && !_disposed;
    public bool HasOffers => Offers.Count != 0;
    public bool HasCharacters => Characters.Count != 0;
    public bool IsServiceOpen { get; private set; }
    public bool HasPrices => Prices.Count != 0;
    public string? OfferArtwork => _offer?.Artwork;
    public string ConditionsLabel => L("Conditions d’utilisation", "Conditions of use");
    public bool HasSelection => _offer is not null;
    public bool ShowStatus => _status != "ready";
    public bool ShowRetry => !IsLoading && _status is "error" or "unavailable" or "rate-limited";
    public string RetryLabel => L("Réessayer", "Try again");
    public string Title => L("Boutique", "Shop");
    public string Subtitle => L("Services et personnalisations pour enrichir votre aventure.", "Services and customization to enrich your adventure.");
    public string CategoryLabel => L("Services de personnage", "Character services");
    public string Availability => L("Ouverture prochaine", "Coming soon");
    public string CreditsLabel => L("Crédits Atlas", "Atlas credits");
    public string CreditBalance => _snapshot?.CreditBalanceEuroCents is long balance ? FormatEuros(balance) : "—";
    public string WalletDescription => L("Un solde commun au launcher et au jeu.", "One balance shared by the launcher and the game.");
    public string CreditsInformation => L("Transformez l’or de vos personnages en Crédits Atlas. Votre solde est affiché en euros et pourra servir aux futurs achats de la boutique.",
        "Turn your characters’ gold into Atlas credits. Your balance is displayed in euros and can be used for future shop purchases.");
    public string Status => _status switch
    {
        "loading" => L("Chargement de la boutique…", "Loading the shop…"),
        "empty" => L("Aucun produit n’est proposé pour le moment.", "No products are available yet."),
        "unauthorized" => L("Reconnectez-vous pour consulter votre boutique.", "Sign in again to view your shop."),
        "rate-limited" => L("Veuillez patienter avant d’actualiser la boutique.", "Please wait before refreshing the shop."),
        "error" => L("La boutique n’a pas pu être chargée. Réessayez dans un instant.", "The shop could not be loaded. Please try again shortly."),
        "ready" => "",
        _ => L("La boutique sera bientôt disponible sur ce royaume.", "The shop will be available on this realm soon.")
    };
    public string OfferName => _offer?.Name ?? "";
    public string OfferDescription => _offer?.Description ?? "";
    public string OfferConditions => _offer is null ? "" : Text(_offer.Offer.Conditions);
    public string PriceLabel => _price?.Label ?? L("Tarif à venir", "Price to be announced");
    public string SelectedCurrencyLabel => _price?.Price.Currency switch { "credits" => CreditsLabel, "eur" => WalletLabel, _ => "" };
    public string SelectedCurrencyColor => _price?.Price.Currency == "credits" ? "#EDD18B" : "#A9DCFA";
    public string Summary => _offer is null ? "" : $"{OfferName}\n{_character?.Character.Name ?? L("Personnage à choisir", "Choose a character")} · {PriceLabel}";

    public ShopOfferRow? SelectedOffer
    {
        get => _offer;
        set
        {
            if (!CanEditPurchase || ReferenceEquals(value, _offer)) return;
            _offer = value is not null && Offers.Contains(value) ? value : null;
            Prices = _offer?.Offer.Prices.Select(CreatePriceRow).ToArray() ?? [];
            _price = Prices.FirstOrDefault(); Changed();
        }
    }
    public ShopCharacterRow? SelectedCharacter
    {
        get => _character;
        set { if (!CanEditPurchase || ReferenceEquals(value, _character)) return; _character = value is not null && Characters.Contains(value) ? value : null; _purchaseNotice = null; Changed(); }
    }
    public ShopPriceRow? SelectedPrice
    {
        get => _price;
        set
        {
            // Replacing a ComboBox ItemsSource can report a transient null or an
            // old row. It must not clear the new offer's valid currency choice.
            // Clearing the catalog/session resets _price directly.
            if (!CanEditPurchase || value is null || !Prices.Contains(value) || ReferenceEquals(value, _price)) return;
            _price = value; Changed();
        }
    }

    internal void OpenService(ShopOfferRow row)
    {
        if (_disposed || !CanEditPurchase || !Offers.Contains(row)) return;
        CloseAdminFunding();
        ClearFundingReturn();
        SelectedOffer = row; IsHistoryOpen = false; IsWalletOpen = false; IsConversionOpen = false; IsServiceOpen = true; Changed();
    }
    internal void CloseService() { IsServiceOpen = false; ClearFundingReturn(); Changed(); }

    internal void Configure(Func<CancellationToken, Task<ShopSnapshot>> read) { ResetConversions(); _conversionActions=null; ResetPurchases(); _purchaseActions=null; ResetManualFunding(); _fundingActions=null; _previewSnapshot = null; _read = read; Changed(); }

    internal async Task RefreshAsync()
    {
        if (!CanRefresh) return;
        long generation = ++_generation;
        using CancellationTokenSource pending = new();
        _pending = pending;
        string? offerId = _offer?.Offer.Id;
        uint? characterId = _character?.Character.Guid;
        string? currency = _price?.Price.Currency;
        Clear(); IsLoading = true; _status = "loading"; Changed();
        try
        {
            ShopSnapshot snapshot = await _read!(pending.Token);
            if (_disposed || generation != _generation || pending.IsCancellationRequested) return;
            snapshot.Validate();
            Apply(snapshot, offerId, characterId, currency);
        }
        catch (OperationCanceledException) { if (generation == _generation) { _status = "error"; ClearFundingReturn(); } }
        catch (Exception error) when (error is HttpRequestException or UnauthorizedAccessException or IOException or JsonException or LauncherAuthException)
        {
            if (generation == _generation)
            {
                ClearFundingReturn();
                _status = error switch
                {
                    UnauthorizedAccessException => "unauthorized",
                    LauncherAuthException { StatusCode: HttpStatusCode.Unauthorized } => "unauthorized",
                    HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable } => "unavailable",
                    HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "rate-limited",
                    _ => "error"
                };
            }
        }
        finally
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
            if (generation == _generation) { IsLoading = false; Changed(); }
        }
    }

    private void Apply(ShopSnapshot snapshot, string? offerId, uint? characterId, string? currency)
    {
        bool sameCatalog = _snapshot?.CatalogRevision == snapshot.CatalogRevision;
        IReadOnlyList<ShopCharacterRow> oldCharacters = Characters;
        IReadOnlyList<ShopPriceRow> oldPrices = Prices;
        _snapshot = snapshot;
        if (_purchaseAttempt is { } attempt && snapshot.Purchases?.Orders.Any(o => o.IdempotencyKey == attempt.IdempotencyKey) == true) _purchaseAttempt = null;
        RefreshTopUpRows();
        if (IsShopAdminOpen && !CanAdministerFunding) CloseAdminFunding(returnToWallet:true);
        RefreshHistoryRows();
        if (!sameCatalog) Offers = snapshot.Offers.Select(o => new ShopOfferRow(o)).ToArray();
        Characters = snapshot.Characters.Select(c =>
        {
            ShopCharacterRow? row = oldCharacters.FirstOrDefault(old => old.Character.Guid == c.Guid);
            if (row is null) return new ShopCharacterRow(c);
            row.Update(c); return row;
        }).ToArray();
        _offer = Offers.FirstOrDefault(o => o.Offer.Id == offerId) ?? Offers.FirstOrDefault();
        Prices = _offer?.Offer.Prices.Select(price =>
        {
            ShopPriceRow row = oldPrices.FirstOrDefault(old => old.Price == price) ?? CreatePriceRow(price);
            row.UpdateBalance(BalanceFor(price.Currency)); return row;
        }).ToArray() ?? [];
        // Never silently assign a different beneficiary after a character disappears.
        _character = Characters.FirstOrDefault(c => c.Character.Guid == characterId);
        _price = Prices.FirstOrDefault(p => p.Price.Currency == currency) ?? Prices.FirstOrDefault();
        _conversionCharacter = Characters.FirstOrDefault(c => c.Character.Guid == _conversionCharacterId);
        ApplyGoldConversions(snapshot);
        _status = Offers.Count == 0 ? "empty" : "ready";
    }

    internal void RefreshLocale()
    {
        foreach (ShopLocalizedRow row in Offers.Cast<ShopLocalizedRow>().Concat(Characters).Concat(Prices).Concat(PaymentMethods).Concat(HistoryRows).Concat(HistoryKindFilters).Concat(HistoryWalletFilters).Concat(TopUpRequests).Concat(AdminTopUps).Concat(AdminStatusFilters).Concat(AdminActions)) row.RefreshLocale();
        Changed();
    }

    internal void ResetSession()
    {
        ResetConversions();
        ResetPurchases();
        ResetManualFunding();
        ++_generation;
        CancellationTokenSource? pending = _pending; _pending = null;
        pending?.Cancel();
        if (_previewSnapshot is not null) _read = null;
        Clear(); ResetHistory(); ClearFundingReturn(); IsWalletOpen = false; _walletAmount = ""; _paymentMethod = null; _previewSnapshot = null; _conversionCharacterId = null; _conversionGold = ""; IsConversionOpen = false; IsLoading = false; _status = "unavailable"; Changed();
    }
    private void Clear()
    {
        IsServiceOpen = false; _snapshot = null; _lastConversion = null; Offers = []; Characters = []; Prices = []; HistoryRows = [];
        _offer = null; _character = null; _conversionCharacter = null; _price = null; TopUpRequests=[];
        CancelAdminReads(); ClearAdminDetail(); AdminTopUps=[];
        foreach(ShopPaymentMethodRow row in PaymentMethods)row.SetManualFunding(false);
    }
    private long? BalanceFor(string currency) => currency == "eur" ? _snapshot?.EuroBalanceCents : _snapshot?.CreditBalanceEuroCents;
    private ShopPriceRow CreatePriceRow(ShopPrice price) => new(price, BalanceFor(price.Currency));
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { if (_disposed) return; _disposed = true; ResetSession(); _read = null; }
    internal static string Text(ShopText text) => LauncherLocalization.IsEnglish ? text.En : text.Fr;
    internal static string L(string fr, string en) => LauncherLocalization.IsEnglish ? en : fr;
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(LauncherLocalization.CurrentLocale);
    internal static string FormatPrice(ShopPrice price) => (price.Currency == "credits"
        ? L("Crédits Atlas", "Atlas credits") : L("Portefeuille", "Wallet")) + " · " + FormatEuros(price.Amount);
    internal static string FormatEuros(long euroCents) => (euroCents / 100m).ToString("N2", Culture) + " €";
}
