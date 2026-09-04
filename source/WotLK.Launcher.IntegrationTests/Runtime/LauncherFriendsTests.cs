using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;

internal static class LauncherFriendsTests
{
    internal static async Task<int> RunAsync()
    {
        CharacterizeSecretFreeImmutableState();
        CharacterizeSocialAvatarContractAndQueryShape();
        CharacterizeDesktopNotifications();
        await RestoreAndLoadRealRelationshipsAsync();
        await RefreshFromOneSessionAwareTimerAsync();
        await CoalesceTimerAndManualRefreshAsync();
        await PreserveKnownDataAfterAutomaticFailureAsync();
        await InvalidateSessionAfterAutomaticUnauthorizedAsync();
        await ExecuteSupportedActionsAsync();
        await RejectConcurrentOperationsImmediatelyAsync();
        await MapActionFailuresAsync();
        await MapFailuresAndProtectSessionAsync();
        await IgnoreLateCompletionAfterShutdownAsync();
        Console.WriteLine("Atlas friends runtime integration OK (03B.1).\n");
        return 0;
    }

    private static void CharacterizeDesktopNotifications()
    {
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        LauncherSettings settings = new()
        {
            FriendPresenceNotifications = true
        };
        using LauncherOperationCoordinator operations = new();
        using LauncherSettingsCoordinator settingsRuntime = new(
            settings,
            operations,
            static _ => { },
            static _ => { },
            static _ => { });
        FakeDesktopNotificationSink notifications = new();
        using LauncherFriendsNotificationCoordinator coordinator = new(
            settingsRuntime,
            notifications,
            static _ => { });

        coordinator.Observe(NotificationSnapshot(
            1,
            friends: [NotificationFriend(2, "Alice", online: false)],
            incoming: [NotificationRequest(8, "Initiale")]));
        Equal(0, notifications.Messages.Count,
            "Le premier chargement doit uniquement établir la baseline.");

        coordinator.Observe(NotificationSnapshot(
            1,
            friends: [NotificationFriend(2, "Alice", online: true)],
            incoming: [NotificationRequest(8, "Initiale")]));
        Equal(1, notifications.Messages.Count,
            "Un ami passant hors ligne vers en ligne doit produire une alerte.");
        Equal("Ami connecté", notifications.Messages[0].Title,
            "Le titre de présence doit rester utilisateur.");
        True(notifications.Messages[0].PlaySound,
            "La connexion d'un ami doit jouer le son discret demandé.");

        coordinator.Observe(NotificationSnapshot(
            1,
            friends: [NotificationFriend(2, "Alice", online: true)],
            incoming: [NotificationRequest(8, "Initiale")]));
        Equal(1, notifications.Messages.Count,
            "Un rafraîchissement identique ne doit pas répéter l'alerte.");

        coordinator.Observe(NotificationSnapshot(
            1,
            friends: [NotificationFriend(2, "Alice", online: false)],
            incoming: [NotificationRequest(8, "Initiale")]));
        _ = settingsRuntime.TrySetFriendPresenceNotifications(false);
        coordinator.Observe(NotificationSnapshot(
            1,
            friends: [NotificationFriend(2, "Alice", online: true)],
            incoming: [NotificationRequest(8, "Initiale")]));
        Equal(1, notifications.Messages.Count,
            "Le réglage désactivé doit couper la notification de connexion.");

        coordinator.Observe(NotificationSnapshot(
            1,
            friends: [NotificationFriend(2, "Alice", online: true)],
            incoming:
            [
                NotificationRequest(8, "Initiale"),
                NotificationRequest(9, "Bob")
            ]));
        Equal(2, notifications.Messages.Count,
            "Une nouvelle demande doit rester notifiée indépendamment du réglage de présence.");
        Equal("Nouvelle demande d’ami", notifications.Messages[1].Title,
            "La demande d'ami doit être identifiable sans détail technique.");
        True(!notifications.Messages[1].PlaySound,
            "Le son de connexion ne doit pas être joué pour une demande.");

        coordinator.Observe(FriendsRuntimeSnapshot.SignedOut);
        coordinator.Observe(NotificationSnapshot(
            1,
            friends: [NotificationFriend(2, "Alice", online: true)],
            incoming: [NotificationRequest(10, "Après reconnexion")]));
        Equal(2, notifications.Messages.Count,
            "Une reconnexion doit recréer une baseline sans avalanche de notifications.");
    }

    private static FriendsRuntimeSnapshot NotificationSnapshot(
        uint currentUserId,
        IReadOnlyList<FriendRuntimeItem> friends,
        IReadOnlyList<FriendRuntimeItem> incoming)
    {
        return FriendsRuntimeSnapshot.SignedOut with
        {
            Sequence = DateTime.UtcNow.Ticks,
            CurrentUserId = currentUserId,
            IsAuthenticated = true,
            LoadState = FriendsLoadState.Loaded,
            Friends = friends.ToImmutableArray(),
            IncomingRequests = incoming.ToImmutableArray(),
            OperationState = FriendsOperationState.None
        };
    }

    private static FriendRuntimeItem NotificationFriend(
        uint accountId,
        string username,
        bool online) => new(
            accountId,
            username,
            AvatarKey: null,
            Avatar: null,
            FriendRelationship.Accepted,
            IsOnline: online,
            CharacterName: null,
            Level: null,
            ClassId: null,
            ZoneId: null,
            LastSeenAt: null);

    private static FriendRuntimeItem NotificationRequest(
        uint accountId,
        string username) => new(
            accountId,
            username,
            AvatarKey: null,
            Avatar: null,
            FriendRelationship.Incoming,
            IsOnline: false,
            CharacterName: null,
            Level: null,
            ClassId: null,
            ZoneId: null,
            LastSeenAt: null);

    private static void CharacterizeSocialAvatarContractAndQueryShape()
    {
        Guid avatarId = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479");
        WotLK.Launcher.Server.Avatars.AvatarDescriptor serverAvatar =
            WotLK.Launcher.Server.Avatars.AvatarDescriptor.Create(avatarId, 7);
        WotLK.Launcher.Server.LauncherFriend serverFriend = new(
            42,
            "atlasfriend",
            "ice",
            "accepted",
            true,
            "Arthasfriend",
            80,
            8,
            210,
            null,
            serverAvatar,
            "Disponible pour un raid",
            "Mage de Norfendre.",
            [
                new WotLK.Launcher.Server.LauncherFriendCharacter(
                    "Arthasfriend", 80, 8, 210, true, null),
                new WotLK.Launcher.Server.LauncherFriendCharacter(
                    "Altfriend", 72, 5, 4395, false, DateTimeOffset.UtcNow)
            ]);
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        string payload = JsonSerializer.Serialize(serverFriend, json);
        WotLK.Launcher.LauncherFriend client = JsonSerializer.Deserialize<WotLK.Launcher.LauncherFriend>(
            payload,
            json)
            ?? throw new InvalidOperationException("Le DTO ami enrichi est illisible côté launcher.");
        Equal(avatarId, client.Avatar?.AvatarId,
            "Le descripteur avatar public doit traverser le contrat Friends.");
        Equal(7UL, client.Avatar?.Version,
            "La version avatar doit rester exacte dans le contrat Friends.");
        Equal("Disponible pour un raid", client.StatusMessage,
            "Le statut public doit traverser le contrat Friends.");
        Equal("Mage de Norfendre.", client.Bio,
            "La bio doit traverser le contrat Friends.");
        Equal(2, client.Characters?.Count ?? 0,
            "Tous les personnages doivent traverser le contrat Friends.");
        True(client.Avatar?.Url64.EndsWith("/64.png", StringComparison.Ordinal) == true,
            "Le contrat social doit fournir la variante 64 px.");

        const string oldPayload = """
            {"accountId":9,"username":"legacy","avatarKey":"gold","relationship":"accepted","online":false,"characterName":null,"level":null,"classId":null,"zoneId":null,"lastSeenAt":null}
            """;
        WotLK.Launcher.LauncherFriend oldClient = JsonSerializer.Deserialize<WotLK.Launcher.LauncherFriend>(
            oldPayload,
            json)
            ?? throw new InvalidOperationException("L’ancien contrat ami est illisible.");
        True(oldClient.Avatar is null,
            "Un ancien serveur sans propriété Avatar doit conserver le fallback.");
        LegacyFriendContract legacyClient = JsonSerializer.Deserialize<LegacyFriendContract>(payload, json)
            ?? throw new InvalidOperationException("Le contrat enrichi est illisible par un ancien client.");
        Equal("atlasfriend", legacyClient.Username,
            "Un ancien client doit ignorer la propriété Avatar ajoutée.");

        Equal(2, WotLK.Launcher.Server.LauncherDatabase.FriendListMaximumQueryCount,
            "La liste sociale doit utiliser au plus une requête profil/avatar et une requête personnages groupée.");
        True(WotLK.Launcher.Server.LauncherDatabase.FriendAccountsQuery.Contains(
                "INNER JOIN atlas_launcher_profile",
                StringComparison.Ordinal)
            && WotLK.Launcher.Server.LauncherDatabase.FriendAccountsQuery.Contains(
                "LEFT JOIN atlas_launcher_profile_avatar",
                StringComparison.Ordinal)
            && WotLK.Launcher.Server.LauncherDatabase.FriendAccountsQuery.Contains(
                "LEFT JOIN atlas_launcher_avatar_asset",
                StringComparison.Ordinal),
            "Les profils Atlas et leurs avatars doivent être chargés par la requête sociale groupée.");
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
            Friend(2, "online", "accepted", online: true, characterName: "Ophntfranck", level: 12, classId: 8, zoneId: 210),
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
        Equal("2 amis · 1 en jeu", view.FriendsSummary, "Le résumé doit regrouper le total et les amis en jeu.");
        Equal(1, view.OnlineFriends.Length, "La section en jeu doit être projetée séparément.");
        Equal(1, view.OfflineFriends.Length, "La section hors ligne doit être projetée séparément.");
        Equal("Mage niveau 12", view.Friends[0].CharacterDetails, "La classe et le niveau doivent être projetés.");
        Equal("Ophntfranck · Mage niveau 12", view.Friends[0].CharacterSummary,
            "Le personnage doit tenir sur une seule ligne compacte.");
        Equal("En jeu · Couronne de glace", view.Friends[0].PresenceText,
            "La zone de jeu connue doit être visible.");
        True(view.Friends[0].HasAvatarTheme, "Le thème d’avatar legacy doit être conservé.");
        True(view.Friends[1].PresenceText.StartsWith("Vu le", StringComparison.Ordinal),
            "La dernière présence doit être affichée sans inventer un statut en ligne.");
        True(!view.IncomingRequests[0].HasCharacter, "Une demande sans personnage doit rester sans détail fictif.");
        True(!view.OutgoingRequests[0].HasAvatarTheme,
            "L’absence d’avatar doit utiliser le fallback par initiale.");

        DateTime localMidday = DateTime.Today.AddHours(12);
        DateTimeOffset localNow = new(localMidday, TimeZoneInfo.Local.GetUtcOffset(localMidday));
        FriendRuntimeItem seenToday = new(
            20,
            "relative",
            null,
            null,
            FriendRelationship.Accepted,
            false,
            null,
            null,
            null,
            null,
            localNow.AddMinutes(-20));
        True(FriendsStateAdapter.GetPresenceText(seenToday, localNow).StartsWith("Aujourd’hui à", StringComparison.Ordinal),
            "Une activité du jour doit utiliser une date relative.");
    }

    private static async Task RefreshFromOneSessionAwareTimerAsync()
    {
        ManualFriendsTimeProvider time = new();
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync(
            timeProvider: time,
            authenticated: false);
        Equal(1, time.CreateTimerCalls,
            "Un coordinateur Amis doit posséder exactement un timer.");
        True(!time.Timer.IsEnabled,
            "Le timer social doit rester inactif sans session Atlas.");
        time.Timer.Fire();
        Equal(0, environment.Authentication.GetFriendsCalls,
            "Aucun tick ne doit appeler Friends sans session.");

        LauncherSessionStartResult login = environment.Session.TryLogin("Dono1402", "password");
        await RequiredSessionCompletion(login);
        True(time.Timer.IsEnabled,
            "Le timer social doit démarrer après connexion.");
        Equal(LauncherFriendsCoordinator.AutomaticRefreshInterval, time.Timer.DueTime,
            "Le premier tick doit être planifié à 15 secondes.");
        Equal(LauncherFriendsCoordinator.AutomaticRefreshInterval, time.Timer.Period,
            "La cadence sociale doit être exactement de 15 secondes.");

        time.Timer.Fire();
        True(await environment.Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1)),
            "Le rafraîchissement automatique doit être observé.");
        Equal(1, environment.Authentication.GetFriendsCalls,
            "Un tick doit produire exactement un GET Friends.");

        LauncherSessionStartResult logout = environment.Session.TryLogout(CancellationToken.None);
        await RequiredSessionCompletion(logout);
        True(!time.Timer.IsEnabled,
            "Le timer social doit s’arrêter après déconnexion.");
        time.Timer.Fire();
        Equal(1, environment.Authentication.GetFriendsCalls,
            "Aucun appel ne doit partir après déconnexion.");

        LauncherSessionStartResult secondLogin = environment.Session.TryLogin("Dono1402", "password");
        await RequiredSessionCompletion(secondLogin);
        Equal(1, time.CreateTimerCalls,
            "Une reconnexion doit réutiliser le timer existant.");
        time.Timer.Fire();
        True(await environment.Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1)),
            "Le timer doit reprendre après reconnexion.");
        Equal(2, environment.Authentication.GetFriendsCalls,
            "La reconnexion doit réactiver un seul flux périodique.");

        environment.Friends.BeginShutdown();
        True(!time.Timer.IsEnabled,
            "Le timer doit être arrêté avant la libération du runtime.");
        time.Timer.Fire();
        Equal(2, environment.Authentication.GetFriendsCalls,
            "La fermeture ne doit produire aucun tick tardif.");
    }

    private static async Task CoalesceTimerAndManualRefreshAsync()
    {
        ManualFriendsTimeProvider time = new();
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync(
            timeProvider: time);
        TaskCompletionSource<IReadOnlyList<WotLK.Launcher.LauncherFriend>> release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Authentication.FriendsHandler = _ => release.Task;

        FriendsActionStartResult manual = environment.Friends.TryRefresh();
        await WaitForAsync(() => environment.Authentication.GetFriendsCalls == 1);
        time.Timer.Fire();
        Equal(1, environment.Authentication.GetFriendsCalls,
            "Un tick ne doit pas doubler un rafraîchissement manuel actif.");
        release.TrySetResult([]);
        await RequiredCompletion(manual);

        TaskCompletionSource<IReadOnlyList<WotLK.Launcher.LauncherFriend>> automaticRelease = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Authentication.FriendsHandler = _ => automaticRelease.Task;
        time.Timer.Fire();
        await WaitForAsync(() => environment.Authentication.GetFriendsCalls == 2);
        Equal(FriendsActionStartStatus.Busy, environment.Friends.TryRefresh().Status,
            "Un clic manuel pendant le tick doit être refusé immédiatement.");
        automaticRelease.TrySetResult([]);
        True(await environment.Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1)),
            "Le tick actif doit se terminer sans file d’attente.");
        Equal(2, environment.Authentication.GetFriendsCalls,
            "Aucun rafraîchissement différé ne doit être ajouté.");
    }

    private static async Task PreserveKnownDataAfterAutomaticFailureAsync()
    {
        ManualFriendsTimeProvider time = new();
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync(
            timeProvider: time);
        AvatarDescriptor firstAvatar = Avatar(4, 1);
        environment.Authentication.FriendsHandler = _ =>
            Task.FromResult<IReadOnlyList<WotLK.Launcher.LauncherFriend>>(
            [
                Friend(4, "known", "accepted", avatar: firstAvatar),
                Friend(1, "self", "accepted")
            ]);
        await RequiredCompletion(environment.Friends.TryRefresh());
        Equal(1, environment.Friends.CurrentSnapshot.Friends.Length,
            "Le compte courant ne doit jamais être publié dans sa propre liste.");
        Equal(firstAvatar, environment.Friends.CurrentSnapshot.Friends[0].Avatar,
            "Le descripteur avatar doit rejoindre le snapshot social.");

        environment.Authentication.FriendsHandler = _ =>
            Task.FromException<IReadOnlyList<WotLK.Launcher.LauncherFriend>>(
                new HttpRequestException("network unavailable"));
        time.Timer.Fire();
        True(await environment.Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1)),
            "L’échec automatique doit rester observé.");
        FriendsRuntimeSnapshot stale = environment.Friends.CurrentSnapshot;
        Equal(FriendsLoadState.Loaded, stale.LoadState,
            "Un échec automatique ne doit pas remplacer l’état chargé.");
        Equal("known", stale.Friends.Single().Username,
            "Les dernières données connues doivent être conservées.");
        True(stale.IsStale && stale.ErrorState == FriendsRuntimeError.None,
            "L’échec automatique doit être discret et marquer les données obsolètes.");
        FriendsViewState view = FriendsStateAdapter.Project(stale);
        True(view.IsStale && !view.ShowsError,
            "La présentation ne doit pas afficher une grande erreur rouge à chaque tick.");

        AvatarDescriptor changedAvatar = Avatar(4, 2);
        environment.Authentication.FriendsHandler = _ =>
            Task.FromResult<IReadOnlyList<WotLK.Launcher.LauncherFriend>>(
            [
                Friend(4, "known", "accepted", avatar: changedAvatar)
            ]);
        time.Timer.Fire();
        True(await environment.Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1)),
            "Le tick suivant doit pouvoir reprendre normalement.");
        Equal(changedAvatar, environment.Friends.CurrentSnapshot.Friends.Single().Avatar,
            "Une nouvelle version d’avatar doit remplacer le descripteur précédent.");
        True(!environment.Friends.CurrentSnapshot.IsStale,
            "Un rafraîchissement réussi doit retirer l’indicateur obsolète.");
    }

    private static async Task InvalidateSessionAfterAutomaticUnauthorizedAsync()
    {
        ManualFriendsTimeProvider time = new();
        await using FriendsEnvironment environment = await FriendsEnvironment.CreateAsync(
            timeProvider: time);
        environment.Authentication.FriendsHandler = _ =>
            Task.FromException<IReadOnlyList<WotLK.Launcher.LauncherFriend>>(
                new LauncherAuthException("raw unauthorized", HttpStatusCode.Unauthorized));

        time.Timer.Fire();
        True(await environment.Friends.WaitForIdleAsync(TimeSpan.FromSeconds(1)),
            "Le 401 automatique doit rester observé.");
        True(!environment.Friends.CurrentSnapshot.IsAuthenticated,
            "Un 401 doit être délégué au coordinateur de session.");
        True(!time.Timer.IsEnabled,
            "Le timer social doit être désactivé après invalidation de session.");
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
        uint? zoneId = 1,
        DateTimeOffset? lastSeenAt = null,
        AvatarDescriptor? avatar = null) =>
        new(accountId, username, accountId % 2 == 0 ? "ice" : null, relationship,
            online, characterName, level, classId, zoneId, lastSeenAt, avatar);

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

    private static async Task<FriendsActionCompletion> RequiredCompletion(
        FriendsActionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null,
            $"L’opération Amis devait démarrer, statut={start.Status}.");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task RequiredSessionCompletion(LauncherSessionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null,
            $"L’opération de session devait démarrer, statut={start.Status}.");
        LauncherSessionCompletion completion = await start.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(LauncherSessionCompletionStatus.Succeeded, completion.Status,
            "L’opération de session doit réussir dans le harnais Amis.");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Le scénario Amis n’a pas atteint l’état attendu.");
            }

            await Task.Delay(10);
        }
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

    private sealed class FakeDesktopNotificationSink : ILauncherDesktopNotificationSink
    {
        internal List<DesktopNotification> Messages { get; } = [];

        public void ShowNotification(string title, string message, bool playSound)
        {
            Messages.Add(new DesktopNotification(title, message, playSound));
        }
    }

    private sealed record DesktopNotification(
        string Title,
        string Message,
        bool PlaySound);

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

        internal LauncherSessionCoordinator Session => _session;

        internal static async Task<FriendsEnvironment> CreateAsync(
            Action<string>? log = null,
            TimeProvider? timeProvider = null,
            bool authenticated = true)
        {
            FakeLauncherAuthService authentication = new()
            {
                RestoreResult = authenticated,
                Session = authenticated ? FakeLauncherAuthService.CreateSession() : null,
                EnsureFreshHandler = _ => Task.FromResult(true)
            };
            CancellationTokenSource lifetime = new();
            LauncherSessionCoordinator session = new(authentication, lifetime.Token, log ?? (_ => { }));
            LauncherFriendsCoordinator friends = new(
                session,
                authentication,
                lifetime.Token,
                () => authentication.Session?.Profile,
                log ?? (_ => { }),
                timeProvider);
            FriendsEnvironment environment = new(authentication, lifetime, session, friends);
            LauncherSessionRestoreResult restore = await session.RestoreOnceAsync();
            Equal(
                authenticated
                    ? LauncherSessionRestoreStatus.Restored
                    : LauncherSessionRestoreStatus.NoSession,
                restore.Status,
                "Le harnais Amis doit refléter la session demandée.");
            Equal(authenticated, friends.CurrentSnapshot.IsAuthenticated,
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

    private sealed record LegacyFriendContract(uint AccountId, string Username);

    private sealed class ManualFriendsTimeProvider : TimeProvider
    {
        internal int CreateTimerCalls { get; private set; }

        internal ManualFriendsTimer Timer { get; private set; } = null!;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            CreateTimerCalls++;
            Timer = new ManualFriendsTimer(callback, state, dueTime, period);
            return Timer;
        }
    }

    private sealed class ManualFriendsTimer : ITimer
    {
        private readonly object _sync = new();
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _isDisposed;

        internal ManualFriendsTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Period = period;
        }

        internal TimeSpan DueTime { get; private set; }

        internal TimeSpan Period { get; private set; }

        internal bool IsEnabled => !_isDisposed && DueTime != Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                DueTime = dueTime;
                Period = period;
                return true;
            }
        }

        internal void Fire()
        {
            lock (_sync)
            {
                if (!IsEnabled)
                {
                    return;
                }
            }

            _callback(_state);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _isDisposed = true;
                DueTime = Timeout.InfiniteTimeSpan;
                Period = Timeout.InfiniteTimeSpan;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
