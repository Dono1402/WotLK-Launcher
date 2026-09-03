using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.Updater;

internal static class V2LifecycleIsolationTests
{
    private static readonly AvatarDescriptor AccountAAvatarV1 = Avatar(101, 1);
    private static readonly AvatarDescriptor AccountAAvatarV2 = Avatar(101, 2);
    private static readonly AvatarDescriptor AccountBAvatar = Avatar(202, 1);
    private static readonly AvatarDescriptor AccountAFriendAvatar = Avatar(111, 4);

    internal static async Task<int> RunAsync()
    {
        await CharacterizeSingleRuntimeCompositionAsync();
        await RunWpfLifecycleHarnessAsync();
        Console.WriteLine(
            "Lifecycle/isolation V2 OK (05A.1): composition unique, A->B, callbacks tardifs, timers/HTTP et navigation100.");
        return 0;
    }

    private static async Task CharacterizeSingleRuntimeCompositionAsync()
    {
        LifecycleRuntimeEnvironment environment = await LifecycleRuntimeEnvironment.CreateAsync(
            AccountASession());
        try
        {
            environment.AssertSingleComposition("après initialisation");
            Equal(1, environment.Authentication.RestoreCalls,
                "La composition réelle doit restaurer la session une seule fois.");
            Equal(1, environment.FriendsTime.CreateTimerCalls,
                "Friends doit posséder un unique timer de session.");
            Equal(1, environment.SelfUpdateTimerCreations,
                "Self-update doit posséder un unique timer 30 secondes.");
            Equal(2, environment.TotalPeriodicTimerCreations,
                "La composition V2 ne doit créer que les timers périodiques Friends et self-update.");
            True(ReferenceEquals(
                    environment.CapturedAuthorizedClient,
                    environment.VerificationHttpClient)
                && ReferenceEquals(
                    environment.CapturedAuthorizedClient,
                    environment.MaintenanceHttpClient)
                && ReferenceEquals(
                    environment.CapturedAuthorizedClient,
                    environment.AddonsHttpClient)
                && ReferenceEquals(
                    environment.CapturedAuthorizedClient,
                    environment.SelfUpdateHttpClient)
                && ReferenceEquals(
                    environment.CapturedAuthorizedClient,
                    environment.AvatarHttpClient),
                "Game, Addons, self-update et avatars doivent recevoir le même HttpClient autorisé.");
            Equal("token-a", environment.AccessTokenProvider?.Invoke(),
                "Le provider d'autorisation partagé doit lire la session courante.");
        }
        finally
        {
            await environment.DisposeAsync();
        }

        Equal(1, environment.Authentication.DisposeCalls,
            "L'authentification doit être libérée une seule fois.");
        Equal(1, environment.HttpHandler.DisposeCalls,
            "L'unique HttpClient autorisé doit être libéré une seule fois.");
        Equal(1, environment.SelfUpdateClient.DisposeCalls,
            "L'unique client self-update doit être libéré une seule fois.");
        Equal(1, environment.SelfUpdateTimer.StopCalls,
            "L'unique timer self-update doit être arrêté une seule fois.");
        Equal(1, environment.FriendsTime.Timer.DisposeCalls,
            "L'unique timer Friends doit être libéré une seule fois.");
    }

    private static async Task RunWpfLifecycleHarnessAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfLifecycleHarness(completion))
        {
            IsBackground = true,
            Name = "AtlasV2LifecycleIsolationHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static void RunWpfLifecycleHarness(TaskCompletionSource completion)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(dispatcher));
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
            LifecycleRuntimeEnvironment? environment = null;
            LauncherShellV2? window = null;
            AccountStateAdapter? accountAdapter = null;
            FriendsStateAdapter? friendsAdapter = null;
            AuthStateAdapter? authAdapter = null;
            ProfileStateAdapter? profileAdapter = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                environment = await LifecycleRuntimeEnvironment.CreateAsync(AccountASession());

                ShellUiState shell = LauncherV2RuntimePresentation.CreateShell(environment.Runtime);
                GameUiState game = LauncherV2RuntimePresentation.CreateGame(
                    environment.Runtime.LocalClient);
                AddonsUiState addons = new(AddonsStateAdapter.Project(
                    environment.Runtime.Addons.CurrentSnapshot));
                DashboardUiState dashboard = LauncherV2RuntimePresentation.CreateDashboard();
                FriendsUiState friends = new(FriendsStateAdapter.Project(
                    environment.Runtime.Friends.CurrentSnapshot));
                ProfileUiState profile = new();
                SettingsUiState settings = new(SettingsStateAdapter.CreateInitialView(
                    environment.Runtime.SettingsRuntime.CurrentSnapshot,
                    environment.Runtime.Game.CurrentSnapshot,
                    environment.Runtime.Dashboard.CurrentSnapshot,
                    environment.Runtime.LauncherVersion,
                    "lifecycle-test.log",
                    selfUpdate: environment.Runtime.SelfUpdate.CurrentSnapshot));
                AccountUiState account = new(AccountStateAdapter.Project(
                    environment.Runtime.Account.CurrentSnapshot,
                    avatarImage: null));
                AvatarCropUiState crop = new(AvatarCropUiState.Empty.Current);
                ActivityUiState activity = new(ActivityStateAdapter.Project(
                    environment.Runtime.Activity.CurrentSnapshot));
                window = new LauncherShellV2(
                    shell,
                    game,
                    addons,
                    dashboard,
                    friends,
                    profile,
                    settings,
                    account,
                    crop,
                    activity)
                {
                    Width = 1080,
                    Height = 680,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                authAdapter = new AuthStateAdapter(
                    window.AuthState,
                    shell,
                    game,
                    environment.Runtime.Session,
                    dispatcher);
                profileAdapter = new ProfileStateAdapter(
                    profile,
                    game,
                    environment.Runtime.Profile,
                    dispatcher);
                accountAdapter = new AccountStateAdapter(
                    account,
                    crop,
                    shell,
                    profile,
                    environment.Runtime.Account,
                    environment.Runtime.AvatarImages,
                    dispatcher);
                friendsAdapter = new FriendsStateAdapter(
                    friends,
                    environment.Runtime.Friends,
                    dispatcher,
                    environment.Runtime.AvatarImages);
                window.Show();
                await PumpAsync(DispatcherPriority.ApplicationIdle);

                await LoadAccountAAsync(environment, shell, profile, account, friends);
                BitmapSource accountAChrome = shell.ProfileAvatarImage
                    ?? throw new InvalidOperationException("La photo A 64 px devait être chargée.");
                BitmapSource accountAFull = account.Current.AvatarImage
                    ?? throw new InvalidOperationException("La photo A 256 px devait être chargée.");
                BitmapSource accountAFriend = friends.Current.Friends.Single().AvatarImage as BitmapSource
                    ?? throw new InvalidOperationException("La photo sociale A devait être chargée.");

                OpenAccountEmailEditor(window, account);
                TextBox pendingEmail = Required<TextBox>(window.AccountPage, "NewEmailBox");
                pendingEmail.Text = "pending-a@example.test";

                TaskCompletionSource<AvatarMediaDownloadResult> accountAAvatarV2Release =
                    NewSignal<AvatarMediaDownloadResult>();
                environment.Media.DownloadHandler = (descriptor, size, _) =>
                {
                    if (descriptor.AvatarId == AccountAAvatarV2.AvatarId
                        && descriptor.Version == AccountAAvatarV2.Version)
                    {
                        environment.Media.SignalDelayedDownload(size);
                        return accountAAvatarV2Release.Task;
                    }

                    return Task.FromResult(AvatarMediaDownloadResult.Success(
                        AccountAvatarClientTests.CreatePng(size, size)));
                };
                environment.Media.ProfileHandler = _ => Task.FromResult(
                    ProfileResult(AccountAProfile(AccountAAvatarV2)));
                AccountActionCompletion avatarV2Refresh = await RequiredAccountCompletion(
                    environment.Runtime.Account.TryRefresh());
                Equal(AccountActionCompletionStatus.Succeeded, avatarV2Refresh.Status,
                    "Le refresh A v2 doit publier son descripteur avant le changement de compte.");
                await environment.Media.WaitForDelayedDownloadsAsync(2);

                TaskCompletionSource<AvatarProfileReadResult> lateAccountA =
                    NewSignal<AvatarProfileReadResult>();
                TaskCompletionSource accountARefreshEntered = NewSignal();
                environment.Media.ProfileHandler = _ =>
                {
                    accountARefreshEntered.TrySetResult();
                    return lateAccountA.Task;
                };
                AccountActionStartResult oldAccountRefresh = environment.Runtime.Account.TryRefresh();
                True(oldAccountRefresh.IsStarted,
                    "Le refresh Compte A tardif doit démarrer avant logout.");
                await accountARefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                TaskCompletionSource<IReadOnlyList<LauncherFriend>> lateFriendsA =
                    NewSignal<IReadOnlyList<LauncherFriend>>();
                TaskCompletionSource friendsARefreshEntered = NewSignal();
                environment.Authentication.FriendsHandler = _ =>
                {
                    friendsARefreshEntered.TrySetResult();
                    return lateFriendsA.Task;
                };
                FriendsActionStartResult oldFriendsRefresh = environment.Runtime.Friends.TryRefresh();
                True(oldFriendsRefresh.IsStarted,
                    "Le refresh Amis A tardif doit démarrer avant logout.");
                await friendsARefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                environment.Authentication.LogoutHandler = _ =>
                {
                    environment.Authentication.InvalidateLocalSession();
                    return Task.CompletedTask;
                };
                ProfileLogoutStartResult logout = environment.Runtime.Profile.TryLogout();
                True(logout.IsStarted, "La déconnexion A doit démarrer.");
                LauncherSessionCompletion logoutCompletion = await logout.Completion!
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Equal(LauncherSessionCompletionStatus.Succeeded, logoutCompletion.Status,
                    "La déconnexion A doit réussir.");
                await PumpAsync(DispatcherPriority.ApplicationIdle);
                AssertSignedOutNeutral(
                    environment,
                    window,
                    shell,
                    profile,
                    account,
                    friends,
                    pendingEmail);

                LauncherAuthSession accountBSession = AccountBSession();
                environment.Authentication.LoginHandler = (_, _, _) =>
                    Task.FromResult(accountBSession);
                environment.Authentication.SessionsHandler = _ => Task.FromResult(
                    AccountBSessions());
                environment.Authentication.FriendsHandler = _ => Task.FromResult(
                    AccountBFriends());
                environment.Media.ProfileHandler = _ => Task.FromResult(
                    ProfileResult(accountBSession.Profile));

                LauncherSessionStartResult loginB = environment.Runtime.TryLogin(
                    "AccountB",
                    "test-password-b");
                True(loginB.IsStarted, "La reconnexion B doit démarrer.");
                LauncherSessionCompletion loginBCompletion = await loginB.Completion!
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Equal(LauncherSessionCompletionStatus.Succeeded, loginBCompletion.Status,
                    "La reconnexion B doit réussir.");
                await PumpAsync(DispatcherPriority.ApplicationIdle);

                FriendsActionCompletion friendsB = await RequiredFriendsCompletion(
                    environment.Runtime.Friends.TryRefresh());
                Equal(FriendsActionCompletionStatus.Succeeded, friendsB.Status,
                    "Les amis B doivent se charger pendant que l'ancienne réponse A est en vol.");
                lateFriendsA.TrySetResult(AccountAFriends());
                FriendsActionCompletion staleFriends = await oldFriendsRefresh.Completion!
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Equal(FriendsActionCompletionStatus.Superseded, staleFriends.Status,
                    "Une réponse Amis A tardive doit être obsolète après login B.");

                accountAAvatarV2Release.TrySetResult(AvatarMediaDownloadResult.Success(
                    AccountAvatarClientTests.CreatePng(24, 24)));
                lateAccountA.TrySetResult(ProfileResult(AccountAProfile(AccountAAvatarV2)));
                AccountActionCompletion staleAccount = await oldAccountRefresh.Completion!
                    .WaitAsync(TimeSpan.FromSeconds(2));
                Equal(AccountActionCompletionStatus.Cancelled, staleAccount.Status,
                    "Une réponse Compte A tardive doit être annulée après login B.");

                AccountActionCompletion accountB = await RequiredAccountCompletion(
                    environment.Runtime.Account.TryRefresh());
                Equal(AccountActionCompletionStatus.Succeeded, accountB.Status,
                    "Le profil et les sessions B doivent se charger après retrait du refresh A.");
                await WaitForAsync(() =>
                    shell.HasProfileAvatar
                    && account.Current.HasProfileAvatar
                    && friends.Current.Friends.Length == 1);
                await PumpAsync(DispatcherPriority.ApplicationIdle);

                AssertAccountBIsolated(
                    environment,
                    window,
                    shell,
                    profile,
                    account,
                    friends,
                    pendingEmail,
                    accountAChrome,
                    accountAFull,
                    accountAFriend);

                CharacterizeHundredRealNavigationChanges(
                    environment,
                    window,
                    shell,
                    game,
                    addons,
                    dashboard,
                    friends,
                    settings,
                    account);

                TaskCompletionSource<AvatarProfileReadResult> afterDisposeAccount =
                    NewSignal<AvatarProfileReadResult>();
                TaskCompletionSource afterDisposeAccountEntered = NewSignal();
                environment.Media.ProfileHandler = _ =>
                {
                    afterDisposeAccountEntered.TrySetResult();
                    return afterDisposeAccount.Task;
                };
                AccountActionStartResult pendingAccount = environment.Runtime.Account.TryRefresh();
                await afterDisposeAccountEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                TaskCompletionSource<IReadOnlyList<LauncherFriend>> afterDisposeFriends =
                    NewSignal<IReadOnlyList<LauncherFriend>>();
                TaskCompletionSource afterDisposeFriendsEntered = NewSignal();
                environment.Authentication.FriendsHandler = _ =>
                {
                    afterDisposeFriendsEntered.TrySetResult();
                    return afterDisposeFriends.Task;
                };
                FriendsActionStartResult pendingFriends = environment.Runtime.Friends.TryRefresh();
                await afterDisposeFriendsEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

                int accountNotifications = 0;
                int friendsNotifications = 0;
                environment.Runtime.Account.SnapshotChanged += (_, _) => accountNotifications++;
                environment.Runtime.Friends.SnapshotChanged += (_, _) => friendsNotifications++;
                AccountViewState uiAccountBeforeDispose = account.Current;
                FriendsViewState uiFriendsBeforeDispose = friends.Current;

                friendsAdapter.Dispose();
                accountAdapter.Dispose();
                profileAdapter.Dispose();
                authAdapter.Dispose();
                friendsAdapter = null;
                accountAdapter = null;
                profileAdapter = null;
                authAdapter = null;
                window.Close();
                window = null;
                environment.Runtime.Dispose();
                environment.Runtime.Dispose();
                int accountNotificationsAtDispose = accountNotifications;
                int friendsNotificationsAtDispose = friendsNotifications;

                afterDisposeAccount.TrySetResult(ProfileResult(AccountAProfile(AccountAAvatarV1)));
                afterDisposeFriends.TrySetResult(AccountAFriends());
                AccountActionCompletion disposedAccount = await pendingAccount.Completion!
                    .WaitAsync(TimeSpan.FromSeconds(2));
                FriendsActionCompletion disposedFriends = await pendingFriends.Completion!
                    .WaitAsync(TimeSpan.FromSeconds(2));
                await PumpAsync(DispatcherPriority.ApplicationIdle);
                Equal(AccountActionCompletionStatus.Cancelled, disposedAccount.Status,
                    "Le refresh Compte ignorant le token doit rester observé après Dispose.");
                Equal(FriendsActionCompletionStatus.Superseded, disposedFriends.Status,
                    "Le refresh Friends ignorant le token doit rester obsolète après Dispose.");
                Equal(accountNotificationsAtDispose, accountNotifications,
                    "Aucun callback Compte ne doit être publié après Dispose.");
                Equal(friendsNotificationsAtDispose, friendsNotifications,
                    "Aucun callback Friends ne doit être publié après Dispose.");
                True(ReferenceEquals(uiAccountBeforeDispose, account.Current),
                    "L'adapter Compte libéré ne doit plus modifier WPF.");
                True(ReferenceEquals(uiFriendsBeforeDispose, friends.Current),
                    "L'adapter Friends libéré ne doit plus modifier WPF.");
                environment.AssertDisposedOnce();
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
            finally
            {
                friendsAdapter?.Dispose();
                accountAdapter?.Dispose();
                profileAdapter?.Dispose();
                authAdapter?.Dispose();
                window?.Close();
                if (environment is not null)
                {
                    await environment.DisposeAsync();
                }
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task LoadAccountAAsync(
        LifecycleRuntimeEnvironment environment,
        ShellUiState shell,
        ProfileUiState profile,
        AccountUiState account,
        FriendsUiState friends)
    {
        environment.Authentication.SessionsHandler = _ => Task.FromResult(AccountASessions());
        environment.Authentication.FriendsHandler = _ => Task.FromResult(AccountAFriends());
        environment.Media.ProfileHandler = _ => Task.FromResult(
            ProfileResult(AccountAProfile(AccountAAvatarV1)));
        environment.Media.DownloadHandler = (_, size, _) => Task.FromResult(
            AvatarMediaDownloadResult.Success(
                AccountAvatarClientTests.CreatePng(size, size)));

        AccountActionCompletion accountCompletion = await RequiredAccountCompletion(
            environment.Runtime.Account.TryRefresh());
        FriendsActionCompletion friendsCompletion = await RequiredFriendsCompletion(
            environment.Runtime.Friends.TryRefresh());
        Equal(AccountActionCompletionStatus.Succeeded, accountCompletion.Status,
            "Le profil A doit se charger.");
        Equal(FriendsActionCompletionStatus.Succeeded, friendsCompletion.Status,
            "Les relations A doivent se charger.");
        await WaitForAsync(() =>
            shell.HasProfileAvatar
            && profile.HasAvatar
            && account.Current.HasProfileAvatar
            && friends.Current.Friends.SingleOrDefault()?.HasAvatarImage == true);

        Equal("AccountA", shell.Username, "Le shell doit afficher A.");
        Equal("a@example.test", account.Current.Email, "L'e-mail A doit être chargé.");
        Equal(2, account.Current.Sessions.Length, "Les sessions A doivent être chargées.");
        Equal(1, friends.Current.Friends.Length, "La liste d'amis A doit être chargée.");
        Equal(1, friends.Current.IncomingRequests.Length,
            "La demande entrante A doit être chargée.");
        Equal(1, friends.Current.OutgoingRequests.Length,
            "La demande sortante A doit être chargée.");
    }

    private static void AssertSignedOutNeutral(
        LifecycleRuntimeEnvironment environment,
        LauncherShellV2 window,
        ShellUiState shell,
        ProfileUiState profile,
        AccountUiState account,
        FriendsUiState friends,
        TextBox pendingEmail)
    {
        Equal(LauncherSessionState.SignedOut, environment.Runtime.Session.CurrentSnapshot.State,
            "La session doit être neutre après logout A.");
        True(!environment.Runtime.Account.CurrentSnapshot.IsAuthenticated,
            "Le runtime Compte doit être déconnecté.");
        Equal(string.Empty, environment.Runtime.Account.CurrentSnapshot.Username,
            "Le nom A doit disparaître du runtime Compte.");
        Equal(string.Empty, environment.Runtime.Account.CurrentSnapshot.Email,
            "L'e-mail A doit disparaître du runtime Compte.");
        True(environment.Runtime.Account.CurrentSnapshot.Avatar is null,
            "Le descripteur avatar A doit disparaître du runtime Compte.");
        True(environment.Runtime.Account.CurrentSnapshot.Sessions.IsEmpty,
            "Les sessions A doivent disparaître du runtime Compte.");
        True(environment.Runtime.Friends.CurrentSnapshot.Friends.IsEmpty
            && environment.Runtime.Friends.CurrentSnapshot.IncomingRequests.IsEmpty
            && environment.Runtime.Friends.CurrentSnapshot.OutgoingRequests.IsEmpty,
            "Amis et demandes A doivent disparaître du runtime social.");
        True(!environment.FriendsTime.Timer.IsEnabled,
            "Le timer Friends doit être désarmé pendant l'état déconnecté.");
        True(!shell.IsAuthenticated && shell.Username == "Compte" && !shell.HasProfileAvatar,
            "Le shell doit revenir à l'identité neutre sans avatar.");
        True(!profile.Current.IsAuthenticated && !profile.IsOpen && !profile.HasAvatar,
            "Le profil et son overlay doivent être fermés et neutres.");
        True(!account.Current.IsRuntimeConnected
            && string.IsNullOrEmpty(account.Current.Username)
            && string.IsNullOrEmpty(account.Current.Email)
            && account.Current.Sessions.IsEmpty
            && !account.Current.HasProfileAvatar
            && !account.Current.IsEmailEditorOpen
            && !account.Current.IsPasswordEditorOpen,
            "La projection Compte doit retirer identité, avatar, sessions et éditeurs A.");
        True(!friends.Current.IsRuntimeConnected
            && friends.Current.Friends.IsEmpty
            && friends.Current.IncomingRequests.IsEmpty
            && friends.Current.OutgoingRequests.IsEmpty
            && !friends.IsOpen,
            "La projection Friends doit retirer les trois listes et fermer le drawer.");
        Equal(string.Empty, pendingEmail.Text,
            "Le champ e-mail A doit être vidé en quittant Compte au logout.");
        Equal(LauncherShellPage.Game, window.CurrentPage,
            "Une page Compte devenue inaccessible doit revenir sur Jeu.");
        Equal(ShellOverlayKind.None, window.CurrentOverlay,
            "Aucun overlay A ne doit rester actif après logout.");
    }

    private static void AssertAccountBIsolated(
        LifecycleRuntimeEnvironment environment,
        LauncherShellV2 window,
        ShellUiState shell,
        ProfileUiState profile,
        AccountUiState account,
        FriendsUiState friends,
        TextBox pendingEmail,
        BitmapSource accountAChrome,
        BitmapSource accountAFull,
        BitmapSource accountAFriend)
    {
        AccountRuntimeSnapshot runtimeAccount = environment.Runtime.Account.CurrentSnapshot;
        FriendsRuntimeSnapshot runtimeFriends = environment.Runtime.Friends.CurrentSnapshot;
        Equal("AccountB", runtimeAccount.Username, "Le runtime Compte doit appartenir à B.");
        Equal("b@example.test", runtimeAccount.Email, "Le runtime ne doit pas conserver l'e-mail A.");
        Equal(AccountBAvatar, runtimeAccount.Avatar, "Le runtime doit utiliser l'avatar B.");
        True(runtimeAccount.Sessions.Length == 1
            && runtimeAccount.Sessions.All(session => session.Id.StartsWith("b-", StringComparison.Ordinal)),
            "Seules les sessions B doivent rester.");
        True(runtimeFriends.Friends.Length == 1
            && runtimeFriends.Friends.All(friend => friend.Username.StartsWith("B", StringComparison.Ordinal))
            && runtimeFriends.IncomingRequests.IsEmpty
            && runtimeFriends.OutgoingRequests.IsEmpty,
            "Les amis et demandes A ne doivent pas réapparaître chez B.");
        Equal("AccountB", shell.Username, "Le shell doit afficher B.");
        Equal("AccountB", profile.Current.Username, "Le menu profil doit afficher B.");
        Equal("b@example.test", account.Current.Email, "La page Compte doit afficher l'e-mail B.");
        True(account.Current.Sessions.Length == 1
            && account.Current.Sessions.All(session => session.Id.StartsWith("b-", StringComparison.Ordinal)),
            "La page Compte ne doit afficher aucune session A.");
        True(shell.ProfileAvatarImage is not null
            && account.Current.AvatarImage is not null
            && !ReferenceEquals(accountAChrome, shell.ProfileAvatarImage)
            && !ReferenceEquals(accountAFull, account.Current.AvatarImage),
            "Les caches d'images A et B doivent produire des projections distinctes.");
        True(friends.Current.Friends.Length == 1
            && friends.Current.Friends[0].Username == "BFriend"
            && !friends.Current.Friends[0].HasAvatarImage
            && friends.Current.Friends[0].Initial == "B",
            "L'ami B sans avatar doit utiliser son initiale, jamais l'image sociale A.");
        True(friends.Current.Friends.All(friend =>
                !ReferenceEquals(friend.AvatarImage, accountAFriend)),
            "L'image sociale A ne doit pas être réutilisée chez B.");
        True(environment.Runtime.AvatarImages.TryGetMemory(
                AccountAAvatarV1,
                AccountStateAdapter.ChromeAvatarPixelSize,
                out BitmapSource? cachedA)
            && ReferenceEquals(cachedA, accountAChrome),
            "L'entrée A peut rester en cache versionné sans être projetée chez B.");
        True(environment.Runtime.AvatarImages.TryGetMemory(
                AccountBAvatar,
                AccountStateAdapter.ChromeAvatarPixelSize,
                out BitmapSource? cachedB)
            && cachedB is not null
            && !ReferenceEquals(cachedA, cachedB),
            "La clé de cache B doit être distincte de la clé A.");
        True(!account.Current.IsEmailEditorOpen
            && !account.Current.IsPasswordEditorOpen
            && string.IsNullOrEmpty(pendingEmail.Text)
            && !profile.IsOpen
            && !friends.IsOpen
            && !window.AuthState.IsOpen
            && window.CurrentOverlay == ShellOverlayKind.None,
            "Aucun éditeur ou overlay A ne doit survivre à la reconnexion B.");
        True(environment.FriendsTime.Timer.IsEnabled,
            "Le même timer Friends doit être réarmé pour B.");
        environment.AssertSingleComposition("après logout/relogin A->B");
        Equal("token-b", environment.AccessTokenProvider?.Invoke(),
            "Le HttpClient partagé doit lire le token B sans être recréé.");
        string logs = string.Join('\n', environment.Logs);
        True(!logs.Contains("a@example.test", StringComparison.OrdinalIgnoreCase)
            && !logs.Contains("token-a", StringComparison.Ordinal)
            && !logs.Contains("pending-a", StringComparison.OrdinalIgnoreCase),
            "Les diagnostics lifecycle ne doivent contenir aucun identifiant sensible A.");
    }

    private static void CharacterizeHundredRealNavigationChanges(
        LifecycleRuntimeEnvironment environment,
        LauncherShellV2 window,
        ShellUiState shell,
        GameUiState game,
        AddonsUiState addons,
        DashboardUiState dashboard,
        FriendsUiState friends,
        SettingsUiState settings,
        AccountUiState account)
    {
        Button gameNavigation = Required<Button>(window, "GameNavigationButton");
        Button addonsNavigation = Required<Button>(window, "AddonsNavigationButton");
        Button settingsNavigation = Required<Button>(window, "SettingsButton");
        Button profileButton = Required<Button>(window, "ProfileButton");
        Button manageAccount = Required<Button>(window.ProfileOverlay, "ManageAccountButton");

        for (int index = 0; index < 25; index++)
        {
            RaiseClick(gameNavigation);
            Equal(LauncherShellPage.Game, window.CurrentPage,
                "La navigation réelle doit atteindre Jeu.");
            RaiseClick(addonsNavigation);
            Equal(LauncherShellPage.Addons, window.CurrentPage,
                "La navigation réelle doit atteindre Addons.");
            RaiseClick(settingsNavigation);
            Equal(LauncherShellPage.Settings, window.CurrentPage,
                "La navigation réelle doit atteindre Paramètres.");
            RaiseClick(profileButton);
            Equal(ShellOverlayKind.Profile, window.CurrentOverlay,
                "Le profil réel doit rester disponible pendant navigation100.");
            RaiseClick(manageAccount);
            Equal(LauncherShellPage.Account, window.CurrentPage,
                "La navigation réelle doit atteindre Compte.");
            Equal(ShellOverlayKind.None, window.CurrentOverlay,
                "Le profil doit se fermer en ouvrant Compte.");
        }

        True(ReferenceEquals(shell, window.ShellState)
            && ReferenceEquals(game, window.GameState)
            && ReferenceEquals(addons, window.AddonsState)
            && ReferenceEquals(dashboard, window.DashboardState)
            && ReferenceEquals(friends, window.FriendsState)
            && ReferenceEquals(settings, window.SettingsState)
            && ReferenceEquals(account, window.AccountState),
            "Cent navigations réelles ne doivent recréer aucun UiState.");
        environment.AssertSingleComposition("après 100 navigations réelles");
        Equal(1, environment.FriendsTime.CreateTimerCalls,
            "Cent navigations ne doivent pas créer un second timer Friends.");
        Equal(1, environment.SelfUpdateTimerCreations,
            "Cent navigations ne doivent pas créer un second timer self-update.");
        Equal(1, environment.AuthorizedHttpClientCreations,
            "Cent navigations ne doivent pas créer un second HttpClient autorisé.");
    }

    private static void OpenAccountEmailEditor(
        LauncherShellV2 window,
        AccountUiState account)
    {
        RaiseClick(Required<Button>(window, "ProfileButton"));
        RaiseClick(Required<Button>(window.ProfileOverlay, "ManageAccountButton"));
        Equal(LauncherShellPage.Account, window.CurrentPage,
            "Le compte A doit être ouvert avant d'éditer l'e-mail.");
        account.SelectSection(AccountSection.Security);
        account.OpenEmailEditor();
        True(account.Current.IsEmailEditorOpen,
            "L'éditeur e-mail A doit être ouvert pour caractériser son nettoyage.");
    }

    private static LauncherAuthSession AccountASession() => Session(
        "token-a",
        "refresh-a",
        AccountAProfile(AccountAAvatarV1));

    private static LauncherAuthSession AccountBSession() => Session(
        "token-b",
        "refresh-b",
        Profile(202, "AccountB", "b@example.test", AccountBAvatar));

    private static LauncherProfile AccountAProfile(AvatarDescriptor avatar) =>
        Profile(101, "AccountA", "a@example.test", avatar);

    private static LauncherProfile Profile(
        uint accountId,
        string username,
        string email,
        AvatarDescriptor? avatar) => new(
        accountId,
        username,
        email,
        EmailVerified: true,
        AvatarKey: null,
        TwoFactorEnabled: false,
        RecoveryCodesGenerated: false,
        Completion: 80,
        avatar);

    private static LauncherAuthSession Session(
        string accessToken,
        string refreshToken,
        LauncherProfile profile) => new(
        accessToken,
        DateTimeOffset.UtcNow.AddHours(1),
        refreshToken,
        DateTimeOffset.UtcNow.AddDays(1),
        profile);

    private static AvatarProfileReadResult ProfileResult(LauncherProfile profile) =>
        new(profile, SupportsProfilePhotos: true);

    private static IReadOnlyList<LauncherDeviceSession> AccountASessions() =>
    [
        DeviceSession("a-current", "A desktop", current: true),
        DeviceSession("a-old", "A laptop", current: false)
    ];

    private static IReadOnlyList<LauncherDeviceSession> AccountBSessions() =>
    [
        DeviceSession("b-current", "B desktop", current: true)
    ];

    private static LauncherDeviceSession DeviceSession(
        string id,
        string device,
        bool current) => new(
        id,
        device,
        DateTimeOffset.UtcNow.AddDays(-2),
        DateTimeOffset.UtcNow.AddMinutes(-5),
        DateTimeOffset.UtcNow.AddDays(2),
        current);

    private static IReadOnlyList<LauncherFriend> AccountAFriends() =>
    [
        Friend(111, "AFriend", "accepted", AccountAFriendAvatar),
        Friend(112, "AIncoming", "incoming", avatar: null),
        Friend(113, "AOutgoing", "outgoing", avatar: null)
    ];

    private static IReadOnlyList<LauncherFriend> AccountBFriends() =>
    [
        Friend(221, "BFriend", "accepted", avatar: null)
    ];

    private static LauncherFriend Friend(
        uint accountId,
        string username,
        string relationship,
        AvatarDescriptor? avatar) => new(
        accountId,
        username,
        AvatarKey: null,
        relationship,
        Online: false,
        CharacterName: null,
        Level: null,
        ClassId: null,
        ZoneId: null,
        LastSeenAt: null,
        avatar);

    private static AvatarDescriptor Avatar(uint accountId, ulong version)
    {
        Guid id = Guid.Parse($"00000000-0000-0000-0000-{accountId:000000000000}");
        string root = $"/media/avatars/{id:N}/{version}";
        return new AvatarDescriptor(
            id,
            version,
            $"{root}/32.png",
            $"{root}/64.png",
            $"{root}/128.png",
            $"{root}/256.png");
    }

    private static async Task<AccountActionCompletion> RequiredAccountCompletion(
        AccountActionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null,
            $"L'opération Compte devait démarrer, statut={start.Status}.");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task<FriendsActionCompletion> RequiredFriendsCompletion(
        FriendsActionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null,
            $"L'opération Friends devait démarrer, statut={start.Status}.");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Le harnais lifecycle WPF n'a pas atteint l'état attendu.");
            }

            await Task.Delay(10);
            await PumpAsync(DispatcherPriority.DataBind);
        }
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewSignal<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement =>
        scope.FindName(name) as T
        ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

    private static void LoadV2Resources(Application application)
    {
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];
        foreach (string resourcePath in resourcePaths)
        {
            if (application.Resources.MergedDictionaries.Any(dictionary =>
                    dictionary.Source?.OriginalString == resourcePath))
            {
                continue;
            }

            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(resourcePath, UriKind.Relative)
            });
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

    private sealed class LifecycleRuntimeEnvironment : IAsyncDisposable
    {
        private readonly LifecycleTemporaryClient _client;
        private int _disposeState;

        private LifecycleRuntimeEnvironment(
            LifecycleTemporaryClient client,
            FakeLauncherAuthService authentication,
            LifecycleAvatarMediaClient media,
            LifecycleFriendsTimeProvider friendsTime,
            LifecycleHttpHandler httpHandler,
            LifecycleSelfUpdateTimer selfUpdateTimer,
            LifecycleSelfUpdateClient selfUpdateClient,
            LauncherRuntime? runtime)
        {
            _client = client;
            Authentication = authentication;
            Media = media;
            FriendsTime = friendsTime;
            HttpHandler = httpHandler;
            SelfUpdateTimer = selfUpdateTimer;
            SelfUpdateClient = selfUpdateClient;
            Runtime = runtime!;
        }

        internal FakeLauncherAuthService Authentication { get; }

        internal LifecycleAvatarMediaClient Media { get; }

        internal LifecycleFriendsTimeProvider FriendsTime { get; }

        internal LifecycleHttpHandler HttpHandler { get; }

        internal LifecycleSelfUpdateTimer SelfUpdateTimer { get; }

        internal LifecycleSelfUpdateClient SelfUpdateClient { get; }

        internal LauncherRuntime Runtime { get; private set; }

        internal List<string> Logs { get; } = [];

        internal int SettingsLoads { get; private set; }

        internal int AuthenticationCreations { get; private set; }

        internal int AuthorizedHttpClientCreations { get; private set; }

        internal int VerificationCreations { get; private set; }

        internal int MaintenanceCreations { get; private set; }

        internal int AddonsCreations { get; private set; }

        internal int GameLaunchCreations { get; private set; }

        internal int SelfUpdateTimerCreations { get; private set; }

        internal int SelfUpdateClientCreations { get; private set; }

        internal int AvatarMediaCreations { get; private set; }

        internal int AvatarCacheCreations { get; private set; }

        internal Func<string?>? AccessTokenProvider { get; private set; }

        internal HttpClient? CapturedAuthorizedClient { get; private set; }

        internal HttpClient? VerificationHttpClient { get; private set; }

        internal HttpClient? MaintenanceHttpClient { get; private set; }

        internal HttpClient? AddonsHttpClient { get; private set; }

        internal HttpClient? SelfUpdateHttpClient { get; private set; }

        internal HttpClient? AvatarHttpClient { get; private set; }

        internal int TotalPeriodicTimerCreations =>
            FriendsTime.CreateTimerCalls + SelfUpdateTimerCreations;

        internal static async Task<LifecycleRuntimeEnvironment> CreateAsync(
            LauncherAuthSession initialSession)
        {
            LifecycleTemporaryClient client = new();
            FakeLauncherAuthService authentication = new()
            {
                Session = initialSession,
                RestoreResult = true,
                EnsureFreshHandler = _ => Task.FromResult(true)
            };
            LifecycleAvatarMediaClient media = new()
            {
                ProfileHandler = _ => Task.FromResult(ProfileResult(initialSession.Profile)),
                DownloadHandler = (_, size, _) => Task.FromResult(
                    AvatarMediaDownloadResult.Success(
                        AccountAvatarClientTests.CreatePng(size, size)))
            };
            LifecycleFriendsTimeProvider friendsTime = new();
            LifecycleHttpHandler httpHandler = new();
            LifecycleSelfUpdateTimer selfUpdateTimer = new(
                LauncherSelfUpdateCoordinator.CheckInterval);
            LifecycleSelfUpdateClient selfUpdateClient = new();
            LifecycleRuntimeEnvironment environment = new(
                client,
                authentication,
                media,
                friendsTime,
                httpHandler,
                selfUpdateTimer,
                selfUpdateClient,
                runtime: null);
            LauncherRuntime runtime = new(new LauncherRuntimeDependencies
            {
                LoadSettings = () =>
                {
                    environment!.SettingsLoads++;
                    return client.Settings;
                },
                CreateAuthentication = () =>
                {
                    environment!.AuthenticationCreations++;
                    return authentication;
                },
                GameClientStateReader = new GameClientStateReader(),
                GetLauncherVersion = () => "v1.1.0-lifecycle-test",
                WriteRuntimeLog = message => environment!.Logs.Add(message),
                CreateAuthorizedHttpClient = provider =>
                {
                    environment!.AuthorizedHttpClientCreations++;
                    environment.AccessTokenProvider = provider;
                    HttpClient httpClient = new(httpHandler, disposeHandler: true);
                    environment.CapturedAuthorizedClient = httpClient;
                    return httpClient;
                },
                CreateGameVerificationService = (httpClient, _) =>
                {
                    environment!.VerificationCreations++;
                    environment.VerificationHttpClient = httpClient;
                    return new RuntimeVerificationStub();
                },
                CreateGameMaintenanceService = (httpClient, _) =>
                {
                    environment!.MaintenanceCreations++;
                    environment.MaintenanceHttpClient = httpClient;
                    return new RuntimeMaintenanceStub();
                },
                CreateAddonManagementService = httpClient =>
                {
                    environment!.AddonsCreations++;
                    environment.AddonsHttpClient = httpClient;
                    return new LifecycleAddonManagementService();
                },
                CreateGameLaunchService = _ =>
                {
                    environment!.GameLaunchCreations++;
                    return new FakeGameLaunchService();
                },
                CreateLauncherSelfUpdateTimer = interval =>
                {
                    environment!.SelfUpdateTimerCreations++;
                    Equal(LauncherSelfUpdateCoordinator.CheckInterval, interval,
                        "Le runtime doit demander la cadence self-update attendue.");
                    return selfUpdateTimer;
                },
                CreateLauncherSelfUpdateClient = httpClient =>
                {
                    environment!.SelfUpdateClientCreations++;
                    environment.SelfUpdateHttpClient = httpClient;
                    return selfUpdateClient;
                },
                LauncherSelfUpdateFinalizer = new LifecycleSelfUpdateFinalizer(),
                FriendsTimeProvider = friendsTime,
                CreateAvatarMediaClient = (httpClient, _) =>
                {
                    environment!.AvatarMediaCreations++;
                    environment.AvatarHttpClient = httpClient;
                    return media;
                },
                GetAvatarCacheRoot = () => client.CacheRoot,
                CreateAvatarImageCache = (avatarMedia, root, token, unauthorized) =>
                {
                    environment!.AvatarCacheCreations++;
                    True(ReferenceEquals(media, avatarMedia),
                        "Le cache avatar doit partager l'unique client média.");
                    return new AvatarImageCache(avatarMedia, root, token, unauthorized);
                },
                RequestApplicationShutdown = static () =>
                    throw new InvalidOperationException(
                        "Le test lifecycle ne doit jamais appliquer de mise à jour.")
            });
            environment.Runtime = runtime;
            await runtime.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));
            return environment;
        }

        internal void AssertSingleComposition(string phase)
        {
            Equal(1, SettingsLoads, $"Settings dupliqué {phase}.");
            Equal(1, AuthenticationCreations, $"Auth dupliquée {phase}.");
            Equal(1, AuthorizedHttpClientCreations, $"HttpClient autorisé dupliqué {phase}.");
            Equal(1, VerificationCreations, $"Game verification dupliquée {phase}.");
            Equal(1, MaintenanceCreations, $"Game maintenance dupliquée {phase}.");
            Equal(1, AddonsCreations, $"Addons dupliqué {phase}.");
            Equal(1, GameLaunchCreations, $"Game launch dupliqué {phase}.");
            Equal(1, SelfUpdateTimerCreations, $"Timer self-update dupliqué {phase}.");
            Equal(1, SelfUpdateClientCreations, $"Client self-update dupliqué {phase}.");
            Equal(1, AvatarMediaCreations, $"Client média avatar dupliqué {phase}.");
            Equal(1, AvatarCacheCreations, $"Cache avatar dupliqué {phase}.");
        }

        internal void AssertDisposedOnce()
        {
            Equal(1, Authentication.DisposeCalls,
                "Auth doit être disposée une seule fois.");
            Equal(1, HttpHandler.DisposeCalls,
                "HttpClient autorisé doit être disposé une seule fois.");
            Equal(1, SelfUpdateClient.DisposeCalls,
                "Client self-update doit être disposé une seule fois.");
            Equal(1, SelfUpdateTimer.StopCalls,
                "Timer self-update doit être arrêté une seule fois.");
            Equal(1, FriendsTime.Timer.DisposeCalls,
                "Timer Friends doit être disposé une seule fois.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            if (!Runtime.IsDisposed)
            {
                Runtime.BeginShutdown();
                await Runtime.WaitForShutdownAsync(TimeSpan.FromSeconds(2));
                Runtime.Dispose();
            }
            _client.Dispose();
        }
    }

    private sealed class LifecycleTemporaryClient : IDisposable
    {
        internal LifecycleTemporaryClient()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "AtlasLifecycleIsolation",
                Guid.NewGuid().ToString("N"));
            CacheRoot = Path.Combine(Root, "avatar-cache");
            Directory.CreateDirectory(Root);
            Settings = new LauncherSettings
            {
                InstallPath = Root,
                ManifestUrl = LauncherSettings.GetDefaultManifestUrl(),
                GameLocale = "frFR",
                AutomaticLauncherUpdates = false,
                CloseLauncherOnGameStart = false
            };
        }

        internal string Root { get; }

        internal string CacheRoot { get; }

        internal LauncherSettings Settings { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class LifecycleAvatarMediaClient : IAvatarMediaClient
    {
        private readonly HashSet<int> _delayedDownloadSizes = [];

        internal Func<CancellationToken, Task<AvatarProfileReadResult>> ProfileHandler { get; set; } =
            _ => Task.FromResult(ProfileResult(AccountAProfile(AccountAAvatarV1)));

        internal Func<AvatarDescriptor, int, CancellationToken, Task<AvatarMediaDownloadResult>>
            DownloadHandler { get; set; } = (_, size, _) => Task.FromResult(
                AvatarMediaDownloadResult.Success(
                    AccountAvatarClientTests.CreatePng(size, size)));

        public Task<AvatarProfileReadResult> GetProfileAsync(CancellationToken cancellationToken) =>
            ProfileHandler(cancellationToken);

        public Task<AvatarDescriptor> UploadAvatarAsync(
            AvatarUploadRequest upload,
            IProgress<AvatarUploadTransferProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Aucun upload n'est attendu dans le test lifecycle.");

        public Task DeleteAvatarAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Aucune suppression n'est attendue dans le test lifecycle.");

        public Task<AvatarMediaDownloadResult> DownloadAvatarAsync(
            AvatarDescriptor descriptor,
            int size,
            CancellationToken cancellationToken) =>
            DownloadHandler(descriptor, size, cancellationToken);

        internal void SignalDelayedDownload(int size)
        {
            lock (_delayedDownloadSizes)
            {
                _delayedDownloadSizes.Add(size);
            }
        }

        internal async Task WaitForDelayedDownloadsAsync(int expected)
        {
            await WaitForAsync(() =>
            {
                lock (_delayedDownloadSizes)
                {
                    return _delayedDownloadSizes.Count >= expected;
                }
            });
        }
    }

    private sealed class LifecycleFriendsTimeProvider : TimeProvider
    {
        internal int CreateTimerCalls { get; private set; }

        internal LifecycleFriendsTimer Timer { get; private set; } = null!;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            CreateTimerCalls++;
            Timer = new LifecycleFriendsTimer(dueTime, period);
            return Timer;
        }
    }

    private sealed class LifecycleFriendsTimer(
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private bool _isDisposed;

        internal TimeSpan DueTime { get; private set; } = dueTime;

        internal TimeSpan Period { get; private set; } = period;

        internal int ChangeCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        internal bool IsEnabled => !_isDisposed && DueTime != Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            ChangeCalls++;
            DueTime = dueTime;
            Period = period;
            return true;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            DisposeCalls++;
            DueTime = Timeout.InfiniteTimeSpan;
            Period = Timeout.InfiniteTimeSpan;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class LifecycleHttpHandler : HttpMessageHandler
    {
        internal int DisposeCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Le test lifecycle ne doit effectuer aucune requête HTTP réelle.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
            }
            base.Dispose(disposing);
        }
    }

    private sealed class LifecycleSelfUpdateTimer(TimeSpan interval)
        : ILauncherSelfUpdateTimer
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

    private sealed class LifecycleSelfUpdateClient : ILauncherSelfUpdateClient, IDisposable
    {
        internal int DisposeCalls { get; private set; }

        public Task<LauncherUpdateManifest> LoadManifestAsync(
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Le test lifecycle ne doit lancer aucun check self-update.");

        public Task DownloadAsync(
            Uri uri,
            string targetPath,
            long expectedSize,
            Action<LauncherSelfUpdateTransferProgress> reportProgress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Le test lifecycle ne doit télécharger aucune mise à jour.");

        public void Dispose() => DisposeCalls++;
    }

    private sealed class LifecycleSelfUpdateFinalizer : ILauncherSelfUpdateFinalizer
    {
        public Task<LauncherUpdateTransaction> PrepareAndLaunchAsync(
            string targetPath,
            string downloadedCandidatePath,
            long expectedSize,
            string expectedSha256,
            string authenticatedTargetVersion,
            int parentProcessId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Le test lifecycle ne doit finaliser aucune mise à jour.");
    }

    private sealed class LifecycleAddonManagementService : IAddonManagementService
    {
        public Task<AddonCatalog> LoadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AddonCatalog
            {
                SchemaVersion = 1,
                ClientInterface = "30403"
            });

        public IReadOnlyDictionary<string, AddonInspection> Inspect(
            AddonCatalog catalog,
            string installRoot) =>
            ImmutableDictionary<string, AddonInspection>.Empty;

        public Task ApplySelectionAsync(
            AddonCatalog catalog,
            string installRoot,
            IReadOnlyDictionary<string, bool> selection,
            IProgress<AddonTransferProgress>? progress,
            Action<string>? log,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "Le test lifecycle ne doit muter aucun addon.");
    }
}
