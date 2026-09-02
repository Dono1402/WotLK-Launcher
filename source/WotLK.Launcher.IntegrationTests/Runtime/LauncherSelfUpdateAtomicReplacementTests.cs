using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WotLK.Launcher.Updater;

internal static class LauncherSelfUpdateAtomicReplacementTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeSingleExecutableReleaseContract();
        ValidateInternalCommandLineContract();
        ValidateHelperRequesterBoundary();
        await PrepareTransactionWithoutTouchingActiveReleaseAsync();
        await RejectInvalidCandidateBeforeTouchingReleaseAsync();
        await KeepPreviousReleaseAcrossPreSwapCrashPointsAsync();
        await RetryTransientAtomicSwapFailureAsync();
        await AbandonPermanentAtomicSwapFailureAsync();
        await RecoverCrashAfterAtomicSwapAsync();
        await RecoverCrashBeforeNewLauncherStartAsync();
        await RecoverCrashAfterNewLauncherStartAsync();
        await RecoverCrashAfterReadyConfirmationAsync();
        await RecoverCrashAfterCommitPersistedAsync();
        await RollBackWhenNewLauncherCannotStartAsync();
        await RollBackWhenNewLauncherExitsImmediatelyAsync();
        await RejectReadyFromExitedLauncherAsync();
        await RollBackWhenReadyNeverArrivesAsync();
        await IgnoreWrongAndStaleReadySignalsAsync();
        await AcceptImmediateAndDelayedReadySignalsAsync();
        await RetryTemporaryWindowsLockAsync();
        await AbandonPermanentWindowsLockAsync();
        await KeepTargetWholeDuringAtomicSwapAsync();
        await RefuseUnsafeTransactionPathsAsync();
        await LeaveReleaseUntouchedWhenParentDoesNotExitAsync();
        Console.WriteLine("Launcher self-update atomic replacement OK (04B.3a).");
        return 0;
    }

    private static void CharacterizeSingleExecutableReleaseContract()
    {
        string repository = FindRepositoryRoot();
        string project = File.ReadAllText(
            Path.Combine(repository, "source", "WotLK.Launcher", "WotLK.Launcher.csproj"));
        string readme = File.ReadAllText(
            Path.Combine(repository, "source", "README.md"));
        string manifest = File.ReadAllText(
            Path.Combine(repository, "source", "launcher-update.json"));

        True(readme.Contains("-p:PublishSingleFile=true", StringComparison.Ordinal),
            "La release launcher caractérisée doit rester publiée en fichier unique.");
        True(project.Contains("IncludeNativeLibrariesForSelfExtract", StringComparison.Ordinal),
            "Le projet doit conserver son contrat single-file natif.");
        True(manifest.Contains("WotLK-Launcher.exe", StringComparison.Ordinal)
             && !manifest.Contains("files", StringComparison.OrdinalIgnoreCase),
            "Le manifeste d'auto-update doit continuer à cibler un unique EXE.");
    }

    private static void ValidateInternalCommandLineContract()
    {
        True(LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.ApplySwitch, @"C:\temp\transaction.json", "42"],
                out bool recovery,
                out string path,
                out int requesterProcessId)
             && !recovery
             && requesterProcessId == 42
             && path.EndsWith("transaction.json", StringComparison.Ordinal),
            "Le mode helper Apply doit être explicite et strict.");
        True(LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.RecoverSwitch, @"C:\temp\transaction.json", "43"],
                out recovery,
                out _,
                out requesterProcessId)
             && recovery
             && requesterProcessId == 43,
            "Le mode helper Recovery doit être distinct.");
        True(!LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.ApplySwitch, "one", "extra"],
                out _,
                out _,
                out _),
            "Des arguments helper supplémentaires doivent être refusés.");
        True(!LauncherUpdateCommandLine.TryParseHelper(
                [LauncherUpdateCommandLine.ApplySwitch, @"C:\temp\transaction.json", "0"],
                out _,
                out _,
                out _),
            "Un PID demandeur invalide doit être refusé.");

        Guid id = Guid.NewGuid();
        string postUpdate = LauncherUpdateCommandLine.BuildPostUpdateArgument(id);
        Equal(id, LauncherUpdateCommandLine.FindPostUpdateTransaction([postUpdate]),
            "Le handshake doit transporter uniquement l'identifiant de transaction.");
        SequenceEqual(
            ["--ui-v2"],
            LauncherUpdateCommandLine.ApplicationArguments(["--ui-v2", postUpdate]),
            "L'argument interne ne doit pas modifier la résolution du mode UI.");
    }

    private static void ValidateHelperRequesterBoundary()
    {
        using AtomicUpdateEnvironment environment = new();
        LauncherUpdateHelperRunner.ValidateRequester(
            recovery: false,
            environment.Transaction,
            environment.Transaction.ParentProcessId,
            (processId, path) => processId == environment.Transaction.ParentProcessId
                                 && string.Equals(
                                     path,
                                     environment.TargetPath,
                                     StringComparison.OrdinalIgnoreCase));

        Throws<InvalidDataException>(
            () => LauncherUpdateHelperRunner.ValidateRequester(
                recovery: false,
                environment.Transaction,
                environment.Transaction.ParentProcessId + 1,
                (_, _) => true),
            "Le helper Apply doit refuser un autre processus demandeur.");
        Throws<InvalidDataException>(
            () => LauncherUpdateHelperRunner.ValidateRequester(
                recovery: true,
                environment.Transaction,
                environment.Transaction.ParentProcessId,
                (_, _) => false),
            "Le helper doit refuser une cible arbitraire ne correspondant pas au demandeur vivant.");
    }

    private static async Task PrepareTransactionWithoutTouchingActiveReleaseAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        RecordingHelperLauncher helper = new();
        LauncherSelfUpdateFinalizer finalizer = new(
            environment.TransactionsRoot,
            environment.Store,
            helper);
        string downloaded = Path.Combine(environment.Root, "downloaded.exe");
        await File.WriteAllBytesAsync(downloaded, environment.NewBytes);

        LauncherUpdateTransaction transaction = await finalizer.PrepareAndLaunchAsync(
            environment.TargetPath,
            downloaded,
            environment.NewBytes.Length,
            Hash(environment.NewBytes),
            Environment.ProcessId,
            CancellationToken.None);

        Equal(1, helper.ApplyCalls, "La préparation doit démarrer un unique helper élevé.");
        Equal(transaction.TransactionId, helper.LastTransaction!.TransactionId,
            "Le helper doit recevoir la transaction préparée.");
        BytesEqual(environment.OldBytes, await File.ReadAllBytesAsync(environment.TargetPath),
            "La préparation ne doit jamais modifier la release active.");
        BytesEqual(environment.NewBytes, await File.ReadAllBytesAsync(transaction.CandidatePath),
            "Le candidat durable doit être complet avant le helper.");
        BytesEqual(environment.OldBytes, await File.ReadAllBytesAsync(transaction.HelperPath),
            "Le helper doit être une copie valide de l'ancienne release single-file.");
        True(!File.Exists(downloaded),
            "Le téléchargement initial doit être nettoyé après sa copie durable validée.");
        True(File.Exists(transaction.TransactionPath),
            "Le marqueur transactionnel doit précéder l'élévation.");
    }

    private static async Task RejectInvalidCandidateBeforeTouchingReleaseAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        environment.CorruptCandidate();
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un candidat corrompu doit être refusé avant le swap.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Aucun nouveau processus ne doit être créé après validation invalide.");
    }

    private static async Task KeepPreviousReleaseAcrossPreSwapCrashPointsAsync()
    {
        LauncherUpdateFaultPoint[] points =
        [
            LauncherUpdateFaultPoint.BeforeCandidateValidation,
            LauncherUpdateFaultPoint.AfterCandidateValidation,
            LauncherUpdateFaultPoint.AfterCandidateStaged,
            LauncherUpdateFaultPoint.AfterBackupCreated
        ];

        foreach (LauncherUpdateFaultPoint point in points)
        {
            using AtomicUpdateEnvironment environment = new(faultPoint: point);
            await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
                () => environment.Service.ApplyAsync(environment.Transaction));
            await environment.AssertTargetIsOldAsync();

            LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
                environment.Store.Load(environment.Transaction.TransactionPath));
            Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, recovered.Outcome,
                $"La récupération de {point} doit conserver l'ancienne version.");
            await environment.AssertTargetIsOldAsync();
        }
    }

    private static async Task RecoverCrashAfterAtomicSwapAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterAtomicSwap);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));
        await environment.AssertTargetIsNewAsync();
        True(File.Exists(environment.Transaction.BackupPath),
            "Le backup doit survivre à un crash juste après le swap.");

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.RolledBack, recovered.Outcome,
            "Une transaction swapée sans Ready doit être rollbackée.");
        await environment.AssertTargetIsOldAsync();
        Equal(1, environment.RecoveryLauncher.RollbackLaunchCalls,
            "L'ancienne version doit être relancée après récupération.");
    }

    private static async Task RetryTransientAtomicSwapFailureAsync()
    {
        FailingThenAtomicMover mover = new(failuresBeforeSuccess: 2);
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 4),
            atomicMover: mover);

        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.Succeeded, result.Outcome,
            "Un verrou transitoire au swap doit être réessayé.");
        Equal(3, mover.Attempts,
            "Le swap doit réussir immédiatement après les deux échecs transitoires.");
        await environment.AssertTargetIsNewAsync();
    }

    private static async Task AbandonPermanentAtomicSwapFailureAsync()
    {
        AlwaysFailingAtomicMover mover = new();
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 3),
            atomicMover: mover);

        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un swap durablement refusé doit abandonner sans toucher à l'ancienne release.");
        Equal(3, mover.Attempts, "Les retries du swap doivent rester bornés.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Aucun nouveau launcher ne doit être lancé après un swap refusé.");
    }

    private static async Task RecoverCrashBeforeNewLauncherStartAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.BeforeNewLauncherStart);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.RolledBack, recovered.Outcome,
            "Un crash avant le lancement doit restaurer l'ancienne release.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RecoverCrashAfterNewLauncherStartAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterNewLauncherStart,
            launchBehavior: FakeLaunchBehavior.NoReady);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.RolledBack, recovered.Outcome,
            "Un launcher démarré sans Ready doit être arrêté puis rollbacké après reprise.");
        True(environment.Launcher.LastProcess is { KillCalls: 1 },
            "La récupération doit arrêter le nouveau processus non confirmé.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RecoverCrashAfterReadyConfirmationAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterReadyConfirmation,
            launchBehavior: FakeLaunchBehavior.Ready);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            environment.Store.Load(environment.Transaction.TransactionPath));
        Equal(LauncherUpdateExecutionOutcome.Succeeded, recovered.Outcome,
            "Un Ready encore rattaché au bon processus doit permettre de terminer le commit.");
        await environment.AssertTargetIsNewAsync();
        True(!File.Exists(environment.Transaction.BackupPath),
            "Le backup peut être supprimé après récupération d'un Ready valide.");
    }

    private static async Task RecoverCrashAfterCommitPersistedAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            faultPoint: LauncherUpdateFaultPoint.AfterCommitPersisted,
            launchBehavior: FakeLaunchBehavior.Ready,
            recoveryProcessIsAlive: false);
        await ThrowsAsync<LauncherUpdateSimulatedCrashException>(
            () => environment.Service.ApplyAsync(environment.Transaction));
        LauncherUpdateTransaction persisted = environment.Store.Load(
            environment.Transaction.TransactionPath);
        Equal(LauncherUpdateTransactionPhase.Committed, persisted.Phase,
            "Le commit doit être durable avant la suppression du backup.");

        LauncherUpdateExecutionResult recovered = await environment.RecoveryService.RecoverAsync(
            persisted);
        Equal(LauncherUpdateExecutionOutcome.Succeeded, recovered.Outcome,
            "Un commit durable doit rester valide même si son processus a disparu.");
        await environment.AssertTargetIsNewAsync();
        True(!File.Exists(environment.Transaction.BackupPath)
             && !File.Exists(environment.Transaction.TransactionPath),
            "La reprise doit terminer le nettoyage d'un commit interrompu.");
    }

    private static async Task RollBackWhenNewLauncherCannotStartAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.Throw);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "Un Process.Start impossible doit provoquer un rollback.");
        await environment.AssertTargetIsOldAsync();
        Equal(1, environment.Launcher.RollbackLaunchCalls,
            "L'ancienne release doit être relancée après l'échec.");
    }

    private static async Task RollBackWhenNewLauncherExitsImmediatelyAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.ImmediateExit);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "Un crash immédiat avant Ready doit provoquer un rollback.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RejectReadyFromExitedLauncherAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.ReadyThenExit);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "Un marqueur Ready ne doit pas valider un processus déjà terminé.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task RollBackWhenReadyNeverArrivesAsync()
    {
        using AtomicUpdateEnvironment environment = new(
            launchBehavior: FakeLaunchBehavior.NoReady);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
            "L'absence de Ready doit provoquer un rollback après timeout.");
        True(environment.Launcher.LastProcess?.KillCalls == 1,
            "Le processus non confirmé doit être arrêté avant rollback.");
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task IgnoreWrongAndStaleReadySignalsAsync()
    {
        foreach (FakeLaunchBehavior behavior in new[]
                 {
                     FakeLaunchBehavior.WrongTransactionReady,
                     FakeLaunchBehavior.WrongProcessReady
                 })
        {
            using AtomicUpdateEnvironment environment = new(launchBehavior: behavior);
            LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
                environment.Transaction);
            Equal(LauncherUpdateExecutionOutcome.RolledBack, result.Outcome,
                $"Le signal {behavior} ne doit jamais confirmer la transaction.");
            await environment.AssertTargetIsOldAsync();
        }
    }

    private static async Task AcceptImmediateAndDelayedReadySignalsAsync()
    {
        foreach (FakeLaunchBehavior behavior in new[]
                 {
                     FakeLaunchBehavior.Ready,
                     FakeLaunchBehavior.DelayedReady
                 })
        {
            using AtomicUpdateEnvironment environment = new(launchBehavior: behavior);
            LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
                environment.Transaction);
            Equal(LauncherUpdateExecutionOutcome.Succeeded, result.Outcome,
                $"Le signal {behavior} doit confirmer la transaction.");
            await environment.AssertTargetIsNewAsync();
            True(!File.Exists(environment.Transaction.BackupPath),
                "Le backup ne doit être supprimé qu'après Ready valide.");
            True(!File.Exists(environment.Transaction.TransactionPath),
                "Le marqueur doit être retiré après commit.");
            Equal(0, environment.Launcher.RollbackLaunchCalls,
                "Une mise à jour confirmée ne doit pas relancer l'ancienne version.");
        }
    }

    private static async Task RetryTemporaryWindowsLockAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsLockingAtomicMover mover = new(TimeSpan.FromMilliseconds(80));
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 30),
            atomicMover: mover);

        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);
        await mover.WaitForAutomaticReleaseAsync();
        Equal(LauncherUpdateExecutionOutcome.Succeeded, result.Outcome,
            "Un verrou Windows transitoire pendant le swap doit être absorbé par les retries.");
        True(mover.Attempts > 1,
            "Le test Windows doit observer au moins un échec réel de MoveFileEx.");
        await environment.AssertTargetIsNewAsync();
    }

    private static async Task AbandonPermanentWindowsLockAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using WindowsLockingAtomicMover mover = new(releaseAfter: null);
        using AtomicUpdateEnvironment environment = new(
            retryPolicy: FastRetryPolicy(fileAttempts: 3),
            atomicMover: mover);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);

        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Un verrou permanent pendant le swap doit abandonner après des retries bornés.");
        Equal(3, mover.Attempts, "Le verrou permanent ne doit provoquer aucune boucle infinie.");
        mover.Release();
        await environment.AssertTargetIsOldAsync();
    }

    private static async Task KeepTargetWholeDuringAtomicSwapAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string root = Path.Combine(
            Path.GetTempPath(),
            "AtlasAtomicObservation",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string target = Path.Combine(root, "AtlasLauncher.exe");
        string candidate = Path.Combine(root, "AtlasLauncher.exe.new");
        byte[] oldBytes = CreatePayload("old", 2 * 1024 * 1024);
        byte[] newBytes = CreatePayload("new", 2 * 1024 * 1024);
        await File.WriteAllBytesAsync(target, oldBytes);
        await File.WriteAllBytesAsync(candidate, newBytes);
        string oldHash = Hash(oldBytes);
        string newHash = Hash(newBytes);
        ConcurrentBag<string> observedHashes = [];
        using CancellationTokenSource stop = new();

        Task observer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using FileStream stream = new(
                        target,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    observedHashes.Add(Convert.ToHexString(
                        await SHA256.HashDataAsync(stream)).ToLowerInvariant());
                }
                catch (IOException)
                {
                }

                await Task.Delay(5);
            }
        });

        try
        {
            await Task.Delay(20);
            WindowsLauncherAtomicFileMover mover = new();
            bool swapped = false;
            for (int attempt = 0; attempt < 100 && !swapped; attempt++)
            {
                try
                {
                    mover.Replace(candidate, target);
                    swapped = true;
                }
                catch (IOException) when (attempt < 99)
                {
                    await Task.Delay(2);
                }
            }

            True(swapped, "Le swap atomique doit finir par réussir malgré les lectures concurrentes.");
            await Task.Delay(20);
            stop.Cancel();
            await observer;
            string finalHash = await LauncherUpdateTransactionStore.ComputeSha256Async(
                target,
                CancellationToken.None);
            observedHashes.Add(finalHash);
            True(observedHashes.Count > 0
                 && observedHashes.All(hash => hash == oldHash || hash == newHash),
                "Un observateur ne doit voir que l'ancien ou le nouveau fichier complet.");
            Equal(newHash, finalHash,
                "La destination finale doit être le nouveau fichier complet.");
        }
        finally
        {
            stop.Cancel();
            await IgnoreFailureAsync(observer);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task RefuseUnsafeTransactionPathsAsync()
    {
        using AtomicUpdateEnvironment environment = new();
        LauncherUpdateTransaction escaped = environment.Transaction with
        {
            CandidatePath = Path.Combine(environment.Root, "outside.exe")
        };
        await File.WriteAllBytesAsync(escaped.CandidatePath, environment.NewBytes);

        Throws<InvalidDataException>(
            () => environment.Store.Save(escaped),
            "Un chemin candidat hors du workspace doit être refusé.");

        LauncherUpdateTransaction networkTarget = environment.Transaction with
        {
            TargetPath = @"\\server\share\AtlasLauncher.exe"
        };
        Throws<InvalidDataException>(
            () => environment.Store.Save(networkTarget),
            "Une destination réseau doit être refusée.");

        LauncherUpdateTransaction externalWorkspace = environment.Transaction with
        {
            WorkspacePath = Path.Combine(environment.Root, Guid.NewGuid().ToString("N"))
        };
        Throws<InvalidDataException>(
            () => environment.Store.Save(externalWorkspace),
            "Le marqueur doit rester sous la racine interne des transactions.");
    }

    private static async Task LeaveReleaseUntouchedWhenParentDoesNotExitAsync()
    {
        using AtomicUpdateEnvironment environment = new(parentExits: false);
        LauncherUpdateExecutionResult result = await environment.Service.ApplyAsync(
            environment.Transaction);
        Equal(LauncherUpdateExecutionOutcome.PreviousVersionIntact, result.Outcome,
            "Le helper doit abandonner si le PID parent ne se ferme pas.");
        await environment.AssertTargetIsOldAsync();
        Equal(0, environment.Launcher.UpdatedLaunchCalls,
            "Aucun nouveau launcher ne doit être démarré tant que l'ancien vit.");
    }

    private static LauncherUpdateRetryPolicy FastRetryPolicy(int fileAttempts = 5) => new(
        fileAttempts,
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(140),
        TimeSpan.FromMilliseconds(10));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "source", "README.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static byte[] CreatePayload(string marker, int size = 256 * 1024)
    {
        byte[] markerBytes = Encoding.UTF8.GetBytes(marker);
        byte[] payload = new byte[size];
        for (int index = 0; index < payload.Length; index++)
        {
            payload[index] = markerBytes[index % markerBytes.Length];
        }

        return payload;
    }

    private static string Hash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

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

    private static void Throws<T>(Action action, string message)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static void BytesEqual(byte[] expected, byte[] actual, string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void SequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string message)
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
            throw new InvalidOperationException(
                $"{message} Attendu={expected}; actuel={actual}.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private enum FakeLaunchBehavior
    {
        Ready,
        DelayedReady,
        NoReady,
        WrongTransactionReady,
        WrongProcessReady,
        ImmediateExit,
        ReadyThenExit,
        Throw
    }

    private sealed class AtomicUpdateEnvironment : IDisposable
    {
        private readonly LauncherUpdateFaultPoint? _faultPoint;

        internal AtomicUpdateEnvironment(
            LauncherUpdateFaultPoint? faultPoint = null,
            FakeLaunchBehavior launchBehavior = FakeLaunchBehavior.Ready,
            LauncherUpdateRetryPolicy? retryPolicy = null,
            bool parentExits = true,
            ILauncherAtomicFileMover? atomicMover = null,
            bool recoveryProcessIsAlive = true)
        {
            _faultPoint = faultPoint;
            Root = Path.Combine(
                Path.GetTempPath(),
                "AtlasLauncherAtomicTests",
                Guid.NewGuid().ToString("N"));
            TransactionsRoot = Path.Combine(Root, "SelfUpdate", "Transactions");
            Directory.CreateDirectory(TransactionsRoot);
            OldBytes = CreatePayload("old-release");
            NewBytes = CreatePayload("new-release");
            string install = Path.Combine(Root, "install");
            Directory.CreateDirectory(install);
            TargetPath = Path.Combine(install, "AtlasLauncher.exe");
            File.WriteAllBytes(TargetPath, OldBytes);

            Store = new LauncherUpdateTransactionStore(TransactionsRoot);
            Transaction = CreateTransaction();
            Store.Save(Transaction);
            Launcher = new FakeApplicationLauncher(Store, launchBehavior);
            RecoveryLauncher = new FakeApplicationLauncher(Store, FakeLaunchBehavior.Ready);
            LauncherUpdateRetryPolicy policy = retryPolicy ?? FastRetryPolicy();
            ILauncherUpdateFaultInjector injector = faultPoint is null
                ? NullLauncherUpdateFaultInjector.Instance
                : new ThrowingFaultInjector(faultPoint.Value);
            Service = new LauncherAtomicReplacementService(
                Store,
                atomicMover ?? new WindowsLauncherAtomicFileMover(),
                new FakeParentWaiter(parentExits),
                Launcher,
                policy,
                injector);
            RecoveryService = new LauncherAtomicReplacementService(
                Store,
                new WindowsLauncherAtomicFileMover(),
                new FakeParentWaiter(true),
                RecoveryLauncher,
                policy,
                processMatchesPath: (processId, path) =>
                    recoveryProcessIsAlive
                    && Launcher.LastProcess is { HasExited: false } process
                    && process.ProcessId == processId
                    && string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase),
                stopProcess: (processId, path) =>
                {
                    if (Launcher.LastProcess is { HasExited: false } process
                        && process.ProcessId == processId
                        && string.Equals(path, TargetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                    }
                });
        }

        internal string Root { get; }

        internal string TransactionsRoot { get; }

        internal string TargetPath { get; }

        internal byte[] OldBytes { get; }

        internal byte[] NewBytes { get; }

        internal LauncherUpdateTransactionStore Store { get; }

        internal LauncherUpdateTransaction Transaction { get; private set; }

        internal FakeApplicationLauncher Launcher { get; }

        internal FakeApplicationLauncher RecoveryLauncher { get; }

        internal LauncherAtomicReplacementService Service { get; }

        internal LauncherAtomicReplacementService RecoveryService { get; }

        internal void CorruptCandidate()
        {
            File.WriteAllText(Transaction.CandidatePath, "corrupt");
        }

        internal async Task AssertTargetIsOldAsync()
        {
            Equal(Hash(OldBytes), await LauncherUpdateTransactionStore.ComputeSha256Async(
                    TargetPath,
                    CancellationToken.None),
                "La cible doit contenir exactement l'ancienne release.");
        }

        internal async Task AssertTargetIsNewAsync()
        {
            Equal(Hash(NewBytes), await LauncherUpdateTransactionStore.ComputeSha256Async(
                    TargetPath,
                    CancellationToken.None),
                "La cible doit contenir exactement la nouvelle release.");
        }

        public void Dispose()
        {
            Launcher.Dispose();
            RecoveryLauncher.Dispose();
            LauncherUpdateTransactionStore.TryDeleteDirectory(Root);
        }

        private LauncherUpdateTransaction CreateTransaction()
        {
            Guid id = Guid.NewGuid();
            string workspace = Path.Combine(TransactionsRoot, id.ToString("N"));
            Directory.CreateDirectory(workspace);
            string candidate = Path.Combine(workspace, "candidate.exe");
            string helper = Path.Combine(workspace, "updater.exe");
            File.WriteAllBytes(candidate, NewBytes);
            File.WriteAllBytes(helper, OldBytes);
            string suffix = ".atlas-" + id.ToString("N");
            return new LauncherUpdateTransaction(
                LauncherUpdateTransaction.CurrentSchemaVersion,
                id,
                Environment.ProcessId,
                TargetPath,
                workspace,
                candidate,
                helper,
                TargetPath + suffix + ".new",
                TargetPath + suffix + ".backup",
                Path.Combine(workspace, "transaction.json"),
                Path.Combine(workspace, "helper-accepted.json"),
                Path.Combine(workspace, "started.json"),
                Path.Combine(workspace, "ready.json"),
                NewBytes.Length,
                Hash(OldBytes),
                Hash(NewBytes),
                LauncherUpdateTransactionPhase.Prepared,
                DateTimeOffset.UtcNow);
        }
    }

    private sealed class FakeParentWaiter(bool exits) : ILauncherUpdateParentWaiter
    {
        public Task<bool> WaitForExitAsync(
            int processId,
            string expectedExecutablePath,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(exits);
    }

    private sealed class ThrowingFaultInjector(LauncherUpdateFaultPoint point)
        : ILauncherUpdateFaultInjector
    {
        public void Hit(
            LauncherUpdateFaultPoint current,
            LauncherUpdateTransaction transaction)
        {
            if (current == point)
            {
                throw new LauncherUpdateSimulatedCrashException(point);
            }
        }
    }

    private sealed class FailingThenAtomicMover(int failuresBeforeSuccess)
        : ILauncherAtomicFileMover
    {
        private readonly WindowsLauncherAtomicFileMover _inner = new();

        internal int Attempts { get; private set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            Attempts++;
            if (Attempts <= failuresBeforeSuccess)
            {
                throw new IOException("simulated transient lock");
            }

            _inner.Replace(sourcePath, destinationPath);
        }
    }

    private sealed class AlwaysFailingAtomicMover : ILauncherAtomicFileMover
    {
        internal int Attempts { get; private set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            Attempts++;
            throw new UnauthorizedAccessException("simulated permission failure");
        }
    }

    private sealed class WindowsLockingAtomicMover(TimeSpan? releaseAfter)
        : ILauncherAtomicFileMover, IDisposable
    {
        private readonly WindowsLauncherAtomicFileMover _inner = new();
        private FileStream? _lock;
        private Task _automaticRelease = Task.CompletedTask;

        internal int Attempts { get; private set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            Attempts++;
            if (_lock is null && Attempts == 1)
            {
                _lock = new FileStream(
                    destinationPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
                if (releaseAfter is TimeSpan delay)
                {
                    _automaticRelease = Task.Run(async () =>
                    {
                        await Task.Delay(delay);
                        Release();
                    });
                }
            }

            _inner.Replace(sourcePath, destinationPath);
        }

        internal Task WaitForAutomaticReleaseAsync() => _automaticRelease;

        internal void Release()
        {
            Interlocked.Exchange(ref _lock, null)?.Dispose();
        }

        public void Dispose() => Release();
    }

    private sealed class FakeApplicationLauncher(
        LauncherUpdateTransactionStore store,
        FakeLaunchBehavior behavior) : ILauncherUpdateApplicationLauncher, IDisposable
    {
        private readonly List<FakeLaunchedProcess> _processes = [];
        private int _nextProcessId = 20_000;

        internal int UpdatedLaunchCalls { get; private set; }

        internal int RollbackLaunchCalls { get; private set; }

        internal FakeLaunchedProcess? LastProcess { get; private set; }

        public async Task<ILauncherUpdateLaunchedProcess> LaunchUpdatedAsync(
            LauncherUpdateTransaction transaction,
            TimeSpan startTimeout,
            TimeSpan pollInterval,
            CancellationToken cancellationToken)
        {
            UpdatedLaunchCalls++;
            if (behavior == FakeLaunchBehavior.Throw)
            {
                throw new InvalidOperationException("simulated launch failure");
            }

            int processId = Interlocked.Increment(ref _nextProcessId);
            LastProcess = new FakeLaunchedProcess(
                processId,
                behavior is FakeLaunchBehavior.ImmediateExit
                    or FakeLaunchBehavior.ReadyThenExit);
            _processes.Add(LastProcess);
            store.WriteStartedSignal(
                transaction,
                new LauncherUpdateProcessSignal(
                    transaction.TransactionId,
                    processId,
                    IsElevated: false,
                    DateTimeOffset.UtcNow));

            switch (behavior)
            {
                case FakeLaunchBehavior.Ready:
                case FakeLaunchBehavior.ReadyThenExit:
                    WriteReady(transaction, transaction.TransactionId, processId);
                    break;
                case FakeLaunchBehavior.DelayedReady:
                    await Task.Delay(35, cancellationToken);
                    WriteReady(transaction, transaction.TransactionId, processId);
                    break;
                case FakeLaunchBehavior.WrongTransactionReady:
                    string originalPath = transaction.ReadySignalPath;
                    LauncherUpdateTransaction wrong = transaction with
                    {
                        TransactionId = Guid.NewGuid(),
                        ReadySignalPath = Path.Combine(
                            transaction.WorkspacePath,
                            "wrong-ready.json")
                    };
                    store.WriteReadySignal(
                        wrong,
                        new LauncherUpdateProcessSignal(
                            wrong.TransactionId,
                            processId,
                            false,
                            DateTimeOffset.UtcNow));
                    File.Copy(wrong.ReadySignalPath, originalPath, overwrite: true);
                    break;
                case FakeLaunchBehavior.WrongProcessReady:
                    WriteReady(transaction, transaction.TransactionId, processId + 100);
                    break;
            }

            return LastProcess;
        }

        public Task LaunchRollbackAsync(
            LauncherUpdateTransaction transaction,
            CancellationToken cancellationToken)
        {
            RollbackLaunchCalls++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            foreach (FakeLaunchedProcess process in _processes)
            {
                process.Dispose();
            }
        }

        private void WriteReady(
            LauncherUpdateTransaction transaction,
            Guid transactionId,
            int processId)
        {
            store.WriteReadySignal(
                transaction,
                new LauncherUpdateProcessSignal(
                    transactionId,
                    processId,
                    IsElevated: false,
                    DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeLaunchedProcess(int processId, bool hasExited)
        : ILauncherUpdateLaunchedProcess
    {
        internal int KillCalls { get; private set; }

        public int ProcessId { get; } = processId;

        public bool HasExited { get; private set; } = hasExited;

        public void Kill()
        {
            KillCalls++;
            HasExited = true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingHelperLauncher : ILauncherUpdateHelperLauncher
    {
        internal int ApplyCalls { get; private set; }

        internal LauncherUpdateTransaction? LastTransaction { get; private set; }

        public Task LaunchApplyAsync(
            LauncherUpdateTransaction transaction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            LastTransaction = transaction;
            return Task.CompletedTask;
        }

        public Task LaunchRecoveryAsync(
            LauncherUpdateTransaction transaction,
            int requesterProcessId,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Recovery inattendue dans ce test.");
        }
    }
}
