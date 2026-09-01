using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Avatars;
using WotLK.Launcher.Server.Database;

internal static partial class AvatarBackendTests
{
    private const string IdentityConnectionVariable = "ATLAS_IDENTITY_TEST_DB";
    private const string IdentityPassword = "Atlas-identity-test-2026";

    internal static async Task<int> RunIdentityMySqlAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable(IdentityConnectionVariable)
            ?? throw new InvalidOperationException(
                $"{IdentityConnectionVariable} doit viser une copie MySQL jetable de production.");
        MySqlConnectionStringBuilder connectionBuilder = new(connectionString);
        if (!connectionBuilder.Database.StartsWith("atlas_identity_test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Le test refuse toute base qui ne porte pas le prefixe atlas_identity_test_.");
        }

        LauncherServerOptions options = new()
        {
            ConnectionString = connectionBuilder.ConnectionString,
            CharacterDatabaseName = Environment.GetEnvironmentVariable("ATLAS_IDENTITY_CHARACTER_DB")
                ?? "arthas_chars",
            AvatarMediaRoot = NewTemporaryRoot()
        };

        try
        {
            await using MySqlConnection connection = new(options.ConnectionString);
            await connection.OpenAsync();
            string mysqlVersion = Convert.ToString(
                    await IdentityScalarAsync(connection, "SELECT VERSION()"),
                    CultureInfo.InvariantCulture)
                ?? string.Empty;
            True(mysqlVersion.StartsWith("8.4.", StringComparison.Ordinal), "MySQL 8.4 est obligatoire.");

            IdentitySnapshot before = await CaptureIdentitySnapshotAsync(connection);
            ValidateExpectedProductionFixture(before);
            await ValidateHistoryAsync(connection, [1U, 2U, 3U]);

            LauncherSchemaMigrator migrator = new(options);
            IReadOnlyList<LauncherSchemaMigrationOutcome> firstRun = await migrator.MigrateAsync();
            Equal(4, firstRun.Count, "Les quatre migrations doivent etre connues.");
            True(
                firstRun.Take(3).All(item => item.State == LauncherSchemaMigrationState.AlreadyApplied)
                && firstRun[3].Version == 4
                && firstRun[3].State == LauncherSchemaMigrationState.Applied,
                "La copie 0001-0003 doit appliquer uniquement 0004.");

            IdentitySnapshot after = await CaptureIdentitySnapshotAsync(connection);
            AssertIdentitySnapshotUnchanged(before, after);
            await ValidateHistoryAsync(connection, [1U, 2U, 3U, 4U]);
            await ValidateProfileScopedForeignKeysAsync(connection);
            await ValidateNoIdentityOrphansAsync(connection);

            IReadOnlyList<LauncherSchemaMigrationOutcome> secondRun = await migrator.MigrateAsync();
            True(
                secondRun.Count == 4
                && secondRun.All(item => item.State == LauncherSchemaMigrationState.AlreadyApplied),
                "Une seconde execution doit etre strictement idempotente.");

            await ValidateLoginBoundariesAsync(options);
            await ValidateRegistrationAtomicityAsync(options);

            Console.WriteLine(
                $"Atlas identity MySQL 8.4 OK: accounts={before.AccountCount}, "
                + $"rndbot={before.RndbotCount}, profiles={before.ProfileCount}, "
                + $"missing-profile-ids=[{string.Join(',', before.NormalAccountIdsWithoutProfile)}].");
            return 0;
        }
        finally
        {
            TryDeleteDirectory(options.AvatarMediaRoot);
        }
    }

    private static async Task ValidateLoginBoundariesAsync(LauncherServerOptions options)
    {
        LauncherDatabase database = CreateDatabase(options);
        string playerUsername = IdentityUsername("PLAYERONLY");
        string technicalUsername = IdentityUsername("RNDBOT");
        uint playerAccountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            playerUsername,
            IdentityPassword);
        uint technicalAccountId = await InsertIdentityAzerothAccountAsync(
            options.ConnectionString,
            technicalUsername,
            IdentityPassword);

        AtlasLoginResult playerLogin = await database.LoginAsync(
            new LoginRequest(playerUsername, IdentityPassword, "identity-player"),
            CancellationToken.None);
        Equal(
            AtlasLoginOutcome.AtlasProfileRequired,
            playerLogin.Outcome,
            "Un joueur AzerothCore sans profil doit recevoir AtlasProfileRequired.");
        True(playerLogin.Response is null, "Le joueur sans profil ne doit recevoir aucun jeton.");

        AtlasLoginResult wrongPassword = await database.LoginAsync(
            new LoginRequest(playerUsername, IdentityPassword + "-wrong", "identity-player"),
            CancellationToken.None);
        Equal(
            AtlasLoginOutcome.InvalidCredentials,
            wrongPassword.Outcome,
            "Un mauvais mot de passe doit rester indistinguable d'un compte absent.");

        AtlasLoginResult technicalLogin = await database.LoginAsync(
            new LoginRequest(technicalUsername, IdentityPassword, "identity-technical"),
            CancellationToken.None);
        Equal(
            AtlasLoginOutcome.AtlasProfileRequired,
            technicalLogin.Outcome,
            "La frontiere technique doit etre identique sans creer de profil.");

        await using (MySqlConnection connection = new(options.ConnectionString))
        {
            await connection.OpenAsync();
            Equal(
                0L,
                await IdentityCountAsync(
                    connection,
                    "SELECT COUNT(*) FROM atlas_launcher_profile WHERE account_id IN (@playerId, @technicalId)",
                    ("@playerId", playerAccountId),
                    ("@technicalId", technicalAccountId)),
                "Un login ne doit creer aucun profil Atlas.");
            Equal(
                0L,
                await IdentityCountAsync(
                    connection,
                    "SELECT COUNT(*) FROM atlas_launcher_session WHERE account_id IN (@playerId, @technicalId)",
                    ("@playerId", playerAccountId),
                    ("@technicalId", technicalAccountId)),
                "Un compte sans profil ne doit obtenir aucune session.");

            await ExpectIdentityForeignKeyFailureAsync(connection, playerAccountId);
        }

        AvatarRepository avatars = new(options);
        await ExpectAsync<InvalidOperationException>(
            () => avatars.TryConsumeUploadPermitAsync(playerAccountId, CancellationToken.None),
            "Un joueur sans profil ne doit pas obtenir de quota avatar.");
        await ExpectAsync<InvalidOperationException>(
            () => avatars.CreatePendingAsync(technicalAccountId, CancellationToken.None),
            "Un compte technique sans profil ne doit pas creer d'avatar.");

        AuthResponse atlas = await RegisterIdentityAccountAsync(database, "ATLASOWNER");
        FriendRequestResult hiddenPlayer = await database.SendFriendRequestAsync(
            atlas.Profile.AccountId,
            playerUsername,
            CancellationToken.None);
        Equal(
            FriendRequestOutcome.NotFound,
            hiddenPlayer.Outcome,
            "Un compte AzerothCore sans profil ne doit pas etre trouvable socialement.");

        await ValidateRegistrationCapabilitiesAsync(options, database, atlas);
        await ValidateLoginHttpContractAsync(database, playerUsername, atlas.Profile.Username);
    }

    private static async Task ValidateRegistrationCapabilitiesAsync(
        LauncherServerOptions options,
        LauncherDatabase database,
        AuthResponse registered)
    {
        await using (MySqlConnection connection = new(options.ConnectionString))
        {
            await connection.OpenAsync();
            Equal(
                1L,
                await IdentityCountAsync(
                    connection,
                    "SELECT COUNT(*) FROM account WHERE id = @accountId",
                    ("@accountId", registered.Profile.AccountId)),
                "L'inscription doit creer le compte AzerothCore.");
            Equal(
                1L,
                await IdentityCountAsync(
                    connection,
                    "SELECT COUNT(*) FROM atlas_launcher_profile WHERE account_id = @accountId",
                    ("@accountId", registered.Profile.AccountId)),
                "L'inscription doit creer le profil Atlas dans la meme transaction.");
            Equal(
                1L,
                await IdentityCountAsync(
                    connection,
                    "SELECT COUNT(*) FROM atlas_launcher_session WHERE account_id = @accountId",
                    ("@accountId", registered.Profile.AccountId)),
                "L'inscription doit creer sa session Atlas initiale.");
        }

        AuthenticatedAccount? authenticated = await database.AuthenticateAsync(
            registered.AccessToken,
            CancellationToken.None);
        True(authenticated?.AccountId == registered.Profile.AccountId, "La session d'inscription doit etre utilisable.");

        AuthResponse? refreshed = await database.RefreshAsync(
            registered.RefreshToken,
            CancellationToken.None);
        True(refreshed?.Profile.AccountId == registered.Profile.AccountId, "La session Atlas doit pouvoir etre renouvelee.");

        AtlasLoginResult login = await database.LoginAsync(
            new LoginRequest(registered.Profile.Username, IdentityPassword, "identity-login"),
            CancellationToken.None);
        Equal(AtlasLoginOutcome.Succeeded, login.Outcome, "Une nouvelle connexion apres inscription doit reussir.");

        AvatarRepository avatars = new(options);
        AvatarRateLimitDecision permit = await avatars.TryConsumeUploadPermitAsync(
            registered.Profile.AccountId,
            CancellationToken.None);
        True(permit.Allowed, "Un profil inscrit doit avoir acces aux avatars.");
        AvatarAssetRecord pending = await avatars.CreatePendingAsync(
            registered.Profile.AccountId,
            CancellationToken.None);
        Equal(registered.Profile.AccountId, pending.OwnerAccountId, "L'avatar doit appartenir au profil Atlas.");

        AuthResponse friend = await RegisterIdentityAccountAsync(database, "ATLASFRIEND");
        FriendRequestResult request = await database.SendFriendRequestAsync(
            registered.Profile.AccountId,
            friend.Profile.Username,
            CancellationToken.None);
        Equal(FriendRequestOutcome.Requested, request.Outcome, "Les amis doivent etre disponibles apres inscription.");
        IReadOnlyList<WotLK.Launcher.Server.LauncherFriend> friends = await database.ListFriendsAsync(
            registered.Profile.AccountId,
            CancellationToken.None);
        True(
            friends.Any(item => item.AccountId == friend.Profile.AccountId),
            "Le profil inscrit doit apparaitre dans la liste d'amis Atlas.");
    }

    private static async Task ValidateRegistrationAtomicityAsync(LauncherServerOptions options)
    {
        LauncherDatabase database = CreateDatabase(options);
        string username = IdentityUsername("ATOMICFAIL");
        string triggerName = "atlas_identity_registration_failure";
        await using MySqlConnection connection = new(options.ConnectionString);
        await connection.OpenAsync();
        await using (MySqlCommand trigger = connection.CreateCommand())
        {
            trigger.CommandText = $"""
                DROP TRIGGER IF EXISTS `{triggerName}`;
                CREATE TRIGGER `{triggerName}`
                BEFORE INSERT ON atlas_launcher_profile
                FOR EACH ROW
                BEGIN
                    IF NEW.display_username = '{username}' THEN
                        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'identity atomicity test';
                    END IF;
                END;
                """;
            await trigger.ExecuteNonQueryAsync();
        }

        try
        {
            await ExpectAsync<MySqlException>(
                () => database.RegisterAsync(
                    new RegisterRequest(username, $"{username.ToLowerInvariant()}@example.test", IdentityPassword),
                    "identity-atomicity",
                    CancellationToken.None),
                "Une panne pendant la creation du profil doit faire echouer l'inscription.");
        }
        finally
        {
            await using MySqlCommand drop = connection.CreateCommand();
            drop.CommandText = $"DROP TRIGGER IF EXISTS `{triggerName}`";
            await drop.ExecuteNonQueryAsync();
        }

        Equal(
            0L,
            await IdentityCountAsync(
                connection,
                "SELECT COUNT(*) FROM account WHERE BINARY username = BINARY @username",
                ("@username", username.ToUpperInvariant())),
            "L'echec du profil doit annuler la creation du compte AzerothCore.");
        Equal(
            0L,
            await IdentityCountAsync(
                connection,
                "SELECT COUNT(*) FROM hermes_bnet_credentials WHERE BINARY username = BINARY @username",
                ("@username", username.ToUpperInvariant())),
            "L'echec doit aussi annuler les identifiants modernes.");
    }

    private static async Task ValidateLoginHttpContractAsync(
        LauncherDatabase database,
        string noProfileUsername,
        string atlasUsername)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(AvatarBackendTests).Assembly.FullName
        });
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(database);
        WebApplication app = builder.Build();
        app.MapPost("/api/v1/auth/login", async (
            LoginRequest request,
            LauncherDatabase db,
            CancellationToken cancellationToken) =>
            AuthenticationEndpointResults.FromLogin(
                await db.LoginAsync(request, cancellationToken)));

        await app.StartAsync();
        try
        {
            IServer server = app.Services.GetRequiredService<IServer>();
            string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
                ?? throw new InvalidOperationException("Adresse HTTP de test introuvable.");
            using HttpClient client = new() { BaseAddress = new Uri(address) };

            using HttpResponseMessage boundary = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(noProfileUsername, IdentityPassword, "identity-http"));
            Equal(HttpStatusCode.Forbidden, boundary.StatusCode, "Le compte sans profil doit recevoir 403.");
            AtlasAuthErrorResponse? error = await boundary.Content.ReadFromJsonAsync<AtlasAuthErrorResponse>();
            Equal(AtlasAuthErrorCodes.ProfileRequired, error?.Code, "Le code d'erreur Atlas doit etre stable.");
            Equal(AtlasAuthErrorCodes.ProfileRequiredMessage, error?.Error, "Le message doit rester non technique.");

            using HttpResponseMessage invalid = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(noProfileUsername, IdentityPassword + "-wrong", "identity-http"));
            Equal(HttpStatusCode.Unauthorized, invalid.StatusCode, "Les mauvais identifiants doivent rester en 401.");

            using HttpResponseMessage success = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new LoginRequest(atlasUsername, IdentityPassword, "identity-http"));
            Equal(HttpStatusCode.OK, success.StatusCode, "Le profil Atlas existant doit se connecter.");
            LauncherAuthSession? legacyContract =
                await success.Content.ReadFromJsonAsync<LauncherAuthSession>();
            True(
                legacyContract is not null
                && legacyContract.Profile.Username == atlasUsername,
                "Le contrat JSON doit rester lisible par le launcher legacy publie.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<IdentitySnapshot> CaptureIdentitySnapshotAsync(MySqlConnection connection)
    {
        Dictionary<string, TableFingerprint> tables = new(StringComparer.Ordinal);
        foreach ((string table, string orderBy) in IdentityTables)
            tables.Add(table, await FingerprintTableAsync(connection, table, orderBy));

        List<uint> missing = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT a.id
                FROM account a
                LEFT JOIN atlas_launcher_profile p ON p.account_id = a.id
                WHERE p.account_id IS NULL
                  AND LOWER(a.username) NOT LIKE 'rndbot%'
                ORDER BY a.id
                """;
            await using MySqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                missing.Add(reader.GetUInt32(0));
        }

        return new IdentitySnapshot(
            await IdentityCountAsync(connection, "SELECT COUNT(*) FROM account"),
            await IdentityCountAsync(connection, "SELECT COUNT(*) FROM account WHERE LOWER(username) LIKE 'rndbot%'"),
            await IdentityCountAsync(connection, "SELECT COUNT(*) FROM account WHERE LOWER(username) NOT LIKE 'rndbot%'"),
            await IdentityCountAsync(connection, "SELECT COUNT(*) FROM atlas_launcher_profile"),
            await IdentityCountAsync(connection, """
                SELECT COUNT(*) FROM account a
                INNER JOIN atlas_launcher_profile p ON p.account_id = a.id
                WHERE LOWER(a.username) LIKE 'rndbot%'
                """),
            missing,
            tables);
    }

    private static void ValidateExpectedProductionFixture(IdentitySnapshot snapshot)
    {
        True(snapshot.AccountCount > 0, "La copie doit contenir les comptes AzerothCore.");
        True(snapshot.RndbotCount > 0, "La copie doit contenir un cas rndbot reel.");
        True(snapshot.NormalAccountCount > 0, "La copie doit contenir des comptes joueurs.");
        True(snapshot.ProfileCount > 0, "La copie doit contenir des profils Atlas existants.");
        Equal(0L, snapshot.RndbotWithProfileCount, "Aucun rndbot ne doit posseder de profil Atlas.");

        AssertExpectedCount("ATLAS_IDENTITY_EXPECTED_ACCOUNTS", snapshot.AccountCount);
        AssertExpectedCount("ATLAS_IDENTITY_EXPECTED_RNDBOTS", snapshot.RndbotCount);
        AssertExpectedCount("ATLAS_IDENTITY_EXPECTED_NORMAL_ACCOUNTS", snapshot.NormalAccountCount);
        AssertExpectedCount("ATLAS_IDENTITY_EXPECTED_PROFILES", snapshot.ProfileCount);

        string? expectedIds = Environment.GetEnvironmentVariable("ATLAS_IDENTITY_EXPECTED_MISSING_PROFILE_IDS");
        if (!string.IsNullOrWhiteSpace(expectedIds))
        {
            uint[] expected = expectedIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => uint.Parse(value, CultureInfo.InvariantCulture))
                .Order()
                .ToArray();
            True(
                expected.SequenceEqual(snapshot.NormalAccountIdsWithoutProfile),
                "Les IDs internes des comptes joueurs sans profil ont change.");
        }
    }

    private static void AssertIdentitySnapshotUnchanged(
        IdentitySnapshot before,
        IdentitySnapshot after)
    {
        Equal(before.AccountCount, after.AccountCount, "0004 ne doit supprimer aucun compte.");
        Equal(before.RndbotCount, after.RndbotCount, "0004 doit conserver tous les rndbot.");
        Equal(before.NormalAccountCount, after.NormalAccountCount, "0004 doit conserver tous les joueurs.");
        Equal(before.ProfileCount, after.ProfileCount, "0004 ne doit creer ni supprimer de profil.");
        Equal(before.RndbotWithProfileCount, after.RndbotWithProfileCount, "0004 ne doit profiler aucun rndbot.");
        True(
            before.NormalAccountIdsWithoutProfile.SequenceEqual(after.NormalAccountIdsWithoutProfile),
            "0004 ne doit modifier aucun compte joueur sans profil.");

        foreach ((string table, TableFingerprint expected) in before.Tables)
        {
            TableFingerprint actual = after.Tables[table];
            Equal(expected.RowCount, actual.RowCount, $"0004 a modifie le nombre de lignes de {table}.");
            Equal(expected.Sha256, actual.Sha256, $"0004 a modifie les donnees de {table}.");
        }
    }

    private static async Task<TableFingerprint> FingerprintTableAsync(
        MySqlConnection connection,
        string table,
        string orderBy)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM `{table}` ORDER BY {orderBy}";
        await using MySqlDataReader reader = await command.ExecuteReaderAsync();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        while (await reader.ReadAsync())
        {
            count++;
            for (int index = 0; index < reader.FieldCount; index++)
            {
                AppendIdentityHash(hash, reader.GetName(index));
                object value = reader.GetValue(index);
                AppendIdentityHash(hash, value switch
                {
                    DBNull => "<null>",
                    byte[] bytes => Convert.ToHexString(bytes),
                    DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
                });
            }
        }
        return new TableFingerprint(count, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void AppendIdentityHash(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static async Task ValidateHistoryAsync(
        MySqlConnection connection,
        IReadOnlyList<uint> expected)
    {
        List<uint> actual = [];
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM atlas_launcher_schema_history ORDER BY version";
        await using MySqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            actual.Add(reader.GetUInt32(0));
        True(expected.SequenceEqual(actual), "L'historique des migrations Atlas est incorrect.");
    }

    private static async Task ValidateProfileScopedForeignKeysAsync(MySqlConnection connection)
    {
        string[] constraints =
        [
            "fk_atlas_session_account",
            "fk_atlas_email_account",
            "fk_atlas_friend_low",
            "fk_atlas_friend_high",
            "fk_atlas_friend_requester",
            "fk_atlas_avatar_owner",
            "fk_atlas_avatar_upload_account"
        ];
        foreach (string constraint in constraints)
        {
            await using MySqlCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT referenced_table_name, referenced_column_name
                FROM information_schema.key_column_usage
                WHERE constraint_schema = DATABASE()
                  AND constraint_name = @constraint
                LIMIT 1
                """;
            command.Parameters.AddWithValue("@constraint", constraint);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync();
            True(await reader.ReadAsync(), $"La cle {constraint} doit exister.");
            Equal("atlas_launcher_profile", reader.GetString(0), $"{constraint} doit viser le profil Atlas.");
            Equal("account_id", reader.GetString(1), $"{constraint} doit viser account_id.");
        }
    }

    private static async Task ValidateNoIdentityOrphansAsync(MySqlConnection connection)
    {
        string[] queries =
        [
            "SELECT COUNT(*) FROM atlas_launcher_session s LEFT JOIN atlas_launcher_profile p ON p.account_id=s.account_id WHERE p.account_id IS NULL",
            "SELECT COUNT(*) FROM atlas_launcher_email_verification e LEFT JOIN atlas_launcher_profile p ON p.account_id=e.account_id WHERE p.account_id IS NULL",
            "SELECT COUNT(*) FROM atlas_launcher_friendship f LEFT JOIN atlas_launcher_profile p ON p.account_id=f.account_low_id WHERE p.account_id IS NULL",
            "SELECT COUNT(*) FROM atlas_launcher_friendship f LEFT JOIN atlas_launcher_profile p ON p.account_id=f.account_high_id WHERE p.account_id IS NULL",
            "SELECT COUNT(*) FROM atlas_launcher_friendship f LEFT JOIN atlas_launcher_profile p ON p.account_id=f.requested_by_id WHERE p.account_id IS NULL",
            "SELECT COUNT(*) FROM atlas_launcher_avatar_asset a LEFT JOIN atlas_launcher_profile p ON p.account_id=a.owner_account_id WHERE p.account_id IS NULL",
            "SELECT COUNT(*) FROM atlas_launcher_profile_avatar a LEFT JOIN atlas_launcher_profile p ON p.account_id=a.account_id WHERE p.account_id IS NULL",
            "SELECT COUNT(*) FROM atlas_launcher_avatar_upload_attempt a LEFT JOIN atlas_launcher_profile p ON p.account_id=a.account_id WHERE p.account_id IS NULL"
        ];
        foreach (string query in queries)
            Equal(0L, await IdentityCountAsync(connection, query), "Aucune reference Atlas orpheline n'est autorisee.");
    }

    private static async Task<uint> InsertIdentityAzerothAccountAsync(
        string connectionString,
        string username,
        string password)
    {
        string normalized = username.ToUpperInvariant();
        (byte[] salt, byte[] verifier) = SrpCredentials.MakeLegacy(normalized, password);
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO account
                (username, salt, verifier, email, reg_mail, joindate, expansion)
            VALUES
                (@username, @salt, @verifier, @email, @email, UTC_TIMESTAMP(), 2);
            SELECT LAST_INSERT_ID();
            """;
        command.Parameters.AddWithValue("@username", normalized);
        command.Parameters.Add("@salt", MySqlDbType.Binary, 32).Value = salt;
        command.Parameters.Add("@verifier", MySqlDbType.Binary, 32).Value = verifier;
        command.Parameters.AddWithValue("@email", $"{normalized.ToLowerInvariant()}@example.test");
        return Convert.ToUInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<AuthResponse> RegisterIdentityAccountAsync(
        LauncherDatabase database,
        string prefix)
    {
        string username = IdentityUsername(prefix);
        return await database.RegisterAsync(
            new RegisterRequest(username, $"{username.ToLowerInvariant()}@example.test", IdentityPassword),
            "identity-integration",
            CancellationToken.None);
    }

    private static string IdentityUsername(string prefix)
        => $"{prefix}{Guid.NewGuid():N}"[..Math.Min(20, prefix.Length + 10)];

    private static async Task ExpectIdentityForeignKeyFailureAsync(
        MySqlConnection connection,
        uint accountId)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO atlas_launcher_session
                (id, account_id, access_hash, refresh_hash, device_name,
                 access_expires_at, refresh_expires_at)
            VALUES
                (UUID_TO_BIN(UUID()), @accountId, RANDOM_BYTES(32), RANDOM_BYTES(32),
                 'identity-fk-test', UTC_TIMESTAMP() + INTERVAL 15 MINUTE,
                 UTC_TIMESTAMP() + INTERVAL 30 DAY)
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        try
        {
            await command.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Une session sans profil Atlas a ete acceptee.");
        }
        catch (MySqlException exception) when (exception.Number == 1452)
        {
        }
    }

    private static async Task<long> IdentityCountAsync(
        MySqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<object?> IdentityScalarAsync(MySqlConnection connection, string sql)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static void AssertExpectedCount(string variable, long actual)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(raw))
            return;
        Equal(long.Parse(raw, CultureInfo.InvariantCulture), actual, $"La fixture ne correspond pas a {variable}.");
    }

    private static readonly (string Table, string OrderBy)[] IdentityTables =
    [
        ("account", "id"),
        ("realmcharacters", "realmid, acctid"),
        ("hermes_bnet_credentials", "username"),
        ("atlas_launcher_profile", "account_id"),
        ("atlas_launcher_session", "id"),
        ("atlas_launcher_email_verification", "id"),
        ("atlas_launcher_friendship", "account_low_id, account_high_id"),
        ("atlas_launcher_avatar_asset", "id"),
        ("atlas_launcher_avatar_variant", "avatar_asset_id, size"),
        ("atlas_launcher_profile_avatar", "account_id"),
        ("atlas_launcher_avatar_upload_attempt", "id")
    ];

    private sealed record IdentitySnapshot(
        long AccountCount,
        long RndbotCount,
        long NormalAccountCount,
        long ProfileCount,
        long RndbotWithProfileCount,
        IReadOnlyList<uint> NormalAccountIdsWithoutProfile,
        IReadOnlyDictionary<string, TableFingerprint> Tables);

    private sealed record TableFingerprint(long RowCount, string Sha256);
}
