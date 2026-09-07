using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static class ServerStatusMySqlTests
{
    internal static async Task<int> RunAsync()
    {
        string supplied = Environment.GetEnvironmentVariable("ATLAS_STATUS_TEST_DB")
            ?? Environment.GetEnvironmentVariable("ATLAS_CHAT_TEST_DB")
            ?? throw new InvalidOperationException("A new disposable loopback test database is required.");
        MySqlConnectionStringBuilder builder = new(supplied);
        Require(builder.Server == "127.0.0.1" && builder.Port == 13307
            && Regex.IsMatch(builder.Database, @"\Aatlas_(?:status|chat)_test_[a-z0-9_]{1,28}\z", RegexOptions.CultureInvariant),
            "Only 127.0.0.1:13307 and a fresh atlas_status_test_ or atlas_chat_test_ database are permitted.");
        string authName = builder.Database;
        string charsName = authName + "_chars";
        string botsName = authName + "_bots";
        string otherRealm = authName + "_other";
        builder.Pooling = false;
        LauncherServerOptions options = new() { ConnectionString = builder.ConnectionString, CharacterDatabaseName = charsName };
        builder.Database = string.Empty;
        await using MySqlConnection admin = new(builder.ConnectionString);
        await admin.OpenAsync();
        string version = Convert.ToString(await ScalarAsync(admin, "SELECT VERSION();"), CultureInfo.InvariantCulture) ?? "";
        Require(version.StartsWith("8.4.", StringComparison.Ordinal), "The test requires the workspace MySQL 8.4 fixture.");
        List<string> created = [];
        try
        {
            foreach (string name in new[] { authName, charsName, botsName, otherRealm })
            {
                // A pre-existing database is neither reused nor deleted by this fixture.
                await ExecuteAsync(admin, $"CREATE DATABASE `{name}`;");
                created.Add(name);
            }
            await ExecuteAsync(admin, $"""
                CREATE TABLE `{charsName}`.characters (guid INT UNSIGNED PRIMARY KEY, account INT UNSIGNED NOT NULL, online TINYINT UNSIGNED NOT NULL);
                CREATE TABLE `{otherRealm}`.characters (guid INT UNSIGNED PRIMARY KEY, account INT UNSIGNED NOT NULL, online TINYINT UNSIGNED NOT NULL);
                CREATE TABLE `{botsName}`.playerbots_account_type (account_id INT UNSIGNED PRIMARY KEY, account_type TINYINT UNSIGNED NOT NULL);
                INSERT INTO `{charsName}`.characters VALUES (1,10,1),(2,11,1),(3,12,0),(4,20,1),(5,21,1),(6,22,1),(7,20,0);
                INSERT INTO `{otherRealm}`.characters VALUES (99,99,1);
                INSERT INTO `{botsName}`.playerbots_account_type VALUES (20,0),(21,1),(22,2);
                """);
            LauncherDatabase database = new(options, new TokenService(), new LauncherSchemaMigrator(options));
            ServerOnlinePlayerCount count = await database.GetOnlinePlayerCountAsync(CancellationToken.None);
            Require(count == new ServerOnlinePlayerCount(5, "characters"), "The actual COUNT must include only online characters in the configured realm.");
            options.PlayerbotsDatabaseName = botsName;
            count = await database.GetOnlinePlayerCountAsync(CancellationToken.None);
            Require(count == new ServerOnlinePlayerCount(2, "excluding-random-bots"),
                "All identified generated accounts must be excluded, including types 0, 1 and 2.");

            HashSet<int> probed = [];
            AtlasStatusService service = new(database, NullLogger<AtlasStatusService>.Instance,
                (port, _) => { probed.Add(port); return Task.FromResult(true); });
            LauncherStatusResponse status = await service.GetAsync(CancellationToken.None);
            Require(status.OnlinePlayers == 2 && status.OnlinePlayerCountKind == "excluding-random-bots"
                && probed.SetEquals([1119,8084,8086,4000]), "The status contract must carry the real count alongside all existing service probes.");
            AtlasStatusService offline = new(database, NullLogger<AtlasStatusService>.Instance,
                (port, _) => Task.FromResult(port != 4000));
            status = await offline.GetAsync(CancellationToken.None);
            Require(!status.WorldServer && status.OnlinePlayers is null && status.OnlinePlayerCountKind is null,
                "An offline world must hide persisted online flags instead of showing stale players.");

            await ExecuteAsync(admin, $"UPDATE `{charsName}`.characters SET online=0;");
            count = await database.GetOnlinePlayerCountAsync(CancellationToken.None);
            Require(count.Count == 0, "A real zero must remain distinguishable from a read error.");

            options.PlayerbotsDatabaseName = botsName + "_missing";
            await ExpectAsync<MySqlException>(() => database.GetOnlinePlayerCountAsync(CancellationToken.None));
            status = await service.GetAsync(CancellationToken.None);
            Require(status.Api && status.WorldServer && status.OnlinePlayers is null && status.OnlinePlayerCountKind is null,
                "A bot database error must not silently fall back to a bot-inclusive count or break service status.");
            options.PlayerbotsDatabaseName = "invalid`database";
            await ExpectAsync<InvalidOperationException>(() => database.GetOnlinePlayerCountAsync(CancellationToken.None));
            status = await service.GetAsync(CancellationToken.None);
            Require(status.OnlinePlayers is null, "An invalid database identifier must fail safely.");
            options.PlayerbotsDatabaseName = botsName;

            await ExecuteAsync(admin, $"LOCK TABLES `{charsName}`.characters WRITE;");
            try
            {
                Stopwatch watch = Stopwatch.StartNew();
                status = await service.GetAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                Require(status.OnlinePlayers is null && status.WorldServer && watch.Elapsed < TimeSpan.FromSeconds(7),
                    "A blocked COUNT must time out while preserving a usable status response.");
                using CancellationTokenSource cancelled = new(TimeSpan.FromMilliseconds(80));
                await ExpectAsync<OperationCanceledException>(() => service.GetAsync(cancelled.Token));
            }
            finally { await ExecuteAsync(admin, "UNLOCK TABLES;"); }

            await ExecuteAsync(admin, $"DROP TABLE `{charsName}`.characters;");
            await ExpectAsync<MySqlException>(() => database.GetOnlinePlayerCountAsync(CancellationToken.None));
            status = await service.GetAsync(CancellationToken.None);
            Require(status.OnlinePlayers is null, "A missing characters table must remain unavailable, never zero.");
            Console.WriteLine($"Server status MySQL {version} OK: actual realm-scoped COUNT, online/offline rows, bot types 0/1/2, optional bot DB, true zero, world offline, missing databases/tables, invalid identifier, locked-query timeout and caller cancellation. All owned databases cleaned up.");
            return 0;
        }
        finally
        {
            for (int index = created.Count - 1; index >= 0; index--)
                await ExecuteAsync(admin, $"DROP DATABASE `{created[index]}`;");
        }
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using MySqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> ScalarAsync(MySqlConnection connection, string sql)
    {
        await using MySqlCommand command = new(sql, connection);
        return await command.ExecuteScalarAsync();
    }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name} from the real database operation.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
