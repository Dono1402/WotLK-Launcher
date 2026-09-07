using System.Collections.Immutable;
using System.Windows;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;

internal static partial class LauncherAddonsRuntimeTests
{
    private static async Task CharacterizeAdvancedActionsAsync()
    {
        AddonPackage dependency = CreateFakePackage("dependency", "Dépendance");
        AddonPackage main = CreateFakePackage("main", "Principal");
        main.Dependencies = [dependency.Id];
        main.SourceUrl = "https://addons.test/main";
        main.KnownLimitations = "Fonctions optionnelles non testées";
        main.AtlasValidation = new() { Version = main.Version, EvidenceUrl = "https://atlas.test/evidence/main" };
        AddonPackage current = CreateFakePackage("current", "À jour");
        AddonPackage update = CreateFakePackage("update", "Mise à jour", version: "2.0.0");
        AddonPackage damaged = CreateFakePackage("damaged", "Endommagé");
        AddonPackage external = CreateFakePackage("external", "Externe");
        AddonCatalog catalog = CreateCatalog(main, dependency, current, update, damaged, external);
        FakeAddonManagementService service = new(catalog);
        service.SetInspection(current.Id, Managed(AddonLocalStatus.Installed, current.Version));
        service.SetInspection(update.Id, Managed(AddonLocalStatus.UpdateAvailable, "1.0.0"));
        service.SetInspection(damaged.Id, Managed(AddonLocalStatus.Installed, damaged.Version) with { HasFileManifest = true });
        service.SetInspection(external.Id, Unmanaged(AddonLocalStatus.DetectedUnmanaged));
        service.VerificationResults[damaged.Id] = new(damaged.Id, AddonVerificationStatus.NeedsRepair, 2,
            ["Atlasdamaged/core.lua"], [], [], DateTimeOffset.UtcNow);
        service.ManualAddons = [new ManualAddonInstallation("manual:personal", "Personal", "Personnel", "1.0", "Auteur",
            "30403", true, [update.Folders[0]], string.Empty)];
        await using AddonsEnvironment environment = new(service, isGameRunning: true);
        await LoadCatalogAsync(environment.Coordinator);
        Equal(1, environment.Coordinator.CurrentSnapshot.ManualAddons.Length,
            "L’inventaire hors catalogue doit accompagner le snapshot.");
        AddonRuntimeItem mainItem = environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == main.Id);
        True(mainItem.IsAtlasValidated && mainItem.SourceUrl == main.SourceUrl && mainItem.KnownLimitations == main.KnownLimitations,
            "La preuve Atlas explicite et les informations de source doivent être projetées.");

        int preparedBefore = environment.Session.PreparationCalls;
        AddonsActionCompletion verified = await CompleteAsync(environment.Coordinator.TryVerify(damaged.Id));
        Equal(AddonsActionCompletionStatus.Succeeded, verified.Status,
            "La vérification doit achever sa lecture même lorsqu’elle découvre un fichier manquant.");
        Equal(preparedBefore, environment.Session.PreparationCalls,
            "Vérifier des fichiers locaux ne doit pas rafraîchir la session réseau.");
        Equal(0, service.AppliedAddonIds.Count, "La vérification ne doit pas appeler le pipeline de mutation.");
        True(environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == damaged.Id).NeedsRepair,
            "Un fichier déclaré manquant doit rendre la réparation disponible sans exiger un dossier entier absent.");
        Equal(AddonsNoticeKind.VerificationIncomplete, environment.Coordinator.CurrentSnapshot.Notice,
            "La vérification ne doit pas annoncer un état sain si elle a découvert une anomalie.");

        AddonsPlanPreview preview = environment.Coordinator.PreviewInstall([main.Id, update.Id, current.Id, damaged.Id]);
        True(preview.IsValid && !preview.RequiresExternalReplacementConfirmation,
            "Le plan mixte sans dossier externe doit être valide.");
        SequenceEqual([dependency.Id, main.Id, update.Id, damaged.Id], preview.Items.Select(item => item.Id),
            "Le plan doit précéder chaque dépendant par ses dépendances et ignorer les éléments déjà à jour.");
        True(preview.Items[0].IsDependency && preview.Items[^1].Action == AddonsRequestedAction.Repair,
            "Le plan doit distinguer les dépendances ajoutées automatiquement et la réparation nécessaire.");
        service.PlanFailure = new AddonPlanException("external-replacement-required", [main.Id]);
        AddonsActionCompletion stalePlan = await CompleteAsync(environment.Coordinator.TryInstallSelection([main.Id, update.Id]));
        Equal(AddonsErrorCategory.ExternalReplacementRequired, stalePlan.Snapshot.Error.Category,
            "Une détection externe survenue après l’aperçu doit bloquer le préflight du lot.");
        Equal(main.Id, stalePlan.Snapshot.Error.AddonId, "L’erreur doit identifier le dossier concerné par le refus.");
        Equal(0, service.AppliedAddonIds.Count, "Le préflight global doit précéder toutes les écritures du lot.");
        service.PlanFailure = null;
        List<AddonsRuntimeSnapshot> snapshots = [];
        environment.Coordinator.SnapshotChanged += (_, e) => snapshots.Add(e.Snapshot);
        AddonsActionCompletion installed = await CompleteAsync(environment.Coordinator.TryInstallSelection(
            [main.Id, update.Id, current.Id, damaged.Id]));
        Equal(AddonsActionCompletionStatus.Succeeded, installed.Status, "Le lot mixte doit aboutir.");
        SequenceEqual([dependency.Id, main.Id, update.Id, damaged.Id], service.AppliedAddonIds,
            "Le pipeline doit suivre exactement le plan confirmé.");
        SequenceEqual([damaged.Id], service.ForcedAddonIds,
            "La réparation après vérification doit forcer le remplacement des fichiers malgré des métadonnées déjà à jour.");
        True(snapshots.Where(snapshot => snapshot.OperationId is not null).All(snapshot => snapshot.ActiveAddonTotal == 4),
            "Le dénominateur de progression du lot doit rester fixé à quatre jusqu’à la fin.");
        True(environment.Coordinator.PreviewInstall([main.Id, update.Id, current.Id, damaged.Id]).Items.IsEmpty,
            "Les actions réussies et déjà à jour doivent disparaître du plan suivant.");

        int appliedBefore = service.AppliedAddonIds.Count;
        AddonsActionStartResult blocked = environment.Coordinator.TryInvokePrimary(external.Id);
        Equal(AddonsActionStartStatus.ConfirmationRequired, blocked.Status,
            "Le point d’entrée historique ne doit pas écraser un addon externe sans consentement.");
        Equal(appliedBefore, service.AppliedAddonIds.Count, "Le refus doit précéder toute mutation.");
        True(environment.Coordinator.PreviewInstall([external.Id]).RequiresExternalReplacementConfirmation,
            "L’aperçu doit annoncer le remplacement des dossiers externes.");
        await CompleteAsync(environment.Coordinator.TryInvokePrimary(external.Id, allowExternalReplacement: true));
        SequenceEqual([external.Id], service.AllowedExternalAddonIds,
            "Le consentement explicite doit être transmis au pipeline ciblé.");

        Equal(AddonsActionStartStatus.DependencyInUse, environment.Coordinator.TryRemove(dependency.Id).Status,
            "La suppression d’une dépendance installée doit être refusée tant que son dépendant est installé.");
        AddonsActionStartResult manualBlock = environment.Coordinator.TryRemove(update.Id);
        True(manualBlock.Status == AddonsActionStartStatus.DependencyInUse
            && manualBlock.RelatedAddonIds.Contains("manual:personal"),
            "Les dépendances TOC des addons manuels doivent protéger leurs dossiers requis.");

        preparedBefore = environment.Session.PreparationCalls;
        await CompleteAsync(environment.Coordinator.TryVerifySelection([current.Id, damaged.Id]));
        Equal(preparedBefore, environment.Session.PreparationCalls, "La vérification multiple doit rester sans réseau.");
        Equal(AddonVerificationStatus.LegacyUnverified,
            environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == current.Id).VerificationStatus,
            "Un ancien état sans manifeste doit rester non vérifiable.");
        await CompleteAsync(environment.Coordinator.TryReinstall(main.Id));
        Equal(main.Id, service.ForcedAddonIds[^1], "Réinstaller doit forcer le package sélectionné déjà à jour.");
        Equal(AddonsNoticeKind.Reinstalled, environment.Coordinator.CurrentSnapshot.Notice,
            "La réinstallation doit publier son résultat dédié.");

        main.Dependencies = ["absent"];
        AddonsPlanPreview invalid = environment.Coordinator.PreviewInstall([main.Id]);
        True(!invalid.IsValid && invalid.ErrorCode == "dependency-missing",
            "Une dépendance absente du catalogue doit bloquer le plan avant une installation.");
    }

    private static async Task CharacterizeRepeatedReinstallRetryAsync()
    {
        AddonPackage first = CreateFakePackage("first-dependency", "Première dépendance");
        AddonPackage second = CreateFakePackage("second-dependency", "Deuxième dépendance");
        AddonPackage target = CreateFakePackage("reinstall-target", "Réinstallation demandée");
        target.Dependencies = [first.Id, second.Id];
        FakeAddonManagementService service = new(CreateCatalog(target, first, second));
        service.SetInspection(target.Id, Managed(AddonLocalStatus.Installed, target.Version));
        service.NextApplyFailure = new IOException("first dependency unavailable");
        await using AddonsEnvironment environment = new(service, isGameRunning: false);
        await LoadCatalogAsync(environment.Coordinator);
        True(!environment.Coordinator.PreviewRetry().IsValid,
            "Un aperçu de reprise ne doit pas inventer un plan lorsqu’aucune opération n’est à reprendre.");

        AddonsActionCompletion original = await CompleteAsync(environment.Coordinator.TryReinstall(target.Id));
        Equal(AddonsActionCompletionStatus.Failed, original.Status, "La dépendance doit faire échouer la première tentative.");
        SequenceEqual([second.Id, target.Id], original.Snapshot.UnprocessedAddonIds,
            "Le plan interrompu doit garder la dépendance suivante et la réinstallation non commencées.");
        AddonsPlanPreview firstRetry = environment.Coordinator.PreviewRetry();
        True(firstRetry.IsValid, "Le premier aperçu de reprise doit être disponible.");
        SequenceEqual([first.Id, second.Id, target.Id], firstRetry.Items.Select(item => item.Id),
            "L’aperçu de reprise doit inclure les deux dépendances restantes puis l’addon à réinstaller.");
        Equal(AddonsRequestedAction.Reinstall, firstRetry.Items[^1].Action,
            "Une réinstallation déjà à jour doit rester explicite dans l’aperçu de reprise.");

        service.NextApplyFailure = new IOException("dependency still unavailable");
        AddonsActionCompletion retryFailure = await CompleteAsync(environment.Coordinator.TryRetryFailed());
        Equal(AddonsActionCompletionStatus.Failed, retryFailure.Status, "La première reprise doit reproduire l’échec contrôlé.");
        Equal(AddonsRequestedAction.InstallSelection, retryFailure.Snapshot.PendingRetryAction,
            "La reprise mixte doit utiliser son état de lot tout en conservant les actions individuelles forcées.");
        AddonsPlanPreview secondRetry = environment.Coordinator.PreviewRetry();
        True(secondRetry.IsValid, "Un second aperçu doit reprendre les actions individuelles conservées.");
        SequenceEqual(firstRetry.Items.Select(item => $"{item.Id}:{item.Action}"), secondRetry.Items.Select(item => $"{item.Id}:{item.Action}"),
            "Le deuxième aperçu doit toujours annoncer exactement les installations et la réinstallation restantes.");

        int callsBefore = service.AppliedAddonIds.Count;
        AddonsActionCompletion completed = await CompleteAsync(environment.Coordinator.TryRetryFailed());
        Equal(AddonsActionCompletionStatus.Succeeded, completed.Status, "La seconde reprise doit achever le lot.");
        SequenceEqual(secondRetry.Items.Select(item => item.Id), service.AppliedAddonIds.Skip(callsBefore),
            "Les paquets effectivement appliqués doivent correspondre à l’aperçu de la seconde reprise.");
        SequenceEqual(secondRetry.Items.Where(item => item.Action is AddonsRequestedAction.Reinstall or AddonsRequestedAction.Repair)
                .Select(item => item.Id), service.ForcedAddonIds,
            "Chaque téléchargement forcé doit être annoncé dans l’aperçu, même après plusieurs reprises.");
        True(!environment.Coordinator.PreviewRetry().IsValid, "Une reprise achevée doit vider son aperçu.");
    }

    private static async Task CharacterizeVerificationFailureInvalidationAsync()
    {
        AddonPackage package = CreateFakePackage("verified-then-locked", "Vérification interrompue");
        FakeAddonManagementService service = new(CreateCatalog(package));
        service.SetInspection(package.Id, Managed(AddonLocalStatus.Installed, package.Version) with { HasFileManifest = true });
        service.VerificationResults[package.Id] = new(package.Id, AddonVerificationStatus.Verified, 3,
            [], [], [], DateTimeOffset.UtcNow);
        await using AddonsEnvironment environment = new(service, isGameRunning: false);
        await LoadCatalogAsync(environment.Coordinator);
        await CompleteAsync(environment.Coordinator.TryVerify(package.Id));
        True(AddonsStateAdapter.Project(environment.Coordinator.CurrentSnapshot).Catalog.Single().IsVerified,
            "Le premier contrôle réussi doit présenter une intégrité vérifiée.");

        service.VerificationFailure = new IOException("files became unreadable");
        List<AddonsRuntimeSnapshot> attempts = [];
        environment.Coordinator.SnapshotChanged += (_, args) => attempts.Add(args.Snapshot);
        int sessionCalls = environment.Session.PreparationCalls;
        AddonsActionCompletion failed = await CompleteAsync(environment.Coordinator.TryVerify(package.Id));
        Equal(AddonsActionCompletionStatus.Failed, failed.Status, "Le second contrôle doit signaler l’erreur de lecture.");
        Equal(AddonsErrorCategory.Disk, failed.Snapshot.Error.Category, "L’erreur de lecture doit rester identifiable.");
        True(attempts.Where(snapshot => snapshot.OperationId is not null).All(snapshot =>
                snapshot.Items.Single().VerificationStatus != AddonVerificationStatus.Verified
                && snapshot.Items.Single().VerifiedAtUtc is null),
            "L’ancienne réussite doit être invalidée dès le démarrage du nouveau contrôle.");
        True(failed.Snapshot.Items.Single().VerificationStatus == AddonVerificationStatus.NotVerified
                && failed.Snapshot.Items.Single().VerifiedAtUtc is null
                && !AddonsStateAdapter.Project(failed.Snapshot).Catalog.Single().IsVerified,
            "Un contrôle échoué ne doit pas conserver un badge Vérifié ni sa date antérieure.");
        environment.Coordinator.RefreshLocalState();
        Equal(AddonVerificationStatus.NotVerified, environment.Coordinator.CurrentSnapshot.Items.Single().VerificationStatus,
            "Une relecture locale ne doit pas ressusciter le résultat d’intégrité invalidé.");
        AddonsPlanPreview retry = environment.Coordinator.PreviewRetry();
        True(retry.IsValid && retry.Items.Single().Action == AddonsRequestedAction.Verify
                && failed.Snapshot.PendingRetryAction == AddonsRequestedAction.Verify,
            "L’aperçu de reprise d’un contrôle local doit conserver l’action Vérifier.");
        Equal(sessionCalls, environment.Session.PreparationCalls, "L’échec de vérification et son aperçu doivent rester sans accès réseau.");
    }

    private sealed class FakeLibraryDialogs : IAddonLibraryDialogs
    {
        public bool ConfirmInstall(Window owner, AddonsPlanPreview plan) => true;
        public string? OpenProfile(Window owner) => null;
        public string? SaveProfile(Window owner) => null;
    }

    private sealed class MemoryLibraryStore : IAddonLibraryStore
    {
        private AddonLibraryPreferences _preferences = AddonLibraryPreferences.Empty;
        public AddonLibraryPreferences Load(string clientRoot) => _preferences;
        public void Save(string clientRoot, AddonLibraryPreferences preferences) => _preferences = preferences;
    }
}
