namespace WotLK.Launcher.Shop.Contracts;

// Shared by the server catalog and the isolated preview. An empty price list means
// pricing is not defined; it must never be interpreted as a free service.
public static class ShopServiceCatalog
{
    public static ShopOffer[] CreateOffers(long renameEuroCents = 500, long renameCreditEuroCents = 700, bool accountServices = false) =>
    [
        new("character-rename", "services", new("Changement de nom", "Name change"),
            new("Le droit de choisir un nouveau nom depuis l’écran de sélection des personnages.",
                "The ability to choose a new name on the character selection screen."),
            accountServices ? new("Le service est conservé sur votre compte jusqu’à utilisation. Choisissez un personnage de niveau 10 minimum et son nouveau nom à l’écran de sélection des personnages. Les règles de nommage du royaume s’appliquent. Annulation possible avant utilisation.",
                "The service stays on your account until used. Choose a character of level 10 or above and a new name on the character selection screen. Realm naming rules apply. You can cancel before use.")
            : new("Le nouveau nom se choisit à l’écran de sélection des personnages, après déconnexion. Il doit respecter les règles de nommage du royaume. Un renommage déjà en attente doit être terminé avant un nouvel achat.",
                "Choose your new name on the character selection screen after logging out. Realm naming rules apply. Complete any pending name change before purchasing another."),
            [new("eur", renameEuroCents), new("credits", renameCreditEuroCents)],
            new("Faites-vous un nom. Écrivez votre prochaine légende.", "Make a name for yourself. Write your next legend."),
            new("Votre personnage, son niveau, son équipement et sa progression.", "Your character, level, equipment and progress.")),
        new("character-level-70", "services", new("Sésame niveau 70", "Level 70 boost"),
            new("Le passage au niveau 70. L’équipement, les compétences et les éventuels éléments complémentaires seront précisés avant l’ouverture.",
                "A boost to level 70. Equipment, skills and any additional items will be detailed before launch."),
            new("Service en préparation. Les personnages éligibles et le contenu inclus seront précisés avant l’ouverture.",
                "Service in preparation. Eligible characters and included content will be announced before launch."),
            [new("eur", 6000), new("credits", 7000)],
            new("Le Norfendre vous attend. Entrez dans l’aventure.", "Northrend awaits. Step into your next adventure."),
            new("Les éléments conservés seront détaillés avec les conditions du sésame avant l’ouverture.",
                "What you keep will be detailed with the boost conditions before launch.")),
        new("character-faction-change", "services", new("Changement de faction", "Faction change"),
            new("Le passage de l’Alliance à la Horde, ou de la Horde à l’Alliance.",
                "A switch from the Alliance to the Horde, or from the Horde to the Alliance."),
            new("Service en préparation. Les combinaisons race et classe autorisées et les règles de transfert seront précisées avant l’ouverture.",
                "Service in preparation. Allowed race and class combinations and transfer rules will be announced before launch."),
            [new("eur", 3500), new("credits", 4500)],
            new("Une nouvelle bannière. De nouvelles conquêtes.", "Fly a new banner. Set out on new conquests."),
            new("Le devenir des quêtes, réputations et objets sera précisé dans les règles de transfert avant l’ouverture.",
                "How quests, reputations and items are handled will be explained in the transfer rules before launch.")),
        new("character-race-change", "services", new("Changement de race", "Race change"),
            new("Le choix d’une nouvelle race compatible avec votre classe.",
                "The choice of a new race compatible with your class."),
            new("Service en préparation. Les races compatibles avec votre classe seront précisées avant l’ouverture.",
                "Service in preparation. Races compatible with your class will be announced before launch."),
            [new("eur", 2000), new("credits", 3000)],
            new("Révélez un nouveau visage à votre légende.", "Give your legend a bold new face."),
            new("Votre faction reste inchangée. Les autres éléments conservés seront détaillés avant l’ouverture.",
                "Your faction stays the same. Other retained elements will be detailed before launch."))
    ];
}
