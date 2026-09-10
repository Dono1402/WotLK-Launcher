namespace WotLK.Launcher.Shop.Contracts;

public sealed record ShopText(string Fr, string En);
public sealed record ShopPrice(string Currency, long Amount);
public sealed record ShopOffer(string Id, string Category, ShopText Name, ShopText Description,
    ShopText Conditions, IReadOnlyList<ShopPrice> Prices);
public sealed record ShopCharacter(uint Guid, string Name, byte Level, bool Online, uint? GoldCopper);

// Balances are nullable: an unavailable wallet is never represented as an empty wallet.
// Checkout is deliberately closed until reservation, fulfillment and payment handling exist.
public sealed record ShopSnapshot(int SchemaVersion, string CatalogRevision, DateTimeOffset ObservedAtUtc,
    bool CheckoutAvailable, long? CreditBalanceEuroCents, IReadOnlyList<ShopOffer> Offers,
    IReadOnlyList<ShopCharacter> Characters, ShopGoldConversionRate GoldConversion, long? EuroBalanceCents = null)
{
    public const int CurrentSchemaVersion = 2;
    public const long MaximumBalanceCents = 1_000_000_000;
    public const int MaximumResponseBytes = 256 * 1024;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion || string.IsNullOrWhiteSpace(CatalogRevision) || CatalogRevision.Length > 80
            || CreditBalanceEuroCents is < 0 or > MaximumBalanceCents || EuroBalanceCents is < 0 or > MaximumBalanceCents
            || GoldConversion is null || GoldConversion.CopperPerEuroCent == 0
            || Offers is null || Characters is null || Offers.Count > 100 || Characters.Count > 50)
            throw new InvalidDataException("Invalid shop snapshot.");
        HashSet<string> offers = new(StringComparer.Ordinal);
        foreach (ShopOffer offer in Offers)
        {
            if (offer is null || string.IsNullOrWhiteSpace(offer.Id) || offer.Id.Length > 80 || !offers.Add(offer.Id)
                || offer.Category is not ("services" or "mounts" or "pets")
                || !ValidText(offer.Name, 120) || !ValidText(offer.Description, 2000) || !ValidText(offer.Conditions, 2000)
                || offer.Prices is null || offer.Prices.Count > 3)
                throw new InvalidDataException("Invalid shop offer.");
            HashSet<string> currencies = new(StringComparer.Ordinal);
            foreach (ShopPrice price in offer.Prices)
                if (price is null || price.Currency is not ("credits" or "eur") || price.Amount <= 0
                    || price.Amount > MaximumBalanceCents
                    || !currencies.Add(price.Currency))
                    throw new InvalidDataException("Invalid shop price.");
        }
        HashSet<uint> characters = [];
        foreach (ShopCharacter character in Characters)
            if (character is null || character.Guid == 0 || !characters.Add(character.Guid)
                || string.IsNullOrWhiteSpace(character.Name) || character.Name.Length > 24
                || character.Level is < 1 or > 80 || (character.Online && character.GoldCopper is not null))
                throw new InvalidDataException("Invalid shop character.");
    }

    private static bool ValidText(ShopText? text, int maximum) => text is not null
        && !string.IsNullOrWhiteSpace(text.Fr) && text.Fr.Length <= maximum
        && !string.IsNullOrWhiteSpace(text.En) && text.En.Length <= maximum;
}
