namespace WotLK.Launcher.Shop.Contracts;

// Shared by the server catalog and the isolated preview. An empty price list means
// pricing is not defined; it must never be interpreted as a free service.
public static class ShopServiceCatalog
{
    public static ShopOffer[] CreateOffers(long renameEuroCents = 500, long renameCreditEuroCents = 500) =>
    [
        new("character-rename", "services", new("Changement de nom", "Name change"),
            new("Un nouveau nom, la même aventure. Conservez votre personnage, son équipement et sa progression.",
                "A new name, the same adventure. Keep your character, equipment and progress."),
            new("Le nouveau nom se choisit à l’écran de sélection des personnages, après déconnexion. Il doit respecter les règles de nommage du royaume. Un renommage déjà en attente doit être terminé avant un nouvel achat.",
                "Choose your new name on the character selection screen after logging out. Realm naming rules apply. Complete any pending name change before purchasing another."),
            [new("credits", renameCreditEuroCents), new("eur", renameEuroCents)]),
        new("character-level-70", "services", new("Sésame niveau 70", "Level 70 boost"),
            new("Préparez votre arrivée en Norfendre avec un personnage de niveau 70.",
                "Prepare for Northrend with a level 70 character."),
            new("Service en préparation. Le tarif, les personnages éligibles et le contenu inclus seront précisés avant l’ouverture.",
                "Service in preparation. Pricing, eligible characters and included content will be announced before launch."), []),
        new("character-faction-change", "services", new("Changement de faction", "Faction change"),
            new("Découvrez l’autre camp et poursuivez votre aventure sous une nouvelle bannière.",
                "Discover the other side and continue your adventure under a new banner."),
            new("Service en préparation. Le tarif, les combinaisons race et classe autorisées et les règles de transfert seront précisés avant l’ouverture.",
                "Service in preparation. Pricing, allowed race and class combinations and transfer rules will be announced before launch."), []),
        new("character-race-change", "services", new("Changement de race", "Race change"),
            new("Donnez une nouvelle identité à votre personnage tout en conservant votre faction.",
                "Give your character a new identity while keeping your faction."),
            new("Service en préparation. Le tarif et les races compatibles avec votre classe seront précisés avant l’ouverture.",
                "Service in preparation. Pricing and races compatible with your class will be announced before launch."), [])
    ];
}
