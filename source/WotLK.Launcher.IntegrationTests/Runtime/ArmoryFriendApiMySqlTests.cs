using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static class ArmoryFriendApiMySqlTests
{
    // Called only inside ArmoryApiMySqlTests' guarded, freshly created databases.
    internal static async Task RunAsync(LauncherServerOptions options, MySqlConnection connection)
    {
        await ExecuteAsync(connection, """
            INSERT INTO account(id,username) VALUES (4,'ARMORYEMPTYFRIEND');
            INSERT INTO atlas_launcher_profile(account_id) VALUES (3),(4);
            CREATE TABLE atlas_launcher_friendship (
                account_low_id INT UNSIGNED NOT NULL,
                account_high_id INT UNSIGNED NOT NULL,
                requested_by_id INT UNSIGNED NOT NULL,
                accepted_at DATETIME NULL,
                created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY(account_low_id,account_high_id),
                INDEX ix_atlas_friend_requested_by(requested_by_id),
                INDEX fk_atlas_friend_high(account_high_id),
                CONSTRAINT fk_atlas_friend_low FOREIGN KEY(account_low_id)
                    REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
                CONSTRAINT fk_atlas_friend_high FOREIGN KEY(account_high_id)
                    REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE,
                CONSTRAINT fk_atlas_friend_requester FOREIGN KEY(requested_by_id)
                    REFERENCES atlas_launcher_profile(account_id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
            INSERT INTO atlas_launcher_friendship(account_low_id,account_high_id,requested_by_id,accepted_at)
            VALUES (1,2,1,UTC_TIMESTAMP()),(1,3,1,NULL),(1,4,4,UTC_TIMESTAMP());
            INSERT INTO atlas_launcher_session(access_hash,account_id,access_expires_at,revoked_at)
            VALUES (@third,3,UTC_TIMESTAMP()+INTERVAL 1 HOUR,NULL);
            """, ("@third", TokenService.Hash("disposable-armory-account-three")));
        await ExecuteAsync(connection, $"""
            CREATE TABLE `{options.CharacterDatabaseName}`.character_stats (
                guid INT UNSIGNED PRIMARY KEY, strength INT UNSIGNED NOT NULL, agility INT UNSIGNED NOT NULL,
                stamina INT UNSIGNED NOT NULL, intellect INT UNSIGNED NOT NULL, spirit INT UNSIGNED NOT NULL,
                armor INT UNSIGNED NOT NULL, maxhealth INT UNSIGNED NOT NULL, maxpower1 INT UNSIGNED NOT NULL,
                attackPower INT UNSIGNED NULL, rangedAttackPower INT UNSIGNED NULL, rangedCritPct FLOAT NULL,
                spellPower INT UNSIGNED NULL, resilience INT UNSIGNED NULL);
            INSERT INTO `{options.CharacterDatabaseName}`.character_stats
                VALUES (101,11,12,13,14,15,16,170,180,705,99,1.571388,17,5),(201,21,37,29,36,25,143,270,280,60,47,5.808800220489502,0,0);
            """);

        LauncherDatabase database = new(options, new TokenService(), new LauncherSchemaMigrator(options));
        ArmoryRoster? roster = await database.ListFriendArmoryCharactersAsync(1, 2, CancellationToken.None);
        Require(roster is not null && roster.Characters.Count == 1 && roster.Characters[0].Character.Guid == 201,
            "An accepted friend's roster must contain only that friend's character.");
        Require(roster!.Characters[0].Values is { BaseAttackPower: 60, BaseRangedAttackPower: 47, RangedCritPct: 5.808800220489502, BaseSpellPower: 0, Resilience: 0 }
            && roster.Characters[0].Snapshot is null,
            $"An accepted friend's saved powers and physical critical chance must work without a combat capture: {JsonSerializer.Serialize(roster.Characters[0].Values)}.");
        ArmoryCatalog? catalog = await database.GetFriendArmoryCatalogAsync(1, 2, 201, CancellationToken.None);
        Require(catalog is not null && catalog.Items.Select(item => item.ItemId).SequenceEqual(new uint[] { 1004 }),
            "The friend's catalog must contain only the target character's equipped templates.");
        Require((await database.ListFriendArmoryCharactersAsync(2, 1, CancellationToken.None))?.Characters.Count == 2,
            "Accepted friendship must authorize the reverse account ordering too.");
        Require((await database.ListFriendArmoryCharactersAsync(1, 4, CancellationToken.None))?.Characters.Count == 0,
            "An accepted friend with no characters must return an empty roster, without falling back to the viewer.");
        Require((await database.GetFriendArmoryCatalogAsync(2, 1, 102, CancellationToken.None))?.Items.Count == 0,
            "An accepted friend's naked character must have an empty catalog.");

        foreach ((uint viewer, uint friend) in new (uint, uint)[] { (1, 1), (1, 3), (3, 1), (2, 4), (1, 9999), (0, 2), (1, 0), (9999, 2) })
        {
            Require(await database.ListFriendArmoryCharactersAsync(viewer, friend, CancellationToken.None) is null,
                "Self, incoming/outgoing pending, nonfriend, missing Atlas identity and invalid targets must be denied.");
            Require(await database.GetFriendArmoryCatalogAsync(viewer, friend, 201, CancellationToken.None) is null,
                "The catalog must not bypass friendship or the Atlas identity boundary.");
        }
        foreach (uint guid in new uint[] { 0, 101, 102, 9999, uint.MaxValue })
            Require(await database.GetFriendArmoryCatalogAsync(1, 2, guid, CancellationToken.None) is null,
                "Even an accepted friend ID cannot authorize another account's, nonexistent or invalid character GUID.");

        await ValidateHttpAsync(database);
        await ValidateConcurrentRemovalAsync(options, connection, database);
        Console.WriteLine("Friend armory MySQL + real loopback HTTP OK: accepted/pending/removed friendship, both account orders, target GUID ownership, empty roster, authentication, no-store, query rejection and shared viewer rate limit; concurrent removal blocked until the read transaction ends.");
    }

    private static async Task ValidateHttpAsync(LauncherDatabase database)
    {
        const string first = "disposable-armory-account-one";
        const string second = "disposable-armory-account-two";
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton<ArmoryReadLimiter>();
        await using WebApplication app = builder.Build();
        app.MapArmoryEndpoints();
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
            const string rosterPath = "/api/v1/friends/2/armory/characters";
            const string catalogPath = rosterPath + "/201/catalog";
            foreach (string path in new[] { rosterPath, catalogPath })
            {
                foreach (string? token in new[] { null, "invalid-disposable-token", "disposable-armory-revoked", "disposable-armory-expired" })
                    await ExpectAsync(path, token, HttpStatusCode.Unauthorized);
                foreach (string query in new[] { "accountId=1", "friendAccountId=1", "itemId=1008", "sql=SELECT+1" })
                    await ExpectAsync(path + "?" + query, first, HttpStatusCode.BadRequest);
            }
            using (HttpResponseMessage response = await GetAsync(rosterPath, first))
            {
                Require(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true
                    && response.Content.Headers.ContentType?.MediaType == "application/json", "Friend roster must be authenticated no-store JSON.");
                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                JsonElement[] rows = json.RootElement.GetProperty("characters").EnumerateArray().ToArray();
                Require(rows.Length == 1 && rows[0].GetProperty("character").GetProperty("guid").GetUInt32() == 201,
                    "The HTTP roster must preserve the target account, without exposing viewer characters.");
                JsonElement values = rows[0].GetProperty("values");
                Require(values.GetProperty("baseRangedAttackPower").GetDouble() == 47
                    && values.GetProperty("rangedCritPct").GetDouble() == 5.808800220489502
                    && !values.TryGetProperty("rangedAttackPower", out _) && !values.TryGetProperty("rangedHitPct", out _)
                    && rows[0].GetProperty("snapshot").ValueKind == JsonValueKind.Null,
                    "Friend HTTP must expose saved base power and exact critical chance without inventing totals or hit.");
            }
            using (HttpResponseMessage response = await GetAsync(catalogPath, first))
            {
                Require(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true,
                    "Friend catalog must return no-store JSON.");
                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                uint[] ids = json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("itemId").GetUInt32()).ToArray();
                Require(ids.SequenceEqual(new uint[] { 1004 }), "Friend HTTP catalog must not include another character's gear.");
            }
            foreach (string friend in new[] { "0", "-1", "1", "3", "9999", "4294967296" })
            {
                await ExpectAsync($"/api/v1/friends/{friend}/armory/characters", first, HttpStatusCode.NotFound);
                await ExpectAsync($"/api/v1/friends/{friend}/armory/characters/201/catalog", first, HttpStatusCode.NotFound);
            }
            foreach (string guid in new[] { "0", "-1", "101", "102", "9999", "4294967296" })
                await ExpectAsync(rosterPath + $"/{guid}/catalog", first, HttpStatusCode.NotFound);
            await ExpectAsync("/api/v1/friends/1/armory/characters", "disposable-armory-account-three", HttpStatusCode.NotFound);
            await ExpectAsync("/api/v1/friends/4/armory/characters", first, HttpStatusCode.OK);
            await ExpectAsync("/api/v1/friends/1/armory/characters/102/catalog", second, HttpStatusCode.OK);

            ArmoryReadLimiter limiter = app.Services.GetRequiredService<ArmoryReadLimiter>();
            for (int attempt = 0; attempt < 120; attempt++) using (limiter.Acquire(1)) { }
            foreach (string path in new[] { rosterPath, catalogPath, "/api/v1/armory/characters" })
            {
                using HttpResponseMessage response = await GetAsync(path, first);
                Require(response.StatusCode == HttpStatusCode.TooManyRequests && response.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60),
                    "Own and friend armory reads must share the viewer's rate limit.");
            }
            await ExpectAsync("/api/v1/friends/1/armory/characters", second, HttpStatusCode.OK);
            return;

            async Task<HttpResponseMessage> GetAsync(string path, string? token)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, path);
                if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await client.SendAsync(request);
            }
            async Task ExpectAsync(string path, string? token, HttpStatusCode expected)
            {
                using HttpResponseMessage response = await GetAsync(path, token);
                Require(response.StatusCode == expected,
                    $"Friend armory HTTP {path}: expected {(int)expected}, received {(int)response.StatusCode}.");
            }
        }
        finally { await app.StopAsync(); }
    }

    private static async Task ValidateConcurrentRemovalAsync(
        LauncherServerOptions options, MySqlConnection observer, LauncherDatabase database)
    {
        // Hold character reads long enough to observe the real friendship row lock.
        // No timing-only assertion: performance_schema confirms the shared lock,
        // then a conflicting DELETE must receive InnoDB's lock wait timeout.
        await using MySqlConnection gate = new(options.ConnectionString);
        await using MySqlConnection remover = new(options.ConnectionString);
        await gate.OpenAsync();
        await remover.OpenAsync();
        await ExecuteAsync(remover, "SET SESSION innodb_lock_wait_timeout=1;");
        await ExecuteAsync(gate, $"LOCK TABLES `{options.CharacterDatabaseName}`.characters WRITE;");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(12));
        Task<ArmoryRoster?> read = database.ListFriendArmoryCharactersAsync(1, 2, timeout.Token);
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            bool sharedLock = false;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await using MySqlCommand locks = observer.CreateCommand();
                locks.CommandText = """
                    SELECT COUNT(*) FROM performance_schema.data_locks
                    WHERE OBJECT_SCHEMA=@schema AND OBJECT_NAME='atlas_launcher_friendship'
                      AND LOCK_TYPE='RECORD' AND LOCK_MODE LIKE 'S%';
                    """;
                locks.Parameters.AddWithValue("@schema", observer.Database);
                sharedLock = Convert.ToInt32(await locks.ExecuteScalarAsync(timeout.Token)) > 0;
                if (sharedLock) break;
                if (read.IsCompleted) await read;
                await Task.Delay(25, timeout.Token);
            }
            Require(sharedLock && !read.IsCompleted, "The friend read must hold a real shared friendship row lock while reading characters.");
            bool blocked = false;
            try { await ExecuteAsync(remover, "DELETE FROM atlas_launcher_friendship WHERE account_low_id=1 AND account_high_id=2;"); }
            catch (MySqlException error) when (error.Number == 1205) { blocked = true; }
            Require(blocked, "Concurrent friendship removal must wait for the armory transaction to finish.");
        }
        finally
        {
            await ExecuteAsync(gate, "UNLOCK TABLES;");
            await read;
        }
        Require((await read)?.Characters.Single().Character.Guid == 201,
            "The read authorized before revocation must finish with the friend's snapshot.");
        Require(await ExecuteAsync(remover, "DELETE FROM atlas_launcher_friendship WHERE account_low_id=1 AND account_high_id=2;") == 1,
            "Friendship removal must succeed after the read releases its lock.");
        Require(await database.ListFriendArmoryCharactersAsync(1, 2, CancellationToken.None) is null
            && await database.GetFriendArmoryCatalogAsync(1, 2, 201, CancellationToken.None) is null,
            "Both routes' data methods must deny the next read after friendship removal.");
    }

    private static async Task<int> ExecuteAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
