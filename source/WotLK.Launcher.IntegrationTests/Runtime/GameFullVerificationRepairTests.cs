using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class GameFullVerificationRepairTests
{
    internal static async Task<int> RunAsync()
    {
        await ClassifyEveryManagedFileWithoutUsingCacheAsync();
        await HandleEmptyLargeAndCancelledVerificationAsync();
        await RejectUnsafeAndUnreadableFilesWithoutSavingCacheAsync();
        await RepairOnlyInvalidFilesAndSafelyCleanManagedHistoryAsync();
        await FinalizeValidClientWithoutDownloadAndSaveCacheLastAsync();
        await PreserveCacheAcrossRepairFailuresAsync();
        await CancelDownloadApplicationAndShutdownWithoutSavingCacheAsync();
        RefuseManualRepairWithoutSessionPathOrAvailability();
        CharacterizeGameRepairCompatibilityMatrix();
        await PublishCoherentRepairRuntimeStatesAsync();
        await CancelRetryAndIgnoreStaleRepairCallbacksAsync();
        await CoalesceFullVerificationAndSuppressShutdownCallbacksAsync();
        await VerifyRepairBindingsOnStaAsync();
        Console.WriteLine("Full verification and targeted repair OK (02C.1).");
        return 0;
    }

    private static async Task ClassifyEveryManagedFileWithoutUsingCacheAsync()
    {
        using TempDirectory temp = new("AtlasFullVerifyCategories");
        byte[] valid = Encoding.UTF8.GetBytes("valid");
        byte[] wrongHash = Encoding.UTF8.GetBytes("abcde");
        WriteBytes(temp.Path, "Data/valid.bin", valid);
        WriteBytes(temp.Path, "Data/size.bin", [1]);
        WriteBytes(temp.Path, "Data/hash.bin", wrongHash);
        WriteBytes(temp.Path, "Data/empty.bin", []);
        string lockedPath = WriteBytes(temp.Path, "Data/locked.bin", valid);
        using FileStream locked = new(
            lockedPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        LauncherManifest manifest = Manifest(
            "full-v1",
            Entry("Data/valid.bin", valid),
            Entry("Data/missing.bin", valid),
            Entry("Data/size.bin", Encoding.UTF8.GetBytes("longer")),
            Entry("Data/hash.bin", Encoding.UTF8.GetBytes("other")),
            Entry("Data/empty.bin", []),
            Entry("Data/locked.bin", valid),
            Entry(Path.Combine(Path.GetPathRoot(temp.Path)!, "outside.bin"), valid),
            Entry("../escape.bin", valid));
        List<GameFullVerificationProgress> progress = [];

        GameFullVerificationResult result = await new GameFullFileVerifier().VerifyAllAsync(
            temp.Path,
            manifest,
            progress.Add,
            CancellationToken.None);

        Dictionary<string, GameManagedFileStatus> statuses = result.Files.ToDictionary(
            item => item.File.Path,
            item => item.Status,
            StringComparer.OrdinalIgnoreCase);
        Equal(GameManagedFileStatus.Valid, statuses["Data/valid.bin"], "Le fichier valide doit rester Valid.");
        Equal(GameManagedFileStatus.Missing, statuses["Data/missing.bin"], "Le fichier absent doit être Missing.");
        Equal(GameManagedFileStatus.SizeMismatch, statuses["Data/size.bin"], "La mauvaise taille doit être détectée.");
        Equal(GameManagedFileStatus.HashMismatch, statuses["Data/hash.bin"], "Le mauvais SHA-256 doit être détecté.");
        Equal(GameManagedFileStatus.Valid, statuses["Data/empty.bin"], "Un fichier vide valide doit être accepté.");
        Equal(GameManagedFileStatus.ReadError, statuses["Data/locked.bin"], "Un fichier illisible doit être ReadError.");
        Equal(2, result.Files.Count(item => item.Status == GameManagedFileStatus.InvalidPath), "Les chemins absolu et traversant doivent être refusés.");
        Equal(3, result.RepairFiles.Count, "Seuls Missing, SizeMismatch et HashMismatch appartiennent au plan.");
        Equal(manifest.Files.Count, progress.Count, "Chaque fichier doit publier une progression réelle.");
        Equal(manifest.Files.Count, progress[^1].ProcessedFileCount, "La progression finale doit atteindre le total.");
        True(!File.Exists(Path.Combine(temp.Path, "escape.bin")), "Aucun chemin refusé ne doit être créé.");
    }

    private static async Task HandleEmptyLargeAndCancelledVerificationAsync()
    {
        using TempDirectory temp = new("AtlasFullVerifyScale");
        GameFullVerificationResult empty = await new GameFullFileVerifier().VerifyAllAsync(
            temp.Path,
            Manifest("empty-v1"),
            null,
            CancellationToken.None);
        Equal(0, empty.Files.Count, "Le vérificateur pur doit accepter une liste vide sans inventer de fichier.");

        const int fileCount = 1000;
        byte[] content = [];
        LauncherFile[] files = new LauncherFile[fileCount];
        for (int index = 0; index < fileCount; index++)
        {
            string relative = $"Data/empty-{index:D4}.bin";
            WriteBytes(temp.Path, relative, content);
            files[index] = Entry(relative, content);
        }

        int progressCount = 0;
        GameFullFileVerifier fastVerifier = new((_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Hash(content));
        });
        GameFullVerificationResult large = await fastVerifier.VerifyAllAsync(
            temp.Path,
            Manifest("large-v1", files),
            _ => progressCount++,
            CancellationToken.None);
        Equal(fileCount, large.Files.Count, "Les milliers de fichiers doivent tous être inspectés.");
        Equal(fileCount, progressCount, "Le service ne doit perdre aucun compteur avant coalescence UI.");
        True(large.Files.All(item => item.Status == GameManagedFileStatus.Valid), "Tous les fichiers témoins doivent rester valides.");

        TaskCompletionSource hashStarted = Signal();
        GameFullFileVerifier cancellable = new(async (_, token) =>
        {
            hashStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return string.Empty;
        });
        LauncherManifest oneFile = Manifest("cancel-v1", Entry("Data/cancel.bin", content));
        WriteBytes(temp.Path, "Data/cancel.bin", content);
        using LauncherOperationCoordinator operations = new();
        LauncherOperationLease lease = operations.TryBegin(
            LauncherOperationKind.GameRepair,
            canUserCancel: true,
            clientIsPlayable: true).Lease!;
        Task<GameFullVerificationResult> running = cancellable.VerifyAllAsync(
            temp.Path,
            oneFile,
            null,
            lease.CancellationToken);
        await hashStarted.Task;
        True(lease.CancelFromUser(), "Le hash complet doit accepter l’annulation utilisateur.");
        await ThrowsAsync<OperationCanceledException>(() => running);
        lease.Complete();

        TaskCompletionSource shutdownHashStarted = Signal();
        GameFullFileVerifier shutdownVerifier = new(async (_, token) =>
        {
            shutdownHashStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return string.Empty;
        });
        LauncherOperationLease shutdownLease = operations.TryBegin(
            LauncherOperationKind.GameRepair,
            canUserCancel: true,
            clientIsPlayable: true).Lease!;
        Task<GameFullVerificationResult> shuttingDown = shutdownVerifier.VerifyAllAsync(
            temp.Path,
            oneFile,
            null,
            shutdownLease.CancellationToken);
        await shutdownHashStarted.Task;
        True(operations.CancelForShutdown(), "La fermeture doit interrompre le hash complet.");
        await ThrowsAsync<OperationCanceledException>(() => shuttingDown);
        shutdownLease.Complete();
    }

    private static async Task RejectUnsafeAndUnreadableFilesWithoutSavingCacheAsync()
    {
        using (MaintenanceEnvironment unsafePath = new())
        {
            unsafePath.SetManifest(Manifest(
                "unsafe-v1",
                Entry("../outside.bin", Encoding.UTF8.GetBytes("x"))));
            await ThrowsAsync<InvalidDataException>(() => unsafePath.RunRepairAsync());
            Equal(0, unsafePath.Store.SaveCalls, "Un chemin refusé ne doit jamais remplacer le cache.");
            Equal(0, unsafePath.Downloads.RequestCount, "Un chemin refusé ne doit jamais être téléchargé.");
        }

        using (MaintenanceEnvironment unreadable = new())
        {
            byte[] content = Encoding.UTF8.GetBytes("locked");
            string path = WriteBytes(unreadable.Root, "Data/locked.bin", content);
            unreadable.SetManifest(Manifest("locked-v1", Entry("Data/locked.bin", content)));
            using FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await ThrowsAsync<IOException>(() => unreadable.RunRepairAsync());
            Equal(0, unreadable.Store.SaveCalls, "Un ReadError ne doit jamais remplacer le cache.");
            Equal(0, unreadable.Downloads.RequestCount, "Un ReadError ne doit pas être transformé en téléchargement.");
        }

        using MaintenanceEnvironment emptyManifest = new();
        emptyManifest.SetManifest(Manifest("empty-v1"));
        await ThrowsAsync<InvalidDataException>(() => emptyManifest.RunRepairAsync());
        Equal(0, emptyManifest.Store.SaveCalls, "Un manifeste vide ne doit pas confirmer À jour.");
    }

    private static async Task RepairOnlyInvalidFilesAndSafelyCleanManagedHistoryAsync()
    {
        using MaintenanceEnvironment environment = new();
        byte[] valid = Encoding.UTF8.GetBytes("valid-current");
        byte[] corruptExpected = Encoding.UTF8.GetBytes("fixed-corrupt");
        byte[] missingExpected = Encoding.UTF8.GetBytes("fixed-missing");
        WriteBytes(environment.Root, "Data/valid.bin", valid);
        WriteBytes(environment.Root, "Data/corrupt.bin", Encoding.UTF8.GetBytes("wrong-content"));
        WriteBytes(environment.Root, "Data/obsolete.bin", Encoding.UTF8.GetBytes("managed-old"));
        WriteBytes(environment.Root, "Screenshots/user-note.txt", Encoding.UTF8.GetBytes("user"));

        LauncherManifest previous = Manifest(
            "previous-v1",
            Entry("Data/valid.bin", valid),
            Entry("Data/corrupt.bin", corruptExpected),
            Entry("Data/obsolete.bin", Encoding.UTF8.GetBytes("managed-old")));
        environment.Store.Save(environment.Root, previous);
        int cacheBaseline = environment.Store.SaveCalls;

        LauncherManifest current = Manifest(
            "repair-v2",
            Entry("Data/valid.bin", valid, "https://atlas.test/valid.bin"),
            Entry("Data/corrupt.bin", corruptExpected, "https://atlas.test/corrupt.bin"),
            Entry("Data/missing.bin", missingExpected, "https://atlas.test/missing.bin"));
        environment.SetManifest(current);
        environment.Downloads.Responder = (_, request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/corrupt.bin" => Response(corruptExpected),
            "/missing.bin" => Response(missingExpected),
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };
        List<GameClientMaintenanceProgress> progress = [];

        GameClientMaintenanceResult result = await environment.RunRepairAsync(progress.Add);

        Equal(GameClientMaintenanceOutcome.Downloaded, result.Outcome, "La réparation ciblée doit signaler Downloaded.");
        Equal(2, result.DownloadedFileCount, "Deux fichiers seulement doivent être réparés.");
        Equal(2, environment.Downloads.RequestCount, "Le fichier valide ne doit jamais être retéléchargé.");
        SequenceEqual(corruptExpected, await File.ReadAllBytesAsync(GamePathPolicy.GetSafeTargetPath(environment.Root, "Data/corrupt.bin")), "Le fichier corrompu doit être remplacé.");
        SequenceEqual(missingExpected, await File.ReadAllBytesAsync(GamePathPolicy.GetSafeTargetPath(environment.Root, "Data/missing.bin")), "Le fichier absent doit être créé.");
        SequenceEqual(valid, await File.ReadAllBytesAsync(GamePathPolicy.GetSafeTargetPath(environment.Root, "Data/valid.bin")), "Le fichier valide doit être conservé.");
        True(!File.Exists(GamePathPolicy.GetSafeTargetPath(environment.Root, "Data/obsolete.bin")), "L’ancien fichier géré doit être nettoyé par la politique 02D.1.");
        True(File.Exists(GamePathPolicy.GetSafeTargetPath(environment.Root, "Screenshots/user-note.txt")), "Un fichier utilisateur non géré ne doit jamais être supprimé.");
        Equal(cacheBaseline + 1, environment.Store.SaveCalls, "Le cache doit être remplacé une seule fois après succès.");
        Equal(1, environment.Events.Count(item => item == "manifest-load"), "Le manifeste doit être obtenu une seule fois.");
        True(progress.All(item => item.OperationId == result.OperationId), "Le même OperationId doit couvrir analyse, réparation et finalisation.");
        True(environment.Events.LastIndexOf("register-game") < environment.Events.LastIndexOf("cache-save"), "Le cache de réparation doit être écrit après la finalisation plateforme.");
        AssertPhaseOrder(
            progress,
            GameClientMaintenancePhase.FullVerification,
            GameClientMaintenancePhase.ComparisonCompleted,
            GameClientMaintenancePhase.RepairDownloading,
            GameClientMaintenancePhase.RepairApplying,
            GameClientMaintenancePhase.Registering,
            GameClientMaintenancePhase.CacheSaved,
            GameClientMaintenancePhase.Completed);
    }

    private static async Task FinalizeValidClientWithoutDownloadAndSaveCacheLastAsync()
    {
        using (MaintenanceEnvironment current = new())
        {
            byte[] content = Encoding.UTF8.GetBytes("already-valid");
            WriteBytes(current.Root, "Data/client.bin", content);
            current.SetManifest(Manifest("current-v1", Entry("Data/client.bin", content)));
            List<GameClientMaintenanceProgress> progress = [];

            GameClientMaintenanceResult result = await current.RunRepairAsync(progress.Add);

            Equal(GameClientMaintenanceOutcome.AlreadyCurrent, result.Outcome, "Aucune réparation ne doit lancer un téléchargement.");
            Equal(0, current.Downloads.RequestCount, "Un client valide ne doit provoquer aucun HTTP fichier.");
            Equal(0, current.Platform.StopCalls, "WoW ne doit pas être arrêté lorsqu’aucune mutation fichier n’est requise.");
            Equal(1, current.Platform.RegisterCalls, "La version validée doit être enregistrée.");
            Equal(1, current.Store.SaveCalls, "Le cache complet doit être enregistré une fois.");
            True(current.Events.LastIndexOf("register-game") < current.Events.LastIndexOf("cache-save"), "Le cache doit rester la dernière écriture de finalisation.");
        }

        using MaintenanceEnvironment platformFailure = new();
        byte[] valid = Encoding.UTF8.GetBytes("valid-before-platform-failure");
        WriteBytes(platformFailure.Root, "Data/client.bin", valid);
        platformFailure.SetManifest(Manifest("platform-failure-v1", Entry("Data/client.bin", valid)));
        platformFailure.Platform.RegistrationFailure = new InvalidOperationException("platform failure");
        await ThrowsAsync<InvalidOperationException>(() => platformFailure.RunRepairAsync());
        Equal(0, platformFailure.Store.SaveCalls, "Une finalisation plateforme en erreur ne doit pas écrire le cache.");
    }

    private static async Task PreserveCacheAcrossRepairFailuresAsync()
    {
        Exception[] failures =
        [
            new HttpRequestException("network"),
            new InvalidOperationException("Taille invalide pour client.bin"),
            new InvalidOperationException("Hash invalide apres telechargement"),
            new IOException("Ferme le jeu: fichier verrouillé"),
            new UnauthorizedAccessException("permission")
        ];

        foreach (Exception failure in failures)
        {
            using MaintenanceEnvironment environment = new(
                new ThrowingFileTransferService(failure));
            byte[] expected = Encoding.UTF8.GetBytes("missing");
            environment.SetManifest(Manifest("failure-v1", Entry("Data/client.bin", expected)));
            Exception observed = await ThrowsAnyAsync(() => environment.RunRepairAsync());
            True(observed.GetType() == failure.GetType(), "La catégorie technique du pipeline doit être préservée.");
            Equal(0, environment.Store.SaveCalls, "Aucun échec de réparation ne doit remplacer le cache.");
        }

        byte[] expectedPayload = Encoding.UTF8.GetBytes("12345678");
        using (MaintenanceEnvironment wrongSize = new())
        {
            wrongSize.SetManifest(Manifest("size-v1", Entry("Data/client.bin", expectedPayload)));
            wrongSize.Downloads.Responder = (_, _, _) => Response(Encoding.UTF8.GetBytes("short"));
            await ThrowsAsync<InvalidOperationException>(() => wrongSize.RunRepairAsync());
            Equal(0, wrongSize.Store.SaveCalls, "Une taille HTTP réellement invalide ne doit pas écrire le cache.");
        }

        using MaintenanceEnvironment wrongHash = new();
        wrongHash.SetManifest(Manifest("hash-v1", Entry("Data/client.bin", expectedPayload)));
        wrongHash.Downloads.Responder = (_, _, _) => Response(Encoding.UTF8.GetBytes("87654321"));
        await ThrowsAsync<InvalidOperationException>(() => wrongHash.RunRepairAsync());
        Equal(0, wrongHash.Store.SaveCalls, "Un SHA-256 réellement invalide ne doit pas écrire le cache.");
    }

    private static async Task CancelDownloadApplicationAndShutdownWithoutSavingCacheAsync()
    {
        await CancelRepairAtStageAsync(GameFileTransferStage.Downloading, shutdown: false);
        await CancelRepairAtStageAsync(GameFileTransferStage.Applying, shutdown: false);
        await CancelRepairAtStageAsync(GameFileTransferStage.Downloading, shutdown: true);
        await CancelRepairAtStageAsync(GameFileTransferStage.Applying, shutdown: true);
    }

    private static async Task CancelRepairAtStageAsync(
        GameFileTransferStage stage,
        bool shutdown)
    {
        RepairBlockingTransferService transfer = new(stage);
        using MaintenanceEnvironment environment = new(transfer);
        byte[] expected = Encoding.UTF8.GetBytes("repair-me");
        environment.SetManifest(Manifest("cancel-v1", Entry("Data/client.bin", expected)));
        LauncherOperationLease? lease = null;
        Task<GameClientMaintenanceResult> running = environment.RunRepairAsync(
            leaseStarted: value => lease = value);
        await transfer.Started.Task;
        bool cancelled = shutdown
            ? lease!.CancelForShutdown()
            : lease!.CancelFromUser();
        True(cancelled, "La réparation active doit accepter l’annulation demandée.");
        True(!lease.CancelFromUser(), "Une seconde annulation doit rester idempotente.");
        await ThrowsAsync<OperationCanceledException>(() => running);
        Equal(0, environment.Store.SaveCalls, "Une réparation annulée ne doit jamais remplacer le cache.");
    }

    private static void RefuseManualRepairWithoutSessionPathOrAvailability()
    {
        using (RuntimeGameEnvironment noSession = new(playable: true, authenticated: false))
        {
            True(!noSession.Coordinator.CanVerify, "Vérifier doit être désactivé sans session.");
            Equal(GameVerificationStartStatus.Unauthenticated, noSession.Coordinator.TryStartFullRepair(), "Aucun manifeste ne doit être demandé sans session.");
            Equal(0, noSession.Maintenance.RepairCalls, "Le pipeline ne doit pas démarrer sans session.");
        }

        using (RuntimeGameEnvironment invalidPath = new(playable: true, authenticated: true))
        {
            invalidPath.Settings.InstallPath = "client-relatif";
            invalidPath.Coordinator.RefreshAuthenticationAvailability();
            True(!invalidPath.Coordinator.CanVerify, "Vérifier doit être désactivé pour un chemin relatif.");
            Equal(GameVerificationStartStatus.RejectedByCompatibility, invalidPath.Coordinator.TryStartFullRepair(), "Un chemin invalide doit être refusé immédiatement.");
            Equal(0, invalidPath.Maintenance.RepairCalls, "Le pipeline ne doit pas démarrer avec un chemin invalide.");
        }

        using RuntimeGameEnvironment busy = new(playable: true, authenticated: true);
        LauncherOperationLease addons = busy.Operations.TryBegin(
            LauncherOperationKind.Addons,
            canUserCancel: true).Lease!;
        True(!busy.Coordinator.CanVerify, "Vérifier doit être désactivé pendant Addons.");
        Equal(GameVerificationStartStatus.Busy, busy.Coordinator.TryStartFullRepair(), "Busy doit être immédiat et sans file d’attente.");
        addons.Complete();
        Equal(0, busy.Maintenance.RepairCalls, "La libération d’Addons ne doit pas rejouer le clic refusé.");
    }

    private static void CharacterizeGameRepairCompatibilityMatrix()
    {
        using (LauncherOperationCoordinator operations = new())
        {
            LauncherOperationLease repair = operations.TryBegin(
                LauncherOperationKind.GameRepair,
                canUserCancel: true,
                clientIsPlayable: true).Lease!;
            Equal(LauncherOperationStartStatus.Busy, operations.TryBegin(LauncherOperationKind.GameRepair, true, true).Status, "Une seconde réparation doit être refusée immédiatement.");
            Equal(LauncherOperationStartStatus.Busy, operations.TryBegin(LauncherOperationKind.Verify, false, true).Status, "L’analyse automatique ne doit pas chevaucher GameRepair.");
            Equal(LauncherOperationStartStatus.Busy, operations.TryBegin(LauncherOperationKind.GameInstall, true).Status, "Install est incompatible avec GameRepair.");
            Equal(LauncherOperationStartStatus.Busy, operations.TryBegin(LauncherOperationKind.GameUpdate, true, true).Status, "Update est incompatible avec GameRepair.");
            Equal(LauncherOperationStartStatus.Busy, operations.TryBegin(LauncherOperationKind.Addons, true).Status, "Addons est incompatible avec GameRepair.");
            Equal(LauncherOperationStartStatus.Busy, operations.TryBegin(LauncherOperationKind.LauncherAutoUpdate, true).Status, "L’auto-update est incompatible avec GameRepair.");
            Equal(LauncherOperationStartStatus.RejectedByCompatibility, operations.TryBeginPlay(true).Status, "Play est incompatible avec GameRepair.");
            repair.Complete();
        }

        using (LauncherOperationCoordinator verifyFirst = new())
        {
            LauncherOperationLease verify = verifyFirst.TryBegin(
                LauncherOperationKind.Verify,
                canUserCancel: false,
                clientIsPlayable: true).Lease!;
            Equal(LauncherOperationStartStatus.Busy, verifyFirst.TryBegin(LauncherOperationKind.GameRepair, true, true).Status, "GameRepair ne doit pas chevaucher l’analyse automatique.");
            verify.Complete();
        }

        using LauncherOperationCoordinator playFirst = new();
        LauncherOperationLease play = playFirst.TryBeginPlay(true).Lease!;
        Equal(LauncherOperationStartStatus.RejectedByCompatibility, playFirst.TryBegin(LauncherOperationKind.GameRepair, true, true).Status, "GameRepair doit refuser un Play actif.");
        play.Complete();
    }

    private static async Task PublishCoherentRepairRuntimeStatesAsync()
    {
        using RuntimeGameEnvironment environment = new(playable: true, authenticated: true);
        List<GameRuntimeSnapshot> snapshots = [environment.Coordinator.CurrentSnapshot];
        environment.Coordinator.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);
        environment.Maintenance.RepairHandler = (_, lease, progress) =>
        {
            progress?.Invoke(RepairProgress(lease, GameClientMaintenancePhase.ManifestLoaded, availableVersion: "repair-v2"));
            progress?.Invoke(RepairProgress(lease, GameClientMaintenancePhase.FullVerification, "Data/client.bin", 1, 1, availableVersion: "repair-v2"));
            progress?.Invoke(RepairProgress(lease, GameClientMaintenancePhase.ComparisonCompleted, missingCount: 0, availableVersion: "repair-v2"));
            progress?.Invoke(RepairProgress(lease, GameClientMaintenancePhase.Registering, availableVersion: "repair-v2"));
            progress?.Invoke(RepairProgress(lease, GameClientMaintenancePhase.CacheSaved, availableVersion: "repair-v2"));
            progress?.Invoke(RepairProgress(lease, GameClientMaintenancePhase.Completed, availableVersion: "repair-v2"));
            environment.LocalState = new GameClientLocalState(
                environment.Root,
                "frFR",
                true,
                "repair-v2",
                GameUpdateKnowledge.Unknown);
            return Task.FromResult(new GameClientMaintenanceResult(
                lease.OperationId,
                GameClientMaintenanceOutcome.AlreadyCurrent,
                "repair-v2",
                0,
                0,
                null,
                null));
        };

        True(environment.Coordinator.CanVerify, "Vérifier doit être actif avec session et client jouable.");
        Equal(GameVerificationStartStatus.Started, environment.Coordinator.TryStartFullRepair(), "Le clic Vérifier doit démarrer GameRepair.");
        await environment.Coordinator.WaitForIdleAsync();

        GameRuntimeSnapshot terminal = environment.Coordinator.CurrentSnapshot;
        Equal(GameAction.Play, terminal.Action, "Une vérification complète réussie doit publier Play.");
        Equal(GameUpdateKnowledge.Known, terminal.UpdateKnowledge, "Une vérification complète réussie doit confirmer Known.");
        Equal("repair-v2", terminal.InstalledVersion, "InstalledVersion doit correspondre au manifeste réparé.");
        Equal("repair-v2", terminal.AvailableVersion, "AvailableVersion doit correspondre au même manifeste.");
        Equal("À jour", GameStateAdapter.Project(terminal).InstallBadgeText, "L’interface doit afficher À jour.");
        True(snapshots.Any(item => item.OperationKind == LauncherOperationKind.GameRepair
            && item.MaintenancePhase == GameClientMaintenancePhase.FullVerification
            && GameStateAdapter.Project(item).PrimaryActionLabel == "Annuler"), "L’analyse complète doit afficher Annuler.");
        True(snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence), "Les snapshots GameRepair doivent rester strictement croissants.");
        Equal(1, environment.Maintenance.RepairCalls, "La V2 doit appeler un seul pipeline de réparation.");
        Equal(0, environment.Maintenance.Calls, "GameRepair ne doit pas appeler InstallOrUpdate.");
    }

    private static async Task CancelRetryAndIgnoreStaleRepairCallbacksAsync()
    {
        using (RuntimeGameEnvironment cancellation = new(playable: true, authenticated: true))
        {
            TaskCompletionSource entered = Signal();
            cancellation.Maintenance.RepairHandler = async (_, lease, progress) =>
            {
                progress?.Invoke(RepairProgress(lease, GameClientMaintenancePhase.ComparisonCompleted, missingCount: 1, availableVersion: "repair-v2"));
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, lease.CancellationToken);
                throw new InvalidOperationException("unreachable");
            };
            Equal(GameVerificationStartStatus.Started, cancellation.Coordinator.TryStartFullRepair(), "La réparation annulable doit démarrer.");
            await entered.Task;
            Equal(GamePrimaryActionStatus.CancelRequested, cancellation.Coordinator.TryExecutePrimaryAction(), "Le bouton principal doit déléguer CancelFromUser.");
            await cancellation.Coordinator.WaitForIdleAsync();
            GameRuntimeSnapshot cancelled = cancellation.Coordinator.CurrentSnapshot;
            Equal(GameAction.Update, cancelled.Action, "Une réparation interrompue après détection doit revenir à Update.");
            True(cancelled.ErrorCategory is null, "Une annulation ne doit jamais devenir une erreur rouge.");
        }

        using RuntimeGameEnvironment retry = new(playable: true, authenticated: true);
        Action<GameClientMaintenanceProgress>? oldProgress = null;
        long firstOperationId = 0;
        retry.Maintenance.RepairHandler = (_, lease, progress) =>
        {
            firstOperationId = lease.OperationId;
            oldProgress = progress;
            return Task.FromException<GameClientMaintenanceResult>(new HttpRequestException("network"));
        };
        Equal(GameVerificationStartStatus.Started, retry.Coordinator.TryStartFullRepair(), "La première tentative doit démarrer.");
        await retry.Coordinator.WaitForIdleAsync();
        GameRuntimeSnapshot failed = retry.Coordinator.CurrentSnapshot;
        Equal(LauncherOperationKind.GameRepair, failed.RetryOperationKind, "L’erreur doit conserver le type GameRepair.");
        Equal("Réessayer", GameStateAdapter.Project(failed).PrimaryActionLabel, "L’erreur doit proposer Réessayer.");

        TaskCompletionSource secondStarted = Signal();
        TaskCompletionSource release = Signal();
        retry.Maintenance.RepairHandler = async (_, lease, progress) =>
        {
            secondStarted.TrySetResult();
            await release.Task;
            retry.LocalState = new GameClientLocalState(retry.Root, "frFR", true, "repair-v3", GameUpdateKnowledge.Unknown);
            return new GameClientMaintenanceResult(lease.OperationId, GameClientMaintenanceOutcome.Downloaded, "repair-v3", 1, 0, null, null);
        };
        Equal(GamePrimaryActionStatus.Started, retry.Coordinator.TryExecutePrimaryAction(), "Réessayer doit acquérir un nouveau bail GameRepair.");
        await secondStarted.Task;
        long sequenceBeforeStale = retry.Coordinator.CurrentSnapshot.Sequence;
        oldProgress?.Invoke(new GameClientMaintenanceProgress(
            firstOperationId,
            GameClientMaintenancePhase.RepairDownloading,
            CurrentFile: "stale.bin",
            DownloadedBytes: 10,
            TotalBytes: 10));
        Equal(sequenceBeforeStale, retry.Coordinator.CurrentSnapshot.Sequence, "Un callback de l’ancienne tentative doit être ignoré.");
        release.TrySetResult();
        await retry.Coordinator.WaitForIdleAsync();
        True(retry.Maintenance.OperationIds.Distinct().Count() == 2
            && retry.Maintenance.OperationIds[1] > retry.Maintenance.OperationIds[0], "Le retry doit obtenir un nouvel OperationId monotone.");
        Equal(GameUpdateKnowledge.Known, retry.Coordinator.CurrentSnapshot.UpdateKnowledge, "Le retry réussi doit confirmer Known.");
    }

    private static async Task CoalesceFullVerificationAndSuppressShutdownCallbacksAsync()
    {
        ManualTimeProvider time = new();
        using (RuntimeGameEnvironment environment = new(playable: true, authenticated: true, time))
        {
            int snapshotCount = 0;
            environment.Coordinator.SnapshotChanged += (_, _) => snapshotCount++;
            environment.Maintenance.RepairHandler = (_, lease, progress) =>
            {
                for (int index = 1; index <= 1000; index++)
                {
                    progress?.Invoke(RepairProgress(
                        lease,
                        GameClientMaintenancePhase.FullVerification,
                        $"Data/{index}.bin",
                        index,
                        1000,
                        availableVersion: "bulk-v1"));
                }

                environment.LocalState = new GameClientLocalState(environment.Root, "frFR", true, "bulk-v1", GameUpdateKnowledge.Unknown);
                return Task.FromResult(new GameClientMaintenanceResult(lease.OperationId, GameClientMaintenanceOutcome.AlreadyCurrent, "bulk-v1", 0, 0, null, null));
            };
            Equal(GameVerificationStartStatus.Started, environment.Coordinator.TryStartFullRepair(), "Le test de coalescence doit démarrer.");
            await environment.Coordinator.WaitForIdleAsync();
            True(snapshotCount < 25, "La progression ordinaire de 1000 fichiers doit être coalescée.");
            Equal(GameUpdateKnowledge.Known, environment.Coordinator.CurrentSnapshot.UpdateKnowledge, "L’état terminal ne doit jamais être retardé par la coalescence.");
        }

        using RuntimeGameEnvironment shutdown = new(playable: true, authenticated: true);
        TaskCompletionSource started = Signal();
        Action<GameClientMaintenanceProgress>? lateProgress = null;
        shutdown.Maintenance.RepairHandler = async (_, lease, progress) =>
        {
            lateProgress = progress;
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, lease.CancellationToken);
            throw new InvalidOperationException("unreachable");
        };
        int published = 0;
        shutdown.Coordinator.SnapshotChanged += (_, _) => published++;
        Equal(GameVerificationStartStatus.Started, shutdown.Coordinator.TryStartFullRepair(), "La réparation de fermeture doit démarrer.");
        await started.Task;
        shutdown.Coordinator.BeginShutdown();
        await shutdown.Coordinator.WaitForIdleAsync();
        int afterShutdown = published;
        lateProgress?.Invoke(new GameClientMaintenanceProgress(
            shutdown.Coordinator.CurrentSnapshot.OperationId ?? 0,
            GameClientMaintenancePhase.FullVerification,
            CurrentFile: "late.bin",
            ProcessedFileCount: 1,
            TotalFileCount: 1));
        Equal(afterShutdown, published, "Aucun callback tardif ne doit être publié après fermeture.");
    }

    private static Task VerifyRepairBindingsOnStaAsync()
    {
        TaskCompletionSource completion = Signal();
        Thread thread = new(() => RunRepairWpfHarness(completion))
        {
            IsBackground = true,
            Name = "Atlas V2 full repair WPF bindings"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunRepairWpfHarness(TaskCompletionSource completion)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(async () =>
        {
            Application? application = null;
            Window? host = null;
            PrimaryActionCommand? primaryCommand = null;
            GameVerificationCommand? verifyCommand = null;
            GameStateAdapter? adapter = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                using RuntimeGameEnvironment environment = new(playable: true, authenticated: true);
                TaskCompletionSource fullStarted = Signal();
                TaskCompletionSource continueDownload = Signal();
                TaskCompletionSource downloadStarted = Signal();
                TaskCompletionSource continueApply = Signal();
                TaskCompletionSource applyStarted = Signal();
                TaskCompletionSource finish = Signal();
                environment.Maintenance.RepairHandler = async (_, lease, progress) =>
                {
                    progress?.Invoke(RepairProgress(
                        lease,
                        GameClientMaintenancePhase.FullVerification,
                        "Data/client.bin",
                        1,
                        2,
                        availableVersion: "wpf-repair-v1"));
                    fullStarted.TrySetResult();
                    await continueDownload.Task;
                    progress?.Invoke(RepairProgress(
                        lease,
                        GameClientMaintenancePhase.ComparisonCompleted,
                        missingCount: 1,
                        availableVersion: "wpf-repair-v1"));
                    progress?.Invoke(new GameClientMaintenanceProgress(
                        lease.OperationId,
                        GameClientMaintenancePhase.RepairDownloading,
                        AvailableVersion: "wpf-repair-v1",
                        CurrentFile: "Data/client.bin",
                        ProcessedFileCount: 0,
                        TotalFileCount: 1,
                        DownloadedBytes: 50,
                        TotalBytes: 100,
                        BytesPerSecond: 25,
                        Remaining: TimeSpan.FromSeconds(2)));
                    downloadStarted.TrySetResult();
                    await continueApply.Task;
                    progress?.Invoke(new GameClientMaintenanceProgress(
                        lease.OperationId,
                        GameClientMaintenancePhase.RepairApplying,
                        AvailableVersion: "wpf-repair-v1",
                        CurrentFile: "Data/client.bin",
                        ProcessedFileCount: 1,
                        TotalFileCount: 1,
                        DownloadedBytes: 100,
                        TotalBytes: 100));
                    applyStarted.TrySetResult();
                    await finish.Task;
                    environment.LocalState = new GameClientLocalState(
                        environment.Root,
                        "frFR",
                        true,
                        "wpf-repair-v1",
                        GameUpdateKnowledge.Unknown);
                    return new GameClientMaintenanceResult(
                        lease.OperationId,
                        GameClientMaintenanceOutcome.Downloaded,
                        "wpf-repair-v1",
                        1,
                        0,
                        null,
                        null);
                };

                GameUiState state = LauncherV2RuntimePresentation.CreateGame(environment.LocalState);
                state.AttachLocalCommands(PreviewCommand.Instance, PreviewCommand.Instance);
                primaryCommand = new PrimaryActionCommand(environment.Coordinator);
                verifyCommand = new GameVerificationCommand(environment.Coordinator);
                state.AttachPrimaryActionCommand(primaryCommand.Command);
                state.AttachVerifyCommand(verifyCommand.Command);
                adapter = new GameStateAdapter(state, environment.Coordinator, dispatcher);
                bool partialNotification = false;
                int groupedNotifications = 0;
                state.PropertyChanged += (_, args) =>
                {
                    if (string.IsNullOrEmpty(args.PropertyName))
                    {
                        groupedNotifications++;
                    }
                    else
                    {
                        partialNotification = true;
                    }
                };

                GameViewV2 view = new() { State = state };
                host = new Window
                {
                    Width = 1080,
                    Height = 680,
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Content = view
                };
                host.Show();
                view.UpdateLayout();
                True(!FindButtons(view, "Vérifier le client").Any(),
                    "La page Jeu immersive ne doit plus dupliquer Vérifier et réparer.");
                True(state.VerifyCommand.CanExecute(null),
                    "La commande Vérifier et réparer doit rester active pour Paramètres.");
                state.VerifyCommand.Execute(null);

                await fullStarted.Task;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal("Vérification complète", state.ClientStatus, "Le statut WPF d’analyse complète est incorrect.");
                Equal("Annuler", state.PrimaryActionLabel, "L’analyse complète doit proposer Annuler.");
                Equal(50d, state.Progress, "La progression WPF par fichiers doit être réelle.");
                True(!state.IsProgressIndeterminate && state.IsPrimaryActionEnabled, "L’analyse complète doit être déterminée et annulable.");

                continueDownload.TrySetResult();
                await downloadStarted.Task;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal("Réparation en cours", state.ClientStatus, "Le téléchargement de réparation doit utiliser son statut dédié.");
                Equal(50d, state.Progress, "Le téléchargement de réparation doit afficher son pourcentage réel.");
                True(state.ProgressPrimaryDetail.Contains("100", StringComparison.Ordinal)
                    && state.ProgressSecondaryDetail.Contains("/s", StringComparison.Ordinal)
                    && state.ProgressSecondaryDetail.Contains("restantes", StringComparison.Ordinal), "Taille, vitesse et ETA doivent être visibles.");

                continueApply.TrySetResult();
                await applyStarted.Task;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal(GamePreviewScenario.Installing, state.Scenario, "L’application doit utiliser l’état Installing validé.");
                Equal("Application de la réparation", state.ProgressTitle, "Le titre de phase d’application est incorrect.");
                True(state.ProgressPrimaryDetail.Contains("1/1", StringComparison.Ordinal), "Les compteurs de fichiers appliqués doivent être visibles.");

                finish.TrySetResult();
                await environment.Coordinator.WaitForIdleAsync();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal(GamePreviewScenario.Ready, state.Scenario, "Le succès doit revenir à Ready.");
                Equal("À jour", state.InstallBadgeText, "Le badge WPF final doit confirmer À jour.");
                Equal("Jouer", state.PrimaryActionLabel, "Jouer doit redevenir visible après réparation.");
                True(!state.IsPrimaryActionEnabled, "Jouer doit rester désactivé jusqu’à 02F.3.");
                True(!partialNotification && groupedNotifications >= 4, "Les snapshots de réparation doivent être appliqués atomiquement.");
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                adapter?.Dispose();
                verifyCommand?.Dispose();
                primaryCommand?.Dispose();
                host?.Close();
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    }

    private static IEnumerable<Button> FindButtons(
        DependencyObject root,
        string automationName)
    {
        return FindVisualChildren<Button>(root)
            .Where(button => string.Equals(
                AutomationProperties.GetName(button),
                automationName,
                StringComparison.Ordinal));
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void LoadV2Resources(Application application)
    {
        foreach (string resourcePath in new[]
        {
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(resourcePath, UriKind.Relative)
            });
        }
    }

    private static GameClientMaintenanceProgress RepairProgress(
        LauncherOperationLease lease,
        GameClientMaintenancePhase phase,
        string? currentFile = null,
        int? processed = null,
        int? total = null,
        int? missingCount = null,
        string? availableVersion = null)
    {
        return new GameClientMaintenanceProgress(
            lease.OperationId,
            phase,
            AvailableVersion: availableVersion,
            CurrentFile: currentFile,
            ProcessedFileCount: processed,
            TotalFileCount: total,
            MissingOrChangedFileCount: missingCount);
    }

    private static LauncherManifest Manifest(string version, params LauncherFile[] files)
    {
        return new LauncherManifest
        {
            Version = version,
            BaseUrl = "https://atlas.test/client/",
            Files = files.ToList()
        };
    }

    private static LauncherFile Entry(string path, byte[] content, string? url = null)
    {
        return new LauncherFile
        {
            Path = path,
            Size = content.LongLength,
            Sha256 = Hash(content),
            Url = url ?? string.Empty
        };
    }

    private static string WriteBytes(string root, string relativePath, byte[] content)
    {
        string path = GamePathPolicy.GetSafeTargetPath(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Hash(byte[] content)
    {
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }

    private static HttpResponseMessage Response(byte[] content)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };
        response.Content.Headers.ContentLength = content.LongLength;
        return response;
    }

    private static void AssertPhaseOrder(
        IReadOnlyList<GameClientMaintenanceProgress> actual,
        params GameClientMaintenancePhase[] expected)
    {
        int previous = -1;
        List<GameClientMaintenancePhase> phases = actual.Select(item => item.Phase).ToList();
        foreach (GameClientMaintenancePhase phase in expected)
        {
            int index = phases.FindIndex(previous + 1, item => item == phase);
            True(index >= 0, "Phase absente ou désordonnée: " + phase);
            previous = index;
        }
    }

    private static TaskCompletionSource Signal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<T> ThrowsAsync<T>(Func<Task> action)
        where T : Exception
    {
        try
        {
            await action();
        }
        catch (T exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Exception attendue: " + typeof(T).Name);
    }

    private static async Task<Exception> ThrowsAnyAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            return exception;
        }

        throw new InvalidOperationException("Une exception était attendue.");
    }

    private static void SequenceEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

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
}

internal sealed class RepairBlockingTransferService(
    GameFileTransferStage stage) : IGameFileTransferService
{
    internal TaskCompletionSource Started { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Uri BuildFileUri(LauncherManifest manifest, LauncherFile file)
    {
        return new Uri("https://atlas.test/repair.bin");
    }

    public async Task DownloadAsync(
        long operationId,
        Uri uri,
        string targetPath,
        long expectedSize,
        string expectedSha256,
        Action<GameFileTransferProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        reportProgress?.Invoke(new GameFileTransferProgress(
            operationId,
            0,
            expectedSize,
            stage));
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
