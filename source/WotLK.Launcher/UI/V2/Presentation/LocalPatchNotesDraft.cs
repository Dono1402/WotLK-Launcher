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
                "Les notes de mise à jour disposent maintenant de leur propre onglet.",
                "Chaque changement est présenté clairement, point par point."
            ]),
            new("Profil",
            [
                "Les accès « Gérer mon profil » et « Gérer mon compte » sont maintenant séparés.",
                "La photo de profil se modifie en cliquant n'importe où sur son aperçu circulaire.",
                "Les actions affichées au survol restent discrètes pour mieux laisser voir la photo.",
                "Les images jusqu'à 25 Mo peuvent être utilisées comme photo de profil.",
                "Le cadrage et l'aperçu de la photo affichent désormais le même résultat.",
                "Le zoom du cadrage se contrôle directement à la molette sur la photo.",
                "La nouvelle photo apparaît immédiatement après sa validation.",
                "Une reconnexion depuis le même appareil ne crée plus de doublons dans les appareils connectés."
            ]),
            new("Social",
            [
                "La photo de profil est synchronisée avec Atlas et visible par les autres utilisateurs."
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
