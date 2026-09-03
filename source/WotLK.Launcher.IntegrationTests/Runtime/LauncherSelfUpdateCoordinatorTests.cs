using System.Collections.Immutable;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.Updater;

internal static class LauncherSelfUpdateCoordinatorTests
{
    internal static async Task<int> RunAsync()
    {
        await CharacterizeTimerCoalescingAndRecoveryAsync();
        await CharacterizeChecksAndErrorsAsync();
        await ExcludeVersionChecksFromActivityAsync();
        await EnforceGlobalLeaseConflictsAsync();
        await ProjectRealDownloadIntoActivityAsync();
        await CancelAndRejectStaleProgressAsync();
        await ValidateCandidateAndAtomicHandoffAsync();
        await StopCleanlyDuringLateCheckAsync();
        Console.WriteLine("Launcher self-update runtime OK (04B.3b).");
        return 0;
    }

    private static async Task CharacterizeTimerCoalescingAndRecoveryAsync()
    {
        SelfUpdateHarness harness = new(automaticChecksEnabled: true);
        TaskCompletionSource<LauncherUpdateManifest> initialGate = NewCompletion<LauncherUpdateManifest>();
        harness.Client.LoadManifestHandler = _ => initialGate.Task;

        harness.Coordinator.ScheduleInitialCheck();
        harness.Coordinator.ScheduleInitialCheck();
        await WaitUntilAsync(() => harness.Client.ManifestCalls == 1);
        harness.Coordinator.StartPeriodicChecks();
        harness.Coordinator.StartPeriodicChecks();

        Equal(1, harness.Timer.StartCalls, "Le timer 30 secondes doit être créé et démarré une seule fois.");
        Equal(LauncherSelfUpdateCoordinator.CheckInterval, harness.Timer.Interval,
            "La cadence legacy doit rester exactement de 30 secondes.");

        Task<LauncherSelfUpdateCheckResult> manual = harness.Coordinator.CheckAsync();
        True(ReferenceEquals(manual, harness.Coordinator.CheckAsync()),
            "Deux demandes pendant un check doivent partager la même tâche, sans file d'attente.");
        initialGate.SetResult(harness.ManifestFor(harness.CurrentBytes, "1.1.0"));
        await manual.WaitAsync(TimeSpan.FromSeconds(5));

        TaskCompletionSource<LauncherUpdateManifest> periodicGate = NewCompletion<LauncherUpdateManifest>();
        harness.Client.LoadManifestHandler = _ => periodicGate.Task;
        harness.Timer.Fire();
        harness.Timer.Fire();
        await WaitUntilAsync(() => harness.Client.ManifestCalls == 2);
        Equal(2, harness.Client.ManifestCalls,
            "Deux ticks rapprochés doivent être coalescés en une seule requête.");
        periodicGate.SetResult(harness.ManifestFor(harness.CurrentBytes, "1.1.0"));
        await harness.Coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(5));

        harness.Coordinator.Dispose();
        harness.Coordinator.Dispose();
        Equal(1, harness.Timer.StopCalls, "La fermeture doit arrêter le timer une seule fois.");
        harness.Dispose();

        using SelfUpdateHarness recovery = new(
            automaticChecksEnabled: true,
            selfUpdateRecoveryOccurred: true);
        recovery.Coordinator.ScheduleInitialCheck();
        recovery.Coordinator.StartPeriodicChecks();
        recovery.Timer.Fire();
        await Task.Delay(25);
        Equal(0, recovery.Client.ManifestCalls,
            "Une récupération 04B.3a doit inhiber les retries automatiques du lancement courant.");
        Equal(0, recovery.Timer.StartCalls,
            "Le timer ne doit pas être armé pendant le lancement ayant récupéré un rollback.");
        True(recovery.Coordinator.IsAutomaticRetrySuppressed,
            "La protection anti-boucle doit rester explicite et éphémère.");

        LauncherSelfUpdateCheckResult explicitCheck = await recovery.Coordinator.CheckAsync();
        True(explicitCheck.Outcome is LauncherSelfUpdateCheckOutcome.Completed
                or LauncherSelfUpdateCheckOutcome.NoUpdate,
            "Une recherche explicitement demandée doit rester possible après la récupération.");
        Equal(1, recovery.Client.ManifestCalls,
            "La récupération ne doit pas blacklister définitivement la version distante.");
    }

    private static async Task CharacterizeChecksAndErrorsAsync()
    {
        using SelfUpdateHarness harness = new();
        harness.Client.Manifest = harness.ManifestFor(harness.CurrentBytes, "1.1.0");
        LauncherSelfUpdateCheckResult noUpdate = await harness.Coordinator.CheckAsync();
        Equal(LauncherSelfUpdateCheckOutcome.NoUpdate, noUpdate.Outcome,
            "Un hash identique doit conserver le comportement sans mise à jour.");
        True(!harness.Coordinator.CurrentSnapshot.IsUpdateAvailable,
            "NoUpdate ne doit pas être présenté comme une erreur ou une disponibilité.");
        True(harness.Coordinator.CurrentSnapshot.ErrorCategory is null,
            "NoUpdate ne doit pas alimenter l'erreur UI.");
        DateTimeOffset firstValidCheck = Required(harness.Coordinator.CurrentSnapshot.LastCheckedAt,
            "Une réponse exploitable doit mettre à jour LastCheckedAt.");

        harness.Advance(TimeSpan.FromMinutes(1));
        harness.Client.Manifest = harness.ManifestFor(harness.CandidateBytes, "1.1.0");
        LauncherSelfUpdateCheckResult equalVersion = await harness.Coordinator.CheckAsync();
        Equal(LauncherSelfUpdateCheckOutcome.NoUpdate, equalVersion.Outcome,
            "Une version égale ne doit pas provoquer de remplacement involontaire.");
        True(!harness.Coordinator.CurrentSnapshot.IsUpdateAvailable,
            "Une version égale doit rester NoUpdate même si son hash diffère.");

        harness.Advance(TimeSpan.FromMinutes(1));
        harness.Client.Manifest = harness.ManifestFor(harness.CandidateBytes, "1.0.9");
        LauncherSelfUpdateCheckResult older = await harness.Coordinator.CheckAsync();
        Equal(LauncherSelfUpdateCheckOutcome.NoUpdate, older.Outcome,
            "Une version distante plus ancienne doit être ignorée.");
        True(!harness.Coordinator.CurrentSnapshot.IsUpdateAvailable,
            "Une version plus ancienne doit retirer la disponibilité précédente.");
        DateTimeOffset lastValidCheck = Required(harness.Coordinator.CurrentSnapshot.LastCheckedAt,
            "Le résultat valide plus ancien doit compter comme vérification exploitable.");
        True(lastValidCheck > firstValidCheck, "LastCheckedAt doit avancer après un résultat valide.");

        LauncherUpdateManifest invalidManifest = harness.ManifestFor(
            harness.CandidateBytes,
            "2.0.0");
        invalidManifest.Sha256 = "invalid";
        harness.Client.Manifest = invalidManifest;
        LauncherSelfUpdateCheckResult invalid = await harness.Coordinator.CheckAsync();
        Equal(LauncherSelfUpdateErrorCategory.ManifestInvalid, invalid.ErrorCategory,
            "Un manifeste invalide doit recevoir une catégorie contrôlée.");
        Equal(lastValidCheck, harness.Coordinator.CurrentSnapshot.LastCheckedAt,
            "Une réponse inexploitable ne doit pas modifier LastCheckedAt.");

        harness.Client.LoadManifestHandler = _ => throw new HttpRequestException("secret-network-detail");
        LauncherSelfUpdateCheckResult unavailable = await harness.Coordinator.CheckAsync();
        Equal(LauncherSelfUpdateErrorCategory.ManifestUnavailable, unavailable.ErrorCategory,
            "Une panne réseau doit être distinguée d'un manifeste invalide.");
        Equal(lastValidCheck, harness.Coordinator.CurrentSnapshot.LastCheckedAt,
            "Une tentative réseau échouée ne doit pas devenir une dernière vérification réussie.");
        True(harness.Logs.All(line => !line.Contains("secret-network-detail", StringComparison.Ordinal)),
            "Le journal ne doit pas exposer le message brut de l'exception.");
    }

    private static async Task ExcludeVersionChecksFromActivityAsync()
    {
        using SelfUpdateHarness harness = new();
        harness.Client.Manifest = harness.ManifestFor(harness.CurrentBytes, "1.1.0");
        for (int index = 0; index < 100; index++)
        {
            await harness.Coordinator.CheckAsync();
        }

        True(!harness.Activity.CurrentSnapshot.HasActiveOperation,
            "Cent checks simples ne doivent jamais apparaître comme opération active.");
        True(harness.Activity.CurrentSnapshot.RecentItems.IsEmpty,
            "Cent checks sans update ne doivent produire aucun historique Activity.");

        TaskCompletionSource<LauncherUpdateManifest> gate = NewCompletion<LauncherUpdateManifest>();
        harness.Client.LoadManifestHandler = _ => gate.Task;
        Task<LauncherSelfUpdateCheckResult> check = harness.Coordinator.CheckAsync();
        await WaitUntilAsync(() => harness.Coordinator.CurrentSnapshot.IsChecking);
        True(!harness.Activity.CurrentSnapshot.HasActiveOperation,
            "Checking doit rester absent du Centre d'activité.");
        gate.SetResult(harness.ManifestFor(harness.CandidateBytes, "2.0.0"));
        await check;
        True(harness.Activity.CurrentSnapshot.RecentItems.IsEmpty,
            "La découverte d'une version ne doit pas créer d'historique avant téléchargement.");
    }

    private static async Task EnforceGlobalLeaseConflictsAsync()
    {
        using SelfUpdateHarness harness = new();
        await harness.DiscoverUpdateAsync();

        LauncherOperationLease gameLease = harness.Operations.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true,
            clientIsPlayable: true,
            operationType: LauncherOperationType.GameUpdate).Lease
            ?? throw new InvalidOperationException("Le bail GameUpdate témoin est requis.");
        LauncherSelfUpdateStartResult gameConflict = harness.Coordinator.TryStartUpdate();
        Equal(LauncherSelfUpdateStartStatus.Busy, gameConflict.Status,
            "Une mise à jour Jeu doit refuser immédiatement le self-update.");
        Equal(0, harness.Client.DownloadCalls,
            "Un refus global ne doit ni télécharger ni mettre le clic en attente.");
        True(harness.Coordinator.CurrentSnapshot.IsUpdateAvailable,
            "La disponibilité doit survivre au refus Jeu.");
        gameLease.Complete();

        LauncherOperationLease addonLease = harness.Operations.TryBegin(
            LauncherOperationKind.Addons,
            canUserCancel: true,
            clientIsPlayable: true,
            operationType: LauncherOperationType.AddonUpdate).Lease
            ?? throw new InvalidOperationException("Le bail AddonUpdate témoin est requis.");
        LauncherSelfUpdateStartResult addonConflict = harness.Coordinator.TryStartUpdate();
        Equal(LauncherSelfUpdateStartStatus.Busy, addonConflict.Status,
            "Une opération Addons doit refuser immédiatement le self-update.");
        Equal(0, harness.Client.DownloadCalls,
            "Le conflit Addons ne doit pas créer de scheduler caché.");
        addonLease.Complete();

        LauncherSelfUpdateStartResult retry = harness.Coordinator.TryStartUpdate();
        True(retry.IsStarted, "Une tentative explicite ultérieure doit fonctionner après libération.");
        LauncherSelfUpdateCompletion completion = await retry.Completion!;
        Equal(LauncherOperationOutcome.Succeeded, completion.Outcome,
            "La tentative normale doit réutiliser la disponibilité conservée.");
        Equal(1, harness.Client.DownloadCalls,
            "Aucun téléchargement différé ne doit avoir précédé le retry explicite.");
    }

    private static async Task ProjectRealDownloadIntoActivityAsync()
    {
        using SelfUpdateHarness harness = new();
        await harness.DiscoverUpdateAsync();
        TaskCompletionSource progressReached = NewCompletion();
        TaskCompletionSource releaseDownload = NewCompletion();
        harness.Client.DownloadHandler = async (_, path, _, reportProgress, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(path, harness.CandidateBytes, cancellationToken);
            harness.Advance(TimeSpan.FromMilliseconds(100));
            reportProgress(new LauncherSelfUpdateTransferProgress(
                BytesProcessed: 2048,
                BytesTotal: 4096,
                Percent: 50,
                BytesPerSecond: 1024,
                Eta: TimeSpan.FromSeconds(2)));
            progressReached.TrySetResult();
            await releaseDownload.Task.WaitAsync(cancellationToken);
        };

        LauncherSelfUpdateStartResult start = harness.Coordinator.TryStartUpdate();
        True(start.IsStarted, "Le téléchargement réel doit prendre le bail LauncherAutoUpdate.");
        await progressReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        LauncherSelfUpdateSnapshot update = harness.Coordinator.CurrentSnapshot;
        Equal(LauncherSelfUpdatePhase.Downloading, update.Phase,
            "La phase runtime doit provenir du downloader legacy extrait.");
        Equal(50d, update.Percent, "Le coordinateur doit conserver le vrai pourcentage brut.");
        Equal(2048L, update.BytesProcessed, "Les octets traités ne doivent pas être recalculés.");
        Equal(4096L, update.BytesTotal, "Le total ne doit pas être recalculé par Activity.");
        Equal(1024d, update.Speed, "Le débit doit rester celui du downloader.");
        Equal(TimeSpan.FromSeconds(2), update.Eta, "L'ETA doit rester celle du downloader.");

        LauncherActivityOperationSnapshot activity = Required(
            harness.Activity.CurrentSnapshot.ActiveOperation,
            "Le téléchargement doit créer une Activity active.");
        Equal("Atlas Launcher", activity.DisplayName,
            "L'opération doit utiliser l'identité produit Atlas Launcher.");
        Equal(LauncherOperationType.LauncherAutoUpdate, activity.OperationType,
            "Le bail doit être typé LauncherAutoUpdate.");
        Equal(50d, activity.Percent, "Activity doit projeter le pourcentage sans le recalculer.");
        Equal(1024d, activity.BytesPerSecond, "Activity doit projeter le débit brut.");
        True(activity.CanUserCancel, "Le téléchargement doit rester annulable.");
        True(harness.Activity.CurrentSnapshot.TopBarProgressKnown,
            "La top bar doit exposer la progression déterminée.");

        ActivityViewState ui = ActivityStateAdapter.Project(harness.Activity.CurrentSnapshot);
        Equal("Mise à jour", Required(ui.ActiveOperation, "L'Activity UI est requise.").ActionName,
            "Le scénario visuel validé doit être réutilisé.");
        releaseDownload.TrySetResult();
        LauncherSelfUpdateCompletion completed = await start.Completion!;
        Equal(LauncherOperationOutcome.Succeeded, completed.Outcome,
            "Le handoff factice accepté doit terminer le chemin runtime.");
        True(!harness.Activity.CurrentSnapshot.HasActiveOperation,
            "La top bar doit revenir au repos après terminaison.");
    }

    private static async Task CancelAndRejectStaleProgressAsync()
    {
        using SelfUpdateHarness harness = new();
        await harness.DiscoverUpdateAsync();
        TaskCompletionSource downloadStarted = NewCompletion();
        Action<LauncherSelfUpdateTransferProgress>? lateProgress = null;
        harness.Client.DownloadHandler = async (_, path, _, reportProgress, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(path, harness.CandidateBytes, cancellationToken);
            lateProgress = reportProgress;
            reportProgress(new LauncherSelfUpdateTransferProgress(512, 4096, 12.5, 256, TimeSpan.FromSeconds(14)));
            downloadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        LauncherSelfUpdateStartResult start = harness.Coordinator.TryStartUpdate();
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(harness.Operations.CancelFromUser(),
            "Le bouton Activity doit déléguer la première annulation au bail global.");
        True(!harness.Coordinator.CancelFromUser(),
            "Une seconde annulation doit être refusée immédiatement.");
        True(!harness.Coordinator.CurrentSnapshot.CanUserCancel,
            "Le bouton Annuler doit être désactivé dès la première demande.");

        LauncherSelfUpdateCompletion completion = await start.Completion!;
        Equal(LauncherOperationOutcome.Cancelled, completion.Outcome,
            "L'annulation doit produire un terminal Cancelled.");
        Equal(LauncherOperationOutcome.Cancelled,
            harness.Activity.CurrentSnapshot.RecentItems.Single().Outcome,
            "Activity doit conserver l'annulation en mémoire.");
        long sequence = harness.Coordinator.CurrentSnapshot.Sequence;
        lateProgress?.Invoke(new LauncherSelfUpdateTransferProgress(4096, 4096, 100, 9999, TimeSpan.Zero));
        Equal(sequence, harness.Coordinator.CurrentSnapshot.Sequence,
            "Un callback de progression obsolète ne doit plus publier après terminaison.");
        True(harness.Coordinator.CurrentSnapshot.IsUpdateAvailable,
            "Une annulation avant application doit conserver la disponibilité.");

        LauncherOperationLease nextOperation = harness.Operations.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true,
            clientIsPlayable: true,
            operationType: LauncherOperationType.GameUpdate).Lease
            ?? throw new InvalidOperationException("L'opération suivante est requise.");
        True(!harness.Coordinator.CancelFromUser(),
            "Une ancienne commande self-update ne doit pas annuler l'opération suivante.");
        True(!nextOperation.CancellationToken.IsCancellationRequested,
            "Le bail suivant doit rester intact après une annulation self-update tardive.");
        nextOperation.Complete();
    }

    private static async Task ValidateCandidateAndAtomicHandoffAsync()
    {
        using (SelfUpdateHarness invalidCandidate = new())
        {
            await invalidCandidate.DiscoverUpdateAsync();
            invalidCandidate.Client.DownloadHandler = async (_, path, _, reportProgress, cancellationToken) =>
            {
                await File.WriteAllBytesAsync(path, [1, 2, 3], cancellationToken);
                reportProgress(new LauncherSelfUpdateTransferProgress(3, 3, 100, null, null));
            };
            LauncherSelfUpdateStartResult start = invalidCandidate.Coordinator.TryStartUpdate();
            LauncherSelfUpdateCompletion result = await start.Completion!;
            Equal(LauncherSelfUpdateErrorCategory.PackageIntegrityFailed, result.ErrorCategory,
                "Le candidat doit être validé avant tout appel du mécanisme 04B.3a.");
            Equal(0, invalidCandidate.Finalizer.Calls,
                "Un candidat invalide ne doit jamais atteindre le finalizer atomique.");
            Equal(0, invalidCandidate.ShutdownCalls,
                "Le launcher doit rester ouvert après validation échouée.");
            Equal(LauncherOperationOutcome.Failed,
                invalidCandidate.Activity.CurrentSnapshot.RecentItems.Single().Outcome,
                "L'échec avant fermeture doit rejoindre Récent.");
        }

        using (SelfUpdateHarness refused = new())
        {
            await refused.DiscoverUpdateAsync();
            refused.Finalizer.Handler = (_, _, _, _, _, _, _) =>
                throw new IOException("helper-secret-refusal");
            LauncherSelfUpdateStartResult start = refused.Coordinator.TryStartUpdate();
            LauncherSelfUpdateCompletion result = await start.Completion!;
            Equal(LauncherSelfUpdateErrorCategory.ReplacementFailed, result.ErrorCategory,
                "Le refus du helper doit être une erreur contrôlée de remplacement.");
            Equal(1, refused.Finalizer.Calls,
                "La passation 04B.3a ne doit être tentée qu'une fois.");
            Equal(0, refused.ShutdownCalls,
                "Le launcher ne doit pas fermer si le helper refuse la transaction.");
            True(refused.Coordinator.CurrentSnapshot.IsUpdateAvailable,
                "L'update doit rester disponible après refus du helper.");
            True(refused.Logs.All(line => !line.Contains("helper-secret-refusal", StringComparison.Ordinal)),
                "Le refus brut du helper ne doit pas fuiter dans les logs runtime.");
        }

        using (SelfUpdateHarness accepted = new())
        {
            await accepted.DiscoverUpdateAsync();
            TaskCompletionSource finalizerEntered = NewCompletion();
            TaskCompletionSource acceptHandoff = NewCompletion();
            accepted.Finalizer.Handler = async (
                target,
                candidate,
                size,
                hash,
                version,
                parent,
                token) =>
            {
                finalizerEntered.TrySetResult();
                await acceptHandoff.Task.WaitAsync(token);
                return accepted.Finalizer.CreateTransaction(
                    target,
                    candidate,
                    size,
                    hash,
                    version,
                    parent);
            };

            LauncherSelfUpdateStartResult start = accepted.Coordinator.TryStartUpdate();
            await finalizerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            LauncherSelfUpdateSnapshot critical = accepted.Coordinator.CurrentSnapshot;
            Equal(LauncherSelfUpdatePhase.WaitingForApply, critical.Phase,
                "La phase critique doit être publiée avant le handoff.");
            True(!critical.CanUserCancel,
                "CanUserCancel doit passer de true à false avant la transaction atomique.");
            LauncherActivityOperationSnapshot activity = Required(
                accepted.Activity.CurrentSnapshot.ActiveOperation,
                "L'application doit rester visible pendant le handoff.");
            Equal(LauncherActivityProgressMode.Indeterminate, activity.ProgressMode,
                "La validation/application ne doit pas conserver une fausse barre déterminée.");
            True(!activity.CanUserCancel,
                "Le Centre d'activité doit suivre la fermeture de la fenêtre d'annulation.");

            acceptHandoff.TrySetResult();
            LauncherSelfUpdateCompletion result = await start.Completion!;
            Equal(LauncherOperationOutcome.Succeeded, result.Outcome,
                "Un helper ayant accepté la transaction doit autoriser le shutdown.");
            Equal(1, accepted.Finalizer.Calls,
                "Le coordinateur doit déléguer une seule fois à 04B.3a.");
            Equal("2.0.0", accepted.Finalizer.AuthenticatedTargetVersion,
                "La version transmise au helper doit provenir du manifeste signé.");
            Equal(1, accepted.ShutdownCalls,
                "Le shutdown ne doit être demandé qu'après acceptation du helper.");
        }

        using (SelfUpdateHarness restartFailure = new(requestShutdown: () =>
               throw new InvalidOperationException("window-secret")))
        {
            await restartFailure.DiscoverUpdateAsync();
            LauncherSelfUpdateStartResult start = restartFailure.Coordinator.TryStartUpdate();
            LauncherSelfUpdateCompletion result = await start.Completion!;
            Equal(LauncherSelfUpdateErrorCategory.RestartFailed, result.ErrorCategory,
                "Un échec de fermeture après handoff doit être catégorisé sans exception brute.");
            Equal(1, restartFailure.Finalizer.Calls,
                "L'échec de restart ne doit pas rejouer le finalizer.");
        }
    }

    private static async Task StopCleanlyDuringLateCheckAsync()
    {
        SelfUpdateHarness harness = new(automaticChecksEnabled: true);
        TaskCompletionSource<LauncherUpdateManifest> gate = NewCompletion<LauncherUpdateManifest>();
        harness.Client.LoadManifestHandler = _ => gate.Task;
        int publications = 0;
        harness.Coordinator.SnapshotChanged += (_, _) => publications++;
        harness.Coordinator.StartPeriodicChecks();
        Task<LauncherSelfUpdateCheckResult> check = harness.Coordinator.CheckAsync();
        await WaitUntilAsync(() => harness.Client.ManifestCalls == 1);
        harness.Coordinator.Dispose();
        int publicationsAtDispose = publications;
        gate.SetResult(harness.ManifestFor(harness.CurrentBytes, "1.1.0"));
        LauncherSelfUpdateCheckResult result = await check.WaitAsync(TimeSpan.FromSeconds(5));

        Equal(LauncherSelfUpdateCheckOutcome.ShuttingDown, result.Outcome,
            "Une implémentation HTTP tardive doit rester observée pendant la fermeture.");
        Equal(publicationsAtDispose, publications,
            "Aucun callback tardif ne doit atteindre WPF après Dispose.");
        Equal(1, harness.Timer.StopCalls,
            "La fermeture doit désarmer l'unique timer même si le client ignore le token.");
        True(await harness.Coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(5)),
            "La tâche tardive doit être entièrement observée.");
        harness.Dispose();

        using SelfUpdateHarness activeUpdate = new();
        await activeUpdate.DiscoverUpdateAsync();
        TaskCompletionSource downloadStarted = NewCompletion();
        OperationTerminalResult? terminal = null;
        activeUpdate.Coordinator.OperationTerminated += (_, eventArgs) =>
            terminal = eventArgs.TerminalResult;
        activeUpdate.Client.DownloadHandler = async (_, path, _, _, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(path, activeUpdate.CandidateBytes, cancellationToken);
            downloadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        LauncherSelfUpdateStartResult start = activeUpdate.Coordinator.TryStartUpdate();
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        activeUpdate.Coordinator.BeginShutdown();
        LauncherSelfUpdateCompletion completion = await start.Completion!.WaitAsync(
            TimeSpan.FromSeconds(5));
        Equal(LauncherOperationOutcome.Cancelled, completion.Outcome,
            "La fermeture doit interrompre même une mise à jour active.");
        Equal(LauncherOperationCancellationReason.Shutdown,
            Required(terminal, "Le terminal de fermeture est requis.").CancellationReason,
            "Le terminal de fermeture doit conserver la raison de cycle de vie.");
        True(activeUpdate.Operations.IsIdle,
            "Le bail global doit être libéré après l'annulation de fermeture.");
    }

    private static T Required<T>(T? value, string message) where T : class =>
        value ?? throw new InvalidOperationException(message);

    private static DateTimeOffset Required(DateTimeOffset? value, string message) =>
        value ?? throw new InvalidOperationException(message);

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition de test self-update non atteinte.");
            }
            await Task.Delay(10);
        }
    }

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
            throw new InvalidOperationException($"{message} Attendu={expected}; obtenu={actual}.");
        }
    }

    private sealed class SelfUpdateHarness : IDisposable
    {
        private int _downloadDirectorySequence;
        private int _disposeState;
        private readonly Action? _requestShutdown;

        internal SelfUpdateHarness(
            bool automaticChecksEnabled = false,
            bool selfUpdateRecoveryOccurred = false,
            Action? requestShutdown = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "AtlasSelfUpdateRuntime", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ExecutablePath = Path.Combine(Root, "AtlasLauncher.exe");
            CurrentBytes = Encoding.UTF8.GetBytes("atlas-launcher-v1-current");
            CandidateBytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
            File.WriteAllBytes(ExecutablePath, CurrentBytes);
            Now = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
            Timer = new FakeSelfUpdateTimer(LauncherSelfUpdateCoordinator.CheckInterval);
            Client = new FakeSelfUpdateClient(CandidateBytes)
            {
                Manifest = ManifestFor(CandidateBytes, "2.0.0")
            };
            Finalizer = new FakeSelfUpdateFinalizer(Root);
            Operations = new LauncherOperationCoordinator();
            _requestShutdown = requestShutdown;
            Coordinator = new LauncherSelfUpdateCoordinator(
                Operations,
                Client,
                Finalizer,
                Timer,
                automaticChecksEnabled,
                "v1.1.0",
                selfUpdateRecoveryOccurred,
                getExecutablePath: () => ExecutablePath,
                createDownloadDirectory: () => Path.Combine(
                    Root,
                    "download-" + Interlocked.Increment(ref _downloadDirectorySequence)),
                getProcessId: () => 4242,
                getNow: () => Now,
                computeSha256: ComputeSha256Async,
                writeLog: Logs.Add,
                requestShutdown: () =>
                {
                    if (_requestShutdown is not null)
                    {
                        _requestShutdown();
                        return;
                    }
                    ShutdownCalls++;
                });
            OperationSource = new OperationActivitySource(Operations);
            SelfUpdateSource = new SelfUpdateActivitySource(Coordinator);
            Activity = new LauncherActivityCoordinator(
                OperationSource,
                new StaticGameActivitySource(),
                new StaticAddonsActivitySource(),
                SelfUpdateSource);
        }

        internal string Root { get; }
        internal string ExecutablePath { get; }
        internal byte[] CurrentBytes { get; }
        internal byte[] CandidateBytes { get; }
        internal DateTimeOffset Now { get; private set; }
        internal FakeSelfUpdateTimer Timer { get; }
        internal FakeSelfUpdateClient Client { get; }
        internal FakeSelfUpdateFinalizer Finalizer { get; }
        internal LauncherOperationCoordinator Operations { get; }
        internal LauncherSelfUpdateCoordinator Coordinator { get; }
        internal OperationActivitySource OperationSource { get; }
        internal SelfUpdateActivitySource SelfUpdateSource { get; }
        internal LauncherActivityCoordinator Activity { get; }
        internal List<string> Logs { get; } = [];
        internal int ShutdownCalls { get; private set; }

        internal void Advance(TimeSpan duration) => Now += duration;

        internal LauncherUpdateManifest ManifestFor(byte[] bytes, string version) => new()
        {
            Version = version,
            Url = $"https://animeclub.fr/wotlk/launcher/releases/{version}/WotLK-Launcher.exe",
            Size = bytes.LongLength,
            Sha256 = Hash(bytes)
        };

        internal async Task DiscoverUpdateAsync()
        {
            Client.LoadManifestHandler = null;
            Client.Manifest = ManifestFor(CandidateBytes, "2.0.0");
            LauncherSelfUpdateCheckResult result = await Coordinator.CheckAsync();
            Equal(LauncherSelfUpdateCheckOutcome.Completed, result.Outcome,
                "Le harnais doit découvrir une mise à jour exploitable.");
            True(Coordinator.CurrentSnapshot.IsUpdateAvailable,
                "Le harnais doit conserver le manifeste disponible.");
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            Activity.Dispose();
            Coordinator.Dispose();
            Operations.Dispose();
            LauncherUpdateTransactionStore.TryDeleteDirectory(Root);
        }

        private static async Task<string> ComputeSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return Hash(bytes);
        }
    }

    private sealed class FakeSelfUpdateTimer(TimeSpan interval) : ILauncherSelfUpdateTimer
    {
        public event EventHandler? Tick;
        public TimeSpan Interval { get; } = interval;
        public bool IsEnabled { get; private set; }
        internal int StartCalls { get; private set; }
        internal int StopCalls { get; private set; }

        public void Start()
        {
            StartCalls++;
            IsEnabled = true;
        }

        public void Stop()
        {
            StopCalls++;
            IsEnabled = false;
        }

        internal void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeSelfUpdateClient(byte[] candidateBytes) : ILauncherSelfUpdateClient
    {
        internal LauncherUpdateManifest Manifest { get; set; } = new();
        internal Func<CancellationToken, Task<LauncherUpdateManifest>>? LoadManifestHandler { get; set; }
        internal Func<Uri, string, long, Action<LauncherSelfUpdateTransferProgress>, CancellationToken, Task>?
            DownloadHandler { get; set; }
        internal int ManifestCalls { get; private set; }
        internal int DownloadCalls { get; private set; }

        public Task<LauncherUpdateManifest> LoadManifestAsync(CancellationToken cancellationToken)
        {
            ManifestCalls++;
            return LoadManifestHandler?.Invoke(cancellationToken) ?? Task.FromResult(Manifest);
        }

        public async Task DownloadAsync(
            Uri uri,
            string targetPath,
            long expectedSize,
            Action<LauncherSelfUpdateTransferProgress> reportProgress,
            CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (DownloadHandler is not null)
            {
                await DownloadHandler(uri, targetPath, expectedSize, reportProgress, cancellationToken);
                return;
            }

            await Task.Yield();
            await File.WriteAllBytesAsync(targetPath, candidateBytes, cancellationToken);
            reportProgress(new LauncherSelfUpdateTransferProgress(
                candidateBytes.LongLength,
                expectedSize,
                expectedSize > 0 ? 100 : null,
                candidateBytes.LongLength,
                TimeSpan.Zero));
        }
    }

    private sealed class FakeSelfUpdateFinalizer(string root) : ILauncherSelfUpdateFinalizer
    {
        internal Func<string, string, long, string, string, int, CancellationToken, Task<LauncherUpdateTransaction>>?
            Handler { get; set; }
        internal int Calls { get; private set; }
        internal string? AuthenticatedTargetVersion { get; private set; }

        public Task<LauncherUpdateTransaction> PrepareAndLaunchAsync(
            string targetPath,
            string downloadedCandidatePath,
            long expectedSize,
            string expectedSha256,
            string authenticatedTargetVersion,
            int parentProcessId,
            CancellationToken cancellationToken)
        {
            Calls++;
            AuthenticatedTargetVersion = authenticatedTargetVersion;
            return Handler?.Invoke(
                    targetPath,
                    downloadedCandidatePath,
                    expectedSize,
                    expectedSha256,
                    authenticatedTargetVersion,
                    parentProcessId,
                    cancellationToken)
                ?? Task.FromResult(CreateTransaction(
                    targetPath,
                    downloadedCandidatePath,
                    expectedSize,
                    expectedSha256,
                    authenticatedTargetVersion,
                    parentProcessId));
        }

        internal LauncherUpdateTransaction CreateTransaction(
            string targetPath,
            string candidatePath,
            long size,
            string hash,
            string authenticatedTargetVersion,
            int parentProcessId)
        {
            Guid id = Guid.NewGuid();
            string workspace = Path.Combine(root, "transaction-" + id.ToString("N"));
            return new LauncherUpdateTransaction(
                LauncherUpdateTransaction.CurrentSchemaVersion,
                id,
                parentProcessId,
                targetPath,
                workspace,
                candidatePath,
                Path.Combine(workspace, "updater.exe"),
                targetPath + ".new",
                targetPath + ".backup",
                Path.Combine(workspace, "transaction.json"),
                Path.Combine(workspace, "helper-accepted.json"),
                Path.Combine(workspace, "started.json"),
                Path.Combine(workspace, "ready.json"),
                size,
                Hash(File.ReadAllBytes(targetPath)),
                hash,
                LauncherUpdateTransactionPhase.Prepared,
                DateTimeOffset.UtcNow,
                AuthenticatedTargetVersion: authenticatedTargetVersion);
        }
    }

    private sealed class OperationActivitySource(LauncherOperationCoordinator source)
        : ILauncherOperationActivitySource
    {
        public event EventHandler<LauncherOperationActivitySnapshotEventArgs>? SnapshotChanged
        {
            add => source.ActivityChanged += value;
            remove => source.ActivityChanged -= value;
        }

        public LauncherOperationActivitySnapshot CurrentSnapshot => source.CurrentActivitySnapshot;
    }

    private sealed class SelfUpdateActivitySource(LauncherSelfUpdateCoordinator source)
        : ILauncherSelfUpdateActivitySource
    {
        public event EventHandler<LauncherSelfUpdateSnapshotEventArgs>? SnapshotChanged
        {
            add => source.SnapshotChanged += value;
            remove => source.SnapshotChanged -= value;
        }

        public event EventHandler<LauncherSelfUpdateTerminalEventArgs>? OperationTerminated
        {
            add => source.OperationTerminated += value;
            remove => source.OperationTerminated -= value;
        }

        public LauncherSelfUpdateSnapshot CurrentSnapshot => source.CurrentSnapshot;
        public long? CurrentOperationId => source.CurrentOperationId;
    }

    private sealed class StaticGameActivitySource : IGameActivitySource
    {
        public event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public GameRuntimeSnapshot CurrentSnapshot { get; } = new(
            Sequence: 0,
            OperationId: null,
            Action: GameAction.Play,
            UpdateKnowledge: GameUpdateKnowledge.Unknown,
            Phase: GameVerificationPhase.Stable,
            IsVerifying: false,
            CanVerify: true,
            IsPlayable: true,
            InstallPath: @"C:\Atlas\WotLK",
            InstalledVersion: "3.4.3",
            AvailableVersion: null,
            ProcessedFileCount: null,
            TotalFileCount: null,
            FailureCategory: null);
    }

    private sealed class StaticAddonsActivitySource : IAddonsActivitySource
    {
        public event EventHandler<AddonsRuntimeSnapshotEventArgs>? SnapshotChanged
        {
            add { }
            remove { }
        }

        public AddonsRuntimeSnapshot CurrentSnapshot => AddonsRuntimeSnapshot.Initial;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
