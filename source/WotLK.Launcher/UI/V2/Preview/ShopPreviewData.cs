using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Preview;

internal static class ShopPreviewData
{
    // Explicit preview route only. No runtime or account data is loaded.
    internal static ShopSnapshot Create() => new(ShopSnapshot.CurrentSchemaVersion, "shop-preview", DateTimeOffset.UtcNow, false, 265,
        ShopServiceCatalog.CreateOffers(),
        [new(101, "Asteria", 80, false, 4_235_067), new(202, "Boreal", 70, false, 1_208_000),
            new(303, "Elune", 60, true, null)], ShopGoldConversionRate.Default, EuroBalanceCents: 1000,
        History:
        [
            new("preview-rename", DateTimeOffset.UtcNow.AddHours(-8), "purchase", "eur", -500, "completed",
                new("Changement de nom", "Name change"), "Asteria", 1000),
            new("preview-wallet", DateTimeOffset.UtcNow.AddDays(-1), "top-up", "eur", 1500, "completed",
                new("Recharge par carte bancaire", "Bank card top-up"), BalanceAfterCents: 1500),
            new("preview-gold", DateTimeOffset.UtcNow.AddDays(-2), "conversion", "credits", 265, "completed",
                new("Conversion d’or", "Gold conversion"), "Asteria", 265, 2_650_000)
        ]);
}
