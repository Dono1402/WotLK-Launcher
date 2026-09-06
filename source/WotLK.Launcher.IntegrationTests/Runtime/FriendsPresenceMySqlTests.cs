using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static class FriendsPresenceMySqlTests
{
    internal static async Task<int> RunAsync()
    {
        string supplied = Environment.GetEnvironmentVariable("ATLAS_PRESENCE_TEST_DB")
            ?? throw new InvalidOperationException("ATLAS_PRESENCE_TEST_DB must identify a new disposable local database.");
        MySqlConnectionStringBuilder builder = new(supplied);
        if (builder.Server != "127.0.0.1" || builder.Port != 13307
            || !Regex.IsMatch(builder.Database, "^atlas_presence_test_[a-z0-9_]{1,30}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Only 127.0.0.1:13307 and a fresh atlas_presence_test_ database are permitted.");

        string databaseName = builder.Database;
        string characterDatabase = databaseName + "_chars";
        builder.Pooling = false;
        LauncherServerOptions options = new()
        {
            ConnectionString = builder.ConnectionString,
            CharacterDatabaseName = characterDatabase,
            MaximumSchemaVersion = 5
        };
        builder.Database = string.Empty;
        await using MySqlConnection admin = new(builder.ConnectionString);
        await admin.OpenAsync();
        string version = Convert.ToString(await ScalarAsync(admin, "SELECT VERSION();"), CultureInfo.InvariantCulture) ?? "";
        Require(version.StartsWith("8.4.", StringComparison.Ordinal), "MySQL 8.4 is required.");
        bool databaseCreated = false, charactersCreated = false;
        try
        {
            // No IF NOT EXISTS: an existing database is never reused or erased by this test.
            await ExecuteAsync(admin, $"CREATE DATABASE `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;");
            databaseCreated = true;
            await ExecuteAsync(admin, $"CREATE DATABASE `{characterDatabase}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;");
            charactersCreated = true;
            await using MySqlConnection connection = new(options.ConnectionString);
            await connection.OpenAsync();
            await CreateFixtureAsync(connection, characterDatabase);
            LauncherDatabase database = new(options, new TokenService(), new LauncherSchemaMigrator(options));

            IReadOnlyList<LauncherFriend> friends = await ListWithQueryCountAsync(database);
            ValidatePresence(friends);
            await ValidateAuthenticationAsync(database, connection);
            LauncherFriend touched = (await ListWithQueryCountAsync(database)).Single(friend => friend.AccountId == 11);
            Require(touched.LauncherOnline && touched.CharacterName is null, "A valid authenticated request must restore launcher-only presence.");

            // The legacy query must also work when the newer profile columns do not exist.
            await ExecuteAsync(connection, "ALTER TABLE atlas_launcher_profile DROP COLUMN status_message, DROP COLUMN bio;");
            options.MaximumSchemaVersion = 4;
            IReadOnlyList<LauncherFriend> legacy = await ListWithQueryCountAsync(database);
            ValidatePresence(legacy);
            Require(legacy.All(friend => friend.StatusMessage == "" && friend.Bio == ""),
                "The schema-4 route must not read or expose newer social profile columns.");
            Console.WriteLine($"Friends presence MySQL {version} OK: launcher-only, offline character, game online, stale/revoked/expired, multisession, pending privacy, auth touch/throttle, legacy schema 4, exactly two queries per friend list.");
            return 0;
        }
        finally
        {
            if (charactersCreated) await ExecuteAsync(admin, $"DROP DATABASE `{characterDatabase}`;");
            if (databaseCreated) await ExecuteAsync(admin, $"DROP DATABASE `{databaseName}`;");
        }
    }

    private static async Task CreateFixtureAsync(MySqlConnection connection, string characterDatabase)
    {
        await ExecuteAsync(connection, $"""
            CREATE TABLE account (id INT UNSIGNED PRIMARY KEY, username VARCHAR(32) NOT NULL);
            CREATE TABLE atlas_launcher_profile (
                account_id INT UNSIGNED PRIMARY KEY, display_username VARCHAR(32) NOT NULL,
                avatar_key VARCHAR(128) NULL, status_message VARCHAR(80) NULL, bio VARCHAR(280) NULL);
            CREATE TABLE atlas_launcher_friendship (
                account_low_id INT UNSIGNED NOT NULL, account_high_id INT UNSIGNED NOT NULL,
                requested_by_id INT UNSIGNED NOT NULL, accepted_at DATETIME NULL,
                PRIMARY KEY(account_low_id, account_high_id));
            CREATE TABLE atlas_launcher_session (
                id BINARY(16) PRIMARY KEY, account_id INT UNSIGNED NOT NULL,
                access_hash BINARY(32) NOT NULL UNIQUE, access_expires_at DATETIME NOT NULL,
                revoked_at DATETIME NULL,
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                INDEX ix_atlas_session_account(account_id));
            CREATE TABLE atlas_launcher_profile_avatar (
                account_id INT UNSIGNED PRIMARY KEY, current_avatar_asset_id BINARY(16) NULL);
            CREATE TABLE atlas_launcher_avatar_asset (
                id BINARY(16) PRIMARY KEY, version BIGINT UNSIGNED NOT NULL, status TINYINT NOT NULL);
            CREATE TABLE `{characterDatabase}`.characters (
                account INT UNSIGNED NOT NULL, guid INT UNSIGNED PRIMARY KEY, name VARCHAR(12) NOT NULL,
                level TINYINT UNSIGNED NOT NULL, `class` TINYINT UNSIGNED NOT NULL,
                zone INT UNSIGNED NOT NULL, online TINYINT(1) NOT NULL, logout_time INT UNSIGNED NOT NULL);
            """);

        for (uint accountId = 1; accountId <= 11; accountId++)
        {
            await ExecuteAsync(connection, """
                INSERT INTO account(id, username) VALUES(@id, @name);
                INSERT INTO atlas_launcher_profile(account_id, display_username, status_message, bio)
                VALUES(@id, @name, 'Fixture status', 'Fixture bio');
                """, ("@id", accountId), ("@name", $"PRESENCE{accountId}"));
            if (accountId > 1)
                await ExecuteAsync(connection, """
                    INSERT INTO atlas_launcher_friendship(account_low_id, account_high_id, requested_by_id, accepted_at)
                    VALUES(1, @id, @requester, IF(@pending, NULL, UTC_TIMESTAMP()));
                    """, ("@id", accountId), ("@requester", accountId == 10 ? 10U : 1U), ("@pending", accountId is 9 or 10));
        }

        foreach (uint id in new uint[] { 2, 3, 4, 9, 10 })
            await InsertSessionAsync(connection, id, Token(id), 5);
        await InsertSessionAsync(connection, 5, Token(5), 61);
        await InsertSessionAsync(connection, 6, Token(6), 30, revoked: true);
        await InsertSessionAsync(connection, 7, Token(7), 30, expired: true);
        await InsertSessionAsync(connection, 8, Token(8) + "-stale", 120);
        await InsertSessionAsync(connection, 8, Token(8) + "-revoked", 5, revoked: true);
        await InsertSessionAsync(connection, 8, Token(8) + "-expired", 5, expired: true);
        await InsertSessionAsync(connection, 8, Token(8), 5);
        await InsertSessionAsync(connection, 11, Token(11), 120);
        foreach (uint id in new uint[] { 3, 4, 9, 10 })
            await ExecuteAsync(connection, $"""
                INSERT INTO `{characterDatabase}`.characters(account, guid, name, level, `class`, zone, online, logout_time)
                VALUES(@id, @id, @name, 80, 8, 1519, @online, UNIX_TIMESTAMP() - 300);
                """, ("@id", id), ("@name", $"Character{id}"), ("@online", id != 3));
    }

    private static async Task InsertSessionAsync(MySqlConnection connection, uint accountId, string token, int age,
        bool revoked = false, bool expired = false) => await ExecuteAsync(connection, """
        INSERT INTO atlas_launcher_session(id, account_id, access_hash, access_expires_at, revoked_at, updated_at)
        VALUES(@id, @account, @hash, UTC_TIMESTAMP() + INTERVAL @expiry SECOND,
            IF(@revoked, UTC_TIMESTAMP(), NULL), UTC_TIMESTAMP() - INTERVAL @age SECOND);
        """, ("@id", Guid.NewGuid().ToByteArray()), ("@account", accountId), ("@hash", TokenService.Hash(token)),
        ("@expiry", expired ? -60 : 3600), ("@revoked", revoked), ("@age", age));

    private static void ValidatePresence(IReadOnlyList<LauncherFriend> friends)
    {
        Require(friends.Count == 10, "The fixture must return each friendship once despite multiple sessions.");
        LauncherFriend noCharacter = Friend(2);
        Require(noCharacter.LauncherOnline && !noCharacter.Online && noCharacter.CharacterName is null
            && noCharacter.Characters?.Count == 0, "A launcher-only account without characters must be online in Atlas.");
        LauncherFriend offlineCharacter = Friend(3);
        Require(offlineCharacter.LauncherOnline && !offlineCharacter.Online && offlineCharacter.CharacterName == "Character3",
            "An offline game character must not erase active launcher presence.");
        Require(Friend(4).Online && Friend(4).LauncherOnline && Friend(4).Characters?.Single().Online == true,
            "Game-online and launcher-online must coexist without changing character presence.");
        foreach (uint id in new uint[] { 5, 6, 7 })
            Require(!Friend(id).LauncherOnline, $"Session fixture {id} is stale, revoked or expired and must be offline.");
        Require(Friend(8).LauncherOnline, "One valid recent session must suffice among stale, expired and revoked sessions.");
        foreach (uint id in new uint[] { 9, 10 })
        {
            LauncherFriend pending = Friend(id);
            Require(!pending.LauncherOnline && pending.LauncherLastSeenAt is null && !pending.Online
                && pending.LastSeenAt is null && pending.Characters?.Count == 0 && pending.StatusMessage == "" && pending.Bio == "",
                "Pending incoming and outgoing requests must not disclose presence, last seen or character details.");
        }
        LauncherFriend Friend(uint id) => friends.Single(friend => friend.AccountId == id);
    }

    private static async Task ValidateAuthenticationAsync(LauncherDatabase database, MySqlConnection connection)
    {
        AuthenticatedAccount? authenticated = await database.AuthenticateAsync(Token(11), CancellationToken.None);
        Require(authenticated?.AccountId == 11, "A valid fixture session must authenticate.");
        long age = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT TIMESTAMPDIFF(SECOND, updated_at, UTC_TIMESTAMP()) FROM atlas_launcher_session WHERE access_hash=@hash;",
            ("@hash", TokenService.Hash(Token(11)))), CultureInfo.InvariantCulture);
        Require(age is >= 0 and <= 2, "Authentication must refresh a session older than the ten-second write threshold.");

        await ExecuteAsync(connection,
            "UPDATE atlas_launcher_session SET updated_at=UTC_TIMESTAMP()-INTERVAL 5 SECOND WHERE access_hash=@hash;",
            ("@hash", TokenService.Hash(Token(11))));
        object? before = await SessionTimeAsync(11);
        Require(await database.AuthenticateAsync(Token(11), CancellationToken.None) is not null,
            "The throttled session must still authenticate.");
        Require(Equals(before, await SessionTimeAsync(11)), "A request inside ten seconds must not rewrite session activity.");

        string beforeInvalid = await SnapshotAsync();
        foreach (string token in new[] { Token(6), Token(7), "presence-test-unknown-token" })
            Require(await database.AuthenticateAsync(token, CancellationToken.None) is null,
                "Revoked, expired and unknown tokens must fail authentication.");
        Require(beforeInvalid == await SnapshotAsync(), "Rejected authentication must not refresh any session.");

        Task<object?> SessionTimeAsync(uint id) => ScalarAsync(connection,
            "SELECT updated_at FROM atlas_launcher_session WHERE access_hash=@hash;", ("@hash", TokenService.Hash(Token(id))));
        async Task<string> SnapshotAsync() => Convert.ToString(await ScalarAsync(connection,
            "SELECT GROUP_CONCAT(CONCAT(HEX(id), ':', UNIX_TIMESTAMP(updated_at)) ORDER BY id SEPARATOR '|') FROM atlas_launcher_session;"),
            CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task<IReadOnlyList<LauncherFriend>> ListWithQueryCountAsync(LauncherDatabase database)
    {
        ConcurrentQueue<string> statements = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == "MySqlConnector",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "Execute"
                    && (activity.GetTagItem("db.statement") ?? activity.GetTagItem("db.query.text")) is string sql)
                    statements.Enqueue(sql);
            }
        };
        ActivitySource.AddActivityListener(listener);
        IReadOnlyList<LauncherFriend> result = await database.ListFriendsAsync(1, CancellationToken.None);
        Require(statements.Count == 2 && statements.Any(sql => sql.Contains("atlas_launcher_friendship", StringComparison.Ordinal))
            && statements.Any(sql => sql.Contains("characters", StringComparison.Ordinal)),
            $"A friend list must execute exactly two SQL statements; observed {statements.Count}.");
        return result;
    }

    private static string Token(uint id) => $"presence-fixture-token-{id}";
    private static async Task ExecuteAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> ScalarAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }
    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
