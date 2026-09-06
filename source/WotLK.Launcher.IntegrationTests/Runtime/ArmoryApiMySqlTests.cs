using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySqlConnector;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static class ArmoryApiMySqlTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string EmptyEnchantments = string.Join(" ", Enumerable.Repeat("0", 36));
    private static readonly string InstanceEnchantments = string.Join(" ", Enumerable.Range(0, 36).Select(i => i == 21 ? "42" : "0"));

    internal static async Task<int> RunAsync()
    {
        string supplied = Environment.GetEnvironmentVariable("ATLAS_ARMORY_TEST_DB")
            ?? throw new InvalidOperationException("ATLAS_ARMORY_TEST_DB must identify a new disposable local database.");
        MySqlConnectionStringBuilder builder = new(supplied);
        if (builder.Server != "127.0.0.1" || builder.Port != 13307
            || !Regex.IsMatch(builder.Database, "^atlas_armory_test_[a-z0-9_]{1,30}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Only 127.0.0.1:13307 and a fresh atlas_armory_test_ database are permitted.");

        string authDatabase = builder.Database;
        string characterDatabase = authDatabase + "_chars";
        string worldDatabase = authDatabase + "_world";
        builder.Pooling = false;
        LauncherServerOptions options = new()
        {
            ConnectionString = builder.ConnectionString,
            CharacterDatabaseName = characterDatabase,
            WorldDatabaseName = worldDatabase,
            MaximumSchemaVersion = 5
        };
        builder.Database = string.Empty;
        await using MySqlConnection admin = new(builder.ConnectionString);
        await admin.OpenAsync();
        string version = Convert.ToString(await ScalarAsync(admin, "SELECT VERSION();"), CultureInfo.InvariantCulture) ?? "";
        Require(version.StartsWith("8.4.", StringComparison.Ordinal), "MySQL 8.4 is required.");

        List<string> created = [];
        try
        {
            foreach (string name in new[] { authDatabase, characterDatabase, worldDatabase })
            {
                // Existing databases are neither reused nor removed: add only after CREATE succeeds.
                await ExecuteAsync(admin, $"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;");
                created.Add(name);
            }

            await using MySqlConnection connection = new(options.ConnectionString);
            await connection.OpenAsync();
            await CreateFixtureAsync(connection, characterDatabase, worldDatabase);
            await ValidateWithoutOptionalTablesAsync(CreateDatabase(options));
            await ValidateSavedStatisticsAsync(options, connection, characterDatabase);
            await ValidateNativeSnapshotsAsync(options, connection, characterDatabase);
            await ValidateCharacterLimitAsync(CreateDatabase(options), connection, characterDatabase);
            await ValidateRemovedOptionalTablesAsync(options, connection, characterDatabase);
            await ValidateHttpAsync(options, connection);
            Console.WriteLine($"Armory API MySQL {version} OK: account and GUID isolation, empty/naked characters, duplicate equipped templates, instance ownership, bag/bank exclusion, optional tables, native snapshot validation, bounded roster and current/native catalog union. Real loopback HTTP: authentication, invalid queries, JSON no-store, 4 MiB response bound and per-account rate limit. Disposable local databases only; no production access.");
            return 0;
        }
        finally
        {
            List<Exception> cleanupErrors = [];
            for (int index = created.Count - 1; index >= 0; index--)
            {
                try { await ExecuteAsync(admin, $"DROP DATABASE `{created[index]}`;"); }
                catch (Exception error) { cleanupErrors.Add(error); }
            }
            if (cleanupErrors.Count != 0)
                throw new AggregateException("Disposable armory database cleanup failed.", cleanupErrors);
        }
    }

    private static LauncherDatabase CreateDatabase(LauncherServerOptions options) =>
        new(options, new TokenService(), new LauncherSchemaMigrator(options));

    private static async Task CreateFixtureAsync(MySqlConnection connection, string characters, string world)
    {
        string statColumns = string.Join(",\n", Enumerable.Range(1, 10).SelectMany(index => new[]
        {
            $"`stat_type{index}` SMALLINT UNSIGNED NOT NULL DEFAULT 0",
            $"`stat_value{index}` INT NOT NULL DEFAULT 0"
        }));
        string spellColumns = string.Join(",\n", Enumerable.Range(1, 5).SelectMany(index => new[]
        {
            $"`spellid_{index}` INT NOT NULL DEFAULT 0",
            $"`spelltrigger_{index}` INT NOT NULL DEFAULT 0"
        }));
        await ExecuteAsync(connection, $"""
            CREATE TABLE account (id INT UNSIGNED PRIMARY KEY, username VARCHAR(32) NOT NULL);
            INSERT INTO account(id, username) VALUES (1,'ARMORYONE'),(2,'ARMORYTWO'),(3,'ARMORYLIMIT');
            CREATE TABLE `{characters}`.characters (
                guid INT UNSIGNED PRIMARY KEY, account INT UNSIGNED NOT NULL, name VARCHAR(12) NOT NULL,
                race TINYINT UNSIGNED NOT NULL DEFAULT 1, `class` TINYINT UNSIGNED NOT NULL DEFAULT 8,
                gender TINYINT UNSIGNED NOT NULL DEFAULT 0, level TINYINT UNSIGNED NOT NULL DEFAULT 20,
                skin TINYINT UNSIGNED NOT NULL DEFAULT 0, face TINYINT UNSIGNED NOT NULL DEFAULT 0,
                hairStyle TINYINT UNSIGNED NOT NULL DEFAULT 0, hairColor TINYINT UNSIGNED NOT NULL DEFAULT 0,
                facialStyle TINYINT UNSIGNED NOT NULL DEFAULT 0, online TINYINT UNSIGNED NOT NULL DEFAULT 0,
                zone INT UNSIGNED NOT NULL DEFAULT 1519, logout_time INT UNSIGNED NOT NULL DEFAULT 1600000000,
                INDEX ix_armory_test_account(account));
            CREATE TABLE `{characters}`.character_inventory (
                guid INT UNSIGNED NOT NULL, bag INT UNSIGNED NOT NULL, slot TINYINT UNSIGNED NOT NULL,
                item INT UNSIGNED NOT NULL, PRIMARY KEY(guid, bag, slot));
            CREATE TABLE `{characters}`.item_instance (
                guid INT UNSIGNED PRIMARY KEY, owner_guid INT UNSIGNED NOT NULL,
                itemEntry INT UNSIGNED NOT NULL, randomPropertyId INT NOT NULL DEFAULT 0,
                enchantments TEXT NOT NULL);
            CREATE TABLE `{world}`.item_template (
                entry INT UNSIGNED PRIMARY KEY, displayid INT UNSIGNED NOT NULL,
                name VARCHAR(255) NOT NULL, description TEXT NOT NULL,
                Quality TINYINT UNSIGNED NOT NULL DEFAULT 0, ItemLevel SMALLINT UNSIGNED NOT NULL DEFAULT 0,
                `class` TINYINT UNSIGNED NOT NULL DEFAULT 0, subclass TINYINT UNSIGNED NOT NULL DEFAULT 0,
                InventoryType TINYINT UNSIGNED NOT NULL DEFAULT 0, RequiredLevel SMALLINT UNSIGNED NOT NULL DEFAULT 0,
                armor INT UNSIGNED NOT NULL DEFAULT 0, block INT UNSIGNED NOT NULL DEFAULT 0,
                bonding TINYINT UNSIGNED NOT NULL DEFAULT 0, MaxDurability INT UNSIGNED NOT NULL DEFAULT 0,
                delay INT UNSIGNED NOT NULL DEFAULT 0,
                dmg_min1 FLOAT NOT NULL DEFAULT 0, dmg_max1 FLOAT NOT NULL DEFAULT 0, dmg_type1 TINYINT UNSIGNED NOT NULL DEFAULT 0,
                dmg_min2 FLOAT NOT NULL DEFAULT 0, dmg_max2 FLOAT NOT NULL DEFAULT 0, dmg_type2 TINYINT UNSIGNED NOT NULL DEFAULT 0,
                {statColumns},
                holy_res INT UNSIGNED NOT NULL DEFAULT 0, fire_res INT UNSIGNED NOT NULL DEFAULT 0,
                nature_res INT UNSIGNED NOT NULL DEFAULT 0, frost_res INT UNSIGNED NOT NULL DEFAULT 0,
                shadow_res INT UNSIGNED NOT NULL DEFAULT 0, arcane_res INT UNSIGNED NOT NULL DEFAULT 0,
                {spellColumns},
                socketColor_1 INT UNSIGNED NOT NULL DEFAULT 0, socketColor_2 INT UNSIGNED NOT NULL DEFAULT 0,
                socketColor_3 INT UNSIGNED NOT NULL DEFAULT 0, socketBonus INT UNSIGNED NOT NULL DEFAULT 0,
                ScalingStatDistribution INT UNSIGNED NOT NULL DEFAULT 0, ScalingStatValue INT UNSIGNED NOT NULL DEFAULT 0);
            CREATE TABLE `{world}`.item_template_locale (
                ID INT UNSIGNED NOT NULL, locale VARCHAR(4) NOT NULL,
                Name VARCHAR(255) NULL, Description TEXT NULL, PRIMARY KEY(ID, locale));
            INSERT INTO `{characters}`.characters(guid,account,name) VALUES (101,1,'Ownmage'),(102,1,'Nakedmage'),(201,2,'Othermage');
            """);

        for (uint id = 1001; id <= 1008; id++)
            await ExecuteAsync(connection, $"""
                INSERT INTO `{world}`.item_template
                    (entry,displayid,name,description,Quality,ItemLevel,`class`,subclass,InventoryType,
                     RequiredLevel,armor,block,bonding,MaxDurability,delay,dmg_min1,dmg_max1,dmg_type1,
                     stat_type1,stat_value1,holy_res,spellid_1,spelltrigger_1,socketColor_1,socketBonus)
                VALUES (@id,@display,@name,'Fixture description',2,40,4,1,1,20,35,3,2,40,2000,1.25,2.5,2,7,4,7,9001,1,2,42);
                """, ("@id", id), ("@display", id + 1000), ("@name", $"Fixture item {id}"));
        await ExecuteAsync(connection, $"""
            INSERT INTO `{world}`.item_template_locale(ID,locale,Name,Description)
            VALUES (1001,'frFR','Objet équipé de test','Description française de test');
            """);

        await InsertItemAsync(connection, characters, 5001, 101, 101, 0, 0, 1001);
        await InsertItemAsync(connection, characters, 5002, 101, 101, 0, 2, 1001, 7, InstanceEnchantments);
        await InsertItemAsync(connection, characters, 5003, 101, 101, 0, 15, 1002);
        await InsertItemAsync(connection, characters, 5004, 201, 201, 0, 0, 1004);
        await InsertItemAsync(connection, characters, 5005, 101, 101, 1, 0, 1005);
        await InsertItemAsync(connection, characters, 5006, 101, 101, 0, 39, 1006);
        await InsertItemAsync(connection, characters, 5007, 101, 201, 0, 5, 1007);
        // 1003 is snapshot-only; 1008 is an unrelated world template, not an equipped item.
    }

    private static Task InsertItemAsync(MySqlConnection connection, string characters, uint instance,
        uint character, uint owner, uint bag, byte slot, uint template, int randomProperty = 0, string? enchantments = null) =>
        ExecuteAsync(connection, $"""
            INSERT INTO `{characters}`.item_instance(guid,owner_guid,itemEntry,randomPropertyId,enchantments)
            VALUES (@instance,@owner,@template,@property,@enchants);
            INSERT INTO `{characters}`.character_inventory(guid,bag,slot,item) VALUES (@character,@bag,@slot,@instance);
            """, ("@instance", instance), ("@owner", owner), ("@template", template), ("@property", randomProperty),
            ("@enchants", enchantments ?? EmptyEnchantments), ("@character", character), ("@bag", bag), ("@slot", slot));

    private static async Task ValidateWithoutOptionalTablesAsync(LauncherDatabase database)
    {
        JsonElement roster = await RosterAsync(database, 1);
        AssertOwnedRoster(roster);
        foreach (JsonElement row in roster.GetProperty("characters").EnumerateArray())
            Require(row.GetProperty("snapshot").ValueKind == JsonValueKind.Null
                && row.GetProperty("values").ValueKind == JsonValueKind.Null,
                "Missing optional tables must yield null snapshots/statistics without hiding characters.");
        Require((await RosterAsync(database, 9)).GetProperty("characters").GetArrayLength() == 0,
            "An account without characters must receive an empty array.");
        JsonElement other = await RosterAsync(database, 2);
        Require(other.GetProperty("characters").GetArrayLength() == 1
            && CharacterRow(other, 201).GetProperty("equipment").GetArrayLength() == 1,
            "The second account must see only its own character.");
        Require(await database.GetArmoryCatalogAsync(1, 201, CancellationToken.None) is null
            && await database.GetArmoryCatalogAsync(2, 101, CancellationToken.None) is null
            && await database.GetArmoryCatalogAsync(1, 9999, CancellationToken.None) is null,
            "Foreign and nonexistent character GUIDs must not return an item catalog.");
        JsonElement naked = await CatalogAsync(database, 1, 102);
        Require(naked.GetProperty("items").GetArrayLength() == 0, "An owned naked character must have an empty catalog, not null.");
        JsonElement catalog = await CatalogAsync(database, 1, 101);
        AssertCatalogIds(catalog, 1001, 1002);
        AssertCatalogProjection(catalog);
    }

    private static async Task ValidateSavedStatisticsAsync(LauncherServerOptions options, MySqlConnection connection, string characters)
    {
        await ExecuteAsync(connection, $"""
            CREATE TABLE `{characters}`.character_stats (
                guid INT UNSIGNED PRIMARY KEY, strength INT UNSIGNED NOT NULL, agility INT UNSIGNED NOT NULL,
                stamina INT UNSIGNED NOT NULL, intellect INT UNSIGNED NOT NULL, spirit INT UNSIGNED NOT NULL,
                armor INT UNSIGNED NOT NULL, maxhealth INT UNSIGNED NOT NULL, maxpower1 INT UNSIGNED NOT NULL);
            INSERT INTO `{characters}`.character_stats VALUES (101,11,12,13,14,15,16,170,180),(201,91,92,93,94,95,96,970,980);
            """);
        JsonElement roster = await RosterAsync(CreateDatabase(options), 1);
        AssertOwnedRoster(roster);
        JsonElement values = CharacterRow(roster, 101).GetProperty("values");
        Require(values.EnumerateObject().Count() == 8 && values.GetProperty("strength").GetInt32() == 11
            && values.GetProperty("maxHealth").GetInt32() == 170 && values.GetProperty("maxMana").GetInt32() == 180,
            "The eight saved character statistics must be projected without using another character's values.");
        Require(CharacterRow(roster, 102).GetProperty("values").ValueKind == JsonValueKind.Null,
            "A missing optional statistics row must stay null.");
        Require(CharacterRow(roster, 101).GetProperty("snapshot").ValueKind == JsonValueKind.Null,
            "Saved statistics must work while the native snapshot table is absent.");
    }

    private static async Task ValidateNativeSnapshotsAsync(LauncherServerOptions options, MySqlConnection connection, string characters)
    {
        await ExecuteAsync(connection, $"""
            CREATE TABLE `{characters}`.atlas_armory_combat_snapshot (guid INT UNSIGNED PRIMARY KEY, snapshot JSON NOT NULL);
            """);
        long observedMilliseconds = Convert.ToInt64(await ScalarAsync(connection,
            "SELECT TIMESTAMPDIFF(MICROSECOND,'1970-01-01 00:00:00',UTC_TIMESTAMP(6)) DIV 1000;"), CultureInfo.InvariantCulture);
        JsonObject valid = NativeSnapshot(observedMilliseconds - 2000);
        await SaveSnapshotAsync(connection, characters, 101, valid);
        LauncherDatabase database = CreateDatabase(options);
        JsonElement roster = await RosterAsync(database, 1);
        AssertOwnedRoster(roster);
        JsonElement snapshot = CharacterRow(roster, 101).GetProperty("snapshot");
        Require(snapshot.ValueKind == JsonValueKind.Object && snapshot.GetProperty("character").GetProperty("guid").GetUInt32() == 101
            && snapshot.GetProperty("equipment")[0].GetProperty("itemId").GetUInt32() == 1003,
            "A complete native snapshot belonging to the selected character must be preserved.");
        AssertCatalogIds(await CatalogAsync(database, 1, 101), 1001, 1002, 1003);

        List<(string Name, JsonNode Value)> rejected = [("non-object JSON", JsonValue.Create("not a snapshot")!)];
        AddRejected("malformed schema", value => value["schemaVersion"] = "invalid");
        AddRejected("foreign GUID", value => value["character"]!["guid"] = 201);
        AddRejected("foreign name", value => value["character"]!["name"] = "Othermage");
        AddRejected("missing combat fields", value => value.Remove("values"));
        AddRejected("unknown capture reason", value => value["reason"] = "untrusted");
        AddRejected("future capture", value => value["capturedAtMs"] = observedMilliseconds + 3_600_000);
        AddRejected("capture before logout", value => value["capturedAtMs"] = 1_599_999_999_000L);
        AddRejected("invalid enchantment triplets", value => value["equipment"]![0]!["enchantments"] = "0 0");
        AddRejected("duplicate equipment slot", value => value["equipment"]!.AsArray().Add(value["equipment"]![0]!.DeepClone()));
        AddRejected("oversized capture", value => value["unused"] = new string('x', 33000));
        foreach ((string name, JsonNode value) in rejected)
        {
            await SaveSnapshotAsync(connection, characters, 101, value);
            JsonElement result = await RosterAsync(database, 1);
            AssertOwnedRoster(result);
            Require(CharacterRow(result, 101).GetProperty("snapshot").ValueKind == JsonValueKind.Null,
                $"The {name} snapshot must be excluded without hiding the account roster.");
            AssertCatalogIds(await CatalogAsync(database, 1, 101), 1001, 1002);
        }

        JsonObject extraFields = valid.DeepClone().AsObject();
        extraFields["privateUnrecognizedData"] = "must never leave the server";
        extraFields["character"]!["unrecognizedCharacterData"] = "must never leave the server";
        await SaveSnapshotAsync(connection, characters, 101, extraFields);
        JsonElement sanitized = CharacterRow(await RosterAsync(database, 1), 101).GetProperty("snapshot");
        Require(!sanitized.TryGetProperty("privateUnrecognizedData", out _)
            && !sanitized.GetProperty("character").TryGetProperty("unrecognizedCharacterData", out _),
            "Unrecognized data must be excluded from a valid native capture.");

        // Restore a valid optional capture before independently removing its neighboring statistics table.
        await SaveSnapshotAsync(connection, characters, 101, valid);
        return;

        void AddRejected(string name, Action<JsonObject> mutate)
        {
            JsonObject copy = valid.DeepClone().AsObject();
            mutate(copy);
            rejected.Add((name, copy));
        }
    }

    private static JsonObject NativeSnapshot(long capturedAt)
    {
        string[] fields = ["strength", "agility", "stamina", "intellect", "spirit", "armor", "maxHealth", "maxMana",
            "attackPower", "rangedAttackPower", "meleeCritPct", "rangedCritPct", "meleeHitPct", "rangedHitPct", "spellHitPct",
            "meleeHastePct", "rangedHastePct", "spellHastePct", "expertise", "healingPower", "manaRegenCasting", "defenseSkill",
            "dodgePct", "parryPct", "blockPct", "resilience"];
        Dictionary<string, int> values = fields.ToDictionary(field => field, _ => 0);
        values["maxHealth"] = 1;
        return JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, source = "atlas-armory-engine", reason = "periodic", capturedAtMs = capturedAt,
            character = new { guid = 101, name = "Ownmage", race = 1, classId = 8, gender = 0, level = 20,
                skin = 0, face = 0, hairStyle = 0, hairColor = 0, facialStyle = 0 },
            equipment = new[] { new { slot = 17, itemId = 1003, displayId = 2003, quality = 2, itemLevel = 40,
                randomPropertyId = 0, enchantments = EmptyEnchantments } },
            values,
            schools = Enumerable.Range(1, 6).Select(id => new { id, spellPower = 0, spellCritPct = 0 }).ToArray(),
            talentPoints = new[] { 0, 0, 0 }, form = 0, includesTemporaryEffects = true
        }, WebJson)!.AsObject();
    }

    private static Task SaveSnapshotAsync(MySqlConnection connection, string characters, uint guid, JsonNode snapshot) =>
        ExecuteAsync(connection, $"""
            INSERT INTO `{characters}`.atlas_armory_combat_snapshot(guid,snapshot) VALUES (@guid,@snapshot)
            ON DUPLICATE KEY UPDATE snapshot=@snapshot;
            """, ("@guid", guid), ("@snapshot", snapshot.ToJsonString(WebJson)));

    private static async Task ValidateCharacterLimitAsync(LauncherDatabase database, MySqlConnection connection, string characters)
    {
        for (uint index = 0; index < 55; index++)
            await ExecuteAsync(connection, $"INSERT INTO `{characters}`.characters(guid,account,name) VALUES (@guid,3,@name);",
                ("@guid", 3000 + index), ("@name", $"Limit{index}"));
        JsonElement roster = await RosterAsync(database, 3);
        JsonElement[] rows = roster.GetProperty("characters").EnumerateArray().ToArray();
        Require(rows.Length == 50 && rows.All(row => row.GetProperty("character").GetProperty("guid").GetUInt32() is >= 3000 and < 3055),
            "The roster must be bounded to fifty characters from the requested account.");
    }

    private static async Task ValidateRemovedOptionalTablesAsync(LauncherServerOptions options, MySqlConnection connection, string characters)
    {
        await ExecuteAsync(connection, $"DROP TABLE `{characters}`.character_stats;");
        LauncherDatabase database = CreateDatabase(options);
        JsonElement roster = await RosterAsync(database, 1);
        AssertOwnedRoster(roster);
        Require(CharacterRow(roster, 101).GetProperty("values").ValueKind == JsonValueKind.Null
            && CharacterRow(roster, 101).GetProperty("snapshot").ValueKind == JsonValueKind.Object,
            "A native snapshot must remain available independently of the optional saved-statistics table.");
        AssertCatalogIds(await CatalogAsync(database, 1, 101), 1001, 1002, 1003);
        await ExecuteAsync(connection, $"DROP TABLE `{characters}`.atlas_armory_combat_snapshot;");
        await ValidateWithoutOptionalTablesAsync(CreateDatabase(options));
    }

    private static async Task ValidateHttpAsync(LauncherServerOptions options, MySqlConnection connection)
    {
        const string firstToken = "disposable-armory-account-one";
        const string secondToken = "disposable-armory-account-two";
        const string revokedToken = "disposable-armory-revoked";
        const string expiredToken = "disposable-armory-expired";
        await ExecuteAsync(connection, """
            CREATE TABLE atlas_launcher_profile (account_id INT UNSIGNED PRIMARY KEY);
            INSERT INTO atlas_launcher_profile VALUES (1),(2);
            CREATE TABLE atlas_launcher_session (
                access_hash BINARY(32) PRIMARY KEY, account_id INT UNSIGNED NOT NULL,
                access_expires_at DATETIME NOT NULL, revoked_at DATETIME NULL,
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP);
            INSERT INTO atlas_launcher_session(access_hash,account_id,access_expires_at,revoked_at)
            VALUES (@first,1,UTC_TIMESTAMP()+INTERVAL 1 HOUR,NULL),
                   (@second,2,UTC_TIMESTAMP()+INTERVAL 1 HOUR,NULL),
                   (@revoked,1,UTC_TIMESTAMP()+INTERVAL 1 HOUR,UTC_TIMESTAMP()),
                   (@expired,1,UTC_TIMESTAMP()-INTERVAL 1 HOUR,NULL);
            """, ("@first", TokenService.Hash(firstToken)), ("@second", TokenService.Hash(secondToken)),
            ("@revoked", TokenService.Hash(revokedToken)), ("@expired", TokenService.Hash(expiredToken)));

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(CreateDatabase(options));
        builder.Services.AddSingleton<ArmoryReadLimiter>();
        await using WebApplication app = builder.Build();
        app.MapArmoryEndpoints();
        // These two routes exist only inside this disposable test application.
        app.MapGet("/fixture/within-limit", () => ArmoryEndpoints.CreateJsonResult(new { payload = "ok" }));
        app.MapGet("/fixture/over-limit", () => ArmoryEndpoints.CreateJsonResult(new { payload = new string('x', ArmoryEndpoints.MaximumResponseBytes) }));
        await app.StartAsync();
        try
        {
            using HttpClient client = new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(20) };
            foreach (string path in new[] { "/api/v1/armory/characters", "/api/v1/armory/characters/101/catalog" })
                await AssertStatusAsync(path, null, HttpStatusCode.Unauthorized);
            foreach (string token in new[] { "disposable-invalid-token", revokedToken, expiredToken })
                await AssertStatusAsync("/api/v1/armory/characters", token, HttpStatusCode.Unauthorized);
            foreach (string query in new[] { "accountId=2", "itemId=1008", "sql=SELECT+1" })
                await AssertStatusAsync("/api/v1/armory/characters?" + query, firstToken, HttpStatusCode.BadRequest);

            using (HttpResponseMessage response = await GetAsync("/api/v1/armory/characters", firstToken))
            {
                Require(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true
                    && response.Content.Headers.ContentType?.MediaType == "application/json", "Authenticated roster must be JSON with no-store.");
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertOwnedRoster(document.RootElement);
            }
            using (HttpResponseMessage response = await GetAsync("/api/v1/armory/characters/101/catalog", firstToken))
            {
                Require(response.StatusCode == HttpStatusCode.OK, "The owned catalog route must succeed.");
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertCatalogIds(document.RootElement, 1001, 1002);
            }
            foreach (string guid in new[] { "201", "9999", "0", "-1", "4294967296" })
                await AssertStatusAsync($"/api/v1/armory/characters/{guid}/catalog", firstToken, HttpStatusCode.NotFound);
            await AssertStatusAsync("/api/v1/armory/characters/101/catalog", secondToken, HttpStatusCode.NotFound);
            await AssertStatusAsync("/api/v1/armory/characters/102/catalog", firstToken, HttpStatusCode.OK);
            await AssertStatusAsync("/fixture/within-limit", null, HttpStatusCode.OK);
            await AssertStatusAsync("/fixture/over-limit", null, HttpStatusCode.ServiceUnavailable);

            ArmoryReadLimiter limiter = app.Services.GetRequiredService<ArmoryReadLimiter>();
            for (int attempt = 0; attempt < 120; attempt++) using (limiter.Acquire(1)) { }
            using (HttpResponseMessage limited = await GetAsync("/api/v1/armory/characters", firstToken))
                Require(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.RetryAfter?.Delta == TimeSpan.FromSeconds(60),
                    "Exhausting one account's read budget must produce 429 with Retry-After.");
            await AssertStatusAsync("/api/v1/armory/characters", secondToken, HttpStatusCode.OK);
            return;

            async Task<HttpResponseMessage> GetAsync(string path, string? token)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, path);
                if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return await client.SendAsync(request);
            }

            async Task AssertStatusAsync(string path, string? token, HttpStatusCode expected)
            {
                using HttpResponseMessage response = await GetAsync(path, token);
                Require(response.StatusCode == expected, $"Armory fixture HTTP status for {path}: expected {(int)expected}, got {(int)response.StatusCode}.");
            }
        }
        finally { await app.StopAsync(); }
    }

    private static async Task<JsonElement> RosterAsync(LauncherDatabase database, uint account) =>
        JsonSerializer.SerializeToElement(await database.ListArmoryCharactersAsync(account, CancellationToken.None), WebJson);

    private static async Task<JsonElement> CatalogAsync(LauncherDatabase database, uint account, uint guid)
    {
        var catalog = await database.GetArmoryCatalogAsync(account, guid, CancellationToken.None);
        Require(catalog is not null, "An owned character must return a catalog envelope.");
        JsonElement result = JsonSerializer.SerializeToElement(catalog, WebJson);
        Require(result.GetProperty("items").ValueKind == JsonValueKind.Array, "Catalog items must always be an array.");
        AssertSqlUtc(result.GetProperty("capturedAtUtc"), "Catalog capture time");
        return result;
    }

    private static void AssertOwnedRoster(JsonElement roster)
    {
        AssertSqlUtc(roster.GetProperty("observedAtUtc"), "Roster observation time");
        Require(roster.GetProperty("characters").GetArrayLength() == 2, "The first account must contain exactly its two characters.");
        JsonElement equipped = CharacterRow(roster, 101);
        JsonElement character = equipped.GetProperty("character");
        foreach (string field in new[] { "guid", "race", "classId", "gender", "level", "skin", "face", "hairStyle", "hairColor", "facialStyle", "online", "zoneId", "lastLogout" })
            Require(character.GetProperty(field).ValueKind == JsonValueKind.Number, $"Character field {field} must use the numeric roster contract.");
        Require(character.GetProperty("name").GetString() == "Ownmage" && character.GetProperty("online").GetInt32() == 0,
            "Character identity and integer presence must be preserved.");
        JsonElement[] items = equipped.GetProperty("equipment").EnumerateArray().ToArray();
        Require(items.Length == 3 && items.Select(item => item.GetProperty("slot").GetInt32()).Order().SequenceEqual(new[] { 0, 2, 15 }),
            "Only equipped items owned by this character are allowed; bags, bank and mismatched owners are excluded.");
        JsonElement[] duplicates = items.Where(item => item.GetProperty("itemId").GetUInt32() == 1001).OrderBy(item => item.GetProperty("slot").GetInt32()).ToArray();
        Require(duplicates.Length == 2 && duplicates[0].GetProperty("randomPropertyId").GetInt32() == 0
            && duplicates[1].GetProperty("randomPropertyId").GetInt32() == 7
            && duplicates[1].GetProperty("enchantments").GetString() == InstanceEnchantments,
            "Two instances of the same template must preserve their slots and distinct instance properties.");
        foreach (JsonElement item in items)
        {
            foreach (string field in new[] { "slot", "itemId", "displayId", "quality", "inventoryType", "itemLevel", "randomPropertyId" })
                Require(item.GetProperty(field).ValueKind == JsonValueKind.Number, $"Equipment field {field} must be numeric.");
            Require(item.GetProperty("name").ValueKind == JsonValueKind.String && item.TryGetProperty("nameFr", out _)
                && item.GetProperty("enchantments").ValueKind == JsonValueKind.String,
                "The equipment row must expose the raw normalizeRoster-compatible names and enchantment string.");
        }
        Require(CharacterRow(roster, 102).GetProperty("equipment").GetArrayLength() == 0,
            "An owned naked character must remain present with an empty equipment array.");
    }

    private static JsonElement CharacterRow(JsonElement roster, uint guid) => roster.GetProperty("characters").EnumerateArray()
        .Single(row => row.GetProperty("character").GetProperty("guid").GetUInt32() == guid);

    private static void AssertCatalogIds(JsonElement catalog, params uint[] expected)
    {
        uint[] actual = catalog.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("itemId").GetUInt32()).Order().ToArray();
        Require(actual.SequenceEqual(expected.Order()), "The catalog must contain exactly the unique current and valid native snapshot item IDs, without other accounts, bags, bank, wrong owners or arbitrary templates.");
    }

    private static void AssertCatalogProjection(JsonElement catalog)
    {
        JsonElement item = catalog.GetProperty("items").EnumerateArray().Single(value => value.GetProperty("itemId").GetUInt32() == 1001);
        foreach (string field in new[] { "displayId", "quality", "itemLevel", "classId", "subclassId", "inventoryType", "requiredLevel", "armor", "block", "bonding", "maxDurability", "delay", "socketBonus", "scalingDistribution", "scalingValue" })
            Require(item.GetProperty(field).ValueKind == JsonValueKind.Number, $"Catalog field {field} must be numeric.");
        Require(item.GetProperty("name").GetProperty("fr").GetString() == "Objet équipé de test"
            && item.GetProperty("name").GetProperty("en").GetString() == "Fixture item 1001"
            && item.GetProperty("description").GetProperty("fr").GetString() == "Description française de test",
            "Localized template names/descriptions must preserve French and English independently.");
        Require(item.GetProperty("stats").GetArrayLength() == 10 && item.GetProperty("stats")[0][0].GetInt32() == 7
            && item.GetProperty("stats")[0][1].GetInt32() == 4, "Catalog stats must retain the ten [type,value] pairs.");
        Require(item.GetProperty("damage").GetArrayLength() == 2 && item.GetProperty("damage")[0].GetProperty("min").GetDecimal() == 1.25m
            && item.GetProperty("damage")[0].GetProperty("max").GetDecimal() == 2.5m
            && item.GetProperty("damage")[0].GetProperty("school").GetInt32() == 2, "Damage ranges and schools must retain their numeric projection.");
        Require(item.GetProperty("resistances").GetArrayLength() == 6 && item.GetProperty("resistances")[0].GetInt32() == 7,
            "Resistance ordering must start with holy and include all six schools.");
        Require(item.GetProperty("spells").GetArrayLength() == 5 && item.GetProperty("spells")[0][0].GetInt32() == 9001
            && item.GetProperty("spells")[0][1].GetInt32() == 1, "Catalog spells must retain five [id,trigger] pairs without inventing descriptions.");
        Require(item.GetProperty("sockets").GetArrayLength() == 3 && item.GetProperty("sockets")[0].GetInt32() == 2,
            "Catalog sockets must retain all three color slots.");
    }

    private static void AssertSqlUtc(JsonElement value, string description) =>
        Require(value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString() ?? "",
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,6})?$", RegexOptions.CultureInvariant),
            description + " must be a SQL-style UTC timestamp accepted by normalizeRoster.");

    private static async Task ExecuteAsync(MySqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(MySqlConnection connection, string sql)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 15;
        return await command.ExecuteScalarAsync();
    }

    private static void Require(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
