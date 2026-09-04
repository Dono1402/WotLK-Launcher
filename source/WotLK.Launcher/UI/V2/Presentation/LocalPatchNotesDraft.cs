using System.Collections.Immutable;

namespace WotLK.Launcher.UI.V2.Presentation;

internal static class LocalPatchNotesDraft
{
    internal const string Id = "draft-next-release";

    internal static PatchNoteEntryViewState Create() => new(
        Id,
        Version: "Brouillon local",
        Title: "Améliorations en préparation",
        PublishedText: "Non publiée",
        Intro: "Cette note reste locale jusqu'à la validation explicite de sa publication.",
        HasIntro: true,
        IsLatest: true,
        IsDraft: true,
        Sections:
        [
            new("Launcher",
            [
                "La page Jeu affiche désormais Norfendre jusqu'aux bords de l'onglet, sans cadre intérieur.",
                "Le Roi-liche accompagne maintenant l'écran principal du jeu avec un nouveau visuel panoramique.",
                "Le cadrage et la luminosité du visuel principal mettent davantage en valeur le Roi-liche et le paysage.",
                "L'accès aux mises à jour utilise désormais un bouton compact uniquement représenté par son icône.",
                "Les actions Mises à jour et Jouer sont regroupées en bas à droite.",
                "Les informations d'installation restent accessibles dans Paramètres sans encombrer la page Jeu.",
                "Les notes de mise à jour disposent maintenant de leur propre onglet.",
                "Chaque changement est présenté clairement, point par point.",
                "La barre supérieure est plus légère, avec des raccourcis sans cadre et un accès Amis limité à son icône.",
                "L’état du serveur de jeu apparaît désormais directement au-dessus du bouton Jouer.",
                "Le statut du serveur et les notes se mettent désormais à jour automatiquement.",
                "Atlas Launcher empêche l’ouverture de plusieurs exemplaires et ramène au premier lorsqu’il est déjà lancé.",
                "La croix range désormais Atlas Launcher dans la zone de notification, avec des actions pour le rouvrir ou le quitter.",
                "L’icône Atlas reste disponible dans la zone de notification pendant l’utilisation du launcher.",
                "La version complète se trouve dans Diagnostic et les clients de test sont identifiés par un badge LOCAL discret."
            ]),
            new("Jeu",
            [
                "Le bouton Jouer indique clairement quand le jeu démarre, quand il est ouvert et quand il est de nouveau disponible.",
                "Les boutons Jouer et Mises à jour sont plus grands et le statut Client prêt a été retiré du visuel principal.",
                "Une fermeture complète d’Atlas Launcher ferme également le jeu en cours."
            ]),
            new("Profil",
            [
                "Les accès « Gérer mon profil » et « Gérer mon compte » sont maintenant séparés.",
                "La photo de profil se modifie en cliquant n'importe où sur son aperçu circulaire.",
                "Les actions affichées au survol restent discrètes pour mieux laisser voir la photo.",
                "Les images jusqu'à 25 Mo peuvent être utilisées comme photo de profil.",
                "Le cadrage et l'aperçu de la photo affichent désormais le même résultat.",
                "Le zoom du cadrage se contrôle directement à la molette sur la photo.",
                "Une nouvelle photo peut être repositionnée immédiatement dans toutes les directions.",
                "La nouvelle photo apparaît immédiatement après sa validation.",
                "Une reconnexion depuis le même appareil ne crée plus de doublons dans les appareils connectés."
            ]),
            new("Paramètres",
            [
                "Les paramètres ont été simplifiés pour ne conserver que les options réellement utiles.",
                "L’interface d’Atlas Launcher peut maintenant être utilisée en français ou en anglais.",
                "Atlas Launcher peut maintenant démarrer automatiquement avec Windows.",
                "La fermeture par la croix peut être configurée pour ranger Atlas Launcher dans la zone de notification.",
                "La page Mises à jour se concentre désormais sur la version installée et la version disponible."
            ]),
            new("Social",
            [
                "La photo de profil est synchronisée avec Atlas et visible par les autres utilisateurs.",
                "Atlas signale désormais les nouvelles demandes d’ami.",
                "Les demandes d’ami restent notifiées automatiquement sans réglage supplémentaire.",
                "Une notification sonore peut prévenir lorsqu’un ami se connecte, avec une option pour la désactiver."
            ])
        ]);

    internal static ImmutableArray<PatchNoteEntryViewState> PrependTo(
        ImmutableArray<PatchNoteEntryViewState> publishedNotes)
    {
        if (publishedNotes.Any(note => string.Equals(note.Id, Id, StringComparison.Ordinal)))
        {
            return publishedNotes;
        }

        ImmutableArray<PatchNoteEntryViewState>.Builder notes =
            ImmutableArray.CreateBuilder<PatchNoteEntryViewState>(publishedNotes.Length + 1);
        notes.Add(Create());
        notes.AddRange(publishedNotes.Select(note => note with { IsLatest = false }));
        return notes.ToImmutable();
    }
}
