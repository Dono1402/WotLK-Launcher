using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static class ChatApiMySqlTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static int _checks;

    internal static async Task<int> RunAsync()
    {
        _checks = 0;
        string supplied = Environment.GetEnvironmentVariable("ATLAS_CHAT_TEST_DB")
            ?? throw new InvalidOperationException("ATLAS_CHAT_TEST_DB must identify a new disposable local database.");
        MySqlConnectionStringBuilder builder = new(supplied);
        if (builder.Server != "127.0.0.1" || builder.Port != 13307
            || !Regex.IsMatch(builder.Database, "^atlas_chat_test_[a-z0-9_]{1,30}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Only 127.0.0.1:13307 and a fresh atlas_chat_test_ database are permitted.");
        string auth = builder.Database, characters = auth + "_chars";
        builder.Pooling = false;
        LauncherServerOptions options = new() { ConnectionString = builder.ConnectionString,
            CharacterDatabaseName = characters, MaximumSchemaVersion = 5 };
        builder.Database = string.Empty;
        await using MySqlConnection admin = new(builder.ConnectionString);
        await admin.OpenAsync();
        string version = Convert.ToString(await ScalarAsync(admin, "SELECT VERSION();"), CultureInfo.InvariantCulture) ?? "";
        Require(version.StartsWith("8.4.", StringComparison.Ordinal), "The fixture requires MySQL 8.4.");
        List<string> created = [];
        try
        {
            foreach (string name in new[] { auth, characters })
            {
                await ExecuteAsync(admin, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;");
                created.Add(name);
            }
            await using MySqlConnection connection = new(options.ConnectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, $"""
                CREATE TABLE account(id INT UNSIGNED NOT NULL PRIMARY KEY, username VARCHAR(32) NOT NULL)
                    ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
                CREATE TABLE `{characters}`.characters(guid INT UNSIGNED NOT NULL PRIMARY KEY,
                    account INT UNSIGNED NOT NULL, online TINYINT UNSIGNED NOT NULL DEFAULT 0,
                    name VARCHAR(12) NOT NULL, INDEX ix_chat_fixture_account(account));
                """);
            for (uint account = 1; account <= 19; account++)
                await ExecuteAsync(connection, "INSERT INTO account(id,username) VALUES(@id,@name);",
                    ("@id", account), ("@name", "LOGIN" + account));
            await ValidateMigrationAsync(options, connection);
            await SeedProfilesAsync(options, connection, characters);
            ValidateText();
            await ValidateCeilingHttpAsync(options);
            options.MaximumSchemaVersion = 6;
            await new LauncherSchemaMigrator(options).MigrateAsync();
            await new LauncherSchemaValidator().ValidateChatAsync(connection, None);
            await new LauncherSchemaMigrator(options).MigrateAsync();
            Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_schema_history;")) == 6,
                "The fresh fixture must contain exactly six applied migrations.");
            await ValidateSchemaDriftAsync(connection);
            LauncherDatabase database = CreateDatabase(options);
            await ValidateMessagesAsync(database, connection, characters);
            await ValidatePaginationAsync(database);
            await ValidateConcurrentPollingAsync(database, connection);
            await ValidateRateLimitAndGameInboxAsync(database, connection);
            _checks += await ChatBridgeSqlMySqlTests.RunAsync(database, connection);
            await ValidateHttpAsync(options, connection);
            Console.WriteLine($"Chat API MySQL {version} PASS: {_checks} assertions; schema 5 remains capped, schema 6 migrates and validates, durable private messages, accepted-friend isolation, UUID idempotence, ordered concurrent polling, monotone read cursors, shared durable send quota, online-only outbox, game character ownership and inbox ingestion, real loopback HTTP authentication/validation/rate limiting. Disposable local databases only; no game client or production access.");
            return 0;
        }
        finally
        {
            List<Exception> errors = [];
            for (int index = created.Count - 1; index >= 0; index--)
                try { await ExecuteAsync(admin, $"DROP DATABASE `{created[index]}`;"); }
                catch (Exception exception) { errors.Add(exception); }
            if (errors.Count > 0) throw new AggregateException("Disposable chat database cleanup failed.", errors);
        }
    }

    private static LauncherDatabase CreateDatabase(LauncherServerOptions options) => new(options, new TokenService(), new LauncherSchemaMigrator(options));

    private static async Task ValidateMigrationAsync(LauncherServerOptions options, MySqlConnection connection)
    {
        IReadOnlyList<LauncherSchemaMigrationOutcome> outcomes = await new LauncherSchemaMigrator(options).MigrateAsync();
        Require(outcomes.Count == 8 && outcomes.Take(5).All(x => x.State == LauncherSchemaMigrationState.Applied)
            && outcomes.Skip(5).All(x => x.State == LauncherSchemaMigrationState.BlockedByCeiling)
            && outcomes.Skip(5).Select(x => x.Version).SequenceEqual([6U, 7U, 8U]),
            "Ceiling 5 must block chat migrations 6 and 7 and global presence migration 8.");
        Require(Convert.ToInt64(await ScalarAsync(connection,
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND (table_name LIKE 'atlas_launcher_chat_%' OR table_name='atlas_launcher_presence');")) == 0,
            "Ceiling 5 must create no chat or global presence table.");
        await ExpectChatAsync(() => CreateDatabase(options).ListChatConversationsAsync(1, null, 50, None), "chat-unavailable");
    }

    private static async Task SeedProfilesAsync(LauncherServerOptions options, MySqlConnection connection, string characters)
    {
        for (uint account = 1; account <= 16; account++)
        {
            string display = account switch { 1 => "AliceAtlas", 2 => "BobAtlas", 3 => "CharlieAtlas", _ => "Atlas" + account };
            await ExecuteAsync(connection, """
                INSERT INTO atlas_launcher_profile(account_id,display_username,email_normalized) VALUES(@id,@display,@email);
                INSERT INTO atlas_launcher_session(id,account_id,access_hash,refresh_hash,access_expires_at,refresh_expires_at)
                VALUES(@session,@id,@access,@refresh,UTC_TIMESTAMP()+INTERVAL 1 HOUR,UTC_TIMESTAMP()+INTERVAL 1 DAY);
                """, ("@id", account), ("@display", display), ("@email", $"chat{account}@fixture.invalid"),
                ("@session", Guid.NewGuid().ToByteArray(bigEndian: true)), ("@access", TokenService.Hash(Token(account))),
                ("@refresh", TokenService.Hash("refresh-fixture-" + account)));
            await ExecuteAsync(connection, $"INSERT INTO `{characters}`.characters(guid,account,online,name) VALUES(@guid,@account,@online,@name);",
                ("@guid", account * 100 + 1), ("@account", account), ("@online", account == 3 ? 0 : 1), ("@name", "Character" + account));
        }
        foreach ((uint low, uint high) in new[] { (1U,2U), (1U,3U), (5U,6U), (7U,8U), (7U,9U), (10U,11U), (12U,13U), (14U,15U) })
            await ExecuteAsync(connection, "INSERT INTO atlas_launcher_friendship(account_low_id,account_high_id,requested_by_id,accepted_at) VALUES(@low,@high,@low,UTC_TIMESTAMP());",
                ("@low", low), ("@high", high));
        await ExecuteAsync(connection, """
            INSERT INTO atlas_launcher_friendship(account_low_id,account_high_id,requested_by_id) VALUES(1,4,1);
            INSERT INTO atlas_launcher_session(id,account_id,access_hash,refresh_hash,access_expires_at,refresh_expires_at,revoked_at)
            VALUES(@revokedId,1,@revoked,@revokedRefresh,UTC_TIMESTAMP()+INTERVAL 1 HOUR,UTC_TIMESTAMP()+INTERVAL 1 DAY,UTC_TIMESTAMP()),
                  (@expiredId,1,@expired,@expiredRefresh,UTC_TIMESTAMP()-INTERVAL 1 HOUR,UTC_TIMESTAMP()+INTERVAL 1 DAY,NULL);
            """, ("@revokedId", Guid.NewGuid().ToByteArray()), ("@revoked", TokenService.Hash("chat-revoked")), ("@revokedRefresh", TokenService.Hash("chat-revoked-refresh")),
            ("@expiredId", Guid.NewGuid().ToByteArray()), ("@expired", TokenService.Hash("chat-expired")), ("@expiredRefresh", TokenService.Hash("chat-expired-refresh")));
    }

    private static void ValidateText()
    {
        Require(ChatMessageValidation.Normalize("  Salut\r\n世界 👋  ") == "Salut\n世界 👋", "Text normalization must preserve Unicode and line breaks.");
        Require(ChatMessageValidation.Normalize(new string('界', 1000)).Length == 1000, "The valid maximum-length Unicode message must be retained.");
        Require(ChatMessageValidation.Normalize(string.Concat(Enumerable.Repeat("👋", 500))).Length == 1000,
            "A supplementary character must count as two UTF-16 units, matching the composer and game bridge.");
        foreach (string? invalid in new[] { null, "", " \t\n ", new string('x', 1001), "a\0b", "a\u0001b", "\ud800", "a\rb",
            string.Concat(Enumerable.Repeat("👋", 501)) }.Concat(Enumerable.Range(0x202A, 5).Concat(Enumerable.Range(0x2066, 4)).Select(code => "a" + (char)code + "b")))
        {
            try { ChatMessageValidation.Normalize(invalid); }
            catch (ChatOperationException error) when (error.Code == "chat-invalid-message") { _checks++; continue; }
            throw new InvalidOperationException("Invalid chat text was accepted.");
        }
    }

    private static async Task ValidateCeilingHttpAsync(LauncherServerOptions options)
    {
        await using WebApplication app = CreateApp(options, withWorker: true);
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()) };
            using HttpResponseMessage anonymous = await RequestAsync(client, HttpMethod.Get, "/api/v1/chat/conversations", null);
            Require(anonymous.StatusCode == HttpStatusCode.Unauthorized, "Capped chat must still authenticate before revealing availability.");
            using HttpResponseMessage authenticated = await RequestAsync(client, HttpMethod.Get, "/api/v1/chat/conversations", Token(1));
            Require(authenticated.StatusCode == HttpStatusCode.ServiceUnavailable, "Ceiling 5 must keep chat unavailable without querying missing chat tables.");
        }
        finally { await app.StopAsync(); }
    }

    private static async Task ValidateSchemaDriftAsync(MySqlConnection connection)
    {
        await ExecuteAsync(connection, "ALTER TABLE atlas_launcher_chat_account ADD COLUMN fixture_drift INT NULL;");
        bool rejected = false;
        try { await new LauncherSchemaValidator().ValidateChatAsync(connection, None); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Chat schema structural drift must be rejected.");
        await ExecuteAsync(connection, "ALTER TABLE atlas_launcher_chat_account DROP COLUMN fixture_drift;");
        await new LauncherSchemaValidator().ValidateChatAsync(connection, None);
    }

    private static async Task ValidateMessagesAsync(LauncherDatabase database, MySqlConnection connection, string characters)
    {
        LauncherChatConversations empty = await database.ListChatConversationsAsync(1, null, 100, None);
        Require(empty.Conversations.Count == 0 && empty.LastMessageId == 0 && empty.UnreadCount == 0, "A new inbox must be empty.");
        foreach (uint target in new[] { 0U, 1U, 4U, 16U, 19U, 999U })
        {
            await ExpectChatAsync(() => database.SendChatMessageAsync(1, target, NewRequest("forbidden"), None), "chat-not-friends");
            await ExpectChatAsync(() => database.ListChatMessagesAsync(1, target, null, null, 50, None), "chat-not-friends");
        }
        Guid requestId = Guid.NewGuid();
        LauncherChatSendResult sent = await database.SendChatMessageAsync(1, 2, new(requestId, " Salut Bob 👋\r\nligne 2 "), None);
        Require(!sent.IsDuplicate && sent.Message.Id > 0 && sent.Message.ClientMessageId == requestId
            && sent.Message.SenderAccountId == 1 && sent.Message.RecipientAccountId == 2
            && sent.Message.SenderUsername == "AliceAtlas" && sent.Message.Origin == "launcher"
            && sent.Message.Body == "Salut Bob 👋\nligne 2" && sent.Message.CreatedAt.Offset == TimeSpan.Zero,
            "A persisted message must use the Atlas profile identity and normalized body.");
        Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_chat_outbox WHERE message_id=@id AND recipient_account_id=2 AND status=0;", ("@id", sent.Message.Id))) == 1,
            "An online recipient must receive exactly one transactional game outbox row.");
        LauncherChatSendResult repeated = await database.SendChatMessageAsync(1, 2, new(requestId, sent.Message.Body), None);
        Require(repeated.IsDuplicate && repeated.Message == sent.Message, "An identical retry must return the original persisted message.");
        await ExpectChatAsync(() => database.SendChatMessageAsync(1, 2, new(requestId, "different"), None), "chat-idempotency-conflict");
        await ExpectChatAsync(() => database.SendChatMessageAsync(1, 3, new(requestId, sent.Message.Body), None), "chat-idempotency-conflict");
        Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT send_window_count FROM atlas_launcher_chat_account WHERE account_id=1;")) == 1,
            "Replays and conflicting IDs must not consume additional send quota.");
        LauncherChatConversations bob = await database.ListChatConversationsAsync(2, null, 100, None);
        Require(bob.Conversations.Single().FriendUsername == "AliceAtlas" && bob.UnreadCount == 1
            && bob.Conversations[0].UnreadCount == 1 && bob.LastMessageId == sent.Message.Id, "Recipient conversations and unread state must match.");
        LauncherChatReadResult read = await database.MarkChatReadAsync(2, 1, sent.Message.Id, None);
        Require(read.LastReadMessageId == sent.Message.Id && (await database.ListChatConversationsAsync(2, null, 100, None)).UnreadCount == 0,
            "Marking an owned conversation read must clear only its unread messages.");
        LauncherChatSendResult reply = await database.SendChatMessageAsync(2, 1, NewRequest("Bonjour Alice"), None);
        await database.MarkChatReadAsync(2, 1, reply.Message.Id, None);
        Require((await database.MarkChatReadAsync(2, 1, sent.Message.Id, None)).LastReadMessageId == reply.Message.Id,
            "A read cursor must never move backwards.");
        Require((await database.ListChatMessagesAsync(1, 2, null, null, 50, None)).FriendLastReadMessageId == reply.Message.Id,
            "The other participant's read cursor must be visible only within the conversation.");
        LauncherChatSendResult offline = await database.SendChatMessageAsync(1, 3, NewRequest("Hors jeu"), None);
        Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_chat_outbox WHERE message_id=@id;", ("@id", offline.Message.Id))) == 0,
            "An offline recipient must not receive a delayed game outbox entry.");
        await ExecuteAsync(connection, $"UPDATE `{characters}`.characters SET online=1 WHERE account=3;");
        Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_chat_outbox WHERE message_id=@id;", ("@id", offline.Message.Id))) == 0,
            "Logging into the game later must not create a retrospective game copy.");
        await ExpectChatAsync(() => database.MarkChatReadAsync(1, 2, offline.Message.Id, None), "chat-invalid-read-cursor");
        await ExpectChatAsync(() => database.MarkChatReadAsync(1, 2, 0, None), "chat-invalid-request");
        await ExpectChatAsync(() => database.ListChatMessagesAsync(1, 2, 0, 100, 50, None), "chat-invalid-query");
        Guid concurrentId = Guid.NewGuid();
        LauncherChatSendResult[] duplicates = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => database.SendChatMessageAsync(1, 2, new(concurrentId, "one concurrent message"), None)));
        Require(duplicates.Select(x => x.Message.Id).Distinct().Count() == 1 && duplicates.Count(x => !x.IsDuplicate) == 1,
            "Concurrent retries must commit exactly one canonical message.");
        long rowsBefore = Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_chat_message;"));
        await ExecuteAsync(connection, "DELETE FROM atlas_launcher_friendship WHERE account_low_id=1 AND account_high_id=2;");
        await ExpectChatAsync(() => database.ListChatMessagesAsync(1, 2, null, null, 50, None), "chat-not-friends");
        await ExpectChatAsync(() => database.SendChatMessageAsync(1, 2, new(requestId, sent.Message.Body), None), "chat-not-friends");
        Require((await database.ListChatConversationsAsync(1, null, 100, None)).Conversations.All(x => x.FriendAccountId != 2)
            && (await database.PollChatUpdatesAsync(1, 0, 100, None)).Messages.All(x => x.SenderAccountId != 2 && x.RecipientAccountId != 2),
            "Removing friendship must hide conversation history and poll updates from both views.");
        Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_chat_message;")) == rowsBefore,
            "Friend removal must preserve durable messages.");
        await ExecuteAsync(connection, "INSERT INTO atlas_launcher_friendship(account_low_id,account_high_id,requested_by_id,accepted_at) VALUES(1,2,1,UTC_TIMESTAMP());");
        Require((await database.ListChatMessagesAsync(1, 2, null, null, 50, None)).Messages.Any(x => x.Id == sent.Message.Id),
            "Accepted friends must regain their own persisted history after reconnecting.");
    }

    private static async Task ValidatePaginationAsync(LauncherDatabase database)
    {
        List<long> expected = [];
        for (int index = 0; index < 8; index++)
            expected.Add((await database.SendChatMessageAsync(5, 6, NewRequest("page " + index), None)).Message.Id);
        LauncherChatMessages latest = await database.ListChatMessagesAsync(6, 5, null, null, 3, None);
        Require(latest.HasMore && latest.Messages.Select(x => x.Id).SequenceEqual(expected.TakeLast(3)), "Default history must return the newest page in ascending display order.");
        LauncherChatMessages older = await database.ListChatMessagesAsync(6, 5, null, latest.Messages[0].Id, 3, None);
        Require(older.Messages.Select(x => x.Id).SequenceEqual(expected.Skip(2).Take(3)), "beforeId must page backwards without duplicates.");
        LauncherChatMessages first = await database.ListChatMessagesAsync(6, 5, 0, null, 3, None);
        Require(first.HasMore && first.Messages.Select(x => x.Id).SequenceEqual(expected.Take(3)), "afterId must page forward in ID order.");
        List<long> polled = [];
        long cursor = 0;
        bool more;
        do
        {
            LauncherChatUpdates updates = await database.PollChatUpdatesAsync(6, cursor, 3, None);
            polled.AddRange(updates.Messages.Select(x => x.Id));
            cursor = updates.LastMessageId;
            more = updates.HasMore;
        } while (more);
        Require(polled.SequenceEqual(expected) && cursor == expected[^1], "Global polling must traverse every owned message once.");
    }

    private static async Task ValidateConcurrentPollingAsync(LauncherDatabase database, MySqlConnection connection)
    {
        await ExecuteAsync(connection, "INSERT INTO atlas_launcher_chat_account(account_id) VALUES(7) ON DUPLICATE KEY UPDATE account_id=7;");
        List<Task<LauncherChatSendResult>> sends = [];
        for (int index = 0; index < 12; index++)
        {
            sends.Add(database.SendChatMessageAsync(7, 8, NewRequest("concurrent outgoing " + index), None));
            sends.Add(database.SendChatMessageAsync(9, 7, NewRequest("concurrent incoming " + index), None));
        }
        Task<LauncherChatSendResult>[] unrelated = Enumerable.Range(0, 3)
            .Select(index => database.SendChatMessageAsync(10, 11, NewRequest("unrelated " + index), None)).ToArray();
        Task<LauncherChatSendResult[]> completion = Task.WhenAll(sends);
        HashSet<long> seen = [];
        long cursor = 0;
        do
        {
            LauncherChatUpdates page = await database.PollChatUpdatesAsync(7, cursor, 3, None);
            foreach (LauncherChatMessage message in page.Messages)
            {
                Require(message.SenderAccountId == 7 || message.RecipientAccountId == 7, "Global poll leaked another account's message.");
                Require(seen.Add(message.Id), "Concurrent polling duplicated a message after its cursor.");
            }
            Require(page.LastMessageId >= cursor, "A global poll cursor moved backwards.");
            cursor = page.LastMessageId;
            if (completion.IsCompleted && !page.HasMore) break;
            await Task.Delay(5);
        } while (true);
        LauncherChatSendResult[] committed = await completion;
        await Task.WhenAll(unrelated);
        LauncherChatUpdates final = await database.PollChatUpdatesAsync(7, cursor, 100, None);
        foreach (LauncherChatMessage message in final.Messages) seen.Add(message.Id);
        Require(seen.SetEquals(committed.Select(x => x.Message.Id)), "The cursor skipped a concurrently committed message across conversations.");
        Require((await database.ListChatConversationsAsync(7, null, 1, None)).HasMore, "Conversation pagination must expose a remaining page.");
    }

    private static async Task ValidateRateLimitAndGameInboxAsync(LauncherDatabase database, MySqlConnection connection)
    {
        SendChatMessageRequest first = NewRequest("quota 0");
        await database.SendChatMessageAsync(12, 13, first, None);
        for (int index = 1; index < ChatMessageValidation.MessagesPerMinute; index++)
            await database.SendChatMessageAsync(12, 13, NewRequest("quota " + index), None);
        await ExpectChatAsync(() => database.SendChatMessageAsync(12, 13, NewRequest("quota overflow"), None), "chat-rate-limited");
        Require((await database.SendChatMessageAsync(12, 13, first, None)).IsDuplicate, "A duplicate retry must work even after its account exhausts its send budget.");
        Guid overQuota = await InsertInboxAsync(connection, 12, 1201, 13, "game shares send quota");
        Require(await database.ProcessChatGameInboxAsync(32, None) == 1, "The game inbox row must be processed.");
        await AssertInboxAsync(connection, overQuota, 2, "chat-rate-limited");
        await ExecuteAsync(connection, "UPDATE atlas_launcher_chat_account SET send_window_started_at=UTC_TIMESTAMP(6)-INTERVAL 61 SECOND WHERE account_id=12;");
        await database.SendChatMessageAsync(12, 13, NewRequest("next window"), None);
        Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT send_window_count FROM atlas_launcher_chat_account WHERE account_id=12;")) == 1,
            "An expired send window must reset durably.");
        Guid valid = await InsertInboxAsync(connection, 14, 1401, 15, "Depuis le jeu 👋");
        Guid wrongCharacter = await InsertInboxAsync(connection, 14, 1501, 15, "forged character");
        Guid wrongFriend = await InsertInboxAsync(connection, 14, 1401, 1, "not friends");
        Guid invalidBody = await InsertInboxAsync(connection, 14, 1401, 15, "bad\u0001body");
        int[] processed = await Task.WhenAll(database.ProcessChatGameInboxAsync(32, None), database.ProcessChatGameInboxAsync(32, None));
        Require(processed.Sum() == 4, "Two inbox workers must consume each pending row exactly once.");
        await AssertInboxAsync(connection, valid, 1, null);
        await AssertInboxAsync(connection, wrongCharacter, 2, "chat-invalid-game-identity");
        await AssertInboxAsync(connection, wrongFriend, 2, "chat-not-friends");
        await AssertInboxAsync(connection, invalidBody, 2, "chat-invalid-message");
        LauncherChatMessage game = (await database.ListChatMessagesAsync(15, 14, null, null, 50, None)).Messages.Single();
        Require(game.ClientMessageId == valid && game.SenderUsername == "Atlas14" && game.Origin == "game"
            && game.SenderAccountId == 14 && game.RecipientAccountId == 15 && game.Body == "Depuis le jeu 👋",
            "Imported game messages must use the real Atlas sender and become launcher history.");
        Require(Convert.ToInt64(await ScalarAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_chat_outbox WHERE message_id=@id AND recipient_account_id=15;", ("@id", game.Id))) == 1,
            "A game-origin message may also mirror to the recipient's active game, never to its sender.");
        // Simulate a crash/retry after canonical persistence but before an external receipt was observed.
        await ExecuteAsync(connection, "UPDATE atlas_launcher_chat_inbox SET status=0,processed_at=NULL,message_id=NULL WHERE request_id=@request;",
            ("@request", valid.ToByteArray(bigEndian: true)));
        Require(await database.ProcessChatGameInboxAsync(32, None) == 1, "An inbox replay must be acknowledged again.");
        Require((await database.ListChatMessagesAsync(15, 14, null, null, 50, None)).Messages.Count == 1,
            "An inbox replay must not duplicate canonical messages or outbox entries.");
        await AssertInboxAsync(connection, valid, 1, null);
    }

    private static WebApplication CreateApp(LauncherServerOptions options, bool withWorker)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(CreateDatabase(options));
        builder.Services.AddSingleton<ChatRequestLimiter>();
        if (withWorker) builder.Services.AddHostedService<ChatGameInboxWorker>();
        WebApplication app = builder.Build();
        app.MapChatEndpoints();
        return app;
    }

    private static async Task ValidateHttpAsync(LauncherServerOptions options, MySqlConnection connection)
    {
        await using WebApplication app = CreateApp(options, withWorker: true);
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
            string messages = "/api/v1/chat/conversations/2/messages";
            foreach (string? token in new[] { null, "unknown-chat-token", "chat-revoked", "chat-expired" })
                await StatusAsync(HttpMethod.Get, "/api/v1/chat/conversations", token, null, HttpStatusCode.Unauthorized);
            foreach (string path in new[] { "/api/v1/chat/conversations/4/messages", "/api/v1/chat/conversations/16/messages",
                "/api/v1/chat/conversations/1/messages", "/api/v1/chat/conversations/4294967296/messages" })
                await StatusAsync(HttpMethod.Get, path, Token(1), null, HttpStatusCode.NotFound);
            foreach (string query in new[] { "senderAccountId=2", "limit=0", "limit=101", "limit=-1", "afterId=-1",
                "afterId=1.2", "afterId=9223372036854775808", "afterId=0&beforeId=1", "limit=1&limit=2" })
                await StatusAsync(HttpMethod.Get, messages + "?" + query, Token(1), null, HttpStatusCode.BadRequest);
            string valid = JsonSerializer.Serialize(new { clientMessageId = Guid.NewGuid(), body = "HTTP authentifié" });
            using (HttpResponseMessage response = await RequestAsync(client, HttpMethod.Post, messages, Token(1), valid))
            {
                Require(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "Authenticated send must be successful JSON with no-store.");
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Require(document.RootElement.GetProperty("message").GetProperty("senderUsername").GetString() == "AliceAtlas"
                    && document.RootElement.GetProperty("message").GetProperty("senderAccountId").GetUInt32() == 1,
                    "HTTP must derive its author from the authenticated account.");
            }
            using (HttpResponseMessage duplicate = await RequestAsync(client, HttpMethod.Post, messages, Token(1), valid))
            {
                using JsonDocument document = JsonDocument.Parse(await duplicate.Content.ReadAsStringAsync());
                Require(duplicate.StatusCode == HttpStatusCode.OK && document.RootElement.GetProperty("isDuplicate").GetBoolean(), "HTTP retry must report the original message.");
            }
            foreach (string invalid in new[] { "null", "[]", "{}", "{", "{\"body\":\"no key\"}",
                "{\"clientMessageId\":\"00000000-0000-0000-0000-000000000000\",\"body\":\"x\"}",
                "{\"clientMessageId\":\"not-a-uuid\",\"body\":\"x\"}",
                valid.TrimEnd('}') + ",\"senderAccountId\":2}", valid.TrimEnd('}') + ",\"body\":\"second body\"}",
                JsonSerializer.Serialize(new { clientMessageId = Guid.NewGuid(), body = "hidden\u202Eordering" }),
                JsonSerializer.Serialize(new { clientMessageId = Guid.NewGuid(), body = new string('x', 1001) }) })
                await StatusAsync(HttpMethod.Post, messages, Token(1), invalid, HttpStatusCode.BadRequest);
            await StatusAsync(HttpMethod.Post, messages, "chat-revoked", valid, HttpStatusCode.Unauthorized);
            await StatusAsync(HttpMethod.Post, messages, "chat-expired", valid, HttpStatusCode.Unauthorized);
            await StatusAsync(HttpMethod.Post, messages, Token(1), new string('x', 9000), HttpStatusCode.RequestEntityTooLarge);
            using (HttpRequestMessage chunked = new(HttpMethod.Post, messages))
            {
                chunked.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(1));
                chunked.Headers.TransferEncodingChunked = true;
                chunked.Content = new StringContent(new string('x', 9000), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.SendAsync(chunked);
                Require(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Chunked request bodies must obey the same byte bound.");
            }
            using (HttpResponseMessage visible = await RequestAsync(client, HttpMethod.Get, messages, Token(1)))
            {
                using JsonDocument document = JsonDocument.Parse(await visible.Content.ReadAsStringAsync());
                long last = document.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("id").GetInt64();
                await StatusAsync(HttpMethod.Post, "/api/v1/chat/conversations/2/read", Token(1),
                    JsonSerializer.Serialize(new { throughMessageId = last }), HttpStatusCode.OK);
            }
            await StatusAsync(HttpMethod.Get, "/api/v1/chat/updates?afterId=0&limit=100", Token(1), null, HttpStatusCode.OK);
            await StatusAsync(HttpMethod.Post, "/api/v1/chat/conversations/2/read", Token(1), "{\"throughMessageId\":-1}", HttpStatusCode.BadRequest);
            Guid background = await InsertInboxAsync(connection, 15, 1501, 14, "Le worker transmet au launcher");
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && Convert.ToInt32(await ScalarAsync(connection,
                "SELECT status FROM atlas_launcher_chat_inbox WHERE request_id=@request;", ("@request", background.ToByteArray(bigEndian: true)))) == 0)
                await Task.Delay(50);
            await AssertInboxAsync(connection, background, 1, null);
            using (HttpResponseMessage received = await RequestAsync(client, HttpMethod.Get, "/api/v1/chat/updates?afterId=0", Token(14)))
            {
                using JsonDocument document = JsonDocument.Parse(await received.Content.ReadAsStringAsync());
                Require(document.RootElement.GetProperty("messages").EnumerateArray().Any(x => x.GetProperty("clientMessageId").GetGuid() == background
                    && x.GetProperty("origin").GetString() == "game" && x.GetProperty("senderUsername").GetString() == "Atlas15"),
                    "The real hosted inbox worker must deliver game-origin messages through HTTP polling.");
            }
            ChatRequestLimiter limiter = app.Services.GetRequiredService<ChatRequestLimiter>();
            for (int index = 0; index < 180; index++) using (limiter.Acquire(1)) { }
            using (HttpResponseMessage limited = await RequestAsync(client, HttpMethod.Get, "/api/v1/chat/conversations", Token(1)))
                Require(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60),
                    "The per-account HTTP limiter must return 429 and Retry-After.");
            await StatusAsync(HttpMethod.Get, "/api/v1/chat/conversations", Token(2), null, HttpStatusCode.OK);

            async Task StatusAsync(HttpMethod method, string path, string? token, string? json, HttpStatusCode expected)
            {
                using HttpResponseMessage response = await RequestAsync(client, method, path, token, json);
                Require(response.StatusCode == expected, $"Chat HTTP {method} {path}: expected {(int)expected}, got {(int)response.StatusCode}.");
                Require(response.Headers.CacheControl?.NoStore == true, "Every chat result must use no-store.");
            }
        }
        finally { await app.StopAsync(); }
    }

    private static Task<HttpResponseMessage> RequestAsync(HttpClient client, HttpMethod method, string path, string? token, string? json = null)
    {
        HttpRequestMessage request = new(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return SendAsync();
        async Task<HttpResponseMessage> SendAsync()
        {
            using (request) return await client.SendAsync(request);
        }
    }

    private static async Task<Guid> InsertInboxAsync(MySqlConnection connection, uint sender, uint character, uint recipient, string body)
    {
        Guid request = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO atlas_launcher_chat_inbox(request_id,realm_id,sender_account_id,sender_character_guid,recipient_account_id,body,created_at)
            VALUES(@request,1,@sender,@character,@recipient,@body,UTC_TIMESTAMP(6));
            """, ("@request", request.ToByteArray(bigEndian: true)), ("@sender", sender), ("@character", character),
            ("@recipient", recipient), ("@body", body));
        return request;
    }

    private static async Task AssertInboxAsync(MySqlConnection connection, Guid request, int status, string? error)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT status,message_id,error_code,processed_at FROM atlas_launcher_chat_inbox WHERE request_id=@request;";
        command.Parameters.AddWithValue("@request", request.ToByteArray(bigEndian: true));
        await using MySqlDataReader reader = await command.ExecuteReaderAsync();
        Require(await reader.ReadAsync() && reader.GetByte("status") == status && !reader.IsDBNull("processed_at")
            && (error is null ? reader.IsDBNull("error_code") && !reader.IsDBNull("message_id")
                : reader.GetString("error_code") == error && reader.IsDBNull("message_id")), "Game inbox acknowledgement did not match its validated outcome.");
    }

    private static SendChatMessageRequest NewRequest(string body) => new(Guid.NewGuid(), body);
    private static string Token(uint account) => "chat-fixture-" + account;
    private static async Task ExpectChatAsync(Func<Task> operation, string code)
    {
        try { await operation(); }
        catch (ChatOperationException exception) when (exception.Code == code) { _checks++; return; }
        throw new InvalidOperationException("Expected chat outcome was not returned: " + code);
    }
    private static void Require(bool value, string message)
    {
        _checks++;
        if (!value) throw new InvalidOperationException(message);
    }
    private static async Task ExecuteAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> ScalarAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] values)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return await command.ExecuteScalarAsync();
    }
}
