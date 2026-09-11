namespace WotLK.Launcher.Shop.Contracts;

// Shared by the server catalog and the isolated preview. An empty price list means
// pricing is not defined; it must never be interpreted as a free service.
public static class ShopServiceCatalog
{
    public static ShopOffer[] CreateOffers(long renameEuroCents = 500, long renameCreditEuroCents = 700) =>
    [
        new("character-rename", "services", new("Changement de nom", "Name change"),
            new("Un nouveau nom, la même aventure. Conservez votre personnage, son équipement et sa progression.",
                "A new name, the same adventure. Keep your character, equipment and progress."),
            new("Le nouveau nom se choisit à l’écran de sélection des personnages, après déconnexion. Il doit respecter les règles de nommage du royaume. Un renommage déjà en attente doit être terminé avant un nouvel achat.",
                "Choose your new name on the character selection screen after logging out. Realm naming rules apply. Complete any pending name change before purchasing another."),
            [new("eur", renameEuroCents), new("credits", renameCreditEuroCents)],
            new("Faites-vous un nom. Écrivez votre prochaine légende.", "Make a name for yourself. Write your next legend.")),
        new("character-level-70", "services", new("Sésame niveau 70", "Level 70 boost"),
            new("Préparez votre arrivée en Norfendre avec un personnage de niveau 70.",
                "Prepare for Northrend with a level 70 character."),
            new("Service en préparation. Les personnages éligibles et le contenu inclus seront précisés avant l’ouverture.",
                "Service in preparation. Eligible characters and included content will be announced before launch."),
            [new("eur", 6000), new("credits", 7000)],
            new("Le Norfendre vous attend. Entrez dans l’aventure.", "Northrend awaits. Step into your next adventure.")),
        new("character-faction-change", "services", new("Changement de faction", "Faction change"),
            new("Découvrez l’autre camp et poursuivez votre aventure sous une nouvelle bannière.",
                "Discover the other side and continue your adventure under a new banner."),
            new("Service en préparation. Les combinaisons race et classe autorisées et les règles de transfert seront précisées avant l’ouverture.",
                "Service in preparation. Allowed race and class combinations and transfer rules will be announced before launch."),
            [new("eur", 3500), new("credits", 4500)],
            new("Une nouvelle bannière. De nouvelles conquêtes.", "Fly a new banner. Set out on new conquests.")),
        new("character-race-change", "services", new("Changement de race", "Race change"),
            new("Donnez une nouvelle identité à votre personnage tout en conservant votre faction.",
                "Give your character a new identity while keeping your faction."),
            new("Service en préparation. Les races compatibles avec votre classe seront précisées avant l’ouverture.",
                "Service in preparation. Races compatible with your class will be announced before launch."),
            [new("eur", 2000), new("credits", 3000)],
            new("Révélez un nouveau visage à votre légende.", "Give your legend a bold new face."))
    ];
}
