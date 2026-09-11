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
        Equal<uint?>(5, LauncherSchemaMigrationCeiling.Resolve("5", isProduction: true),
            "Le plafond de production attendu doit etre accepte.");
        True(
            LauncherSchemaMigrator.SupportsRequiredLockingSyntax("8.4.6")
            && LauncherSchemaMigrator.SupportsRequiredLockingSyntax("8.0.36-0ubuntu0")
            && !LauncherSchemaMigrator.SupportsRequiredLockingSyntax("5.7.44")
            && !LauncherSchemaMigrator.SupportsRequiredLockingSyntax("10.11.8-MariaDB"),
            "Les schemas avec SKIP LOCKED doivent refuser explicitement MySQL 5.7 et MariaDB.");

        ExpectConfigurationFailure(null, isProduction: true);
        foreach (string invalid in new[] { "", "0", "-1", "+3", "03", " 3", "3 ", "3.0", "4294967296" })
            ExpectConfigurationFailure(invalid, isProduction: true);

        IReadOnlyList<LauncherSchemaMigration> embedded = new EmbeddedLauncherSchemaMigrationSource().Load();
        Equal(11, embedded.Count, "Les onze migrations doivent rester embarquees.");
        Equal((uint)4, embedded[3].Version, "La frontiere d'identite doit rester versionnee en 0004.");
        Equal("atlas_profile_identity_boundary", embedded[3].Name,
            "La migration de frontiere ne doit pas etre remplacee.");
        Equal((uint)5, embedded[4].Version, "0005 doit conserver sa version.");
        Equal("social_profile", embedded[4].Name,
            "La migration du profil social doit rester embarquee.");
        Equal("private_chat", embedded[5].Name, "La messagerie privee doit rester en migration 0006.");
        Equal("chat_workspace", embedded[6].Name, "L'espace de chat doit rester en migration 0007.");
        Equal("global_presence", embedded[7].Name, "La presence globale doit rester en migration 0008.");
        Equal((uint)9, embedded[8].Version, "Les familles de session doivent rester versionnees en 0009.");
        Equal("auth_session_families", embedded[8].Name,
            "Les familles de session doivent rester en migration 0009.");
        Equal((uint)10, embedded[9].Version, "Les index des sessions restent en 0010.");
        Equal((uint)11, embedded[10].Version, "Le portefeuille reste en 0011.");
        Equal("manual_shop_funding", embedded[10].Name, "La migration du portefeuille doit rester embarquee.");
        Equal("auth_session_gc", embedded[9].Name,
            "Les index de collecte des sessions doivent rester en migration 0010.");
        foreach (string indexName in new[]
                 {
                     "ix_atlas_session_revoked_gc",
                     "ix_atlas_session_expired_gc",
                     "ix_atlas_session_account_revoked",
                     "ix_atlas_session_account_active",
                     "ix_atlas_session_account_active_order"
                 })
        {
            True(embedded[9].Sql.Contains(indexName, StringComparison.Ordinal),
                $"La migration 0010 doit embarquer l'index {indexName}.");
        }
        Equal(256, LauncherDatabase.RefreshHistoryCleanupBatchSize,
            "La purge opportuniste de l'historique refresh doit rester bornee par requete.");
        Equal(3, LauncherDatabase.AuthTransactionDeadlockRetryLimit,
            "Les transactions d'authentification doivent conserver une reprise bornee sur interblocage MySQL.");
        Equal(4096, LauncherDatabase.MaximumRefreshRotationsPerFamily,
            "Une famille active ne doit jamais accumuler plus de 4096 preuves de rotation.");
        DateTimeOffset absoluteDeadline = DateTimeOffset.UtcNow.AddMinutes(1);
        SessionTokens deadlineBound = new TokenService().Create(
            accessMinutes: 15,
            refreshDays: 30,
            absoluteRefreshExpiresAt: absoluteDeadline);
        True(deadlineBound.AccessExpiresAt == absoluteDeadline
            && deadlineBound.RefreshExpiresAt == absoluteDeadline,
            "Une rotation proche de l'echeance absolue doit borner aussi le jeton d'acces a cette echeance.");
        Equal(12, LauncherDatabase.MaximumActiveSessionsPerAccount,
            "Le nombre global de sessions actives par compte doit rester explicitement borne.");
        Equal(64, LauncherDatabase.MaximumSessionTombstonesPerAccount,
            "Le nombre de tombstones conserves par compte doit rester explicitement borne.");
        Equal(60, LauncherDatabase.SessionTombstoneRetentionMinutes,
            "La retention temporelle des tombstones doit rester courte et explicite.");
        Equal(64, LauncherDatabase.SessionGarbageCollectionBatchSize,
            "Chaque requete de collecte des sessions doit rester bornee.");
        Equal(64, LauncherDatabase.SessionRevocationBatchSize,
            "Une transaction ne doit revoquer au plus qu'un lot fixe de 64 sessions.");
        Equal(77, LauncherDatabase.AccountSessionRepairProbeSize,
            "Le probe de reparation doit lire au plus plafond actif + budget de 64 + sentinelle.");
        Equal(
            LauncherDatabase.MaximumActiveSessionsPerAccount,
            LauncherDatabase.SessionListLimit,
            "La liste des sessions ne doit pas lire davantage que le plafond actif du compte.");
        True(
            LauncherDatabase.ActiveSessionProbeSql.Contains(
                "FORCE INDEX (ix_atlas_session_account_active)",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ActiveSessionProbeSql.Contains(
                "ORDER BY refresh_expires_at ASC, id ASC",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ActiveSessionProbeSql.Contains(
                "LIMIT @probeLimit",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ActiveSessionProbeSql.Contains(
                "FOR UPDATE",
                StringComparison.OrdinalIgnoreCase),
            "Le login doit verrouiller un probe actif indexe, deterministe et explicitement borne avant de choisir ses victimes.");
        True(
            LauncherDatabase.ExcessSessionTombstoneIdsSql.Contains(
                "FORCE INDEX (ix_atlas_session_account_revoked)",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ExcessSessionTombstoneIdsSql.Contains(
                "ORDER BY revoked_at ASC, id ASC",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ExcessSessionTombstoneIdsSql.Contains(
                "LIMIT @probeLimit",
                StringComparison.OrdinalIgnoreCase),
            "Le plafond par compte doit verrouiller ses tombstones du plus ancien au plus recent, dans le meme ordre que la collecte globale.");
        True(
            LauncherDatabase.ListSessionsSql.Contains(
                "LIMIT @sessionLimit",
                StringComparison.OrdinalIgnoreCase),
            "La requete de liste des sessions doit conserver une limite SQL parametree.");
        string cleanupSql = LauncherDatabase.RefreshHistoryCleanupSql;
        int expiryFilter = cleanupSql.IndexOf(
            "WHERE expires_at <= UTC_TIMESTAMP()",
            StringComparison.OrdinalIgnoreCase);
        int expiryOrder = cleanupSql.IndexOf(
            "ORDER BY expires_at",
            StringComparison.OrdinalIgnoreCase);
        int batchLimit = cleanupSql.IndexOf(
            "LIMIT @batchLimit",
            StringComparison.OrdinalIgnoreCase);
        True(expiryFilter >= 0 && expiryOrder > expiryFilter && batchLimit > expiryOrder,
            "La purge refresh doit utiliser l'index d'expiration, supprimer les plus anciennes lignes et conserver une limite parametree.");
        True(cleanupSql.Contains("token_hash ASC", StringComparison.OrdinalIgnoreCase)
            && cleanupSql.Contains(
                "FORCE INDEX (ix_atlas_refresh_history_expiry)",
                StringComparison.OrdinalIgnoreCase)
            && cleanupSql.Contains(
                "FOR UPDATE SKIP LOCKED",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.RefreshHistoryDeleteSql.Contains(
                "WHERE token_hash = @tokenHash",
                StringComparison.OrdinalIgnoreCase),
            "La purge refresh doit verrouiller un lot d'expirations non occupees puis supprimer seulement ces cles exactes.");
        string replayLocatorSql = LauncherDatabase.ReplayHistoryLocatorSql;
        string replaySessionLockSql = LauncherDatabase.ReplaySessionLockSql;
        string replayRevalidationSql = LauncherDatabase.ReplayHistoryRevalidationSql;
        True(replayLocatorSql.Contains(
                "expires_at > UTC_TIMESTAMP()",
                StringComparison.OrdinalIgnoreCase)
            && !replayLocatorSql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
            && !replayLocatorSql.Contains("atlas_launcher_session", StringComparison.OrdinalIgnoreCase)
            && replaySessionLockSql.Contains(
                "absolute_expires_at > UTC_TIMESTAMP()",
                StringComparison.OrdinalIgnoreCase)
            && replaySessionLockSql.Contains(
                "account_id = @accountId",
                StringComparison.OrdinalIgnoreCase)
            && replaySessionLockSql.Contains("revoked_at IS NULL", StringComparison.OrdinalIgnoreCase)
            && replaySessionLockSql.Contains(
                "FOR UPDATE",
                StringComparison.OrdinalIgnoreCase)
            && replayRevalidationSql.Contains("session_id = @sessionId", StringComparison.OrdinalIgnoreCase)
            && replayRevalidationSql.Contains("expires_at > UTC_TIMESTAMP()", StringComparison.OrdinalIgnoreCase)
            && replayRevalidationSql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase),
            "La detection de rejeu doit localiser sans verrou, verrouiller la session, puis revalider la preuve et ses echeances.");
        True(
            LauncherDatabase.RefreshHistoryCountSql.Contains(
                "FORCE INDEX (ix_atlas_refresh_history_session)",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.RefreshHistoryCountSql.Contains(
                "WHERE session_id = @sessionId",
                StringComparison.OrdinalIgnoreCase),
            "Le plafond de rotation doit compter une seule famille par l'index de session de 0009.");
        True(
            LauncherDatabase.PurgeRefreshHistoryBySessionSql.Contains(
                "DELETE FROM atlas_launcher_refresh_history",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.PurgeRefreshHistoryBySessionSql.Contains(
                "WHERE session_id = @sessionId",
                StringComparison.OrdinalIgnoreCase),
            "Une revocation ciblee doit supprimer atomiquement tout l'historique de sa famille.");
        foreach (string cleanup in new[]
                 {
                     LauncherDatabase.RevokedSessionCleanupSql,
                     LauncherDatabase.ExpiredSessionCleanupSql
                 })
        {
            True(!cleanup.Contains("NOT EXISTS", StringComparison.OrdinalIgnoreCase)
                && cleanup.Contains("FORCE INDEX", StringComparison.OrdinalIgnoreCase)
                && cleanup.Contains("LIMIT @batchLimit", StringComparison.OrdinalIgnoreCase)
                && cleanup.Contains("FOR UPDATE SKIP LOCKED", StringComparison.OrdinalIgnoreCase),
                "Chaque classe de collecte doit verrouiller un lot fixe de parents avant toute lecture de l'historique.");
        }
        True(
            LauncherDatabase.RevokedSessionCleanupSql.Contains(
                "ORDER BY revoked_at ASC, id ASC",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ExpiredSessionCleanupSql.Contains(
                "revoked_at IS NULL",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ExpiredSessionCleanupSql.Contains(
                "FORCE INDEX (ix_atlas_session_expired_gc)",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ExpiredSessionCleanupSql.Contains(
                "access_expires_at <= UTC_TIMESTAMP()",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ExpiredSessionDeleteSql.Contains(
                "access_expires_at <= UTC_TIMESTAMP()",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.ExpiredSessionCleanupSql.Contains(
                "ORDER BY absolute_expires_at ASC, id ASC",
                StringComparison.OrdinalIgnoreCase),
            "Les collectes revoquee et expiree doivent etre disjointes et deterministes.");
        True(
            LauncherDatabase.RefreshHistoryExistenceLockSql.Contains(
                "FORCE INDEX (ix_atlas_refresh_history_session)",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.RefreshHistoryExistenceLockSql.Contains(
                "WHERE session_id = @sessionId",
                StringComparison.OrdinalIgnoreCase)
            && LauncherDatabase.RefreshHistoryExistenceLockSql.Contains(
                "FOR UPDATE",
                StringComparison.OrdinalIgnoreCase),
            "Apres verrouillage du lot parent, chaque famille doit revalider son historique par l'index de session avant suppression.");

        Console.WriteLine(
            "Migration ceiling configuration OK: production 0005 preserved; migrations locales 0006-0011 embedded; bounded session/history cleanup, replay lock order, rotation and account caps verified.");
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
        Equal(11, first.Count, "Le resultat doit rendre visibles les migrations eligibles et bloquees.");
        True(first.Take(3).All(item => item.State == LauncherSchemaMigrationState.Applied),
            "Une base fraiche doit appliquer 0001, 0002 et 0003.");
        True(first.Skip(3).All(item => item.State == LauncherSchemaMigrationState.BlockedByCeiling),
            "0004 a 0011 doivent etre explicitement bloquees.");
        True(logger.Messages.Any(message => message.Contains("0004", StringComparison.Ordinal)
            && message.Contains("0003", StringComparison.Ordinal)
            && message.Contains("bloquee", StringComparison.Ordinal)),
            "Le journal doit expliquer que 0004 est disponible mais bloquee par le plafond 0003.");
        True(logger.Messages.Any(message => message.Contains("0005", StringComparison.Ordinal)
            && message.Contains("0003", StringComparison.Ordinal)
            && message.Contains("bloquee", StringComparison.Ordinal)),
            "Le journal doit expliquer que 0005 est disponible mais bloquee par le plafond 0003.");
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U]);
        await AssertSchemaThreeForeignKeysAsync(builder.ConnectionString);
        await ValidateSocialRuntimeOnSchemaThreeAsync(options);

        IReadOnlyList<LauncherSchemaMigrationOutcome> second = await migrator.MigrateAsync();
        True(second.Take(3).All(item => item.State == LauncherSchemaMigrationState.AlreadyApplied),
            "La seconde execution doit conserver 0001-0003 sans modification.");
        True(second.Skip(3).All(item => item.State == LauncherSchemaMigrationState.BlockedByCeiling),
            "0004 a 0011 doivent rester bloquees lors d'une seconde execution.");
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U]);
    }

    private static async Task ValidateHigherHistoryIsRejectedAsync(MySqlConnectionStringBuilder builder)
    {
        await ResetFreshSchemaAsync(builder.ConnectionString);
        LauncherServerOptions unrestricted = CreateOptions(builder, maximumSchemaVersion: null);
        await new LauncherSchemaMigrator(unrestricted).MigrateAsync();
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U]);

        LauncherServerOptions capped = CreateOptions(builder, maximumSchemaVersion: 3);
        await ExpectAsync<InvalidOperationException>(
            () => new LauncherSchemaMigrator(capped).MigrateAsync(),
            "Une base contenant deja des migrations superieures a 0003 doit refuser ce plafond.");
        await AssertHistoryAsync(builder.ConnectionString, [1U, 2U, 3U, 4U, 5U, 6U, 7U, 8U, 9U, 10U]);
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
        LauncherSchemaMigration[] changedMigrations = original.ToArray();
        changedMigrations[1] = changed;
        await ExpectAsync<InvalidOperationException>(
            () => new LauncherSchemaMigrator(
                options,
                new FixedMigrationSource(changedMigrations),
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
            DROP TABLE IF EXISTS atlas_launcher_refresh_history;
            DROP TABLE IF EXISTS atlas_launcher_presence;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_event;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_preferences;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_reaction;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_request;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_message;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_member;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_thread;
            DROP TABLE IF EXISTS atlas_launcher_chat_v2_sequence;
            DROP TABLE IF EXISTS atlas_launcher_chat_outbox;
            DROP TABLE IF EXISTS atlas_launcher_chat_inbox;
            DROP TABLE IF EXISTS atlas_launcher_chat_message;
            DROP TABLE IF EXISTS atlas_launcher_chat_conversation;
            DROP TABLE IF EXISTS atlas_launcher_chat_account;
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
