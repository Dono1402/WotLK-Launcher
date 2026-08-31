using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class GameLaunchTests
{
    private const string SecretTicket = "HP-0123456789ABCDEF0123456789ABCDEF01234567";

    internal static async Task<int> RunAsync()
    {
        await PreserveLegacyLaunchOrderAndArgumentsAsync();
        await CategorizeLaunchFailuresWithoutSideEffectsAsync();
        await CancelBeforeProcessStartAsync();
        await AcquireExactlyOneTicketAndProtectSessionAsync();
        await EnforcePlaySingleFlightAndTerminalRecoveryAsync();
        await ManagePendingAuthenticationExactlyOnceAsync();
        await EnforceOperationCompatibilityAsync();
        await CancelPlayForShutdownWithoutLateCallbacksAsync();
        KeepLaunchingPreviewIsolated();
        await ValidateWpfPlayAndAuthenticationFlowAsync();
        Console.WriteLine("V2 Play ticket, SSO and process coordination OK (02F.3 simulated).");
        return 0;
    }

    private static async Task PreserveLegacyLaunchOrderAndArgumentsAsync()
    {
        using LaunchServiceEnvironment environment = new();
        List<string> events = [];
        environment.Session.EventSink = events.Add;
        environment.Platform.EventSink = events.Add;
        environment.Process.EventSink = events.Add;
        List<GameLaunchPhase> phases = [];

        GameLaunchResult result = await environment.Service.LaunchAsync(
            new GameLaunchRequest(41, environment.Root, "frFR"),
            progress => phases.Add(progress.Phase),
            CancellationToken.None);

        Equal(GameLaunchOutcome.Started, result.Outcome, "Le lancement simulé doit réussir.");
        SequenceEqual(
            ["config", "ticket", "sso", "process"],
            events,
            "L'ordre config, ticket, SSO et processus doit rester celui du legacy.");
        SequenceEqual(
            [
                GameLaunchPhase.RequestingTicket,
                GameLaunchPhase.PreparingSso,
                GameLaunchPhase.StartingProcess
            ],
            phases,
            "Les phases de lancement sont incohérentes.");
        Equal(1, environment.Session.Calls, "Une tentative doit demander exactement un ticket.");
        Equal(1, environment.Platform.ConfigCalls, "La configuration doit être préparée une fois.");
        Equal(1, environment.Platform.SsoCalls, "Le SSO doit être écrit une fois.");
        Equal(1, environment.Process.Calls, "Process.Start doit être appelé une fois.");
        True(environment.Process.StartInfo is not null, "Les paramètres Arctium doivent être capturés.");

        ProcessStartInfo startInfo = environment.Process.StartInfo!;
        Equal(
            Path.Combine(environment.Root, GameInstallServices.GameLauncherFileName),
            startInfo.FileName,
            "L'exécutable Arctium legacy a changé.");
        Equal(environment.Root, startInfo.WorkingDirectory, "Le dossier de travail legacy a changé.");
        True(!startInfo.UseShellExecute && startInfo.CreateNoWindow, "Les options de processus legacy ont changé.");
        Equal(ProcessWindowStyle.Hidden, startInfo.WindowStyle, "Le style de fenêtre legacy a changé.");
        SequenceEqual(
            [
                "--version",
                "Classic",
                "--path",
                Path.Combine(environment.Root, GameInstallServices.ClassicDirectoryName),
                "--portal",
                GameInstallServices.PortalAddress,
                "--skipcertcheck",
                "-launcherlogin",
                "-uid",
                "wow_classic"
            ],
            startInfo.ArgumentList.ToArray(),
            "Les arguments Arctium legacy ont changé.");
        True(
            startInfo.ArgumentList.All(argument => !argument.Contains(SecretTicket, StringComparison.Ordinal)),
            "Le ticket ne doit jamais être placé dans les arguments du processus.");
    }

    private static async Task CategorizeLaunchFailuresWithoutSideEffectsAsync()
    {
        using (LaunchServiceEnvironment missing = new())
        {
            missing.Platform.HasPlayableClientResult = false;
            GameLaunchResult result = await missing.LaunchAsync();
            Equal(GameLaunchOutcome.ExecutableMissing, result.Outcome, "Un client absent doit être distingué.");
            Equal(0, missing.Session.Calls, "Un client absent ne doit consommer aucun ticket.");
            Equal(0, missing.Platform.SsoCalls, "Un client absent ne doit écrire aucun SSO.");
            Equal(0, missing.Process.Calls, "Un client absent ne doit lancer aucun processus.");
        }

        using (LaunchServiceEnvironment running = new())
        {
            running.Platform.IsGameRunningResult = true;
            GameLaunchResult result = await running.LaunchAsync();
            Equal(GameLaunchOutcome.AlreadyRunning, result.Outcome, "Le jeu déjà lancé doit rester un résultat distinct.");
            Equal(0, running.Session.Calls, "Le jeu déjà lancé ne doit consommer aucun ticket.");
            Equal(0, running.Process.Calls, "Le jeu déjà lancé ne doit pas créer un second processus.");
        }

        (GameTicketAcquisitionStatus TicketStatus, GameLaunchOutcome Outcome, GameLaunchFailureCategory Category)[] ticketCases =
        [
            (GameTicketAcquisitionStatus.AuthenticationRequired, GameLaunchOutcome.AuthenticationRequired, GameLaunchFailureCategory.AuthenticationRequired),
            (GameTicketAcquisitionStatus.NetworkUnavailable, GameLaunchOutcome.NetworkUnavailable, GameLaunchFailureCategory.Network),
            (GameTicketAcquisitionStatus.ServiceUnavailable, GameLaunchOutcome.ServiceUnavailable, GameLaunchFailureCategory.ServiceUnavailable),
            (GameTicketAcquisitionStatus.TicketRejected, GameLaunchOutcome.TicketFailed, GameLaunchFailureCategory.TicketRejected),
            (GameTicketAcquisitionStatus.Cancelled, GameLaunchOutcome.Cancelled, GameLaunchFailureCategory.Cancelled),
            (GameTicketAcquisitionStatus.Unknown, GameLaunchOutcome.Unknown, GameLaunchFailureCategory.Unknown)
        ];
        foreach ((GameTicketAcquisitionStatus status, GameLaunchOutcome outcome, GameLaunchFailureCategory category) in ticketCases)
        {
            using LaunchServiceEnvironment environment = new();
            environment.Session.Result = new GameTicketAcquisitionResult(
                status,
                Failure: new HttpRequestException("sensitive-ticket-detail"));
            GameLaunchResult result = await environment.LaunchAsync();
            Equal(outcome, result.Outcome, $"Le résultat ticket {status} est incorrect.");
            Equal(category, result.FailureCategory, $"La catégorie ticket {status} est incorrecte.");
            Equal(0, environment.Platform.SsoCalls, "Un ticket refusé ne doit écrire aucun SSO.");
            Equal(0, environment.Process.Calls, "Un ticket refusé ne doit lancer aucun processus.");
        }

        using (LaunchServiceEnvironment timeout = new())
        {
            timeout.Session.Result = new GameTicketAcquisitionResult(
                GameTicketAcquisitionStatus.NetworkUnavailable,
                Failure: new TaskCanceledException("timeout"));
            GameLaunchResult result = await timeout.LaunchAsync();
            Equal(GameLaunchFailureCategory.Timeout, result.FailureCategory, "Un timeout ticket doit être distingué du réseau.");
        }

        using (LaunchServiceEnvironment sso = new())
        {
            sso.Platform.SsoFailure = new IOException("partial-write-secret");
            GameLaunchResult result = await sso.LaunchAsync();
            Equal(GameLaunchOutcome.SsoFailed, result.Outcome, "Un échec SSO doit être catégorisé.");
            Equal(0, sso.Process.Calls, "Un échec SSO ne doit jamais lancer Arctium.");
        }

        using (LaunchServiceEnvironment deniedSso = new())
        {
            deniedSso.Platform.SsoFailure = new UnauthorizedAccessException("registry denied");
            GameLaunchResult result = await deniedSso.LaunchAsync();
            Equal(GameLaunchOutcome.AccessDenied, result.Outcome, "Un refus d'accès SSO doit rester AccessDenied.");
            Equal(0, deniedSso.Process.Calls, "Un SSO refusé ne doit jamais lancer Arctium.");
        }

        using (LaunchServiceEnvironment nullProcess = new())
        {
            nullProcess.Process.StartResult = false;
            GameLaunchResult result = await nullProcess.LaunchAsync();
            Equal(GameLaunchOutcome.StartFailed, result.Outcome, "Process.Start null doit être un échec.");
        }

        using (LaunchServiceEnvironment deniedProcess = new())
        {
            deniedProcess.Process.Failure = new Win32Exception(5, "access denied raw path");
            GameLaunchResult result = await deniedProcess.LaunchAsync();
            Equal(GameLaunchOutcome.AccessDenied, result.Outcome, "Win32 access denied doit être catégorisé.");
        }

        using (LaunchServiceEnvironment failedProcess = new())
        {
            failedProcess.Process.Failure = new InvalidOperationException("raw process detail");
            GameLaunchResult result = await failedProcess.LaunchAsync();
            Equal(GameLaunchOutcome.StartFailed, result.Outcome, "Une exception Process.Start doit être catégorisée.");
        }
    }

    private static async Task CancelBeforeProcessStartAsync()
    {
        using LaunchServiceEnvironment environment = new();
        using CancellationTokenSource cancellation = new();
        environment.Platform.AfterSso = cancellation.Cancel;

        GameLaunchResult result = await environment.Service.LaunchAsync(
            new GameLaunchRequest(42, environment.Root, "frFR"),
            reportProgress: null,
            cancellation.Token);

        Equal(GameLaunchOutcome.Cancelled, result.Outcome, "La fermeture juste avant Process.Start doit annuler la tentative.");
        Equal(1, environment.Platform.SsoCalls, "Le SSO témoin devait être atteint.");
        Equal(0, environment.Process.Calls, "Aucun processus ne doit démarrer après annulation.");
    }

    private static async Task AcquireExactlyOneTicketAndProtectSessionAsync()
    {
        List<string> logs = [];
        using CancellationTokenSource lifetime = new();
        FakeLauncherAuthService authentication = AuthenticatedService();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, logs.Add);
        LauncherSessionRestoreResult restored = await coordinator.RestoreOnceAsync();
        Equal(LauncherSessionRestoreStatus.Restored, restored.Status, "La session témoin doit être restaurée.");
        authentication.ResetOperationCounters();

        GameTicketAcquisitionResult success = await coordinator.AcquireGameTicketAsync(CancellationToken.None);
        Equal(GameTicketAcquisitionStatus.Succeeded, success.Status, "Le ticket valide doit être accepté.");
        Equal(1, authentication.EnsureFreshCalls, "Le coordinateur doit vérifier la fraîcheur une fois.");
        Equal(1, authentication.CreateGameTicketCalls, "Le coordinateur doit demander exactement un ticket.");

        authentication.GameTicketHandler = _ => Task.FromException<GameTicket>(
            new HttpRequestException($"network {SecretTicket}"));
        GameTicketAcquisitionResult network = await coordinator.AcquireGameTicketAsync(CancellationToken.None);
        Equal(GameTicketAcquisitionStatus.NetworkUnavailable, network.Status, "Une panne réseau doit être conservée.");
        True(authentication.Session is not null, "Une panne réseau ne doit pas effacer la session stockée.");
        True(logs.All(line => !line.Contains(SecretTicket, StringComparison.Ordinal)), "Le ticket ne doit jamais apparaître dans les logs.");

        FakeLauncherAuthService unauthorizedAuthentication = AuthenticatedService();
        unauthorizedAuthentication.GameTicketHandler = _ => Task.FromException<GameTicket>(
            new LauncherAuthException($"unauthorized {SecretTicket}", HttpStatusCode.Unauthorized));
        using LauncherSessionCoordinator unauthorized = new(
            unauthorizedAuthentication,
            CancellationToken.None,
            logs.Add);
        await unauthorized.RestoreOnceAsync();
        unauthorizedAuthentication.ResetOperationCounters();
        GameTicketAcquisitionResult rejected = await unauthorized.AcquireGameTicketAsync(CancellationToken.None);
        Equal(GameTicketAcquisitionStatus.AuthenticationRequired, rejected.Status, "Unauthorized doit demander une reconnexion.");
        Equal(LauncherSessionState.SignedOut, unauthorized.CurrentSnapshot.State, "La session réellement invalide doit être invalidée.");
        True(unauthorizedAuthentication.Session is null, "Le stockage local doit être invalidé après Unauthorized réel.");
        Equal(1, unauthorizedAuthentication.CreateGameTicketCalls, "Le rejet ne doit pas provoquer une seconde demande de ticket.");
        True(logs.All(line => !line.Contains(SecretTicket, StringComparison.Ordinal)), "Le détail Unauthorized ne doit pas exposer le ticket.");
    }

    private static async Task EnforcePlaySingleFlightAndTerminalRecoveryAsync()
    {
        using PlayRuntimeEnvironment environment = new(authenticated: true);
        TaskCompletionSource entered = Signal();
        TaskCompletionSource release = Signal();
        Action<GameLaunchProgress>? firstProgress = null;
        environment.Launch.Handler = async (request, progress, cancellationToken) =>
        {
            firstProgress = progress;
            progress?.Invoke(new GameLaunchProgress(request.AttemptId, GameLaunchPhase.RequestingTicket));
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new GameLaunchResult(request.AttemptId, GameLaunchOutcome.Started);
        };
        int startedEvents = 0;
        environment.Coordinator.PlayStarted += (_, _) => startedEvents++;

        True(environment.Coordinator.CanExecutePrimaryAction, "Jouer doit être disponible avec un client et une session valides.");
        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "La première tentative Play doit démarrer.");
        await entered.Task;
        long firstAttempt = environment.Coordinator.CurrentSnapshot.PlayAttemptId
            ?? throw new InvalidOperationException("L'identifiant Play est absent.");
        Equal(GamePrimaryActionStatus.Busy, environment.Coordinator.TryExecutePrimaryAction(), "Un double clic doit être refusé immédiatement.");
        Equal(GamePrimaryActionStatus.Busy, environment.Coordinator.TryExecutePrimaryAction(), "Entrée répétée doit rester single-flight.");
        Equal(1, environment.Launch.Calls, "Le double événement ne doit pas créer une seconde orchestration.");
        GameViewState activeView = GameStateAdapter.Project(environment.Coordinator.CurrentSnapshot);
        Equal("Lancement…", activeView.PrimaryActionLabel, "Le bouton actif doit afficher Lancement…");
        True(!activeView.IsPrimaryActionEnabled && activeView.IsLaunchInProgress, "Le bouton actif doit être désactivé avec son indicateur.");

        release.TrySetResult();
        await environment.Coordinator.WaitForIdleAsync();
        Equal(1, startedEvents, "Le succès réel doit lever un seul événement PlayStarted.");
        Equal(GameAction.Play, environment.Coordinator.CurrentSnapshot.Action, "GameAction doit rester Play.");
        Equal(GameLaunchPhase.Started, environment.Coordinator.CurrentSnapshot.PlayLaunchPhase, "Le résultat terminal doit être Started.");
        True(environment.Coordinator.CanExecutePrimaryAction, "Le verrou Play doit être libéré après succès.");

        TaskCompletionSource secondEntered = Signal();
        TaskCompletionSource secondRelease = Signal();
        environment.Launch.Handler = async (request, progress, cancellationToken) =>
        {
            progress?.Invoke(new GameLaunchProgress(request.AttemptId, GameLaunchPhase.PreparingSso));
            secondEntered.TrySetResult();
            await secondRelease.Task.WaitAsync(cancellationToken);
            return new GameLaunchResult(
                request.AttemptId,
                GameLaunchOutcome.TicketFailed,
                GameLaunchFailureCategory.TicketRejected,
                new InvalidOperationException(SecretTicket));
        };
        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "Une nouvelle tentative doit être possible.");
        await secondEntered.Task;
        long secondAttempt = environment.Coordinator.CurrentSnapshot.PlayAttemptId
            ?? throw new InvalidOperationException("Le second identifiant Play est absent.");
        True(secondAttempt > firstAttempt, "Les identifiants Play doivent être monotones.");
        long sequenceBeforeStale = environment.Coordinator.CurrentSnapshot.Sequence;
        firstProgress?.Invoke(new GameLaunchProgress(firstAttempt, GameLaunchPhase.StartingProcess));
        Equal(sequenceBeforeStale, environment.Coordinator.CurrentSnapshot.Sequence, "Un callback d'une ancienne tentative doit être ignoré.");

        secondRelease.TrySetResult();
        await environment.Coordinator.WaitForIdleAsync();
        GameRuntimeSnapshot failed = environment.Coordinator.CurrentSnapshot;
        Equal(GameLaunchPhase.Failed, failed.PlayLaunchPhase, "Le refus ticket doit produire Failed.");
        Equal(GameAction.Play, failed.Action, "Un refus ticket ne doit pas devenir une erreur du client local.");
        True(environment.Coordinator.CanExecutePrimaryAction, "Un échec terminal doit libérer Play pour un nouvel essai.");
        Equal(1, startedEvents, "Un échec ne doit pas lever PlayStarted.");
        True(environment.Logs.All(line => !line.Contains(SecretTicket, StringComparison.Ordinal)), "Le journal Play ne doit pas exposer le ticket.");
        AssertSnapshotContainsNoSecret(failed);
    }

    private static async Task ManagePendingAuthenticationExactlyOnceAsync()
    {
        using PlayRuntimeEnvironment environment = new(authenticated: false);
        int overlayRequests = 0;
        environment.Coordinator.PlayAuthenticationRequired += (_, _) => overlayRequests++;

        Equal(GamePrimaryActionStatus.Unauthenticated, environment.Coordinator.TryExecutePrimaryAction(), "Play déconnecté doit demander l'overlay.");
        Equal(1, overlayRequests, "L'overlay doit être demandé exactement une fois.");
        True(environment.Coordinator.CurrentSnapshot.IsPlayPendingAuthentication, "Une seule demande Play doit être conservée.");
        Equal(0, environment.Launch.Calls, "Aucun ticket ou lancement ne doit précéder l'authentification.");
        Equal(GamePrimaryActionStatus.Busy, environment.Coordinator.TryExecutePrimaryAction(), "Un second clic pendant l'overlay doit être refusé.");
        Equal(1, overlayRequests, "Le second clic ne doit pas ouvrir un second overlay.");

        True(environment.Coordinator.CancelPendingPlayAuthentication(), "La fermeture de l'overlay doit abandonner la demande.");
        True(!environment.Coordinator.CurrentSnapshot.IsPlayPendingAuthentication, "La demande fermée ne doit pas survivre.");
        True(!environment.Coordinator.ResumePendingPlayAfterAuthentication(), "Une authentification tardive ne doit pas reprendre une demande annulée.");
        True(environment.Coordinator.CanExecutePrimaryAction, "Le bouton Jouer doit redevenir disponible après fermeture.");

        Equal(GamePrimaryActionStatus.Unauthenticated, environment.Coordinator.TryExecutePrimaryAction(), "La nouvelle demande Play doit démarrer.");
        environment.Authenticated = true;
        environment.SessionState = LauncherSessionState.Authenticated;
        environment.Coordinator.RefreshAuthenticationAvailability();
        True(environment.Coordinator.ResumePendingPlayAfterAuthentication(), "Le succès d'authentification doit reprendre automatiquement Play.");
        True(!environment.Coordinator.ResumePendingPlayAfterAuthentication(), "La demande en attente doit être consommée une seule fois.");
        await environment.Coordinator.WaitForIdleAsync();
        Equal(1, environment.Launch.Calls, "La reprise doit produire une seule tentative de lancement.");
        Equal(GameLaunchOutcome.Started, environment.Coordinator.CurrentSnapshot.LastPlayOutcome, "La reprise automatique doit atteindre Started.");

        environment.Launch.ResultFactory = request => new GameLaunchResult(
            request.AttemptId,
            GameLaunchOutcome.AuthenticationRequired,
            GameLaunchFailureCategory.AuthenticationRequired);
        int requestsBeforeExpiredSession = overlayRequests;
        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "La session expirée doit démarrer une tentative unique.");
        await WaitForAsync(() => environment.Coordinator.CurrentSnapshot.IsPlayPendingAuthentication);
        Equal(requestsBeforeExpiredSession + 1, overlayRequests, "Une session réellement expirée doit rouvrir l'overlay une fois.");
        Equal(GameAction.Play, environment.Coordinator.CurrentSnapshot.Action, "L'expiration ne doit pas altérer le client local.");
        True(environment.Coordinator.CancelPendingPlayAuthentication(), "La seconde demande doit pouvoir être abandonnée.");

        environment.SessionState = LauncherSessionState.Unavailable;
        environment.Authenticated = false;
        environment.Coordinator.RefreshAuthenticationAvailability();
        int requestsBeforeNetwork = overlayRequests;
        Equal(GamePrimaryActionStatus.AuthenticationUnavailable, environment.Coordinator.TryExecutePrimaryAction(), "Le réseau indisponible doit être un refus terminal court.");
        Equal(requestsBeforeNetwork, overlayRequests, "Une panne réseau ne doit pas ouvrir arbitrairement l'overlay.");
        Equal(GameAction.Play, environment.Coordinator.CurrentSnapshot.Action, "Le réseau ne doit pas devenir une erreur client.");
        True(environment.Coordinator.CanExecutePrimaryAction, "Une nouvelle tentative manuelle doit rester possible.");
    }

    private static async Task EnforceOperationCompatibilityAsync()
    {
        using (PlayRuntimeEnvironment concurrent = new(authenticated: true))
        {
            TaskCompletionSource verifyEntered = Signal();
            TaskCompletionSource verifyRelease = Signal();
            concurrent.Verification.Handler = async (_, _, _, cancellationToken) =>
            {
                verifyEntered.TrySetResult();
                await verifyRelease.Task.WaitAsync(cancellationToken);
                return UpToDate();
            };
            Equal(GameVerificationStartStatus.Started, concurrent.Coordinator.TryStartVerification(), "La vérification automatique doit démarrer.");
            await verifyEntered.Task;
            Equal(GamePrimaryActionStatus.Started, concurrent.Coordinator.TryExecutePrimaryAction(), "Play doit coexister avec Verify non mutant.");
            await WaitForAsync(() => concurrent.Coordinator.CurrentSnapshot.LastPlayOutcome == GameLaunchOutcome.Started);
            Equal(GameAction.Play, concurrent.Coordinator.CurrentSnapshot.Action, "Verify + Play doit préserver GameAction.");
            verifyRelease.TrySetResult();
            await concurrent.Coordinator.WaitForIdleAsync();
        }

        LauncherOperationKind[] incompatibleKinds =
        [
            LauncherOperationKind.GameRepair,
            LauncherOperationKind.GameInstall,
            LauncherOperationKind.GameUpdate,
            LauncherOperationKind.Addons,
            LauncherOperationKind.LauncherAutoUpdate
        ];
        foreach (LauncherOperationKind kind in incompatibleKinds)
        {
            using PlayRuntimeEnvironment environment = new(authenticated: true);
            LauncherOperationStartResult operation = environment.Operations.TryBegin(
                kind,
                canUserCancel: true,
                clientIsPlayable: true);
            True(operation.IsStarted, $"Le bail témoin {kind} doit démarrer.");
            True(!environment.Coordinator.CanExecutePrimaryAction, $"Play doit être désactivé pendant {kind}.");
            Equal(GamePrimaryActionStatus.Busy, environment.Coordinator.TryExecutePrimaryAction(), $"Play doit être refusé immédiatement pendant {kind}.");
            Equal(0, environment.Launch.Calls, $"Le refus {kind} ne doit jamais être mis en file.");
            operation.Lease!.Complete();
            await environment.Operations.WaitForIdleAsync(TimeSpan.FromSeconds(1));
            Equal(0, environment.Launch.Calls, $"La libération de {kind} ne doit pas rejouer le clic refusé.");
        }
    }

    private static async Task CancelPlayForShutdownWithoutLateCallbacksAsync()
    {
        using PlayRuntimeEnvironment environment = new(authenticated: true);
        TaskCompletionSource entered = Signal();
        Action<GameLaunchProgress>? delayedProgress = null;
        environment.Launch.Handler = async (request, progress, cancellationToken) =>
        {
            delayedProgress = progress;
            progress?.Invoke(new GameLaunchProgress(request.AttemptId, GameLaunchPhase.RequestingTicket));
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return new GameLaunchResult(
                    request.AttemptId,
                    GameLaunchOutcome.Cancelled,
                    GameLaunchFailureCategory.Cancelled);
            }

            throw new InvalidOperationException("Unreachable");
        };
        int snapshots = 0;
        int started = 0;
        environment.Coordinator.SnapshotChanged += (_, _) => snapshots++;
        environment.Coordinator.PlayStarted += (_, _) => started++;

        Equal(GamePrimaryActionStatus.Started, environment.Coordinator.TryExecutePrimaryAction(), "Play doit démarrer avant la fermeture témoin.");
        await entered.Task;
        long attempt = environment.Coordinator.CurrentSnapshot.PlayAttemptId!.Value;
        environment.Coordinator.BeginShutdown();
        await environment.Coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        int snapshotsAfterShutdown = snapshots;
        delayedProgress?.Invoke(new GameLaunchProgress(attempt, GameLaunchPhase.StartingProcess));
        Equal(snapshotsAfterShutdown, snapshots, "Un callback tardif ne doit rien publier après fermeture.");
        Equal(0, started, "Une fermeture pendant ticket ne doit jamais annoncer un lancement réussi.");
        True(environment.Operations.IsShuttingDown, "Le coordinateur global doit observer la fermeture.");
    }

    private static void KeepLaunchingPreviewIsolated()
    {
        Equal(
            GamePreviewScenario.Launching,
            LauncherV2PreviewData.ResolveScenario(["--ui-v2", "--preview-state=launching"]),
            "Le scénario preview launching doit être reconnu sans runtime.");
        GameUiState state = LauncherV2PreviewData.CreateGame(GamePreviewScenario.Launching);
        Equal("Lancement…", state.PrimaryActionLabel, "Le preview doit montrer le libellé de lancement.");
        True(state.IsLaunchInProgress && !state.IsPrimaryActionEnabled, "Le preview launching doit rester statique et non déclenchable.");
        state.PrimaryActionCommand.Execute(null);
        Equal("Lancement…", state.PrimaryActionLabel, "La commande preview ne doit produire aucun effet de bord.");
    }

    private static async Task ValidateWpfPlayAndAuthenticationFlowAsync()
    {
        TaskCompletionSource completion = Signal();
        Thread thread = new(() => RunWpfHarness(completion))
        {
            IsBackground = true,
            Name = "AtlasGameLaunchWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void RunWpfHarness(TaskCompletionSource completion)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.UnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };

        _ = RunAsync();
        Dispatcher.Run();
        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }

        async Task RunAsync()
        {
            Application? application = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                await ValidateWpfFlowCoreAsync();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateWpfFlowCoreAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        client.WriteVersionMarker("wpf-play-v1");
        FakeLauncherAuthService authentication = new()
        {
            PrepareRestoreHandler = _ => Task.FromResult(new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.NoSession,
                null)),
            LoginHandler = (username, _, _) => Task.FromResult(
                FakeLauncherAuthService.CreateSession(username)),
            StatusHandler = _ => Task.FromResult(OnlineStatus()),
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>([])
        };
        FakeLaunchPlatform platform = new();
        FakeProcessStarter process = new();
        using LauncherRuntime runtime = CreateWpfRuntime(client, authentication, platform, process);
        await runtime.InitializeAsync();

        ShellUiState shell = LauncherV2RuntimePresentation.CreateShell(runtime);
        GameUiState game = LauncherV2RuntimePresentation.CreateGame(runtime.LocalClient);
        LauncherShellV2 window = new(
            shell,
            game,
            LauncherV2RuntimePresentation.CreateDashboard(),
            LauncherV2RuntimePresentation.CreateFriends())
        {
            Width = 1080,
            Height = 680,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false
        };
        AuthCommands authCommands = new(runtime);
        window.AttachAuthentication(authCommands);
        using AuthStateAdapter authAdapter = new(
            window.AuthState,
            shell,
            game,
            runtime.Session,
            window.Dispatcher);
        using PrimaryActionCommand primary = new(
            runtime.Game,
            window.OpenAuthenticationForPendingPlay);
        game.AttachPrimaryActionCommand(primary.Command);
        using GameStateAdapter gameAdapter = new(game, runtime.Game, window.Dispatcher);

        try
        {
            window.Show();
            window.Activate();
            await PumpAsync(DispatcherPriority.Loaded);
            GameViewV2 gameView = FindVisualChildren<GameViewV2>(window).Single();
            Button play = (Button)gameView.PrimaryActionFocusTarget;
            Button friends = Required<Button>(window, "FriendsButton");
            True(play.IsEnabled, "Jouer doit être actif dans WPF pour un client jouable déconnecté.");

            RaiseClick(friends);
            True(window.FriendsState.IsOpen, "Le drawer Amis témoin doit être ouvert.");
            True(ReferenceEquals(play.Command, primary.Command), "Le bouton WPF doit utiliser PrimaryActionCommand.");
            play.Command.Execute(play.CommandParameter);
            await PumpAsync(DispatcherPriority.DataBind);
            True(window.AuthState.IsOpen, "Jouer déconnecté doit ouvrir l'overlay de connexion.");
            True(!window.FriendsState.IsOpen, "L'overlay doit fermer le drawer Amis en premier.");
            True(runtime.Game.CurrentSnapshot.IsPlayPendingAuthentication, "La demande Play doit être en attente.");
            True(!play.IsEnabled, "Jouer ne doit pas être redéclenchable pendant l'overlay.");
            Equal(0, authentication.CreateGameTicketCalls, "Aucun ticket ne doit précéder la connexion.");
            Equal(0, process.Calls, "Aucun processus ne doit précéder la connexion.");

            Button close = Required<Button>(window.AuthenticationOverlay, "CloseButton");
            RaiseClick(close);
            await DelayAndPumpAsync(220);
            True(!runtime.Game.CurrentSnapshot.IsPlayPendingAuthentication, "Fermer l'overlay doit abandonner Play.");
            True(play.IsEnabled, "Jouer doit redevenir disponible après fermeture.");
            Equal(play, Keyboard.FocusedElement, "Le focus doit revenir à Jouer.");

            play.Command.Execute(play.CommandParameter);
            await PumpAsync(DispatcherPriority.DataBind);
            AuthOverlayViewV2 overlay = window.AuthenticationOverlay;
            Required<TextBox>(overlay, "LoginUsernameBox").Text = "WpfPlay";
            Required<PasswordBox>(overlay, "LoginPasswordBox").Password = "transient-password";
            await PumpAsync(DispatcherPriority.DataBind);
            RaiseClick(Required<Button>(overlay, "PrimaryAuthButton"));
            await WaitForAsync(() => process.Calls == 1);
            await runtime.Game.WaitForIdleAsync();
            await DelayAndPumpAsync(220);

            Equal(1, authentication.LoginCalls, "La connexion doit être soumise une fois.");
            Equal(1, authentication.CreateGameTicketCalls, "La reprise doit demander exactement un ticket.");
            Equal(1, authentication.GetStatusCalls, "La connexion ne doit produire qu'une actualisation du statut.");
            Equal(1, authentication.GetNewsCalls, "La connexion ne doit produire qu'une actualisation des actualités.");
            Equal(1, platform.SsoCalls, "La reprise doit écrire exactement un SSO simulé.");
            Equal(1, process.Calls, "La reprise doit démarrer exactement un processus simulé.");
            True(!window.AuthState.IsOpen, "Le succès doit fermer l'overlay.");
            Equal("Jouer", game.PrimaryActionLabel, "Le bouton doit revenir à Jouer après succès.");
            True(game.IsPrimaryActionEnabled, "Le bouton doit être réutilisable après succès.");
            True(!game.ShowsProgress, "Le lancement ne doit jamais afficher une barre de téléchargement.");
        }
        finally
        {
            gameAdapter.Dispose();
            primary.Dispose();
            authAdapter.Dispose();
            authCommands.Dispose();
            if (window.IsLoaded)
            {
                window.Close();
            }
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static LauncherRuntime CreateWpfRuntime(
        TemporaryClient client,
        FakeLauncherAuthService authentication,
        FakeLaunchPlatform platform,
        FakeProcessStarter process)
    {
        return new LauncherRuntime(new LauncherRuntimeDependencies
        {
            LoadSettings = () => client.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test",
            CreateAuthorizedHttpClient = _ => new HttpClient(new NoNetworkHandler()),
            CreateGameVerificationService = (_, _) => new RuntimeVerificationStub(),
            CreateGameMaintenanceService = (_, _) => new RuntimeMaintenanceStub(),
            CreateGameLaunchService = session => new GameLaunchService(session, platform, process),
            HasPlayableClient = GameInstallServices.HasPlayableClient
        });
    }

    private static FakeLauncherAuthService AuthenticatedService()
    {
        LauncherAuthSession session = FakeLauncherAuthService.CreateSession("TicketUser");
        return new FakeLauncherAuthService
        {
            Session = session,
            PrepareRestoreHandler = _ => Task.FromResult(new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.Restored,
                session)),
            EnsureFreshHandler = _ => Task.FromResult(true),
            GameTicketHandler = _ => Task.FromResult(Ticket()),
            StatusHandler = _ => Task.FromResult(OnlineStatus()),
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>([])
        };
    }

    private static GameTicket Ticket()
    {
        return new GameTicket(
            SecretTicket,
            DateTimeOffset.UtcNow.AddMinutes(1),
            "Dono1402",
            "1#1",
            1);
    }

    private static LauncherServerStatus OnlineStatus()
    {
        return new LauncherServerStatus(
            "Arthas",
            true,
            true,
            true,
            true,
            true,
            DateTimeOffset.UtcNow);
    }

    private static GameClientVerificationResult UpToDate()
    {
        return new GameClientVerificationResult(
            GameVerificationOutcome.UpToDate,
            GameAction.Play,
            GameUpdateKnowledge.Known,
            "remote-v1",
            0);
    }

    private static void AssertSnapshotContainsNoSecret(GameRuntimeSnapshot snapshot)
    {
        foreach (PropertyInfo property in typeof(GameRuntimeSnapshot).GetProperties())
        {
            if (property.PropertyType == typeof(string)
                && property.GetValue(snapshot) is string value)
            {
                True(!value.Contains(SecretTicket, StringComparison.Ordinal), $"Le snapshot expose un secret via {property.Name}.");
            }
        }
    }

    private static TaskCompletionSource Signal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Le scénario Play n'a pas atteint l'état attendu.");
            }

            await DelayAndPumpAsync(15);
        }
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement
    {
        return scope.FindName(name) as T
            ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");
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

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);
    }

    private static void LoadV2Resources(Application application)
    {
        foreach (string path in new[]
        {
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(path, UriKind.Relative)
            });
        }
    }

    private static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
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

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Aucun appel HTTP métier n'est autorisé dans le test Play.");
        }
    }
}

internal sealed class LaunchServiceEnvironment : IDisposable
{
    internal LaunchServiceEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "AtlasGameLaunchService", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, GameInstallServices.ClassicDirectoryName));
        Session = new FakeGameLaunchSession();
        Platform = new FakeLaunchPlatform();
        Process = new FakeProcessStarter();
        Service = new GameLaunchService(Session, Platform, Process);
    }

    internal string Root { get; }

    internal FakeGameLaunchSession Session { get; }

    internal FakeLaunchPlatform Platform { get; }

    internal FakeProcessStarter Process { get; }

    internal GameLaunchService Service { get; }

    internal Task<GameLaunchResult> LaunchAsync()
    {
        return Service.LaunchAsync(
            new GameLaunchRequest(1, Root, "frFR"),
            reportProgress: null,
            CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class FakeGameLaunchSession : IGameLaunchSession
{
    internal GameTicketAcquisitionResult Result { get; set; } =
        GameTicketAcquisitionResult.Success(new GameTicket(
            "HP-0123456789ABCDEF0123456789ABCDEF01234567",
            DateTimeOffset.UtcNow.AddMinutes(1),
            "Dono1402",
            "1#1",
            1));

    internal int Calls { get; private set; }

    internal Action<string>? EventSink { get; set; }

    public Task<GameTicketAcquisitionResult> AcquireGameTicketAsync(CancellationToken cancellationToken)
    {
        Calls++;
        EventSink?.Invoke("ticket");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result);
    }
}

internal sealed class FakeLaunchPlatform : IGameLaunchPlatform
{
    internal bool HasPlayableClientResult { get; set; } = true;

    internal bool IsGameRunningResult { get; set; }

    internal bool FileExistsResult { get; set; } = true;

    internal Exception? ConfigFailure { get; set; }

    internal Exception? SsoFailure { get; set; }

    internal Action? AfterSso { get; set; }

    internal Action<string>? EventSink { get; set; }

    internal int ConfigCalls { get; private set; }

    internal int SsoCalls { get; private set; }

    public bool HasPlayableClient(string installRoot) => HasPlayableClientResult;

    public bool IsGameRunning(string installRoot) => IsGameRunningResult;

    public bool FileExists(string path) => FileExistsResult;

    public string EnsureDefaultClientConfig(string installRoot, string locale)
    {
        ConfigCalls++;
        EventSink?.Invoke("config");
        if (ConfigFailure is not null)
        {
            throw ConfigFailure;
        }

        return Path.Combine(installRoot, GameInstallServices.ClassicDirectoryName, "WTF", "Config.wtf");
    }

    public void WriteSingleSignOn(GameTicket ticket, string locale)
    {
        SsoCalls++;
        EventSink?.Invoke("sso");
        if (SsoFailure is not null)
        {
            throw SsoFailure;
        }

        AfterSso?.Invoke();
    }
}

internal sealed class FakeProcessStarter : IGameProcessStarter
{
    internal bool StartResult { get; set; } = true;

    internal Exception? Failure { get; set; }

    internal Action<string>? EventSink { get; set; }

    internal int Calls { get; private set; }

    internal ProcessStartInfo? StartInfo { get; private set; }

    public bool Start(ProcessStartInfo startInfo)
    {
        Calls++;
        StartInfo = startInfo;
        EventSink?.Invoke("process");
        if (Failure is not null)
        {
            throw Failure;
        }

        return StartResult;
    }
}

internal sealed class FakeGameLaunchService : IGameLaunchService
{
    internal Func<
        GameLaunchRequest,
        Action<GameLaunchProgress>?,
        CancellationToken,
        Task<GameLaunchResult>>? Handler { get; set; }

    internal Func<GameLaunchRequest, GameLaunchResult> ResultFactory { get; set; } =
        request => new GameLaunchResult(request.AttemptId, GameLaunchOutcome.Started);

    internal int Calls { get; private set; }

    internal List<long> AttemptIds { get; } = [];

    public Task<GameLaunchResult> LaunchAsync(
        GameLaunchRequest request,
        Action<GameLaunchProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        Calls++;
        AttemptIds.Add(request.AttemptId);
        return Handler?.Invoke(request, reportProgress, cancellationToken)
            ?? Task.FromResult(ResultFactory(request));
    }
}

internal sealed class PlayVerificationStub : IGameClientVerificationService
{
    internal Func<
        LauncherSettings,
        bool,
        Action<GameVerificationProgress>?,
        CancellationToken,
        Task<GameClientVerificationResult>>? Handler { get; set; }

    public Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        return Handler?.Invoke(settings, reportFileProgress, reportProgress, cancellationToken)
            ?? Task.FromResult(new GameClientVerificationResult(
                GameVerificationOutcome.UpToDate,
                GameAction.Play,
                GameUpdateKnowledge.Known,
                "remote-v1",
                0));
    }
}

internal sealed class PlayRuntimeEnvironment : IDisposable
{
    private readonly LauncherOperationCoordinator _operations = new();

    internal PlayRuntimeEnvironment(bool authenticated)
    {
        Root = Path.Combine(Path.GetTempPath(), "AtlasPlayRuntime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Settings = new LauncherSettings
        {
            InstallPath = Root,
            ManifestUrl = "https://atlas.test/manifest.json",
            GameLocale = "frFR",
            AutomaticLauncherUpdates = false,
            CloseLauncherOnGameStart = false
        };
        LocalState = new GameClientLocalState(
            Root,
            "frFR",
            IsPlayable: true,
            InstalledVersion: "local-v1",
            GameUpdateKnowledge.Unknown);
        Authenticated = authenticated;
        SessionState = authenticated
            ? LauncherSessionState.Authenticated
            : LauncherSessionState.SignedOut;
        Verification = new PlayVerificationStub();
        Maintenance = new RuntimeMaintenanceStub();
        Launch = new FakeGameLaunchService();
        Coordinator = new GameRuntimeCoordinator(
            Verification,
            _operations,
            Settings,
            LocalState,
            () => Authenticated,
            Logs.Add,
            _ => true,
            TimeProvider.System,
            Maintenance,
            () => LocalState,
            Launch,
            () => SessionState);
        Coordinator.RefreshAuthenticationAvailability();
    }

    internal string Root { get; }

    internal LauncherSettings Settings { get; }

    internal GameClientLocalState LocalState { get; set; }

    internal bool Authenticated { get; set; }

    internal LauncherSessionState SessionState { get; set; }

    internal PlayVerificationStub Verification { get; }

    internal RuntimeMaintenanceStub Maintenance { get; }

    internal FakeGameLaunchService Launch { get; }

    internal LauncherOperationCoordinator Operations => _operations;

    internal GameRuntimeCoordinator Coordinator { get; }

    internal List<string> Logs { get; } = [];

    public void Dispose()
    {
        Coordinator.BeginShutdown();
        Coordinator.Dispose();
        _operations.Dispose();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
