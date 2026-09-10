using System.Security.Cryptography;
using System.Text.Json;
using WotLK.Launcher.Shop.Contracts;

namespace WotLK.Launcher.Server;

internal sealed class ShopCatalog
{
    private readonly ShopOffer[] _offers;
    private readonly string _revision;

    internal ShopCatalog(long renameEuroCents = 500, long renameCreditEuroCents = 500)
    {
        ShopPrice[] prices = [new("credits", renameCreditEuroCents), new("eur", renameEuroCents)];
        _offers = [new("character-rename", "services",
            new("Changement de nom", "Name change"),
            new("Un nouveau nom, la même aventure. Conservez votre personnage, son équipement et sa progression.",
                "A new name, the same adventure. Keep your character, equipment and progress."),
            new("Le nouveau nom se choisit à l’écran de sélection des personnages, après déconnexion. Il doit respecter les règles de nommage du royaume. Un renommage déjà en attente doit être terminé avant un nouvel achat.",
                "Choose your new name on the character selection screen after logging out. Realm naming rules apply. Complete any pending name change before purchasing another."),
            prices)];
        _revision = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Offers = _offers, GoldConversion = ShopGoldConversionRate.Default }))).ToLowerInvariant();
        CreateSnapshot([]).Validate();
    }

    internal ShopSnapshot CreateSnapshot(IReadOnlyList<ShopCharacter> characters) =>
        new(ShopSnapshot.CurrentSchemaVersion, _revision, DateTimeOffset.UtcNow, false, null, _offers, characters, ShopGoldConversionRate.Default);
}
