using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;

internal static class LauncherChatTests
{
    internal static async Task<int> RunAsync()
    {
        await VerifyHttpContractAsync();
        await RejectInvalidHttpResponsesAsync();
        await PollFromOneTimerAndLoadHistoryAsync();
        await MarkOnlyMessagesConfirmedVisibleAsync();
        await KeepAcceptedSendAfterRefreshFailureAsync();
        await RefreshFailureThatClearsAuthenticationAsync();
        await IgnoreLateUnauthorizedAfterAccountChangeAsync();
        await IgnoreSupersededConversationAsync();
        await RemoveFriendAndRejectQueuedSendAsync();
        await RejectMessagesForOtherAccountsAsync();
        await StopAndObserveLateRequestsAsync();
        Console.WriteLine("Atlas chat HTTP/session/polling/send/history integration OK.");
        return 0;
    }

    private static async Task VerifyHttpContractAsync()
    {
        Guid sentId = Guid.NewGuid();
        List<string> requests = [];
        using HttpClient http = new(new CallbackHandler(async (request, cancellation) =>
        {
            string relative = request.RequestUri!.PathAndQuery;
            requests.Add(request.Method + " " + relative);
            if (request.Method == HttpMethod.Post)
            {
                using JsonDocument json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
                if (relative.EndsWith("/read", StringComparison.Ordinal))
                {
                    Equal(12L, json.RootElement.GetProperty("throughMessageId").GetInt64(), "Le curseur de lecture doit rester exact.");
                    Equal(1, json.RootElement.EnumerateObject().Count(), "La lecture ne doit envoyer que son curseur.");
                    return JsonResponse(new LauncherChatReadResult(12));
                }
                Equal(sentId, json.RootElement.GetProperty("clientMessageId").GetGuid(), "L’UUID doit traverser le transport sans remplacement.");
                Equal("Bonjour\n世界", json.RootElement.GetProperty("body").GetString(), "Le texte multiligne Unicode doit rester intact.");
                Equal(2, json.RootElement.EnumerateObject().Count(), "Aucun identifiant de compte source ni secret ne doit entrer dans le corps du message.");
                return JsonResponse(new LauncherChatSendResult(Message(13, 1, 2, "Bonjour\n世界", sentId), false));
            }
            if (relative.StartsWith("/api/v1/chat/conversations?", StringComparison.Ordinal))
                return JsonResponse(new LauncherChatConversations([Conversation(2, Message(12, 2, 1))], 12, 1, false));
            if (relative.Contains("/messages?", StringComparison.Ordinal))
                return JsonResponse(new LauncherChatMessages(2, "Alice", [Message(11, 2, 1)], false, 0, 0));
            return JsonResponse(new LauncherChatUpdates([Message(12, 2, 1, origin: "game")], 12, false));
        }));
        LauncherChatApiClient api = new(http, new Uri("https://atlas.invalid/api/v1/"));
        await api.GetConversationsAsync(20, CancellationToken.None);
        await api.GetMessagesAsync(2, 12, CancellationToken.None);
        LauncherChatUpdates updates = await api.GetUpdatesAsync(11, CancellationToken.None);
        Equal("game", updates.Messages.Single().Origin, "L’origine en jeu doit être conservée.");
        await api.SendAsync(2, sentId, "Bonjour\n世界", CancellationToken.None);
        await api.MarkReadAsync(2, 12, CancellationToken.None);
        Equal("GET /api/v1/chat/conversations?limit=100&beforeId=20", requests[0], "La liste doit paginer par beforeId.");
        Equal("GET /api/v1/chat/conversations/2/messages?limit=50&beforeId=12", requests[1], "L’historique doit demander la page précédente.");
        Equal("GET /api/v1/chat/updates?afterId=11&limit=100", requests[2], "Le polling doit utiliser le curseur incrémental.");
        Equal("POST /api/v1/chat/conversations/2/messages", requests[3], "L’envoi doit viser l’ami dans l’URL.");
        Equal("POST /api/v1/chat/conversations/2/read", requests[4], "La lecture doit utiliser l’endpoint prévu.");
    }

    private static async Task RejectInvalidHttpResponsesAsync()
    {
        foreach (string body in new[] { "[]", "null", "<html>old API</html>" })
        {
            using HttpClient http = new(new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            { Content = new StringContent(body) })));
            LauncherChatApiClient api = new(http, new Uri("https://atlas.invalid/api/v1/"));
            LauncherChatApiException failure = await ThrowsAsync<LauncherChatApiException>(() => api.GetConversationsAsync(null, CancellationToken.None));
            Equal("chat-unavailable", failure.Code, "Une ancienne API 404 doit rester une indisponibilité contrôlée, même avec un corps inattendu.");
        }
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
        {
            using HttpClient http = new(new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") })));
            LauncherChatApiClient api = new(http, new Uri("https://atlas.invalid/api/v1/"));
            LauncherChatApiException failure = await ThrowsAsync<LauncherChatApiException>(() => api.GetConversationsAsync(null, CancellationToken.None));
            Equal(status, failure.StatusCode, "Le statut d’autorisation ou de limitation ne doit pas être masqué.");
        }
        foreach (bool knownLength in new[] { true, false })
        {
            using HttpClient http = new(new CallbackHandler((_, _) =>
            {
                HttpContent content = knownLength ? new ByteArrayContent([0]) : new UnknownLengthContent(new byte[LauncherChatApiClient.MaximumResponseBytes + 1]);
                if (knownLength) content.Headers.ContentLength = LauncherChatApiClient.MaximumResponseBytes + 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }));
            LauncherChatApiClient api = new(http, new Uri("https://atlas.invalid/api/v1/"));
            await ThrowsAsync<InvalidDataException>(() => api.GetConversationsAsync(null, CancellationToken.None));
        }
        using HttpClient invalidHttp = new(new CallbackHandler((_, _) => Task.FromResult(JsonResponse(
            new LauncherChatMessages(2, "Alice", [Message(14, 2, 1)], false, 0, 0)))));
        LauncherChatApiClient invalidApi = new(invalidHttp, new Uri("https://atlas.invalid/api/v1/"));
        await ThrowsAsync<InvalidDataException>(() => invalidApi.GetMessagesAsync(2, 12, CancellationToken.None));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => invalidApi.GetConversationsAsync(null, cancelled.Token));
    }

    private static async Task PollFromOneTimerAndLoadHistoryAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        environment.Api.SetMessages(2, Enumerable.Range(1, 51).Select(index => Message(index, 2, 1)));
        Equal(1, environment.ChatTime.CreateTimerCalls, "Le chat doit créer un seul timer.");
        True(!environment.ChatTime.Timer.Enabled, "Le runtime seul ne doit pas démarrer le polling avant son raccord au Shell.");
        environment.Chat.Start();
        environment.Chat.Start();
        Equal(LauncherChatCoordinator.PollInterval, environment.ChatTime.Timer.Period, "Le polling doit rester à cinq secondes.");
        environment.ChatTime.Timer.Fire();
        await environment.Chat.RefreshAsync();
        True(environment.Chat.CurrentSnapshot.IsAvailable, "Un chargement réel réussi doit activer la messagerie.");
        await environment.Chat.OpenConversationAsync(2, "Alice");
        Equal(50, environment.Chat.CurrentSnapshot.Messages.Length, "La première page doit contenir les cinquante messages récents.");
        Equal(2L, environment.Chat.CurrentSnapshot.Messages[0].Id, "L’historique initial doit être classé chronologiquement.");
        True(environment.Chat.CurrentSnapshot.HasEarlier, "L’historique plus ancien doit rester chargeable.");
        await environment.Chat.LoadEarlierAsync(2, 2);
        Equal(51, environment.Chat.CurrentSnapshot.Messages.Length, "Charger plus doit ajouter la page sans remplacer l’historique connu.");
        Equal(1L, environment.Chat.CurrentSnapshot.Messages[0].Id, "Le message ancien doit être ajouté avant la page courante.");
        True(!environment.Chat.CurrentSnapshot.HasEarlier, "La fin de l’historique doit désactiver Charger plus.");
        environment.Api.AddMessage(Message(60, 2, 1, "Depuis le jeu", origin: "game"));
        await environment.Chat.RefreshAsync();
        Equal(60L, environment.Chat.CurrentSnapshot.Messages[^1].Id, "Le polling doit ajouter le nouveau message du jeu.");
        Equal(0, environment.Api.ReadCalls, "Une page non visible ne doit envoyer aucun accusé de lecture.");

        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Api.UpdatesHandler = async (after, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new LauncherChatUpdates([], after, false);
        };
        Task first = environment.Chat.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task second = environment.Chat.RefreshAsync();
        environment.ChatTime.Timer.Fire();
        True(ReferenceEquals(first, second), "Un timer et une demande manuelle doivent partager le polling déjà en cours.");
        release.TrySetResult();
        await first;
    }

    private static async Task KeepAcceptedSendAfterRefreshFailureAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        await environment.Chat.OpenConversationAsync(2, "Alice");
        Guid id = Guid.NewGuid();
        environment.Api.ConversationsHandler = (_, _) => Task.FromException<LauncherChatConversations>(new HttpRequestException("read unavailable"));
        ChatSendCompletion sent = await environment.Chat.SendAsync(2, id, "Accepté", environment.Chat.CurrentSnapshot.SessionId);
        True(sent.Success, "Un envoi accepté doit rester réussi même si le rafraîchissement suivant échoue.");
        Equal(id, environment.Api.SentIds.Single(), "Le coordinateur doit transmettre l’UUID donné par le brouillon.");
        Equal("Accepté", environment.Chat.CurrentSnapshot.Messages[^1].Body, "Seule la réponse acceptée du serveur doit ajouter la bulle.");
        True(!environment.Chat.CurrentSnapshot.IsSending, "Le statut d’envoi doit se libérer après la réponse.");

        environment.Api.SendHandler = (_, _, _, _) => Task.FromException<LauncherChatSendResult>(
            new LauncherChatApiException(HttpStatusCode.Forbidden, "chat-not-friends"));
        ChatSendCompletion rejected = await environment.Chat.SendAsync(2, Guid.NewGuid(), "Refusé", environment.Chat.CurrentSnapshot.SessionId);
        True(!rejected.Success && environment.Chat.CurrentSnapshot.SelectedFriendAccountId is null && !environment.Chat.CurrentSnapshot.CanSend,
            "Un refus not-friends doit fermer la conversation et bloquer les envois supplémentaires.");
    }

    private static async Task MarkOnlyMessagesConfirmedVisibleAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        await environment.Chat.OpenConversationAsync(2, "Alice");
        Guid sessionId = environment.Chat.CurrentSnapshot.SessionId;
        environment.Chat.SetViewActive(true);
        await environment.Chat.RefreshAsync();
        Equal(0, environment.Api.ReadCalls, "Une page active sans confirmation de rendu ne doit pas marquer de message comme lu.");
        environment.Chat.SetThreadAtBottom(sessionId, 2, false, 0);
        environment.Api.AddMessage(Message(21, 2, 1));
        await environment.Chat.RefreshAsync();
        Equal(0, environment.Api.ReadCalls, "Recevoir un message pendant la remontée de l’historique doit préserver les non lus.");
        environment.Chat.SetThreadAtBottom(sessionId, 2, true, 10);
        await environment.Chat.RefreshAsync();
        Equal(10L, environment.Api.ReadThrough.Last(), "Le curseur doit être plafonné au dernier message confirmé par la vue, même si le polling connaît déjà un message plus récent.");
        environment.Chat.SetThreadAtBottom(sessionId, 2, true, 21);
        await environment.Chat.RefreshAsync();
        Equal(21L, environment.Api.ReadThrough.Last(), "Le retour au dernier message affiché doit permettre son accusé de lecture.");
        int before = environment.Api.ReadCalls;
        environment.Chat.SetThreadAtBottom(sessionId, 2, false, 0);
        environment.Api.AddMessage(Message(30, 2, 1));
        environment.Chat.SetThreadAtBottom(Guid.NewGuid(), 2, true, 30);
        environment.Chat.SetThreadAtBottom(sessionId, 3, true, 30);
        await environment.Chat.RefreshAsync();
        Equal(before, environment.Api.ReadCalls, "Un retour de rendu de la mauvaise session ou conversation doit être ignoré.");
    }

    private static async Task RefreshFailureThatClearsAuthenticationAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        long before = environment.Session.CurrentSnapshot.Sequence;
        environment.Authentication.EnsureFreshHandler = _ =>
        {
            environment.Authentication.Session = null;
            return Task.FromResult(false);
        };
        await environment.Chat.RefreshAsync();
        True(!environment.Session.CurrentSnapshot.IsAuthenticated && environment.Session.CurrentSnapshot.Sequence > before,
            "Un refresh 401 qui a déjà effacé AuthService.Session doit aussi invalider le coordinateur de session.");
        Equal(0u, environment.Chat.CurrentSnapshot.OwnerAccountId, "Les données Chat doivent être vidées après expiration.");
        Equal(0, environment.Api.ConversationCalls, "Aucune requête Chat ne doit partir après le refresh refusé.");
    }

    private static async Task IgnoreLateUnauthorizedAfterAccountChangeAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        await environment.Chat.RefreshAsync();
        Guid previousSession = environment.Chat.CurrentSnapshot.SessionId;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Api.UpdatesHandler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task; // Deliberately ignore cancellation to model a late network response.
            throw new LauncherChatApiException(HttpStatusCode.Unauthorized, "chat-unauthorized");
        };
        Task oldRequest = environment.Chat.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await CompleteSessionAsync(environment.Session.TryLogout(CancellationToken.None));
        LauncherAuthSession other = FakeLauncherAuthService.CreateSession("Other");
        other = other with { Profile = other.Profile with { AccountId = 9 } };
        environment.Authentication.LoginHandler = (_, _, _) => Task.FromResult(other);
        await CompleteSessionAsync(environment.Session.TryLogin("Other", "password-for-test"));
        release.TrySetResult();
        await oldRequest;
        True(environment.Session.CurrentSnapshot.IsAuthenticated && environment.Authentication.Session?.Profile.AccountId == 9,
            "Le 401 tardif de l’ancien compte ne doit jamais déconnecter le nouveau.");
        True(environment.Chat.CurrentSnapshot.SessionId != previousSession && environment.Chat.CurrentSnapshot.OwnerAccountId == 9
            && environment.Chat.CurrentSnapshot.Messages.IsEmpty && environment.Chat.CurrentSnapshot.Conversations.IsEmpty,
            "La nouvelle génération doit rester vide des données de l’ancien compte.");
    }

    private static async Task IgnoreSupersededConversationAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Api.MessagesHandler = async (friend, _, _) =>
        {
            if (friend == 2) { started.TrySetResult(); await release.Task; }
            return new LauncherChatMessages(friend, friend == 2 ? "Alice" : "Bob", [Message(friend * 10, friend, 1)], false, 0, 0);
        };
        bool mixed = false;
        environment.Chat.SnapshotChanged += (_, args) => mixed |= args.Snapshot.SelectedFriendAccountId == 3
            && args.Snapshot.Messages.Any(message => message.SenderAccountId == 2);
        Task first = environment.Chat.OpenConversationAsync(2, "Alice");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task second = environment.Chat.OpenConversationAsync(3, "Bob");
        Equal(3u, environment.Chat.CurrentSnapshot.SelectedFriendAccountId, "Le choix suivant doit apparaître immédiatement pendant le chargement.");
        release.TrySetResult();
        await Task.WhenAll(first, second);
        True(!mixed && environment.Chat.CurrentSnapshot.Messages.All(message => message.SenderAccountId == 3),
            "La réponse tardive d’Alice ne doit jamais apparaître dans la conversation de Bob.");
    }

    private static async Task RemoveFriendAndRejectQueuedSendAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        await environment.Chat.OpenConversationAsync(2, "Alice");
        environment.Authentication.FriendsHandler = _ => Task.FromResult<IReadOnlyList<LauncherFriend>>([Friend(3, "Bob")]);
        FriendsActionStartResult removed = environment.Friends.TryRemoveFriend(2);
        True(removed.Completion is not null, "La suppression factice doit démarrer.");
        await removed.Completion!;
        True(environment.Chat.CurrentSnapshot.SelectedFriendAccountId is null && !environment.Chat.CurrentSnapshot.CanSend,
            "Retirer l’ami doit vider la sélection Chat sans attendre le polling.");
        ChatSendCompletion send = await environment.Chat.SendAsync(2, Guid.NewGuid(), "Trop tard", environment.Chat.CurrentSnapshot.SessionId);
        True(!send.Success && environment.Api.SentIds.Count == 0, "Un envoi retardé vers cet ami doit être refusé avant le transport.");
    }

    private static async Task RejectMessagesForOtherAccountsAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        await environment.Chat.OpenConversationAsync(2, "Alice");
        int before = environment.Chat.CurrentSnapshot.Messages.Length;
        environment.Api.UpdatesHandler = (after, _) => Task.FromResult(new LauncherChatUpdates([Message(after + 1, 8, 9)], after + 1, false));
        await environment.Chat.RefreshAsync();
        True(!environment.Chat.CurrentSnapshot.IsAvailable && environment.Chat.CurrentSnapshot.Messages.Length == before,
            "Un payload appartenant à d’autres comptes doit être refusé sans publication de texte.");
    }

    private static async Task StopAndObserveLateRequestsAsync()
    {
        await using ChatEnvironment environment = await ChatEnvironment.CreateAsync();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Api.ConversationsHandler = async (_, _) =>
        {
            started.TrySetResult();
            await release.Task;
            return new LauncherChatConversations([Conversation(2, Message(10, 2, 1))], 10, 1, false);
        };
        Task request = environment.Chat.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        environment.Chat.Start();
        environment.Chat.BeginShutdown();
        long sequence = environment.Chat.CurrentSnapshot.Sequence;
        release.TrySetResult();
        await request;
        True(await environment.Chat.WaitForIdleAsync(TimeSpan.FromSeconds(1)), "L’arrêt doit attendre aussi les réponses qui ignorent l’annulation.");
        Equal(sequence, environment.Chat.CurrentSnapshot.Sequence, "Aucun état tardif ne doit être publié après l’arrêt.");
        True(!environment.ChatTime.Timer.Enabled, "L’arrêt doit désarmer le polling.");
    }

    private static LauncherChatMessage Message(long id, uint sender, uint recipient, string body = "Message de test", Guid? clientId = null, string origin = "launcher") =>
        new(id, clientId ?? Guid.NewGuid(), sender, recipient, sender == 1 ? "Owner" : sender == 2 ? "Alice" : "Bob",
            body, origin, new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero).AddSeconds(id));
    private static LauncherChatConversation Conversation(uint friend, LauncherChatMessage last, int unread = 1) =>
        new(friend, friend == 2 ? "Alice" : "Bob", last, unread, 0, 0);
    private static LauncherFriend Friend(uint id, string name) => new(id, name, null, "accepted", false, null, null, null, null, null);
    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static async Task CompleteSessionAsync(LauncherSessionStartResult start)
    {
        True(start.Completion is not null, "L’opération de session factice doit démarrer.");
        LauncherSessionCompletion result = await start.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
        Equal(LauncherSessionCompletionStatus.Succeeded, result.Status, "La transition de session doit réussir.");
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Exception attendue : {typeof(T).Name}.");
    }
    private static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}."); }

    private sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class ChatEnvironment : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private ChatEnvironment()
        {
            Authentication = new FakeLauncherAuthService
            {
                RestoreResult = true, Session = FakeLauncherAuthService.CreateSession(), EnsureFreshHandler = _ => Task.FromResult(true),
                FriendsHandler = _ => Task.FromResult<IReadOnlyList<LauncherFriend>>([Friend(2, "Alice"), Friend(3, "Bob")])
            };
            Session = new LauncherSessionCoordinator(Authentication, _lifetime.Token, _ => { });
            Friends = new LauncherFriendsCoordinator(Session, Authentication, _lifetime.Token, () => Authentication.Session?.Profile, _ => { }, new ManualTimeProvider());
            Chat = new LauncherChatCoordinator(Session, Authentication, Friends, Api, _lifetime.Token, _ => { }, ChatTime);
        }
        internal FakeLauncherAuthService Authentication { get; }
        internal LauncherSessionCoordinator Session { get; }
        internal LauncherFriendsCoordinator Friends { get; }
        internal FakeChatApi Api { get; } = new();
        internal ManualTimeProvider ChatTime { get; } = new();
        internal LauncherChatCoordinator Chat { get; }
        internal static async Task<ChatEnvironment> CreateAsync()
        {
            ChatEnvironment result = new();
            await result.Session.RestoreOnceAsync();
            FriendsActionStartResult friends = result.Friends.TryRefresh();
            True(friends.Completion is not null, "La liste factice doit démarrer.");
            await friends.Completion!;
            return result;
        }
        public async ValueTask DisposeAsync()
        {
            Chat.BeginShutdown(); Friends.BeginShutdown(); Session.BeginShutdown(); _lifetime.Cancel();
            await Chat.WaitForIdleAsync(TimeSpan.FromSeconds(2));
            await Friends.WaitForIdleAsync(TimeSpan.FromSeconds(2));
            Chat.Dispose(); Friends.Dispose(); Session.Dispose(); Authentication.Dispose(); _lifetime.Dispose();
        }
    }

    private sealed class FakeChatApi : ILauncherChatApiClient
    {
        private readonly List<LauncherChatMessage> _messages = [Message(10, 2, 1), Message(20, 3, 1)];
        internal Func<long?, CancellationToken, Task<LauncherChatConversations>>? ConversationsHandler { get; set; }
        internal Func<uint, long?, CancellationToken, Task<LauncherChatMessages>>? MessagesHandler { get; set; }
        internal Func<long, CancellationToken, Task<LauncherChatUpdates>>? UpdatesHandler { get; set; }
        internal Func<uint, Guid, string, CancellationToken, Task<LauncherChatSendResult>>? SendHandler { get; set; }
        internal int ConversationCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal List<long> ReadThrough { get; } = [];
        internal List<Guid> SentIds { get; } = [];
        internal void SetMessages(uint friend, IEnumerable<LauncherChatMessage> messages)
        {
            _messages.RemoveAll(message => message.SenderAccountId == friend || message.RecipientAccountId == friend);
            _messages.AddRange(messages);
        }
        internal void AddMessage(LauncherChatMessage message) => _messages.Add(message);
        public Task<LauncherChatConversations> GetConversationsAsync(long? beforeId, CancellationToken cancellationToken)
        {
            ConversationCalls++;
            if (ConversationsHandler is not null) return ConversationsHandler(beforeId, cancellationToken);
            LauncherChatConversation[] conversations = _messages.GroupBy(message => message.SenderAccountId == 1 ? message.RecipientAccountId : message.SenderAccountId)
                .Select(group => Conversation(group.Key, group.MaxBy(message => message.Id)!)).OrderByDescending(item => item.LastMessage.Id).ToArray();
            return Task.FromResult(new LauncherChatConversations(conversations, _messages.Select(message => message.Id).DefaultIfEmpty(0).Max(), conversations.Length, false));
        }
        public Task<LauncherChatMessages> GetMessagesAsync(uint accountId, long? beforeId, CancellationToken cancellationToken)
        {
            if (MessagesHandler is not null) return MessagesHandler(accountId, beforeId, cancellationToken);
            LauncherChatMessage[] all = _messages.Where(message => (message.SenderAccountId == accountId || message.RecipientAccountId == accountId)
                && (beforeId is null || message.Id < beforeId)).OrderByDescending(message => message.Id).ToArray();
            return Task.FromResult(new LauncherChatMessages(accountId, accountId == 2 ? "Alice" : "Bob", all.Take(50).Reverse().ToArray(), all.Length > 50, 0, 0));
        }
        public Task<LauncherChatUpdates> GetUpdatesAsync(long afterId, CancellationToken cancellationToken)
        {
            if (UpdatesHandler is not null) return UpdatesHandler(afterId, cancellationToken);
            LauncherChatMessage[] updates = _messages.Where(message => message.Id > afterId).OrderBy(message => message.Id).ToArray();
            return Task.FromResult(new LauncherChatUpdates(updates, Math.Max(afterId, _messages.Select(message => message.Id).DefaultIfEmpty(0).Max()), false));
        }
        public Task<LauncherChatSendResult> SendAsync(uint accountId, Guid clientMessageId, string body, CancellationToken cancellationToken)
        {
            SentIds.Add(clientMessageId);
            if (SendHandler is not null) return SendHandler(accountId, clientMessageId, body, cancellationToken);
            LauncherChatMessage message = Message(_messages.Max(item => item.Id) + 1, 1, accountId, body, clientMessageId);
            _messages.Add(message);
            return Task.FromResult(new LauncherChatSendResult(message, false));
        }
        public Task<LauncherChatReadResult> MarkReadAsync(uint accountId, long throughMessageId, CancellationToken cancellationToken)
        {
            ReadCalls++;
            ReadThrough.Add(throughMessageId);
            return Task.FromResult(new LauncherChatReadResult(throughMessageId));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        internal int CreateTimerCalls { get; private set; }
        internal ManualTimer Timer { get; private set; } = null!;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            CreateTimerCalls++;
            return Timer = new ManualTimer(callback, state, dueTime, period);
        }
    }
    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private bool _disposed;
        internal TimeSpan DueTime { get; private set; } = dueTime;
        internal TimeSpan Period { get; private set; } = period;
        internal bool Enabled => !_disposed && DueTime != Timeout.InfiniteTimeSpan;
        public bool Change(TimeSpan due, TimeSpan repeat) { DueTime = due; Period = repeat; return !_disposed; }
        internal void Fire() { if (Enabled) callback(state); }
        public void Dispose() { _disposed = true; DueTime = Period = Timeout.InfiniteTimeSpan; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
