using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using WotLK.Launcher;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;

internal static class ChatWorkspaceTests
{
    private const string ThreadId = "direct-1-2";
    private static readonly CancellationToken None = CancellationToken.None;
    private static int _checks;

    internal static async Task<int> RunAsync()
    {
        _checks = 0;
        await ProtectedStoreAndStaging();
        await BoundedChunks();
        await DraftsAndCancellation();
        await CharacterCardDuringDraftSave();
        await RetryAfterLostResponse();
        await PrivateReadAndLogout();
        await RevocationWhileReadIsInFlight();
        await PreferencesDuringStaleRefresh();
        await UploadResume();
        Console.WriteLine($"Chat workspace PASS: {_checks} assertions. DPAPI account/environment isolation, durable drafts/outbox, cancel before POST, lost-response UUID retry, private read opt-out, session isolation, staged-file lifecycle and streaming 500000000-byte upload/resume. Synthetic HTTP/files only; no real account or launcher UI.");
        return 0;
    }

    private static async Task ProtectedStoreAndStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), "atlas-chat-workspace-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ChatWorkspaceStore store = new(root, new Uri("https://fixture.invalid/api/v1/"));
            ChatWorkspaceStore other = new(root, new Uri("https://second.invalid/api/v1/"));
            ChatWorkspaceLocalState state = new() { Drafts = [new() { ThreadId = ThreadId, Body = "brouillon 日本語 secret", ReplyToMessageId = 9007199254740993 }] };
            await store.SaveAsync(1, state, None);
            Check((await store.LoadAsync<ChatWorkspaceLocalState>(1, None))!.Drafts[0].ReplyToMessageId == 9007199254740993, "Exact Int64 persisted.");
            Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(store.GetPath(1))).Contains("brouillon", StringComparison.Ordinal), "DPAPI hides draft plaintext.");
            Check(await store.LoadAsync<ChatWorkspaceLocalState>(2, None) is null && await other.LoadAsync<ChatWorkspaceLocalState>(1, None) is null, "Account and API environment isolated.");
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            await Fails<OperationCanceledException>(() => store.SaveAsync(1, new ChatWorkspaceLocalState(), cancelled.Token));
            Check((await store.LoadAsync<ChatWorkspaceLocalState>(1, None))!.Drafts[0].Body == state.Drafts[0].Body, "Cancelled save preserves committed draft.");
            Directory.CreateDirectory(Path.GetDirectoryName(store.GetPath(2))!);
            File.Copy(store.GetPath(1), store.GetPath(2));
            await Fails<System.Security.Cryptography.CryptographicException>(() => store.LoadAsync<ChatWorkspaceLocalState>(2, None));

            ChatAttachmentFileSource files = new(root, new Uri("https://fixture.invalid/api/v1/"));
            string original = Path.Combine(root, "personal.gif");
            await File.WriteAllBytesAsync(original, "GIF89a-personal"u8.ToArray());
            ChatSelectedFile staged = await files.InspectAsync(1, original, None);
            Check(staged.SourcePath != original && File.Exists(original), "Staging copies without moving original.");
            File.Delete(original);
            ChatLocalUpload upload = new() { SourcePath = staged.SourcePath, Size = staged.Size, LastWriteAt = staged.LastWriteAt, Sha256 = staged.Sha256 };
            await using (Stream stream = await files.OpenVerifiedAsync(upload, None))
            { using StreamReader reader = new(stream); Check(await reader.ReadToEndAsync() == "GIF89a-personal", "Staged data survives disappearance of original/clipboard temporary."); }
            await Fails<InvalidDataException>(() => files.DeleteStagedAsync(2, staged.SourcePath, None));
            Check(File.Exists(staged.SourcePath), "Another account cannot delete staged source.");
            await files.DeleteStagedAsync(1, staged.SourcePath, None);
            Check(!File.Exists(staged.SourcePath), "Only owned staged file removed.");
            foreach (string name in new[] { "a.zip", "a.exe", "a.svg", "a.doc", "a.xls", "a.ppt", "a.html", "a.markdown" })
                await Fails<ChatWorkspaceException>(() => Task.FromResult(ChatAttachmentFileSource.ContentTypeForName(name)));
            Check(ChatAttachmentFileSource.ContentTypeForName("my.GIF") == "image/gif" && ChatAttachmentFileSource.ContentTypeForName("notes.docx").Contains("wordprocessingml", StringComparison.Ordinal), "Personal animated images and supported Office extension accepted.");
        }
        finally
        {
            string full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("atlas-chat-workspace-fixture-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup.");
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        }
    }

    private static async Task BoundedChunks()
    {
        using PatternStream source = new(ChatLimits.MaximumAttachmentBytes);
        long copied = 0;
        while (copied < source.Length)
        {
            int count = (int)Math.Min(ChatLimits.UploadChunkBytes, source.Length - copied);
            using ChatUploadChunkContent content = new(source, count);
            Check(content.Headers.ContentLength == count, "Chunk declares exact bounded length.");
            await content.CopyToAsync(Stream.Null);
            copied += count;
        }
        Check(copied == 500_000_000 && source.Position == copied && source.MaximumRead <= 65536, "500Mo streamed with at most64KiB read buffer.");
        Check(!source.Disposed, "Chunk content retains caller ownership of source.");
        Check(JsonSerializer.Serialize(new ChatReadRequest(9007199254740993), ChatJson.Options).Contains("\"9007199254740993\"", StringComparison.Ordinal), "Wire IDs above JS exact integer limit are strings.");
        using FixtureApi handler = new(); using HttpClient http = new(handler);
        LauncherChatV2ApiClient client = new(http, new Uri("https://fixture.invalid/api/v1/"));
        await Fails<ArgumentException>(() => client.StartUploadAsync(new("large.txt", "text/plain", ChatLimits.MaximumAttachmentBytes + 1), None));
        Check(handler.Requests.Count == 0, "Over-limit transfer rejected before HTTP.");
    }

    private static async Task DraftsAndCancellation()
    {
        MemoryStore store = new();
        await using (Environment env = await Environment.Create(store))
        {
            await env.Workspace.OpenThreadAsync(ThreadId);
            env.Api.PreferenceGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await env.Workspace.SetPreferencesAsync(new() { DoNotDisturb = true });
            await Until(() => env.Api.PreferenceEntered, "Preference must occupy command gate.");
            Guid id = Guid.NewGuid();
            await env.Workspace.QueueSendAsync(ThreadId, new() { ClientMessageId = id, Body = "annuler avant POST" });
            await env.Workspace.CancelSendAsync(id);
            await env.Workspace.SaveDraftAsync(new() { ThreadId = ThreadId, Body = "persistant 日本語", Card = new() { Kind = "quest", Title = "Fixture" } });
            env.Api.PreferenceGate.TrySetResult();
            await Until(() => env.Workspace.CurrentSnapshot.State.Preferences.DoNotDisturb, "DND should be saved.");
            Check(env.Workspace.CurrentSnapshot.Outbox.Single().Status == "cancelled", "Queued operation cancelled while awaiting command gate.");
            Check(env.Api.Sends.Count == 0, "Cancelled operation never reaches POST.");
        }
        await using (Environment other = await Environment.Create(store, owner: 7))
            Check(other.Workspace.CurrentSnapshot.Drafts.Count == 0 && other.Workspace.CurrentSnapshot.Outbox.Count == 0, "No draft/outbox crosses account boundary.");
        await using (Environment restored = await Environment.Create(store))
        {
            await restored.Workspace.OpenThreadAsync(ThreadId);
            Check(restored.Workspace.CurrentSnapshot.Draft?.Body == "persistant 日本語" && restored.Workspace.CurrentSnapshot.Draft.Card?.Kind == "quest", "Draft/card restored after restart.");
            Check(restored.Api.Sends.Count == 0, "Cancelled UUID stays cancelled after restart.");
        }
        MemoryStore offline = new();
        await using (Environment env = await Environment.Create(offline))
        {
            await env.Workspace.OpenThreadAsync(ThreadId); env.Api.FailState = true; await env.Workspace.RefreshAsync();
            Guid id = Guid.NewGuid(); await env.Workspace.QueueSendAsync(ThreadId, new() { ClientMessageId = id, Body = "hors ligne" });
            Check(!env.Workspace.CurrentSnapshot.IsAvailable && env.Api.Sends.Count == 0 && env.Workspace.CurrentSnapshot.Outbox.Single().Status == "queued", "Offline send is durable and waits for connectivity.");
            await env.Workspace.CancelSendAsync(id);
        }
    }

    private static async Task CharacterCardDuringDraftSave()
    {
        MemoryStore store = new();
        await using Environment env = await Environment.Create(store);
        await env.Workspace.OpenThreadAsync(ThreadId);
        await env.Workspace.SaveDraftAsync(new() { ThreadId = ThreadId, Body = "ancien texte" });
        MemoryStore.DraftSaveGate gate = store.BlockDraftSave("texte juste saisi");
        Task textSave = env.Workspace.SaveDraftAsync(new()
            { ThreadId = ThreadId, Body = "texte juste saisi", ReplyToMessageId = 9007199254740993 });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Check(env.Workspace.CurrentSnapshot.Draft?.Body == "ancien texte", "The published snapshot can lag behind a pending draft write.");
        ChatCardDto card = new() { Kind = "character", Title = "Fixture", ReferenceId = "4294967295",
            Fields = new Dictionary<string, string> { ["ownerAccountId"] = "1", ["characterGuid"] = "4294967295" } };
        Task cardSave = env.Workspace.SetDraftCardAsync(ThreadId, card);
        Check(!cardSave.IsCompleted, "Character selection waits for the draft persistence gate.");
        gate.Release.TrySetResult();
        await Task.WhenAll(textSave, cardSave);
        ChatWorkspaceDraft current = env.Workspace.CurrentSnapshot.Draft!;
        Check(current.Body == "texte juste saisi" && current.ReplyToMessageId == 9007199254740993
            && current.Card?.ReferenceId == "4294967295", "Character selection preserves newly saved text and reply while applying only its card.");
        ChatWorkspaceDraft persisted = (await store.LoadAsync<ChatWorkspaceLocalState>(1, None))!.Drafts.Single();
        Check(persisted.Body == current.Body && persisted.ReplyToMessageId == current.ReplyToMessageId
            && persisted.Card?.ReferenceId == current.Card?.ReferenceId, "The merged draft is also durable.");
        await env.Workspace.SetDraftCardAsync(ThreadId, null);
        Check(env.Workspace.CurrentSnapshot.Draft?.Body == current.Body && env.Workspace.CurrentSnapshot.Draft?.Card is null,
            "Removing a card also preserves draft text.");
    }

    private static async Task RetryAfterLostResponse()
    {
        MemoryStore store = new(); FixtureServer server = new(); Guid id = Guid.NewGuid();
        await using (Environment env = await Environment.Create(store, server: server))
        {
            await env.Workspace.OpenThreadAsync(ThreadId); env.Api.LoseSendResponse = true;
            await env.Workspace.QueueSendAsync(ThreadId, new() { ClientMessageId = id, Body = "une seule fois" });
            await Until(() => !env.Workspace.CurrentSnapshot.IsAvailable && env.Workspace.CurrentSnapshot.Outbox.Single().Status == "queued", "Lost response should return to durable retry queue.");
            Check(env.Workspace.CurrentSnapshot.Outbox.Single().WasSubmitted, "Uncertain POST retains submitted boundary.");
            await Fails<ChatWorkspaceException>(() => env.Workspace.CancelSendAsync(id));
        }
        await using (Environment restored = await Environment.Create(store, server: server))
        {
            await Until(() => restored.Workspace.CurrentSnapshot.Outbox.Single().Status == "sent", "Retry must reconcile completed message.");
            Check(server.Messages.Count == 1 && restored.Api.Sends.Single() == id, "Same UUID survives restart; response loss never duplicates message.");
        }
    }

    private static async Task PrivateReadAndLogout()
    {
        FixtureServer server = new() { Preferences = new() { ShareReadReceipts = false, ShareTyping = false, MessageSoundEnabled = true } };
        await using Environment env = await Environment.Create(new(), server: server);
        await env.Workspace.OpenThreadAsync(ThreadId);
        long through = env.Workspace.CurrentSnapshot.Messages.Last().Id;
        await env.Workspace.MarkReadAsync(ThreadId, through);
        Check(env.Api.Reads.Count == 0, "Hidden thread does not clear unread.");
        env.Workspace.SetViewActive(true);
        await env.Workspace.MarkReadAsync(ThreadId, through);
        Check(env.Api.Reads.Single() == through && env.Workspace.CurrentSnapshot.State.Threads.First().LastReadMessageId == through, "Private cursor advances even with shared read receipts disabled.");
        await env.Workspace.SetTypingAsync(ThreadId, true);
        Check(env.Api.TypingCalls == 0 && !env.Workspace.CurrentSnapshot.State.Preferences.MessageSoundEnabled, "Typing opt-out respected and sound forced off.");
        ChatWorkspaceSnapshot prior = env.Workspace.CurrentSnapshot;
        var logout = env.Session.TryLogout(None);
        if (logout.Completion is not null) await logout.Completion;
        Check(!env.Workspace.AcceptsAction(prior.SessionId, prior.OwnerAccountId, prior.Sequence)
            && env.Workspace.CurrentSnapshot.OwnerAccountId == 0 && env.Workspace.CurrentSnapshot.Messages.Count == 0, "Logout clears visible state and rejects stale native actions.");
    }

    private static async Task UploadResume()
    {
        MemoryStore store = new(); FixtureServer server = new(); FakeFiles files = new(ChatLimits.MaximumAttachmentBytes);
        string localId;
        await using (Environment env = await Environment.Create(store, server: server, files: files))
        {
            await env.Workspace.OpenThreadAsync(ThreadId); env.Api.LoseChunkResponse = true;
            await env.Workspace.AddFilesAsync(ThreadId, ["C:\\synthetic\\large.txt"]);
            await Until(() => !env.Workspace.CurrentSnapshot.IsAvailable && env.Workspace.CurrentSnapshot.Uploads.Single().Status == "queued", "Interrupted upload should wait for reconnect.");
            localId = env.Workspace.CurrentSnapshot.Uploads.Single().LocalId;
            Check(server.Upload?.Offset == ChatLimits.UploadChunkBytes && env.Workspace.CurrentSnapshot.Uploads.Single().Offset == 0, "Server can commit a chunk before response is lost.");
        }
        await using (Environment restored = await Environment.Create(store, server: server, files: files))
        {
            await restored.Workspace.OpenThreadAsync(ThreadId);
            await Until(() => restored.Workspace.CurrentSnapshot.Uploads.Single().Status == "complete", "Large upload should resume to completion.", 15000);
            Check(restored.Api.ChunkOffsets.First() == ChatLimits.UploadChunkBytes, "Resume begins at server-confirmed offset.");
            Check(server.Upload?.Offset == 500_000_000 && files.MaximumRead <= 65536, "Full500Mo transfer uses bounded streaming reads.");
            Check(!JsonSerializer.Serialize(restored.Workspace.CurrentSnapshot, ChatJson.Options).Contains("SourcePath", StringComparison.OrdinalIgnoreCase), "Local paths do not enter presentation snapshot.");
            await restored.Workspace.RemoveAttachmentAsync(ThreadId, localId);
            Check(files.Deleted.Count == 1 && restored.Workspace.CurrentSnapshot.Draft?.AttachmentIds.Count == 0, "Removing final reference releases owned staged file.");
        }
        FakeFiles broken = new(32) { FailInspectionAt = 2 };
        await using (Environment env = await Environment.Create(new(), files: broken))
        {
            await env.Workspace.OpenThreadAsync(ThreadId);
            await Fails<ChatWorkspaceException>(() => env.Workspace.AddFilesAsync(ThreadId, ["C:\\synthetic\\a.txt", "C:\\synthetic\\b.txt"]));
            Check(broken.Deleted.Count == 1 && env.Workspace.CurrentSnapshot.Uploads.Count == 0, "Failed multiple selection cleans uncommitted staging copies.");
        }
    }

    private static async Task RevocationWhileReadIsInFlight()
    {
        await using Environment env = await Environment.Create(new());
        await env.Workspace.OpenThreadAsync(ThreadId);
        env.Workspace.SetViewActive(true);
        env.Api.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task read = env.Workspace.MarkReadAsync(ThreadId, env.Workspace.CurrentSnapshot.Messages.Last().Id);
        await Until(() => env.Api.ReadEntered, "Read request should be in flight.");
        env.Api.Server.Revoked = true;
        await env.Api.Events.Writer.WriteAsync(new() { EventCursor = 1, Events = [new() { Id = 1, Kind = "access", ThreadId = ThreadId }] });
        await Until(() => env.Workspace.CurrentSnapshot.State.Threads.Count == 0, "Access event should remove the thread.");
        env.Api.ReadGate.TrySetResult();
        await read;
        Check(env.Workspace.CurrentSnapshot.State.Threads.Count == 0 && env.Workspace.CurrentSnapshot.SelectedThreadId is null
            && env.Workspace.CurrentSnapshot.Messages.Count == 0, "Late read response cannot resurrect revoked thread/history.");
    }

    private static async Task PreferencesDuringStaleRefresh()
    {
        MemoryStore store = new();
        await using Environment env = await Environment.Create(store);
        env.Api.StateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task refresh = env.Workspace.RefreshAsync();
        await Until(() => env.Api.StateEntered, "State request should be suspended before changing DND.");
        await env.Workspace.SetPreferencesAsync(new() { DoNotDisturb = true });
        await Until(() => store.LoadAsync<ChatWorkspaceLocalState>(1, None).GetAwaiter().GetResult() is { PreferencesPending: false, Preferences.DoNotDisturb: true }, "DND update should be durably acknowledged.");
        env.Api.StateGate.TrySetResult();
        await refresh;
        Check(env.Workspace.CurrentSnapshot.State.Preferences.DoNotDisturb, "Old directory response cannot overwrite a newer acknowledged DND change.");
    }

    private static void Check(bool condition, string description) { _checks++; if (!condition) throw new InvalidOperationException(description); }
    private static async Task Fails<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { _checks++; return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Until(Func<bool> condition, string description, int milliseconds = 5000)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
        while (!condition()) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException(description); await Task.Delay(10); }
    }

    private sealed class MemoryStore : IChatWorkspaceStore
    {
        private readonly ConcurrentDictionary<uint, byte[]> _states = new();
        private DraftSaveGate? _draftSaveGate;
        internal sealed class DraftSaveGate(string body)
        {
            internal string Body { get; } = body;
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        internal DraftSaveGate BlockDraftSave(string body) => _draftSaveGate = new(body);
        public Task<T?> LoadAsync<T>(uint owner, CancellationToken token) where T : class
        { token.ThrowIfCancellationRequested(); return Task.FromResult(_states.TryGetValue(owner, out byte[]? bytes) ? JsonSerializer.Deserialize<T>(bytes, ChatJson.Options) : null); }
        public async Task SaveAsync<T>(uint owner, T state, CancellationToken token) where T : class
        {
            token.ThrowIfCancellationRequested();
            DraftSaveGate? gate = _draftSaveGate;
            if (gate is not null && state is ChatWorkspaceLocalState local && local.Drafts.Any(draft => draft.Body == gate.Body)
                && Interlocked.CompareExchange(ref _draftSaveGate, null, gate) == gate)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(token);
            }
            _states[owner] = JsonSerializer.SerializeToUtf8Bytes(state, ChatJson.Options);
        }
    }

    private sealed class Environment : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly HttpClient _http;
        private Environment(MemoryStore store, uint owner, FixtureServer? server, FakeFiles? files)
        {
            LauncherAuthSession session = FakeLauncherAuthService.CreateSession();
            Auth = new() { Session = session with { Profile = session.Profile with { AccountId = owner } }, RestoreResult = true, EnsureFreshHandler = _ => Task.FromResult(true) };
            Session = new(Auth, _lifetime.Token, _ => { });
            Api = new() { Owner = owner, Server = server ?? new() }; _http = new(Api);
            Workspace = new(Session, Auth, new LauncherChatV2ApiClient(_http, new Uri("https://fixture.invalid/api/v1/")), store,
                _lifetime.Token, _ => { }, files: files ?? new FakeFiles(16));
        }
        internal FakeLauncherAuthService Auth { get; }
        internal LauncherSessionCoordinator Session { get; }
        internal LauncherChatWorkspace Workspace { get; }
        internal FixtureApi Api { get; }
        internal static async Task<Environment> Create(MemoryStore store, uint owner = 1, FixtureServer? server = null, FakeFiles? files = null)
        {
            Environment env = new(store, owner, server, files); await env.Session.RestoreOnceAsync(); env.Workspace.Start();
            await Until(() => env.Workspace.CurrentSnapshot.IsAvailable, "Workspace initial state must load."); return env;
        }
        public async ValueTask DisposeAsync()
        {
            Workspace.BeginShutdown(); Session.BeginShutdown(); _lifetime.Cancel();
            await Workspace.WaitForIdleAsync(TimeSpan.FromSeconds(3));
            Workspace.Dispose(); Session.Dispose(); Auth.Dispose(); _http.Dispose(); _lifetime.Dispose();
        }
    }

    private sealed class FixtureServer
    {
        internal ConcurrentDictionary<Guid, ChatMessageDto> Messages { get; } = new();
        internal ChatUploadDto? Upload;
        internal ChatPreferencesDto Preferences = new();
        internal long LastRead;
        internal bool Revoked;
    }

    private sealed class FixtureApi : HttpMessageHandler
    {
        internal uint Owner = 1;
        internal FixtureServer Server = new();
        internal bool FailState;
        internal bool LoseSendResponse;
        internal bool LoseChunkResponse;
        internal TaskCompletionSource? PreferenceGate;
        internal bool PreferenceEntered;
        internal int TypingCalls;
        internal TaskCompletionSource? ReadGate;
        internal bool ReadEntered;
        internal TaskCompletionSource? StateGate;
        internal bool StateEntered;
        internal Channel<ChatEventsDto> Events { get; } = Channel.CreateUnbounded<ChatEventsDto>();
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal ConcurrentQueue<Guid> Sends { get; } = new();
        internal ConcurrentQueue<long> Reads { get; } = new();
        internal ConcurrentQueue<long> ChunkOffsets { get; } = new();
        private ChatMessageDto Incoming => new() { Id = 9007199254740993, ThreadId = ThreadId, ClientMessageId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), Sender = new() { AccountId = 2, Username = "Alice" }, Body = "bonjour", CreatedAt = DateTimeOffset.UtcNow, Version = 1 };
        private ChatThreadDto Thread => new() { Id = ThreadId, CanSend = true, Version = 1, Members = [new() { Profile = new() { AccountId = Owner, Username = "Self" } }, new() { Profile = new() { AccountId = 2, Username = "Alice" } }], LastMessage = Incoming, LastReadMessageId = Server.LastRead, UnreadCount = Server.LastRead == 0 ? 1 : 0 };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (!path.StartsWith("/api/v2/chat/", StringComparison.Ordinal)) throw new InvalidOperationException("Wrong API generation: " + path);
            string route = path[13..]; Requests.Enqueue(request.Method + " " + route);
            if (route == "state")
            {
                HttpResponseMessage state = FailState ? Json(new ChatErrorDto("chat-unavailable"), HttpStatusCode.ServiceUnavailable)
                    : Json(new ChatStateDto { Self = new() { AccountId = Owner, Username = "Self" }, Threads = Server.Revoked ? [] : [Thread], Contacts = [new() { AccountId = 2, Username = "Alice" }], Preferences = Server.Preferences });
                if (StateGate is not null) { StateEntered = true; await StateGate.Task.WaitAsync(token); }
                return state;
            }
            if (route == "events") return Json(await Events.Reader.ReadAsync(token));
            if (route == "preferences")
            {
                ChatPreferencesRequest prefs = await Read<ChatPreferencesRequest>(request, token); PreferenceEntered = true;
                if (PreferenceGate is not null) await PreferenceGate.Task.WaitAsync(token);
                Server.Preferences = Server.Preferences with { DoNotDisturb = prefs.DoNotDisturb ?? Server.Preferences.DoNotDisturb,
                    ShareReadReceipts = prefs.ShareReadReceipts ?? Server.Preferences.ShareReadReceipts, ShareTyping = prefs.ShareTyping ?? Server.Preferences.ShareTyping };
                return Json(Server.Preferences);
            }
            if (route == "threads/" + ThreadId + "/messages" && request.Method == HttpMethod.Get)
                return Json(new ChatMessagesPageDto { Thread = Thread, Messages = new[] { Incoming }.Concat(Server.Messages.Values).OrderBy(message => message.Id).ToArray() });
            if (route == "threads/" + ThreadId + "/messages" && request.Method == HttpMethod.Post)
            {
                ChatSendMessageRequest send = await Read<ChatSendMessageRequest>(request, token); Sends.Enqueue(send.ClientMessageId);
                bool existed = Server.Messages.TryGetValue(send.ClientMessageId, out ChatMessageDto? message);
                message ??= new() { Id = 9007199254740994 + Server.Messages.Count, ThreadId = ThreadId, ClientMessageId = send.ClientMessageId,
                    Sender = new() { AccountId = Owner, Username = "Self" }, Body = send.Body, CreatedAt = DateTimeOffset.UtcNow, Version = 1 };
                Server.Messages[send.ClientMessageId] = message;
                if (LoseSendResponse) { LoseSendResponse = false; throw new HttpRequestException("Synthetic response loss."); }
                return Json(new ChatSendMessageResult { Message = message, IsDuplicate = existed });
            }
            if (route.EndsWith("/read", StringComparison.Ordinal))
            {
                ChatReadRequest read = await Read<ChatReadRequest>(request, token); Reads.Enqueue(read.ThroughMessageId);
                Server.LastRead = Math.Max(Server.LastRead, read.ThroughMessageId); ChatThreadDto accepted = Thread; ReadEntered = true;
                if (ReadGate is not null) await ReadGate.Task.WaitAsync(token);
                return Json(accepted);
            }
            if (route.EndsWith("/typing", StringComparison.Ordinal)) { TypingCalls++; return Json(new { accepted = true }); }
            if (route == "uploads" && request.Method == HttpMethod.Post)
            {
                ChatUploadRequest start = await Read<ChatUploadRequest>(request, token);
                Server.Upload = new() { Id = "upload-fixture", FileName = start.FileName, ContentType = start.ContentType, Size = start.Size }; return Json(Server.Upload);
            }
            if (route == "uploads/upload-fixture" && request.Method == HttpMethod.Get) return Json(Server.Upload!);
            if (route == "uploads/upload-fixture" && request.Method == HttpMethod.Put)
            {
                long offset = long.Parse(request.RequestUri.Query[8..], System.Globalization.CultureInfo.InvariantCulture); ChunkOffsets.Enqueue(offset);
                if (Server.Upload!.Offset != offset) throw new InvalidOperationException("Incorrect upload resume offset.");
                await request.Content!.CopyToAsync(Stream.Null, token);
                Server.Upload = Server.Upload with { Offset = offset + request.Content.Headers.ContentLength!.Value };
                if (LoseChunkResponse) { LoseChunkResponse = false; throw new HttpRequestException("Synthetic accepted chunk response loss."); }
                return Json(Server.Upload);
            }
            if (route == "uploads/upload-fixture/complete")
            {
                ChatUploadDto upload = Server.Upload!;
                Server.Upload = upload with { IsComplete = true, Attachment = new() { Id = upload.Id, FileName = upload.FileName,
                    ContentType = upload.ContentType, Size = upload.Size, Url = "/api/v2/chat/attachments/" + upload.Id } }; return Json(Server.Upload);
            }
            if (route == "uploads/upload-fixture" && request.Method == HttpMethod.Delete) return Json(new { accepted = true });
            throw new InvalidOperationException("Unexpected synthetic route: " + route);
        }
        private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { Content = new StringContent(JsonSerializer.Serialize(value, ChatJson.Options), Encoding.UTF8, "application/json") };
        private static async Task<T> Read<T>(HttpRequestMessage request, CancellationToken token)
            => JsonSerializer.Deserialize<T>(await request.Content!.ReadAsStringAsync(token), ChatJson.Options)!;
    }

    private sealed class FakeFiles(long size) : IChatAttachmentFileSource
    {
        internal int FailInspectionAt;
        private int _inspections;
        internal ConcurrentBag<string> Deleted { get; } = [];
        private readonly ConcurrentBag<PatternStream> _streams = [];
        internal int MaximumRead => _streams.Select(stream => stream.MaximumRead).DefaultIfEmpty().Max();
        public Task<ChatSelectedFile> InspectAsync(uint owner, string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); int count = Interlocked.Increment(ref _inspections);
            if (count == FailInspectionAt) throw new ChatWorkspaceException("chat-file-unavailable");
            return Task.FromResult(new ChatSelectedFile("C:\\synthetic-staged\\" + Guid.NewGuid().ToString("N") + ".blob", Path.GetFileName(path), "text/plain", size, DateTimeOffset.UnixEpoch, ""));
        }
        public Task<Stream> OpenVerifiedAsync(ChatLocalUpload file, CancellationToken token)
        { token.ThrowIfCancellationRequested(); PatternStream stream = new(file.Size); _streams.Add(stream); return Task.FromResult<Stream>(stream); }
        public Task<Stream> OpenPreviewAsync(ChatLocalUpload file, CancellationToken token) => OpenVerifiedAsync(file, token);
        public Task DeleteStagedAsync(uint owner, string path, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Deleted.Add(path); return Task.CompletedTask; }
    }

    private sealed class PatternStream(long size) : Stream
    {
        internal int MaximumRead;
        internal bool Disposed;
        public override bool CanRead => !Disposed;
        public override bool CanSeek => !Disposed;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        { MaximumRead = Math.Max(MaximumRead, buffer.Length); int count = (int)Math.Min(buffer.Length, size - Position); buffer[..count].Fill((byte)'A'); Position += count; return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => size + offset };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
