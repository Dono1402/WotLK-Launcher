using System.Collections.Immutable;

namespace WotLK.Launcher.UI.V2.Presentation;

internal static class LocalPatchNotesDraft
{
    internal const string Id = "draft-next-release";

    internal static PatchNoteEntryViewState Create() => new(
        Id,
        Version: "1.5.0",
        Title: "Atlas Launcher 1.5.0",
        PublishedText: "Non publiée",
        Intro: "La messagerie Atlas arrive avec les conversations privées et de groupe, le partage de fichiers et les lecteurs intégrés. Cette version enrichit aussi la présence, les profils des amis et les échanges avec les joueurs en jeu, et corrige le recadrage des avatars.",
        HasIntro: true,
        IsLatest: true,
        IsDraft: true,
        Sections:
        [
            new("Messages et conversations",
            [
                "Nouvelle page Messages avec liste de conversations, recherche de contacts, filtre Non lus et création de conversation depuis le bouton +.",
                "Conversations privées et groupes avec nom, image, invitations et gestion des membres. Les nouveaux membres accèdent à l’historique à partir de leur entrée dans le groupe.",
                "Réponses à un message, réactions par emoji, modification et suppression de ses propres messages, épinglage des conversations et des messages.",
                "Messages regroupés par auteur et par jour, repère des nouveaux messages, indicateurs de saisie et de lecture selon la présence choisie.",
                "Mise en forme du texte avec gras, italique, listes, citations, code et spoilers. Le texte reste sélectionnable et copiable.",
                "Les brouillons sont conservés par compte et par conversation. Les envois en attente peuvent être repris ou annulés après une interruption.",
                "Un envoi en échec peut être supprimé sans réapparaître après reconnexion. Les messages supprimés disparaissent aussi des extraits, réponses et épingles.",
                "Les conversations auparavant archivées restent accessibles dans Toutes."
            ]),
            new("Images et pièces jointes",
            [
                "Ajout de fichiers par sélection ou glisser-déposer, et collage de captures, avec aperçu avant l’envoi.",
                "Envoi d’images, GIF personnels, documents, fichiers audio et vidéos, jusqu’à 500 Mo par fichier et dix pièces jointes par message.",
                "Les images et GIF conservent leurs proportions, y compris les formats portrait, sans cadre de fichier autour de l’image.",
                "Suivi de progression, annulation et reprise des transferts. Les aperçus du brouillon restent stables pendant la saisie.",
                "Visionneuse agrandie et menu contextuel Enregistrer sous… pour les images, fichiers audio et documents autorisés.",
                "Les liens affichent des aperçus. Un aperçu de page non vidéo peut être retiré sans effacer le lien ni le message."
            ]),
            new("Audio et vidéo",
            [
                "Lecteurs intégrés avec lecture/pause, progression, durée, volume, agrandissement et plein écran vidéo, dans le fil comme dans le brouillon. Navigation dans la barre améliorée, y compris pendant les glissements.",
                "Les commandes vidéo apparaissent en transparence sur l’image et se masquent automatiquement pendant la lecture.",
                "L’agrandissement et le retour au fil conservent la position de lecture et le volume. L’arrivée de nouveaux messages ne recrée plus le lecteur.",
                "La lecture continue lorsque le launcher perd le focus ou est réduit. Elle s’arrête en quittant Messages ou en changeant de conversation.",
                "Les aperçus YouTube et Vimeo démarrent depuis leur vignette. Les fichiers audio et vidéo proches de la zone visible se préparent plus tôt, avec un chargement anticipé au survol ou au focus clavier.",
                "Prise en charge élargie des pièces jointes audio et vidéo, notamment M4A, FLAC, MOV et MKV. La lecture intégrée dépend du codec contenu dans le fichier."
            ]),
            new("Présence et notifications",
            [
                "Les statuts En ligne, Absent, Ne pas déranger et Apparaître hors ligne se choisissent dans le menu du profil et sont indiqués sur l’avatar.",
                "Passage automatique à Absent après vingt minutes d’inactivité Windows, puis retour à En ligne à la reprise. Les statuts choisis manuellement sont conservés.",
                "Le statut est partagé entre Messages, la liste d’amis et les profils. Une autre session active évite de vous rendre absent à tort.",
                "Ne pas déranger suspend les notifications de messages et d’amis. Les sons de la messagerie restent désactivés.",
                "Apparaître hors ligne masque l’activité du launcher et du jeu, ainsi que les indicateurs de saisie et de lecture partagés."
            ]),
            new("Profils, armurerie et avatars",
            [
                "Les profils des amis s’ouvrent dans la vue complète, avec leurs personnages, leur équipement et l’armurerie 3D en consultation.",
                "Partager l’Armory d’un personnage permet de choisir son personnage directement dans Messages. La carte ouvre précisément le personnage partagé.",
                "Correction du recadrage des avatars sur les écrans avec mise à l’échelle Windows : le résultat correspond désormais à la zone sélectionnée.",
                "Photos de profil plus nettes dans les grands profils et chargement des avatars fiabilisé dans Messages et la liste d’amis."
            ]),
            new("Chuchotements avec le jeu",
            [
                "Les conversations privées relient le launcher aux personnages connectés en jeu.",
                "Un message envoyé depuis le launcher apparaît sous le nom Pseudo#Launcher pour identifier son auteur.",
                "Les réponses adressées à cette identité rejoignent la conversation du compte dans Messages."
            ]),
            new("Compte, paramètres et addons en jeu",
            [
                "Le profil, la gestion du compte, les réglages de sécurité et les sessions restent accessibles pendant que le jeu est ouvert.",
                "Les paramètres et la gestion des addons restent disponibles en jeu, y compris l’installation, la mise à jour et la suppression des addons."
            ]),
            new("Fluidité et finitions",
            [
                "Retrait des messages d’attente et confirmations superflus dans le profil, les amis, Messages, le compte et les addons. Les indicateurs sont intégrés aux contrôles ; les erreurs et progressions utiles restent visibles.",
                "Menus, transitions et aperçus de Messages plus fluides, avec respect du réglage Windows de réduction des animations.",
                "Amélioration de la cadence d’affichage de Messages sur les écrans à fréquence élevée, lorsque la version de Windows le permet.",
                "Libellés, commandes de lecture et nouveaux états disponibles en français et en anglais."
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
