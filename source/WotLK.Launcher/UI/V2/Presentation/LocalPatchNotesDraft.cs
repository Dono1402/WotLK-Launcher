using System.Collections.Immutable;

namespace WotLK.Launcher.UI.V2.Presentation;

internal static class LocalPatchNotesDraft
{
    internal const string Id = "draft-next-release";

    internal static PatchNoteEntryViewState Create() => new(
        Id,
        Version: "1.7.0",
        Title: "Atlas Launcher 1.7.0",
        PublishedText: "Non publiée",
        Intro: "Atlas Launcher 1.7.0 ajoute la boutique Atlas, les soldes séparés et le changement de nom à utiliser depuis la sélection des personnages.",
        HasIntro: true,
        IsLatest: true,
        IsDraft: true,
        Sections:
        [
            new("Boutique Atlas",
            [
                "Parcourez les services et comparez leur prix en euros du portefeuille ou en Crédits Atlas.",
                "Consultez les deux soldes séparément et accédez à leur détail depuis la barre supérieure.",
                "Retrouvez les conditions du service, la monnaie choisie et le montant manquant éventuel avant un achat.",
                "Suivez vos achats, annulations et remboursements dans un historique filtrable."
            ]),
            new("Changement de nom",
            [
                "Achetez un changement de nom pour votre compte, puis choisissez le personnage en jeu au moment de l’utiliser.",
                "Continuez votre partie après l’achat et revenez volontairement à la sélection des personnages pour utiliser le service.",
                "Validez le nouveau nom avant de confirmer ; un nom refusé laisse le service disponible.",
                "Annulez un service non utilisé pour récupérer son montant dans la monnaie de l’achat."
            ]),
            new("Suivi et disponibilité",
            [
                "Retrouvez les services disponibles après une reconnexion et le reçu du renommage avec l’ancien et le nouveau nom.",
                "Le changement de nom est le premier service prévu pour activation. Les autres services, la conversion réelle d’or et les paiements automatiques restent indisponibles."
            ])
        ]);

    internal static ImmutableArray<PatchNoteEntryViewState> PrependTo(
        ImmutableArray<PatchNoteEntryViewState> publishedNotes)
    {
        if (publishedNotes.Any(note => string.Equals(note.Id, Id, StringComparison.Ordinal)
            || string.Equals(note.Id, "atlas-launcher-1-7-0", StringComparison.Ordinal)))
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
