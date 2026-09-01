using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static class LauncherAuthenticationTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeSecretFreePresentationModels();
        await CharacterizeRestorationAsync();
        await CharacterizeLoginAsync();
        await CharacterizeRegistrationAsync();
        await CharacterizeRuntimeIntegrationAsync();
        await CharacterizeRealWpfOverlayAsync();
        Console.WriteLine("Launcher authentication and session OK (02F.2).");
        return 0;
    }

    private static void CharacterizeSecretFreePresentationModels()
    {
        string[] forbiddenFragments = ["Password", "Token", "Authorization", "Ticket", "Secret"];
        foreach (Type type in new[] { typeof(AuthSessionSnapshot), typeof(AuthUiState), typeof(ShellUiState) })
        {
            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                True(
                    forbiddenFragments.All(fragment => !property.Name.Contains(
                        fragment,
                        StringComparison.OrdinalIgnoreCase)),
                    $"{type.Name}.{property.Name} ne doit contenir aucun secret.");
            }

            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                True(
                    forbiddenFragments.All(fragment => !field.Name.Contains(
                        fragment,
                        StringComparison.OrdinalIgnoreCase)),
                    $"{type.Name}.{field.Name} ne doit conserver aucun secret.");
            }
        }
    }

    private static async Task CharacterizeRestorationAsync()
    {
        await RestoreNoStoredSessionAsync();
        await RestoreValidSessionOnceAsync();
        await RestoreRejectedSessionAsync();
        await RestoreFailureAsync(
            new HttpRequestException("offline"),
            LauncherSessionFailureCategory.Network);
        await RestoreFailureAsync(
            new TaskCanceledException("timeout"),
            LauncherSessionFailureCategory.Timeout);
        await RestoreFailureAsync(
            new LauncherAuthException("unavailable", HttpStatusCode.ServiceUnavailable),
            LauncherSessionFailureCategory.ServiceUnavailable);
        await RestoreUnauthorizedAsync();
        await RestoreDuringShutdownAsync(ignoreCancellation: false);
        await RestoreDuringShutdownAsync(ignoreCancellation: true);
        await RestoreLoggingNeverLeaksAsync();
    }

    private static async Task RestoreNoStoredSessionAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            PrepareRestoreHandler = _ => Task.FromResult(new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.NoSession,
                null))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionRestoreResult result = await coordinator.RestoreOnceAsync();

        Equal(LauncherSessionRestoreStatus.NoSession, result.Status, "L'absence de session doit rester normale.");
        Equal(LauncherSessionState.SignedOut, coordinator.CurrentSnapshot.State, "L'absence de secret doit produire SignedOut.");
        Equal(LauncherSessionFailureCategory.NoStoredSession, coordinator.CurrentSnapshot.FailureCategory, "La cause NoStoredSession doit rester distincte.");
        Equal(0, authentication.CommitSessionCalls, "Aucune session ne doit être écrite.");
    }

    private static async Task RestoreValidSessionOnceAsync()
    {
        LauncherAuthSession session = FakeLauncherAuthService.CreateSession("Alice");
        TaskCompletionSource<LauncherAuthRestoreAttempt> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            PrepareRestoreHandler = _ => release.Task
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        Task<LauncherSessionRestoreResult> first = coordinator.RestoreOnceAsync();
        Task<LauncherSessionRestoreResult> second = coordinator.RestoreOnceAsync();
        release.SetResult(new LauncherAuthRestoreAttempt(
            LauncherAuthRestoreOutcome.Restored,
            session));
        LauncherSessionRestoreResult[] results = await Task.WhenAll(first, second);
        LauncherSessionRestoreResult third = await coordinator.RestoreOnceAsync();

        True(results.All(result => result.Status == LauncherSessionRestoreStatus.Restored), "Les restaurations concurrentes doivent partager le succès.");
        Equal(LauncherSessionRestoreStatus.Restored, third.Status, "Le résultat doit être idempotent.");
        Equal(1, authentication.RestoreCalls, "Une seule restauration réseau est autorisée.");
        Equal(1, authentication.CommitSessionCalls, "La session restaurée ne doit être validée qu'une fois.");
        Equal(LauncherSessionState.Authenticated, coordinator.CurrentSnapshot.State, "La session valide doit authentifier le runtime.");
        Equal("Alice", coordinator.CurrentSnapshot.Username, "L'identité restaurée est incorrecte.");
    }

    private static async Task RestoreRejectedSessionAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            PrepareRestoreHandler = _ => Task.FromResult(new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.Rejected,
                null))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionRestoreResult result = await coordinator.RestoreOnceAsync();

        Equal(LauncherSessionRestoreStatus.Rejected, result.Status, "Une session rejetée doit rester distincte d'une panne réseau.");
        Equal(LauncherSessionState.SignedOut, coordinator.CurrentSnapshot.State, "Une session expirée doit revenir à SignedOut.");
        Equal(LauncherSessionFailureCategory.SessionExpired, coordinator.CurrentSnapshot.FailureCategory, "La cause SessionExpired est attendue.");
    }

    private static async Task RestoreUnauthorizedAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            PrepareRestoreHandler = _ => Task.FromException<LauncherAuthRestoreAttempt>(
                new LauncherAuthException("unauthorized", HttpStatusCode.Unauthorized))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionRestoreResult result = await coordinator.RestoreOnceAsync();

        Equal(LauncherSessionRestoreStatus.Rejected, result.Status, "Un Unauthorized de restauration doit invalider la session.");
        Equal(LauncherSessionState.SignedOut, coordinator.CurrentSnapshot.State, "Unauthorized ne doit pas devenir une erreur client rouge.");
        Equal(LauncherSessionFailureCategory.SessionExpired, coordinator.CurrentSnapshot.FailureCategory, "Unauthorized doit être présenté comme session expirée.");
    }

    private static async Task RestoreFailureAsync(
        Exception exception,
        LauncherSessionFailureCategory expectedCategory)
    {
        LauncherAuthSession retained = FakeLauncherAuthService.CreateSession("Retained");
        FakeLauncherAuthService authentication = new()
        {
            Session = retained,
            PrepareRestoreHandler = _ => Task.FromException<LauncherAuthRestoreAttempt>(exception)
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionRestoreResult result = await coordinator.RestoreOnceAsync();

        Equal(LauncherSessionRestoreStatus.Unavailable, result.Status, "Une panne transitoire doit produire Unavailable.");
        Equal(LauncherSessionState.Unavailable, coordinator.CurrentSnapshot.State, "La restauration indisponible doit rester orthogonale au client.");
        Equal(expectedCategory, coordinator.CurrentSnapshot.FailureCategory, "La catégorie de restauration est incorrecte.");
        Equal(retained, authentication.Session, "Une panne réseau ne doit pas effacer arbitrairement la session existante.");
        Equal(0, authentication.CommitSessionCalls, "Une panne ne doit rien écrire dans le stockage.");
    }

    private static async Task RestoreDuringShutdownAsync(bool ignoreCancellation)
    {
        TaskCompletionSource<LauncherAuthRestoreAttempt> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            PrepareRestoreHandler = cancellationToken => ignoreCancellation
                ? release.Task
                : release.Task.WaitAsync(cancellationToken)
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });
        List<AuthSessionSnapshot> snapshots = [];
        coordinator.SnapshotChanged += (_, args) => snapshots.Add(args.Snapshot);

        Task<LauncherSessionRestoreResult> restore = coordinator.RestoreOnceAsync();
        lifetime.Cancel();
        release.TrySetResult(new LauncherAuthRestoreAttempt(
            LauncherAuthRestoreOutcome.Restored,
            FakeLauncherAuthService.CreateSession("Late")));
        LauncherSessionRestoreResult result = await restore;

        Equal(LauncherSessionRestoreStatus.Cancelled, result.Status, "La fermeture doit annuler la restauration.");
        True(snapshots.All(snapshot => !snapshot.IsAuthenticated), "Aucun succès tardif ne doit être publié après fermeture.");
        Equal(0, authentication.CommitSessionCalls, "Un résultat tardif ne doit pas être stocké.");
    }

    private static async Task RestoreLoggingNeverLeaksAsync()
    {
        const string secret = "refresh-token-secret";
        List<string> logs = [];
        FakeLauncherAuthService authentication = new()
        {
            PrepareRestoreHandler = _ => Task.FromException<LauncherAuthRestoreAttempt>(
                new InvalidOperationException(secret))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, logs.Add);

        await coordinator.RestoreOnceAsync();

        Equal(1, logs.Count, "L'échec doit être journalisé une fois.");
        True(!logs[0].Contains(secret, StringComparison.Ordinal), "Le journal ne doit contenir aucun secret.");
        True(logs[0].Contains(nameof(InvalidOperationException), StringComparison.Ordinal), "Le type technique doit rester disponible.");
    }

    private static async Task CharacterizeLoginAsync()
    {
        await LoginSuccessAsync();
        await LoginFailureAsync(
            new LauncherAuthException("bad credentials", HttpStatusCode.Unauthorized),
            LauncherSessionFailureCategory.InvalidCredentials);
        await LoginFailureAsync(
            new LauncherAuthException(
                AtlasProfileRequiredMessage,
                HttpStatusCode.Forbidden,
                "AtlasProfileRequired"),
            LauncherSessionFailureCategory.AtlasProfileRequired);
        await AtlasProfileRequiredContractAndPresentationAsync();
        await LoginFailureAsync(
            new HttpRequestException("offline"),
            LauncherSessionFailureCategory.Network);
        await LoginFailureAsync(
            new TaskCanceledException("timeout"),
            LauncherSessionFailureCategory.Timeout);
        await LoginFailureAsync(
            new LauncherAuthException("server", HttpStatusCode.InternalServerError),
            LauncherSessionFailureCategory.ServiceUnavailable);
        await LoginDoubleSubmitAndRetryAsync();
        await LoginStaleResultCannotReplaceNewerAsync();
    }

    private static async Task AtlasProfileRequiredContractAndPresentationAsync()
    {
        using HttpResponseMessage response = new(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new
            {
                error = AtlasProfileRequiredMessage,
                code = "AtlasProfileRequired"
            })
        };
        LauncherAuthException? parsed = null;
        try
        {
            await LauncherAuthService.EnsureSuccessAsync(response, CancellationToken.None);
        }
        catch (LauncherAuthException exception)
        {
            parsed = exception;
        }

        True(parsed is not null, "Le client legacy doit observer le refus Atlas controle.");
        Equal(HttpStatusCode.Forbidden, parsed!.StatusCode, "Le statut du refus Atlas doit rester 403.");
        Equal("AtlasProfileRequired", parsed.Code, "Le code de frontiere Atlas doit etre conserve.");
        Equal(AtlasProfileRequiredMessage, parsed.Message, "Le legacy doit afficher le message utilisateur du serveur.");

        using AuthUiState state = new();
        state.PrepareForOpen();
        state.ApplySessionSnapshot(new AuthSessionSnapshot(
            1,
            1,
            LauncherSessionState.SignedOut,
            LauncherSessionOperationKind.Login,
            "PlayerOnly",
            false,
            LauncherSessionFailureCategory.AtlasProfileRequired));

        Equal(AuthErrorKind.AtlasProfileRequired, state.ErrorKind, "La V2 doit distinguer ce refus des mauvais identifiants.");
        Equal(AtlasProfileRequiredMessage, state.ErrorMessage, "La V2 doit afficher un message controle.");
        True(
            !state.ErrorMessage.Contains("bot", StringComparison.OrdinalIgnoreCase)
            && !state.ErrorMessage.Contains("technique", StringComparison.OrdinalIgnoreCase),
            "Le message utilisateur ne doit reveler aucune nature technique du compte.");
    }

    private static async Task LoginSuccessAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            LoginHandler = (username, _, _) => Task.FromResult(
                FakeLauncherAuthService.CreateSession(username))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionStartResult start = coordinator.TryLogin(" Alice ", "transient-password");
        LauncherSessionCompletion completion = await RequiredCompletion(start);

        Equal(LauncherSessionStartStatus.Started, start.Status, "La connexion valide doit démarrer.");
        Equal(LauncherSessionCompletionStatus.Succeeded, completion.Status, "La connexion doit réussir.");
        Equal("Alice", authentication.Session?.Profile.Username, "Le nom doit être normalisé avant l'appel.");
        Equal(1, authentication.LoginCalls, "La connexion doit appeler le service une fois.");
        Equal(1, authentication.CommitSessionCalls, "Le résultat doit être validé une fois.");
        Equal(
            LauncherSessionStartStatus.AlreadyAuthenticated,
            coordinator.TryLogin("Other", "transient-password").Status,
            "Une session valide ne doit pas être remplacée par une nouvelle soumission implicite.");
        Equal(1, authentication.LoginCalls, "Le refus ne doit pas invalider la session active.");
    }

    private static async Task LoginFailureAsync(
        Exception exception,
        LauncherSessionFailureCategory expectedCategory)
    {
        FakeLauncherAuthService authentication = new()
        {
            LoginHandler = (_, _, _) => Task.FromException<LauncherAuthSession>(exception)
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionCompletion completion = await RequiredCompletion(
            coordinator.TryLogin("Dono1402", "transient-password"));

        Equal(LauncherSessionCompletionStatus.Failed, completion.Status, "L'échec doit être observé.");
        Equal(expectedCategory, completion.Snapshot.FailureCategory, "La catégorie de connexion est incorrecte.");
        Equal(0, authentication.CommitSessionCalls, "Un échec ne doit pas créer de session.");
    }

    private const string AtlasProfileRequiredMessage =
        "Ce compte n’est pas encore inscrit dans Atlas Launcher.";

    private static async Task LoginDoubleSubmitAndRetryAsync()
    {
        TaskCompletionSource<LauncherAuthSession> firstRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int attempt = 0;
        FakeLauncherAuthService authentication = new()
        {
            LoginHandler = (_, _, _) => ++attempt == 1
                ? firstRelease.Task
                : Task.FromResult(FakeLauncherAuthService.CreateSession("Retry"))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionStartResult first = coordinator.TryLogin("Dono1402", "transient-password");
        LauncherSessionStartResult duplicate = coordinator.TryLogin("Dono1402", "transient-password");
        Equal(LauncherSessionStartStatus.Busy, duplicate.Status, "Un double-clic doit être refusé immédiatement.");
        Equal(1, authentication.LoginCalls, "Le refus ne doit pas appeler le service.");
        firstRelease.SetException(new HttpRequestException("offline"));
        Equal(LauncherSessionCompletionStatus.Failed, (await RequiredCompletion(first)).Status, "Le premier échec doit terminer.");

        LauncherSessionStartResult retry = coordinator.TryLogin("Retry", "transient-password");
        Equal(LauncherSessionCompletionStatus.Succeeded, (await RequiredCompletion(retry)).Status, "Une nouvelle tentative doit être possible.");
        Equal(2, authentication.LoginCalls, "La nouvelle tentative doit appeler le service une fois.");
    }

    private static async Task LoginStaleResultCannotReplaceNewerAsync()
    {
        TaskCompletionSource<LauncherAuthSession> firstRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<LauncherAuthSession> secondRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            LoginHandler = (username, _, _) => username == "First"
                ? firstRelease.Task
                : secondRelease.Task
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionStartResult first = coordinator.TryLogin("First", "transient-password");
        True(coordinator.CancelInteractiveAttempt(), "Fermer l'overlay doit invalider la première génération.");
        LauncherSessionStartResult second = coordinator.TryLogin("Second", "transient-password");
        secondRelease.SetResult(FakeLauncherAuthService.CreateSession("Second"));
        Equal(LauncherSessionCompletionStatus.Succeeded, (await RequiredCompletion(second)).Status, "La seconde connexion doit réussir.");
        firstRelease.SetResult(FakeLauncherAuthService.CreateSession("First"));
        Equal(LauncherSessionCompletionStatus.Superseded, (await RequiredCompletion(first)).Status, "Le résultat ancien doit être ignoré.");

        Equal("Second", authentication.Session?.Profile.Username, "La tentative obsolète ne doit jamais remplacer la session récente.");
        Equal(1, authentication.CommitSessionCalls, "Seule la génération courante doit être stockée.");
    }

    private static async Task CharacterizeRegistrationAsync()
    {
        CharacterizeRegistrationValidation();
        await RegistrationSuccessAsync();
        await RegistrationConflictAsync(
            "Ce nom d'utilisateur est déjà utilisé.",
            LauncherSessionFailureCategory.UsernameAlreadyExists);
        await RegistrationConflictAsync(
            "Cette adresse e-mail est déjà utilisée.",
            LauncherSessionFailureCategory.EmailAlreadyExists);
        await RegistrationFailureAsync(new HttpRequestException("offline"), LauncherSessionFailureCategory.Network);
        await RegistrationDirectSignInMissingAsync();
        await RegistrationDoubleSubmitAsync();
    }

    private static void CharacterizeRegistrationValidation()
    {
        (string Username, string Email, string Password, string Confirmation)[] invalid =
        [
            ("ab", "dono@example.test", "0123456789", "0123456789"),
            (new string('a', 21), "dono@example.test", "0123456789", "0123456789"),
            ("dono-1402", "dono@example.test", "0123456789", "0123456789"),
            ("Dono1402", "invalid", "0123456789", "0123456789"),
            ("Dono1402", "dono@example.test", "123456789", "123456789"),
            ("Dono1402", "dono@example.test", new string('x', 129), new string('x', 129)),
            ("Dono1402", "dono@example.test", "0123456789", "different0")
        ];

        foreach ((string username, string email, string password, string confirmation) in invalid)
        {
            FakeLauncherAuthService authentication = new();
            using CancellationTokenSource lifetime = new();
            using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });
            LauncherSessionStartResult result = coordinator.TryRegister(
                username,
                email,
                password,
                confirmation);
            Equal(LauncherSessionStartStatus.RejectedByValidation, result.Status, "Le formulaire invalide doit être refusé localement.");
            Equal(0, authentication.RegisterCalls, "Aucune requête ne doit partir pour un formulaire invalide.");
        }
    }

    private static async Task RegistrationSuccessAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            RegisterHandler = (username, email, _, _) => Task.FromResult(
                FakeLauncherAuthService.CreateSession(username, email, emailVerified: false))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionCompletion completion = await RequiredCompletion(coordinator.TryRegister(
            "New_User",
            "new@example.test",
            "0123456789",
            "0123456789"));

        Equal(LauncherSessionCompletionStatus.Succeeded, completion.Status, "L'inscription doit connecter directement comme le legacy.");
        Equal(LauncherSessionState.Authenticated, completion.Snapshot.State, "Le compte créé doit être authentifié.");
        True(!completion.Snapshot.IsEmailVerified, "L'état réel de vérification e-mail doit être conservé.");
        Equal(1, authentication.RegisterCalls, "Le contrat d'inscription doit être appelé une fois.");
        Equal(0, authentication.LoginCalls, "Le contrat existant renvoie directement la session, sans second login.");
    }

    private static async Task RegistrationConflictAsync(
        string message,
        LauncherSessionFailureCategory expectedCategory)
    {
        FakeLauncherAuthService authentication = new()
        {
            RegisterHandler = (_, _, _, _) => Task.FromException<LauncherAuthSession>(
                new LauncherAuthException(message, HttpStatusCode.Conflict))
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionCompletion completion = await RequiredCompletion(coordinator.TryRegister(
            "New_User",
            "new@example.test",
            "0123456789",
            "0123456789"));

        Equal(expectedCategory, completion.Snapshot.FailureCategory, "Le conflit d'inscription est mal classé.");
        Equal(0, authentication.CommitSessionCalls, "Un conflit ne doit pas créer de session.");
    }

    private static async Task RegistrationFailureAsync(
        Exception exception,
        LauncherSessionFailureCategory expectedCategory)
    {
        FakeLauncherAuthService authentication = new()
        {
            RegisterHandler = (_, _, _, _) => Task.FromException<LauncherAuthSession>(exception)
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionCompletion completion = await RequiredCompletion(coordinator.TryRegister(
            "New_User",
            "new@example.test",
            "0123456789",
            "0123456789"));
        Equal(expectedCategory, completion.Snapshot.FailureCategory, "La panne d'inscription est mal classée.");
    }

    private static async Task RegistrationDirectSignInMissingAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            RegisterHandler = (_, _, _, _) => Task.FromResult<LauncherAuthSession>(null!)
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionCompletion completion = await RequiredCompletion(coordinator.TryRegister(
            "Created_User",
            "created@example.test",
            "0123456789",
            "0123456789"));

        Equal(LauncherSessionCompletionStatus.Failed, completion.Status, "Une réponse sans session ne doit pas prétendre être connectée.");
        Equal(LauncherSessionFailureCategory.AccountCreatedSignInRequired, completion.Snapshot.FailureCategory, "Le compte créé sans session doit demander une connexion.");
        Equal("Created_User", completion.Snapshot.Username, "Le nom doit rester disponible pour préremplir la connexion.");
    }

    private static async Task RegistrationDoubleSubmitAsync()
    {
        TaskCompletionSource<LauncherAuthSession> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = new()
        {
            RegisterHandler = (_, _, _, _) => release.Task
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator coordinator = new(authentication, lifetime.Token, _ => { });

        LauncherSessionStartResult first = coordinator.TryRegister(
            "New_User",
            "new@example.test",
            "0123456789",
            "0123456789");
        LauncherSessionStartResult duplicate = coordinator.TryRegister(
            "New_User",
            "new@example.test",
            "0123456789",
            "0123456789");
        Equal(LauncherSessionStartStatus.Busy, duplicate.Status, "La double inscription doit être refusée immédiatement.");
        release.SetResult(FakeLauncherAuthService.CreateSession("New_User"));
        await RequiredCompletion(first);
        Equal(1, authentication.RegisterCalls, "Une seule inscription réseau est autorisée.");
    }

    private static async Task CharacterizeRuntimeIntegrationAsync()
    {
        await RuntimeInitializationIsSingleFlightAsync();
        await RuntimeEnablesInstallAndRefreshesDashboardOnceAsync();
        await RuntimeEnablesUpdateAfterVerificationAsync();
        await RuntimeEnablesPlayWithoutLaunchingAsync();
    }

    private static async Task RuntimeInitializationIsSingleFlightAsync()
    {
        using TemporaryClient client = new();
        FakeLauncherAuthService authentication = AuthForRuntime("RestoredUser");
        LauncherAuthSession session = FakeLauncherAuthService.CreateSession("RestoredUser");
        authentication.PrepareRestoreHandler = _ => Task.FromResult(
            new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.Restored,
                session));
        using LauncherRuntime runtime = CreateRuntime(
            client,
            authentication,
            new RuntimeVerificationStub(),
            new RuntimeMaintenanceStub());

        Task<LauncherSessionRestoreResult> first = runtime.InitializeAsync();
        Task<LauncherSessionRestoreResult> second = runtime.InitializeAsync();
        LauncherSessionRestoreResult[] results = await Task.WhenAll(first, second);

        True(results.All(result => result.Status == LauncherSessionRestoreStatus.Restored),
            "Les appels concurrents doivent partager le résultat de restauration.");
        Equal(1, authentication.RestoreCalls, "Le runtime ne doit lancer qu'une restauration.");
        Equal(1, authentication.GetStatusCalls, "Le statut du dashboard ne doit être actualisé qu'une fois.");
        Equal(1, authentication.GetNewsCalls, "Les patch notes ne doivent être actualisées qu'une fois.");
    }

    private static async Task RuntimeEnablesInstallAndRefreshesDashboardOnceAsync()
    {
        using TemporaryClient client = new();
        FakeLauncherAuthService authentication = AuthForRuntime("InstallerUser");
        RuntimeMaintenanceStub maintenance = new();
        using LauncherRuntime runtime = CreateRuntime(client, authentication, new RuntimeVerificationStub(), maintenance);
        await runtime.InitializeAsync();
        authentication.ResetOperationCounters();

        LauncherSessionCompletion completion = await RequiredCompletion(
            runtime.TryLogin("InstallerUser", "transient-password"));

        Equal(LauncherSessionCompletionStatus.Succeeded, completion.Status, "La connexion runtime doit réussir.");
        True(runtime.Game.CurrentSnapshot.CanPrimaryAction, "Installer doit devenir actif après authentification.");
        Equal(GameAction.Install, runtime.Game.CurrentSnapshot.Action, "Le client absent doit rester Install.");
        Equal(1, authentication.GetStatusCalls, "Le dashboard doit être actualisé exactement une fois.");
        Equal(1, authentication.GetNewsCalls, "Les patch notes doivent être actualisées exactement une fois.");
        Equal(0, maintenance.Calls, "Aucune installation ne doit démarrer automatiquement.");
        Equal(0, authentication.CreateGameTicketCalls, "Aucun ticket de jeu ne doit être créé en 02F.2.");
    }

    private static async Task RuntimeEnablesUpdateAfterVerificationAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        RuntimeVerificationStub verification = new()
        {
            Result = new GameClientVerificationResult(
                GameVerificationOutcome.UpdateAvailable,
                GameAction.Update,
                GameUpdateKnowledge.Known,
                "remote-v2",
                2)
        };
        FakeLauncherAuthService authentication = AuthForRuntime("UpdateUser");
        RuntimeMaintenanceStub maintenance = new();
        using LauncherRuntime runtime = CreateRuntime(client, authentication, verification, maintenance);
        await runtime.InitializeAsync();
        await RequiredCompletion(runtime.TryLogin("UpdateUser", "transient-password"));

        Equal(GameVerificationStartStatus.Started, runtime.Game.TryStartVerification(), "La vérification doit pouvoir partir après connexion.");
        await runtime.Game.WaitForIdleAsync();

        Equal(GameAction.Update, runtime.Game.CurrentSnapshot.Action, "La vérification doit conserver Update.");
        True(runtime.Game.CurrentSnapshot.CanPrimaryAction, "Mettre à jour doit être actif après authentification.");
        Equal(0, maintenance.Calls, "La mise à jour ne doit pas démarrer automatiquement.");
        Equal(0, authentication.CreateGameTicketCalls, "Aucun ticket ne doit être créé.");
    }

    private static async Task RuntimeEnablesPlayWithoutLaunchingAsync()
    {
        using TemporaryClient client = new();
        client.CreatePlayableFiles();
        FakeLauncherAuthService authentication = AuthForRuntime("Player");
        using LauncherRuntime runtime = CreateRuntime(
            client,
            authentication,
            new RuntimeVerificationStub(),
            new RuntimeMaintenanceStub());
        await runtime.InitializeAsync();
        await RequiredCompletion(runtime.TryLogin("Player", "transient-password"));

        Equal(GameAction.Play, runtime.Game.CurrentSnapshot.Action, "Le client jouable doit rester Play.");
        True(runtime.Game.CurrentSnapshot.CanPrimaryAction, "Jouer doit être actif après authentification en 02F.3.");
        True(string.IsNullOrWhiteSpace(runtime.Game.CurrentSnapshot.PrimaryActionUnavailableReason), "Jouer actif ne doit pas conserver une ancienne raison de blocage.");
        Equal(0, authentication.CreateGameTicketCalls, "Aucun ticket ne doit être créé avant le clic Jouer.");
    }

    private static FakeLauncherAuthService AuthForRuntime(string username)
    {
        return new FakeLauncherAuthService
        {
            PrepareRestoreHandler = _ => Task.FromResult(new LauncherAuthRestoreAttempt(
                LauncherAuthRestoreOutcome.NoSession,
                null)),
            LoginHandler = (_, _, _) => Task.FromResult(
                FakeLauncherAuthService.CreateSession(username)),
            StatusHandler = _ => Task.FromResult(new LauncherServerStatus(
                "Arthas",
                true,
                true,
                true,
                true,
                true,
                DateTimeOffset.UtcNow)),
            NewsHandler = _ => Task.FromResult<IReadOnlyList<LauncherNews>>([])
        };
    }

    private static LauncherRuntime CreateRuntime(
        TemporaryClient client,
        FakeLauncherAuthService authentication,
        IGameClientVerificationService verification,
        IGameClientMaintenanceService maintenance)
    {
        return new LauncherRuntime(new LauncherRuntimeDependencies
        {
            LoadSettings = () => client.Settings,
            CreateAuthentication = () => authentication,
            GameClientStateReader = new GameClientStateReader(),
            GetLauncherVersion = () => "v1.1.0-test",
            CreateAuthorizedHttpClient = _ => new HttpClient(new RejectingHttpHandler()),
            CreateGameVerificationService = (_, _) => verification,
            CreateGameMaintenanceService = (_, _) => maintenance,
            HasPlayableClient = GameInstallServices.HasPlayableClient
        });
    }

    private static async Task CharacterizeRealWpfOverlayAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion))
        {
            IsBackground = true,
            Name = "AtlasAuthenticationRuntimeWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void RunWpfHarness(TaskCompletionSource completion)
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
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                await ValidateRealLoginFlowAsync();
                await ValidateRealRegistrationFlowAsync();
                await ValidateCloseDuringRealRequestAsync();
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

    private static async Task ValidateRealLoginFlowAsync()
    {
        using TemporaryClient client = new();
        TaskCompletionSource<LauncherAuthSession> firstLogin = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = AuthForRuntime("WpfUser");
        authentication.LoginHandler = (_, _, _) => firstLogin.Task;
        using LauncherRuntime runtime = CreateRuntime(
            client,
            authentication,
            new RuntimeVerificationStub(),
            new RuntimeMaintenanceStub());
        RuntimeAuthHarness harness = CreateRuntimeAuthHarness(runtime);
        try
        {
            True(!harness.Window.AuthState.IsOpen, "L'overlay ne doit pas clignoter avant la restauration.");
            harness.Window.Show();
            harness.Window.Activate();
            await PumpAsync(DispatcherPriority.Loaded);
            True(!harness.Window.AuthState.IsOpen, "L'overlay doit rester fermé pendant la restauration.");

            await runtime.InitializeAsync();
            await PumpAsync(DispatcherPriority.DataBind);
            True(!harness.Window.AuthState.IsOpen, "Aucune session enregistrée ne doit ouvrir automatiquement l'overlay.");
            Button profile = Required<Button>(harness.Window, "ProfileButton");
            True(profile.IsEnabled, "Le profil doit permettre la connexion après restauration.");

            Button friends = Required<Button>(harness.Window, "FriendsButton");
            RaiseClick(friends);
            True(harness.Window.FriendsState.IsOpen, "Le drawer Amis doit s'ouvrir avant l'authentification.");
            RaiseClick(profile);
            await DelayAndPumpAsync(220);
            True(harness.Window.AuthState.IsOpen, "Le profil déconnecté doit ouvrir l'overlay réel.");
            True(!harness.Window.FriendsState.IsOpen, "L'ouverture de l'authentification doit fermer les amis.");

            AuthOverlayViewV2 overlay = harness.Window.AuthenticationOverlay;
            TextBox username = Required<TextBox>(overlay, "LoginUsernameBox");
            PasswordBox password = Required<PasswordBox>(overlay, "LoginPasswordBox");
            Button submit = Required<Button>(overlay, "PrimaryAuthButton");
            username.Text = "WpfUser";
            password.Password = "transient-password";
            await PumpAsync(DispatcherPriority.DataBind);
            RaiseClick(submit);

            True(harness.Window.AuthState.IsBusy, "La connexion réelle doit passer immédiatement en busy.");
            True(!username.IsEnabled && !password.IsEnabled, "Les champs doivent être désactivés pendant la requête.");
            Equal(string.Empty, password.Password, "Le mot de passe doit être abandonné dès le démarrage.");
            Equal(1, authentication.LoginCalls, "Le premier clic doit produire une requête.");
            RaiseClick(submit);
            RaisePreviewKey(overlay, Key.Enter);
            Equal(1, authentication.LoginCalls, "Le double-clic et Entrée répétée doivent être refusés.");

            firstLogin.SetException(new LauncherAuthException(
                "raw server detail",
                HttpStatusCode.Unauthorized));
            await WaitForAsync(() => !harness.Window.AuthState.IsBusy);
            Equal(AuthErrorKind.InvalidCredentials, harness.Window.AuthState.ErrorKind, "L'erreur d'identifiants doit être contrôlée.");
            Equal("Identifiants incorrects.", harness.Window.AuthState.ErrorMessage, "Le détail serveur brut ne doit pas être affiché.");
            Equal("WpfUser", harness.Window.AuthState.LoginUsername, "Le nom doit être conservé après une erreur.");

            authentication.LoginHandler = (_, _, _) => Task.FromResult(
                FakeLauncherAuthService.CreateSession(
                    "WpfUser",
                    "wpf@example.test",
                    emailVerified: false));
            password.Password = "second-transient-password";
            await PumpAsync(DispatcherPriority.DataBind);
            RaiseClick(submit);
            await WaitForAsync(() => harness.Window.ShellState.IsAuthenticated);
            await DelayAndPumpAsync(220);

            Equal("WpfUser", harness.Window.ShellState.Username, "La barre supérieure doit afficher l'identité réelle.");
            Equal("W", harness.Window.ShellState.ProfileInitial, "L'initiale réelle est incorrecte.");
            True(overlay.IsFullyClosed, "Le succès doit fermer proprement l'overlay.");
            True(overlay.ArePasswordFieldsEmpty, "Tous les mots de passe doivent être nettoyés après succès.");
            Equal(profile, Keyboard.FocusedElement, "Le focus doit revenir au profil après fermeture.");
            True(harness.Window.GameState.ShowsNotification, "L'e-mail non vérifié doit produire une notification unique.");
            True(harness.Window.GameState.NotificationMessage.Contains("non vérifiée", StringComparison.OrdinalIgnoreCase), "La notification e-mail est incorrecte.");
            True(runtime.Game.CurrentSnapshot.CanPrimaryAction, "Installer doit être réévalué après connexion.");
            string? unavailableReason = runtime.Game.CurrentSnapshot.PrimaryActionUnavailableReason;
            True(
                string.IsNullOrWhiteSpace(unavailableReason)
                || !unavailableReason.Contains("Connexion requise", StringComparison.OrdinalIgnoreCase),
                "La raison Connexion requise doit disparaître.");
            Equal(0, authentication.CreateGameTicketCalls, "Le flux WPF ne doit créer aucun ticket.");
        }
        finally
        {
            harness.Dispose();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateCloseDuringRealRequestAsync()
    {
        using TemporaryClient client = new();
        TaskCompletionSource<LauncherAuthSession> firstLate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<LauncherAuthSession> secondLate = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int attempt = 0;
        FakeLauncherAuthService authentication = AuthForRuntime("LateUser");
        authentication.LoginHandler = (_, _, _) => ++attempt == 1
            ? firstLate.Task
            : secondLate.Task;
        using LauncherRuntime runtime = CreateRuntime(
            client,
            authentication,
            new RuntimeVerificationStub(),
            new RuntimeMaintenanceStub());
        RuntimeAuthHarness harness = CreateRuntimeAuthHarness(runtime);
        ShellUiState retainedShell = harness.Window.ShellState;
        try
        {
            harness.Window.Show();
            await runtime.InitializeAsync();
            await PumpAsync(DispatcherPriority.DataBind);
            Button profile = Required<Button>(harness.Window, "ProfileButton");
            RaiseClick(profile);
            await DelayAndPumpAsync(220);
            AuthOverlayViewV2 overlay = harness.Window.AuthenticationOverlay;
            Required<TextBox>(overlay, "LoginUsernameBox").Text = "LateUser";
            Required<PasswordBox>(overlay, "LoginPasswordBox").Password = "transient-password";
            RaiseClick(Required<Button>(overlay, "PrimaryAuthButton"));
            True(harness.Window.AuthState.IsBusy, "La requête tardive doit avoir démarré.");

            RaiseClick(Required<Button>(overlay, "CloseButton"));
            await DelayAndPumpAsync(220);
            True(overlay.IsFullyClosed, "Fermer doit rester disponible pendant busy.");
            firstLate.SetResult(FakeLauncherAuthService.CreateSession("Obsolete"));
            await DelayAndPumpAsync(80);
            True(!retainedShell.IsAuthenticated, "Le résultat tardif après fermeture doit être ignoré.");
            Equal(0, authentication.CommitSessionCalls, "La tentative fermée ne doit pas être stockée.");

            RaiseClick(profile);
            await DelayAndPumpAsync(220);
            Required<TextBox>(overlay, "LoginUsernameBox").Text = "LateUser";
            Required<PasswordBox>(overlay, "LoginPasswordBox").Password = "second-transient";
            RaiseClick(Required<Button>(overlay, "PrimaryAuthButton"));
            True(harness.Window.AuthState.IsBusy, "La seconde requête doit démarrer après annulation.");

            runtime.BeginShutdown();
            harness.DisposePresentationAndWindow();
            runtime.Dispose();
            secondLate.SetResult(FakeLauncherAuthService.CreateSession("AfterClose"));
            await DelayAndPumpAsync(80);
            True(!retainedShell.IsAuthenticated, "La fermeture de la fenêtre doit interdire toute mise à jour WPF tardive.");
            Equal(0, authentication.CommitSessionCalls, "La fermeture de l'application doit empêcher la validation tardive.");
        }
        finally
        {
            harness.Dispose();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateRealRegistrationFlowAsync()
    {
        using TemporaryClient client = new();
        TaskCompletionSource<LauncherAuthSession> registration = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeLauncherAuthService authentication = AuthForRuntime("RegisteredUser");
        authentication.RegisterHandler = (_, _, _, _) => registration.Task;
        using LauncherRuntime runtime = CreateRuntime(
            client,
            authentication,
            new RuntimeVerificationStub(),
            new RuntimeMaintenanceStub());
        RuntimeAuthHarness harness = CreateRuntimeAuthHarness(runtime);
        try
        {
            harness.Window.Show();
            await runtime.InitializeAsync();
            await PumpAsync(DispatcherPriority.DataBind);
            RaiseClick(Required<Button>(harness.Window, "ProfileButton"));
            await DelayAndPumpAsync(220);
            AuthOverlayViewV2 overlay = harness.Window.AuthenticationOverlay;
            harness.Window.AuthState.ShowRegisterCommand.Execute(null);
            await PumpAsync(DispatcherPriority.Input);

            TextBox username = Required<TextBox>(overlay, "RegisterUsernameBox");
            TextBox email = Required<TextBox>(overlay, "RegisterEmailBox");
            PasswordBox password = Required<PasswordBox>(overlay, "RegisterPasswordBox");
            PasswordBox confirmation = Required<PasswordBox>(overlay, "RegisterPasswordConfirmBox");
            Button submit = Required<Button>(overlay, "PrimaryAuthButton");
            username.Text = "RegisteredUser";
            email.Text = "registered@example.test";
            password.Password = "0123456789";
            confirmation.Password = "different0";
            overlay.ValidateForPreview(showErrors: true);
            Equal(AuthErrorKind.Validation, harness.Window.AuthState.ErrorKind, "La confirmation différente doit rester locale.");
            Equal(0, authentication.RegisterCalls, "Le formulaire invalide ne doit produire aucune requête.");

            confirmation.Password = "0123456789";
            await PumpAsync(DispatcherPriority.DataBind);
            RaiseClick(submit);
            True(harness.Window.AuthState.IsBusy, "L'inscription réelle doit passer en Registering.");
            True(!username.IsEnabled && !email.IsEnabled, "Le formulaire d'inscription doit être désactivé pendant l'envoi.");
            True(!harness.Window.AuthState.ShowLoginCommand.CanExecute(null), "La navigation doit être désactivée pendant l'inscription.");
            Equal(string.Empty, password.Password, "Le mot de passe d'inscription doit être abandonné immédiatement.");
            Equal(string.Empty, confirmation.Password, "La confirmation doit être abandonnée immédiatement.");
            RaiseClick(submit);
            Equal(1, authentication.RegisterCalls, "La double inscription doit être bloquée dans WPF.");

            registration.SetResult(FakeLauncherAuthService.CreateSession(
                "RegisteredUser",
                "registered@example.test",
                emailVerified: true));
            await WaitForAsync(() => harness.Window.ShellState.IsAuthenticated);
            await DelayAndPumpAsync(220);
            Equal("RegisteredUser", harness.Window.ShellState.Username, "L'identité inscrite doit atteindre la barre supérieure.");
            True(overlay.IsFullyClosed, "L'inscription réussie doit fermer l'overlay.");
            Equal(0, authentication.LoginCalls, "L'endpoint existant fournit directement la session sans second appel de connexion.");
            Equal(0, authentication.CreateGameTicketCalls, "L'inscription ne doit jamais lancer le jeu.");
        }
        finally
        {
            harness.Dispose();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static RuntimeAuthHarness CreateRuntimeAuthHarness(LauncherRuntime runtime)
    {
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
            ShowInTaskbar = false,
            ShowActivated = true
        };
        AuthCommands commands = new(runtime);
        window.AttachAuthentication(commands);
        AuthStateAdapter adapter = new(
            window.AuthState,
            shell,
            game,
            runtime.Session,
            window.Dispatcher);
        return new RuntimeAuthHarness(window, commands, adapter);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Le scénario WPF n'a pas atteint l'état attendu.");
            }

            await DelayAndPumpAsync(15);
        }
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
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

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement
    {
        return scope.FindName(name) as T
            ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");
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

    private static async Task<LauncherSessionCompletion> RequiredCompletion(
        LauncherSessionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null, $"La tentative aurait dû démarrer ({start.Status}).");
        return await start.Completion!;
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

    private sealed class RejectingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Aucun HttpClient métier ne doit être utilisé par ce test.");
        }
    }

    private sealed class RuntimeAuthHarness : IDisposable
    {
        private int _disposeState;

        internal RuntimeAuthHarness(
            LauncherShellV2 window,
            AuthCommands commands,
            AuthStateAdapter adapter)
        {
            Window = window;
            Commands = commands;
            Adapter = adapter;
        }

        internal LauncherShellV2 Window { get; }

        private AuthCommands Commands { get; }

        private AuthStateAdapter Adapter { get; }

        internal void DisposePresentationAndWindow()
        {
            Adapter.Dispose();
            Commands.Dispose();
            if (Window.IsLoaded)
            {
                Window.Close();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            {
                return;
            }

            DisposePresentationAndWindow();
        }
    }
}
