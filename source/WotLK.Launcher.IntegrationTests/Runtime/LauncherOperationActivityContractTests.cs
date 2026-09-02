using System.Collections.Immutable;
using System.Net.Http;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;

internal static class LauncherOperationActivityContractTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeObservableLeaseProjection();
        CharacterizeActivityHistoryBoundary();
        CharacterizeConcurrencyMatrix();
        CharacterizeLegacyAutoUpdateLease();
        await CharacterizeGameTerminalMatrixAsync();
        await CharacterizeAddonTerminalMatrixAsync();
        await CharacterizeAddonBatchAndStaleCallbacksAsync();
        await CharacterizeCancellationPrecedenceAsync();
        Console.WriteLine("Launcher operation activity contracts OK (04B.0).");
        return 0;
    }

    private static void CharacterizeActivityHistoryBoundary()
    {
        Equal(10, LauncherOperationActivityPolicy.RecentHistoryLimit,
            "L'historique futur doit rester limité à dix résultats en mémoire.");
        LauncherOperationType[] tracked =
        [
            LauncherOperationType.GameInstall,
            LauncherOperationType.GameUpdate,
            LauncherOperationType.GameVerify,
            LauncherOperationType.GameRepair,
            LauncherOperationType.AddonInstall,
            LauncherOperationType.AddonUpdate,
            LauncherOperationType.AddonRepair,
            LauncherOperationType.AddonRemove,
            LauncherOperationType.AddonBatchUpdate,
            LauncherOperationType.LauncherAutoUpdate
        ];
        True(tracked.All(LauncherOperationActivityPolicy.IsTracked),
            "Toutes les opérations de maintenance produit doivent être suivies.");
        True(!LauncherOperationActivityPolicy.IsTracked(LauncherOperationType.Play),
            "Play ne doit produire aucune activité ni entrée récente.");
        True(!LauncherOperationActivityPolicy.IsTracked(LauncherOperationType.AvatarUpload),
            "Les transferts de profil ne sont pas des opérations du centre d'activité.");
        True(!LauncherOperationActivityPolicy.IsTracked(LauncherOperationType.AccountPasswordChange),
            "Les actions de compte ne sont pas des opérations du centre d'activité.");
        True(!LauncherOperationActivityPolicy.IsTracked(LauncherOperationType.AddonSynchronization),
            "Le type legacy agrégé ne doit pas alimenter la future V2.");
    }

    private static void CharacterizeObservableLeaseProjection()
    {
        using LauncherOperationCoordinator operations = new();
        List<LauncherOperationActivitySnapshot> published = [];
        operations.ActivityChanged += (_, args) => published.Add(args.Snapshot);

        Equal(LauncherOperationActivitySnapshot.Initial, operations.CurrentActivitySnapshot,
            "Le contrat observable doit commencer inactif.");
        LauncherOperationStartResult first = operations.TryBegin(
            LauncherOperationKind.GameInstall,
            canUserCancel: true);
        True(first.IsStarted, "GameInstall doit démarrer immédiatement.");
        LauncherOperationLease firstLease = first.Lease!;
        LauncherOperationActivitySnapshot active = operations.CurrentActivitySnapshot;
        True(active.IsActive, "Le bail actif doit être observable.");
        Equal(firstLease.OperationId, active.OperationId, "OperationId doit rester celui du bail.");
        Equal(LauncherOperationType.GameInstall, active.OperationType,
            "Le type observable doit être métier et précis.");
        True(active.CanUserCancel, "Une installation doit être annulable au départ.");

        Equal(LauncherOperationStartStatus.Busy,
            operations.TryBegin(LauncherOperationKind.Addons, true).Status,
            "Un second clic doit être refusé sans file d'attente.");
        Equal(firstLease.OperationId, operations.CurrentActivitySnapshot.OperationId,
            "Un refus ne doit pas remplacer l'opération active.");

        True(firstLease.CancelFromUser(), "L'annulation utilisateur initiale doit réussir.");
        Equal(LauncherOperationCancellationReason.User, firstLease.CancellationReason,
            "Le bail doit mémoriser l'origine utilisateur.");
        True(!operations.CurrentActivitySnapshot.CanUserCancel,
            "Une annulation déjà demandée ne doit plus être proposée.");
        Equal(LauncherOperationCancellationReason.User,
            operations.CurrentActivitySnapshot.CancellationReason,
            "Le snapshot observable doit porter l'origine de l'annulation.");
        True(!firstLease.CancelFromUser(), "La double annulation doit être idempotente.");
        firstLease.Complete();
        True(!operations.CurrentActivitySnapshot.IsActive,
            "La fin du bail doit être publiée explicitement.");

        LauncherOperationLease secondLease = operations.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true).Lease!;
        True(secondLease.OperationId > firstLease.OperationId,
            "Un OperationId terminé ne doit jamais être réutilisé.");
        True(secondLease.DisableUserCancellation(),
            "Une phase finale doit pouvoir retirer l'annulation utilisateur.");
        True(!operations.CurrentActivitySnapshot.CanUserCancel,
            "Le retrait dynamique doit être immédiatement observable.");
        secondLease.Complete();

        Equal(LauncherOperationStartStatus.RejectedByCompatibility,
            operations.TryBegin(
                LauncherOperationKind.Addons,
                true,
                operationType: LauncherOperationType.GameInstall).Status,
            "Un type métier incompatible avec le bail doit être refusé.");
        True(published.Zip(published.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence),
            "Les snapshots d'activité doivent avoir une séquence strictement croissante.");

        int beforePlay = published.Count;
        LauncherOperationLease play = operations.TryBeginPlay(clientIsPlayable: true).Lease!;
        Equal(beforePlay, published.Count,
            "Play ne doit pas apparaître comme activité de maintenance.");
        play.Complete();

        LauncherOperationLease shutdownLease = operations.TryBegin(
            LauncherOperationKind.Verify,
            canUserCancel: false,
            clientIsPlayable: true).Lease!;
        operations.CancelForShutdown();
        LauncherOperationActivitySnapshot shuttingDown = operations.CurrentActivitySnapshot;
        True(shuttingDown.IsShuttingDown, "La fermeture doit être observable.");
        True(!shuttingDown.CanUserCancel, "La fermeture ne doit pas exposer d'annulation utilisateur.");
        Equal(LauncherOperationCancellationReason.Shutdown, shuttingDown.CancellationReason,
            "Le snapshot observable doit distinguer une fermeture d'un clic Annuler.");
        Equal(LauncherOperationCancellationReason.Shutdown, shutdownLease.CancellationReason,
            "Le bail doit distinguer l'annulation de cycle de vie.");
        shutdownLease.Complete();
    }

    private static void CharacterizeConcurrencyMatrix()
    {
        LauncherOperationKind[] maintenanceKinds =
        [
            LauncherOperationKind.GameInstall,
            LauncherOperationKind.GameUpdate,
            LauncherOperationKind.GameRepair,
            LauncherOperationKind.Addons,
            LauncherOperationKind.LauncherAutoUpdate
        ];

        foreach (LauncherOperationKind activeKind in maintenanceKinds)
        {
            using LauncherOperationCoordinator operations = new();
            LauncherOperationLease active = operations.TryBegin(activeKind, true).Lease!;
            foreach (LauncherOperationKind contender in maintenanceKinds)
            {
                Equal(LauncherOperationStartStatus.Busy,
                    operations.TryBegin(contender, true).Status,
                    $"{contender} doit être refusé immédiatement pendant {activeKind}.");
            }
            Equal(LauncherOperationStartStatus.RejectedByCompatibility,
                operations.TryBeginPlay(clientIsPlayable: true).Status,
                $"Play doit être refusé pendant {activeKind}.");
            active.Complete();
        }

        using (LauncherOperationCoordinator operations = new())
        {
            LauncherOperationLease play = operations.TryBeginPlay(clientIsPlayable: true).Lease!;
            LauncherOperationStartResult verify = operations.TryBegin(
                LauncherOperationKind.Verify,
                canUserCancel: false,
                clientIsPlayable: true);
            True(verify.IsStarted, "Verify et Play doivent pouvoir coexister si le client est jouable.");
            Equal(LauncherOperationStartStatus.Busy,
                operations.TryBegin(LauncherOperationKind.Addons, true).Status,
                "Addons doit être refusé pendant Verify + Play.");
            Equal(LauncherOperationStartStatus.Busy,
                operations.TryBeginPlay(clientIsPlayable: true).Status,
                "Play doit rester single-flight.");
            verify.Lease!.Complete();
            play.Complete();
        }

        using (LauncherOperationCoordinator operations = new())
        {
            LauncherOperationLease verify = operations.TryBegin(
                LauncherOperationKind.Verify,
                canUserCancel: false,
                clientIsPlayable: true).Lease!;
            True(operations.TryBeginPlay(clientIsPlayable: true).IsStarted,
                "La compatibilité Verify + Play doit être symétrique.");
            verify.Complete();
        }
    }

    private static void CharacterizeLegacyAutoUpdateLease()
    {
        using LauncherOperationCoordinator background = new();
        LauncherOperationLease check = background.TryBegin(
            LauncherOperationKind.LauncherAutoUpdate,
            canUserCancel: false).Lease!;
        Equal(LauncherOperationType.LauncherAutoUpdate, check.OperationType,
            "Le check périodique doit exposer LauncherAutoUpdate.");
        True(!background.CurrentActivitySnapshot.CanUserCancel,
            "Le check périodique legacy n'est pas annulable par l'utilisateur.");
        check.Complete();

        using LauncherOperationCoordinator manual = new();
        LauncherOperationLease update = manual.TryBegin(
            LauncherOperationKind.LauncherAutoUpdate,
            canUserCancel: true).Lease!;
        True(manual.CurrentActivitySnapshot.CanUserCancel,
            "Le téléchargement manuel legacy reste annulable.");
        True(update.CancelFromUser(), "Le téléchargement manuel doit accepter l'annulation.");
        update.Complete();
    }

    private static async Task CharacterizeGameTerminalMatrixAsync()
    {
        foreach (GameContractOperation operation in Enum.GetValues<GameContractOperation>())
        {
            await using (GameContractHarness success = await GameContractHarness.CreateAsync(operation))
            {
                success.ConfigureSuccess(operation);
                await success.StartAndWaitAsync(operation);
                AssertTerminal(success.Coordinator.CurrentSnapshot.TerminalResult,
                    ToOperationType(operation), LauncherOperationOutcome.Succeeded,
                    LauncherOperationCancellationReason.None,
                    $"succès {operation}");
            }

            await using (GameContractHarness failure = await GameContractHarness.CreateAsync(operation))
            {
                failure.ConfigureFailure(operation);
                await failure.StartAndWaitAsync(operation);
                OperationTerminalResult terminal = RequiredTerminal(
                    failure.Coordinator.CurrentSnapshot.TerminalResult,
                    $"erreur {operation}");
                Equal(ToOperationType(operation), terminal.OperationType,
                    $"Le type terminal d'erreur {operation} est incorrect.");
                Equal(LauncherOperationOutcome.Failed, terminal.Outcome,
                    $"L'erreur {operation} doit être explicite.");
                True(!string.IsNullOrWhiteSpace(terminal.ErrorCategory),
                    $"L'erreur {operation} doit exposer une catégorie sûre.");
            }

            if (operation != GameContractOperation.Verify)
            {
                await using GameContractHarness cancelled = await GameContractHarness.CreateAsync(operation);
                TaskCompletionSource started = cancelled.ConfigureBlocking(operation);
                cancelled.Start(operation);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                True(cancelled.Operations.CancelFromUser(),
                    $"{operation} doit accepter l'annulation utilisateur.");
                await cancelled.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
                AssertTerminal(cancelled.Coordinator.CurrentSnapshot.TerminalResult,
                    ToOperationType(operation), LauncherOperationOutcome.Cancelled,
                    LauncherOperationCancellationReason.User,
                    $"annulation {operation}");
            }

            await using (GameContractHarness shutdown = await GameContractHarness.CreateAsync(operation))
            {
                TaskCompletionSource started = shutdown.ConfigureBlocking(operation);
                shutdown.Start(operation);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                if (operation == GameContractOperation.Verify)
                {
                    GameRuntimeSnapshot verifying = shutdown.Coordinator.CurrentSnapshot;
                    True(verifying.ProcessedFileCount is null && verifying.TotalFileCount is null,
                        "Verify doit rester indéterminé sans comptage métier réel.");
                    True(!shutdown.Operations.CurrentActivitySnapshot.CanUserCancel,
                        "Verify ne doit pas proposer d'annulation utilisateur.");
                }
                int publishedBeforeShutdown = shutdown.PublishedSnapshots;
                shutdown.Coordinator.BeginShutdown();
                await shutdown.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
                AssertTerminal(shutdown.Coordinator.CurrentSnapshot.TerminalResult,
                    ToOperationType(operation), LauncherOperationOutcome.Cancelled,
                    LauncherOperationCancellationReason.Shutdown,
                    $"shutdown {operation}");
                Equal(publishedBeforeShutdown, shutdown.PublishedSnapshots,
                    $"{operation} ne doit publier aucun snapshot WPF après shutdown.");
            }
        }
    }

    private static async Task CharacterizeAddonTerminalMatrixAsync()
    {
        foreach (AddonContractOperation operation in Enum.GetValues<AddonContractOperation>())
        {
            await using (AddonContractHarness success = await AddonContractHarness.CreateAsync(operation))
            {
                AddonsActionStartResult start = success.Start(operation);
                AddonsActionCompletion completion = await RequiredCompletion(start);
                AssertTerminal(completion.TerminalResult,
                    ToOperationType(operation), LauncherOperationOutcome.Succeeded,
                    LauncherOperationCancellationReason.None,
                    $"succès addon {operation}");
                Equal(completion.TerminalResult, completion.Snapshot.TerminalResult,
                    "Le résultat rendu et le snapshot doivent partager le même terminal.");
            }

            await using (AddonContractHarness failure = await AddonContractHarness.CreateAsync(operation))
            {
                failure.Service.NextFailure = new IOException("activity-contract-failure");
                AddonsActionCompletion completion = await RequiredCompletion(failure.Start(operation));
                AssertTerminal(completion.TerminalResult,
                    ToOperationType(operation), LauncherOperationOutcome.Failed,
                    LauncherOperationCancellationReason.None,
                    $"erreur addon {operation}");
                True(!string.IsNullOrWhiteSpace(completion.TerminalResult!.ErrorCategory),
                    "Une erreur addon doit fournir une catégorie sûre.");
            }

            if (operation != AddonContractOperation.Remove)
            {
                await using AddonContractHarness cancelled = await AddonContractHarness.CreateAsync(operation);
                TaskCompletionSource started = cancelled.Service.BlockNextApply();
                AddonsActionStartResult start = cancelled.Start(operation);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                True(cancelled.Coordinator.CancelCurrent(),
                    $"L'opération addon {operation} doit être annulable.");
                AddonsActionCompletion completion = await RequiredCompletion(start);
                AssertTerminal(completion.TerminalResult,
                    ToOperationType(operation), LauncherOperationOutcome.Cancelled,
                    LauncherOperationCancellationReason.User,
                    $"annulation addon {operation}");
            }
        }

        await using AddonContractHarness removal = await AddonContractHarness.CreateAsync(
            AddonContractOperation.Remove);
        TaskCompletionSource removeStarted = removal.Service.BlockNextApply(ignoreCancellation: true);
        AddonsActionStartResult remove = removal.Start(AddonContractOperation.Remove);
        await removeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        AddonsRuntimeSnapshot removing = removal.Coordinator.CurrentSnapshot;
        Equal(AddonsOperationPhase.Removing, removing.OperationPhase,
            "Remove doit exposer sa phase indéterminée.");
        True(removing.Progress.IsIndeterminate, "Remove ne doit fabriquer aucun pourcentage.");
        True(!removing.CanCancel && !removal.Operations.CurrentActivitySnapshot.CanUserCancel,
            "Remove doit exposer CanUserCancel=false.");
        True(!removal.Coordinator.CancelCurrent(), "Remove doit refuser l'annulation utilisateur.");
        removal.Service.ReleaseBlockedApply();
        AddonsActionCompletion removed = await RequiredCompletion(remove);
        AssertTerminal(removed.TerminalResult, LauncherOperationType.AddonRemove,
            LauncherOperationOutcome.Succeeded, LauncherOperationCancellationReason.None,
            "suppression addon");

        await using AddonContractHarness shutdown = await AddonContractHarness.CreateAsync(
            AddonContractOperation.Install);
        TaskCompletionSource shutdownStarted = shutdown.Service.BlockNextApply();
        AddonsActionStartResult shutdownStart = shutdown.Start(AddonContractOperation.Install);
        await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        int beforeShutdown = shutdown.PublishedSnapshots;
        shutdown.Coordinator.BeginShutdown();
        AddonsActionCompletion shutdownCompletion = await RequiredCompletion(shutdownStart);
        AssertTerminal(shutdownCompletion.TerminalResult, LauncherOperationType.AddonInstall,
            LauncherOperationOutcome.Cancelled, LauncherOperationCancellationReason.Shutdown,
            "shutdown addon");
        Equal(beforeShutdown, shutdown.PublishedSnapshots,
            "Aucun snapshot addon ne doit être publié après shutdown.");
    }

    private static async Task CharacterizeAddonBatchAndStaleCallbacksAsync()
    {
        await using (AddonContractHarness batch = await AddonContractHarness.CreateBatchAsync())
        {
            TaskCompletionSource firstStarted = batch.Service.BlockNextApply(ignoreCancellation: true);
            AddonsActionStartResult start = batch.Coordinator.TryUpdateAll();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            AddonsRuntimeSnapshot active = batch.Coordinator.CurrentSnapshot;
            Equal(start.OperationId, active.OperationId, "Le batch doit garder un OperationId global.");
            Equal(LauncherOperationType.AddonBatchUpdate,
                batch.Operations.CurrentActivitySnapshot.OperationType,
                "Tout mettre à jour doit exposer AddonBatchUpdate.");
            Equal(2, active.PendingAddonIds.Length,
                "Les addons en attente doivent rester des enfants visuels du batch.");
            batch.Service.ReleaseBlockedApply();
            AddonsActionCompletion completion = await RequiredCompletion(start);
            Equal(2, batch.Service.ApplyCalls, "Le batch doit rester séquentiel.");
            AssertTerminal(completion.TerminalResult, LauncherOperationType.AddonBatchUpdate,
                LauncherOperationOutcome.Succeeded, LauncherOperationCancellationReason.None,
                "batch réussi");
        }

        await using (AddonContractHarness failedBatch = await AddonContractHarness.CreateBatchAsync())
        {
            failedBatch.Service.NextFailure = new IOException("first-addon-failed");
            AddonsActionCompletion completion = await RequiredCompletion(
                failedBatch.Coordinator.TryUpdateAll());
            Equal(1, failedBatch.Service.ApplyCalls,
                "Le batch doit conserver l'arrêt au premier échec.");
            AssertTerminal(completion.TerminalResult, LauncherOperationType.AddonBatchUpdate,
                LauncherOperationOutcome.Failed, LauncherOperationCancellationReason.None,
                "batch en erreur");
        }

        await using (AddonContractHarness cancelledBatch = await AddonContractHarness.CreateBatchAsync())
        {
            TaskCompletionSource firstStarted = cancelledBatch.Service.BlockNextApply();
            AddonsActionStartResult start = cancelledBatch.Coordinator.TryUpdateAll();
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            True(cancelledBatch.Coordinator.CancelCurrent(),
                "Le batch doit accepter une annulation utilisateur unique.");
            AddonsActionCompletion completion = await RequiredCompletion(start);
            Equal(1, cancelledBatch.Service.ApplyCalls,
                "L'annulation du batch ne doit pas démarrer l'enfant suivant.");
            AssertTerminal(completion.TerminalResult, LauncherOperationType.AddonBatchUpdate,
                LauncherOperationOutcome.Cancelled, LauncherOperationCancellationReason.User,
                "batch annulé");
        }

        await using AddonContractHarness stale = await AddonContractHarness.CreateStaleAsync();
        IProgress<AddonTransferProgress>? oldProgress = null;
        stale.Service.ApplyBehavior = (call, token) =>
        {
            if (call.Package.Id == "addon-a")
            {
                oldProgress = call.Progress;
                return Task.CompletedTask;
            }
            return stale.Service.WaitOnManualGateAsync(token);
        };
        await RequiredCompletion(stale.Coordinator.TryInvokePrimary("addon-a"));
        AddonsActionStartResult current = stale.Coordinator.TryInvokePrimary("addon-b");
        await stale.Service.ManualGateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        long currentId = current.OperationId!.Value;
        oldProgress!.Report(new AddonTransferProgress("Addon A", 100, 100));
        AddonsRuntimeSnapshot afterStale = stale.Coordinator.CurrentSnapshot;
        Equal(currentId, afterStale.OperationId,
            "Un callback ancien ne doit pas modifier l'OperationId courant.");
        Equal("addon-b", afterStale.ActiveAddonId,
            "Un callback ancien ne doit pas remplacer le contexte enfant courant.");
        True(afterStale.TerminalResult is null,
            "Le terminal de l'opération A ne doit pas contaminer B.");
        stale.Service.ReleaseManualGate();
        await RequiredCompletion(current);
    }

    private static async Task CharacterizeCancellationPrecedenceAsync()
    {
        await using (GameContractHarness game = await GameContractHarness.CreateAsync(
                         GameContractOperation.Install))
        {
            TaskCompletionSource started = NewSignal();
            TaskCompletionSource release = NewSignal();
            game.Maintenance.Handler = async (_, lease, _) =>
            {
                started.TrySetResult();
                await release.Task;
                game.LocalState = new GameClientLocalState(
                    game.Root,
                    "frFR",
                    true,
                    "remote-v2",
                    GameUpdateKnowledge.Known);
                return new GameClientMaintenanceResult(
                    lease.OperationId,
                    GameClientMaintenanceOutcome.Downloaded,
                    "remote-v2",
                    1,
                    0,
                    null,
                    null);
            };

            game.Start(GameContractOperation.Install);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            True(game.Operations.CancelFromUser(),
                "L'annulation Jeu témoin doit être acceptée.");
            release.TrySetResult();
            await game.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
            AssertTerminal(game.Coordinator.CurrentSnapshot.TerminalResult,
                LauncherOperationType.GameInstall,
                LauncherOperationOutcome.Cancelled,
                LauncherOperationCancellationReason.User,
                "succès Jeu tardif après annulation");
        }

        await using AddonContractHarness addon = await AddonContractHarness.CreateAsync(
            AddonContractOperation.Install);
        TaskCompletionSource addonStarted = NewSignal();
        TaskCompletionSource addonRelease = NewSignal();
        addon.Service.ApplyBehavior = async (_, _) =>
        {
            addonStarted.TrySetResult();
            await addonRelease.Task;
            throw new IOException("late-failure-after-cancellation");
        };
        AddonsActionStartResult addonStart = addon.Start(AddonContractOperation.Install);
        await addonStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        True(addon.Coordinator.CancelCurrent(),
            "L'annulation Addons témoin doit être acceptée.");
        addonRelease.TrySetResult();
        AddonsActionCompletion addonCompletion = await RequiredCompletion(addonStart);
        AssertTerminal(addonCompletion.TerminalResult,
            LauncherOperationType.AddonInstall,
            LauncherOperationOutcome.Cancelled,
            LauncherOperationCancellationReason.User,
            "erreur Addons tardive après annulation");
    }

    private static async Task<AddonsActionCompletion> RequiredCompletion(
        AddonsActionStartResult start)
    {
        True(start.IsStarted, $"L'action addon devait démarrer ({start.Status}).");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static OperationTerminalResult RequiredTerminal(
        OperationTerminalResult? terminal,
        string context)
    {
        return terminal ?? throw new InvalidOperationException(
            $"Résultat terminal absent pour {context}.");
    }

    private static void AssertTerminal(
        OperationTerminalResult? candidate,
        LauncherOperationType type,
        LauncherOperationOutcome outcome,
        LauncherOperationCancellationReason cancellationReason,
        string context)
    {
        OperationTerminalResult terminal = RequiredTerminal(candidate, context);
        True(terminal.OperationId > 0, $"OperationId terminal invalide pour {context}.");
        Equal(type, terminal.OperationType, $"OperationType incorrect pour {context}.");
        Equal(outcome, terminal.Outcome, $"Outcome incorrect pour {context}.");
        Equal(cancellationReason, terminal.CancellationReason,
            $"Origine d'annulation incorrecte pour {context}.");
        True(terminal.CompletedAt != default, $"CompletedAt absent pour {context}.");
        True(terminal.DisplayContext is not null,
            $"Le contexte d'affichage minimal est absent pour {context}.");
        True(typeof(OperationTerminalResult).GetProperties().All(property =>
                !typeof(Exception).IsAssignableFrom(property.PropertyType)
                && property.PropertyType != typeof(Uri)),
            "Le contrat terminal ne doit pouvoir transporter ni exception ni URL brute.");
    }

    private static LauncherOperationType ToOperationType(GameContractOperation operation) =>
        operation switch
        {
            GameContractOperation.Install => LauncherOperationType.GameInstall,
            GameContractOperation.Update => LauncherOperationType.GameUpdate,
            GameContractOperation.Verify => LauncherOperationType.GameVerify,
            GameContractOperation.Repair => LauncherOperationType.GameRepair,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static LauncherOperationType ToOperationType(AddonContractOperation operation) =>
        operation switch
        {
            AddonContractOperation.Install => LauncherOperationType.AddonInstall,
            AddonContractOperation.Update => LauncherOperationType.AddonUpdate,
            AddonContractOperation.Repair => LauncherOperationType.AddonRepair,
            AddonContractOperation.Remove => LauncherOperationType.AddonRemove,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; actuel={actual}.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private enum GameContractOperation
    {
        Install,
        Update,
        Verify,
        Repair
    }

    private enum AddonContractOperation
    {
        Install,
        Update,
        Repair,
        Remove
    }

    private sealed class GameContractHarness : IAsyncDisposable
    {
        private GameContractHarness(bool playable)
        {
            Root = Path.Combine(Path.GetTempPath(), "AtlasActivityGame", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Settings = new LauncherSettings
            {
                InstallPath = Root,
                ManifestUrl = "https://atlas.test/manifest.json",
                GameLocale = "frFR",
                AutomaticLauncherUpdates = false
            };
            LocalState = new GameClientLocalState(
                Root,
                "frFR",
                playable,
                playable ? "local-v1" : null,
                GameUpdateKnowledge.Unknown);
            Verification = new ActivityVerificationService();
            Maintenance = new ActivityMaintenanceService();
            Operations = new LauncherOperationCoordinator();
            Coordinator = new GameRuntimeCoordinator(
                Verification,
                Operations,
                Settings,
                LocalState,
                () => true,
                _ => { },
                _ => LocalState.IsPlayable,
                TimeProvider.System,
                Maintenance,
                () => LocalState);
            Coordinator.SnapshotChanged += (_, _) => PublishedSnapshots++;
            Coordinator.RefreshAuthenticationAvailability();
        }

        internal string Root { get; }
        internal LauncherSettings Settings { get; }
        internal LauncherOperationCoordinator Operations { get; }
        internal ActivityVerificationService Verification { get; }
        internal ActivityMaintenanceService Maintenance { get; }
        internal GameRuntimeCoordinator Coordinator { get; }
        internal GameClientLocalState LocalState { get; set; }
        internal int PublishedSnapshots { get; private set; }

        internal static async Task<GameContractHarness> CreateAsync(GameContractOperation operation)
        {
            GameContractHarness harness = new(operation != GameContractOperation.Install);
            if (operation == GameContractOperation.Update)
            {
                harness.Verification.Handler = (_, _, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(new GameClientVerificationResult(
                        GameVerificationOutcome.UpdateAvailable,
                        GameAction.Update,
                        GameUpdateKnowledge.Known,
                        "remote-v2",
                        1));
                };
                Equal(GameVerificationStartStatus.Started,
                    harness.Coordinator.TryStartVerification(),
                    "La préparation Update doit démarrer.");
                await harness.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            return harness;
        }

        internal void ConfigureSuccess(GameContractOperation operation)
        {
            if (operation == GameContractOperation.Verify)
            {
                Verification.Handler = (_, _, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(new GameClientVerificationResult(
                        GameVerificationOutcome.UpToDate,
                        GameAction.Play,
                        GameUpdateKnowledge.Known,
                        "remote-v2",
                        0));
                };
                return;
            }

            Maintenance.Handler = (_, lease, _) =>
            {
                LocalState = new GameClientLocalState(
                    Root, "frFR", true, "remote-v2", GameUpdateKnowledge.Known);
                return Task.FromResult(SuccessResult(lease));
            };
        }

        internal void ConfigureFailure(GameContractOperation operation)
        {
            if (operation == GameContractOperation.Verify)
            {
                Verification.Handler = (_, _, _) =>
                    Task.FromException<GameClientVerificationResult>(
                        new HttpRequestException("activity verify unavailable"));
                return;
            }

            Maintenance.Handler = (_, _, _) =>
                Task.FromException<GameClientMaintenanceResult>(
                    new IOException("activity maintenance failed"));
        }

        internal TaskCompletionSource ConfigureBlocking(GameContractOperation operation)
        {
            TaskCompletionSource started = NewSignal();
            if (operation == GameContractOperation.Verify)
            {
                Verification.Handler = async (_, _, token) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("unreachable");
                };
            }
            else
            {
                Maintenance.Handler = async (_, lease, _) =>
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, lease.CancellationToken);
                    throw new InvalidOperationException("unreachable");
                };
            }
            return started;
        }

        internal void Start(GameContractOperation operation)
        {
            switch (operation)
            {
                case GameContractOperation.Verify:
                    Equal(GameVerificationStartStatus.Started,
                        Coordinator.TryStartVerification(), "Verify doit démarrer.");
                    break;
                case GameContractOperation.Repair:
                    Equal(GameVerificationStartStatus.Started,
                        Coordinator.TryStartFullRepair(), "Repair doit démarrer.");
                    break;
                default:
                    Equal(GamePrimaryActionStatus.Started,
                        Coordinator.TryExecutePrimaryAction(), $"{operation} doit démarrer.");
                    break;
            }
        }

        internal async Task StartAndWaitAsync(GameContractOperation operation)
        {
            Start(operation);
            await Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }

        public async ValueTask DisposeAsync()
        {
            Coordinator.BeginShutdown();
            await Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Coordinator.Dispose();
            Operations.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static GameClientMaintenanceResult SuccessResult(LauncherOperationLease lease) =>
            new(
                lease.OperationId,
                GameClientMaintenanceOutcome.Downloaded,
                "remote-v2",
                1,
                0,
                null,
                null);
    }

    private sealed class ActivityVerificationService : IGameClientVerificationService
    {
        internal Func<LauncherSettings, Action<GameVerificationProgress>?, CancellationToken,
            Task<GameClientVerificationResult>> Handler { get; set; } = (_, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new GameClientVerificationResult(
                GameVerificationOutcome.UpToDate,
                GameAction.Play,
                GameUpdateKnowledge.Known,
                "remote-v1",
                0));
        };

        public Task<GameClientVerificationResult> VerifyAsync(
            LauncherSettings settings,
            bool reportFileProgress,
            Action<GameVerificationProgress>? reportProgress,
            CancellationToken cancellationToken) =>
            Handler(settings, reportProgress, cancellationToken);
    }

    private sealed class ActivityMaintenanceService : IGameClientMaintenanceService
    {
        internal Func<GameClientMaintenanceRequest, LauncherOperationLease,
            Action<GameClientMaintenanceProgress>?, Task<GameClientMaintenanceResult>> Handler { get; set; } =
            (_, lease, _) => Task.FromResult(new GameClientMaintenanceResult(
                lease.OperationId,
                GameClientMaintenanceOutcome.AlreadyCurrent,
                "remote-v1",
                0,
                0,
                null,
                null));

        public Task<GameClientMaintenanceResult> InstallOrUpdateAsync(
            GameClientMaintenanceRequest request,
            LauncherOperationLease operation,
            Action<GameClientMaintenanceProgress>? reportProgress) =>
            Handler(request, operation, reportProgress);

        public Task<GameClientMaintenanceResult> VerifyAndRepairAsync(
            GameClientMaintenanceRequest request,
            LauncherOperationLease operation,
            Action<GameClientMaintenanceProgress>? reportProgress) =>
            Handler(request, operation, reportProgress);
    }

    private sealed class AddonContractHarness : IAsyncDisposable
    {
        private AddonContractHarness(AddonCatalog catalog, ActivityAddonService service)
        {
            Root = Path.Combine(Path.GetTempPath(), "AtlasActivityAddons", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Service = service;
            Operations = new LauncherOperationCoordinator();
            Coordinator = new LauncherAddonsCoordinator(
                Service,
                new ActivityAddonsSession(),
                Operations,
                new LauncherSettings { InstallPath = Root },
                _ => true,
                _ => false,
                _ => { });
            Coordinator.SnapshotChanged += (_, _) => PublishedSnapshots++;
        }

        internal string Root { get; }
        internal ActivityAddonService Service { get; }
        internal LauncherOperationCoordinator Operations { get; }
        internal LauncherAddonsCoordinator Coordinator { get; }
        internal int PublishedSnapshots { get; private set; }

        internal static Task<AddonContractHarness> CreateAsync(AddonContractOperation operation)
        {
            AddonPackage package = Package("test-addon", "Test Addon");
            AddonLocalStatus status = operation switch
            {
                AddonContractOperation.Install => AddonLocalStatus.NotInstalled,
                AddonContractOperation.Update => AddonLocalStatus.UpdateAvailable,
                AddonContractOperation.Repair => AddonLocalStatus.MissingFiles,
                AddonContractOperation.Remove => AddonLocalStatus.Installed,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            return CreateLoadedAsync(
                Catalog(package),
                new Dictionary<string, AddonInspection>(StringComparer.OrdinalIgnoreCase)
                {
                    [package.Id] = Inspection(status)
                });
        }

        internal static Task<AddonContractHarness> CreateBatchAsync()
        {
            AddonPackage first = Package("addon-a", "Addon A");
            AddonPackage second = Package("addon-b", "Addon B");
            return CreateLoadedAsync(
                Catalog(first, second),
                new Dictionary<string, AddonInspection>(StringComparer.OrdinalIgnoreCase)
                {
                    [first.Id] = Inspection(AddonLocalStatus.UpdateAvailable),
                    [second.Id] = Inspection(AddonLocalStatus.UpdateAvailable)
                });
        }

        internal static Task<AddonContractHarness> CreateStaleAsync()
        {
            AddonPackage first = Package("addon-a", "Addon A");
            AddonPackage second = Package("addon-b", "Addon B");
            return CreateLoadedAsync(
                Catalog(first, second),
                new Dictionary<string, AddonInspection>(StringComparer.OrdinalIgnoreCase)
                {
                    [first.Id] = Inspection(AddonLocalStatus.NotInstalled),
                    [second.Id] = Inspection(AddonLocalStatus.NotInstalled)
                });
        }

        internal AddonsActionStartResult Start(AddonContractOperation operation) =>
            operation == AddonContractOperation.Remove
                ? Coordinator.TryRemove("test-addon")
                : Coordinator.TryInvokePrimary("test-addon");

        public async ValueTask DisposeAsync()
        {
            Coordinator.BeginShutdown();
            await Coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
            Coordinator.Dispose();
            Operations.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static async Task<AddonContractHarness> CreateLoadedAsync(
            AddonCatalog catalog,
            Dictionary<string, AddonInspection> inspections)
        {
            ActivityAddonService service = new(catalog, inspections);
            AddonContractHarness harness = new(catalog, service);
            AddonsCatalogStartResult load = harness.Coordinator.TryLoadCatalog();
            True(load.IsStarted, "Le catalogue du contrat addon doit démarrer.");
            await load.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
            return harness;
        }

        private static AddonCatalog Catalog(params AddonPackage[] packages) => new()
        {
            SchemaVersion = 1,
            ClientInterface = AddonInstallServices.SupportedInterface,
            Addons = [.. packages]
        };

        private static AddonPackage Package(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Description = "Activity contract",
            Category = "Test",
            Version = "2.0.0",
            Interface = AddonInstallServices.SupportedInterface,
            Url = $"https://atlas.test/{id}.zip",
            Size = 100,
            Sha256 = new string('a', 64),
            InstallHash = new string('b', 64),
            Folders = [$"Atlas_{id}"]
        };

        private static AddonInspection Inspection(AddonLocalStatus status) => new(
            status,
            IsManaged: status is AddonLocalStatus.Installed
                or AddonLocalStatus.UpdateAvailable
                or AddonLocalStatus.MissingFiles,
            InstalledVersion: status == AddonLocalStatus.NotInstalled ? null : "1.0.0");
    }

    private sealed record ActivityAddonApplyCall(
        AddonPackage Package,
        bool Selected,
        IProgress<AddonTransferProgress>? Progress);

    private sealed class ActivityAddonService(
        AddonCatalog catalog,
        Dictionary<string, AddonInspection> inspections) : IAddonManagementService
    {
        private readonly object _sync = new();
        private TaskCompletionSource? _blockedGate;
        private bool _ignoreBlockedCancellation;

        internal Exception? NextFailure { get; set; }
        internal Func<ActivityAddonApplyCall, CancellationToken, Task>? ApplyBehavior { get; set; }
        internal int ApplyCalls { get; private set; }
        internal TaskCompletionSource ManualGateStarted { get; } = NewSignal();
        private TaskCompletionSource ManualGate { get; } = NewSignal();

        public Task<AddonCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(catalog);
        }

        public IReadOnlyDictionary<string, AddonInspection> Inspect(
            AddonCatalog requestedCatalog,
            string installRoot)
        {
            lock (_sync)
            {
                return requestedCatalog.Addons.ToDictionary(
                    package => package.Id,
                    package => inspections[package.Id],
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task ApplySelectionAsync(
            AddonCatalog requestedCatalog,
            string installRoot,
            IReadOnlyDictionary<string, bool> selection,
            IProgress<AddonTransferProgress>? progress,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            AddonPackage package = requestedCatalog.Addons.Single();
            bool selected = selection.TryGetValue(package.Id, out bool value) && value;
            ApplyCalls++;
            TaskCompletionSource? blockedGate;
            bool ignoreCancellation;
            lock (_sync)
            {
                blockedGate = _blockedGate;
                ignoreCancellation = _ignoreBlockedCancellation;
                _blockedGate = null;
                _ignoreBlockedCancellation = false;
            }
            if (blockedGate is not null)
            {
                blockedGate.TrySetResult();
                if (ignoreCancellation)
                {
                    await ManualGate.Task;
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }

            Exception? failure = NextFailure;
            NextFailure = null;
            if (failure is not null)
            {
                throw failure;
            }

            ActivityAddonApplyCall call = new(package, selected, progress);
            if (ApplyBehavior is not null)
            {
                await ApplyBehavior(call, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                inspections[package.Id] = selected
                    ? new AddonInspection(
                        AddonLocalStatus.Installed,
                        true,
                        package.Version,
                        package.EffectiveInstallHash,
                        package.Folders.ToImmutableArray(),
                        DateTimeOffset.UtcNow)
                    : new AddonInspection(AddonLocalStatus.NotInstalled, false);
            }
        }

        internal TaskCompletionSource BlockNextApply(bool ignoreCancellation = false)
        {
            TaskCompletionSource started = NewSignal();
            lock (_sync)
            {
                _blockedGate = started;
                _ignoreBlockedCancellation = ignoreCancellation;
            }
            return started;
        }

        internal void ReleaseBlockedApply() => ManualGate.TrySetResult();

        internal async Task WaitOnManualGateAsync(CancellationToken cancellationToken)
        {
            ManualGateStarted.TrySetResult();
            await ManualGate.Task.WaitAsync(cancellationToken);
        }

        internal void ReleaseManualGate() => ManualGate.TrySetResult();
    }

    private sealed class ActivityAddonsSession : IAddonsSessionContext
    {
        public event EventHandler<AuthSessionSnapshotEventArgs>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public AuthSessionSnapshot CurrentSnapshot { get; } = new(
            1,
            null,
            LauncherSessionState.Authenticated,
            null,
            "activity-user",
            true,
            LauncherSessionFailureCategory.None);

        public Task<AtlasRequestPreparationStatus> PrepareAuthenticatedRequestAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AtlasRequestPreparationStatus.Ready);
        }

        public void NotifyAuthenticatedRequestUnauthorized()
        {
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
