using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using System.Windows.Threading;

internal static class LauncherOperationCoordinatorTests
{
    internal static async Task<int> RunAsync()
    {
        StartImmediatelyWithoutQueueAndIncreaseIds();
        KeepCompletionAndDisposalIdempotent();
        KeepStaleLeasesAndCallbacksIsolated();
        await DistinguishUserAndShutdownCancellationAsync();
        await ShutdownDuringSimulatedOperationAsync(
            LauncherOperationKind.Verify,
            canUserCancel: false);
        await ShutdownDuringSimulatedOperationAsync(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true);
        await LetVerificationUseTheGlobalLeaseAsync();
        RejectClientAddonAndAutoUpdateConcurrency();
        ValidatePlaySingleFlightAndCompatibility();
        await IgnoreObsoleteSnapshotsAndDetachAsync();
        await LetRuntimeOwnShutdownAsync();
        KeepPreviewFreeOfRuntimeOperations();
        Console.WriteLine("Launcher operation coordination OK (02D.0).");
        return 0;
    }

    private static void StartImmediatelyWithoutQueueAndIncreaseIds()
    {
        using LauncherOperationCoordinator coordinator = new();
        LauncherOperationStartResult first = coordinator.TryBegin(
            LauncherOperationKind.GameInstall,
            canUserCancel: true);
        True(first.IsStarted, "La première opération doit démarrer immédiatement.");

        LauncherOperationStartResult refused = coordinator.TryBegin(
            LauncherOperationKind.Addons,
            canUserCancel: true);
        Equal(LauncherOperationStartStatus.Busy, refused.Status, "Le second clic doit être refusé immédiatement.");
        True(refused.Lease is null, "Une opération refusée ne doit recevoir aucun bail.");
        Equal(LauncherOperationKind.GameInstall, coordinator.ActiveMaintenanceKind, "Le refus ne doit pas remplacer l'opération active.");

        long firstId = first.Lease!.OperationId;
        first.Lease.Complete();
        LauncherOperationStartResult second = coordinator.TryBegin(
            LauncherOperationKind.Addons,
            canUserCancel: true);
        True(second.IsStarted, "Une nouvelle opération doit démarrer après libération.");
        True(second.Lease!.OperationId > firstId, "OperationId doit être strictement croissant.");
        second.Lease.Complete();
    }

    private static void KeepCompletionAndDisposalIdempotent()
    {
        using LauncherOperationCoordinator coordinator = new();
        LauncherOperationLease lease = coordinator.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true).Lease!;

        lease.Complete();
        True(!lease.CancelFromUser(), "Une annulation après Complete doit être ignorée.");
        lease.Complete();
        lease.Dispose();
        lease.Dispose();
        True(coordinator.IsIdle, "Complete et Dispose répétés doivent laisser le coordinateur libre.");

        LauncherOperationStartResult next = coordinator.TryBegin(
            LauncherOperationKind.LauncherAutoUpdate,
            canUserCancel: true);
        True(next.IsStarted, "Le bail idempotent ne doit pas bloquer l'opération suivante.");
        next.Lease!.Complete();
    }

    private static void KeepStaleLeasesAndCallbacksIsolated()
    {
        using LauncherOperationCoordinator coordinator = new();
        LauncherOperationLease oldLease = coordinator.TryBegin(
            LauncherOperationKind.GameInstall,
            canUserCancel: true).Lease!;
        int oldCallbacks = 0;
        True(oldLease.TryInvoke(() => oldCallbacks++), "Le callback courant doit être accepté.");
        oldLease.Complete();

        LauncherOperationLease currentLease = coordinator.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true).Lease!;
        int currentCallbacks = 0;
        oldLease.Complete();
        oldLease.Dispose();
        True(!oldLease.TryInvoke(() => oldCallbacks++), "Un callback ancien doit être ignoré.");
        True(currentLease.TryInvoke(() => currentCallbacks++), "Le callback de l'opération courante doit rester accepté.");
        Equal(1, oldCallbacks, "L'ancien callback ne doit pas modifier l'état de la nouvelle opération.");
        Equal(1, currentCallbacks, "Le callback courant doit être publié une fois.");
        Equal(LauncherOperationKind.GameUpdate, coordinator.ActiveMaintenanceKind, "L'ancien bail ne doit pas libérer le nouveau.");
        currentLease.Complete();
        True(!currentLease.TryInvoke(() => currentCallbacks++), "Un callback après Complete doit être ignoré.");
    }

    private static async Task DistinguishUserAndShutdownCancellationAsync()
    {
        using (LauncherOperationCoordinator coordinator = new())
        {
            LauncherOperationLease cancellable = coordinator.TryBegin(
                LauncherOperationKind.Addons,
                canUserCancel: true).Lease!;
            int cancellationCallbacks = 0;
            using CancellationTokenRegistration registration = cancellable.CancellationToken.Register(
                () => cancellationCallbacks++);

            True(cancellable.CancelFromUser(), "La première annulation utilisateur doit être acceptée.");
            True(!cancellable.CancelFromUser(), "La double annulation utilisateur doit être idempotente.");
            Equal(1, cancellationCallbacks, "Le token utilisateur ne doit être annulé qu'une fois.");
            True(!coordinator.IsIdle, "L'annulation ne doit jamais libérer prématurément le bail.");
            Equal(
                LauncherOperationStartStatus.Busy,
                coordinator.TryBegin(LauncherOperationKind.GameInstall, true).Status,
                "Une opération annulée mais non terminée doit rester Busy.");
            cancellable.Complete();

            LauncherOperationStartResult restarted = coordinator.TryBegin(
                LauncherOperationKind.GameInstall,
                canUserCancel: true);
            True(restarted.IsStarted, "Une opération doit pouvoir redémarrer après fin confirmée.");
            restarted.Lease!.Complete();
        }

        using (LauncherOperationCoordinator coordinator = new())
        {
            LauncherOperationLease verification = coordinator.TryBegin(
                LauncherOperationKind.Verify,
                canUserCancel: false,
                clientIsPlayable: true).Lease!;
            True(!verification.CancelFromUser(), "Verify doit refuser l'annulation utilisateur.");
            True(!coordinator.CancelFromUser(), "L'interface ne doit pas contourner CanUserCancel.");
            True(!verification.CancellationToken.IsCancellationRequested, "Le refus utilisateur ne doit pas annuler le token.");

            True(coordinator.CancelForShutdown(), "La fermeture doit demander l'annulation de Verify.");
            True(verification.CancellationToken.IsCancellationRequested, "La fermeture doit annuler même CanUserCancel=false.");
            int callbackAfterShutdown = 0;
            True(
                !verification.TryInvoke(() => callbackAfterShutdown++),
                "Un callback reçu après fermeture doit être ignoré.");
            Equal(0, callbackAfterShutdown, "Le callback de fermeture ne doit modifier aucun état.");
            True(!coordinator.IsIdle, "La fermeture ne doit pas libérer le bail avant confirmation.");
            Equal(
                LauncherOperationStartStatus.ShuttingDown,
                coordinator.TryBegin(LauncherOperationKind.Addons, true).Status,
                "Toute nouvelle opération doit être refusée après fermeture.");
            True(!await coordinator.WaitForIdleAsync(TimeSpan.FromMilliseconds(20)), "WaitForIdle doit constater le bail encore actif.");
            verification.Complete();
            True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(1)), "WaitForIdle doit réussir après Complete.");
        }
    }

    private static async Task ShutdownDuringSimulatedOperationAsync(
        LauncherOperationKind kind,
        bool canUserCancel)
    {
        using LauncherOperationCoordinator coordinator = new();
        LauncherOperationLease lease = coordinator.TryBegin(
            kind,
            canUserCancel,
            clientIsPlayable: kind == LauncherOperationKind.Verify).Lease!;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFinally = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancellationObserved = false;
        Task operation = Task.Run(async () =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, lease.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
                await releaseFinally.Task;
            }
            finally
            {
                lease.Complete();
            }
        });

        await started.Task;
        coordinator.CancelForShutdown();
        await WaitUntilAsync(
            () => cancellationObserved,
            $"La fermeture n'a pas interrompu {kind}.");
        True(!coordinator.IsIdle, $"{kind} ne doit pas être libéré avant son finally.");
        releaseFinally.TrySetResult();
        await operation;
        True(await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(1)), $"{kind} doit confirmer sa fin.");
    }

    private static async Task LetVerificationUseTheGlobalLeaseAsync()
    {
        using VerificationEnvironment environment = new();
        using LauncherOperationCoordinator operations = new();
        BlockingVerificationService service = new();
        using GameRuntimeCoordinator verification = environment.CreateCoordinator(
            service,
            operations);

        LauncherOperationLease addons = operations.TryBegin(
            LauncherOperationKind.Addons,
            canUserCancel: true).Lease!;
        Equal(
            GameVerificationStartStatus.Busy,
            verification.TryStartVerification(),
            "GameVerificationCoordinator doit respecter le bail Addons global.");
        Equal(0, service.Calls, "Un refus global ne doit pas appeler le service de vérification.");
        addons.Complete();

        Equal(
            GameVerificationStartStatus.Started,
            verification.TryStartVerification(),
            "Verify doit acquérir le bail global après libération.");
        await service.Started.Task;
        Equal(LauncherOperationKind.Verify, operations.ActiveMaintenanceKind, "Verify ne doit pas posséder un second verrou indépendant.");
        Equal(
            LauncherOperationStartStatus.Busy,
            operations.TryBegin(LauncherOperationKind.GameUpdate, true).Status,
            "Update doit être refusé pendant la vérification globale.");

        operations.CancelForShutdown();
        await verification.WaitForIdleAsync();
        True(service.ObservedCancellation, "La fermeture globale doit interrompre la vérification V2.");
        True(await operations.WaitForIdleAsync(TimeSpan.FromSeconds(1)), "Verify doit libérer son bail dans finally.");
    }

    private static void RejectClientAddonAndAutoUpdateConcurrency()
    {
        LauncherOperationKind[] kinds =
        [
            LauncherOperationKind.GameRepair,
            LauncherOperationKind.GameInstall,
            LauncherOperationKind.GameUpdate,
            LauncherOperationKind.Addons,
            LauncherOperationKind.LauncherAutoUpdate,
            LauncherOperationKind.AvatarUpload,
            LauncherOperationKind.AvatarDelete
        ];

        foreach (LauncherOperationKind activeKind in kinds)
        {
            using LauncherOperationCoordinator coordinator = new();
            LauncherOperationLease active = coordinator.TryBegin(activeKind, true).Lease!;
            foreach (LauncherOperationKind contender in kinds.Where(kind => kind != activeKind))
            {
                Equal(
                    LauncherOperationStartStatus.Busy,
                    coordinator.TryBegin(contender, true).Status,
                    $"{contender} doit être refusé pendant {activeKind}.");
            }

            active.Complete();
        }
    }

    private static void ValidatePlaySingleFlightAndCompatibility()
    {
        using LauncherOperationCoordinator coordinator = new();
        Equal(
            LauncherOperationStartStatus.RejectedByCompatibility,
            coordinator.TryBeginPlay(clientIsPlayable: false).Status,
            "Play doit refuser un client non jouable.");

        LauncherOperationLease play = coordinator.TryBeginPlay(clientIsPlayable: true).Lease!;
        Equal(
            LauncherOperationStartStatus.Busy,
            coordinator.TryBeginPlay(clientIsPlayable: true).Status,
            "Deux Play simultanés doivent être refusés.");
        Equal(
            LauncherOperationStartStatus.RejectedByCompatibility,
            coordinator.TryBegin(LauncherOperationKind.GameUpdate, true).Status,
            "Update ne doit pas coexister avec Play.");

        LauncherOperationStartResult verifyStart = coordinator.TryBegin(
            LauncherOperationKind.Verify,
            canUserCancel: false,
            clientIsPlayable: true);
        True(verifyStart.IsStarted, "Play et Verify doivent pouvoir coexister pour un client jouable.");
        verifyStart.Lease!.Complete();
        play.Complete();

        LauncherOperationLease verify = coordinator.TryBegin(
            LauncherOperationKind.Verify,
            canUserCancel: false,
            clientIsPlayable: true).Lease!;
        LauncherOperationStartResult playDuringVerify = coordinator.TryBeginPlay(clientIsPlayable: true);
        True(playDuringVerify.IsStarted, "La compatibilité Play/Verify doit être symétrique.");
        Equal(
            LauncherOperationStartStatus.Busy,
            coordinator.TryBegin(LauncherOperationKind.Addons, true).Status,
            "Une opération mutante doit rester refusée pendant Verify.");
        playDuringVerify.Lease!.Complete();
        verify.Complete();
    }

    private static Task IgnoreObsoleteSnapshotsAndDetachAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    GameRuntimeSnapshot initial = Snapshot(
                        sequence: 10,
                        operationId: 5,
                        GameAction.Play,
                        GameUpdateKnowledge.Known);
                    SnapshotVerificationRuntime runtime = new(initial);
                    GameUiState state = new();
                    using GameStateAdapter adapter = new(state, runtime, dispatcher);
                    Equal(GamePreviewScenario.Ready, state.Scenario, "Le snapshot initial doit être appliqué.");

                    runtime.Publish(Snapshot(
                        sequence: 9,
                        operationId: 5,
                        GameAction.Install,
                        GameUpdateKnowledge.Unknown));
                    Equal(GamePreviewScenario.Ready, state.Scenario, "Une séquence obsolète doit être ignorée.");

                    runtime.Publish(Snapshot(
                        sequence: 11,
                        operationId: 4,
                        GameAction.Install,
                        GameUpdateKnowledge.Unknown));
                    Equal(GamePreviewScenario.Ready, state.Scenario, "Un ancien OperationId doit être ignoré.");

                    runtime.Publish(Snapshot(
                        sequence: 12,
                        operationId: 6,
                        GameAction.Update,
                        GameUpdateKnowledge.Known));
                    Equal(GamePreviewScenario.UpdateAvailable, state.Scenario, "Le snapshot courant doit être appliqué.");

                    adapter.Dispose();
                    runtime.Publish(Snapshot(
                        sequence: 13,
                        operationId: 7,
                        GameAction.Install,
                        GameUpdateKnowledge.Unknown));
                    Equal(GamePreviewScenario.UpdateAvailable, state.Scenario, "Aucun snapshot ne doit passer après désinscription.");
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "Atlas operation snapshot ordering"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static GameRuntimeSnapshot Snapshot(
        long sequence,
        long operationId,
        GameAction action,
        GameUpdateKnowledge knowledge)
    {
        return new GameRuntimeSnapshot(
            sequence,
            operationId,
            action,
            knowledge,
            GameVerificationPhase.Stable,
            IsVerifying: false,
            CanVerify: true,
            IsPlayable: action != GameAction.Install,
            InstallPath: @"C:\WotLK",
            InstalledVersion: "installed",
            AvailableVersion: "available",
            ProcessedFileCount: null,
            TotalFileCount: null,
            FailureCategory: null);
    }

    private static async Task LetRuntimeOwnShutdownAsync()
    {
        using TemporaryClient client = new();
        FakeLauncherAuthService authentication = new();
        LauncherRuntime runtime = new(new LauncherRuntimeDependencies
        {
            LoadSettings = () => client.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test"
        });

        LauncherOperationLease operation = runtime.Operations.TryBegin(
            LauncherOperationKind.GameUpdate,
            canUserCancel: true).Lease!;
        runtime.BeginShutdown();
        True(operation.CancellationToken.IsCancellationRequested, "LauncherRuntime doit imposer l'annulation de fermeture.");
        operation.Complete();
        True(await runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(1)), "LauncherRuntime doit suivre la fin des baux.");
        runtime.Dispose();
        runtime.Dispose();
        Equal(1, authentication.DisposeCalls, "La fermeture répétée doit rester idempotente.");
    }

    private static void KeepPreviewFreeOfRuntimeOperations()
    {
        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Ready"]),
            "Le preview doit conserver sa branche de démarrage isolée.");
        foreach (GamePreviewScenario scenario in Enum.GetValues<GamePreviewScenario>())
        {
            GameUiState state = LauncherV2PreviewData.CreateGame(scenario);
            True(!state.VerifyCommand.CanExecute(null), $"Le preview {scenario} ne doit acquérir aucun bail réel.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        True(condition(), message);
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

internal sealed class SnapshotVerificationRuntime(GameRuntimeSnapshot snapshot)
    : IGameVerificationRuntime
{
    public event EventHandler? AvailabilityChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<GameRuntimeSnapshotEventArgs>? SnapshotChanged;

    public bool CanVerify => true;

    public GameRuntimeSnapshot CurrentSnapshot { get; private set; } = snapshot;

    public GameVerificationStartStatus TryStartVerification()
    {
        return GameVerificationStartStatus.Busy;
    }

    public GameVerificationStartStatus TryStartFullRepair()
    {
        return GameVerificationStartStatus.Busy;
    }

    internal void Publish(GameRuntimeSnapshot next)
    {
        CurrentSnapshot = next;
        SnapshotChanged?.Invoke(this, new GameRuntimeSnapshotEventArgs(next));
    }
}
