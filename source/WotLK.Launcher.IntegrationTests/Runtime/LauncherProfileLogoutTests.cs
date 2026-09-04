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
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class LauncherProfileLogoutTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeSecretFreeProfileModels();
        CharacterizeProfilePreviewIsolation();
        await DelegateLogoutToTheExistingAuthenticationServiceAsync();
        await PreserveLogoutFailureSemanticsAsync();
        await IgnoreLateLogoutAfterShutdownAsync();
        await EnforceOperationCompatibilityAsync();
        await CancelPendingPlayBeforeLogoutAsync();
        await ApplySuccessfulLogoutAcrossRuntimeAsync();
        await CharacterizeRealWpfProfileMenuAsync();
        Console.WriteLine("Profile and logout V2 integration OK (02F.4).");
        return 0;
    }

    private static void CharacterizeSecretFreeProfileModels()
    {
        Type[] types =
        [
            typeof(ProfileRuntimeSnapshot),
            typeof(ProfileViewState),
            typeof(ProfileUiState)
        ];
        string[] forbiddenNames =
        [
            "AccessToken",
            "RefreshToken",
            "Password",
            "Ticket",
            "Authorization",
            "EmailAddress"
        ];
        foreach (Type type in types)
        {
            string[] propertyNames = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(property => property.Name)
                .ToArray();
            foreach (string forbiddenName in forbiddenNames)
            {
                True(
                    !propertyNames.Contains(forbiddenName, StringComparer.OrdinalIgnoreCase),
                    $"{type.Name} ne doit pas exposer {forbiddenName}.");
            }
        }

        ProfileRuntimeSnapshot verified = Snapshot(
            LauncherSessionState.Authenticated,
            failure: LauncherSessionFailureCategory.None,
            verified: true);
        ProfileViewState verifiedView = ProfileStateAdapter.Project(verified);
        Equal("Dono1402", verifiedView.Username, "Le nom réel doit être projeté.");
        Equal("D", verifiedView.Initial, "L'initiale réelle doit être projetée.");
        Equal("Adresse e-mail vérifiée", verifiedView.EmailStatusText, "Le statut e-mail vérifié est incorrect.");
        True(!verifiedView.EmailStatusText.Contains('@'), "Le panneau ne doit pas afficher l'adresse complète.");

        ProfileViewState unverifiedView = ProfileStateAdapter.Project(
            verified with { IsEmailVerified = false });
        Equal(
            "Adresse e-mail non vérifiée",
            unverifiedView.EmailStatusText,
            "Le statut e-mail non vérifié est incorrect.");
        True(unverifiedView.CanLogout, "L'e-mail non vérifié ne doit pas bloquer la déconnexion.");

        ProfileViewState busyView = ProfileStateAdapter.Project(
            verified with
            {
                CanLogout = false,
                LogoutUnavailableReason = "Une opération est en cours."
            });
        Equal("Une opération est en cours.", busyView.LogoutToolTip, "La raison d'indisponibilité doit être courte.");
        ProfileViewState locallySignedOutFailure = ProfileStateAdapter.Project(
            Snapshot(
                LauncherSessionState.SignedOut,
                LauncherSessionFailureCategory.Network,
                verified: true));
        Equal(
            string.Empty,
            locallySignedOutFailure.ErrorMessage,
            "Une session locale supprimée ne doit pas afficher qu'elle reste active.");
    }

    private static void CharacterizeProfilePreviewIsolation()
    {
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-profile=signed-in"]),
            "preview-profile sans --ui-v2 doit être refusé avant composition.");
        Equal(
            LauncherStartupMode.UiV2ProfilePreview,
            App.ResolveStartupMode(["--ui-v2", "--preview-profile=email-unverified"]),
            "preview-profile doit utiliser sa branche isolée.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode([
                "--ui-v2",
                "--preview-auth=login",
                "--preview-profile=signed-in"
            ]),
            "Deux previews d'overlay ne doivent jamais être composés ensemble.");

        foreach (ProfilePreviewScenario scenario in Enum.GetValues<ProfilePreviewScenario>())
        {
            ProfileUiState state = LauncherV2PreviewData.CreateProfile(scenario);
            True(state.Current.IsAuthenticated, $"Le preview {scenario} doit rester fictivement connecté.");
            True(
                scenario == ProfilePreviewScenario.LoggingOut
                    ? !state.LogoutCommand.CanExecute(null)
                    : state.LogoutCommand.CanExecute(null),
                $"La commande fictive du preview {scenario} est incohérente.");
        }
    }

    private static async Task DelegateLogoutToTheExistingAuthenticationServiceAsync()
    {
        List<string> order = [];
        FakeLauncherAuthService authentication = AuthenticatedService();
        authentication.LogoutHandler = _ =>
        {
            order.Add("remote-revocation");
            authentication.InvalidateLocalSession();
            order.Add("local-invalidation");
            return Task.CompletedTask;
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        await RestoreAuthenticatedAsync(session);

        LauncherSessionStartResult start = session.TryLogout(CancellationToken.None);
        LauncherSessionCompletion result = await RequiredCompletion(start);

        Equal(LauncherSessionCompletionStatus.Succeeded, result.Status, "La déconnexion déléguée doit réussir.");
        Equal(LauncherSessionState.SignedOut, result.Snapshot.State, "La session doit devenir SignedOut.");
        Equal(1, authentication.LogoutCalls, "Le service legacy doit être appelé exactement une fois.");
        Equal(1, authentication.InvalidateLocalSessionCalls, "Le coordinateur ne doit pas dupliquer le nettoyage local.");
        Equal("remote-revocation", order[0], "La révocation distante doit précéder le nettoyage local.");
        Equal("local-invalidation", order[1], "Le nettoyage local doit terminer le contrat legacy.");

        authentication.PrepareRestoreHandler = _ => Task.FromResult(
            new LauncherAuthRestoreAttempt(LauncherAuthRestoreOutcome.NoSession, null));
        using LauncherSessionCoordinator restarted = new(authentication, lifetime.Token, _ => { });
        LauncherSessionRestoreResult restored = await restarted.RestoreOnceAsync();
        Equal(
            LauncherSessionRestoreStatus.NoSession,
            restored.Status,
            "Une session nettoyée ne doit pas être restaurée au redémarrage suivant.");
    }

    private static async Task PreserveLogoutFailureSemanticsAsync()
    {
        (Exception Failure, LauncherSessionFailureCategory Category)[] failures =
        [
            (new HttpRequestException("access-token must remain secret"), LauncherSessionFailureCategory.Network),
            (new TaskCanceledException("refresh-token must remain secret"), LauncherSessionFailureCategory.Timeout),
            (new LauncherAuthException("server ticket detail", HttpStatusCode.BadRequest), LauncherSessionFailureCategory.ServerRejected),
            (new IOException("secure session path"), LauncherSessionFailureCategory.SecureStorage),
            (new InvalidOperationException("password must remain secret"), LauncherSessionFailureCategory.Unknown)
        ];

        foreach ((Exception failure, LauncherSessionFailureCategory category) in failures)
        {
            List<string> logs = [];
            FakeLauncherAuthService authentication = AuthenticatedService();
            authentication.LogoutHandler = _ => Task.FromException(failure);
            using CancellationTokenSource lifetime = new();
            using LauncherSessionCoordinator session = new(authentication, lifetime.Token, logs.Add);
            await RestoreAuthenticatedAsync(session);

            LauncherSessionCompletion result = await RequiredCompletion(
                session.TryLogout(CancellationToken.None));

            Equal(LauncherSessionCompletionStatus.Failed, result.Status, $"{category} doit produire Failed.");
            Equal(LauncherSessionState.Authenticated, result.Snapshot.State, $"{category} ne doit pas inventer SignedOut.");
            Equal(category, result.Snapshot.FailureCategory, $"La catégorie {category} doit être conservée.");
            Equal("Dono1402", result.Snapshot.Username, "L'identité doit rester présente après un échec total.");
            True(authentication.Session is not null, "Une session encore valide ne doit pas être effacée par le coordinateur.");
            string joinedLogs = string.Join(Environment.NewLine, logs);
            foreach (string secret in new[] { "access-token", "refresh-token", "password", "ticket" })
            {
                True(!joinedLogs.Contains(secret, StringComparison.OrdinalIgnoreCase), "Le journal de déconnexion contient un secret.");
            }
        }

        FakeLauncherAuthService locallyCleared = AuthenticatedService();
        locallyCleared.LogoutHandler = _ =>
        {
            locallyCleared.InvalidateLocalSession();
            return Task.FromException(new HttpRequestException("remote confirmation unavailable"));
        };
        using CancellationTokenSource localLifetime = new();
        using LauncherSessionCoordinator localSession = new(locallyCleared, localLifetime.Token, _ => { });
        await RestoreAuthenticatedAsync(localSession);
        LauncherSessionCompletion localResult = await RequiredCompletion(
            localSession.TryLogout(CancellationToken.None));
        Equal(LauncherSessionState.SignedOut, localResult.Snapshot.State, "Un nettoyage local réel doit rester SignedOut.");
        Equal(LauncherSessionFailureCategory.Network, localResult.Snapshot.FailureCategory, "L'échec de confirmation doit rester visible.");
        True(locallyCleared.Session is null, "L'interface ne doit jamais prétendre que la session locale supprimée existe encore.");
    }

    private static async Task IgnoreLateLogoutAfterShutdownAsync()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = AuthenticatedService();
        authentication.LogoutHandler = async _ =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            authentication.InvalidateLocalSession();
        };
        using CancellationTokenSource lifetime = new();
        LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        await RestoreAuthenticatedAsync(session);
        int notifications = 0;
        session.SnapshotChanged += (_, _) => notifications++;

        LauncherSessionStartResult start = session.TryLogout(CancellationToken.None);
        await started.Task;
        int beforeDispose = notifications;
        session.Dispose();
        session.Dispose();
        release.TrySetResult();
        LauncherSessionCompletion result = await RequiredCompletion(start);

        Equal(LauncherSessionCompletionStatus.Superseded, result.Status, "Un résultat tardif après fermeture doit être obsolète.");
        Equal(beforeDispose, notifications, "Aucun snapshot ne doit atteindre WPF après désinscription.");
        True(await session.WaitForIdleAsync(TimeSpan.FromSeconds(1)), "La tâche tardive doit rester observée.");
        Equal(1, authentication.LogoutCalls, "La fermeture ne doit pas relancer la déconnexion.");
    }

    private static async Task EnforceOperationCompatibilityAsync()
    {
        await using ProfileRuntimeEnvironment environment = await ProfileRuntimeEnvironment.CreateAsync();
        LauncherOperationKind[] maintenanceKinds =
        [
            LauncherOperationKind.GameInstall,
            LauncherOperationKind.GameUpdate,
            LauncherOperationKind.GameRepair,
            LauncherOperationKind.Verify,
            LauncherOperationKind.Addons,
            LauncherOperationKind.LauncherAutoUpdate
        ];

        foreach (LauncherOperationKind kind in maintenanceKinds)
        {
            LauncherOperationStartResult operation = environment.Runtime.Operations.TryBegin(
                kind,
                canUserCancel: kind != LauncherOperationKind.Verify,
                clientIsPlayable: true);
            True(operation.IsStarted, $"L'opération témoin {kind} doit démarrer.");
            True(!environment.Runtime.Profile.CurrentSnapshot.CanLogout, $"Logout doit être désactivé pendant {kind}.");
            ProfileLogoutStartResult refused = environment.Runtime.Profile.TryLogout();
            True(!refused.IsStarted, $"Logout doit être refusé immédiatement pendant {kind}.");
            True(!operation.Lease!.CancellationToken.IsCancellationRequested, $"Logout ne doit pas annuler {kind}.");
            operation.Lease.Complete();
            True(environment.Runtime.Profile.CurrentSnapshot.CanLogout, $"Logout doit redevenir disponible après {kind}.");
        }

        LauncherOperationStartResult play = environment.Runtime.Operations.TryBeginPlay(clientIsPlayable: true);
        True(play.IsStarted, "Le lancement témoin doit démarrer.");
        Equal(
            ProfileLogoutStartStatus.RejectedByCompatibility,
            environment.Runtime.Profile.TryLogout().Status,
            "Logout doit être refusé pendant ticket, SSO ou lancement.");
        True(!play.Lease!.CancellationToken.IsCancellationRequested, "Logout ne doit pas tuer un jeu déjà en lancement.");
        play.Lease.Complete();
        Equal(0, environment.Authentication.LogoutCalls, "Un refus ne doit appeler aucun endpoint.");
    }

    private static async Task CancelPendingPlayBeforeLogoutAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        client.WriteVersionMarker("local-v1");
        FakeLauncherAuthService authentication = AuthenticatedService();
        authentication.LogoutHandler = _ =>
        {
            authentication.InvalidateLocalSession();
            return Task.CompletedTask;
        };
        using CancellationTokenSource lifetime = new();
        using LauncherOperationCoordinator operations = new();
        using LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        await RestoreAuthenticatedAsync(session);
        GameClientLocalState local = new GameClientStateReader().Read(client.Settings);
        FakeGameLaunchService launch = new();
        using GameRuntimeCoordinator game = new(
            new RuntimeVerificationStub(),
            operations,
            client.Settings,
            local,
            isAuthenticated: () => false,
            writeLog: _ => { },
            hasPlayableClient: GameInstallServices.HasPlayableClient,
            maintenanceService: new RuntimeMaintenanceStub(),
            readLocalState: () => local,
            launchService: launch,
            getSessionState: () => LauncherSessionState.SignedOut,
            processMonitor: new FakeGameProcessMonitor());
        using LauncherDashboardCoordinator dashboard = new(
            authentication,
            lifetime.Token,
            _ => { });
        using LauncherProfileCoordinator profile = new(session, operations, game, dashboard);

        Equal(
            GamePrimaryActionStatus.Unauthenticated,
            game.TryExecutePrimaryAction(),
            "Le clic Jouer déconnecté doit créer une unique attente d'authentification.");
        True(game.CurrentSnapshot.IsPlayPendingAuthentication, "La demande Play témoin doit être en attente.");
        True(profile.CurrentSnapshot.CanLogout, "Une attente Play doit pouvoir être abandonnée avant déconnexion.");

        LauncherOperationLease verification = operations.TryBegin(
            LauncherOperationKind.Verify,
            canUserCancel: false,
            clientIsPlayable: true).Lease!;
        True(!profile.CurrentSnapshot.CanLogout, "Verify doit rester prioritaire même avec un Play en attente.");
        Equal(
            ProfileLogoutStartStatus.RejectedByCompatibility,
            profile.TryLogout().Status,
            "La déconnexion doit être refusée sans abandonner Play lorsqu'une vérification coexiste.");
        True(game.CurrentSnapshot.IsPlayPendingAuthentication, "Un refus ne doit pas consommer la demande Play.");
        True(!verification.CancellationToken.IsCancellationRequested, "Le refus ne doit pas annuler Verify.");
        verification.Complete();
        True(profile.CurrentSnapshot.CanLogout, "Logout doit redevenir possible après Verify.");

        ProfileLogoutStartResult logout = profile.TryLogout();
        True(logout.IsStarted, "La déconnexion doit démarrer après abandon atomique du Play en attente.");
        LauncherSessionCompletion result = await logout.Completion!;
        Equal(LauncherSessionState.SignedOut, result.Snapshot.State, "La déconnexion après Play en attente doit réussir.");
        True(!game.CurrentSnapshot.IsPlayPendingAuthentication, "La demande Play ne doit pas survivre à la déconnexion.");
        Equal(0, launch.Calls, "Une demande abandonnée ne doit jamais lancer Arctium.");
        Equal(0, authentication.CreateGameTicketCalls, "Une demande abandonnée ne doit créer aucun ticket.");
    }

    private static async Task ApplySuccessfulLogoutAcrossRuntimeAsync()
    {
        await using ProfileRuntimeEnvironment environment = await ProfileRuntimeEnvironment.CreateAsync();
        DashboardSnapshot before = environment.Runtime.Dashboard.CurrentSnapshot;
        True(before.RealmState == DashboardRealmState.Online && before.HasPatchNote, "Le dashboard témoin doit contenir des données réelles.");
        int statusCalls = environment.Authentication.GetStatusCalls;
        int newsCalls = environment.Authentication.GetNewsCalls;
        TaskCompletionSource logoutStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseLogout = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Authentication.LogoutHandler = async _ =>
        {
            logoutStarted.TrySetResult();
            await releaseLogout.Task.ConfigureAwait(false);
            environment.Authentication.InvalidateLocalSession();
        };

        ProfileLogoutStartResult start = environment.Runtime.Profile.TryLogout();
        True(start.IsStarted, "Logout doit démarrer depuis une session restaurée.");
        await logoutStarted.Task;
        Equal(ProfileLogoutStartStatus.Busy, environment.Runtime.Profile.TryLogout().Status, "Le double clic doit être refusé.");
        Equal(
            DashboardRefreshStartStatus.NoSession,
            environment.Runtime.Dashboard.TryRefresh(),
            "Aucune nouvelle requête dashboard ne doit démarrer pendant la déconnexion.");
        releaseLogout.TrySetResult();
        LauncherSessionCompletion result = await start.Completion!;

        Equal(LauncherSessionState.SignedOut, result.Snapshot.State, "La session runtime doit être SignedOut.");
        True(!environment.Runtime.Profile.CurrentSnapshot.IsAuthenticated, "L'identité profil doit être retirée.");
        Equal(string.Empty, environment.Runtime.Profile.CurrentSnapshot.Username, "Le nom ne doit pas rester dans le profil.");
        True(!environment.Runtime.Game.CanVerify, "La réparation complète doit être réévaluée sans session.");
        DashboardSnapshot after = environment.Runtime.Dashboard.CurrentSnapshot;
        Equal(DashboardRealmState.Unavailable, after.RealmState, "La déconnexion doit rendre le dashboard neutre.");
        Equal(DashboardFailureCategory.NoSession, after.FailureCategory, "Le dashboard ne doit pas inventer RealmOffline.");
        True(after.IsStale && after.HasPatchNote, "Les dernières données doivent rester visibles comme obsolètes.");
        Equal(statusCalls, environment.Authentication.GetStatusCalls, "Aucun statut authentifié ne doit repartir après déconnexion.");
        Equal(newsCalls, environment.Authentication.GetNewsCalls, "Aucune note authentifiée ne doit repartir après déconnexion.");
        Equal(DashboardRefreshStartStatus.NoSession, environment.Runtime.Dashboard.TryRefresh(), "Le dashboard doit refuser immédiatement sans session.");
    }

    private static async Task CharacterizeRealWpfProfileMenuAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion))
        {
            IsBackground = true,
            Name = "AtlasProfileLogoutWpfHarness"
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
                await ValidateLoggingOutProfilePreviewAsync();
                await ValidateProfileMenuInWpfAsync();
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

    private static async Task ValidateLoggingOutProfilePreviewAsync()
    {
        LauncherShellV2 preview = new(
            GamePreviewScenario.Ready,
            ProfilePreviewScenario.LoggingOut)
        {
            Width = 1080,
            Height = 680,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        preview.Show();
        try
        {
            await DelayAndPumpAsync(180);
            True(!preview.HasRealAuthenticationAttached, "Le preview profil ne doit attacher aucun service réel.");
            Equal(
                ShellOverlayKind.Profile,
                preview.CurrentOverlay,
                "Le preview logging-out doit ouvrir le panneau malgré son état busy fictif.");
            Equal("Déconnexion…", preview.ProfileState.Current.LogoutLabel, "Le preview busy doit être visible.");
        }
        finally
        {
            preview.Close();
            await PumpAsync(DispatcherPriority.Background);
        }

        LauncherShellV2 unverifiedPreview = new(
            GamePreviewScenario.Ready,
            ProfilePreviewScenario.EmailUnverified)
        {
            Width = 1080,
            Height = 680,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        unverifiedPreview.Show();
        try
        {
            await DelayAndPumpAsync(180);
            Equal(
                "Adresse e-mail non vérifiée",
                unverifiedPreview.ProfileState.Current.EmailStatusText,
                "Le preview WPF doit exposer l'e-mail non vérifié sans le bloquer.");
            True(
                unverifiedPreview.ProfileState.LogoutCommand.CanExecute(null),
                "L'e-mail non vérifié ne doit pas désactiver Déconnexion.");
        }
        finally
        {
            unverifiedPreview.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateProfileMenuInWpfAsync()
    {
        await using ProfileRuntimeEnvironment environment = await ProfileRuntimeEnvironment.CreateAsync(
            initialize: false);
        ShellUiState shell = LauncherV2RuntimePresentation.CreateShell(environment.Runtime);
        GameUiState game = LauncherV2RuntimePresentation.CreateGame(environment.Runtime.LocalClient);
        DashboardUiState dashboard = LauncherV2RuntimePresentation.CreateDashboard();
        ProfileUiState profile = new();
        LauncherShellV2 window = new(
            shell,
            game,
            dashboard,
            LauncherV2RuntimePresentation.CreateFriends(),
            profile)
        {
            Width = 1080,
            Height = 680,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = true
        };
        using AuthCommands authCommands = new(environment.Runtime);
        window.AttachAuthentication(authCommands);
        using AuthStateAdapter authAdapter = new(
            window.AuthState,
            shell,
            game,
            environment.Runtime.Session,
            window.Dispatcher);
        using LogoutCommand logoutCommand = new(environment.Runtime.Profile);
        profile.AttachLogoutCommand(logoutCommand.Command);
        using ProfileStateAdapter profileAdapter = new(
            profile,
            game,
            environment.Runtime.Profile,
            window.Dispatcher);
        using PrimaryActionCommand primary = new(
            environment.Runtime.Game,
            window.OpenAuthenticationForPendingPlay);
        game.AttachPrimaryActionCommand(primary.Command);
        using GameStateAdapter gameAdapter = new(
            game,
            environment.Runtime.Game,
            window.Dispatcher);
        using DashboardStateAdapter dashboardAdapter = new(
            dashboard,
            environment.Runtime.Dashboard,
            window.Dispatcher);

        window.Show();
        try
        {
            await environment.Runtime.InitializeAsync();
            await WaitForAsync(() => shell.IsAuthenticated && profile.Current.CanLogout);
            Button profileButton = Required<Button>(window, "ProfileButton");
            Button friendsButton = Required<Button>(window, "FriendsButton");
            Button logoutButton = Required<Button>(window.ProfileOverlay, "LogoutButton");
            Button closeButton = Required<Button>(window.ProfileOverlay, "CloseProfileButton");

            Equal("Dono1402", shell.ProfileToolTip, "Le profil connecté doit exposer le nom en info-bulle.");
            RaiseClick(profileButton);
            await DelayAndPumpAsync(180);
            Equal(ShellOverlayKind.Profile, window.CurrentOverlay, "Le profil connecté doit ouvrir le menu et non l'auth.");
            Equal("Dono1402", profile.Current.Username, "Le panneau doit afficher l'identité réelle.");
            Equal("D", profile.Current.Initial, "Le panneau doit afficher l'initiale réelle.");
            Equal("Adresse e-mail vérifiée", profile.Current.EmailStatusText, "Le statut e-mail doit être réel.");
            True(
                Descendants<TextBlock>(window.ProfileOverlay)
                    .All(text => !text.Text.Contains('@')),
                "Le panneau WPF ne doit afficher aucune adresse complète.");
            True(
                ReferenceEquals(Keyboard.FocusedElement, logoutButton),
                "Le premier contrôle interactif disponible doit recevoir le focus.");
            logoutButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            await PumpAsync(DispatcherPriority.Input);
            True(
                window.ProfileOverlay.ContainsTarget(Keyboard.FocusedElement as DependencyObject),
                "Tab doit rester dans le menu profil.");
            closeButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            await PumpAsync(DispatcherPriority.Input);
            True(
                window.ProfileOverlay.ContainsTarget(Keyboard.FocusedElement as DependencyObject),
                "Shift+Tab doit rester dans le menu profil.");

            RaisePreviewKey(window, Key.Escape);
            await DelayAndPumpAsync(180);
            True(window.ProfileOverlay.IsFullyClosed, "Échap doit retirer le panneau et son hit-test.");
            True(ReferenceEquals(Keyboard.FocusedElement, profileButton), "Le focus doit revenir au bouton profil.");

            RaiseClick(profileButton);
            RaiseClick(profileButton);
            RaiseClick(profileButton);
            await DelayAndPumpAsync(180);
            True(profile.IsOpen && !window.ProfileOverlay.IsFullyClosed, "Des transitions opposées rapides doivent finir dans un état stable.");
            RaisePreviewMouseDown(window);
            await DelayAndPumpAsync(180);
            True(window.ProfileOverlay.IsFullyClosed, "Un clic extérieur doit fermer le panneau sans voile.");

            RaiseClick(profileButton);
            window.OpenAuthenticationForPendingPlay();
            Equal(ShellOverlayKind.Authentication, window.CurrentOverlay, "Ouvrir Auth doit fermer Profil.");
            True(!profile.IsOpen, "Le menu profil ne doit pas rester sous AuthOverlay.");
            RaisePreviewKey(window, Key.Escape);
            await DelayAndPumpAsync(180);

            RaiseClick(friendsButton);
            Equal(ShellOverlayKind.Friends, window.CurrentOverlay, "Le drawer Amis doit s'ouvrir seul.");
            RaiseClick(profileButton);
            Equal(ShellOverlayKind.Profile, window.CurrentOverlay, "Ouvrir Profil doit fermer Amis.");
            True(!window.FriendsState.IsOpen, "FriendsDrawer ne doit pas rester ouvert derrière Profil.");
            await DelayAndPumpAsync(180);
            True(friendsButton.Focusable, "Le bouton Amis doit rester dans la navigation après fermeture de son drawer.");
            RaiseClick(friendsButton);
            Equal(ShellOverlayKind.Friends, window.CurrentOverlay, "Ouvrir Amis doit fermer Profil.");
            True(!profile.IsOpen, "Le menu profil doit être fermé avant le drawer Amis.");
            await DelayAndPumpAsync(180);
            True(profileButton.Focusable, "Le bouton Profil doit rester dans la navigation après fermeture de son menu.");
            RaiseClick(friendsButton);

            LauncherOperationLease addons = environment.Runtime.Operations.TryBegin(
                LauncherOperationKind.Addons,
                canUserCancel: true).Lease!;
            await WaitForAsync(() => !profile.Current.CanLogout);
            RaiseClick(profileButton);
            await DelayAndPumpAsync(180);
            True(!logoutButton.IsEnabled, "Déconnexion doit être désactivée pendant une opération authentifiée.");
            Equal("Une opération est en cours.", profile.Current.LogoutToolTip, "Le refus doit être expliqué sans détail technique.");
            ExecuteBoundCommand(logoutButton);
            Equal(0, environment.Authentication.LogoutCalls, "Un bouton désactivé ne doit pas annuler Addons.");
            True(!addons.CancellationToken.IsCancellationRequested, "Déconnexion ne doit jamais annuler Addons.");
            addons.Complete();
            await WaitForAsync(() => profile.Current.CanLogout && logoutButton.IsEnabled);

            environment.Authentication.LogoutHandler = _ => Task.FromException(
                new HttpRequestException("secret access-token"));
            int logoutCallsBeforeFailure = environment.Authentication.LogoutCalls;
            ExecuteBoundCommand(logoutButton);
            await WaitForAsync(() => environment.Authentication.LogoutCalls > logoutCallsBeforeFailure);
            await DelayAndPumpAsync(240);
            True(
                !profile.Current.IsLoggingOut && profile.Current.ErrorMessage.Length > 0,
                "L'échec doit être projeté dans WPF. "
                + $"Session={environment.Runtime.Session.CurrentSnapshot.State}; "
                + $"SessionFailure={environment.Runtime.Session.CurrentSnapshot.FailureCategory}; "
                + $"ProfileFailure={environment.Runtime.Profile.CurrentSnapshot.FailureCategory}; "
                + $"CanLogout={profile.Current.CanLogout}.");
            True(profile.IsOpen, "Un échec total doit laisser le menu disponible pour réessayer.");
            True(profile.Current.IsAuthenticated, "Une session conservée doit rester affichée comme connectée.");
            True(!profile.Current.ErrorMessage.Contains("Http", StringComparison.OrdinalIgnoreCase), "L'interface ne doit pas montrer l'exception brute.");

            TaskCompletionSource logoutStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseLogout = new(TaskCreationOptions.RunContinuationsAsynchronously);
            environment.Authentication.LogoutHandler = async _ =>
            {
                logoutStarted.TrySetResult();
                await releaseLogout.Task.ConfigureAwait(false);
                environment.Authentication.InvalidateLocalSession();
            };
            ExecuteBoundCommand(logoutButton);
            await logoutStarted.Task;
            await WaitForAsync(() => profile.Current.IsLoggingOut);
            Equal("Déconnexion…", profile.Current.LogoutLabel, "L'état busy doit être explicite.");
            True(!logoutButton.IsEnabled, "Le second clic doit être refusé pendant la déconnexion.");
            int callsDuringBusy = environment.Authentication.LogoutCalls;
            ExecuteBoundCommand(logoutButton);
            RaiseClick(friendsButton);
            Equal(callsDuringBusy, environment.Authentication.LogoutCalls, "Aucune seconde requête ne doit être mise en attente.");
            Equal(ShellOverlayKind.Profile, window.CurrentOverlay, "Un autre overlay doit être refusé pendant la déconnexion.");
            releaseLogout.TrySetResult();
            await WaitForAsync(() => !shell.IsAuthenticated);
            await DelayAndPumpAsync(180);

            Equal(ShellOverlayKind.None, window.CurrentOverlay, "Le menu doit se fermer après succès.");
            Equal("Se connecter", shell.ProfileToolTip, "Le bouton profil doit revenir à l'état déconnecté.");
            Equal(DashboardRealmState.Unavailable, environment.Runtime.Dashboard.CurrentSnapshot.RealmState, "Le dashboard doit devenir neutre.");
            True(environment.Runtime.Dashboard.CurrentSnapshot.IsStale, "Les données dashboard doivent rester marquées obsolètes.");

            GameViewV2 gameView = Required<GameViewV2>(window, "GameView");
            Button playButton = gameView.PrimaryActionFocusTarget as Button
                ?? throw new InvalidOperationException("Le bouton principal Jeu est absent.");
            ExecuteBoundCommand(playButton);
            await WaitForAsync(() => window.AuthState.IsOpen);
            Equal(ShellOverlayKind.Authentication, window.CurrentOverlay, "Jouer après déconnexion doit ouvrir AuthOverlayV2.");
            True(environment.Runtime.Game.CurrentSnapshot.IsPlayPendingAuthentication, "Une seule demande Play doit être conservée.");
            Equal(0, environment.Launch.Calls, "Le jeu ne doit pas démarrer avant authentification.");
            Equal(0, environment.Authentication.CreateGameTicketCalls, "Aucun ticket ne doit être créé avant authentification.");
            RaisePreviewKey(window, Key.Escape);
            await DelayAndPumpAsync(180);
            True(!environment.Runtime.Game.CurrentSnapshot.IsPlayPendingAuthentication, "Fermer l'auth doit abandonner la demande Play.");

            RaiseClick(profileButton);
            await WaitForAsync(() => window.AuthState.IsOpen);
            Equal(ShellOverlayKind.Authentication, window.CurrentOverlay, "Le profil déconnecté doit ouvrir l'authentification.");
        }
        finally
        {
            window.Close();
            environment.Runtime.BeginShutdown();
            await environment.Runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(2));
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static ProfileRuntimeSnapshot Snapshot(
        LauncherSessionState state,
        LauncherSessionFailureCategory failure,
        bool verified)
    {
        return new ProfileRuntimeSnapshot(
            Sequence: 1,
            SessionSequence: 1,
            LogoutAttemptId: state == LauncherSessionState.LoggingOut ? 1 : null,
            SessionState: state,
            Username: state is LauncherSessionState.Authenticated or LauncherSessionState.LoggingOut
                ? "Dono1402"
                : string.Empty,
            IsEmailVerified: verified,
            CanLogout: state == LauncherSessionState.Authenticated,
            LogoutUnavailableReason: state == LauncherSessionState.Authenticated
                ? string.Empty
                : "Déconnexion indisponible.",
            FailureCategory: failure);
    }

    internal static FakeLauncherAuthService AuthenticatedService(
        bool emailVerified = true)
    {
        FakeLauncherAuthService authentication = new()
        {
            Session = FakeLauncherAuthService.CreateSession(
                "Dono1402",
                "dono@example.test",
                emailVerified)
        };
        authentication.PrepareRestoreHandler = _ => Task.FromResult(
            new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.Restored,
                authentication.Session));
        authentication.EnsureFreshHandler = _ => Task.FromResult(true);
        authentication.StatusHandler = _ => Task.FromResult(new LauncherServerStatus(
            "Arthas",
            true,
            true,
            true,
            true,
            true,
            DateTimeOffset.UtcNow));
        authentication.NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>(
        [
            new LauncherNews(
                "atlas-launcher-1-1-0",
                "Launcher",
                "Atlas Launcher 1.1",
                "État stable de caractérisation.",
                DateTimeOffset.UtcNow)
        ]);
        return authentication;
    }

    private static async Task RestoreAuthenticatedAsync(
        LauncherSessionCoordinator session)
    {
        LauncherSessionRestoreResult restored = await session.RestoreOnceAsync();
        Equal(LauncherSessionRestoreStatus.Restored, restored.Status, "La session témoin doit être restaurée.");
        Equal(LauncherSessionState.Authenticated, session.CurrentSnapshot.State, "Le coordinateur doit être authentifié.");
    }

    private static async Task<LauncherSessionCompletion> RequiredCompletion(
        LauncherSessionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null, $"La tentative aurait dû démarrer ({start.Status}).");
        return await start.Completion!;
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static void ExecuteBoundCommand(Button button)
    {
        ICommand command = button.Command
            ?? throw new InvalidOperationException($"Le bouton {button.Name} n'a aucune commande.");
        if (command.CanExecute(button.CommandParameter))
        {
            command.Execute(button.CommandParameter);
        }
    }

    private static void RaisePreviewKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("La source WPF du contrôle est absente.");
        KeyEventArgs args = new(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        target.RaiseEvent(args);
    }

    private static void RaisePreviewMouseDown(UIElement target)
    {
        MouseButtonEventArgs args = new(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = target
        };
        target.RaiseEvent(args);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement
    {
        return scope.FindName(name) as T
            ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Le scénario profil WPF n'a pas atteint l'état attendu.");
            }

            await DelayAndPumpAsync(15);
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
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];
        foreach (string path in resourcePaths)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(path, UriKind.Relative)
            });
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

internal sealed class ProfileRuntimeEnvironment : IAsyncDisposable
{
    private readonly TemporaryClient _client;

    private ProfileRuntimeEnvironment(
        TemporaryClient client,
        FakeLauncherAuthService authentication,
        FakeGameLaunchService launch,
        LauncherRuntime runtime)
    {
        _client = client;
        Authentication = authentication;
        Launch = launch;
        Runtime = runtime;
    }

    internal FakeLauncherAuthService Authentication { get; }

    internal FakeGameLaunchService Launch { get; }

    internal LauncherRuntime Runtime { get; }

    internal static async Task<ProfileRuntimeEnvironment> CreateAsync(
        bool initialize = true)
    {
        TemporaryClient client = new();
        client.CreatePlayableFiles();
        client.WriteVersionMarker("local-v1");
        FakeLauncherAuthService authentication = LauncherProfileLogoutTests.AuthenticatedService();
        FakeGameLaunchService launch = new();
        LauncherRuntime runtime = new(new LauncherRuntimeDependencies
        {
            LoadSettings = () => client.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test",
            WriteRuntimeLog = _ => { },
            CreateAuthorizedHttpClient = _ => new HttpClient(new ProfileRejectingHttpHandler()),
            CreateGameVerificationService = (_, _) => new RuntimeVerificationStub(),
            CreateGameMaintenanceService = (_, _) => new RuntimeMaintenanceStub(),
            CreateGameLaunchService = _ => launch,
            HasPlayableClient = GameInstallServices.HasPlayableClient
        });
        ProfileRuntimeEnvironment environment = new(client, authentication, launch, runtime);
        if (initialize)
        {
            await runtime.InitializeAsync();
        }

        return environment;
    }

    public async ValueTask DisposeAsync()
    {
        Runtime.BeginShutdown();
        await Runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(2));
        Runtime.Dispose();
        _client.Dispose();
    }

    private sealed class ProfileRejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Aucun HttpClient métier direct ne doit être utilisé par le test profil.");
        }
    }
}
