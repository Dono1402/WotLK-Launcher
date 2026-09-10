using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.UI.V2.Preview;

internal static class ShopPreviewData
{
    // Explicit preview route only. No runtime or account data is loaded.
    internal static ShopSnapshot Create() => new(ShopSnapshot.CurrentSchemaVersion, "shop-preview", DateTimeOffset.UtcNow, false, 265,
        [new("character-rename", "services", new("Changement de nom", "Name change"),
            new("Un nouveau nom, la même aventure. Conservez votre personnage, son équipement et sa progression.",
                "A new name, the same adventure. Keep your character, equipment and progress."),
            new("Le nouveau nom se choisit à l’écran de sélection des personnages, après déconnexion. Il doit respecter les règles de nommage du royaume. Un renommage déjà en attente doit être terminé avant un nouvel achat.",
                "Choose your new name on the character selection screen after logging out. Realm naming rules apply. Complete any pending name change before purchasing another."),
            [new("credits", 500), new("eur", 500)])],
        [new(101, "Asteria", 80, false, 4_235_067), new(202, "Boreal", 70, false, 1_208_000),
            new(303, "Elune", 60, true, null)], ShopGoldConversionRate.Default, EuroBalanceCents: 1000);
}
