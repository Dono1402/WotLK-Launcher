using System.Collections.Immutable;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

internal static class LauncherActivityCoordinatorTests
{
    internal static Task<int> RunAsync()
    {
        ProjectGameOperations();
        ProjectRepairPhaseChanges();
        ProjectAddonOperations();
        ProjectAddonBatch();
        ProjectCancellationAndTerminalResults();
        DelegateCancellationToExistingAuthority();
        RejectObsoleteCallbacksAcrossOperations();
        BoundAndOrderRuntimeHistory();
        PreserveHistoryWhileClosedAndAcrossUnrelatedState();
        ExcludeUntrackedOperations();
        StopObservingAfterDispose();
        Console.WriteLine("Launcher activity runtime aggregation OK (04B.2).");
        return Task.FromResult(0);
    }

    private static void ProjectGameOperations()
    {
        using Harness harness = new();
        foreach ((LauncherOperationType type, LauncherOperationKind kind) in new[]
        {
            (LauncherOperationType.GameInstall, LauncherOperationKind.GameInstall),
            (LauncherOperationType.GameUpdate, LauncherOperationKind.GameUpdate)
        })
        {
            long id = harness.NextOperationId();
            harness.Operations.Publish(Operation(
                harness.NextOperationSequence(), id, type, true, true));
            harness.Game.Publish(Game(
                harness.NextGameSequence(),
                id,
                kind,
                maintenancePhase: GameClientMaintenancePhase.Downloading,
                downloaded: 68,
                totalBytes: 100,
                speed: 18,
                remaining: TimeSpan.FromMinutes(2)));

            LauncherActivityOperationSnapshot active = RequiredActive(harness);
            Equal(type, active.OperationType, "Le type Jeu doit provenir du bail courant.");
            Equal(68d, active.Percent, "Le pourcentage Jeu doit provenir du snapshot runtime.");
            Equal(68L, active.BytesProcessed, "Les octets Jeu ne doivent pas être recalculés.");
            Equal(18d, active.BytesPerSecond, "Le débit Jeu doit être projeté tel quel.");
            Equal(TimeSpan.FromMinutes(2), active.Eta, "L’ETA Jeu doit être projetée telle quelle.");
            True(active.CanUserCancel, "Install/Update doit suivre CanUserCancel du bail.");
            True(harness.Coordinator.CurrentSnapshot.TopBarProgressKnown,
                "Une progression Jeu déterminée doit apparaître dans la top bar.");

            harness.Operations.Publish(Operation(
                harness.NextOperationSequence(), id, type, false, false));
        }

        long verifyId = harness.NextOperationId();
        harness.Operations.Publish(Operation(
            harness.NextOperationSequence(),
            verifyId,
            LauncherOperationType.GameVerify,
            true,
            false));
        harness.Game.Publish(Game(
            harness.NextGameSequence(),
            verifyId,
            LauncherOperationKind.Verify,
            verificationPhase: GameVerificationPhase.LoadingManifest));
        LauncherActivityOperationSnapshot verify = RequiredActive(harness);
        Equal(LauncherActivityProgressMode.Indeterminate, verify.ProgressMode,
            "Verify doit rester indéterminé sans comptage réel.");
        True(verify.Percent is null && !verify.CanUserCancel,
            "Verify ne doit fabriquer ni pourcentage ni annulation.");
        True(!harness.Coordinator.CurrentSnapshot.TopBarProgressKnown,
            "La top bar ne doit pas conserver un ancien pourcentage.");

        harness.Game.Publish(Game(
            harness.NextGameSequence(),
            verifyId,
            LauncherOperationKind.Verify,
            verificationPhase: GameVerificationPhase.ScanningFiles,
            processedFiles: 25,
            totalFiles: 100));
        verify = RequiredActive(harness);
        Equal(25d, verify.Percent, "Un vrai comptage Verify doit être projeté.");
        Equal(25, verify.FilesProcessed, "Le nombre de fichiers analysés doit rester brut.");
    }

    private static void ProjectRepairPhaseChanges()
    {
        using Harness harness = new();
        const long operationId = 41;
        harness.Operations.Publish(Operation(
            1,
            operationId,
            LauncherOperationType.GameRepair,
            true,
            true));
        harness.Game.Publish(Game(
            1,
            operationId,
            LauncherOperationKind.GameRepair,
            maintenancePhase: GameClientMaintenancePhase.FullVerification,
            processedFiles: 10,
            totalFiles: 50));
        Equal(LauncherActivityPhase.ScanningFiles, RequiredActive(harness).Phase,
            "Repair doit commencer par la phase réelle de vérification.");

        harness.Game.Publish(Game(
            2,
            operationId,
            LauncherOperationKind.GameRepair,
            maintenancePhase: GameClientMaintenancePhase.RepairDownloading,
            downloaded: 30,
            totalBytes: 100));
        LauncherActivityOperationSnapshot downloading = RequiredActive(harness);
        Equal(operationId, downloading.OperationId,
            "Le changement de phase Repair doit conserver OperationId.");
        Equal(LauncherActivityPhase.Downloading, downloading.Phase,
            "Repair doit refléter sa phase de téléchargement.");

        harness.Game.Publish(Game(
            3,
            operationId,
            LauncherOperationKind.GameRepair,
            maintenancePhase: GameClientMaintenancePhase.RepairApplying));
        Equal(LauncherActivityPhase.Applying, RequiredActive(harness).Phase,
            "Repair doit refléter sa phase d’application sans recréer l’opération.");
    }

    private static void ProjectAddonOperations()
    {
        foreach ((LauncherOperationType type, AddonsOperationState state, AddonsOperationPhase phase) in new[]
        {
            (LauncherOperationType.AddonInstall, AddonsOperationState.Installing, AddonsOperationPhase.Downloading),
            (LauncherOperationType.AddonUpdate, AddonsOperationState.Updating, AddonsOperationPhase.Downloading),
            (LauncherOperationType.AddonRepair, AddonsOperationState.Repairing, AddonsOperationPhase.Downloading),
            (LauncherOperationType.AddonRemove, AddonsOperationState.Removing, AddonsOperationPhase.Removing)
        })
        {
            using Harness harness = new();
            const long operationId = 8;
            bool canCancel = type != LauncherOperationType.AddonRemove;
            harness.Operations.Publish(Operation(1, operationId, type, true, canCancel));
            harness.Addons.Publish(Addons(
                sequence: 1,
                operationId,
                type,
                state,
                phase,
                activeId: "questie",
                canCancel,
                bytes: phase == AddonsOperationPhase.Downloading ? 50 : null,
                totalBytes: phase == AddonsOperationPhase.Downloading ? 100 : null));

            LauncherActivityOperationSnapshot active = RequiredActive(harness);
            Equal("Questie", active.DisplayName, "Le vrai nom addon doit être projeté.");
            Equal("questie", active.TargetId, "L’identifiant addon doit rester navigable.");
            Equal(LauncherActivityNavigationTarget.Addons, active.NavigationTarget,
                "Une activité addon doit cibler la page Addons.");
            if (type == LauncherOperationType.AddonRemove)
            {
                Equal(LauncherActivityProgressMode.Indeterminate, active.ProgressMode,
                    "AddonRemove doit rester indéterminé.");
                True(!active.CanUserCancel && active.Percent is null,
                    "AddonRemove ne doit inventer aucune annulation ou progression.");
            }
            else
            {
                Equal(50d, active.Percent, "La progression addon doit venir du runtime.");
                True(active.CanUserCancel, "L’annulation addon doit suivre le bail.");
            }
        }
    }

    private static void ProjectAddonBatch()
    {
        using Harness harness = new();
        const long operationId = 73;
        harness.Operations.Publish(Operation(
            1,
            operationId,
            LauncherOperationType.AddonBatchUpdate,
            true,
            true));
        harness.Addons.Publish(Addons(
            sequence: 1,
            operationId,
            LauncherOperationType.AddonBatchUpdate,
            AddonsOperationState.UpdatingAll,
            AddonsOperationPhase.Downloading,
            activeId: "questie",
            canCancel: true,
            bytes: 68,
            totalBytes: 100,
            pending: ["questie", "dbm", "details", "auctionator"],
            position: 1,
            total: 4));

        LauncherActivitySnapshot first = harness.Coordinator.CurrentSnapshot;
        Equal("Questie", RequiredActive(harness).DisplayName,
            "Le batch doit afficher l’addon réellement actif.");
        Equal(1, RequiredActive(harness).AddonPosition, "La position du batch doit venir des addons.");
        Equal(4, RequiredActive(harness).AddonTotal, "Le total du batch doit venir des addons.");
        True(!first.TopBarProgressKnown,
            "La progression de l’enfant ne doit jamais devenir le pourcentage global du batch.");
        SequenceEqual(["dbm", "details", "auctionator"],
            first.PendingItems.Select(item => item.TargetId),
            "La file d’attente doit exclure l’addon actif et garder son ordre.");

        harness.Addons.Publish(Addons(
            sequence: 2,
            operationId,
            LauncherOperationType.AddonBatchUpdate,
            AddonsOperationState.UpdatingAll,
            AddonsOperationPhase.Downloading,
            activeId: "dbm",
            canCancel: true,
            pending: ["dbm", "details", "auctionator"],
            position: 2,
            total: 4));
        Equal(operationId, RequiredActive(harness).OperationId,
            "Le prochain addon doit conserver l’OperationId global du batch.");
        Equal("Deadly Boss Mods", RequiredActive(harness).DisplayName,
            "Le produit actif doit passer au prochain addon.");
        Equal(2, RequiredActive(harness).AddonPosition,
            "La position suivante doit être projetée sans historique enfant.");
        True(harness.Coordinator.CurrentSnapshot.RecentItems.IsEmpty,
            "Un succès enfant ne doit pas produire d’entrée récente.");

        harness.Operations.Publish(Operation(
            2,
            operationId,
            LauncherOperationType.AddonBatchUpdate,
            false,
            false));
        harness.Addons.Publish(Addons(
            sequence: 3,
            operationId: null,
            LauncherOperationType.AddonBatchUpdate,
            AddonsOperationState.None,
            AddonsOperationPhase.None,
            activeId: string.Empty,
            canCancel: false,
            terminal: Terminal(
                operationId,
                LauncherOperationType.AddonBatchUpdate,
                LauncherOperationOutcome.Succeeded,
                "addon-batch",
                "4 addons")));
        Equal(1, harness.Coordinator.CurrentSnapshot.RecentItems.Length,
            "Le batch global doit produire une seule entrée terminale.");
    }

    private static void ProjectCancellationAndTerminalResults()
    {
        using Harness harness = new();
        const long operationId = 9;
        harness.Operations.Publish(Operation(
            1,
            operationId,
            LauncherOperationType.GameUpdate,
            true,
            true));
        harness.Game.Publish(Game(
            1,
            operationId,
            LauncherOperationKind.GameUpdate,
            maintenancePhase: GameClientMaintenancePhase.Downloading,
            downloaded: 25,
            totalBytes: 100));
        harness.Operations.Publish(Operation(
            2,
            operationId,
            LauncherOperationType.GameUpdate,
            true,
            false,
            LauncherOperationCancellationReason.User));
        LauncherActivityOperationSnapshot cancelling = RequiredActive(harness);
        True(cancelling.IsCancellationRequested && !cancelling.CanUserCancel,
            "La demande d’annulation doit désactiver immédiatement une seconde demande.");
        Equal(LauncherActivityPhase.Cancelling, cancelling.Phase,
            "L’état Annulation doit venir de la raison portée par le bail.");

        harness.Operations.Publish(Operation(
            3,
            operationId,
            LauncherOperationType.GameUpdate,
            false,
            false,
            LauncherOperationCancellationReason.User));
        harness.Game.Publish(Game(
            2,
            operationId,
            LauncherOperationKind.GameUpdate,
            terminal: Terminal(
                operationId,
                LauncherOperationType.GameUpdate,
                LauncherOperationOutcome.Cancelled,
                "wotlk-classic",
                "WotLK Classic",
                LauncherOperationCancellationReason.User)));
        LauncherActivitySnapshot cancelled = harness.Coordinator.CurrentSnapshot;
        True(cancelled.ActiveOperation is null, "Une opération terminale doit quitter En cours.");
        Equal(LauncherOperationOutcome.Cancelled, cancelled.RecentItems.Single().Outcome,
            "Le contrat terminal Cancelled doit être conservé.");

        const long failureId = 10;
        harness.Game.Publish(Game(
            3,
            failureId,
            LauncherOperationKind.GameRepair,
            terminal: Terminal(
                failureId,
                LauncherOperationType.GameRepair,
                LauncherOperationOutcome.Failed,
                "wotlk-classic",
                "WotLK Classic",
                errorCategory: "Integrity")));
        True(harness.Coordinator.CurrentSnapshot.HasRecentFailure,
            "Un terminal Failed doit alimenter le témoin d’échec récent.");

        harness.Game.Publish(Game(
            4,
            failureId,
            LauncherOperationKind.GameRepair,
            terminal: Terminal(
                failureId,
                LauncherOperationType.GameRepair,
                LauncherOperationOutcome.Failed,
                "wotlk-classic",
                "WotLK Classic",
                errorCategory: "Integrity")));
        Equal(2, harness.Coordinator.CurrentSnapshot.RecentItems.Length,
            "Un OperationId terminal ne doit apparaître qu’une fois.");
    }

    private static void RejectObsoleteCallbacksAcrossOperations()
    {
        using Harness harness = new();
        harness.Operations.Publish(Operation(1, 100, LauncherOperationType.GameUpdate, true, true));
        harness.Game.Publish(Game(
            1,
            100,
            LauncherOperationKind.GameUpdate,
            maintenancePhase: GameClientMaintenancePhase.Downloading,
            downloaded: 40,
            totalBytes: 100));

        harness.Operations.Publish(Operation(2, 100, LauncherOperationType.GameUpdate, false, false));
        harness.Operations.Publish(Operation(3, 101, LauncherOperationType.GameInstall, true, true));
        harness.Game.Publish(Game(
            2,
            101,
            LauncherOperationKind.GameInstall,
            maintenancePhase: GameClientMaintenancePhase.Downloading,
            downloaded: 10,
            totalBytes: 100));

        harness.Game.Publish(Game(
            3,
            100,
            LauncherOperationKind.GameUpdate,
            maintenancePhase: GameClientMaintenancePhase.Downloading,
            downloaded: 99,
            totalBytes: 100));
        LauncherActivityOperationSnapshot active = RequiredActive(harness);
        Equal(101L, active.OperationId,
            "Une progression tardive A ne doit jamais remplacer l’opération B.");
        Equal(10d, active.Percent,
            "Le contenu de B doit rester intact après un callback obsolète de A.");

        harness.Game.Publish(Game(
            4,
            100,
            LauncherOperationKind.GameUpdate,
            terminal: Terminal(
                100,
                LauncherOperationType.GameUpdate,
                LauncherOperationOutcome.Succeeded,
                "wotlk-classic",
                "WotLK Classic")));
        Equal(101L, RequiredActive(harness).OperationId,
            "Un terminal tardif A peut rejoindre Récent sans retirer B.");
        Equal(100L, harness.Coordinator.CurrentSnapshot.RecentItems.Single().OperationId,
            "Le résultat légitime A doit rester mémorisé.");
    }

    private static void DelegateCancellationToExistingAuthority()
    {
        using LauncherOperationCoordinator operations = new();
        LauncherOperationStartResult started = operations.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true,
            clientIsPlayable: true,
            operationType: LauncherOperationType.GameUpdate);
        LauncherOperationLease lease = started.Lease
            ?? throw new InvalidOperationException("Le bail GameUpdate était attendu.");
        ActivityUiState state = new(new ActivityViewState(
            IsPreview: false,
            ActiveOperation: new ActivityOperationUiItem(
                ProductName: "WotLK Classic",
                ActionName: "Mise à jour",
                PhaseText: "Téléchargement",
                ProgressPercent: 20,
                IsIndeterminate: false,
                TransferText: string.Empty,
                RateAndEtaText: string.Empty,
                DetailText: string.Empty,
                IconUri: string.Empty,
                HasIcon: false,
                CanUserCancel: true,
                IsCancellationRequested: false,
                ErrorMessage: string.Empty,
                BatchPosition: string.Empty,
                OperationId: lease.OperationId,
                TargetId: "wotlk-classic",
                NavigationTarget: ActivityNavigationTarget.Game),
            PendingOperations: ImmutableArray<ActivityPendingUiItem>.Empty,
            RecentOperations: ImmutableArray<ActivityRecentUiItem>.Empty));
        using WotLK.Launcher.UI.V2.Commands.ActivityCancelCommand command =
            new(operations, state);

        True(command.CanExecute(null),
            "La commande doit suivre le CanUserCancel projeté.");
        command.Execute(null);
        Equal(LauncherOperationCancellationReason.User, lease.CancellationReason,
            "ActivityCancelCommand doit déléguer au bail global existant.");
        True(lease.CancellationToken.IsCancellationRequested,
            "Aucune CTS locale ne doit remplacer l'annulation du bail.");
        True(!operations.CancelFromUser(),
            "Une deuxième annulation doit rester refusée par l'autorité métier.");
        lease.Dispose();
    }

    private static void BoundAndOrderRuntimeHistory()
    {
        using Harness harness = new();
        DateTimeOffset origin = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        for (int index = 1; index <= 50; index++)
        {
            harness.Game.Publish(Game(
                index,
                index,
                LauncherOperationKind.Verify,
                terminal: Terminal(
                    index,
                    LauncherOperationType.GameVerify,
                    index % 7 == 0
                        ? LauncherOperationOutcome.Failed
                        : LauncherOperationOutcome.Succeeded,
                    "wotlk-classic",
                    "WotLK Classic",
                    completedAt: origin.AddMinutes(index))));
        }

        LauncherActivitySnapshot snapshot = harness.Coordinator.CurrentSnapshot;
        Equal(10, snapshot.RecentItems.Length,
            "L’historique doit rester borné après 50 opérations.");
        SequenceEqual(Enumerable.Range(41, 10).Reverse().Select(value => (long)value),
            snapshot.RecentItems.Select(item => item.OperationId),
            "Le plus récent doit rester en tête et le plus ancien doit être supprimé.");
    }

    private static void PreserveHistoryWhileClosedAndAcrossUnrelatedState()
    {
        using Harness harness = new();
        ActivityUiState state = new(ActivityStateAdapter.Project(
            harness.Coordinator.CurrentSnapshot));
        using ActivityStateAdapter adapter = new(
            state,
            harness.Coordinator,
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        True(!state.IsOpen, "Le panneau témoin doit commencer fermé.");

        harness.Game.Publish(Game(
            1,
            1,
            LauncherOperationKind.Verify,
            terminal: Terminal(
                1,
                LauncherOperationType.GameVerify,
                LauncherOperationOutcome.Succeeded,
                "wotlk-classic",
                "WotLK Classic")));
        Equal(1, state.Current.RecentOperations.Length,
            "Le coordinator doit observer une fin lorsque le panneau est fermé.");

        harness.Addons.Publish(AddonsRuntimeSnapshot.Initial with { Sequence = 1 });
        Equal(1, harness.Coordinator.CurrentSnapshot.RecentItems.Length,
            "Un changement sans opération ne doit pas effacer l’historique de session.");
        Equal(ActivityNavigationTarget.Game,
            state.Current.RecentOperations.Single().NavigationTarget,
            "Une entrée Jeu doit conserver sa cible de navigation.");
    }

    private static void ExcludeUntrackedOperations()
    {
        using Harness harness = new();
        harness.Operations.Publish(Operation(1, 1, LauncherOperationType.Play, true, false));
        True(harness.Coordinator.CurrentSnapshot.ActiveOperation is null,
            "Play doit rester totalement exclu du centre d’activité.");

        harness.Game.Publish(Game(
            1,
            1,
            LauncherOperationKind.Play,
            terminal: Terminal(
                1,
                LauncherOperationType.Play,
                LauncherOperationOutcome.Succeeded,
                "wotlk-classic",
                "WotLK Classic")));
        True(harness.Coordinator.CurrentSnapshot.RecentItems.IsEmpty,
            "Play ne doit produire aucune entrée historique.");

        harness.Operations.Publish(Operation(
            2,
            2,
            LauncherOperationType.LauncherAutoUpdate,
            true,
            true));
        True(harness.Coordinator.CurrentSnapshot.ActiveOperation is null,
            "L'auto-update doit rester preview-only pendant 04B.2.");
        harness.Game.Publish(Game(
            2,
            2,
            LauncherOperationKind.LauncherAutoUpdate,
            terminal: Terminal(
                2,
                LauncherOperationType.LauncherAutoUpdate,
                LauncherOperationOutcome.Succeeded,
                "atlas-launcher",
                "Atlas Launcher")));
        True(harness.Coordinator.CurrentSnapshot.RecentItems.IsEmpty,
            "L'auto-update réel ne doit pas être connecté prématurément à l'historique.");
    }

    private static void StopObservingAfterDispose()
    {
        FakeOperationSource operations = new();
        FakeGameSource game = new();
        FakeAddonsSource addons = new();
        LauncherActivityCoordinator coordinator = new(operations, game, addons);
        int publications = 0;
        coordinator.SnapshotChanged += (_, _) => publications++;
        coordinator.Dispose();
        coordinator.Dispose();

        operations.Publish(Operation(1, 1, LauncherOperationType.GameUpdate, true, true));
        game.Publish(Game(
            1,
            1,
            LauncherOperationKind.GameUpdate,
            terminal: Terminal(
                1,
                LauncherOperationType.GameUpdate,
                LauncherOperationOutcome.Succeeded,
                "wotlk-classic",
                "WotLK Classic")));
        Equal(0, publications,
            "Un coordinateur libéré ne doit plus publier vers la présentation WPF.");
        True(coordinator.CurrentSnapshot.RecentItems.IsEmpty,
            "La libération doit détacher les trois sources sans tâche tardive.");
    }

    private static LauncherActivityOperationSnapshot RequiredActive(Harness harness) =>
        harness.Coordinator.CurrentSnapshot.ActiveOperation
        ?? throw new InvalidOperationException("Une opération active était attendue.");

    private static LauncherOperationActivitySnapshot Operation(
        long sequence,
        long operationId,
        LauncherOperationType operationType,
        bool active,
        bool canCancel,
        LauncherOperationCancellationReason cancellationReason =
            LauncherOperationCancellationReason.None) => new(
        sequence,
        active ? operationId : null,
        active ? operationType : null,
        active,
        canCancel,
        IsShuttingDown: false,
        cancellationReason);

    private static GameRuntimeSnapshot Game(
        long sequence,
        long operationId,
        LauncherOperationKind kind,
        GameVerificationPhase verificationPhase = GameVerificationPhase.Stable,
        GameClientMaintenancePhase? maintenancePhase = null,
        long? downloaded = null,
        long? totalBytes = null,
        double? speed = null,
        TimeSpan? remaining = null,
        int? processedFiles = null,
        int? totalFiles = null,
        OperationTerminalResult? terminal = null) => new(
        Sequence: sequence,
        OperationId: operationId,
        Action: kind == LauncherOperationKind.GameInstall ? GameAction.Install : GameAction.Play,
        UpdateKnowledge: GameUpdateKnowledge.Unknown,
        Phase: verificationPhase,
        IsVerifying: kind == LauncherOperationKind.Verify,
        CanVerify: false,
        IsPlayable: kind != LauncherOperationKind.GameInstall,
        InstallPath: "C:\\Atlas\\WotLK",
        InstalledVersion: "3.3.5a",
        AvailableVersion: "3.3.5a-atlas",
        ProcessedFileCount: processedFiles,
        TotalFileCount: totalFiles,
        FailureCategory: null,
        OperationKind: kind,
        MaintenancePhase: maintenancePhase,
        CanPrimaryAction: false,
        CanUserCancel: kind != LauncherOperationKind.Verify,
        DownloadedBytes: downloaded,
        TotalBytes: totalBytes,
        BytesPerSecond: speed,
        Remaining: remaining,
        TerminalResult: terminal);

    private static AddonsRuntimeSnapshot Addons(
        long sequence,
        long? operationId,
        LauncherOperationType operationType,
        AddonsOperationState state,
        AddonsOperationPhase phase,
        string activeId,
        bool canCancel,
        long? bytes = null,
        long? totalBytes = null,
        ImmutableArray<string> pending = default,
        int? position = null,
        int? total = null,
        OperationTerminalResult? terminal = null)
    {
        ImmutableArray<AddonRuntimeItem> items =
        [
            Addon("questie", "Questie"),
            Addon("dbm", "Deadly Boss Mods"),
            Addon("details", "Details!"),
            Addon("auctionator", "Auctionator")
        ];
        return AddonsRuntimeSnapshot.Initial with
        {
            Sequence = sequence,
            OperationId = operationId,
            Items = items,
            LoadState = AddonsCatalogLoadState.Loaded,
            OperationState = state,
            OperationPhase = phase,
            ActiveAddonId = activeId,
            PendingAddonIds = pending.IsDefault ? ImmutableArray<string>.Empty : pending,
            Progress = new AddonsRuntimeProgress(
                activeId,
                phase,
                bytes,
                totalBytes,
                bytes is > 0 ? 8 : null,
                bytes is > 0 ? TimeSpan.FromSeconds(5) : null),
            IsAuthenticated = true,
            IsClientPlayable = true,
            CanCancel = canCancel,
            TerminalResult = terminal,
            ActiveAddonPosition = position,
            ActiveAddonTotal = total
        };
    }

    private static AddonRuntimeItem Addon(string id, string name) => new(
        id,
        name,
        "Description",
        "Interface",
        "1.0",
        "0.9",
        string.Empty,
        null,
        "30403",
        "Atlas",
        ImmutableArray<string>.Empty,
        ImmutableArray<string>.Empty,
        AddonLocalStatus.UpdateAvailable,
        IsManaged: true,
        AddonsOperationState.None,
        AddonsRequestedAction.None,
        AddonsErrorCategory.None);

    private static OperationTerminalResult Terminal(
        long operationId,
        LauncherOperationType operationType,
        LauncherOperationOutcome outcome,
        string targetId,
        string targetName,
        LauncherOperationCancellationReason cancellationReason =
            LauncherOperationCancellationReason.None,
        string? errorCategory = null,
        DateTimeOffset? completedAt = null) => new(
        operationId,
        operationType,
        outcome,
        completedAt ?? DateTimeOffset.UtcNow.AddSeconds(operationId),
        cancellationReason,
        errorCategory,
        new LauncherOperationDisplayContext(targetId, targetName));

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Harness : IDisposable
    {
        private long _operationId;
        private long _operationSequence;
        private long _gameSequence;

        internal Harness()
        {
            Coordinator = new LauncherActivityCoordinator(Operations, Game, Addons);
        }

        internal FakeOperationSource Operations { get; } = new();

        internal FakeGameSource Game { get; } = new();

        internal FakeAddonsSource Addons { get; } = new();

        internal LauncherActivityCoordinator Coordinator { get; }

        internal long NextOperationId() => ++_operationId;

        internal long NextOperationSequence() => ++_operationSequence;

        internal long NextGameSequence() => ++_gameSequence;

        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class FakeOperationSource : ILauncherOperationActivitySource
    {
        public event EventHandler<LauncherOperationActivitySnapshotEventArgs>? SnapshotChanged;

        public LauncherOperationActivitySnapshot CurrentSnapshot { get; private set; } =
            LauncherOperationActivitySnapshot.Initial;

        internal void Publish(LauncherOperationActivitySnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, new LauncherOperationActivitySnapshotEventArgs(snapshot));
        }
    }

    private sealed class FakeGameSource : IGameActivitySource
    {
        public event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

        public GameRuntimeSnapshot CurrentSnapshot { get; private set; } = Game(
            0,
            0,
            LauncherOperationKind.Verify);

        internal void Publish(GameRuntimeSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, new GameRuntimeSnapshotEventArgs(snapshot));
        }
    }

    private sealed class FakeAddonsSource : IAddonsActivitySource
    {
        public event EventHandler<AddonsRuntimeSnapshotEventArgs>? SnapshotChanged;

        public AddonsRuntimeSnapshot CurrentSnapshot { get; private set; } =
            AddonsRuntimeSnapshot.Initial;

        internal void Publish(AddonsRuntimeSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(this, new AddonsRuntimeSnapshotEventArgs(snapshot));
        }
    }
}
