using System.Security.Cryptography;
using System.Text.Json;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal sealed class ShopCatalog
{
    private readonly ShopOffer[] _offers;
    private readonly string _revision;

    internal ShopCatalog(long renameEuroCents = 500, long renameCreditEuroCents = 700, bool accountServices = false)
    {
        _offers = ShopServiceCatalog.CreateOffers(renameEuroCents, renameCreditEuroCents, accountServices);
        _revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Offers = _offers, GoldConversion = ShopGoldConversionRate.Default }))).ToLowerInvariant();
        CreateSnapshot([]).Validate();
    }

    internal ShopSnapshot CreateSnapshot(IReadOnlyList<ShopCharacter> characters) =>
        new(ShopSnapshot.CurrentSchemaVersion, _revision, DateTimeOffset.UtcNow, false, null, _offers, characters, ShopGoldConversionRate.Default);
}
