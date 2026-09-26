using System.Collections.Immutable;

namespace WotLK.Launcher.UI.V2.Presentation;

internal static class LocalPatchNotesDraft
{
    internal const string Id = "draft-next-release";

    internal static PatchNoteEntryViewState Create() => new(
        Id,
        Version: "À venir",
        Title: "Atlas Launcher · prochaine mise à jour",
        PublishedText: "Non publiée",
        Intro: "Cette version locale prépare Atlas aux jeux et services multiples, avec une navigation plus cohérente et des transitions harmonisées.",
        HasIntro: true,
        IsLatest: true,
        IsDraft: true,
        Sections:
        [
            new("Jeux et services",
            [
                "Passe de WOTLK Server à Minecraft depuis le sélecteur d’univers.",
                "Les activités ouvrent le jeu concerné et les profils amis conservent ton contexte de navigation.",
                "La page Minecraft présente le serveur de survie ; son lancement et son suivi restent à connecter."
            ]),
            new("Navigation et présentation",
            [
                "Les pages, menus et changements d’univers partagent des transitions discrètes et respectent la réduction des animations.",
                "Les fonctions communes à Atlas utilisent une présentation stable, avec des titres et espacements harmonisés.",
                "La barre reste accessible dans les profils et le bouton Retour retrouve l’écran d’origine."
            ]),
            new("Actions plus claires",
            [
                "Les soldes expliquent leur destination au survol et la copie d’une référence affiche une confirmation.",
                "Échap ferme d’abord le menu ouvert, sans quitter la page située derrière.",
                "Quitter Atlas est disponible dans le menu du profil ; la première fermeture explique le fonctionnement en arrière-plan."
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
