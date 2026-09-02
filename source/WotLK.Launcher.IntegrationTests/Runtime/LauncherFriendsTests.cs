using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Reflection;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

internal static class LauncherFriendsTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeSecretFreeImmutableState();
        await RestoreAndLoadRealRelationshipsAsync();
        await ExecuteSupportedActionsAsync();
        await RejectConcurrentOperationsImmediatelyAsync();
        await MapActionFailuresAsync();
        await MapFailuresAndProtectSessionAsync();
        await IgnoreLateCompletionAfterShutdownAsync();
        Console.WriteLine("Atlas friends runtime integration OK (03B).\n");
        return 0;
    }

    private static void CharacterizeSecretFreeImmutableState()
    {
        True(typeof(FriendsRuntimeSnapshot).IsSealed, "Le snapshot Amis doit rester immuable.");
        string[] forbidden = ["AccessToken", "RefreshToken", "Password", "Authorization", "Ticket"];
        foreach (Type type in new[]
        {
            typeof(FriendsRuntimeSnapshot),
            typeof(FriendRuntimeItem),
            typeof(FriendsViewState),
            typeof(FriendUiItem)
        })
        {
            string[] properties = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(property => property.Name)
                .ToArray();
            foreach (string name in forbidden)
            {
                True(!properties.Contains(name, StringComparer.OrdinalIgnoreCase),
                    $"{type.Name} ne doit pas exposer {name}.");
            }
        }

        Equal(
            "Impossible de joindre Atlas pour le moment.",
            FriendsStateAdapter.MapError(FriendsErrorCategory.Network),
            "Une erreur réseau doit être traduite sans détail brut.");
        Equal(
            "Ta session Atlas doit être renouvelée.",
            FriendsStateAdapter.MapError(FriendsErrorCategory.Unauthorized),
            "Une session expirée doit être présentée proprement.");
    }

    private static async Task RestoreAndLoadRealRelationshipsAsync()
    {
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync();
        environment.Authentication.FriendsHandler = _ =>
            Task.FromResult<IReadOnlyList<LauncherFriend>>([]);
        FriendsActionCompletion empty = await RequiredCompletion(environment.Friends.TryRefresh());
        True(empty.Snapshot.Friends.IsEmpty
            && empty.Snapshot.IncomingRequests.IsEmpty
            && empty.Snapshot.OutgoingRequests.IsEmpty,
            "Une liste vide réelle doit rester vide.");
        True(FriendsStateAdapter.Project(empty.Snapshot).ShowsGlobalEmpty,
            "La présentation doit exposer l’état vide global.");

        DateTimeOffset lastSeen = new(2026, 8, 29, 20, 15, 0, TimeSpan.Zero);
        environment.Authentication.FriendsHandler = _ => Task.FromResult<IReadOnlyList<LauncherFriend>>(
        [
            Friend(4, "offline", "accepted", online: false, lastSeenAt: lastSeen),
            Friend(2, "online", "accepted", online: true, characterName: "Ophntfranck", level: 12, classId: 8),
            Friend(8, "incoming", "incoming"),
            Friend(9, "outgoing", "outgoing"),
            Friend(10, "ignored", "unsupported")
        ]);

        FriendsActionCompletion completion = await RequiredCompletion(environment.Friends.TryRefresh());
        Equal(FriendsActionCompletionStatus.Succeeded, completion.Status, "Le chargement réel doit réussir.");
        FriendsRuntimeSnapshot snapshot = completion.Snapshot;
        Equal(FriendsLoadState.Loaded, snapshot.LoadState, "Le chargement doit devenir Loaded.");
        Equal(2, snapshot.Friends.Length, "Les relations acceptées doivent être séparées.");
        Equal("online", snapshot.Friends[0].Username, "Les amis en ligne doivent être listés en premier.");
        Equal(1, snapshot.IncomingRequests.Length, "La demande reçue doit être conservée.");
        Equal(1, snapshot.OutgoingRequests.Length, "La demande envoyée doit être conservée.");
        Equal(2, environment.Authentication.GetFriendsCalls,
            "Chaque rafraîchissement doit produire exactement un appel liste.");

        FriendsViewState view = FriendsStateAdapter.Project(snapshot);
        Equal(1, view.OnlineCount, "Le compteur en jeu doit provenir des données réelles.");
        Equal("Mage · niveau 12", view.Friends[0].CharacterDetails, "La classe et le niveau doivent être projetés.");
        True(view.Friends[0].HasAvatarTheme, "Le thème d’avatar legacy doit être conservé.");
        True(view.Friends[1].PresenceText.StartsWith("Hors ligne · vu le", StringComparison.Ordinal),
            "La dernière présence doit être affichée sans inventer un statut en ligne.");
        True(!view.IncomingRequests[0].HasCharacter, "Une demande sans personnage doit rester sans détail fictif.");
        True(!view.OutgoingRequests[0].HasAvatarTheme,
            "L’absence d’avatar doit utiliser le fallback par initiale.");
    }

    private static async Task ExecuteSupportedActionsAsync()
    {
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync();
        IReadOnlyList<LauncherFriend> current =
        [
            Friend(2, "friend", "accepted"),
            Friend(3, "requester", "incoming"),
            Friend(4, "pending", "outgoing")
        ];
        environment.Authentication.FriendsHandler = _ => Task.FromResult(current);
        await RequiredCompletion(environment.Friends.TryRefresh());

        FriendsActionCompletion accepted = await RequiredCompletion(
            environment.Friends.TryAcceptRequest(3));
        Equal(FriendsActionCompletionStatus.Succeeded, accepted.Status, "Accepter doit réussir.");
        True(accepted.Snapshot.Friends.Any(item => item.AccountId == 3), "La demande acceptée doit rejoindre les amis.");
        True(accepted.Snapshot.IncomingRequests.All(item => item.AccountId != 3), "La demande acceptée doit disparaître.");
        Equal(1, environment.Authentication.AcceptFriendCalls, "L’endpoint d’acceptation doit être appelé une fois.");

        FriendsActionCompletion cancelled = await RequiredCompletion(
            environment.Friends.TryCancelRequest(4));
        Equal(FriendsNoticeKind.RequestCancelled, cancelled.Snapshot.Notice, "L’annulation sortante doit être identifiée.");
        True(cancelled.Snapshot.OutgoingRequests.IsEmpty, "La demande annulée doit disparaître.");

        FriendsActionCompletion removed = await RequiredCompletion(
            environment.Friends.TryRemoveFriend(2));
        Equal(FriendsNoticeKind.FriendRemoved, removed.Snapshot.Notice, "Le retrait d’un ami doit être identifié.");
        True(removed.Snapshot.Friends.All(item => item.AccountId != 2), "L’ami retiré doit disparaître.");

        current = [Friend(6, "refused", "incoming")];
        await RequiredCompletion(environment.Friends.TryRefresh());
        FriendsActionCompletion rejected = await RequiredCompletion(
            environment.Friends.TryRejectRequest(6));
        Equal(FriendsNoticeKind.RequestRejected, rejected.Snapshot.Notice, "Le refus entrant doit être identifié.");
        Equal(3, environment.Authentication.RemoveFriendCalls,
            "Refuser, annuler et retirer doivent réutiliser le DELETE existant.");

        current = [Friend(7, "newfriend", "outgoing")];
        FriendsActionCompletion sent = await RequiredCompletion(
            environment.Friends.TrySendRequest("  newfriend  "));
        Equal(FriendsActionCompletionStatus.Succeeded, sent.Status, "L’envoi doit réussir.");
        Equal(FriendsNoticeKind.RequestSent, sent.Snapshot.Notice, "Une demande sortante doit être annoncée.");
        Equal(1, environment.Authentication.SendFriendRequestCalls, "L’envoi doit être appelé une fois.");
        Equal(3, environment.Authentication.GetFriendsCalls,
            "L’envoi doit recharger une fois la liste réelle après la mutation.");

        Equal(FriendsActionStartStatus.InvalidRequest,
            environment.Friends.TrySendRequest("x").Status,
            "Un nom trop court doit être refusé avant l’API.");
        Equal(1, environment.Authentication.SendFriendRequestCalls,
            "Une validation locale ne doit pas appeler l’API.");
    }

    private static async Task RejectConcurrentOperationsImmediatelyAsync()
    {
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<LauncherFriend>> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Authentication.FriendsHandler = _ =>
        {
            started.TrySetResult();
            return release.Task;
        };

        FriendsActionStartResult first = environment.Friends.TryRefresh();
        True(first.IsStarted, "La première actualisation doit démarrer.");
        Equal(FriendsActionStartStatus.Busy, environment.Friends.TryRefresh().Status,
            "Un second rafraîchissement doit être refusé immédiatement.");
        Equal(FriendsActionStartStatus.Busy, environment.Friends.TrySendRequest("another").Status,
            "Une mutation ne doit pas être mise en file derrière le chargement.");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Equal(1, environment.Authentication.GetFriendsCalls, "Une seule requête doit être active.");

        release.TrySetResult([]);
        await RequiredCompletion(first);
        FriendsActionCompletion retry = await RequiredCompletion(environment.Friends.TryRefresh());
        Equal(FriendsActionCompletionStatus.Succeeded, retry.Status,
            "Une nouvelle opération doit être possible après la fin.");
    }

    private static async Task MapFailuresAndProtectSessionAsync()
    {
        List<string> logs = [];
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync(logs.Add);
        const string secret = "access-token-super-secret";
        environment.Authentication.FriendsHandler = _ =>
            Task.FromException<IReadOnlyList<LauncherFriend>>(
                new HttpRequestException(secret));
        FriendsActionCompletion network = await RequiredCompletion(environment.Friends.TryRefresh());
        Equal(FriendsErrorCategory.Network, network.Snapshot.ErrorState.Category,
            "Une panne réseau doit rester distincte.");
        True(!string.Join('\n', logs).Contains(secret, StringComparison.Ordinal),
            "Le détail sensible d’une exception ne doit pas être journalisé.");

        environment.Authentication.FriendsHandler = _ =>
            Task.FromException<IReadOnlyList<LauncherFriend>>(
                new LauncherAuthException("session raw detail", HttpStatusCode.Unauthorized));
        FriendsActionCompletion unauthorized = await RequiredCompletion(environment.Friends.TryRefresh());
        Equal(FriendsActionCompletionStatus.Superseded, unauthorized.Status,
            "L’invalidation de session doit rendre l’ancien résultat obsolète.");
        True(!environment.Friends.CurrentSnapshot.IsAuthenticated,
            "Un 401 doit être délégué au coordinateur de session.");
        Equal(FriendsLoadState.SignedOut, environment.Friends.CurrentSnapshot.LoadState,
            "Les données sociales doivent disparaître après expiration de session.");
    }

    private static async Task MapActionFailuresAsync()
    {
        (Exception Failure, FriendsErrorCategory Category, string Label)[] failures =
        [
            (new LauncherAuthException("unknown raw", HttpStatusCode.NotFound),
                FriendsErrorCategory.UserNotFound, "utilisateur introuvable"),
            (new LauncherAuthException("Tu ne peux pas t'ajouter toi-même.", HttpStatusCode.BadRequest),
                FriendsErrorCategory.Self, "demande à soi-même"),
            (new LauncherAuthException("Une demande est déjà en attente pour ce compte.", HttpStatusCode.Conflict),
                FriendsErrorCategory.AlreadyPending, "demande déjà envoyée"),
            (new LauncherAuthException("Ce compte fait déjà partie de tes amis.", HttpStatusCode.Conflict),
                FriendsErrorCategory.AlreadyFriends, "déjà ami"),
            (new TaskCanceledException("timeout raw"), FriendsErrorCategory.Timeout, "timeout"),
            (new LauncherAuthException("server raw", HttpStatusCode.ServiceUnavailable),
                FriendsErrorCategory.ServiceUnavailable, "serveur")
        ];

        foreach ((Exception failure, FriendsErrorCategory category, string label) in failures)
        {
            await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync();
            environment.Authentication.SendFriendRequestHandler = (_, _) =>
                Task.FromException<string>(failure);
            FriendsActionCompletion result = await RequiredCompletion(
                environment.Friends.TrySendRequest("target"));
            Equal(FriendsActionCompletionStatus.Failed, result.Status,
                $"Le cas {label} doit échouer proprement.");
            Equal(category, result.Snapshot.ErrorState.Category,
                $"Le cas {label} est mal catégorisé.");
            Equal(0, environment.Authentication.GetFriendsCalls,
                $"Le cas {label} ne doit pas rafraîchir après un envoi refusé.");
        }

        await using FriendsEnvironment forbidden = await FriendsEnvironment.CreateAsync();
        forbidden.Authentication.AcceptFriendHandler = (_, _) => Task.FromException(
            new LauncherAuthException("forbidden raw", HttpStatusCode.Forbidden));
        FriendsActionCompletion forbiddenResult = await RequiredCompletion(
            forbidden.Friends.TryAcceptRequest(42));
        Equal(FriendsErrorCategory.Forbidden, forbiddenResult.Snapshot.ErrorState.Category,
            "Un refus d’autorisation doit rester distinct.");
    }

    private static async Task IgnoreLateCompletionAfterShutdownAsync()
    {
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Authentication.FriendsHandler = async _ =>
        {
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return [Friend(12, "late", "accepted")];
        };
        int notifications = 0;
        environment.Friends.SnapshotChanged += (_, _) => notifications++;
        FriendsActionStartResult operation = environment.Friends.TryRefresh();
        await started.Task;
        int beforeShutdown = notifications;
        environment.Friends.BeginShutdown();
        environment.Friends.BeginShutdown();
        release.TrySetResult();
        FriendsActionCompletion result = await RequiredCompletion(operation);

        Equal(FriendsActionCompletionStatus.Superseded, result.Status,
            "Un résultat tardif après fermeture doit être obsolète.");
        Equal(beforeShutdown, notifications, "Aucun snapshot tardif ne doit atteindre la présentation.");
        True(await environment.Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1)),
            "La tâche ignorant l’annulation doit rester observée.");
        Equal(FriendsActionStartStatus.ShuttingDown, environment.Friends.TryRefresh().Status,
            "Une opération après fermeture doit être refusée immédiatement.");
    }

    private static LauncherFriend Friend(
        uint accountId,
        string username,
        string relationship,
        bool online = false,
        string? characterName = null,
        byte? level = null,
        byte? classId = null,
        DateTimeOffset? lastSeenAt = null) =>
        new(accountId, username, accountId % 2 == 0 ? "ice" : null, relationship,
            online, characterName, level, classId, 1, lastSeenAt);

    private static async Task<FriendsActionCompletion> RequiredCompletion(
        FriendsActionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null,
            $"L’opération Amis devait démarrer, statut={start.Status}.");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }

    private sealed class FriendsEnvironment : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private readonly LauncherSessionCoordinator _session;

        private FriendsEnvironment(
            FakeLauncherAuthService authentication,
            CancellationTokenSource lifetime,
            LauncherSessionCoordinator session,
            LauncherFriendsCoordinator friends)
        {
            Authentication = authentication;
            _lifetime = lifetime;
            _session = session;
            Friends = friends;
        }

        internal FakeLauncherAuthService Authentication { get; }

        internal LauncherFriendsCoordinator Friends { get; }

        internal static async Task<FriendsEnvironment> CreateAsync(Action<string>? log = null)
        {
            FakeLauncherAuthService authentication = new()
            {
                RestoreResult = true,
                Session = FakeLauncherAuthService.CreateSession(),
                EnsureFreshHandler = _ => Task.FromResult(true)
            };
            CancellationTokenSource lifetime = new();
            LauncherSessionCoordinator session = new(authentication, lifetime.Token, log ?? (_ => { }));
            LauncherFriendsCoordinator friends = new(
                session,
                authentication,
                lifetime.Token,
                () => authentication.Session?.Profile,
                log ?? (_ => { }));
            FriendsEnvironment environment = new(authentication, lifetime, session, friends);
            LauncherSessionRestoreResult restore = await session.RestoreOnceAsync();
            Equal(LauncherSessionRestoreStatus.Restored, restore.Status,
                "La session du harnais Amis doit être restaurée.");
            True(friends.CurrentSnapshot.IsAuthenticated,
                "Le coordinateur Amis doit suivre la restauration de session.");
            return environment;
        }

        public async ValueTask DisposeAsync()
        {
            Friends.BeginShutdown();
            _session.BeginShutdown();
            _lifetime.Cancel();
            await Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1));
            Friends.Dispose();
            _session.Dispose();
            _lifetime.Dispose();
        }
    }
}
