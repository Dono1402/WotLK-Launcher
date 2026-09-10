using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public string EuroPrice => Offer.Prices.FirstOrDefault(p => p.Currency == "eur") is { } price ? ShopUiState.FormatPrice(price) : "—";
    public string GoldPrice => Offer.Prices.FirstOrDefault(p => p.Currency == "gold") is { } price ? ShopUiState.FormatPrice(price) : "—";
    public string OrLabel => ShopUiState.L("ou", "or");
    public string Price => Offer.Prices.Count == 0 ? ShopUiState.L("Tarif à venir", "Price to be announced")
        : string.Join(ShopUiState.L(" ou ", " or "), Offer.Prices.Select(ShopUiState.FormatPrice));
}
internal sealed class ShopCharacterRow(ShopCharacter character) : ShopLocalizedRow
{
    public ShopCharacter Character { get; } = character;
    public string Label => $"{Character.Name} - {ShopUiState.L("Niv.", "Lv.")} {Character.Level}";
}
internal sealed class ShopPriceRow(ShopPrice price) : ShopLocalizedRow
{
    public ShopPrice Price { get; } = price;
    public string Label => ShopUiState.FormatPrice(Price);
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
    public bool CanRefresh => !IsLoading && _read is not null && !_disposed;
    public bool HasOffers => Offers.Count != 0;
    public bool HasCharacters => Characters.Count != 0;
    public bool HasSelection => _offer is not null;
    public bool ShowStatus => _status != "ready";
    public bool CanPurchase => false;
    public string Title => L("Boutique", "Shop");
    public string Subtitle => L("Services et personnalisations pour enrichir votre aventure.", "Services and customization to enrich your adventure.");
    public string CategoryLabel => L("Services de personnage", "Character services");
    public string Availability => L("Ouverture prochaine", "Coming soon");
    public string CreditsLabel => L("Crédits Atlas", "Atlas credits");
    public string CreditBalance => _snapshot?.CreditBalanceEuroCents is long balance ? FormatEuros(balance) : "—";
    public string WalletDescription => L("Un solde commun au launcher et au jeu.", "One balance shared by the launcher and the game.");
    public string CreditsInformation => L("Transformez l’or de vos personnages en crédit Atlas. Votre solde est affiché en euros et pourra servir aux futurs achats de la boutique.",
        "Turn your characters’ gold into Atlas credit. Your balance is displayed in euros and can be used for future shop purchases.");
    public string ConversionGold
    {
        get => _conversionGold;
        set { if (_conversionGold == value) return; _conversionGold = value; Changed(); }
    }
    public bool CanConvert => false;
    public bool CanPreviewConversion => _snapshot is not null;
    public ShopGoldConversionQuote? ConversionQuote
    {
        get
        {
            if (_snapshot is null || !Regex.IsMatch(_conversionGold, "^[0-9]{1,6}([.,][0-9]{1,4})?$", RegexOptions.CultureInvariant)
                || !decimal.TryParse(_conversionGold.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal gold)
                || gold * 10_000 > uint.MaxValue) return null;
            return _snapshot.GoldConversion.Quote((uint)(gold * 10_000));
        }
    }
    public string ConversionRate => _snapshot is null ? "—" : FormatGold(_snapshot.GoldConversion.CopperPerEuroCent * 100L)
        + " = " + FormatEuros(100) + L(" de crédit Atlas", " Atlas credit");
    public string ConversionCredit => ConversionQuote is { } quote ? FormatEuros(quote.CreditEuroCents) : "—";
    public string ConversionDebit => ConversionQuote is { } quote ? FormatGold(quote.DebitedCopper) : "—";
    public string ConversionRemainder => ConversionQuote is { } quote ? FormatGold(quote.RemainingCopper) : "—";
    public string ConversionHint => ConversionQuote is not { } quote
        ? L("Saisissez un montant d’or. Les pièces d’argent et de cuivre peuvent être indiquées après la virgule.", "Enter a gold amount. Silver and copper can be entered after the decimal point.")
        : quote.CreditEuroCents == 0 ? L("Le montant doit permettre d’obtenir au moins 0,01 € de crédit Atlas.", "The amount must yield at least €0.01 of Atlas credit.")
        : _character is null ? L("Choisissez le personnage dont l’or sera converti.", "Choose the character whose gold will be converted.")
        : _character.Character.GoldCopper is uint balance && quote.DebitedCopper > balance
            ? L("Le solde d’or sauvegardé de ce personnage est insuffisant.", "This character’s saved gold balance is insufficient.")
        : L("Le reste est conservé sur le personnage. Son solde sera vérifié au moment de la conversion.", "The remainder stays on your character. Their balance will be checked when converting.");
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
    public string CharacterHint => !HasCharacters ? L("Aucun personnage sur ce compte.", "No characters on this account.")
        : _character?.Character.Online == true ? L("En ligne · le solde d’or sera vérifié en jeu.", "Online · your gold balance will be checked in game.")
        : _character?.Character.GoldCopper is uint gold ? L("Or sauvegardé : ", "Saved gold: ") + FormatGold(gold)
        : L("Sélectionnez le personnage bénéficiaire.", "Select the character receiving this service.");
    public string Summary => _offer is null ? "" : $"{OfferName}\n{_character?.Character.Name ?? L("Personnage à choisir", "Choose a character")} · {PriceLabel}";
    public string PurchaseHint => L("Les achats ouvriront une fois le service disponible sur le royaume.", "Purchases will open once the service is available on the realm.");
    public string PaymentHint => _price?.Price.Currency == "eur"
        ? L("Paiement direct · Carte bancaire, Bancontact ou PayPal.", "Direct payment · Card, Bancontact or PayPal.")
        : _price?.Price.Currency == "gold" ? L("Or du personnage sélectionné · aucun échange avec les crédits.", "Gold from the selected character · no credit conversion.") : "";

    public ShopOfferRow? SelectedOffer
    {
        get => _offer;
        set
        {
            if (ReferenceEquals(value, _offer)) return;
            _offer = value is not null && Offers.Contains(value) ? value : null;
            Prices = _offer?.Offer.Prices.Select(p => new ShopPriceRow(p)).ToArray() ?? [];
            _price = Prices.FirstOrDefault(); Changed();
        }
    }
    public ShopCharacterRow? SelectedCharacter
    {
        get => _character;
        set { if (ReferenceEquals(value, _character)) return; _character = value is not null && Characters.Contains(value) ? value : null; Changed(); }
    }
    public ShopPriceRow? SelectedPrice
    {
        get => _price;
        set { if (Equals(value, _price)) return; _price = value is not null && Prices.Contains(value) ? value : null; Changed(); }
    }

    internal void Configure(Func<CancellationToken, Task<ShopSnapshot>> read) { _read = read; Changed(); }

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
        catch (OperationCanceledException) { if (generation == _generation) _status = "error"; }
        catch (Exception error) when (error is HttpRequestException or UnauthorizedAccessException or IOException or JsonException or LauncherAuthException)
        {
            if (generation == _generation)
                _status = error switch
                {
                    UnauthorizedAccessException => "unauthorized",
                    LauncherAuthException { StatusCode: HttpStatusCode.Unauthorized } => "unauthorized",
                    HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable } => "unavailable",
                    HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => "rate-limited",
                    _ => "error"
                };
        }
        finally
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
            if (generation == _generation) { IsLoading = false; Changed(); }
        }
    }

    private void Apply(ShopSnapshot snapshot, string? offerId, uint? characterId, string? currency)
    {
        _snapshot = snapshot;
        Offers = snapshot.Offers.Select(o => new ShopOfferRow(o)).ToArray();
        Characters = snapshot.Characters.Select(c => new ShopCharacterRow(c)).ToArray();
        _offer = Offers.FirstOrDefault(o => o.Offer.Id == offerId) ?? Offers.FirstOrDefault();
        Prices = _offer?.Offer.Prices.Select(p => new ShopPriceRow(p)).ToArray() ?? [];
        // Never silently assign a different beneficiary after a character disappears.
        _character = Characters.FirstOrDefault(c => c.Character.Guid == characterId);
        _price = Prices.FirstOrDefault(p => p.Price.Currency == currency) ?? Prices.FirstOrDefault();
        _status = Offers.Count == 0 ? "empty" : "ready";
    }

    internal void RefreshLocale()
    {
        foreach (ShopLocalizedRow row in Offers.Cast<ShopLocalizedRow>().Concat(Characters).Concat(Prices)) row.RefreshLocale();
        Changed();
    }

    internal void ResetSession()
    {
        ++_generation;
        CancellationTokenSource? pending = _pending; _pending = null;
        pending?.Cancel();
        Clear(); _conversionGold = ""; IsLoading = false; _status = "unavailable"; Changed();
    }
    private void Clear() { _snapshot = null; Offers = []; Characters = []; Prices = []; _offer = null; _character = null; _price = null; }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public void Dispose() { if (_disposed) return; _disposed = true; ResetSession(); _read = null; }
    internal static string Text(ShopText text) => LauncherLocalization.IsEnglish ? text.En : text.Fr;
    internal static string L(string fr, string en) => LauncherLocalization.IsEnglish ? en : fr;
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(LauncherLocalization.CurrentLocale);
    internal static string FormatPrice(ShopPrice price) => price.Currency == "gold" ? FormatGold(price.Amount)
        : price.Currency == "eur" ? FormatEuros(price.Amount)
        : FormatEuros(price.Amount) + L(" de crédit Atlas", " Atlas credit");
    internal static string FormatEuros(long euroCents) => (euroCents / 100m).ToString("N2", Culture) + " €";
    internal static string FormatGold(long copper) => (copper / 10000).ToString("N0", Culture) + L(" po", " gold")
        + (copper % 10000 == 0 ? "" : $" {copper / 100 % 100}{L(" pa", " silver")} {copper % 100}{L(" pc", " copper")}");
}
