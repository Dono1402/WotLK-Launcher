using System.Data;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MySqlConnector;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static partial class AuthSessionSecurityMySqlTests
{
    private const string ConnectionVariable = "ATLAS_AUTH_SESSION_TEST_DB";
    private const string DatabasePrefix = "atlas_auth_session_test_";
    private const uint AccountId = 1001;
    private const uint GarbageCollectionAccountId = 1002;
    private const string DisplayUsername = "AtlasAuthFixture";
    private const string Username = "ATLASAUTHFIXTURE";
    private const string InitialPassword = "fixture-initial-password";
    private const string ChangedPassword = "fixture-changed-password";
    private const string FinalPassword = "fixture-final-password";
    private static readonly CancellationToken None = CancellationToken.None;
    private static int _checks;

    internal static async Task<int> RunAsync()
    {
        _checks = 0;
        MySqlConnectionStringBuilder connection = ReadSafeConnection();
        await ResetSchemaAsync(connection.ConnectionString);
        try
        {
            await CreateAzerothCoreFixtureAsync(connection.ConnectionString);
            LauncherServerOptions version8 = Options(connection, 8);
            IReadOnlyList<LauncherSchemaMigrationOutcome> firstEight =
                await new LauncherSchemaMigrator(version8).MigrateAsync(None);
            Check(firstEight.Count == 10
                && firstEight.Take(8).All(item =>
                    item.State == LauncherSchemaMigrationState.Applied)
                && firstEight.Skip(8).All(item =>
                    item.State == LauncherSchemaMigrationState.BlockedByCeiling),
                "A fresh fixture applies 0001-0008 and explicitly blocks 0009-0010.");

            await SeedAccountAsync(connection.ConnectionString);
            TokenService tokenService = new();
            DateTimeOffset originalAbsoluteExpiry = TruncateToSecond(
                DateTimeOffset.UtcNow.AddHours(6));
            SessionTokens original = tokenService
                .Create(accessMinutes: 15, refreshDays: 30, originalAbsoluteExpiry);
            byte[] originalFamilyId = Guid.NewGuid().ToByteArray();
            await InsertVersion8SessionAsync(
                connection.ConnectionString,
                originalFamilyId,
                original,
                "migration-fixture");

            // Simulate a process loss after MySQL committed the first 0009 DDL
            // statement but before the backfill, index, table and history row.
            await ExecuteAsync(
                connection.ConnectionString,
                """
                ALTER TABLE atlas_launcher_session
                    ADD COLUMN absolute_expires_at DATETIME NULL AFTER refresh_expires_at;
                """);
            Check(await ScalarInt64Async(
                    connection.ConnectionString,
                    """
                    SELECT COUNT(*)
                    FROM atlas_launcher_session
                    WHERE absolute_expires_at IS NULL;
                    """) == 1,
                "The interrupted 0009 fixture contains the committed nullable column and no backfill.");

            LauncherServerOptions version9 = Options(connection, 9);
            IReadOnlyList<LauncherSchemaMigrationOutcome> migration9 =
                await new LauncherSchemaMigrator(version9).MigrateAsync(None);
            Check(migration9.Count == 10
                && migration9.Take(8).All(item =>
                    item.State == LauncherSchemaMigrationState.AlreadyApplied)
                && migration9[8].State == LauncherSchemaMigrationState.Applied
                && migration9[9].State == LauncherSchemaMigrationState.BlockedByCeiling,
                "0009 applies once after an existing version-8 history.");
            Check(await ScalarInt64Async(
                    connection.ConnectionString,
                    "SELECT COUNT(*) FROM atlas_launcher_refresh_history;") == 0,
                "The new refresh-token history starts empty.");
            DateTimeOffset backfilled = await ReadDateTimeAsync(
                connection.ConnectionString,
                "SELECT absolute_expires_at FROM atlas_launcher_session WHERE id = @id;",
                ("@id", originalFamilyId));
            Check(backfilled == originalAbsoluteExpiry,
                "0009 resumes after partial DDL and backfills the existing refresh deadline without extending it.");

            await ExecuteAsync(
                connection.ConnectionString,
                """
                DELETE FROM atlas_launcher_schema_history WHERE version = 9;
                ALTER TABLE atlas_launcher_refresh_history
                    DROP INDEX ix_atlas_refresh_history_expiry;
                """);
            IReadOnlyList<LauncherSchemaMigrationOutcome> repaired9 =
                await new LauncherSchemaMigrator(version9).MigrateAsync(None);
            Check(repaired9.Take(8).All(item =>
                    item.State == LauncherSchemaMigrationState.AlreadyApplied)
                && repaired9[8].State == LauncherSchemaMigrationState.Applied
                && repaired9[9].State == LauncherSchemaMigrationState.BlockedByCeiling,
                "0009 repairs a missing refresh-history index before recording the interrupted migration.");

            await ExecuteAsync(
                connection.ConnectionString,
                "DELETE FROM atlas_launcher_schema_history WHERE version = 9;");
            IReadOnlyList<LauncherSchemaMigrationOutcome> adopted =
                await new LauncherSchemaMigrator(version9).MigrateAsync(None);
            Check(adopted.Take(8).All(item =>
                    item.State == LauncherSchemaMigrationState.AlreadyApplied)
                && adopted[8].State == LauncherSchemaMigrationState.Adopted
                && adopted[9].State == LauncherSchemaMigrationState.BlockedByCeiling,
                "A complete and validated 0009 schema with a missing history row is safely adopted.");

            IReadOnlyList<LauncherSchemaMigrationOutcome> idempotent =
                await new LauncherSchemaMigrator(version9).MigrateAsync(None);
            Check(idempotent.Take(9).All(item =>
                    item.State == LauncherSchemaMigrationState.AlreadyApplied)
                && idempotent[9].State == LauncherSchemaMigrationState.BlockedByCeiling,
                "A second version-9 migration run is fully idempotent.");

            // Simulate interruption after the first 0010 index was committed.
            await ExecuteAsync(
                connection.ConnectionString,
                """
                ALTER TABLE atlas_launcher_session
                    ADD INDEX ix_atlas_session_revoked_gc (revoked_at, id);
                """);
            LauncherServerOptions version10 = Options(connection, 10);
            IReadOnlyList<LauncherSchemaMigrationOutcome> migration10 =
                await new LauncherSchemaMigrator(version10).MigrateAsync(None);
            Check(migration10.Count == 10
                && migration10.Take(9).All(item =>
                    item.State == LauncherSchemaMigrationState.AlreadyApplied)
                && migration10[9].State == LauncherSchemaMigrationState.Applied,
                "0010 resumes after one committed index and adds every missing GC index atomically.");

            await ExecuteAsync(
                connection.ConnectionString,
                "DELETE FROM atlas_launcher_schema_history WHERE version = 10;");
            IReadOnlyList<LauncherSchemaMigrationOutcome> adopted10 =
                await new LauncherSchemaMigrator(version10).MigrateAsync(None);
            Check(adopted10.Take(9).All(item =>
                    item.State == LauncherSchemaMigrationState.AlreadyApplied)
                && adopted10[9].State == LauncherSchemaMigrationState.Adopted,
                "A complete 0010 index set without its history row is validated and adopted.");
            IReadOnlyList<LauncherSchemaMigrationOutcome> idempotent10 =
                await new LauncherSchemaMigrator(version10).MigrateAsync(None);
            Check(idempotent10.All(item =>
                    item.State == LauncherSchemaMigrationState.AlreadyApplied),
                "A second version-10 migration run is fully idempotent.");
            await ExecuteAsync(
                connection.ConnectionString,
                "ALTER TABLE atlas_launcher_session ALTER INDEX ix_atlas_session_revoked_gc INVISIBLE;");
            bool invisibleRejected = false;
            try
            {
                _ = await new LauncherSchemaMigrator(version10).MigrateAsync(None);
            }
            catch (InvalidOperationException)
            {
                invisibleRejected = true;
            }
            Check(invisibleRejected,
                "Final version-10 validation rejects an invisible index even when migration history is already recorded.");
            await ExecuteAsync(
                connection.ConnectionString,
                "ALTER TABLE atlas_launcher_session ALTER INDEX ix_atlas_session_revoked_gc VISIBLE;");
            await ExecuteAsync(
                connection.ConnectionString,
                """
                ALTER TABLE atlas_launcher_session
                    DROP INDEX ix_atlas_session_revoked_gc,
                    ADD INDEX ix_atlas_session_revoked_gc (revoked_at ASC, id DESC);
                """);
            bool descendingComponentRejected = false;
            try
            {
                _ = await new LauncherSchemaMigrator(version10).MigrateAsync(None);
            }
            catch (InvalidOperationException)
            {
                descendingComponentRejected = true;
            }
            Check(descendingComponentRejected,
                "Final version-10 validation rejects a mixed-direction index that cannot preserve deterministic GC ordering.");
            await ExecuteAsync(
                connection.ConnectionString,
                """
                ALTER TABLE atlas_launcher_session
                    DROP INDEX ix_atlas_session_revoked_gc,
                    ADD INDEX ix_atlas_session_revoked_gc (revoked_at, id);
                """);
            await ExecuteAsync(
                connection.ConnectionString,
                "ALTER TABLE atlas_launcher_refresh_history ALTER INDEX ix_atlas_refresh_history_expiry INVISIBLE;");
            bool invisibleHistoryIndexRejected = false;
            try
            {
                _ = await new LauncherSchemaMigrator(version10).MigrateAsync(None);
            }
            catch (InvalidOperationException)
            {
                invisibleHistoryIndexRejected = true;
            }
            Check(invisibleHistoryIndexRejected,
                "Final version-9-plus validation rejects an invisible refresh-history expiry index.");
            await ExecuteAsync(
                connection.ConnectionString,
                "ALTER TABLE atlas_launcher_refresh_history ALTER INDEX ix_atlas_refresh_history_expiry VISIBLE;");

            LauncherDatabase database = new(
                version10,
                tokenService,
                new LauncherSchemaMigrator(version10));
            await VerifyRotationAndSequentialReplayAsync(
                database,
                connection.ConnectionString,
                originalFamilyId,
                original,
                originalAbsoluteExpiry);
            await VerifySimultaneousReplayAsync(database);
            await VerifyRotationCeilingAsync(database, connection.ConnectionString);
            await VerifyLogoutProofAsync(database, connection.ConnectionString);
            await VerifyTargetedSessionRevocationPurgeAsync(
                database,
                connection.ConnectionString);
            await VerifyDeviceRevocationPurgesAsync(database, connection.ConnectionString);
            await VerifyAbsoluteExpiryAsync(database, connection.ConnectionString);
            await VerifyExpiredHistoryCleanupAsync(database, connection.ConnectionString);
            await VerifyGlobalActiveSessionCapAsync(database, connection.ConnectionString);
            await VerifyReplayLogoutLockOrderAsync(database, connection.ConnectionString);
            await VerifyAtomicPasswordReplacementAsync(
                database,
                connection.ConnectionString);
            await VerifyGarbageCollectionLogoutBarrierAsync(
                database,
                connection.ConnectionString);
            await VerifySessionGarbageCollectionAsync(database, connection.ConnectionString);

            Console.WriteLine(
                $"Auth session security MySQL PASS: {_checks} assertions; disposable local schema only. Migrations 0009-0010, replay lock order, rotation ceiling, atomic history purge, bounded session/history GC, per-account active/tombstone caps and password serialization verified.");
            return 0;
        }
        finally
        {
            await ResetSchemaAsync(connection.ConnectionString);
        }
    }

    private static async Task VerifyRotationAndSequentialReplayAsync(
        LauncherDatabase database,
        string connectionString,
        byte[] familyId,
        SessionTokens original,
        DateTimeOffset absoluteExpiry)
    {
        LauncherDatabase.RefreshSessionResult first =
            await database.RefreshSessionAsync(original.RefreshToken, None);
        Check(first.Response is not null && !first.ReplayDetected,
            "The original refresh token rotates successfully once.");
        AuthResponse firstRotation = first.Response!;
        Check(firstRotation.RefreshExpiresAt == absoluteExpiry,
            "The first rotation preserves the family's absolute deadline.");

        LauncherDatabase.RefreshSessionResult second =
            await database.RefreshSessionAsync(firstRotation.RefreshToken, None);
        Check(second.Response is not null && !second.ReplayDetected,
            "The newly issued refresh token can rotate once.");
        AuthResponse current = second.Response!;
        Check(current.RefreshExpiresAt == absoluteExpiry,
            "A later rotation still cannot move the absolute deadline.");
        Check((await ReadBytesAsync(
                connectionString,
                "SELECT id FROM atlas_launcher_session WHERE access_hash = @hash;",
                ("@hash", TokenService.Hash(current.AccessToken))))
            .SequenceEqual(familyId),
            "Refresh rotation keeps one stable server-side family identifier.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", familyId)) == 2,
            "Each consumed token is archived in the same family.");

        LauncherDatabase.RefreshSessionResult replay =
            await database.RefreshSessionAsync(original.RefreshToken, None);
        Check(replay.Response is null
            && replay.ReplayUsername == Username
            && replay.ReplayDetected,
            "Reusing the oldest archived token is identified as a replay.");
        Check(await database.AuthenticateAsync(current.AccessToken, None) is null,
            "A replay revokes the current access token for the whole family.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", familyId)) == 0,
            "Replay revocation atomically removes every archived proof for the family.");
        LauncherDatabase.RefreshSessionResult currentAfterReplay =
            await database.RefreshSessionAsync(current.RefreshToken, None);
        Check(currentAfterReplay.Response is null && !currentAfterReplay.ReplayDetected,
            "The family's current refresh token is unusable after replay revocation.");
        AuthResponse newerFamily = await LoginAsync(
            database,
            InitialPassword,
            "post-replay-family");
        LauncherDatabase.RefreshSessionResult repeatedReplay =
            await database.RefreshSessionAsync(original.RefreshToken, None);
        Check(repeatedReplay.Response is null && !repeatedReplay.ReplayDetected,
            "An archived token from an already revoked family returns invalid without emitting another replay signal.");
        Check(await database.AuthenticateAsync(newerFamily.AccessToken, None) is not null,
            "Repeating an old family's replay cannot affect a newer session for the same account.");
    }

    private static async Task VerifySimultaneousReplayAsync(LauncherDatabase database)
    {
        AuthResponse original = await LoginAsync(
            database,
            InitialPassword,
            "parallel-refresh");
        Task<LauncherDatabase.RefreshSessionResult> first =
            database.RefreshSessionAsync(original.RefreshToken, None);
        Task<LauncherDatabase.RefreshSessionResult> second =
            database.RefreshSessionAsync(original.RefreshToken, None);
        LauncherDatabase.RefreshSessionResult[] results = await Task.WhenAll(first, second);

        Check(results.Count(result => result.Response is not null) == 1
            && results.Count(result => result.ReplayDetected) == 1,
            "Two simultaneous uses yield exactly one rotation and one replay.");
        AuthResponse winner = results.Single(result => result.Response is not null).Response!;
        Check(await database.AuthenticateAsync(winner.AccessToken, None) is null,
            "The detected simultaneous replay revokes the token returned to the winner.");
    }

    private static async Task VerifyReplayLogoutLockOrderAsync(
        LauncherDatabase database,
        string connectionString)
    {
        for (int iteration = 0; iteration < 8; iteration++)
        {
            AuthResponse original = await LoginAsync(
                database,
                InitialPassword,
                $"replay-logout-race-{iteration}");
            LauncherDatabase.RefreshSessionResult rotation =
                await database.RefreshSessionAsync(original.RefreshToken, None);
            Check(rotation.Response is not null,
                $"Replay/logout race fixture {iteration + 1} rotates once.");
            AuthResponse current = rotation.Response!;
            byte[] familyId = await ReadBytesAsync(
                connectionString,
                "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
                ("@hash", TokenService.Hash(current.RefreshToken)));

            Task<LauncherDatabase.RefreshSessionResult> replayTask =
                database.RefreshSessionAsync(original.RefreshToken, None);
            Task<LauncherDatabase.LogoutSessionResult?> logoutTask =
                database.LogoutSessionAsync(
                    current.AccessToken,
                    current.RefreshToken,
                    None);
            await Task.WhenAll(replayTask, logoutTask);
            LauncherDatabase.RefreshSessionResult replay = await replayTask;
            LauncherDatabase.LogoutSessionResult? logout = await logoutTask;

            Check(logout is not null
                && (replay.ReplayDetected ^ logout.RevokedNow),
                $"Replay/logout race {iteration + 1} completes with exactly one first revocation.");
            Check(await database.AuthenticateAsync(current.AccessToken, None) is null,
                $"Replay/logout race {iteration + 1} leaves the family revoked.");
            Check(await ScalarInt64Async(
                    connectionString,
                    "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                    ("@id", familyId)) == 0,
                $"Replay/logout race {iteration + 1} atomically purges family history.");
        }
    }

    private static async Task VerifyRotationCeilingAsync(
        LauncherDatabase database,
        string connectionString)
    {
        AuthResponse original = await LoginAsync(
            database,
            InitialPassword,
            "rotation-ceiling");
        byte[] familyId = await ReadBytesAsync(
            connectionString,
            "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
            ("@hash", TokenService.Hash(original.RefreshToken)));
        await SeedLiveRefreshHistoryAsync(
            connectionString,
            familyId,
            LauncherDatabase.MaximumRefreshRotationsPerFamily - 1);

        LauncherDatabase.RefreshSessionResult lastPermitted =
            await database.RefreshSessionAsync(original.RefreshToken, None);
        Check(lastPermitted.Response is not null
            && lastPermitted.RevocationReason is null,
            "A family with 4095 archived tokens may perform its 4096th rotation.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", familyId)) == LauncherDatabase.MaximumRefreshRotationsPerFamily,
            "The last permitted rotation leaves exactly 4096 archived proofs.");

        LauncherDatabase.RefreshSessionResult exceeded =
            await database.RefreshSessionAsync(lastPermitted.Response!.RefreshToken, None);
        Check(exceeded.Response is null
            && exceeded.RevokedUsername == Username
            && exceeded.ReplayUsername is null
            && exceeded.RotationLimitWasExceeded,
            "The next rotation returns the distinct rotation-limit revocation outcome.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", familyId)) == 0,
            "Rotation-limit revocation purges the family's 4096 archived proofs atomically.");
        Check(await database.AuthenticateAsync(lastPermitted.Response.AccessToken, None) is null,
            "Rotation-limit revocation invalidates the family's current access token.");

        LauncherDatabase.RefreshSessionResult repeated =
            await database.RefreshSessionAsync(lastPermitted.Response.RefreshToken, None);
        Check(repeated.Response is null && repeated.RevocationReason is null,
            "A purged, already-revoked family cannot emit the Hermes revocation outcome twice.");
    }

    private static async Task VerifyLogoutProofAsync(
        LauncherDatabase database,
        string connectionString)
    {
        AuthResponse original = await LoginAsync(
            database,
            InitialPassword,
            "refresh-proof-logout");
        LauncherDatabase.RefreshSessionResult rotation =
            await database.RefreshSessionAsync(original.RefreshToken, None);
        Check(rotation.Response is not null, "The logout fixture rotates before logout.");
        AuthResponse current = rotation.Response!;
        byte[] familyId = await ReadBytesAsync(
            connectionString,
            "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
            ("@hash", TokenService.Hash(current.RefreshToken)));
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_session
            SET access_expires_at = UTC_TIMESTAMP() - INTERVAL 1 MINUTE
            WHERE access_hash = @hash;
            """,
            ("@hash", TokenService.Hash(current.AccessToken)));

        LauncherDatabase.LogoutSessionResult? logout = await database.LogoutSessionAsync(
            current.AccessToken,
            original.RefreshToken,
            None);
        Check(logout?.Username == Username && logout.RevokedNow,
            "An archived refresh proof logs out even after the access token expires.");
        Check(await database.AuthenticateAsync(current.AccessToken, None) is null,
            "Refresh-proof logout revokes the rotated current session.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", familyId)) == 0,
            "Logout purges the revoked family's archived refresh proofs in the same transaction.");
        LauncherDatabase.LogoutSessionResult? repeated = await database.LogoutSessionAsync(
            current.AccessToken,
            original.RefreshToken,
            None);
        Check(repeated is null,
            "A purged archived logout proof cannot emit a second cross-service revocation signal.");
    }

    private static async Task VerifyAbsoluteExpiryAsync(
        LauncherDatabase database,
        string connectionString)
    {
        AuthResponse historyOriginal = await LoginAsync(
            database,
            InitialPassword,
            "expired-history-proof");
        LauncherDatabase.RefreshSessionResult historyRotation =
            await database.RefreshSessionAsync(historyOriginal.RefreshToken, None);
        Check(historyRotation.Response is not null,
            "The expired-history fixture rotates once before its archived proof expires.");
        AuthResponse historyCurrent = historyRotation.Response!;
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_refresh_history
            SET expires_at = UTC_TIMESTAMP() - INTERVAL 1 SECOND
            WHERE token_hash = @hash;
            """,
            ("@hash", TokenService.Hash(historyOriginal.RefreshToken)));
        LauncherDatabase.LogoutSessionResult? expiredProofLogout =
            await database.LogoutSessionAsync(
                null,
                historyOriginal.RefreshToken,
                None);
        Check(expiredProofLogout is null,
            "An expired archived refresh proof is ignored by logout lookup.");
        Check(await database.AuthenticateAsync(historyCurrent.AccessToken, None) is not null,
            "An expired archived proof cannot revoke the still-current family.");
        LauncherDatabase.RefreshSessionResult expiredProofReplay =
            await database.RefreshSessionAsync(historyOriginal.RefreshToken, None);
        Check(expiredProofReplay.Response is null
            && !expiredProofReplay.ReplayDetected
            && expiredProofReplay.ReplayUsername is null,
            "An expired archived proof is invalid without producing a replay or Hermes revocation signal.");
        Check(await database.AuthenticateAsync(historyCurrent.AccessToken, None) is not null,
            "Trying the expired archived proof leaves the current access token active.");

        AuthResponse familyOriginal = await LoginAsync(
            database,
            InitialPassword,
            "expired-family-proof");
        LauncherDatabase.RefreshSessionResult familyRotation =
            await database.RefreshSessionAsync(familyOriginal.RefreshToken, None);
        Check(familyRotation.Response is not null,
            "The expired-family fixture archives one refresh proof.");
        AuthResponse familyCurrent = familyRotation.Response!;
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_session
            SET absolute_expires_at = UTC_TIMESTAMP() - INTERVAL 1 SECOND,
                refresh_expires_at = UTC_TIMESTAMP() + INTERVAL 1 DAY
            WHERE refresh_hash = @hash;
            """,
            ("@hash", TokenService.Hash(familyCurrent.RefreshToken)));
        LauncherDatabase.LogoutSessionResult? expiredFamilyLogout =
            await database.LogoutSessionAsync(
                null,
                familyOriginal.RefreshToken,
                None);
        Check(expiredFamilyLogout is null,
            "An archived proof cannot match a family beyond its absolute deadline.");
        LauncherDatabase.LogoutSessionResult? accessFallbackLogout =
            await database.LogoutSessionAsync(
                familyCurrent.AccessToken,
                familyOriginal.RefreshToken,
                None);
        Check(accessFallbackLogout is { RevokedNow: true },
            "When an archived refresh proof fails revalidation, a valid access proof for the same session still logs out.");
        LauncherDatabase.RefreshSessionResult expiredFamilyReplay =
            await database.RefreshSessionAsync(familyOriginal.RefreshToken, None);
        Check(expiredFamilyReplay.Response is null
            && !expiredFamilyReplay.ReplayDetected
            && expiredFamilyReplay.ReplayUsername is null,
            "A very old proof for an expired family cannot produce a replay or Hermes revocation signal.");

        AuthResponse expiring = await LoginAsync(
            database,
            InitialPassword,
            "absolute-expiry");
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_session
            SET absolute_expires_at = UTC_TIMESTAMP() - INTERVAL 1 SECOND,
                refresh_expires_at = UTC_TIMESTAMP() + INTERVAL 1 DAY
            WHERE refresh_hash = @hash;
            """,
            ("@hash", TokenService.Hash(expiring.RefreshToken)));
        LauncherDatabase.RefreshSessionResult expired =
            await database.RefreshSessionAsync(expiring.RefreshToken, None);
        Check(expired.Response is null && !expired.ReplayDetected,
            "A current refresh token cannot rotate after its absolute family deadline.");
    }

    private static async Task VerifyTargetedSessionRevocationPurgeAsync(
        LauncherDatabase database,
        string connectionString)
    {
        AuthResponse original = await LoginAsync(
            database,
            InitialPassword,
            "targeted-revocation");
        LauncherDatabase.RefreshSessionResult rotation =
            await database.RefreshSessionAsync(original.RefreshToken, None);
        Check(rotation.Response is not null,
            "The targeted-revocation fixture archives one refresh proof.");
        byte[] familyId = await ReadBytesAsync(
            connectionString,
            "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
            ("@hash", TokenService.Hash(rotation.Response!.RefreshToken)));

        bool revoked = await database.RevokeSessionAsync(
            AccountId,
            Convert.ToHexString(familyId),
            None);
        Check(revoked,
            "The authenticated session-revocation API revokes the selected active family.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", familyId)) == 0,
            "The authenticated session-revocation API purges that family's history atomically.");
        Check(await database.AuthenticateAsync(rotation.Response.AccessToken, None) is null,
            "The targeted family's current access token is unusable after revocation.");
    }

    private static async Task VerifyDeviceRevocationPurgesAsync(
        LauncherDatabase database,
        string connectionString)
    {
        const string sameDevice = "same-device-purge";
        AuthResponse sameOriginal = await LoginAsync(
            database,
            InitialPassword,
            sameDevice);
        LauncherDatabase.RefreshSessionResult sameRotation =
            await database.RefreshSessionAsync(sameOriginal.RefreshToken, None);
        Check(sameRotation.Response is not null,
            "The same-device fixture archives one refresh proof.");
        byte[] sameFamilyId = await ReadBytesAsync(
            connectionString,
            "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
            ("@hash", TokenService.Hash(sameRotation.Response!.RefreshToken)));
        AuthResponse sameReplacement = await LoginAsync(
            database,
            InitialPassword,
            sameDevice);
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", sameFamilyId)) == 0,
            "Same-device login purges the superseded family's refresh history.");
        Check(await database.AuthenticateAsync(sameRotation.Response.AccessToken, None) is null
            && await database.AuthenticateAsync(sameReplacement.AccessToken, None) is not null,
            "Same-device login revokes only the superseded family.");

        AuthResponse olderOriginal = await LoginAsync(
            database,
            InitialPassword,
            "older-device-source");
        LauncherDatabase.RefreshSessionResult olderRotation =
            await database.RefreshSessionAsync(olderOriginal.RefreshToken, None);
        Check(olderRotation.Response is not null,
            "The older-device fixture archives one refresh proof.");
        AuthResponse newer = await LoginAsync(
            database,
            InitialPassword,
            "newer-device-source");
        byte[] olderFamilyId = await ReadBytesAsync(
            connectionString,
            "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
            ("@hash", TokenService.Hash(olderRotation.Response!.RefreshToken)));
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_session
            SET device_name = 'coalesced-device',
                created_at = CASE
                    WHEN id = @olderId THEN UTC_TIMESTAMP() - INTERVAL 1 DAY
                    ELSE UTC_TIMESTAMP()
                END
            WHERE refresh_hash IN (@olderHash, @newerHash);
            """,
            ("@olderId", olderFamilyId),
            ("@olderHash", TokenService.Hash(olderRotation.Response.RefreshToken)),
            ("@newerHash", TokenService.Hash(newer.RefreshToken)));

        LauncherDatabase.RefreshSessionResult newerRotation =
            await database.RefreshSessionAsync(newer.RefreshToken, None);
        Check(newerRotation.Response is not null,
            "Refreshing the newer duplicate-device family succeeds.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", olderFamilyId)) == 0,
            "Refresh of a newer device family purges the revoked older family's history.");
        Check(await database.AuthenticateAsync(olderRotation.Response.AccessToken, None) is null,
            "The older duplicate-device family is revoked during the newer refresh.");
    }

    private static async Task VerifyExpiredHistoryCleanupAsync(
        LauncherDatabase database,
        string connectionString)
    {
        AuthResponse original = await LoginAsync(
            database,
            InitialPassword,
            "history-cleanup");
        byte[] familyId = await ReadBytesAsync(
            connectionString,
            "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
            ("@hash", TokenService.Hash(original.RefreshToken)));
        int expiredFixtureCount = LauncherDatabase.RefreshHistoryCleanupBatchSize + 44;
        await SeedExpiredRefreshHistoryAsync(
            connectionString,
            familyId,
            expiredFixtureCount);
        Check(await CountExpiredHistoryAsync(connectionString) == expiredFixtureCount,
            "The cleanup fixture starts with more expired rows than one bounded batch.");

        LauncherDatabase.RefreshSessionResult first =
            await database.RefreshSessionAsync(original.RefreshToken, None);
        Check(first.Response is not null,
            "A bounded cleanup batch runs without preventing the requested rotation.");
        Check(await CountExpiredHistoryAsync(connectionString) == 44,
            "One refresh deletes exactly the configured maximum expired-history batch.");

        LauncherDatabase.RefreshSessionResult second =
            await database.RefreshSessionAsync(first.Response!.RefreshToken, None);
        Check(second.Response is not null,
            "A later rotation remains usable while it drains the remaining cleanup backlog.");
        Check(await CountExpiredHistoryAsync(connectionString) == 0,
            "A second bounded refresh drains the remaining expired-history rows.");
        Check(await ScalarInt64Async(
                connectionString,
                """
                SELECT COUNT(*)
                FROM atlas_launcher_refresh_history
                WHERE session_id = @sessionId
                  AND expires_at > UTC_TIMESTAMP();
                """,
                ("@sessionId", familyId)) == 2,
            "Cleanup preserves both unexpired replay proofs created by the two rotations.");
    }

    private static async Task VerifyAtomicPasswordReplacementAsync(
        LauncherDatabase database,
        string connectionString)
    {
        AuthResponse firstDeviceOriginal = await LoginAsync(
            database,
            InitialPassword,
            "password-device-a");
        LauncherDatabase.RefreshSessionResult firstDeviceRotation =
            await database.RefreshSessionAsync(firstDeviceOriginal.RefreshToken, None);
        Check(firstDeviceRotation.Response is not null,
            "The first password-change family archives one refresh proof.");
        AuthResponse firstDevice = firstDeviceRotation.Response!;
        AuthResponse secondDeviceOriginal = await LoginAsync(
            database,
            InitialPassword,
            "password-device-b");
        LauncherDatabase.RefreshSessionResult secondDeviceRotation =
            await database.RefreshSessionAsync(secondDeviceOriginal.RefreshToken, None);
        Check(secondDeviceRotation.Response is not null,
            "The second password-change family archives one refresh proof.");
        AuthResponse secondDevice = secondDeviceRotation.Response!;

        AuthResponse? replacement = await database.ChangePasswordAsync(
            AccountId,
            firstDevice.AccessToken,
            InitialPassword,
            ChangedPassword,
            None);
        Check(replacement is not null,
            "A valid current session changes the password and receives a replacement session.");
        Check(await database.AuthenticateAsync(firstDevice.AccessToken, None) is null
            && await database.AuthenticateAsync(secondDevice.AccessToken, None) is null,
            "Password replacement revokes every previously active device session.");
        Check(await ScalarInt64Async(
                connectionString,
                """
                SELECT COUNT(*)
                FROM atlas_launcher_refresh_history h
                INNER JOIN atlas_launcher_session s ON s.id = h.session_id
                WHERE s.account_id = @accountId
                  AND s.revoked_at IS NOT NULL;
                """,
                ("@accountId", AccountId)) == 0,
            "Password replacement purges refresh history for every revoked family.");
        Check(await database.AuthenticateAsync(replacement!.AccessToken, None) is not null,
            "The transaction leaves its newly issued replacement session active.");
        Check(await CountActiveSessionsAsync(connectionString) == 1,
            "Exactly one active session remains immediately after password replacement.");

        AtlasLoginResult oldPassword = await database.LoginAsync(
            new LoginRequest(DisplayUsername, InitialPassword, "old-password-check"),
            None);
        Check(oldPassword.Outcome == AtlasLoginOutcome.InvalidCredentials,
            "The old password is rejected after the transaction commits.");
        AuthResponse raceSession = await LoginAsync(
            database,
            ChangedPassword,
            "password-race-owner");

        Task<AuthResponse?> passwordChange = database.ChangePasswordAsync(
            AccountId,
            raceSession.AccessToken,
            ChangedPassword,
            FinalPassword,
            None);
        Task<AtlasLoginResult> concurrentOldLogin = database.LoginAsync(
            new LoginRequest(DisplayUsername, ChangedPassword, "password-race-login"),
            None);
        await Task.WhenAll(passwordChange, concurrentOldLogin);
        AuthResponse? finalReplacement = await passwordChange;
        AtlasLoginResult racedLogin = await concurrentOldLogin;

        Check(finalReplacement is not null,
            "Concurrent login cannot make a valid password-change transaction lose its replacement.");
        if (racedLogin.Response is not null)
        {
            Check(await database.AuthenticateAsync(racedLogin.Response.AccessToken, None) is null,
                "A login that serialized before password replacement is revoked by that replacement.");
        }
        else
        {
            Check(racedLogin.Outcome == AtlasLoginOutcome.InvalidCredentials,
                "A login that serialized after password replacement rejects the superseded password.");
        }
        Check(await database.AuthenticateAsync(finalReplacement!.AccessToken, None) is not null,
            "The final password replacement remains authenticated after the race.");
        Check(await CountActiveSessionsAsync(connectionString) == 1,
            "The password/login race converges to one active replacement session.");
        Check((await database.LoginAsync(
                new LoginRequest(DisplayUsername, ChangedPassword, "changed-password-check"),
                None)).Outcome == AtlasLoginOutcome.InvalidCredentials,
            "The intermediate password is rejected after the serialized race.");
        Check((await database.LoginAsync(
                new LoginRequest(DisplayUsername, FinalPassword, "final-password-check"),
                None)).Outcome == AtlasLoginOutcome.Succeeded,
            "The final password is accepted after the serialized race.");
    }

    private static async Task VerifyGlobalActiveSessionCapAsync(
        LauncherDatabase database,
        string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_session
            SET created_at = UTC_TIMESTAMP() - INTERVAL 2 DAY
            WHERE account_id = @accountId
              AND revoked_at IS NULL;
            """,
            ("@accountId", AccountId));
        AtlasLoginResult firstResult = await database.LoginAsync(
            new LoginRequest(DisplayUsername, InitialPassword, null),
            None);
        Check(firstResult.Outcome == AtlasLoginOutcome.Succeeded
            && firstResult.Response is not null,
            "A login without a device name succeeds before the global cap is exercised.");
        LauncherDatabase.RefreshSessionResult firstRotation =
            await database.RefreshSessionAsync(firstResult.Response!.RefreshToken, None);
        Check(firstRotation.Response is not null,
            "The oldest overflow fixture archives one refresh proof before it is pruned.");
        AuthResponse first = firstRotation.Response!;
        byte[] firstFamilyId = await ReadBytesAsync(
            connectionString,
            "SELECT id FROM atlas_launcher_session WHERE refresh_hash = @hash;",
            ("@hash", TokenService.Hash(first.RefreshToken)));
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_session
            SET created_at = UTC_TIMESTAMP() - INTERVAL 1 DAY
            WHERE access_hash = @hash;
            """,
            ("@hash", TokenService.Hash(first.AccessToken)));

        AuthResponse latest = first;
        for (int index = 0; index < LauncherDatabase.MaximumActiveSessionsPerAccount; index++)
        {
            AtlasLoginResult result = await database.LoginAsync(
                new LoginRequest(DisplayUsername, InitialPassword, null),
                None);
            Check(result.Outcome == AtlasLoginOutcome.Succeeded
                && result.Response is not null,
                $"Device-less login {index + 2} succeeds under the account transaction.");
            latest = result.Response!;
        }

        Check(await CountActiveSessionsAsync(connectionString)
            == LauncherDatabase.MaximumActiveSessionsPerAccount,
            "Device-less logins cannot grow the account beyond the global active-session cap.");
        Check(await database.AuthenticateAsync(first.AccessToken, None) is null,
            "The oldest device-less session is revoked when the global cap overflows.");
        Check(await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE session_id = @id;",
                ("@id", firstFamilyId)) == 0,
            "Global active-session overflow purges the oldest revoked family's history.");
        Check(await database.AuthenticateAsync(latest.AccessToken, None) is not null,
            "The newly issued session is never selected as overflow cleanup.");
        IReadOnlyList<LauncherSessionInfo> listed = await database.ListSessionsAsync(
            AccountId,
            latest.AccessToken,
            None);
        Check(listed.Count == LauncherDatabase.SessionListLimit
            && listed.Any(session => session.Current)
            && listed.All(session => session.DeviceName == "Appareil inconnu"),
            "Session listing is SQL-bounded and uses one controlled label for missing device names.");
    }

    private static async Task VerifySessionGarbageCollectionAsync(
        LauncherDatabase database,
        string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            "DELETE FROM atlas_launcher_session WHERE account_id = @accountId;",
            ("@accountId", AccountId));
        _ = await LoginAsync(database, FinalPassword, "gc-active-baseline");

        List<byte[]> capped = [];
        for (int index = 0; index < LauncherDatabase.MaximumSessionTombstonesPerAccount; index++)
        {
            capped.Add(await InsertGarbageCollectionSessionAsync(
                connectionString,
                AccountId,
                $"tombstone-cap-{index:D3}",
                revokedAt: DateTime.UtcNow.AddSeconds(-(200 - index)),
                absoluteExpiresAt: DateTime.UtcNow.AddDays(1),
                accessExpiresAt: DateTime.UtcNow.AddHours(1)));
        }

        _ = await LoginAsync(database, FinalPassword, "gc-cap-exact");
        Check(await CountRevokedSessionsAsync(connectionString, AccountId)
            == LauncherDatabase.MaximumSessionTombstonesPerAccount,
            "Exactly 64 recent tombstones are retained without an off-by-one deletion.");

        byte[] newest = await InsertGarbageCollectionSessionAsync(
            connectionString,
            AccountId,
            "tombstone-cap-newest",
            revokedAt: DateTime.UtcNow,
            absoluteExpiresAt: DateTime.UtcNow.AddDays(1),
            accessExpiresAt: DateTime.UtcNow.AddHours(1));
        _ = await LoginAsync(database, FinalPassword, "gc-cap-overflow");
        Check(await CountRevokedSessionsAsync(connectionString, AccountId)
            == LauncherDatabase.MaximumSessionTombstonesPerAccount,
            "The 65th tombstone is pruned transactionally back to the exact account cap.");
        Check(await CountSessionByIdAsync(connectionString, capped[0]) == 0
            && await CountSessionByIdAsync(connectionString, newest) == 1,
            "The account cap removes the oldest tombstone and retains the newest proof.");

        Task<AtlasLoginResult>[] concurrentLogins = Enumerable.Range(0, 8)
            .Select(_ => database.LoginAsync(
                new LoginRequest(DisplayUsername, FinalPassword, "gc-concurrent-device"),
                None))
            .ToArray();
        AtlasLoginResult[] concurrentResults = await Task.WhenAll(concurrentLogins);
        Check(concurrentResults.All(result =>
                result.Outcome == AtlasLoginOutcome.Succeeded && result.Response is not null)
            && await CountRevokedSessionsAsync(connectionString, AccountId)
                == LauncherDatabase.MaximumSessionTombstonesPerAccount,
            "Concurrent same-device logins serialize on the account row and preserve the exact tombstone cap.");

        AuthResponse logoutProof = await LoginAsync(
            database,
            FinalPassword,
            "gc-logout-retention");
        LauncherDatabase.LogoutSessionResult? firstLogout = await database.LogoutSessionAsync(
            logoutProof.AccessToken,
            logoutProof.RefreshToken,
            None);
        LauncherDatabase.LogoutSessionResult? repeatedLogout = await database.LogoutSessionAsync(
            logoutProof.AccessToken,
            logoutProof.RefreshToken,
            None);
        Check(firstLogout is { RevokedNow: true }
            && repeatedLogout is { RevokedNow: false }
            && await CountRevokedSessionsAsync(connectionString, AccountId)
                == LauncherDatabase.MaximumSessionTombstonesPerAccount,
            "Logout retains its fresh tombstone for an idempotent retry while preserving the hard account cap.");

        AuthResponse replayOriginal = await LoginAsync(
            database,
            FinalPassword,
            "gc-concurrent-replay");
        LauncherDatabase.RefreshSessionResult replayRotation =
            await database.RefreshSessionAsync(replayOriginal.RefreshToken, None);
        Check(replayRotation.Response is not null,
            "The concurrent cap fixture rotates one family before replay.");
        AuthResponse concurrentLogout = await LoginAsync(
            database,
            FinalPassword,
            "gc-concurrent-logout");
        Task<LauncherDatabase.RefreshSessionResult> replayAtCap =
            database.RefreshSessionAsync(replayOriginal.RefreshToken, None);
        Task<LauncherDatabase.LogoutSessionResult?> logoutAtCap =
            database.LogoutSessionAsync(
                concurrentLogout.AccessToken,
                concurrentLogout.RefreshToken,
                None);
        await Task.WhenAll(replayAtCap, logoutAtCap);
        Check((await replayAtCap).ReplayDetected
            && (await logoutAtCap) is { RevokedNow: true }
            && await CountRevokedSessionsAsync(connectionString, AccountId)
                == LauncherDatabase.MaximumSessionTombstonesPerAccount,
            "Concurrent replay and logout serialize on the account row and keep exactly 64 tombstones.");

        await SeedGarbageCollectionAccountAsync(connectionString);
        await ExecuteAsync(
            connectionString,
            "DELETE FROM atlas_launcher_session WHERE account_id = @accountId;",
            ("@accountId", GarbageCollectionAccountId));

        List<byte[]> revoked = [];
        List<byte[]> expired = [];
        DateTime now = DateTime.UtcNow;
        byte[] advertisedAccess = await InsertGarbageCollectionSessionAsync(
            connectionString,
            GarbageCollectionAccountId,
            "gc-advertised-access",
            revokedAt: null,
            absoluteExpiresAt: now.AddHours(-4),
            accessExpiresAt: now.AddHours(1));
        for (int index = 0; index < LauncherDatabase.SessionGarbageCollectionBatchSize + 1; index++)
        {
            revoked.Add(await InsertGarbageCollectionSessionAsync(
                connectionString,
                GarbageCollectionAccountId,
                $"gc-revoked-{index:D3}",
                revokedAt: now.AddHours(-3).AddSeconds(index),
                absoluteExpiresAt: now.AddDays(1),
                accessExpiresAt: now.AddHours(1)));
            expired.Add(await InsertGarbageCollectionSessionAsync(
                connectionString,
                GarbageCollectionAccountId,
                $"gc-expired-{index:D3}",
                revokedAt: null,
                absoluteExpiresAt: now.AddHours(-3).AddSeconds(index),
                accessExpiresAt: now.AddHours(-2)));
        }

        byte[] blockedHistoryHash = TokenService.Hash("gc-unexpired-history-blocker");
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO atlas_launcher_refresh_history
                (token_hash, session_id, expires_at, consumed_at)
            VALUES
                (@hash, @sessionId, UTC_TIMESTAMP() + INTERVAL 1 HOUR, UTC_TIMESTAMP(6));
            """,
            ("@hash", blockedHistoryHash),
            ("@sessionId", expired[0]));

        _ = await LoginAsync(database, FinalPassword, "gc-global-first-batch");
        Check(await CountSessionsByDevicePrefixAsync(
                connectionString,
                GarbageCollectionAccountId,
                "gc-revoked-") == 1
            && await CountSessionByIdAsync(connectionString, revoked[^1]) == 1,
            "One login deletes exactly one deterministic batch of old revoked sessions.");
        Check(await CountSessionsByDevicePrefixAsync(
                connectionString,
                GarbageCollectionAccountId,
                "gc-expired-") == 2
            && await CountSessionByIdAsync(connectionString, expired[0]) == 1
            && await CountSessionByIdAsync(connectionString, expired[^1]) == 1
            && await CountSessionByIdAsync(connectionString, advertisedAccess) == 1
            && await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE token_hash = @hash;",
                ("@hash", blockedHistoryHash)) == 1,
            "Expired-session GC examines exactly 64 parents: the blocked first family and the unselected 65th family remain.");

        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_refresh_history
            SET expires_at = UTC_TIMESTAMP() - INTERVAL 1 MINUTE
            WHERE token_hash = @hash;
            """,
            ("@hash", blockedHistoryHash));
        _ = await LoginAsync(database, FinalPassword, "gc-global-second-batch");
        Check(await CountSessionsByDevicePrefixAsync(
                connectionString,
                GarbageCollectionAccountId,
                "gc-revoked-") == 0
            && await CountSessionsByDevicePrefixAsync(
                connectionString,
                GarbageCollectionAccountId,
                "gc-expired-") == 0
            && await CountSessionByIdAsync(connectionString, advertisedAccess) == 1
            && await ScalarInt64Async(
                connectionString,
                "SELECT COUNT(*) FROM atlas_launcher_refresh_history WHERE token_hash = @hash;",
                ("@hash", blockedHistoryHash)) == 0,
            "The next bounded pass drains expired history first, then removes the now-empty parent and remaining revoked row.");

        await ExecuteAsync(
            connectionString,
            "DELETE FROM atlas_launcher_session WHERE account_id = @accountId;",
            ("@accountId", AccountId));
        int oversizedTombstoneBacklog =
            LauncherDatabase.MaximumSessionTombstonesPerAccount
            + LauncherDatabase.SessionGarbageCollectionBatchSize
            + 1;
        for (int index = 0; index < oversizedTombstoneBacklog; index++)
        {
            _ = await InsertGarbageCollectionSessionAsync(
                connectionString,
                AccountId,
                $"gc-tombstone-budget-{index:D3}",
                revokedAt: DateTime.UtcNow,
                absoluteExpiresAt: DateTime.UtcNow.AddDays(1),
                accessExpiresAt: DateTime.UtcNow.AddHours(1));
        }
        bool tombstoneBacklogRejected = false;
        try
        {
            _ = await LoginAsync(database, FinalPassword, "gc-tombstone-budget-login");
        }
        catch (InvalidOperationException)
        {
            tombstoneBacklogRejected = true;
        }
        Check(tombstoneBacklogRejected
            && await CountRevokedSessionsAsync(connectionString, AccountId)
                == oversizedTombstoneBacklog
            && await CountActiveSessionsAsync(connectionString) == 0,
            "A tombstone backlog beyond the 64-row repair budget aborts issuance without partial mutation.");

        await ExecuteAsync(
            connectionString,
            "DELETE FROM atlas_launcher_session WHERE account_id = @accountId;",
            ("@accountId", AccountId));
        for (int index = 0; index < LauncherDatabase.AccountSessionRepairProbeSize; index++)
        {
            _ = await InsertGarbageCollectionSessionAsync(
                connectionString,
                AccountId,
                $"gc-active-budget-{index:D3}",
                revokedAt: null,
                absoluteExpiresAt: DateTime.UtcNow.AddDays(1),
                accessExpiresAt: DateTime.UtcNow.AddHours(1));
        }
        bool activeBacklogRejected = false;
        try
        {
            _ = await LoginAsync(database, FinalPassword, "gc-active-budget-login");
        }
        catch (InvalidOperationException)
        {
            activeBacklogRejected = true;
        }
        Check(activeBacklogRejected
            && await CountActiveSessionsAsync(connectionString)
                == LauncherDatabase.AccountSessionRepairProbeSize,
            "An active backlog beyond the fixed probe budget rejects the new row and leaves the pre-existing set unchanged.");

        await ExecuteAsync(
            connectionString,
            "DELETE FROM atlas_launcher_session WHERE account_id = @accountId;",
            ("@accountId", AccountId));
        for (int index = 0; index < LauncherDatabase.SessionRevocationBatchSize + 1; index++)
        {
            _ = await InsertGarbageCollectionSessionAsync(
                connectionString,
                AccountId,
                "gc-revocation-budget",
                revokedAt: null,
                absoluteExpiresAt: DateTime.UtcNow.AddDays(1),
                accessExpiresAt: DateTime.UtcNow.AddHours(1));
        }
        bool revocationBacklogRejected = false;
        try
        {
            _ = await LoginAsync(database, FinalPassword, "gc-revocation-budget");
        }
        catch (InvalidOperationException)
        {
            revocationBacklogRejected = true;
        }
        Check(revocationBacklogRejected
            && await CountActiveSessionsAsync(connectionString)
                == LauncherDatabase.SessionRevocationBatchSize + 1,
            "A matching-device revocation set beyond 64 aborts before any session is revoked.");
    }

    private static async Task VerifyGarbageCollectionLogoutBarrierAsync(
        LauncherDatabase database,
        string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            "DELETE FROM atlas_launcher_session WHERE account_id = @accountId;",
            ("@accountId", AccountId));
        AuthResponse expiring = await LoginAsync(
            database,
            FinalPassword,
            "gc-logout-barrier");
        await ExecuteAsync(
            connectionString,
            """
            UPDATE atlas_launcher_session
            SET absolute_expires_at = UTC_TIMESTAMP() - INTERVAL 2 HOUR,
                refresh_expires_at = UTC_TIMESTAMP() - INTERVAL 2 HOUR,
                access_expires_at = UTC_TIMESTAMP() + INTERVAL 1 HOUR
            WHERE access_hash = @hash;
            """,
            ("@hash", TokenService.Hash(expiring.AccessToken)));
        byte[] oldTombstone = await InsertGarbageCollectionSessionAsync(
            connectionString,
            AccountId,
            "gc-revoked-barrier",
            revokedAt: DateTime.UtcNow.AddHours(-2),
            absoluteExpiresAt: DateTime.UtcNow.AddDays(1),
            accessExpiresAt: DateTime.UtcNow.AddHours(1));

        await using MySqlConnection blocker = new(connectionString);
        await blocker.OpenAsync(None);
        await using MySqlTransaction blockerTransaction =
            await blocker.BeginTransactionAsync(IsolationLevel.ReadCommitted, None);
        await using (MySqlCommand lockTombstone = blocker.CreateCommand())
        {
            lockTombstone.Transaction = blockerTransaction;
            lockTombstone.CommandText =
                "SELECT id FROM atlas_launcher_session WHERE id = @id FOR UPDATE;";
            lockTombstone.Parameters.Add("@id", MySqlDbType.Binary, 16).Value = oldTombstone;
            _ = await lockTombstone.ExecuteScalarAsync(None);
        }

        Task<LauncherDatabase.LogoutSessionResult?> logout = database.LogoutSessionAsync(
            expiring.AccessToken,
            expiring.RefreshToken,
            None);
        await Task.Delay(100, None);
        Check(!logout.IsCompleted,
            "The barrier holds logout after it locks the expired family and reaches the tombstone probe.");

        Task<AtlasLoginResult> cleanupLogin = database.LoginAsync(
            new LoginRequest(DisplayUsername, FinalPassword, "gc-cleanup-barrier"),
            None);
        await Task.Delay(100, None);
        await blockerTransaction.CommitAsync(None);

        LauncherDatabase.LogoutSessionResult? logoutResult =
            await logout.WaitAsync(TimeSpan.FromSeconds(10));
        AtlasLoginResult loginResult =
            await cleanupLogin.WaitAsync(TimeSpan.FromSeconds(10));
        Check(logoutResult is { RevokedNow: true }
            && loginResult.Outcome == AtlasLoginOutcome.Succeeded
            && loginResult.Response is not null,
            "GC and logout cross the expired-to-revoked transition without a lock cycle.");
    }

    private static async Task<AuthResponse> LoginAsync(
        LauncherDatabase database,
        string password,
        string device)
    {
        AtlasLoginResult result = await database.LoginAsync(
            new LoginRequest(DisplayUsername, password, device),
            None);
        Check(result.Outcome == AtlasLoginOutcome.Succeeded && result.Response is not null,
            $"Fixture login on {device} succeeds.");
        return result.Response!;
    }

    private static LauncherServerOptions Options(
        MySqlConnectionStringBuilder connection,
        uint maximumVersion)
        => new()
        {
            ConnectionString = connection.ConnectionString,
            CharacterDatabaseName = connection.Database,
            WorldDatabaseName = connection.Database,
            MaximumSchemaVersion = maximumVersion,
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30
        };

    private static MySqlConnectionStringBuilder ReadSafeConnection()
    {
        string supplied = Environment.GetEnvironmentVariable(ConnectionVariable)
            ?? throw new InvalidOperationException(
                $"{ConnectionVariable} must identify a disposable local MySQL database.");
        MySqlConnectionStringBuilder connection = new(supplied);
        if (!connection.Database.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            || !SafeIdentifier().IsMatch(connection.Database))
        {
            throw new InvalidOperationException(
                $"The test database name must start with {DatabasePrefix} and contain only ASCII letters, digits and underscores.");
        }
        if (!IsLoopbackServer(connection.Server))
        {
            throw new InvalidOperationException(
                "Auth session tests refuse non-loopback MySQL servers.");
        }
        return connection;
    }

    private static bool IsLoopbackServer(string server)
        => string.Equals(server, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(server, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(server, "::1", StringComparison.Ordinal)
            || string.Equals(server, "[::1]", StringComparison.Ordinal);

    private static async Task ResetSchemaAsync(string connectionString)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(None);
        List<string> tables = [];
        await using (MySqlCommand list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT TABLE_NAME
                FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = DATABASE();
                """;
            await using MySqlDataReader reader = await list.ExecuteReaderAsync(None);
            while (await reader.ReadAsync(None))
                tables.Add(reader.GetString(0));
        }

        if (tables.Any(table => !SafeIdentifier().IsMatch(table)))
            throw new InvalidOperationException("The disposable schema contains an unsafe table name.");

        await ExecuteAsync(connection, "SET FOREIGN_KEY_CHECKS = 0;");
        try
        {
            foreach (string table in tables)
                await ExecuteAsync(connection, $"DROP TABLE IF EXISTS `{table}`;");
        }
        finally
        {
            await ExecuteAsync(connection, "SET FOREIGN_KEY_CHECKS = 1;");
        }
    }

    private static async Task CreateAzerothCoreFixtureAsync(string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            """
            CREATE TABLE account (
                id INT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
                username VARCHAR(32) NOT NULL UNIQUE,
                salt BINARY(32) NOT NULL,
                verifier BINARY(32) NOT NULL,
                session_key VARBINARY(40) NULL,
                email VARCHAR(254) NOT NULL DEFAULT '',
                reg_mail VARCHAR(254) NOT NULL DEFAULT '',
                joindate DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                expansion TINYINT UNSIGNED NOT NULL DEFAULT 2
            ) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

            CREATE TABLE hermes_bnet_credentials (
                username VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                srp_version TINYINT UNSIGNED NOT NULL,
                salt BINARY(32) NOT NULL,
                verifier VARBINARY(256) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
            """);
    }

    private static async Task SeedAccountAsync(string connectionString)
    {
        (byte[] legacySalt, byte[] legacyVerifier) =
            SrpCredentials.MakeLegacy(Username, InitialPassword);
        (byte[] modernSalt, byte[] modernVerifier) =
            SrpCredentials.MakeModern(Username, InitialPassword);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO account
                (id, username, salt, verifier, email, reg_mail)
            VALUES
                (@id, @username, @legacySalt, @legacyVerifier, @email, @email);

            INSERT INTO hermes_bnet_credentials
                (username, srp_version, salt, verifier)
            VALUES
                (@username, 2, @modernSalt, @modernVerifier);

            INSERT INTO atlas_launcher_profile
                (account_id, display_username, email_normalized)
            VALUES
                (@id, @displayUsername, @email);
            """,
            ("@id", AccountId),
            ("@username", Username),
            ("@displayUsername", DisplayUsername),
            ("@email", "AUTH-FIXTURE@EXAMPLE.TEST"),
            ("@legacySalt", legacySalt),
            ("@legacyVerifier", legacyVerifier),
            ("@modernSalt", modernSalt),
            ("@modernVerifier", modernVerifier));
    }

    private static async Task SeedGarbageCollectionAccountAsync(string connectionString)
    {
        await ExecuteAsync(
            connectionString,
            """
            INSERT IGNORE INTO account
                (id, username, salt, verifier, email, reg_mail)
            VALUES
                (@id, 'ATLASGCFIXTURE', @salt, @verifier,
                 'ATLAS-GC@EXAMPLE.TEST', 'ATLAS-GC@EXAMPLE.TEST');

            INSERT IGNORE INTO atlas_launcher_profile
                (account_id, display_username, email_normalized)
            VALUES
                (@id, 'AtlasGcFixture', 'ATLAS-GC@EXAMPLE.TEST');
            """,
            ("@id", GarbageCollectionAccountId),
            ("@salt", new byte[32]),
            ("@verifier", new byte[32]));
    }

    private static async Task<byte[]> InsertGarbageCollectionSessionAsync(
        string connectionString,
        uint accountId,
        string deviceName,
        DateTime? revokedAt,
        DateTime absoluteExpiresAt,
        DateTime accessExpiresAt)
    {
        byte[] id = Guid.NewGuid().ToByteArray();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO atlas_launcher_session
                (id, account_id, access_hash, refresh_hash, device_name,
                 access_expires_at, refresh_expires_at, absolute_expires_at,
                 revoked_at, created_at, updated_at)
            VALUES
                (@id, @accountId, @accessHash, @refreshHash, @deviceName,
                 @accessExpiresAt, @absoluteExpiresAt, @absoluteExpiresAt,
                 @revokedAt, @createdAt, @updatedAt);
            """,
            ("@id", id),
            ("@accountId", accountId),
            ("@accessHash", TokenService.Hash($"gc-access-{Guid.NewGuid():N}")),
            ("@refreshHash", TokenService.Hash($"gc-refresh-{Guid.NewGuid():N}")),
            ("@deviceName", deviceName),
            ("@accessExpiresAt", accessExpiresAt),
            ("@absoluteExpiresAt", absoluteExpiresAt),
            ("@revokedAt", (object?)revokedAt ?? DBNull.Value),
            ("@createdAt", DateTime.UtcNow.AddDays(-1)),
            ("@updatedAt", (object?)revokedAt ?? DateTime.UtcNow.AddDays(-1)));
        return id;
    }

    private static Task<long> CountRevokedSessionsAsync(
        string connectionString,
        uint accountId)
        => ScalarInt64Async(
            connectionString,
            """
            SELECT COUNT(*)
            FROM atlas_launcher_session
            WHERE account_id = @accountId
              AND revoked_at IS NOT NULL;
            """,
            ("@accountId", accountId));

    private static Task<long> CountSessionByIdAsync(string connectionString, byte[] sessionId)
        => ScalarInt64Async(
            connectionString,
            "SELECT COUNT(*) FROM atlas_launcher_session WHERE id = @id;",
            ("@id", sessionId));

    private static Task<long> CountSessionsByDevicePrefixAsync(
        string connectionString,
        uint accountId,
        string prefix)
        => ScalarInt64Async(
            connectionString,
            """
            SELECT COUNT(*)
            FROM atlas_launcher_session
            WHERE account_id = @accountId
              AND device_name LIKE CONCAT(@prefix, '%');
            """,
            ("@accountId", accountId),
            ("@prefix", prefix));

    private static async Task InsertVersion8SessionAsync(
        string connectionString,
        byte[] familyId,
        SessionTokens tokens,
        string deviceName)
    {
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO atlas_launcher_session
                (id, account_id, access_hash, refresh_hash, device_name,
                 access_expires_at, refresh_expires_at)
            VALUES
                (@id, @accountId, @accessHash, @refreshHash, @deviceName,
                 @accessExpiresAt, @refreshExpiresAt);
            """,
            ("@id", familyId),
            ("@accountId", AccountId),
            ("@accessHash", tokens.AccessHash),
            ("@refreshHash", tokens.RefreshHash),
            ("@deviceName", deviceName),
            ("@accessExpiresAt", tokens.AccessExpiresAt.UtcDateTime),
            ("@refreshExpiresAt", tokens.RefreshExpiresAt.UtcDateTime));
    }

    private static async Task SeedExpiredRefreshHistoryAsync(
        string connectionString,
        byte[] familyId,
        int count)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(None);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO atlas_launcher_refresh_history
                (token_hash, session_id, expires_at, consumed_at)
            VALUES
            """ + string.Join(
                ",\n",
                Enumerable.Range(0, count).Select(index =>
                    $"(@token{index}, @sessionId, UTC_TIMESTAMP() - INTERVAL 1 DAY, UTC_TIMESTAMP(6))"));
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = familyId;
        foreach (int index in Enumerable.Range(0, count))
        {
            command.Parameters.Add($"@token{index}", MySqlDbType.Binary, 32).Value =
                TokenService.Hash($"fixture-expired-history-{index}");
        }
        await command.ExecuteNonQueryAsync(None);
    }

    private static async Task SeedLiveRefreshHistoryAsync(
        string connectionString,
        byte[] familyId,
        int count)
    {
        const int batchSize = 256;
        string familyKey = Convert.ToHexString(familyId);
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(None);
        for (int offset = 0; offset < count; offset += batchSize)
        {
            int currentBatchSize = Math.Min(batchSize, count - offset);
            await using MySqlCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO atlas_launcher_refresh_history
                    (token_hash, session_id, expires_at, consumed_at)
                VALUES
                """ + string.Join(
                    ",\n",
                    Enumerable.Range(0, currentBatchSize).Select(index =>
                        $"(@token{index}, @sessionId, UTC_TIMESTAMP() + INTERVAL 1 DAY, UTC_TIMESTAMP(6))"));
            command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = familyId;
            foreach (int index in Enumerable.Range(0, currentBatchSize))
            {
                command.Parameters.Add($"@token{index}", MySqlDbType.Binary, 32).Value =
                    TokenService.Hash($"fixture-live-history-{familyKey}-{offset + index}");
            }
            await command.ExecuteNonQueryAsync(None);
        }
    }

    private static Task<long> CountExpiredHistoryAsync(string connectionString)
        => ScalarInt64Async(
            connectionString,
            """
            SELECT COUNT(*)
            FROM atlas_launcher_refresh_history
            WHERE expires_at <= UTC_TIMESTAMP();
            """);

    private static async Task<long> CountActiveSessionsAsync(string connectionString)
        => await ScalarInt64Async(
            connectionString,
            """
            SELECT COUNT(*)
            FROM atlas_launcher_session
            WHERE account_id = @accountId
              AND revoked_at IS NULL
              AND refresh_expires_at > UTC_TIMESTAMP()
              AND absolute_expires_at > UTC_TIMESTAMP();
            """,
            ("@accountId", AccountId));

    private static async Task<long> ScalarInt64Async(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(None);
        await using MySqlCommand command = CreateCommand(connection, sql, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(None), CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset> ReadDateTimeAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(None);
        await using MySqlCommand command = CreateCommand(connection, sql, parameters);
        DateTime value = Convert.ToDateTime(
            await command.ExecuteScalarAsync(None),
            CultureInfo.InvariantCulture);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static async Task<byte[]> ReadBytesAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(None);
        await using MySqlCommand command = CreateCommand(connection, sql, parameters);
        return (byte[])(await command.ExecuteScalarAsync(None)
            ?? throw new InvalidOperationException("Expected binary fixture row was absent."));
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using MySqlConnection connection = new(connectionString);
        await connection.OpenAsync(None);
        await using MySqlCommand command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(None);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, string sql)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(None);
    }

    private static MySqlCommand CreateCommand(
        MySqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        MySqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static DateTimeOffset TruncateToSecond(DateTimeOffset value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Offset);

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
