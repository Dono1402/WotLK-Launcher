using WotLK.Launcher;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class GameServerAvailabilityTests
{
    internal static async Task RunAsync()
    {
        KeepOfflineKnowledgeAndRejectRepeatedCommands();
        await RecheckEveryLaunchBoundaryAsync();
        await InvalidateAnAwaitingTicketAcrossRecoveryAsync();
        await ReleasePendingAuthenticationWithoutStartingAsync();
        await PreserveAutomaticVerificationAsync();
        await PreserveMaintenanceAndRunningGameAsync();
        Console.WriteLine("Game server availability OK: offline CTA/CanExecute, repeated direct calls, retained offline status, stale snapshots, ticket/SSO/process races, recovery, authentication and maintenance; fake services only.");
    }

    private static void KeepOfflineKnowledgeAndRejectRepeatedCommands()
    {
        using PlayRuntimeEnvironment environment = new(authenticated: true);
        ServerStatusStub dashboard = new();
        environment.Coordinator.AttachDashboard(dashboard);
        using PrimaryActionCommand command = new(environment.Coordinator);
        int invalidations = 0;
        command.Command.CanExecuteChanged += (_, _) => invalidations++;
        True(command.Command.CanExecute(null), "L'absence de première observation ne doit pas bloquer le lancement/authentification.");
        dashboard.Publish(DashboardRealmState.Unavailable);
        True(command.Command.CanExecute(null), "Une première erreur de statut doit laisser les contrôles réseau du ticket fonctionner.");
        dashboard.Publish(DashboardRealmState.Offline);
        AssertBlocked();
        for (int index = 0; index < 5; index++)
        {
            command.Command.Execute(null);
            Equal(GamePrimaryActionStatus.ServerUnavailable, environment.Coordinator.TryExecutePrimaryAction(),
                "L'appel direct répété doit être bloqué même s'il contourne ICommand.CanExecute.");
        }
        Equal(0, environment.Launch.Calls, "Un serveur hors ligne ne doit créer aucune tentative de lancement.");
        True(environment.Operations.IsIdle && environment.Coordinator.CanVerify,
            "Le refus ne doit prendre aucun bail et doit préserver la vérification du client.");
        foreach (DashboardRealmState state in new[] { DashboardRealmState.Loading, DashboardRealmState.Unavailable, DashboardRealmState.Unknown })
        {
            dashboard.Publish(state);
            AssertBlocked();
        }
        dashboard.Publish(DashboardRealmState.Online, sequence: 1);
        AssertBlocked();
        dashboard.Publish(DashboardRealmState.Degraded);
        True(command.Command.CanExecute(null), "Des passerelles et un monde disponibles doivent réautoriser Jouer même si un autre service est dégradé.");
        Equal("Jouer", GameStateAdapter.Project(environment.Coordinator.CurrentSnapshot).PrimaryActionLabel,
            "Le bouton doit redevenir Jouer après rétablissement.");
        True(invalidations >= 2, "Le changement de statut doit notifier la disponibilité de la commande.");
        True(environment.Authenticated && environment.SessionState == LauncherSessionState.Authenticated,
            "Un statut de monde hors ligne ne doit pas modifier la session du launcher.");

        GameUiState preview = LauncherV2PreviewData.CreateGame(GamePreviewScenario.RealmOffline);
        Equal("Serveur indisponible", preview.PrimaryActionLabel, "Le scénario visuel hors ligne doit refléter le libellé réel.");
        True(!preview.IsPrimaryActionEnabled && !preview.PrimaryActionCommand.CanExecute(null), "Le preview hors ligne doit rester non déclenchable.");

        void AssertBlocked()
        {
            GameRuntimeSnapshot snapshot = environment.Coordinator.CurrentSnapshot;
            GameViewState view = GameStateAdapter.Project(snapshot);
            True(!command.Command.CanExecute(null) && !snapshot.CanPrimaryAction && snapshot.IsGameServerUnavailable,
                "Le serveur hors ligne doit désactiver la commande et le snapshot runtime ensemble.");
            Equal("Serveur indisponible", view.PrimaryActionLabel, "Le bouton hors ligne doit expliquer son indisponibilité.");
            True(!view.IsPrimaryActionEnabled && view.IsClientReady, "Le refus du serveur doit conserver le client local prêt.");
        }
    }

    private static async Task RecheckEveryLaunchBoundaryAsync()
    {
        foreach (string boundary in new[] { "before", "config", "request-ticket", "ticket-return", "prepare-sso", "after-sso", "start-process" })
        {
            using LaunchServiceEnvironment environment = new();
            GameLaunchAvailability availability = new();
            long sequence = 0;
            void GoOffline() => availability.Update(Snapshot(++sequence, DashboardRealmState.Offline));
            if (boundary == "before") GoOffline();
            GameLaunchPermit permit = availability.CreatePermit();
            environment.Platform.EventSink = name => { if (boundary == "config" && name == "config") GoOffline(); };
            environment.Session.EventSink = _ => { if (boundary == "ticket-return") GoOffline(); };
            environment.Platform.AfterSso = () => { if (boundary == "after-sso") GoOffline(); };
            GameLaunchResult result = await environment.Service.LaunchAsync(
                new GameLaunchRequest(100, environment.Root, "frFR", permit),
                progress =>
                {
                    if (boundary == "request-ticket" && progress.Phase == GameLaunchPhase.RequestingTicket
                        || boundary == "prepare-sso" && progress.Phase == GameLaunchPhase.PreparingSso
                        || boundary == "start-process" && progress.Phase == GameLaunchPhase.StartingProcess) GoOffline();
                }, CancellationToken.None);
            Equal(GameLaunchOutcome.ServerUnavailable, result.Outcome, $"Le changement hors ligne à {boundary} doit arrêter la tentative.");
            Equal(0, environment.Process.Calls, $"Aucun processus ne doit démarrer après le refus à {boundary}.");
            if (boundary is "before" or "config" or "request-ticket")
                Equal(0, environment.Session.Calls, $"Aucun ticket ne doit être demandé après le refus à {boundary}.");
            if (boundary is not ("after-sso" or "start-process"))
                Equal(0, environment.Platform.SsoCalls, $"Aucun SSO ne doit être écrit après le refus à {boundary}.");
            if (boundary == "before") Equal(0, environment.Platform.ConfigCalls, "Le refus initial ne doit pas modifier la configuration du client.");
        }

        using LaunchServiceEnvironment running = new();
        running.Platform.IsGameRunningResult = true;
        GameLaunchAvailability offline = new();
        offline.Update(Snapshot(1, DashboardRealmState.Offline));
        GameLaunchResult existing = await running.Service.LaunchAsync(new GameLaunchRequest(101, running.Root, "frFR", offline.CreatePermit()), null, CancellationToken.None);
        Equal(GameLaunchOutcome.AlreadyRunning, existing.Outcome, "Un jeu existant doit rester identifié sans être relancé ni stoppé.");
        Equal(0, running.Process.Calls, "Le contrôle hors ligne ne doit jamais créer un deuxième processus.");
    }

    private static async Task InvalidateAnAwaitingTicketAcrossRecoveryAsync()
    {
        using PlayRuntimeEnvironment environment = new(authenticated: true);
        using LaunchServiceEnvironment platform = new();
        DeferredTicketSession session = new(platform.Session.Result);
        GameLaunchService service = new(session, platform.Platform, platform.Process);
        environment.Launch.Handler = service.LaunchAsync;
        ServerStatusStub dashboard = new();
        environment.Coordinator.AttachDashboard(dashboard);
        dashboard.Publish(DashboardRealmState.Online);
        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "La tentative avant panne doit commencer.");
        await session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        dashboard.Publish(DashboardRealmState.Offline);
        Equal("Serveur indisponible", GameStateAdapter.Project(environment.Coordinator.CurrentSnapshot).PrimaryActionLabel,
            "La panne pendant l'attente du ticket doit être annoncée sur le bouton.");
        Equal(GamePrimaryActionStatus.Busy, environment.Coordinator.TryExecutePrimaryAction(), "Une répétition ne doit pas doubler la tentative encore en attente.");
        dashboard.Publish(DashboardRealmState.Online);
        session.Release.TrySetResult();
        await environment.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Equal(GameLaunchOutcome.ServerUnavailable, environment.Coordinator.CurrentSnapshot.LastPlayOutcome,
            "Le retour du serveur ne doit pas ressusciter un ticket commencé avant sa panne.");
        Equal(0, platform.Platform.SsoCalls, "Le ticket tardif doit être abandonné avant toute écriture SSO.");
        Equal(0, platform.Process.Calls, "Le ticket tardif ne doit démarrer aucun processus.");
        Equal(1, environment.Launch.Calls, "Les appels répétés ne doivent créer qu'une tentative.");
        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "Un nouveau clic après rétablissement doit pouvoir repartir.");
        await environment.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Equal(1, platform.Process.Calls, "Seule la nouvelle tentative explicite doit démarrer le processus simulé.");
    }

    private static async Task ReleasePendingAuthenticationWithoutStartingAsync()
    {
        using PlayRuntimeEnvironment environment = new(authenticated: false);
        ServerStatusStub dashboard = new();
        environment.Coordinator.AttachDashboard(dashboard);
        Equal(GamePrimaryActionStatus.Unauthenticated, environment.Coordinator.TryExecutePrimaryAction(), "La première observation inconnue doit permettre la connexion habituelle.");
        dashboard.Publish(DashboardRealmState.Offline);
        True(!environment.Coordinator.CurrentSnapshot.IsPlayPendingAuthentication && environment.Operations.IsIdle,
            "La panne doit libérer la demande Play en attente de connexion.");
        True(!environment.Authenticated && environment.SessionState == LauncherSessionState.SignedOut, "Le refus du jeu ne doit pas modifier la session d'authentification.");
        environment.Authenticated = true;
        environment.SessionState = LauncherSessionState.Authenticated;
        True(!environment.Coordinator.ResumePendingPlayAfterAuthentication(), "La fin de la connexion ne doit pas relancer une ancienne demande hors ligne.");
        Equal(0, environment.Launch.Calls, "La connexion terminée pendant une panne ne doit lancer aucun jeu.");
        dashboard.Publish(DashboardRealmState.Online);
        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "Après connexion et rétablissement, une nouvelle demande doit fonctionner.");
        await environment.Coordinator.WaitForIdleAsync();
    }

    private static async Task PreserveAutomaticVerificationAsync()
    {
        using PlayRuntimeEnvironment environment = new(authenticated: true);
        ServerStatusStub dashboard = new();
        environment.Coordinator.AttachDashboard(dashboard);
        dashboard.Publish(DashboardRealmState.Offline);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Verification.Handler = async (_, _, progress, cancellationToken) =>
        {
            progress?.Invoke(new GameVerificationProgress(GameVerificationPhase.ScanningFiles, 1, 4));
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new GameClientVerificationResult(GameVerificationOutcome.UpToDate, GameAction.Play, GameUpdateKnowledge.Known, "v1", 0);
        };
        try
        {
            Equal(GameVerificationStartStatus.Started, environment.Coordinator.TryStartVerification(), "Le serveur hors ligne ne doit pas bloquer la vérification automatique.");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            GameUiState view = new();
            view.ApplyRuntimeView(GameStateAdapter.Project(environment.Coordinator.CurrentSnapshot));
            True(view.ShowsProgress && view.Progress == 25, "La vérification doit conserver son indicateur pendant l'indisponibilité du serveur.");
            Equal("Serveur indisponible", view.PrimaryActionLabel, "Le CTA Jouer doit rester bloqué pendant la vérification automatique.");
        }
        finally { release.TrySetResult(); }
        await environment.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task PreserveMaintenanceAndRunningGameAsync()
    {
        using (RuntimeGameEnvironment install = new(playable: false, authenticated: true))
        {
            ServerStatusStub dashboard = new();
            install.Coordinator.AttachDashboard(dashboard);
            dashboard.Publish(DashboardRealmState.Offline);
            Equal("Installer", GameStateAdapter.Project(install.Coordinator.CurrentSnapshot).PrimaryActionLabel, "Le serveur hors ligne ne doit pas remplacer Installer.");
            Equal(GamePrimaryActionStatus.Started, install.Coordinator.TryExecutePrimaryAction(), "Installer doit rester autorisé hors ligne côté monde.");
            await install.Coordinator.WaitForIdleAsync();
            Equal(1, install.Maintenance.Calls, "L'installation simulée doit être atteinte.");
        }
        using (RuntimeGameEnvironment update = new(playable: true, authenticated: true))
        {
            ServerStatusStub dashboard = new();
            update.Coordinator.AttachDashboard(dashboard);
            dashboard.Publish(DashboardRealmState.Offline);
            update.Verification.Result = new GameClientVerificationResult(GameVerificationOutcome.UpdateAvailable, GameAction.Update, GameUpdateKnowledge.Known, "v2", 1);
            Equal(GameVerificationStartStatus.Started, update.Coordinator.TryStartVerification(), "La vérification du client doit rester disponible.");
            await update.Coordinator.WaitForIdleAsync();
            Equal("Mettre à jour", GameStateAdapter.Project(update.Coordinator.CurrentSnapshot).PrimaryActionLabel, "La maintenance doit conserver son propre CTA.");
            Equal(GamePrimaryActionStatus.Started, update.Coordinator.TryExecutePrimaryAction(), "Mettre à jour doit rester autorisé.");
            await update.Coordinator.WaitForIdleAsync();
            Equal(GameVerificationStartStatus.Started, update.Coordinator.TryStartFullRepair(), "La réparation doit rester autorisée.");
            await update.Coordinator.WaitForIdleAsync();
            Equal(1, update.Maintenance.RepairCalls, "La réparation simulée doit être atteinte.");
        }
        using PlayRuntimeEnvironment running = new(authenticated: true);
        ServerStatusStub runningStatus = new();
        running.Coordinator.AttachDashboard(runningStatus);
        running.ProcessMonitor.HoldLifecycle();
        Equal(GamePrimaryActionStatus.Started, running.Coordinator.TryExecutePrimaryAction(), "Le jeu témoin doit commencer avant la panne.");
        running.ProcessMonitor.MarkRunning();
        await UntilAsync(() => running.Coordinator.CurrentSnapshot.PlayLaunchPhase == GameLaunchPhase.Running);
        runningStatus.Publish(DashboardRealmState.Offline);
        Equal("Jeu en cours d’utilisation", GameStateAdapter.Project(running.Coordinator.CurrentSnapshot).PrimaryActionLabel,
            "Un jeu déjà lancé doit garder son état en cours d'utilisation.");
        Equal(0, running.ProcessMonitor.StopCalls, "Une panne du monde ne doit pas arrêter brutalement le jeu existant.");
        running.Coordinator.BeginShutdown();
        await running.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        True(running.ProcessMonitor.StopCalls >= 1, "L'arrêt demandé du launcher doit toujours pouvoir arrêter le jeu hors ligne.");
    }

    private static DashboardSnapshot Snapshot(long sequence, DashboardRealmState state) =>
        DashboardSnapshot.Initial with { Sequence = sequence, RealmState = state };

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} Attendu={expected}; actuel={actual}.");

    private sealed class DeferredTicketSession(GameTicketAcquisitionResult result) : IGameLaunchSession
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GameTicketAcquisitionResult> AcquireGameTicketAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    private sealed class ServerStatusStub : ILauncherDashboardRuntime
    {
        private long _sequence;
        public event EventHandler? AvailabilityChanged { add { } remove { } }
        public event EventHandler<DashboardSnapshotEventArgs>? SnapshotChanged;
        public DashboardSnapshot CurrentSnapshot { get; private set; } = DashboardSnapshot.Initial;
        public bool CanRefresh => false;
        public DashboardRefreshStartStatus TryRefresh() => DashboardRefreshStartStatus.NoSession;
        internal void Publish(DashboardRealmState state, long? sequence = null)
        {
            CurrentSnapshot = Snapshot(sequence ?? ++_sequence, state);
            SnapshotChanged?.Invoke(this, new DashboardSnapshotEventArgs(CurrentSnapshot));
        }
    }
}
