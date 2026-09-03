using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Avatars;
using WotLK.Launcher.Server.Database;

internal static class MigrationCeilingTests
{
    private const string TestConnectionVariable = "ATLAS_MIGRATION_CEILING_TEST_DB";

    internal static int Run()
    {
        Equal<uint?>(null, LauncherSchemaMigrationCeiling.Resolve(null, isProduction: false),
            "Le developpement peut suivre toutes les migrations lorsque la variable est absente.");
        Equal<uint?>(3, LauncherSchemaMigrationCeiling.Resolve("3", isProduction: true),
            "Le plafond de production attendu doit etre accepte.");

        ExpectConfigurationFailure(null, isProduction: true);
        foreach (string invalid in new[] { "", "0", "-1", "+3", "03", " 3", "3 ", "3.0", "4294967296" })
            ExpectConfigurationFailure(invalid, isProduction: true);

        IReadOnlyList<LauncherSchemaMigration> embedded = new EmbeddedLauncherSchemaMigrationSource().Load();
        Equal(4, embedded.Count, "Les quatre migrations doivent rester embarquees.");
        Equal((uint)4, embedded[^1].Version, "0004 doit rester la derniere migration versionnee.");
        Equal("atlas_profile_identity_boundary", embedded[^1].Name,
            "La migration differee ne doit pas etre remplacee.");

        Console.WriteLine(
            "Migration ceiling configuration OK: strict production value and embedded 0004 preserved.");
        return 0;
    }

    internal static async Task<int> RunMySqlAsync()
    {
        string connectionString = Environment.GetEnvironmentVariable(TestConnectionVariable)
            ?? throw new InvalidOperationException(
                $"{TestConnectionVariable} doit viser une base MySQL 8.4 jetable.");
        MySqlConnectionStringBuilder builder = new(connectionString);
        if (!builder.Database.StartsWith("atlas_migration_ceiling_test_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Le test refuse toute base qui ne porte pas le prefixe atlas_migration_ceiling_test_.");
        }

        await AssertMySql84Async(builder.ConnectionString);
        await ValidateCeilingThreeLifecycleAsync(builder);
        await ValidateHigherHistoryIsRejectedAsync(builder);
        await ValidateAppliedChecksumStillProtectedAsync(builder);

        Console.WriteLine(
            "Migration ceiling MySQL 8.4 OK: 0001-0003 only, idempotence, schema-3 social runtime, history and checksum guards.");
        return 0;
    }

    private static void ExpectConfigurationFailure(string? value, bool isProduction)
    {
        try
        {
            _ = LauncherSchemaMigrationCeiling.Resolve(value, isProduction);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"La configuration de plafond '{value ?? "<absente>"}' aurait du etre refusee.");
    }

    private static async Task ValidateCeilingThreeLifecycleAsync(MySqlConnectionStringBuilder builder)
    {
        await ResetFreshSchemaAsync(builder.ConnectionString);
        CapturingLogger<LauncherSchemaMigrator> logger = new();
        LauncherServerOptions options = CreateOptions(builder, maximumSchemaVersion: 3);
        LauncherSchemaMigrator migrator = new(
            options,
            new EmbeddedLauncherSchemaMigrationSource(),
            new LauncherSchemaValidator(),
            "04C.3a-test",
            logger);

        IReadOnlyList<LauncherSchemaMigrationOutcome> first = await migrator.MigrateAsync();
        Equal(4, first.Count, "Le resultat doit rendre visibles les migrations eligibles et bloquees.");
        True(first.Take(3).All(item => item.State == LauncherSchemaMigrationState.Applied),
            "Une base fraiche doit appliquer 0001, 0002 et 0003.");
        Equal(LauncherSchemaMigrationState.BlockedByCeiling, first[3].State,
            "0004 doit etre explicitement bloquee.");
        True(logger.Messages.Any(message => message.Contains("0004", StringComparison.Ordinal)
            && message.Contains("0003", StringComparison.Ordinal)
            && message.Contains("bloquee", StringComparison.Ordinal)),
            "Le journal doit expliquer que 0004 est disponible mais bloquee par le plafond 0003.");
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U]);
        await AssertSchemaThreeForeignKeysAsync(builder.ConnectionString);
        await ValidateSocialRuntimeOnSchemaThreeAsync(options);

        IReadOnlyList<LauncherSchemaMigrationOutcome> second = await migrator.MigrateAsync();
        True(second.Take(3).All(item => item.State == LauncherSchemaMigrationState.AlreadyApplied),
            "La seconde execution doit conserver 0001-0003 sans modification.");
        Equal(LauncherSchemaMigrationState.BlockedByCeiling, second[3].State,
            "0004 doit rester bloquee lors d'une seconde execution.");
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U]);
    }

    private static async Task ValidateHigherHistoryIsRejectedAsync(MySqlConnectionStringBuilder builder)
    {
        await ResetFreshSchemaAsync(builder.ConnectionString);
        LauncherServerOptions unrestricted = CreateOptions(builder, maximumSchemaVersion: null);
        await new LauncherSchemaMigrator(unrestricted).MigrateAsync();
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U, 4U]);

        LauncherServerOptions capped = CreateOptions(builder, maximumSchemaVersion: 3);
        await ExpectAsync<InvalidOperationException>(
            () => new LauncherSchemaMigrator(capped).MigrateAsync(),
            "Une base contenant deja 0004 doit refuser un plafond 0003.");
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U, 4U]);
    }

    private static async Task ValidateAppliedChecksumStillProtectedAsync(MySqlConnectionStringBuilder builder)
    {
        await ResetFreshSchemaAsync(builder.ConnectionString);
        LauncherServerOptions options = CreateOptions(builder, maximumSchemaVersion: 3);
        await new LauncherSchemaMigrator(options).MigrateAsync();

        IReadOnlyList<LauncherSchemaMigration> original = new EmbeddedLauncherSchemaMigrationSource().Load();
        string changedSql = original[1].Sql + "-- forbidden checksum change\n";
        LauncherSchemaMigration changed = original[1] with
        {
            Sql = changedSql,
            Sha256 = SHA256.HashData(Encoding.UTF8.GetBytes(changedSql))
        };
        await ExpectAsync<InvalidOperationException>(
            () => new LauncherSchemaMigrator(
                options,
                new FixedMigrationSource([original[0], changed, original[2], original[3]]),
                new LauncherSchemaValidator(),
                "04C.3a-checksum").MigrateAsync(),
            "Le plafond ne doit pas contourner le controle des checksums appliques.");
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U]);
    }

    private static LauncherServerOptions CreateOptions(
        MySqlConnectionStringBuilder builder,
        uint? maximumSchemaVersion)
        => new()
        {
            ConnectionString = builder.ConnectionString,
            CharacterDatabaseName = builder.Database,
            MaximumSchemaVersion = maximumSchemaVersion
        };

    private static async Task ValidateSocialRuntimeOnSchemaThreeAsync(LauncherServerOptions options)
    {
        Guid avatarId = Guid.NewGuid();
        await using (MySqlConnection connection = new(options.ConnectionString))
        {
            await connection.OpenAsync();
            await using MySqlCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO account (id, username) VALUES
                    (1001, 'ATLASOWNER'),
                    (1002, 'ATLASPHOTO'),
                    (1003, 'ATLASPLAIN'),
                    (1004, 'RNDBOT_HIDDEN');

                INSERT INTO atlas_launcher_profile
                    (account_id, display_username, email_normalized)
                VALUES
                    (1001, 'AtlasOwner', 'OWNER@EXAMPLE.TEST'),
                    (1002, 'AtlasPhoto', 'PHOTO@EXAMPLE.TEST'),
                    (1003, 'AtlasPlain', 'PLAIN@EXAMPLE.TEST');

                INSERT INTO atlas_launcher_avatar_asset
                    (id, owner_account_id, version, status, storage_key)
                VALUES
                    (@avatarId, 1002, 7, 1, 'avatars/test/schema3/photo/v7');

                INSERT INTO atlas_launcher_profile_avatar
                    (account_id, current_avatar_asset_id)
                VALUES
                    (1002, @avatarId);

                INSERT INTO atlas_launcher_friendship
                    (account_low_id, account_high_id, requested_by_id, accepted_at)
                VALUES
                    (1001, 1002, 1001, UTC_TIMESTAMP()),
                    (1001, 1003, 1001, NULL),
                    (1001, 1004, 1004, NULL);

                INSERT INTO characters
                    (guid, account, name, level, `class`, zone, online, logout_time)
                VALUES
                    (2001, 1002, 'Photochar', 80, 6, 67, 1, 0),
                    (2002, 1003, 'Plainchar', 42, 8, 12, 0, 1700000000),
                    (2003, 1004, 'Hiddenbot', 80, 1, 1, 1, 0);
                """;
            command.Parameters.Add("@avatarId", MySqlDbType.Binary, 16)
                .Value = avatarId.ToByteArray(bigEndian: true);
            await command.ExecuteNonQueryAsync();
        }

        LauncherDatabase database = new(
            options,
            new TokenService(),
            new LauncherSchemaMigrator(options));
        IReadOnlyList<LauncherFriend> friends = await database.ListFriendsAsync(
            1001,
            CancellationToken.None);
        Equal(2, friends.Count,
            "Le compte AzerothCore sans profil doit etre exclu meme avec les FK de schema 0003.");
        LauncherFriend withAvatar = friends.Single(item => item.AccountId == 1002);
        Equal(AvatarDescriptor.Create(avatarId, 7), withAvatar.Avatar,
            "Le descripteur avatar doit fonctionner sans 0004.");
        True(friends.Single(item => item.AccountId == 1003).Avatar is null,
            "Un profil sans photo doit conserver Avatar=null.");
        True(friends.All(item => item.AccountId != 1004),
            "Un compte technique sans profil ne doit jamais etre expose.");
        Equal(2, LauncherDatabase.FriendListMaximumQueryCount,
            "La lecture sociale doit rester groupee en deux requetes au maximum.");

        FriendRequestResult hidden = await database.SendFriendRequestAsync(
            1001,
            "RNDBOT_HIDDEN",
            CancellationToken.None);
        Equal(FriendRequestOutcome.NotFound, hidden.Outcome,
            "La recherche sociale doit exiger atlas_launcher_profile sous le schema 0003.");
    }

    private static async Task ResetFreshSchemaAsync(string connectionString)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SET FOREIGN_KEY_CHECKS = 0;
            DROP TABLE IF EXISTS atlas_launcher_avatar_upload_attempt;
            DROP TABLE IF EXISTS atlas_launcher_profile_avatar;
            DROP TABLE IF EXISTS atlas_launcher_avatar_variant;
            DROP TABLE IF EXISTS atlas_launcher_avatar_asset;
            DROP TABLE IF EXISTS atlas_launcher_email_verification;
            DROP TABLE IF EXISTS atlas_launcher_session;
            DROP TABLE IF EXISTS atlas_launcher_friendship;
            DROP TABLE IF EXISTS atlas_launcher_profile;
            DROP TABLE IF EXISTS atlas_launcher_schema_history;
            DROP TABLE IF EXISTS characters;
            DROP TABLE IF EXISTS account;
            SET FOREIGN_KEY_CHECKS = 1;

            CREATE TABLE account (
                id INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                username VARCHAR(32) NOT NULL UNIQUE
            ) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

            CREATE TABLE characters (
                guid BIGINT UNSIGNED NOT NULL PRIMARY KEY,
                account INT UNSIGNED NOT NULL,
                name VARCHAR(32) NOT NULL,
                level TINYINT UNSIGNED NOT NULL,
                `class` TINYINT UNSIGNED NOT NULL,
                zone INT UNSIGNED NOT NULL,
                online TINYINT UNSIGNED NOT NULL,
                logout_time INT UNSIGNED NOT NULL,
                INDEX ix_characters_account (account)
            ) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertMySql84Async(string connectionString)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT VERSION()";
        string version = Convert.ToString(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        True(version.StartsWith("8.4.", StringComparison.Ordinal),
            $"MySQL 8.4 est obligatoire pour ce test, version observee : {version}.");
    }

    private static async Task AssertHistoryAsync(
        string connectionString,
        IReadOnlyList<uint> expected)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM atlas_launcher_schema_history ORDER BY version";
        List<uint> actual = [];
        await using MySqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            actual.Add(reader.GetUInt32(0));
        True(expected.SequenceEqual(actual),
            $"Historique inattendu : [{string.Join(',', actual)}].");
    }

    private static async Task AssertSchemaThreeForeignKeysAsync(string connectionString)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE CONSTRAINT_SCHEMA = DATABASE()
              AND CONSTRAINT_NAME IN (
                  'fk_atlas_session_account',
                  'fk_atlas_email_account',
                  'fk_atlas_friend_low',
                  'fk_atlas_friend_high',
                  'fk_atlas_friend_requester',
                  'fk_atlas_avatar_owner',
                  'fk_atlas_avatar_upload_account')
              AND REFERENCED_TABLE_NAME = 'account';
            """;
        long count = Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
        Equal(7L, count,
            "Le schema de test doit rester exactement en 0003, avant le changement de FK de 0004.");
    }

    private static async Task ExpectAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Attendu={expected}, reel={actual}.");
    }

    private sealed class FixedMigrationSource(IReadOnlyList<LauncherSchemaMigration> migrations)
        : ILauncherSchemaMigrationSource
    {
        public IReadOnlyList<LauncherSchemaMigration> Load() => migrations;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
