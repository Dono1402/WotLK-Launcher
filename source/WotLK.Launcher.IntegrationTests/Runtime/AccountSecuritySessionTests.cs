using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Server;
using WotLK.Launcher.UI.V2.Presentation;

internal static class AccountSecuritySessionTests
{
    private const string CurrentSessionId = "session-current";
    private const string OtherSessionId = "session-other";

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        ValidateDeviceNamePolicy();
        await ValidateSnapshotAndSessionsAsync();
        await ValidateEmailMutationsAsync();
        await ValidatePasswordMutationsAsync();
        await ValidateSessionRevocationAsync();
        await ValidateCompatibilityAndLifecycleAsync();
        await AccountSecuritySessionWpfTests.RunAsync(captureDirectory);
        Console.WriteLine("Account security and sessions OK: runtime, errors, lifecycle and WPF.");
        return 0;
    }

    private static void ValidateDeviceNamePolicy()
    {
        Equal(
            "DESKTOP-UP6AMRA",
            LauncherDatabase.NormalizeDeviceName("  DESKTOP-UP6AMRA  "),
            "Le nom d'appareil doit être stabilisé avant la création de session.");
        True(
            LauncherDatabase.NormalizeDeviceName(null) is null
            && LauncherDatabase.NormalizeDeviceName("   ") is null,
            "Un client sans nom d'appareil ne doit pas fusionner toutes les sessions anonymes.");
    }

    private static async Task ValidateSnapshotAndSessionsAsync()
    {
        List<LauncherDeviceSession> source =
        [
            Session(CurrentSessionId, "Atlas Launcher - Windows", current: true),
            Session(OtherSessionId, "Atlas Launcher - Portable", current: false)
        ];
        using TestContext context = await TestContext.CreateAsync(
            emailVerified: false,
            sessions: source);

        AccountActionCompletion refreshed = await CompleteAsync(context.Account.TryRefresh());
        Equal(AccountActionCompletionStatus.Succeeded, refreshed.Status,
            "Le profil et les sessions réelles doivent se charger ensemble.");
        AccountRuntimeSnapshot snapshot = context.Account.CurrentSnapshot;
        False(snapshot.EmailVerified, "Le statut e-mail non vérifié doit rester autoritaire.");
        Equal(AccountSecurityState.Ready, snapshot.SecurityState,
            "La sécurité doit devenir disponible après le profil.");
        Equal(AccountSessionsState.Loaded, snapshot.SessionsState,
            "La liste des sessions doit être marquée chargée.");
        Equal(CurrentSessionId, snapshot.CurrentSessionId,
            "La session courante doit venir du contrat serveur.");
        Equal(2, snapshot.Sessions.Length, "Aucune session réelle ne doit être perdue.");

        source.Clear();
        Equal(2, snapshot.Sessions.Length,
            "Le snapshot doit conserver une copie immuable des sessions.");
        AccountViewState view = AccountStateAdapter.Project(snapshot, avatarImage: null);
        True(view.Sessions.Single(item => item.IsCurrent).LastActivityText == "Active maintenant",
            "La session courante doit être présentée comme active.");
        False(view.Sessions.Any(item =>
                item.DeviceName.Contains("Liège", StringComparison.OrdinalIgnoreCase)
                || item.DeviceName.Contains("Namur", StringComparison.OrdinalIgnoreCase)
                || item.LastActivityText.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)),
            "La projection réelle ne doit inventer ni lieu ni adresse IP.");
        True(view.CanResendVerification,
            "Un e-mail non vérifié doit proposer le renvoi existant.");

        using TestContext empty = await TestContext.CreateAsync(
            emailVerified: true,
            sessions: []);
        _ = await CompleteAsync(empty.Account.TryRefresh());
        AccountViewState emptyView = AccountStateAdapter.Project(
            empty.Account.CurrentSnapshot,
            avatarImage: null);
        True(emptyView.IsEmailVerified && !emptyView.CanResendVerification,
            "Un e-mail vérifié ne doit pas proposer un renvoi.");
        Equal("Aucune autre session active.", emptyView.SessionsMessage,
            "Une liste vide doit recevoir un libellé stable.");
    }

    private static async Task ValidateEmailMutationsAsync()
    {
        using TestContext context = await TestContext.CreateAsync(
            emailVerified: true,
            sessions: DefaultSessions());
        _ = await CompleteAsync(context.Account.TryRefresh());
        LauncherProfile changed = FakeLauncherAuthService.CreateProfile(
            "Dono1402",
            "new-address@example.test",
            emailVerified: false);
        context.Authentication.ChangeEmailHandler = (_, _) => Task.FromResult(
            new EmailChangeResult(changed, true, "ignored-server-message"));

        AccountActionCompletion success = await CompleteAsync(
            context.Account.TryChangeEmail(" new-address@example.test "));
        Equal(AccountActionCompletionStatus.Succeeded, success.Status,
            "La modification d'e-mail doit utiliser l'endpoint existant.");
        Equal("new-address@example.test", success.Snapshot.Email,
            "L'adresse retournée par le serveur doit être publiée.");
        False(success.Snapshot.EmailVerified,
            "Une nouvelle adresse ne doit jamais devenir vérifiée artificiellement.");
        Equal(AccountNoticeKind.EmailChangedVerificationSent, success.Snapshot.Notice,
            "Le résultat best-effort d'envoi doit être reflété.");

        context.Authentication.ResendVerificationHandler = _ => Task.FromResult("sent");
        AccountActionCompletion resent = await CompleteAsync(
            context.Account.TryResendVerification());
        Equal(AccountNoticeKind.VerificationEmailSent, resent.Snapshot.Notice,
            "Le renvoi de validation doit produire un retour court.");

        LauncherProfile changedWithoutMail = changed with { Email = "stored@example.test" };
        context.Authentication.ChangeEmailHandler = (_, _) => Task.FromResult(
            new EmailChangeResult(changedWithoutMail, false, "mail transport failed"));
        AccountActionCompletion stored = await CompleteAsync(
            context.Account.TryChangeEmail("stored@example.test"));
        Equal("stored@example.test", stored.Snapshot.Email,
            "L'échec best-effort du mail ne doit pas annuler l'adresse enregistrée.");
        Equal(AccountNoticeKind.EmailChangedVerificationUnavailable, stored.Snapshot.Notice,
            "L'échec best-effort doit être annoncé sans rollback fictif.");

        context.Authentication.ChangeEmailHandler = (_, _) => Task.FromException<EmailChangeResult>(
            new LauncherAuthException("sensitive duplicate payload", HttpStatusCode.Conflict));
        AccountActionCompletion duplicate = await CompleteAsync(
            context.Account.TryChangeEmail("used@example.test"));
        Equal(AccountErrorCategory.EmailAlreadyUsed, duplicate.Snapshot.AccountError.Category,
            "HTTP 409 doit rester un e-mail déjà utilisé.");

        context.Authentication.ChangeEmailHandler = (_, _) => Task.FromException<EmailChangeResult>(
            new LauncherAuthException("invalid e-mail details", HttpStatusCode.BadRequest));
        AccountActionCompletion invalid = await CompleteAsync(
            context.Account.TryChangeEmail("invalid-value"));
        Equal(AccountErrorCategory.InvalidEmail, invalid.Snapshot.AccountError.Category,
            "HTTP 400 e-mail doit être présenté comme une adresse invalide.");

        context.Authentication.ChangeEmailHandler = (_, _) => Task.FromException<EmailChangeResult>(
            new HttpRequestException("sensitive network payload"));
        AccountActionCompletion network = await CompleteAsync(
            context.Account.TryChangeEmail("network@example.test"));
        Equal(AccountErrorCategory.Network, network.Snapshot.AccountError.Category,
            "Une panne réseau doit être structurée.");
        False(context.Logs.Any(line =>
                line.Contains("new-address@example.test", StringComparison.OrdinalIgnoreCase)
                || line.Contains("sensitive", StringComparison.OrdinalIgnoreCase)),
            "Les logs ne doivent contenir ni e-mail soumis ni message d'exception.");
    }

    private static async Task ValidatePasswordMutationsAsync()
    {
        using TestContext context = await TestContext.CreateAsync(
            emailVerified: true,
            sessions: DefaultSessions());
        _ = await CompleteAsync(context.Account.TryRefresh());

        Equal(AccountActionStartStatus.InvalidRequest,
            context.Account.TryChangePassword("old", "short").Status,
            "Un mot de passe hors 10-128 doit être refusé avant le réseau.");
        Equal(0, context.Authentication.ChangePasswordCalls,
            "La validation locale ne doit pas appeler l'API.");

        const string oldSecret = "CurrentSecret-03A5";
        const string newSecret = "ReplacementSecret-03A5";
        context.Authentication.ChangePasswordHandler = (current, next, _) =>
        {
            Equal(oldSecret, current, "Le secret actuel doit être transmis uniquement à l'appel.");
            Equal(newSecret, next, "Le nouveau secret doit être transmis uniquement à l'appel.");
            return Task.CompletedTask;
        };
        AccountActionCompletion success = await CompleteAsync(
            context.Account.TryChangePassword(oldSecret, newSecret));
        Equal(AccountNoticeKind.PasswordChanged, success.Snapshot.Notice,
            "Le succès mot de passe doit rester explicite.");
        Equal(2, success.Snapshot.Sessions.Length,
            "Le changement de mot de passe ne doit pas inventer une révocation de sessions.");
        string snapshotText = success.Snapshot.ToString();
        False(snapshotText.Contains(oldSecret, StringComparison.Ordinal)
            || snapshotText.Contains(newSecret, StringComparison.Ordinal),
            "Aucun mot de passe ne doit entrer dans le snapshot.");

        context.Authentication.ChangePasswordHandler = (_, _, _) => Task.FromException(
            new LauncherAuthException(oldSecret, HttpStatusCode.Unauthorized));
        context.Authentication.RefreshProfileHandler = _ => Task.FromResult(
            FakeLauncherAuthService.CreateProfile());
        AccountActionCompletion wrongPassword = await CompleteAsync(
            context.Account.TryChangePassword(oldSecret, newSecret));
        Equal(AccountErrorCategory.CurrentPasswordIncorrect,
            wrongPassword.Snapshot.AccountError.Category,
            "Un 401 mot de passe avec session encore valide doit désigner l'ancien mot de passe.");
        True(context.Session.CurrentSnapshot.IsAuthenticated,
            "Un ancien mot de passe incorrect ne doit pas fermer la session valide.");
        False(context.Logs.Any(line => line.Contains(oldSecret, StringComparison.Ordinal)),
            "Le secret ne doit pas être recopié dans les logs.");

        TaskCompletionSource entered = Signal();
        TaskCompletionSource release = Signal();
        context.Authentication.ChangePasswordHandler = async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        AccountActionStartResult first = context.Account.TryChangePassword(oldSecret, newSecret);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(AccountActionStartStatus.Busy,
            context.Account.TryChangePassword(oldSecret, newSecret).Status,
            "Un double envoi doit être refusé immédiatement, sans file d'attente.");
        release.TrySetResult();
        _ = await CompleteAsync(first);

        using TestContext expired = await TestContext.CreateAsync(
            emailVerified: true,
            sessions: DefaultSessions());
        expired.Authentication.ChangePasswordHandler = (_, _, _) => Task.FromException(
            new LauncherAuthException("expired", HttpStatusCode.Unauthorized));
        expired.Authentication.RefreshProfileHandler = _ => Task.FromException<LauncherProfile>(
            new LauncherAuthException("expired", HttpStatusCode.Unauthorized));
        AccountActionCompletion expiredResult = await CompleteAsync(
            expired.Account.TryChangePassword(oldSecret, newSecret));
        Equal(AccountActionCompletionStatus.Failed, expiredResult.Status,
            "Une session expirée doit terminer l'opération de façon observée.");
        False(expired.Session.CurrentSnapshot.IsAuthenticated
            || expired.Account.CurrentSnapshot.IsAuthenticated,
            "Un vrai 401 de session doit être délégué au coordinateur central.");
        Equal(1, expired.Authentication.InvalidateLocalSessionCalls,
            "L'invalidation locale ne doit être exécutée qu'une fois.");

        foreach (AccountErrorCategory category in Enum.GetValues<AccountErrorCategory>()
                     .Where(category => category != AccountErrorCategory.None))
        {
            True(!string.IsNullOrWhiteSpace(AccountStateAdapter.MapAccountError(category)),
                $"La catégorie {category} doit posséder un message utilisateur stable.");
        }
    }

    private static async Task ValidateSessionRevocationAsync()
    {
        using TestContext context = await TestContext.CreateAsync(
            emailVerified: true,
            sessions: DefaultSessions());
        _ = await CompleteAsync(context.Account.TryRefresh());

        Equal(AccountActionStartStatus.InvalidRequest,
            context.Account.TryRevokeSession(CurrentSessionId).Status,
            "La session courante doit être quittée uniquement par Logout.");
        Equal(0, context.Authentication.RevokeSessionCalls,
            "Aucun DELETE ne doit viser la session courante.");

        string? revokedId = null;
        context.Authentication.RevokeSessionHandler = (id, _) =>
        {
            revokedId = id;
            return Task.CompletedTask;
        };
        AccountActionCompletion success = await CompleteAsync(
            context.Account.TryRevokeSession(OtherSessionId));
        Equal(OtherSessionId, revokedId,
            "La révocation doit cibler exactement la session choisie.");
        True(success.Snapshot.Sessions.All(item => item.Id != OtherSessionId)
            && success.Snapshot.Sessions.Any(item => item.Id == CurrentSessionId),
            "La réussite doit retirer uniquement l'autre session.");

        await context.ReplaceSessionsAsync(DefaultSessions());
        TaskCompletionSource entered = Signal();
        TaskCompletionSource release = Signal();
        context.Authentication.RevokeSessionHandler = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        AccountActionStartResult first = context.Account.TryRevokeSession(OtherSessionId);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Equal(AccountActionStartStatus.Busy,
            context.Account.TryRevokeSession(OtherSessionId).Status,
            "Une deuxième révocation ne doit jamais être mise en attente.");
        release.TrySetResult();
        _ = await CompleteAsync(first);

        await context.ReplaceSessionsAsync(DefaultSessions());
        context.Authentication.RevokeSessionHandler = (_, _) => Task.FromException(
            new LauncherAuthException("gone", HttpStatusCode.NotFound));
        AccountActionCompletion missing = await CompleteAsync(
            context.Account.TryRevokeSession(OtherSessionId));
        Equal(AccountErrorCategory.SessionNotFound, missing.Snapshot.AccountError.Category,
            "Une session déjà absente doit être reconnue sans erreur brute.");

        context.Authentication.RevokeSessionHandler = (_, _) => Task.FromException(
            new HttpRequestException("network details"));
        AccountActionCompletion network = await CompleteAsync(
            context.Account.TryRevokeSession(OtherSessionId));
        Equal(AccountErrorCategory.Network, network.Snapshot.AccountError.Category,
            "La panne réseau de révocation doit être stable.");

        context.Authentication.RevokeSessionHandler = (_, _) => Task.FromException(
            new LauncherAuthException("expired bearer", HttpStatusCode.Unauthorized));
        AccountActionCompletion unauthorized = await CompleteAsync(
            context.Account.TryRevokeSession(OtherSessionId));
        Equal(AccountActionCompletionStatus.Failed, unauthorized.Status,
            "Le 401 doit terminer l'action observée.");
        False(context.Session.CurrentSnapshot.IsAuthenticated,
            "Le 401 réel doit être délégué au coordinateur de session.");
        False(context.Account.CurrentSnapshot.IsAuthenticated,
            "Le compte doit revenir à SignedOut après invalidation centrale.");
    }

    private static async Task ValidateCompatibilityAndLifecycleAsync()
    {
        using (TestContext context = await TestContext.CreateAsync(
                   emailVerified: true,
                   sessions: DefaultSessions()))
        {
            LauncherOperationStartResult play = context.Operations.TryBeginPlay(clientIsPlayable: true);
            True(play.IsStarted, "Le verrou Play de référence doit démarrer.");
            Equal(AccountActionStartStatus.RejectedByCompatibility,
                context.Account.TryChangeEmail("next@example.test").Status,
                "Une mutation sensible doit être refusée immédiatement pendant Play.");
            play.Lease!.Dispose();

            TaskCompletionSource entered = Signal();
            TaskCompletionSource release = Signal();
            context.Authentication.ChangeEmailHandler = async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return new EmailChangeResult(
                    FakeLauncherAuthService.CreateProfile(email: "next@example.test", emailVerified: false),
                    false,
                    string.Empty);
            };
            AccountActionStartResult email = context.Account.TryChangeEmail("next@example.test");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(LauncherOperationStartStatus.Busy,
                context.Operations.TryBegin(
                    LauncherOperationKind.Addons,
                    canUserCancel: true).Status,
                "Une opération mutante globale ne doit pas coexister avec le compte.");
            release.TrySetResult();
            _ = await CompleteAsync(email);
        }

        await ValidateLateLifecycleAsync(AccountOperationState.ChangingEmail);
        await ValidateLateLifecycleAsync(AccountOperationState.ChangingPassword);
        await ValidateLateLifecycleAsync(AccountOperationState.RevokingSession);
    }

    private static async Task ValidateLateLifecycleAsync(AccountOperationState operation)
    {
        TestContext context = await TestContext.CreateAsync(
            emailVerified: true,
            sessions: DefaultSessions());
        try
        {
            _ = await CompleteAsync(context.Account.TryRefresh());
            TaskCompletionSource entered = Signal();
            TaskCompletionSource release = Signal();
            AccountActionStartResult started;
            if (operation == AccountOperationState.ChangingEmail)
            {
                context.Authentication.ChangeEmailHandler = async (_, _) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    return new EmailChangeResult(
                        FakeLauncherAuthService.CreateProfile(email: "late@example.test"),
                        false,
                        string.Empty);
                };
                started = context.Account.TryChangeEmail("late@example.test");
            }
            else if (operation == AccountOperationState.ChangingPassword)
            {
                context.Authentication.ChangePasswordHandler = async (_, _, _) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                };
                started = context.Account.TryChangePassword(
                    "CurrentSecret-03A5",
                    "ReplacementSecret-03A5");
            }
            else
            {
                context.Authentication.RevokeSessionHandler = async (_, _) =>
                {
                    entered.TrySetResult();
                    await release.Task;
                };
                started = context.Account.TryRevokeSession(OtherSessionId);
            }

            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            int published = 0;
            context.Account.SnapshotChanged += Count;
            context.Account.Dispose();
            context.Operations.CancelForShutdown();
            release.TrySetResult();
            AccountActionCompletion completion = await started.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(AccountActionCompletionStatus.Cancelled, completion.Status,
                "Un résultat tardif après fermeture doit être ignoré.");
            True(await context.Account.WaitForIdleAsync(TimeSpan.FromSeconds(2)),
                "La fermeture doit observer la tâche même si l'API ignore son token.");
            await Task.Delay(25);
            Equal(0, published, "Aucun callback de présentation ne doit suivre Dispose.");
            context.Account.SnapshotChanged -= Count;

            void Count(object? sender, AccountRuntimeSnapshotEventArgs args) => published++;
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task<AccountActionCompletion> CompleteAsync(AccountActionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null,
            $"L'action devait démarrer, statut réel : {start.Status}.");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static IReadOnlyList<LauncherDeviceSession> DefaultSessions() =>
    [
        Session(CurrentSessionId, "Atlas Launcher - Windows", current: true),
        Session(OtherSessionId, "Atlas Launcher - Portable", current: false)
    ];

    private static LauncherDeviceSession Session(string id, string device, bool current)
    {
        DateTimeOffset now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);
        return new LauncherDeviceSession(
            id,
            device,
            now.AddDays(-20),
            current ? now : now.AddHours(-3),
            now.AddDays(10),
            current);
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void True(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }

    private sealed class TestContext : IDisposable
    {
        private readonly string _cacheRoot;
        private IReadOnlyList<LauncherDeviceSession> _sessions;

        private TestContext(
            string cacheRoot,
            CancellationTokenSource lifetime,
            FakeLauncherAuthService authentication,
            LauncherSessionCoordinator session,
            LauncherOperationCoordinator operations,
            AvatarImageCache cache,
            LauncherAccountCoordinator account,
            IReadOnlyList<LauncherDeviceSession> sessions,
            List<string> logs)
        {
            _cacheRoot = cacheRoot;
            Lifetime = lifetime;
            Authentication = authentication;
            Session = session;
            Operations = operations;
            Cache = cache;
            Account = account;
            _sessions = sessions;
            Logs = logs;
            Authentication.SessionsHandler = _ => Task.FromResult(_sessions);
        }

        internal CancellationTokenSource Lifetime { get; }
        internal FakeLauncherAuthService Authentication { get; }
        internal LauncherSessionCoordinator Session { get; }
        internal LauncherOperationCoordinator Operations { get; }
        internal AvatarImageCache Cache { get; }
        internal LauncherAccountCoordinator Account { get; }
        internal List<string> Logs { get; }

        internal static async Task<TestContext> CreateAsync(
            bool emailVerified,
            IReadOnlyList<LauncherDeviceSession> sessions)
        {
            string cacheRoot = AccountAvatarClientTests.NewRoot("security-sessions");
            CancellationTokenSource lifetime = new();
            LauncherProfile profile = FakeLauncherAuthService.CreateProfile(
                emailVerified: emailVerified);
            FakeLauncherAuthService authentication = new()
            {
                Session = FakeLauncherAuthService.CreateSession(
                    profile.Username,
                    profile.Email,
                    profile.EmailVerified),
                RestoreResult = true,
                EnsureFreshHandler = _ => Task.FromResult(true)
            };
            LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
            Equal(LauncherSessionRestoreStatus.Restored,
                (await session.RestoreOnceAsync()).Status,
                "Le test Compte exige une session Atlas restaurée.");
            LauncherOperationCoordinator operations = new();
            StubAvatarMediaClient media = new()
            {
                ProfileResult = new AvatarProfileReadResult(profile, SupportsProfilePhotos: true)
            };
            AvatarImageCache cache = new(media, cacheRoot, lifetime.Token);
            List<string> logs = [];
            LauncherAccountCoordinator account = new(
                session,
                authentication,
                operations,
                media,
                cache,
                () => authentication.Session?.Profile,
                logs.Add);
            return new TestContext(
                cacheRoot,
                lifetime,
                authentication,
                session,
                operations,
                cache,
                account,
                sessions,
                logs);
        }

        internal async Task ReplaceSessionsAsync(IReadOnlyList<LauncherDeviceSession> sessions)
        {
            _sessions = sessions;
            _ = await CompleteAsync(Account.TryRefresh());
        }

        public void Dispose()
        {
            Account.Dispose();
            Operations.Dispose();
            Session.Dispose();
            Cache.Dispose();
            Lifetime.Cancel();
            Lifetime.Dispose();
            AccountAvatarClientTests.TryDelete(_cacheRoot);
        }
    }
}
