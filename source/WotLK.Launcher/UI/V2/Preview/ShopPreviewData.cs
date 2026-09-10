using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Preview;

internal static class ShopPreviewData
{
    // Explicit preview route only. No runtime or account data is loaded.
    internal static ShopSnapshot Create() => new(ShopSnapshot.CurrentSchemaVersion, "shop-preview", DateTimeOffset.UtcNow, false, 265,
        ShopServiceCatalog.CreateOffers(),
        [new(101, "Asteria", 80, false, 4_235_067), new(202, "Boreal", 70, false, 1_208_000),
            new(303, "Elune", 60, true, null)], ShopGoldConversionRate.Default, EuroBalanceCents: 1000);
}
