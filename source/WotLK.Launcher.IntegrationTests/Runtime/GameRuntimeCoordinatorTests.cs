using System.IO;
using System.Net;
using System.Net.Http;
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

internal static class GameRuntimeCoordinatorTests
{
    internal static async Task<int> RunAsync()
    {
        await ProjectInitialInstallUpdateAndPlayStatesAsync();
        await EnforceAuthenticationAndImmediateBusyRefusalAsync();
        await PublishCoherentDownloadAndSuccessSnapshotsAsync();
        await CancelDuringDownloadAndApplicationIdempotentlyAsync();
        await DisableUserCancellationDuringFinalizationAsync();
        await CategorizeFailuresAndRetryWithNewOperationAsync();
        await IgnoreStaleCallbacksAndCoalesceProgressAsync();
        await CancelEveryPhaseForShutdownWithoutLateSnapshotsAsync();
        KeepPreviewCommandsIsolated();
        await VerifyWpfPrimaryActionAndAtomicStateAsync();
        Console.WriteLine("V2 install/update runtime coordination OK (02D.2).");
        return 0;
    }

    private static async Task ProjectInitialInstallUpdateAndPlayStatesAsync()
    {
        using RuntimeGameEnvironment install = new(playable: false, authenticated: true);
        GameRuntimeSnapshot installSnapshot = install.Coordinator.CurrentSnapshot;
        Equal(GameAction.Install, installSnapshot.Action, "L'état local absent doit rester Install.");
        True(installSnapshot.CanPrimaryAction, "Installer doit être disponible avec une session valide.");
        GameViewState installView = GameStateAdapter.Project(installSnapshot);
        Equal("Installer", installView.PrimaryActionLabel, "Le libellé Install est incorrect.");
        True(installView.IsPrimaryActionEnabled, "Installer doit être activé.");

        using RuntimeGameEnvironment play = new(playable: true, authenticated: true);
        GameViewState playView = GameStateAdapter.Project(play.Coordinator.CurrentSnapshot);
        Equal(GameAction.Play, play.Coordinator.CurrentSnapshot.Action, "Le client local jouable doit rester Play.");
        Equal("Jouer", playView.PrimaryActionLabel, "Le libellé Play est incorrect.");
        True(!playView.IsPrimaryActionEnabled, "Jouer doit rester désactivé avant 02F.3.");

        play.Verification.Result = new GameClientVerificationResult(
            GameVerificationOutcome.UpdateAvailable,
            GameAction.Update,
            GameUpdateKnowledge.Known,
            "remote-v2",
            2);
        Equal(GameVerificationStartStatus.Started, play.Coordinator.TryStartVerification(), "La vérification témoin doit démarrer.");
        await play.Coordinator.WaitForIdleAsync();
        GameViewState updateView = GameStateAdapter.Project(play.Coordinator.CurrentSnapshot);
        Equal(GameAction.Update, play.Coordinator.CurrentSnapshot.Action, "Le résultat distant doit devenir Update.");
        Equal("Mettre à jour", updateView.PrimaryActionLabel, "Le libellé Update est incorrect.");
        True(updateView.IsPrimaryActionEnabled, "Mettre à jour doit être activé avec une session valide.");

        LauncherOperationKind? observedKind = null;
        play.Maintenance.Handler = (_, lease, _) =>
        {
            observedKind = lease.Kind;
            play.LocalState = Local(play.Root, playable: true, version: "remote-v2");
            return Task.FromResult(Result(lease, "remote-v2"));
        };
        Equal(GamePrimaryActionStatus.Started, play.Coordinator.TryExecutePrimaryAction(), "Mettre à jour doit démarrer.");
        await play.Coordinator.WaitForIdleAsync();
        Equal(LauncherOperationKind.GameUpdate, observedKind, "Update doit acquérir exclusivement un bail GameUpdate.");
    }

    private static async Task EnforceAuthenticationAndImmediateBusyRefusalAsync()
    {
        using RuntimeGameEnvironment noSession = new(playable: false, authenticated: false);
        Equal(
            GamePrimaryActionStatus.Unauthenticated,
            noSession.Coordinator.TryExecutePrimaryAction(),
            "Une session absente doit refuser immédiatement l'installation.");
        Equal(0, noSession.Maintenance.Calls, "Aucun manifeste ne doit être demandé sans session.");
        Equal("Connexion requise", noSession.Coordinator.CurrentSnapshot.PrimaryActionUnavailableReason, "La raison temporaire doit être explicite.");

        noSession.Authenticated = true;
        noSession.Coordinator.RefreshAuthenticationAvailability();
        True(noSession.Coordinator.CanExecutePrimaryAction, "Une session restaurée doit activer Installer.");
        noSession.Authenticated = false;
        noSession.Coordinator.RefreshAuthenticationAvailability();
        True(!noSession.Coordinator.CanExecutePrimaryAction, "Une session expirée doit désactiver Installer.");

        using RuntimeGameEnvironment busy = new(playable: false, authenticated: true);
        LauncherOperationStartResult addons = busy.Operations.TryBegin(
            LauncherOperationKind.Addons,
            canUserCancel: true);
        True(addons.IsStarted, "Le bail Addons témoin doit démarrer.");
        Equal(GamePrimaryActionStatus.Busy, busy.Coordinator.TryExecutePrimaryAction(), "Busy doit être refusé sans file d'attente.");
        Equal(0, busy.Maintenance.Calls, "Une commande Busy ne doit jamais être rejouée plus tard.");
        addons.Lease!.Complete();
        await busy.Operations.WaitForIdleAsync(TimeSpan.FromSeconds(1));
        Equal(0, busy.Maintenance.Calls, "La libération du bail ne doit pas lancer la commande refusée.");
    }

    private static async Task PublishCoherentDownloadAndSuccessSnapshotsAsync()
    {
        using RuntimeGameEnvironment environment = new(playable: false, authenticated: true);
        List<GameRuntimeSnapshot> snapshots = [environment.Coordinator.CurrentSnapshot];
        environment.Coordinator.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);
        environment.Maintenance.Handler = (request, lease, progress) =>
        {
            progress?.Invoke(Progress(lease, GameClientMaintenancePhase.ManifestLoaded, availableVersion: "client-v2"));
            progress?.Invoke(Progress(lease, GameClientMaintenancePhase.DownloadingStarted, totalBytes: 100));
            progress?.Invoke(Progress(
                lease,
                GameClientMaintenancePhase.Downloading,
                currentFile: "Data/client.bin",
                downloadedBytes: 50,
                totalBytes: 100,
                bytesPerSecond: 25,
                remaining: TimeSpan.FromSeconds(2)));
            progress?.Invoke(Progress(lease, GameClientMaintenancePhase.CacheSaved, availableVersion: "client-v2"));
            progress?.Invoke(Progress(lease, GameClientMaintenancePhase.Completed, availableVersion: "client-v2"));
            environment.LocalState = Local(environment.Root, playable: true, version: "client-v2");
            return Task.FromResult(Result(lease, "client-v2"));
        };

        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "Installer doit démarrer.");
        await environment.Coordinator.WaitForIdleAsync();

        True(snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence), "Sequence doit être strictement croissante.");
        GameRuntimeSnapshot transfer = snapshots.Last(item => item.MaintenancePhase == GameClientMaintenancePhase.Downloading);
        Equal(GameAction.Install, transfer.Action, "L'action fonctionnelle doit rester Install pendant le téléchargement.");
        Equal(50L, transfer.DownloadedBytes, "Les octets réels doivent être publiés.");
        Equal(GameViewMode.Downloading, transfer.ViewMode, "Le mode doit être dérivé du téléchargement.");
        Equal("Annuler", GameStateAdapter.Project(transfer).PrimaryActionLabel, "Le téléchargement doit proposer Annuler.");
        True(!transfer.CanVerify, "Vérifier doit être indisponible pendant une maintenance incompatible.");
        True(snapshots.Any(item => item.IsFinalizing && !item.CanUserCancel), "La finalisation doit devenir non annulable.");

        GameRuntimeSnapshot terminal = environment.Coordinator.CurrentSnapshot;
        Equal(GameAction.Play, terminal.Action, "Un succès doit publier Play.");
        Equal(GameUpdateKnowledge.Known, terminal.UpdateKnowledge, "Le manifeste appliqué doit confirmer À jour.");
        Equal("client-v2", terminal.InstalledVersion, "La version locale doit être relue.");
        Equal("client-v2", terminal.AvailableVersion, "La version disponible doit rester celle du manifeste appliqué.");
        GameViewState terminalView = GameStateAdapter.Project(terminal);
        Equal("À jour", terminalView.InstallBadgeText, "Le badge final doit être À jour.");
        Equal("Jouer", terminalView.PrimaryActionLabel, "Le succès doit afficher Jouer.");
        True(!terminalView.IsPrimaryActionEnabled, "Jouer reste désactivé jusqu'à 02F.3.");
        Equal(1, environment.Maintenance.Calls, "Un seul pipeline doit être exécuté.");
    }

    private static async Task CancelDuringDownloadAndApplicationIdempotentlyAsync()
    {
        await CancelAtPhaseAsync(GameClientMaintenancePhase.Downloading, GamePreviewScenario.Downloading);
        await CancelAtPhaseAsync(GameClientMaintenancePhase.Cleaning, GamePreviewScenario.Installing);
    }

    private static async Task CancelAtPhaseAsync(
        GameClientMaintenancePhase phase,
        GamePreviewScenario expectedScenario)
    {
        using RuntimeGameEnvironment environment = new(playable: false, authenticated: true);
        TaskCompletionSource started = NewSignal();
        TaskCompletionSource release = NewSignal();
        environment.Maintenance.Handler = async (_, lease, progress) =>
        {
            progress?.Invoke(Progress(
                lease,
                phase,
                downloadedBytes: phase == GameClientMaintenancePhase.Downloading ? 10 : null,
                totalBytes: phase == GameClientMaintenancePhase.Downloading ? 100 : null,
                processedFileCount: phase == GameClientMaintenancePhase.Cleaning ? 1 : null,
                totalFileCount: phase == GameClientMaintenancePhase.Cleaning ? 3 : null));
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, lease.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                await release.Task;
                throw;
            }

            throw new InvalidOperationException("Unreachable");
        };

        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "La maintenance témoin doit démarrer.");
        await started.Task;
        Equal(expectedScenario, GameStateAdapter.Project(environment.Coordinator.CurrentSnapshot).Scenario, "Le mapping de phase est incorrect.");
        Equal(GamePrimaryActionStatus.CancelRequested, environment.Coordinator.TryExecutePrimaryAction(), "Annuler doit déléguer au bail global.");
        Equal(GamePrimaryActionStatus.CancelRequested, environment.Coordinator.TryExecutePrimaryAction(), "Une seconde annulation doit rester idempotente.");
        release.TrySetResult();
        await environment.Coordinator.WaitForIdleAsync();

        GameRuntimeSnapshot terminal = environment.Coordinator.CurrentSnapshot;
        Equal(GameAction.Install, terminal.Action, "Une installation annulée doit revenir à Install.");
        True(terminal.ErrorCategory is null, "Une annulation utilisateur ne doit pas devenir une erreur rouge.");
        True(!terminal.IsMaintenanceActive, "La progression doit disparaître après annulation.");
        Equal(1, environment.Maintenance.Calls, "Un double clic Annuler ne doit jamais démarrer un second pipeline.");
    }

    private static async Task DisableUserCancellationDuringFinalizationAsync()
    {
        using RuntimeGameEnvironment environment = new(playable: false, authenticated: true);
        TaskCompletionSource finalizing = NewSignal();
        TaskCompletionSource release = NewSignal();
        environment.Maintenance.Handler = async (_, lease, progress) =>
        {
            progress?.Invoke(Progress(lease, GameClientMaintenancePhase.CacheSaved, availableVersion: "final-v1"));
            finalizing.TrySetResult();
            await release.Task;
            environment.LocalState = Local(environment.Root, playable: true, version: "final-v1");
            return Result(lease, "final-v1");
        };

        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "La finalisation témoin doit démarrer.");
        await finalizing.Task;
        GameRuntimeSnapshot snapshot = environment.Coordinator.CurrentSnapshot;
        True(snapshot.IsFinalizing, "CacheSaved doit être une finalisation.");
        True(!snapshot.CanUserCancel, "La finalisation ne doit plus être annulable par l'utilisateur.");
        Equal("Finalisation…", GameStateAdapter.Project(snapshot).PrimaryActionLabel, "Le bouton final doit être neutre.");
        Equal(GamePrimaryActionStatus.Busy, environment.Coordinator.TryExecutePrimaryAction(), "Le clic final doit être refusé.");
        True(!environment.Operations.CancelFromUser(), "Le coordinateur global doit aussi refuser l'annulation utilisateur.");
        release.TrySetResult();
        await environment.Coordinator.WaitForIdleAsync();
    }

    private static async Task CategorizeFailuresAndRetryWithNewOperationAsync()
    {
        (Exception Error, GameRuntimeErrorCategory Category)[] cases =
        [
            (new HttpRequestException("https://secret.example/token"), GameRuntimeErrorCategory.Network),
            (new TaskCanceledException("network timeout"), GameRuntimeErrorCategory.Network),
            (new HttpRequestException("expired", null, HttpStatusCode.Unauthorized), GameRuntimeErrorCategory.Unauthorized),
            (new UnauthorizedAccessException(@"C:\secret\client"), GameRuntimeErrorCategory.Permission),
            (new InvalidDataException("invalid manifest payload"), GameRuntimeErrorCategory.Integrity),
            (new IOException("disk full"), GameRuntimeErrorCategory.Disk),
            (new IOException("Ferme le jeu: fichier verrouillé"), GameRuntimeErrorCategory.LockedFile),
            (new InvalidOperationException("Hash invalide pour Data/client.bin"), GameRuntimeErrorCategory.Integrity),
            (new InvalidOperationException("Taille invalide pour Data/client.bin"), GameRuntimeErrorCategory.Integrity),
            (new InvalidOperationException("Plateforme indisponible"), GameRuntimeErrorCategory.Platform),
            (new Exception("secret-token"), GameRuntimeErrorCategory.Unknown)
        ];

        foreach ((Exception error, GameRuntimeErrorCategory category) in cases)
        {
            using RuntimeGameEnvironment environment = new(playable: false, authenticated: true);
            List<string> logs = [];
            environment.LogSink = logs.Add;
            environment.Maintenance.Handler = (_, _, _) => Task.FromException<GameClientMaintenanceResult>(error);
            Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), $"Le cas {category} doit démarrer.");
            await environment.Coordinator.WaitForIdleAsync();
            GameRuntimeSnapshot snapshot = environment.Coordinator.CurrentSnapshot;
            Equal(category, snapshot.ErrorCategory, $"Catégorie incorrecte pour {category}.");
            True(snapshot.RetryAction == GameAction.Install && snapshot.CanPrimaryAction, "L'erreur doit conserver Retry Install.");
            GameViewState view = GameStateAdapter.Project(snapshot);
            Equal("Réessayer", view.PrimaryActionLabel, "Une erreur doit proposer Réessayer.");
            True(!view.ErrorSummary.Contains("http", StringComparison.OrdinalIgnoreCase)
                && !view.ErrorSummary.Contains(@"C:\", StringComparison.OrdinalIgnoreCase)
                && !view.ErrorSummary.Contains("secret", StringComparison.OrdinalIgnoreCase),
                "Le résumé visible ne doit exposer aucun détail brut.");
            True(logs.All(line => !line.Contains("secret", StringComparison.OrdinalIgnoreCase)), "Le journal structuré ne doit exposer aucun secret.");
            True(environment.Authenticated, "Une panne technique ne doit jamais supprimer la session locale.");
        }

        using RuntimeGameEnvironment retry = new(playable: false, authenticated: true);
        int attempt = 0;
        retry.Maintenance.Handler = (_, lease, _) =>
        {
            attempt++;
            if (attempt == 1)
            {
                return Task.FromException<GameClientMaintenanceResult>(new IOException("disk failure"));
            }

            retry.LocalState = Local(retry.Root, playable: true, version: "retry-v2");
            return Task.FromResult(Result(lease, "retry-v2"));
        };
        Equal(GamePrimaryActionStatus.Started, retry.Coordinator.TryExecutePrimaryAction(), "La première tentative doit démarrer.");
        await retry.Coordinator.WaitForIdleAsync();
        long firstOperation = retry.Coordinator.CurrentSnapshot.OperationId!.Value;
        Equal(GamePrimaryActionStatus.Started, retry.Coordinator.TryExecutePrimaryAction(), "Réessayer doit démarrer une nouvelle tentative.");
        await retry.Coordinator.WaitForIdleAsync();
        long secondOperation = retry.Coordinator.CurrentSnapshot.OperationId!.Value;
        True(secondOperation > firstOperation, "Réessayer doit obtenir un nouvel OperationId.");
        Equal(2, retry.Maintenance.Calls, "Réessayer doit appeler une fois le pipeline partagé.");
    }

    private static async Task IgnoreStaleCallbacksAndCoalesceProgressAsync()
    {
        ManualTimeProvider clock = new();
        using RuntimeGameEnvironment environment = new(
            playable: false,
            authenticated: true,
            timeProvider: clock);
        Action<GameClientMaintenanceProgress>? staleCallback = null;
        long staleOperation = 0;
        int attempt = 0;
        TaskCompletionSource secondStarted = NewSignal();
        TaskCompletionSource secondRelease = NewSignal();
        environment.Maintenance.Handler = async (_, lease, progress) =>
        {
            attempt++;
            if (attempt == 1)
            {
                staleCallback = progress;
                staleOperation = lease.OperationId;
                throw new IOException("first failure");
            }

            progress?.Invoke(Progress(lease, GameClientMaintenancePhase.DownloadingStarted, totalBytes: 4));
            for (int value = 1; value <= 4; value++)
            {
                progress?.Invoke(Progress(
                    lease,
                    GameClientMaintenancePhase.Downloading,
                    downloadedBytes: value,
                    totalBytes: 4));
            }
            secondStarted.TrySetResult();
            await secondRelease.Task;
            environment.LocalState = Local(environment.Root, playable: true, version: "coalesced-v1");
            return Result(lease, "coalesced-v1");
        };
        List<long> publishedBytes = [];
        environment.Coordinator.SnapshotChanged += (_, args) =>
        {
            if (args.Snapshot.MaintenancePhase == GameClientMaintenancePhase.Downloading
                && args.Snapshot.DownloadedBytes is long value)
            {
                publishedBytes.Add(value);
            }
        };

        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "La tentative obsolète doit démarrer.");
        await environment.Coordinator.WaitForIdleAsync();
        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "Le retry témoin doit démarrer.");
        await secondStarted.Task;
        GameRuntimeSnapshot beforeStale = environment.Coordinator.CurrentSnapshot;
        staleCallback?.Invoke(new GameClientMaintenanceProgress(
            staleOperation,
            GameClientMaintenancePhase.Downloading,
            DownloadedBytes: 999,
            TotalBytes: 1000));
        Equal(beforeStale, environment.Coordinator.CurrentSnapshot, "Un callback ancien doit être ignoré intégralement.");
        SequenceEqual([1L, 4L], publishedBytes, "La progression ordinaire doit être coalescée, sans retarder 100 %.");
        secondRelease.TrySetResult();
        await environment.Coordinator.WaitForIdleAsync();
    }

    private static async Task CancelEveryPhaseForShutdownWithoutLateSnapshotsAsync()
    {
        foreach (GameClientMaintenancePhase phase in new[]
        {
            GameClientMaintenancePhase.Downloading,
            GameClientMaintenancePhase.Cleaning,
            GameClientMaintenancePhase.CacheSaved
        })
        {
            using RuntimeGameEnvironment environment = new(playable: false, authenticated: true);
            TaskCompletionSource started = NewSignal();
            environment.Maintenance.Handler = async (_, lease, progress) =>
            {
                progress?.Invoke(Progress(lease, phase));
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, lease.CancellationToken);
                throw new InvalidOperationException("Unreachable");
            };
            List<GameRuntimeSnapshot> snapshots = [];
            environment.Coordinator.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);
            Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), $"La phase {phase} doit démarrer.");
            await started.Task;
            int beforeShutdown = snapshots.Count;
            environment.Coordinator.BeginShutdown();
            await environment.Coordinator.WaitForIdleAsync();
            Equal(beforeShutdown, snapshots.Count, $"Aucun terminal ne doit être publié après fermeture en phase {phase}.");
            Equal(GamePrimaryActionStatus.ShuttingDown, environment.Coordinator.TryExecutePrimaryAction(), "Toute nouvelle commande doit être refusée après fermeture.");
        }
    }

    private static void KeepPreviewCommandsIsolated()
    {
        foreach (GamePreviewScenario scenario in new[]
        {
            GamePreviewScenario.NotInstalled,
            GamePreviewScenario.UpdateAvailable,
            GamePreviewScenario.Downloading,
            GamePreviewScenario.Installing,
            GamePreviewScenario.Error,
            GamePreviewScenario.Ready
        })
        {
            GameUiState state = LauncherV2PreviewData.CreateGame(scenario);
            True(state.PrimaryActionCommand.CanExecute(null), $"La commande preview {scenario} doit rester fictive et locale.");
            state.PrimaryActionCommand.Execute(null);
        }

        Equal(
            LauncherStartupMode.UiV2Preview,
            App.ResolveStartupMode(["--ui-v2", "--preview-state=Downloading"]),
            "Le preview doit rester dans sa branche sans runtime réel.");
    }

    private static Task VerifyWpfPrimaryActionAndAtomicStateAsync()
    {
        TaskCompletionSource completion = NewSignal();
        Thread thread = new(() => RunWpfHarness(completion))
        {
            IsBackground = true,
            Name = "Atlas V2 maintenance WPF bindings"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunWpfHarness(TaskCompletionSource completion)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(async () =>
        {
            Application? application = null;
            Window? host = null;
            PrimaryActionCommand? primaryCommand = null;
            GameStateAdapter? adapter = null;
            try
            {
                application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                LoadV2Resources(application);
                using RuntimeGameEnvironment environment = new(playable: false, authenticated: true);
                int attempt = 0;
                TaskCompletionSource transferStarted = NewSignal();
                Action<GameClientMaintenanceProgress>? oldProgress = null;
                environment.Maintenance.Handler = async (_, lease, progress) =>
                {
                    attempt++;
                    if (attempt == 1)
                    {
                        throw new HttpRequestException("https://private.example/token");
                    }

                    oldProgress = progress;
                    progress?.Invoke(Progress(lease, GameClientMaintenancePhase.DownloadingStarted, totalBytes: 100));
                    progress?.Invoke(Progress(
                        lease,
                        GameClientMaintenancePhase.Downloading,
                        currentFile: "Data/client.bin",
                        downloadedBytes: 50,
                        totalBytes: 100,
                        bytesPerSecond: 25,
                        remaining: TimeSpan.FromSeconds(2)));
                    transferStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, lease.CancellationToken);
                    throw new InvalidOperationException("Unreachable");
                };

                GameUiState state = LauncherV2RuntimePresentation.CreateGame(environment.LocalState);
                state.AttachLocalCommands(PreviewCommand.Instance, PreviewCommand.Instance);
                primaryCommand = new PrimaryActionCommand(environment.Coordinator);
                state.AttachPrimaryActionCommand(primaryCommand.Command);
                adapter = new GameStateAdapter(state, environment.Coordinator, dispatcher);
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

                int groupedNotifications = 0;
                bool partialStateObserved = false;
                state.PropertyChanged += (_, args) =>
                {
                    if (string.IsNullOrEmpty(args.PropertyName))
                    {
                        groupedNotifications++;
                        partialStateObserved |= state.Scenario == GamePreviewScenario.Downloading
                            && state.PrimaryActionLabel != "Annuler";
                    }
                };

                Button installer = view.FindName("PrimaryActionButton") as Button
                    ?? throw new InvalidOperationException("PrimaryActionButton introuvable.");
                True(installer.IsEnabled && ReferenceEquals(installer.Command, state.PrimaryActionCommand), "Installer WPF doit utiliser PrimaryActionCommand.");
                Equal("Installer", AutomationProperties.GetName(installer), "L'action principale doit annoncer Installer.");
                state.PrimaryActionCommand.Execute(null);
                await environment.Coordinator.WaitForIdleAsync();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                view.UpdateLayout();
                Equal(GamePreviewScenario.Error, state.Scenario, "L'erreur doit être appliquée atomiquement.");
                Equal("Réessayer", state.PrimaryActionLabel, "L'erreur doit afficher Réessayer.");
                True(state.IsRetryEnabled && state.PrimaryActionCommand.CanExecute(null), "Réessayer doit être exécutable.");
                True(installer.IsEnabled, "L'action principale Réessayer doit être active.");
                Equal("Réessayer", AutomationProperties.GetName(installer), "L'action principale doit annoncer Réessayer.");
                True(!FindButtons(view, "Ouvrir le diagnostic").Any(),
                    "La page Jeu immersive ne doit plus dupliquer Diagnostic après une erreur.");

                state.PrimaryActionCommand.Execute(null);
                await transferStarted.Task;
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                view.UpdateLayout();
                Equal(GamePreviewScenario.Downloading, state.Scenario, "Le retry doit afficher le téléchargement.");
                Equal("Annuler", state.PrimaryActionLabel, "Le bouton doit devenir Annuler.");
                Equal(50d, state.Progress, "La progression déterminée doit être 50 %.");
                True(state.ProgressPrimaryDetail.Contains("100", StringComparison.Ordinal), "La taille totale doit être affichée.");
                True(state.ProgressSecondaryDetail.Contains("/s", StringComparison.Ordinal)
                    && state.ProgressSecondaryDetail.Contains("restantes", StringComparison.Ordinal),
                    "Vitesse et ETA doivent être visibles.");

                state.PrimaryActionCommand.Execute(null);
                await environment.Coordinator.WaitForIdleAsync();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal(GamePreviewScenario.NotInstalled, state.Scenario, "L'annulation doit retirer la progression.");
                True(!partialStateObserved && groupedNotifications >= 4, "Chaque snapshot doit être appliqué par une seule notification cohérente.");

                adapter.Dispose();
                adapter = null;
                primaryCommand.Dispose();
                primaryCommand = null;
                int notificationsBeforeStale = groupedNotifications;
                oldProgress?.Invoke(new GameClientMaintenanceProgress(
                    1,
                    GameClientMaintenancePhase.Downloading,
                    DownloadedBytes: 99,
                    TotalBytes: 100));
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
                Equal(notificationsBeforeStale, groupedNotifications, "Aucune notification ne doit atteindre WPF après désinscription.");
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                adapter?.Dispose();
                primaryCommand?.Dispose();
                host?.Close();
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
    }

    private static GameClientMaintenanceProgress Progress(
        LauncherOperationLease lease,
        GameClientMaintenancePhase phase,
        string? availableVersion = null,
        string? currentFile = null,
        int? processedFileCount = null,
        int? totalFileCount = null,
        long? downloadedBytes = null,
        long? totalBytes = null,
        double? bytesPerSecond = null,
        TimeSpan? remaining = null)
    {
        return new GameClientMaintenanceProgress(
            lease.OperationId,
            phase,
            availableVersion,
            currentFile,
            processedFileCount,
            totalFileCount,
            DownloadedBytes: downloadedBytes,
            TotalBytes: totalBytes,
            BytesPerSecond: bytesPerSecond,
            Remaining: remaining);
    }

    private static GameClientMaintenanceResult Result(
        LauncherOperationLease lease,
        string version)
    {
        return new GameClientMaintenanceResult(
            lease.OperationId,
            GameClientMaintenanceOutcome.Downloaded,
            version,
            DownloadedFileCount: 1,
            DeletedFileCount: 0,
            ConfigPath: null,
            UninstallerPath: null);
    }

    private static GameClientLocalState Local(string root, bool playable, string? version)
    {
        return new GameClientLocalState(
            root,
            "frFR",
            playable,
            version,
            GameUpdateKnowledge.Unknown);
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private static List<Button> FindButtons(DependencyObject root, string automationName)
    {
        return FindVisualChildren<Button>(root)
            .Where(button => string.Equals(
                AutomationProperties.GetName(button),
                automationName,
                StringComparison.Ordinal))
            .ToList();
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

    private static void SequenceEqual<T>(
        IReadOnlyList<T> expected,
        IReadOnlyList<T> actual,
        string message)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"{message} Attendu=[{string.Join(", ", expected)}]; actuel=[{string.Join(", ", actual)}].");
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

internal sealed class RuntimeGameEnvironment : IDisposable
{
    private readonly LauncherOperationCoordinator _operations = new();
    private Action<string> _logSink = _ => { };

    internal RuntimeGameEnvironment(
        bool playable,
        bool authenticated,
        TimeProvider? timeProvider = null)
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "AtlasRuntime02D2",
            Guid.NewGuid().ToString("N"));
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
        Authenticated = authenticated;
        Verification = new RuntimeVerificationStub();
        Maintenance = new RuntimeMaintenanceStub();
        Coordinator = new GameRuntimeCoordinator(
            Verification,
            _operations,
            Settings,
            LocalState,
            () => Authenticated,
            message => _logSink(message),
            _ => LocalState.IsPlayable,
            timeProvider ?? new ManualTimeProvider(),
            Maintenance,
            () => LocalState);
        Coordinator.RefreshAuthenticationAvailability();
    }

    internal string Root { get; }

    internal LauncherSettings Settings { get; }

    internal bool Authenticated { get; set; }

    internal GameClientLocalState LocalState { get; set; }

    internal RuntimeVerificationStub Verification { get; }

    internal RuntimeMaintenanceStub Maintenance { get; }

    internal LauncherOperationCoordinator Operations => _operations;

    internal GameRuntimeCoordinator Coordinator { get; }

    internal Action<string> LogSink
    {
        set => _logSink = value ?? (_ => { });
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        _operations.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class RuntimeVerificationStub : IGameClientVerificationService
{
    internal GameClientVerificationResult Result { get; set; } = new(
        GameVerificationOutcome.UpToDate,
        GameAction.Play,
        GameUpdateKnowledge.Known,
        "remote-v1",
        0);

    public Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result);
    }
}

internal sealed class RuntimeMaintenanceStub : IGameClientMaintenanceService
{
    internal Func<
        GameClientMaintenanceRequest,
        LauncherOperationLease,
        Action<GameClientMaintenanceProgress>?,
        Task<GameClientMaintenanceResult>> Handler { get; set; } =
        (_, lease, _) => Task.FromResult(new GameClientMaintenanceResult(
            lease.OperationId,
            GameClientMaintenanceOutcome.AlreadyCurrent,
            "remote-v1",
            0,
            0,
            null,
            null));

    internal Func<
        GameClientMaintenanceRequest,
        LauncherOperationLease,
        Action<GameClientMaintenanceProgress>?,
        Task<GameClientMaintenanceResult>>? RepairHandler { get; set; }

    internal int Calls { get; private set; }

    internal int RepairCalls { get; private set; }

    internal List<long> OperationIds { get; } = [];

    public Task<GameClientMaintenanceResult> InstallOrUpdateAsync(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        Calls++;
        OperationIds.Add(operation.OperationId);
        return Handler(request, operation, reportProgress);
    }

    public Task<GameClientMaintenanceResult> VerifyAndRepairAsync(
        GameClientMaintenanceRequest request,
        LauncherOperationLease operation,
        Action<GameClientMaintenanceProgress>? reportProgress)
    {
        RepairCalls++;
        OperationIds.Add(operation.OperationId);
        return (RepairHandler ?? Handler)(request, operation, reportProgress);
    }
}
