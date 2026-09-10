using System.Data;
using System.Security.Cryptography;
using MySqlConnector;
using WotLK.Launcher.Server.Database;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal const int RefreshHistoryCleanupBatchSize = 256;
    internal const int SessionGarbageCollectionBatchSize = 64;
    internal const int SessionTombstoneRetentionMinutes = 60;
    internal const int MaximumSessionTombstonesPerAccount = 64;
    internal const int SessionRevocationBatchSize = 64;
    internal const int AccountSessionRepairProbeSize =
        MaximumActiveSessionsPerAccount + SessionRevocationBatchSize + 1;
    internal const int AuthTransactionDeadlockRetryLimit = 3;
    internal const int MaximumRefreshRotationsPerFamily = 4096;
    internal const int MaximumActiveSessionsPerAccount = 12;
    internal const int SessionListLimit = MaximumActiveSessionsPerAccount;
    internal const string RefreshHistoryCleanupSql = """
        SELECT token_hash
        FROM atlas_launcher_refresh_history
            FORCE INDEX (ix_atlas_refresh_history_expiry)
        WHERE expires_at <= UTC_TIMESTAMP()
        ORDER BY expires_at ASC, token_hash ASC
        LIMIT @batchLimit
        FOR UPDATE SKIP LOCKED;
        """;
    internal const string RefreshHistoryDeleteSql = """
        DELETE FROM atlas_launcher_refresh_history
        WHERE token_hash = @tokenHash;
        """;
    internal const string RevokedSessionCleanupSql = """
        SELECT id
        FROM atlas_launcher_session
            FORCE INDEX (ix_atlas_session_revoked_gc)
        WHERE revoked_at IS NOT NULL
          AND revoked_at <= UTC_TIMESTAMP() - INTERVAL @retentionMinutes MINUTE
        ORDER BY revoked_at ASC, id ASC
        LIMIT @batchLimit
        FOR UPDATE SKIP LOCKED;
        """;
    internal const string ExpiredSessionCleanupSql = """
        SELECT id
        FROM atlas_launcher_session
            FORCE INDEX (ix_atlas_session_expired_gc)
        WHERE revoked_at IS NULL
          AND absolute_expires_at <= UTC_TIMESTAMP() - INTERVAL @retentionMinutes MINUTE
          AND access_expires_at <= UTC_TIMESTAMP()
        ORDER BY absolute_expires_at ASC, id ASC
        LIMIT @batchLimit
        FOR UPDATE SKIP LOCKED;
        """;
    internal const string RefreshHistoryExistenceLockSql = """
        SELECT 1
        FROM atlas_launcher_refresh_history
            FORCE INDEX (ix_atlas_refresh_history_session)
        WHERE session_id = @sessionId
        LIMIT 1
        FOR UPDATE;
        """;
    internal const string RevokedSessionDeleteSql = """
        DELETE FROM atlas_launcher_session
        WHERE id = @sessionId
          AND revoked_at IS NOT NULL
          AND revoked_at <= UTC_TIMESTAMP() - INTERVAL @retentionMinutes MINUTE;
        """;
    internal const string ExpiredSessionDeleteSql = """
        DELETE FROM atlas_launcher_session
        WHERE id = @sessionId
          AND revoked_at IS NULL
          AND absolute_expires_at <= UTC_TIMESTAMP() - INTERVAL @retentionMinutes MINUTE
          AND access_expires_at <= UTC_TIMESTAMP();
        """;
    internal const string ReplayHistoryLocatorSql = """
        SELECT session_id
        FROM atlas_launcher_refresh_history
        WHERE token_hash = @tokenHash
          AND expires_at > UTC_TIMESTAMP()
        LIMIT 1;
        """;
    internal const string ReplaySessionLockSql = """
        SELECT id, account_id, device_name, created_at, refresh_hash,
               refresh_expires_at > UTC_TIMESTAMP() AS refresh_is_valid,
               absolute_expires_at
        FROM atlas_launcher_session
        WHERE id = @sessionId
          AND account_id = @accountId
          AND revoked_at IS NULL
          AND absolute_expires_at > UTC_TIMESTAMP()
        FOR UPDATE;
        """;
    internal const string ReplayHistoryRevalidationSql = """
        SELECT 1
        FROM atlas_launcher_refresh_history
        WHERE token_hash = @tokenHash
          AND session_id = @sessionId
          AND expires_at > UTC_TIMESTAMP()
          AND @absoluteExpiresAt > UTC_TIMESTAMP()
        LIMIT 1
        FOR UPDATE;
        """;
    internal const string RefreshHistoryCountSql = """
        SELECT COUNT(*)
        FROM atlas_launcher_refresh_history
            FORCE INDEX (ix_atlas_refresh_history_session)
        WHERE session_id = @sessionId;
        """;
    internal const string PurgeRefreshHistoryBySessionSql = """
        DELETE FROM atlas_launcher_refresh_history
        WHERE session_id = @sessionId;
        """;
    internal const string ActiveSessionProbeSql = """
        SELECT id
        FROM atlas_launcher_session
            FORCE INDEX (ix_atlas_session_account_active)
        WHERE account_id = @accountId
          AND revoked_at IS NULL
          AND refresh_expires_at > UTC_TIMESTAMP()
        ORDER BY refresh_expires_at ASC, id ASC
        LIMIT @probeLimit
        FOR UPDATE;
        """;
    private const string LegacyActiveSessionProbeSql = """
        SELECT id
        FROM atlas_launcher_session
        WHERE account_id = @accountId
          AND revoked_at IS NULL
          AND refresh_expires_at > UTC_TIMESTAMP()
        ORDER BY refresh_expires_at ASC, id ASC
        LIMIT @probeLimit
        FOR UPDATE;
        """;
    internal const string ExcessSessionTombstoneIdsSql = """
        SELECT id
        FROM atlas_launcher_session
            FORCE INDEX (ix_atlas_session_account_revoked)
        WHERE account_id = @accountId
          AND revoked_at IS NOT NULL
        ORDER BY revoked_at ASC, id ASC
        LIMIT @probeLimit
        FOR UPDATE;
        """;
    internal const string ListSessionsSql = """
        SELECT HEX(id) AS session_id, device_name, created_at, updated_at,
               refresh_expires_at, access_hash
        FROM atlas_launcher_session
            FORCE INDEX (ix_atlas_session_account_active)
        WHERE account_id = @accountId
          AND revoked_at IS NULL
          AND refresh_expires_at > UTC_TIMESTAMP()
        ORDER BY updated_at DESC, created_at DESC, id DESC
        LIMIT @sessionLimit;
        """;
    private const string LegacyListSessionsSql = """
        SELECT HEX(id) AS session_id, device_name, created_at, updated_at,
               refresh_expires_at, access_hash
        FROM atlas_launcher_session
        WHERE account_id = @accountId
          AND revoked_at IS NULL
          AND refresh_expires_at > UTC_TIMESTAMP()
        ORDER BY updated_at DESC, created_at DESC, id DESC
        LIMIT @sessionLimit;
        """;

    private readonly LauncherServerOptions _options;
    private readonly TokenService _tokens;
    private readonly LauncherSchemaMigrator _schemaMigrator;

    internal bool SocialProfilesAvailable =>
        _options.MaximumSchemaVersion is null or >= 5;

    internal bool SessionFamiliesAvailable =>
        _options.MaximumSchemaVersion is null or >= 9;

    internal bool SessionGarbageCollectionAvailable =>
        _options.MaximumSchemaVersion is null or >= 10;

    internal LauncherDatabase(
        LauncherServerOptions options,
        TokenService tokens,
        LauncherSchemaMigrator schemaMigrator)
    {
        _options = options;
        _tokens = tokens;
        _schemaMigrator = schemaMigrator;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _schemaMigrator.MigrateAsync(cancellationToken);
    }

    public async Task<AuthResponse> RegisterAsync(
        RegisterRequest request,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Email);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Password);
        string displayUsername = request.Username.Trim();
        string username = displayUsername.ToUpperInvariant();
        string email = request.Email.Trim();
        if (email.Length > AuthenticationInputValidation.MaximumEmailLength)
            throw new ArgumentOutOfRangeException(nameof(request), "L'adresse e-mail est trop longue.");
        string normalizedEmail = email.ToUpperInvariant();
        (byte[] legacySalt, byte[] legacyVerifier) = SrpCredentials.MakeLegacy(username, request.Password);
        (byte[] modernSalt, byte[] modernVerifier) = SrpCredentials.MakeModern(username, request.Password);

        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await DeleteStaleSessionsBatchAsync(connection, cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (await AccountExistsAsync(connection, transaction, username, normalizedEmail, cancellationToken))
            throw new DuplicateNameException("Ce nom d'utilisateur ou cette adresse e-mail est déjà utilisé.");

        uint accountId;
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO account
                    (username, salt, verifier, email, reg_mail, joindate, expansion)
                VALUES
                    (@username, @salt, @verifier, @email, @email, UTC_TIMESTAMP(), 2);
                SELECT LAST_INSERT_ID();
                """;
            command.Parameters.AddWithValue("@username", username);
            command.Parameters.Add("@salt", MySqlDbType.Binary, 32).Value = legacySalt;
            command.Parameters.Add("@verifier", MySqlDbType.Binary, 32).Value = legacyVerifier;
            command.Parameters.AddWithValue("@email", normalizedEmail);
            accountId = Convert.ToUInt32(await command.ExecuteScalarAsync(cancellationToken));
        }

        await UpsertModernCredentialAsync(
            connection, transaction, username, modernSalt, modernVerifier, cancellationToken);

        await using (MySqlCommand profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = """
                INSERT INTO atlas_launcher_profile
                    (account_id, display_username, email_normalized)
                VALUES
                    (@accountId, @displayUsername, @email);
                """;
            profile.Parameters.AddWithValue("@accountId", accountId);
            profile.Parameters.AddWithValue("@displayUsername", displayUsername);
            profile.Parameters.AddWithValue("@email", normalizedEmail);
            await profile.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (MySqlCommand realms = connection.CreateCommand())
        {
            realms.Transaction = transaction;
            realms.CommandText = """
                INSERT IGNORE INTO realmcharacters (realmid, acctid, numchars)
                SELECT id, @accountId, 0 FROM realmlist;
                """;
            realms.Parameters.AddWithValue("@accountId", accountId);
            await realms.ExecuteNonQueryAsync(cancellationToken);
        }

        SessionTokens session = _tokens.Create(_options.AccessTokenMinutes, _options.RefreshTokenDays);
        await InsertSessionAsync(
            connection,
            transaction,
            accountId,
            NormalizeDeviceName(deviceName),
            session,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AccountProfile accountProfile = new(
            accountId, displayUsername, email, false, null, false, false, 40, null);
        return ToAuthResponse(session, accountProfile);
    }

    public Task<AtlasLoginResult> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
        => ExecuteWithAuthDeadlockRetryAsync(
            retryToken => LoginOnceAsync(request, retryToken),
            cancellationToken);

    private async Task<AtlasLoginResult> LoginOnceAsync(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        string username = (request.Username ?? string.Empty).Trim().ToUpperInvariant();
        string password = request.Password ?? string.Empty;
        bool inputValid = username.Length is >= 3 and <= 20
            && username.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
            && password.Length is >= 1 and <= 128;
        if (!inputValid)
        {
            SrpCredentials.PerformDummyModernVerification(
                username[..Math.Min(username.Length, 20)],
                password[..Math.Min(password.Length, 128)]);
            return new AtlasLoginResult(AtlasLoginOutcome.InvalidCredentials, null);
        }

        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await DeleteStaleSessionsBatchAsync(connection, cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        uint? accountId = await LockAccountByUsernameAsync(
            connection, transaction, username, cancellationToken);
        AccountCredential? credential = accountId is null
            ? null
            : await LoadCredentialByIdAsync(
                connection, transaction, accountId.Value, cancellationToken);
        if (credential is null)
        {
            SrpCredentials.PerformDummyModernVerification(username, password);
            return new AtlasLoginResult(AtlasLoginOutcome.InvalidCredentials, null);
        }

        bool valid = credential.ModernSalt is not null && credential.ModernVerifier is not null
            ? SrpCredentials.VerifyModern(
                credential.Username, password, credential.ModernSalt, credential.ModernVerifier)
            : SrpCredentials.VerifyLegacy(
                credential.Username, password, credential.LegacySalt, credential.LegacyVerifier);
        if (!valid)
            return new AtlasLoginResult(AtlasLoginOutcome.InvalidCredentials, null);

        if (!credential.HasAtlasProfile)
            return new AtlasLoginResult(AtlasLoginOutcome.AtlasAccountUnavailable, null);

        if (credential.ModernSalt is null || credential.ModernVerifier is null)
        {
            (byte[] salt, byte[] verifier) =
                SrpCredentials.MakeModern(credential.Username, password);
            await UpsertModernCredentialAsync(
                connection, transaction, credential.Username, salt, verifier, cancellationToken);
        }

        AccountProfile profile = await LoadProfileAsync(
            connection, transaction, credential.AccountId, cancellationToken);
        SessionTokens session = _tokens.Create(_options.AccessTokenMinutes, _options.RefreshTokenDays);
        string? deviceName = NormalizeDeviceName(request.DeviceName);
        if (deviceName is not null)
        {
            await RevokeActiveDeviceSessionsAsync(
                connection,
                transaction,
                credential.AccountId,
                deviceName,
                cancellationToken);
        }

        byte[] currentSessionId = await InsertSessionAsync(
            connection,
            transaction,
            credential.AccountId,
            deviceName,
            session,
            cancellationToken);
        await EnforceActiveSessionLimitAsync(
            connection,
            transaction,
            credential.AccountId,
            currentSessionId,
            cancellationToken);
        await EnforceSessionTombstoneLimitAsync(
            connection,
            transaction,
            credential.AccountId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AtlasLoginResult(
            AtlasLoginOutcome.Succeeded,
            ToAuthResponse(session, profile));
    }

    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        => (await RefreshSessionAsync(refreshToken, cancellationToken)).Response;

    internal Task<RefreshSessionResult> RefreshSessionAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (!TokenService.IsRefreshToken(refreshToken))
            return Task.FromResult(RefreshSessionResult.Invalid);

        return SessionFamiliesAvailable
            ? ExecuteWithAuthDeadlockRetryAsync(
                retryToken => RefreshFamilyAsync(refreshToken, retryToken),
                cancellationToken)
            : ExecuteWithAuthDeadlockRetryAsync(
                retryToken => RefreshLegacySessionAsync(refreshToken, retryToken),
                cancellationToken);
    }

    private async Task<RefreshSessionResult> RefreshFamilyAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        byte[] refreshHash = TokenService.Hash(refreshToken);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await DeleteExpiredRefreshHistoryBatchAsync(connection, cancellationToken);

        SessionLocator? locator = await LocateRefreshProofAsync(
            connection, refreshHash, cancellationToken);
        if (locator is null)
            return RefreshSessionResult.Invalid;

        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (!await LockAccountByIdAsync(
                connection, transaction, locator.AccountId, cancellationToken))
            return RefreshSessionResult.Invalid;

        SessionAccount? account = await LockRefreshFamilySessionAsync(
            connection, transaction, locator, cancellationToken);
        if (account is null)
            return RefreshSessionResult.Invalid;

        string? username = await LoadUsernameAsync(
            connection, transaction, account.AccountId, cancellationToken);
        if (username is null)
            return RefreshSessionResult.Invalid;
        account = account with { Username = username };

        bool isCurrentProof = account.RefreshHash is not null
            && CryptographicOperations.FixedTimeEquals(account.RefreshHash, refreshHash)
            && account.RefreshIsValid;
        if (!isCurrentProof)
        {
            if (!await RevalidateRefreshHistoryForUpdateAsync(
                    connection,
                    transaction,
                    refreshHash,
                    account.SessionId,
                    account.AbsoluteExpiresAt,
                    cancellationToken))
                return RefreshSessionResult.Invalid;

            bool revokedNow = await RevokeSessionByIdAsync(
                connection,
                transaction,
                account.AccountId,
                account.SessionId,
                cancellationToken);
            if (!revokedNow)
                return RefreshSessionResult.Invalid;

            await EnforceSessionTombstoneLimitAsync(
                connection,
                transaction,
                account.AccountId,
                cancellationToken,
                account.SessionId);
            await transaction.CommitAsync(cancellationToken);
            return RefreshSessionResult.Replay(account.Username);
        }

        long rotationCount = await CountRefreshHistoryAsync(
            connection,
            transaction,
            account.SessionId,
            cancellationToken);
        if (rotationCount >= MaximumRefreshRotationsPerFamily)
        {
            _ = await RevokeSessionByIdAsync(
                connection,
                transaction,
                account.AccountId,
                account.SessionId,
                cancellationToken);
            await EnforceSessionTombstoneLimitAsync(
                connection,
                transaction,
                account.AccountId,
                cancellationToken,
                account.SessionId);
            await transaction.CommitAsync(cancellationToken);
            return RefreshSessionResult.RotationLimitExceeded(account.Username);
        }

        string? deviceName = NormalizeDeviceName(account.DeviceName);
        if (deviceName is not null)
        {
            await RevokeOlderDeviceSessionsAsync(
                connection,
                transaction,
                account.AccountId,
                account.SessionId,
                account.CreatedAt,
                deviceName,
                cancellationToken);
        }

        SessionTokens session = _tokens.Create(
            _options.AccessTokenMinutes,
            _options.RefreshTokenDays,
            new DateTimeOffset(account.AbsoluteExpiresAt, TimeSpan.Zero));
        await ArchiveRefreshTokenAsync(
            connection,
            transaction,
            refreshHash,
            account.SessionId,
            account.AbsoluteExpiresAt,
            cancellationToken);
        await using (MySqlCommand rotate = connection.CreateCommand())
        {
            rotate.Transaction = transaction;
            rotate.CommandText = """
                UPDATE atlas_launcher_session
                SET access_hash = @accessHash,
                    refresh_hash = @refreshHash,
                    access_expires_at = @accessExpires,
                    refresh_expires_at = @refreshExpires,
                    updated_at = UTC_TIMESTAMP()
                WHERE id = @id;
                """;
            rotate.Parameters.Add("@accessHash", MySqlDbType.Binary, 32).Value = session.AccessHash;
            rotate.Parameters.Add("@refreshHash", MySqlDbType.Binary, 32).Value = session.RefreshHash;
            rotate.Parameters.AddWithValue("@accessExpires", session.AccessExpiresAt.UtcDateTime);
            rotate.Parameters.AddWithValue("@refreshExpires", session.RefreshExpiresAt.UtcDateTime);
            rotate.Parameters.Add("@id", MySqlDbType.Binary, 16).Value = account.SessionId;
            await rotate.ExecuteNonQueryAsync(cancellationToken);
        }

        AccountProfile profile = await LoadProfileAsync(
            connection, transaction, account.AccountId, cancellationToken);
        await EnforceSessionTombstoneLimitAsync(
            connection, transaction, account.AccountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RefreshSessionResult.Success(ToAuthResponse(session, profile));
    }

    private async Task<RefreshSessionResult> RefreshLegacySessionAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        byte[] refreshHash = TokenService.Hash(refreshToken);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        SessionLocator? locator = await LocateSessionByHashAsync(
            connection, "refresh_hash", refreshHash, cancellationToken);
        if (locator is null)
            return RefreshSessionResult.Invalid;

        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (!await LockAccountByIdAsync(
                connection, transaction, locator.AccountId, cancellationToken))
            return RefreshSessionResult.Invalid;

        SessionAccount? account = await LockLegacyRefreshSessionAsync(
            connection, transaction, locator, refreshHash, cancellationToken);
        if (account is null)
            return RefreshSessionResult.Invalid;

        string? username = await LoadUsernameAsync(
            connection, transaction, account.AccountId, cancellationToken);
        if (username is null)
            return RefreshSessionResult.Invalid;
        account = account with { Username = username };

        string? deviceName = NormalizeDeviceName(account.DeviceName);
        if (deviceName is not null)
        {
            await RevokeOlderDeviceSessionsAsync(
                connection,
                transaction,
                account.AccountId,
                account.SessionId,
                account.CreatedAt,
                deviceName,
                cancellationToken);
        }

        SessionTokens session = _tokens.Create(_options.AccessTokenMinutes, _options.RefreshTokenDays);
        await using (MySqlCommand rotate = connection.CreateCommand())
        {
            rotate.Transaction = transaction;
            rotate.CommandText = """
                UPDATE atlas_launcher_session
                SET access_hash = @accessHash,
                    refresh_hash = @refreshHash,
                    access_expires_at = @accessExpires,
                    refresh_expires_at = @refreshExpires,
                    updated_at = UTC_TIMESTAMP()
                WHERE id = @id;
                """;
            rotate.Parameters.Add("@accessHash", MySqlDbType.Binary, 32).Value = session.AccessHash;
            rotate.Parameters.Add("@refreshHash", MySqlDbType.Binary, 32).Value = session.RefreshHash;
            rotate.Parameters.AddWithValue("@accessExpires", session.AccessExpiresAt.UtcDateTime);
            rotate.Parameters.AddWithValue("@refreshExpires", session.RefreshExpiresAt.UtcDateTime);
            rotate.Parameters.Add("@id", MySqlDbType.Binary, 16).Value = account.SessionId;
            await rotate.ExecuteNonQueryAsync(cancellationToken);
        }

        AccountProfile profile = await LoadProfileAsync(
            connection, transaction, account.AccountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return RefreshSessionResult.Success(ToAuthResponse(session, profile));
    }

    public async Task<AuthenticatedAccount?> AuthenticateAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        byte[] hash = TokenService.Hash(accessToken);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.account_id, a.username
            FROM atlas_launcher_session s
            INNER JOIN atlas_launcher_profile p ON p.account_id = s.account_id
            INNER JOIN account a ON a.id = p.account_id
            WHERE s.access_hash = @hash
              AND s.revoked_at IS NULL
              AND s.access_expires_at > UTC_TIMESTAMP()
            LIMIT 1;
            """;
        command.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = hash;
        AuthenticatedAccount? account;
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            account = await reader.ReadAsync(cancellationToken)
                ? new AuthenticatedAccount(reader.GetUInt32("account_id"), reader.GetString("username"))
                : null;
        }

        if (account is not null)
        {
            // The authenticated friends poll keeps launcher presence alive even without a game character.
            // Throttling limits writes when several API requests share the same session.
            await using MySqlCommand touch = connection.CreateCommand();
            touch.CommandText = """
                UPDATE atlas_launcher_session
                SET updated_at = UTC_TIMESTAMP()
                WHERE access_hash = @hash
                  AND revoked_at IS NULL
                  AND access_expires_at > UTC_TIMESTAMP()
                  AND updated_at < UTC_TIMESTAMP() - INTERVAL 10 SECOND;
                """;
            touch.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = hash;
            await touch.ExecuteNonQueryAsync(cancellationToken);
        }

        return account;
    }

    public async Task<AccountProfile> GetProfileAsync(uint accountId, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        return await LoadProfileAsync(connection, null, accountId, cancellationToken);
    }

    public async Task<AccountProfile> ChangeAvatarAsync(
        uint accountId,
        string? avatarKey,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE atlas_launcher_profile
            SET avatar_key = @avatarKey
            WHERE account_id = @accountId;
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue(
            "@avatarKey",
            string.IsNullOrWhiteSpace(avatarKey) ? DBNull.Value : avatarKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await LoadProfileAsync(connection, null, accountId, cancellationToken);
    }

    public async Task<IReadOnlyList<LauncherSessionInfo>> ListSessionsAsync(
        uint accountId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        byte[] currentHash = TokenService.Hash(accessToken);
        List<LauncherSessionInfo> sessions = [];
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = SessionGarbageCollectionAvailable
            ? ListSessionsSql
            : LegacyListSessionsSql;
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.Add("@sessionLimit", MySqlDbType.Int32).Value = SessionListLimit;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new LauncherSessionInfo(
                reader.GetString("session_id").ToLowerInvariant(),
                reader.IsDBNull("device_name")
                    ? "Appareil inconnu"
                    : reader.GetString("device_name"),
                new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime("created_at"), DateTimeKind.Utc)),
                new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime("updated_at"), DateTimeKind.Utc)),
                new DateTimeOffset(
                    DateTime.SpecifyKind(reader.GetDateTime("refresh_expires_at"), DateTimeKind.Utc)),
                CryptographicOperations.FixedTimeEquals(
                    (byte[])reader["access_hash"],
                    currentHash)));
        }

        return sessions;
    }

    public Task<bool> RevokeSessionAsync(
        uint accountId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId.Length != 32 || !sessionId.All(Uri.IsHexDigit))
            return Task.FromResult(false);

        byte[] id = Convert.FromHexString(sessionId);
        return ExecuteWithAuthDeadlockRetryAsync(
            retryToken => RevokeSessionOnceAsync(accountId, id, retryToken),
            cancellationToken);
    }

    private async Task<bool> RevokeSessionOnceAsync(
        uint accountId,
        byte[] sessionId,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (!await LockAccountByIdAsync(
                connection, transaction, accountId, cancellationToken))
            return false;

        bool revokedNow = await RevokeSessionByIdAsync(
            connection, transaction, accountId, sessionId, cancellationToken);
        await EnforceSessionTombstoneLimitAsync(
            connection,
            transaction,
            accountId,
            cancellationToken,
            sessionId);

        await transaction.CommitAsync(cancellationToken);
        return revokedNow;
    }

    public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken)
        => _ = await LogoutSessionAsync(accessToken, null, cancellationToken);

    internal Task<LogoutSessionResult?> LogoutSessionAsync(
        string? accessToken,
        string? refreshToken,
        CancellationToken cancellationToken)
    {
        bool hasRefreshProof = TokenService.IsRefreshToken(refreshToken);
        if (!hasRefreshProof && string.IsNullOrWhiteSpace(accessToken))
            return Task.FromResult<LogoutSessionResult?>(null);

        return ExecuteWithAuthDeadlockRetryAsync(
            retryToken => LogoutSessionOnceAsync(
                accessToken,
                refreshToken,
                hasRefreshProof,
                retryToken),
            cancellationToken);
    }

    private async Task<LogoutSessionResult?> LogoutSessionOnceAsync(
        string? accessToken,
        string? refreshToken,
        bool hasRefreshProof,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        List<LogoutCandidate> candidates = [];
        if (hasRefreshProof)
        {
            byte[] refreshHash = TokenService.Hash(refreshToken!);
            SessionLocator? refreshLocator = SessionFamiliesAvailable
                ? await LocateRefreshProofAsync(connection, refreshHash, cancellationToken)
                : await LocateSessionByHashAsync(
                    connection, "refresh_hash", refreshHash, cancellationToken);
            if (refreshLocator is not null)
            {
                candidates.Add(new LogoutCandidate(
                    refreshLocator,
                    refreshHash,
                    IsRefreshProof: true));
            }
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            byte[] accessHash = TokenService.Hash(accessToken);
            SessionLocator? accessLocator = await LocateSessionByHashAsync(
                connection,
                "access_hash",
                accessHash,
                cancellationToken);
            if (accessLocator is not null)
            {
                candidates.Add(new LogoutCandidate(
                    accessLocator,
                    accessHash,
                    IsRefreshProof: false));
            }
        }

        foreach (LogoutCandidate candidate in candidates)
        {
            await using MySqlTransaction transaction =
                await connection.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted, cancellationToken);
            if (!await LockAccountByIdAsync(
                    connection,
                    transaction,
                    candidate.Locator.AccountId,
                    cancellationToken))
                continue;

            LogoutSession? session = await LockLogoutSessionAsync(
                connection, transaction, candidate.Locator, cancellationToken);
            if (session is null)
                continue;

            bool proofIsValid;
            if (candidate.IsRefreshProof)
            {
                proofIsValid = CryptographicOperations.FixedTimeEquals(
                    session.RefreshHash, candidate.TokenHash);
                if (!proofIsValid
                    && SessionFamiliesAvailable
                    && session.RevokedAt is null
                    && session.AbsoluteExpiresAt is { } absoluteExpiresAt)
                {
                    proofIsValid = await RevalidateRefreshHistoryForUpdateAsync(
                        connection,
                        transaction,
                        candidate.TokenHash,
                        session.SessionId,
                        absoluteExpiresAt,
                        cancellationToken);
                }
            }
            else
            {
                proofIsValid = CryptographicOperations.FixedTimeEquals(
                    session.AccessHash, candidate.TokenHash);
            }

            if (!proofIsValid)
                continue;

            string? username = await LoadUsernameAsync(
                connection, transaction, session.AccountId, cancellationToken);
            if (username is null)
                continue;

            bool revokedNow = await RevokeSessionByIdAsync(
                connection,
                transaction,
                session.AccountId,
                session.SessionId,
                cancellationToken);
            await EnforceSessionTombstoneLimitAsync(
                connection,
                transaction,
                session.AccountId,
                cancellationToken,
                session.SessionId);
            await transaction.CommitAsync(cancellationToken);
            return new LogoutSessionResult(username, revokedNow);
        }

        return null;
    }

    public async Task<AccountProfile> ChangeEmailAsync(
        uint accountId,
        string email,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        if (email.Trim().Length > AuthenticationInputValidation.MaximumEmailLength)
            throw new ArgumentOutOfRangeException(nameof(email));
        string normalized = email.Trim().ToUpperInvariant();
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        AccountProfile currentProfile = await LoadProfileAsync(
            connection, transaction, accountId, cancellationToken);
        if (string.Equals(
                currentProfile.Email,
                normalized,
                StringComparison.OrdinalIgnoreCase))
        {
            await transaction.CommitAsync(cancellationToken);
            return currentProfile;
        }

        if (await EmailExistsAsync(
                connection,
                transaction,
                normalized,
                accountId,
                cancellationToken))
        {
            throw new DuplicateNameException("Cette adresse e-mail est déjà utilisée.");
        }

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE atlas_launcher_profile
                SET email_normalized = @email, email_verified_at = NULL
                WHERE account_id = @accountId;
                UPDATE account
                SET email = @email
                WHERE id = @accountId;
                """;
            command.Parameters.AddWithValue("@email", normalized);
            command.Parameters.AddWithValue("@accountId", accountId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (MySqlCommand invalidate = connection.CreateCommand())
        {
            invalidate.Transaction = transaction;
            invalidate.CommandText = """
                UPDATE atlas_launcher_email_verification
                SET consumed_at = UTC_TIMESTAMP()
                WHERE account_id = @accountId
                  AND consumed_at IS NULL;
                """;
            invalidate.Parameters.AddWithValue("@accountId", accountId);
            await invalidate.ExecuteNonQueryAsync(cancellationToken);
        }

        AccountProfile profile = await LoadProfileAsync(
            connection, transaction, accountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    public async Task<AccountProfile> UpdateSocialProfileAsync(
        uint accountId,
        string statusMessage,
        string bio,
        CancellationToken cancellationToken)
    {
        if (!SocialProfilesAvailable)
        {
            throw new InvalidOperationException(
                "Le profil social requiert la migration Atlas 0005.");
        }

        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE atlas_launcher_profile
            SET status_message = @statusMessage,
                bio = @bio
            WHERE account_id = @accountId;
            """;
        command.Parameters.AddWithValue(
            "@statusMessage",
            string.IsNullOrWhiteSpace(statusMessage) ? DBNull.Value : statusMessage);
        command.Parameters.AddWithValue(
            "@bio",
            string.IsNullOrWhiteSpace(bio) ? DBNull.Value : bio);
        command.Parameters.AddWithValue("@accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await LoadProfileAsync(connection, null, accountId, cancellationToken);
    }

    public async Task<EmailVerificationChallenge?> CreateEmailVerificationAsync(
        uint accountId,
        int expiryHours,
        int cooldownSeconds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        string username;
        string email;
        bool verified;
        DateTime? latestCreatedAt;
        await using (MySqlCommand profile = connection.CreateCommand())
        {
            profile.Transaction = transaction;
            profile.CommandText = """
                SELECT p.display_username, p.email_normalized, p.email_verified_at,
                       (
                           SELECT MAX(v.created_at)
                           FROM atlas_launcher_email_verification v
                           WHERE v.account_id = p.account_id
                             AND v.consumed_at IS NULL
                             AND v.expires_at > UTC_TIMESTAMP()
                       ) AS latest_created_at
                FROM atlas_launcher_profile p
                WHERE p.account_id = @accountId
                LIMIT 1
                FOR UPDATE;
                """;
            profile.Parameters.AddWithValue("@accountId", accountId);

            await using MySqlDataReader reader =
                await profile.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Profil launcher introuvable.");

            username = reader.GetString("display_username");
            email = reader.GetString("email_normalized").ToLowerInvariant();
            verified = !reader.IsDBNull("email_verified_at");
            latestCreatedAt = reader.IsDBNull("latest_created_at")
                ? null
                : reader.GetDateTime("latest_created_at");
        }

        if (verified)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (latestCreatedAt.HasValue)
        {
            DateTimeOffset latest = new(
                DateTime.SpecifyKind(latestCreatedAt.Value, DateTimeKind.Utc));
            int remaining = Math.Max(
                0,
                cooldownSeconds - (int)(now - latest).TotalSeconds);
            if (remaining > 0)
                throw new EmailVerificationCooldownException(remaining);
        }

        string token = TokenService.CreateEmailVerificationToken();
        byte[] tokenHash = TokenService.Hash(token);
        DateTimeOffset expiresAt = now.AddHours(Math.Clamp(expiryHours, 1, 168));

        await using (MySqlCommand cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM atlas_launcher_email_verification
                WHERE expires_at < UTC_TIMESTAMP() - INTERVAL 7 DAY
                   OR consumed_at < UTC_TIMESTAMP() - INTERVAL 7 DAY;
                """;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (MySqlCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO atlas_launcher_email_verification
                    (id, account_id, email_normalized, token_hash, expires_at)
                VALUES
                    (@id, @accountId, @email, @tokenHash, @expiresAt);
                """;
            insert.Parameters.Add("@id", MySqlDbType.Binary, 16).Value =
                Guid.NewGuid().ToByteArray();
            insert.Parameters.AddWithValue("@accountId", accountId);
            insert.Parameters.AddWithValue("@email", email.ToUpperInvariant());
            insert.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
            insert.Parameters.AddWithValue("@expiresAt", expiresAt.UtcDateTime);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new EmailVerificationChallenge(
            accountId,
            username,
            email,
            token,
            tokenHash,
            expiresAt);
    }

    public async Task CancelEmailVerificationAsync(
        uint accountId,
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM atlas_launcher_email_verification
            WHERE account_id = @accountId
              AND token_hash = @tokenHash
              AND consumed_at IS NULL;
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EmailVerificationResult> VerifyEmailAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (!TokenService.IsEmailVerificationToken(token))
        {
            return EmailVerificationResult.Invalid;
        }

        byte[] tokenHash = TokenService.Hash(token);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        uint accountId;
        string tokenEmail;
        string currentEmail;
        DateTime expiresAt;
        bool consumed;
        bool alreadyVerified;
        await using (MySqlCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT v.account_id, v.email_normalized, v.expires_at, v.consumed_at,
                       p.email_normalized AS current_email, p.email_verified_at
                FROM atlas_launcher_email_verification v
                JOIN atlas_launcher_profile p ON p.account_id = v.account_id
                WHERE v.token_hash = @tokenHash
                LIMIT 1
                FOR UPDATE;
                """;
            select.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;

            await using MySqlDataReader reader =
                await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return EmailVerificationResult.Invalid;

            accountId = reader.GetUInt32("account_id");
            tokenEmail = reader.GetString("email_normalized");
            currentEmail = reader.GetString("current_email");
            expiresAt = reader.GetDateTime("expires_at");
            consumed = !reader.IsDBNull("consumed_at");
            alreadyVerified = !reader.IsDBNull("email_verified_at");
        }

        EmailVerificationResult result;
        if (!string.Equals(tokenEmail, currentEmail, StringComparison.OrdinalIgnoreCase))
        {
            result = EmailVerificationResult.Invalid;
        }
        else if (alreadyVerified)
        {
            result = EmailVerificationResult.AlreadyVerified;
        }
        else if (consumed)
        {
            result = EmailVerificationResult.Invalid;
        }
        else if (new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)) <= now)
        {
            result = EmailVerificationResult.Expired;
        }
        else
        {
            await using MySqlCommand verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandText = """
                UPDATE atlas_launcher_profile
                SET email_verified_at = UTC_TIMESTAMP()
                WHERE account_id = @accountId
                  AND email_verified_at IS NULL;
                """;
            verify.Parameters.AddWithValue("@accountId", accountId);
            await verify.ExecuteNonQueryAsync(cancellationToken);
            result = EmailVerificationResult.Verified;
        }

        await using (MySqlCommand consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = result is EmailVerificationResult.Verified
                or EmailVerificationResult.AlreadyVerified
                ? """
                    UPDATE atlas_launcher_email_verification
                    SET consumed_at = COALESCE(consumed_at, UTC_TIMESTAMP())
                    WHERE account_id = @accountId
                      AND BINARY email_normalized = BINARY @email;
                    """
                : """
                    UPDATE atlas_launcher_email_verification
                    SET consumed_at = COALESCE(consumed_at, UTC_TIMESTAMP())
                    WHERE token_hash = @tokenHash;
                    """;
            consume.Parameters.AddWithValue("@accountId", accountId);
            consume.Parameters.AddWithValue("@email", tokenEmail);
            consume.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
            await consume.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public Task<AuthResponse?> ChangePasswordAsync(
        uint accountId,
        string accessToken,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
        => ExecuteWithAuthDeadlockRetryAsync(
            retryToken => ChangePasswordOnceAsync(
                accountId,
                accessToken,
                currentPassword,
                newPassword,
                retryToken),
            cancellationToken);

    private async Task<AuthResponse?> ChangePasswordOnceAsync(
        uint accountId,
        string accessToken,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(currentPassword)
            || currentPassword.Length > 128
            || newPassword is null
            || newPassword.Length is < 10 or > 128)
        {
            return null;
        }

        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await DeleteStaleSessionsBatchAsync(connection, cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (!await LockAccountByIdAsync(
                connection, transaction, accountId, cancellationToken))
            return null;

        AccountCredential? credential = await LoadCredentialByIdAsync(
            connection, transaction, accountId, cancellationToken);
        if (credential is null || !credential.HasAtlasProfile)
            return null;

        PasswordSession? currentSession = await FindActivePasswordSessionForUpdateAsync(
            connection,
            transaction,
            accountId,
            TokenService.Hash(accessToken),
            cancellationToken);
        if (currentSession is null)
            return null;

        bool valid = credential.ModernSalt is not null && credential.ModernVerifier is not null
            ? SrpCredentials.VerifyModern(
                credential.Username, currentPassword, credential.ModernSalt, credential.ModernVerifier)
            : SrpCredentials.VerifyLegacy(
                credential.Username, currentPassword, credential.LegacySalt, credential.LegacyVerifier);
        if (!valid)
            return null;

        (byte[] legacySalt, byte[] legacyVerifier) =
            SrpCredentials.MakeLegacy(credential.Username, newPassword);
        (byte[] modernSalt, byte[] modernVerifier) =
            SrpCredentials.MakeModern(credential.Username, newPassword);

        await using (MySqlCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE account
                SET salt = @salt, verifier = @verifier, session_key = NULL
                WHERE id = @accountId;
                """;
            update.Parameters.Add("@salt", MySqlDbType.Binary, 32).Value = legacySalt;
            update.Parameters.Add("@verifier", MySqlDbType.Binary, 32).Value = legacyVerifier;
            update.Parameters.AddWithValue("@accountId", accountId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpsertModernCredentialAsync(
            connection, transaction, credential.Username, modernSalt, modernVerifier, cancellationToken);

        await RevokeSessionBatchesAsync(
            connection,
            transaction,
            accountId,
            SessionGarbageCollectionAvailable
                ? """
                    SELECT id
                    FROM atlas_launcher_session
                        FORCE INDEX (ix_atlas_session_account_active_order)
                    WHERE account_id = @accountId
                      AND revoked_at IS NULL
                    ORDER BY created_at ASC, id ASC
                    LIMIT @batchLimit
                    FOR UPDATE;
                    """
                : """
                    SELECT id
                    FROM atlas_launcher_session
                    WHERE account_id = @accountId
                      AND revoked_at IS NULL
                    ORDER BY created_at ASC, id ASC
                    LIMIT @batchLimit
                    FOR UPDATE;
                    """,
            command => command.Parameters.AddWithValue("@accountId", accountId),
            cancellationToken);

        SessionTokens replacement = _tokens.Create(
            _options.AccessTokenMinutes,
            _options.RefreshTokenDays);
        await InsertSessionAsync(
            connection,
            transaction,
            accountId,
            NormalizeDeviceName(currentSession.DeviceName),
            replacement,
            cancellationToken);
        await EnforceSessionTombstoneLimitAsync(
            connection,
            transaction,
            accountId,
            cancellationToken);
        AccountProfile profile = await LoadProfileAsync(
            connection, transaction, accountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToAuthResponse(replacement, profile);
    }

    private static async Task<T> ExecuteWithAuthDeadlockRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (MySqlException exception) when (
                exception.Number == 1213
                && attempt < AuthTransactionDeadlockRetryLimit)
            {
                int exponentialDelay = 10 << (attempt - 1);
                int jitter = Random.Shared.Next(0, 11);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(exponentialDelay + jitter),
                    cancellationToken);
            }
        }
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        MySqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<uint?> LockAccountByUsernameAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string username,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM account
            WHERE username = @username
            LIMIT 1
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("@username", username);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull
            ? null
            : Convert.ToUInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> LockAccountByIdAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM account
            WHERE id = @accountId
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> AccountExistsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string username,
        string email,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM account a
            LEFT JOIN atlas_launcher_profile p ON p.account_id = a.id
            WHERE BINARY a.username = BINARY @username
               OR BINARY a.email = BINARY @email
               OR BINARY p.email_normalized = BINARY @email
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@email", email);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<AccountCredential?> LoadCredentialByIdAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CredentialQuery + " WHERE a.id = @accountId LIMIT 1;";
        command.Parameters.AddWithValue("@accountId", accountId);
        return await ReadCredentialAsync(command, cancellationToken);
    }

    private static async Task<AccountCredential?> ReadCredentialAsync(
        MySqlCommand command,
        CancellationToken cancellationToken)
    {
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new AccountCredential(
            reader.GetUInt32("id"),
            reader.GetString("username"),
            reader.GetString("email"),
            (byte[])reader["salt"],
            (byte[])reader["verifier"],
            reader.IsDBNull("modern_salt") ? null : (byte[])reader["modern_salt"],
            reader.IsDBNull("modern_verifier") ? null : (byte[])reader["modern_verifier"],
            reader.GetBoolean("has_atlas_profile"));
    }

    private static async Task UpsertModernCredentialAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string username,
        byte[] salt,
        byte[] verifier,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO hermes_bnet_credentials
                (username, srp_version, salt, verifier)
            VALUES
                (@username, 2, @salt, @verifier)
            ON DUPLICATE KEY UPDATE
                srp_version = VALUES(srp_version),
                salt = VALUES(salt),
                verifier = VALUES(verifier);
            """;
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.Add("@salt", MySqlDbType.Binary, 32).Value = salt;
        command.Parameters.Add("@verifier", MySqlDbType.VarBinary, 256).Value = verifier;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<byte[]> InsertSessionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        string? deviceName,
        SessionTokens session,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SessionFamiliesAvailable
            ? """
                INSERT INTO atlas_launcher_session
                    (id, account_id, access_hash, refresh_hash, device_name,
                     access_expires_at, refresh_expires_at, absolute_expires_at)
                VALUES
                    (@id, @accountId, @accessHash, @refreshHash, @deviceName,
                     @accessExpires, @refreshExpires, @refreshExpires);
                """
            : """
                INSERT INTO atlas_launcher_session
                    (id, account_id, access_hash, refresh_hash, device_name,
                     access_expires_at, refresh_expires_at)
                VALUES
                    (@id, @accountId, @accessHash, @refreshHash, @deviceName,
                     @accessExpires, @refreshExpires);
                """;
        byte[] sessionId = Guid.NewGuid().ToByteArray();
        command.Parameters.Add("@id", MySqlDbType.Binary, 16).Value = sessionId;
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.Add("@accessHash", MySqlDbType.Binary, 32).Value = session.AccessHash;
        command.Parameters.Add("@refreshHash", MySqlDbType.Binary, 32).Value = session.RefreshHash;
        command.Parameters.AddWithValue("@deviceName", (object?)deviceName ?? DBNull.Value);
        command.Parameters.AddWithValue("@accessExpires", session.AccessExpiresAt.UtcDateTime);
        command.Parameters.AddWithValue("@refreshExpires", session.RefreshExpiresAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return sessionId;
    }

    private async Task EnforceActiveSessionLimitAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        byte[] currentSessionId,
        CancellationToken cancellationToken)
    {
        // LoginOnce holds the account row FOR UPDATE before inserting. Concurrent
        // logins for one account therefore count and prune inside one serial order.
        IReadOnlyList<byte[]> active = await LockSessionIdsAsync(
            connection,
            transaction,
            SessionGarbageCollectionAvailable
                ? ActiveSessionProbeSql
                : LegacyActiveSessionProbeSql,
            command =>
            {
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.Add("@probeLimit", MySqlDbType.Int32).Value =
                    AccountSessionRepairProbeSize;
            },
            cancellationToken);

        if (active.Count > MaximumActiveSessionsPerAccount + SessionRevocationBatchSize)
        {
            throw new InvalidOperationException(
                "Le retard de sessions actives depasse le budget transactionnel de reparation.");
        }

        int overflow = active.Count - MaximumActiveSessionsPerAccount;
        if (overflow <= 0) return;

        IReadOnlyList<byte[]> victims = active
            .Where(id => !id.AsSpan().SequenceEqual(currentSessionId))
            .Take(overflow)
            .ToArray();
        if (victims.Count != overflow)
        {
            throw new InvalidOperationException(
                "Le plafond de sessions actives ne peut pas conserver la session courante.");
        }

        foreach (byte[] sessionId in victims)
        {
            _ = await RevokeSessionByIdAsync(
                connection,
                transaction,
                accountId,
                sessionId,
                cancellationToken);
        }
    }

    private async Task EnforceSessionTombstoneLimitAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        CancellationToken cancellationToken,
        byte[]? protectedSessionId = null)
    {
        if (!SessionGarbageCollectionAvailable)
            return;

        // The caller holds the account row. One fixed probe repairs up to 64
        // excess rows. A larger legacy backlog aborts the issuing transaction
        // instead of turning one authentication request into unbounded work.
        IReadOnlyList<byte[]> tombstones = await LockSessionIdsAsync(
            connection,
            transaction,
            ExcessSessionTombstoneIdsSql,
            command =>
            {
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.Add("@probeLimit", MySqlDbType.Int32).Value =
                    MaximumSessionTombstonesPerAccount
                    + SessionGarbageCollectionBatchSize
                    + 1;
            },
            cancellationToken);
        if (tombstones.Count
            > MaximumSessionTombstonesPerAccount + SessionGarbageCollectionBatchSize)
        {
            throw new InvalidOperationException(
                "Le retard de tombstones depasse le budget transactionnel de reparation.");
        }

        int excessCount = tombstones.Count - MaximumSessionTombstonesPerAccount;
        if (excessCount <= 0)
            return;

        IReadOnlyList<byte[]> victims = tombstones
            .Where(id => protectedSessionId is null
                || !id.AsSpan().SequenceEqual(protectedSessionId))
            .Take(excessCount)
            .ToArray();
        if (victims.Count != excessCount)
            throw new InvalidOperationException("Le tombstone protege ne peut pas etre conserve.");

        foreach (byte[] sessionId in victims)
        {
            await PurgeRefreshHistoryBySessionAsync(
                connection,
                transaction,
                sessionId,
                cancellationToken);
            await DeleteRevokedSessionByIdAsync(
                connection,
                transaction,
                accountId,
                sessionId,
                cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<byte[]>> LockSessionIdsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string sql,
        Action<MySqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        List<byte[]> sessionIds = [];
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        addParameters(command);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            sessionIds.Add((byte[])reader["id"]);
        return sessionIds;
    }

    private async Task RevokeSessionBatchesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        string lockSql,
        Action<MySqlCommand> addParameters,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<byte[]> sessionIds = await LockSessionIdsAsync(
            connection,
            transaction,
            lockSql,
            command =>
            {
                addParameters(command);
                command.Parameters.Add("@batchLimit", MySqlDbType.Int32).Value =
                    SessionRevocationBatchSize + 1;
            },
            cancellationToken);
        if (sessionIds.Count > SessionRevocationBatchSize)
        {
            throw new InvalidOperationException(
                "Le lot de revocation depasse le budget transactionnel autorise.");
        }

        foreach (byte[] sessionId in sessionIds)
        {
            _ = await RevokeSessionByIdAsync(
                connection,
                transaction,
                accountId,
                sessionId,
                cancellationToken);
        }
    }

    private static async Task DeleteRevokedSessionByIdAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        byte[] sessionId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM atlas_launcher_session
            WHERE id = @sessionId
              AND account_id = @accountId
              AND revoked_at IS NOT NULL;
            """;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        command.Parameters.AddWithValue("@accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountRefreshHistoryAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        byte[] sessionId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RefreshHistoryCountSql;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ArchiveRefreshTokenAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        byte[] tokenHash,
        byte[] sessionId,
        DateTime expiresAt,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO atlas_launcher_refresh_history
                (token_hash, session_id, expires_at, consumed_at)
            VALUES
                (@tokenHash, @sessionId, @expiresAt, UTC_TIMESTAMP(6));
            """;
        command.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        command.Parameters.AddWithValue("@expiresAt", expiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteExpiredRefreshHistoryBatchAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        // Materialize only unlocked history rows, then delete those exact keys.
        // A family purge that already holds one history row can therefore never
        // form an opposite-order cycle with this expiry walk.
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        List<byte[]> tokenHashes = [];
        await using (MySqlCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = RefreshHistoryCleanupSql;
            select.Parameters.Add("@batchLimit", MySqlDbType.Int32).Value =
                RefreshHistoryCleanupBatchSize;
            await using MySqlDataReader reader =
                await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tokenHashes.Add((byte[])reader["token_hash"]);
        }

        foreach (byte[] tokenHash in tokenHashes)
        {
            await using MySqlCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = RefreshHistoryDeleteSql;
            delete.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task DeleteStaleSessionsBatchAsync(
        MySqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (!SessionGarbageCollectionAvailable)
            return;

        // Drain a bounded history batch before taking any session lock. Parent
        // deletion below is still conditional on the family having no history.
        await DeleteExpiredRefreshHistoryBatchAsync(connection, cancellationToken);

        // Revoked and naturally expired rows use separate transactions. An auth
        // transaction may move one row from expired to revoked before applying
        // the account cap; no GC transaction may therefore hold both classes.
        await DeleteStaleSessionClassBatchAsync(
            connection,
            RevokedSessionCleanupSql,
            requireExpiredAccessToken: false,
            cancellationToken);
        await DeleteStaleSessionClassBatchAsync(
            connection,
            ExpiredSessionCleanupSql,
            requireExpiredAccessToken: true,
            cancellationToken);
    }

    private static async Task DeleteStaleSessionClassBatchAsync(
        MySqlConnection connection,
        string candidateSql,
        bool requireExpiredAccessToken,
        CancellationToken cancellationToken)
    {
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        IReadOnlyList<byte[]> candidates = await LockSessionIdsAsync(
            connection,
            transaction,
            candidateSql,
            command =>
            {
                command.Parameters.Add("@retentionMinutes", MySqlDbType.Int32).Value =
                    SessionTombstoneRetentionMinutes;
                command.Parameters.Add("@batchLimit", MySqlDbType.Int32).Value =
                    SessionGarbageCollectionBatchSize;
            },
            cancellationToken);
        foreach (byte[] sessionId in candidates)
        {
            if (!await RefreshHistoryExistsForUpdateAsync(
                    connection, transaction, sessionId, cancellationToken))
            {
                await DeleteStaleSessionByIdAsync(
                    connection,
                    transaction,
                    sessionId,
                    requireExpiredAccessToken,
                    cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> RefreshHistoryExistsForUpdateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        byte[] sessionId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RefreshHistoryExistenceLockSql;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task DeleteStaleSessionByIdAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        byte[] sessionId,
        bool requireExpiredAccessToken,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = requireExpiredAccessToken
            ? ExpiredSessionDeleteSql
            : RevokedSessionDeleteSql;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        command.Parameters.Add("@retentionMinutes", MySqlDbType.Int32).Value =
            SessionTombstoneRetentionMinutes;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SessionLocator?> LocateSessionByHashAsync(
        MySqlConnection connection,
        string hashColumn,
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        if (hashColumn is not ("access_hash" or "refresh_hash"))
            throw new ArgumentOutOfRangeException(nameof(hashColumn));

        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, account_id
            FROM atlas_launcher_session
            WHERE {hashColumn} = @tokenHash
            LIMIT 1;
            """;
        command.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
        await using (MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            return new SessionLocator(
                (byte[])reader["id"],
                reader.GetUInt32("account_id"));
        }
    }

    private static async Task<SessionLocator?> LocateRefreshProofAsync(
        MySqlConnection connection,
        byte[] tokenHash,
        CancellationToken cancellationToken)
    {
        SessionLocator? current = await LocateSessionByHashAsync(
            connection, "refresh_hash", tokenHash, cancellationToken);
        if (current is not null)
            return current;

        byte[]? sessionId;
        await using (MySqlCommand locateHistory = connection.CreateCommand())
        {
            locateHistory.CommandText = ReplayHistoryLocatorSql;
            locateHistory.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
            sessionId = await locateHistory.ExecuteScalarAsync(cancellationToken) as byte[];
        }
        if (sessionId is null)
            return null;

        await using MySqlCommand locateSession = connection.CreateCommand();
        locateSession.CommandText = """
            SELECT account_id
            FROM atlas_launcher_session
            WHERE id = @sessionId
            LIMIT 1;
            """;
        locateSession.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        object? accountId = await locateSession.ExecuteScalarAsync(cancellationToken);
        return accountId is null or DBNull
            ? null
            : new SessionLocator(
                sessionId,
                Convert.ToUInt32(
                    accountId,
                    System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<SessionAccount?> LockRefreshFamilySessionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        SessionLocator locator,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReplaySessionLockSql;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = locator.SessionId;
        command.Parameters.AddWithValue("@accountId", locator.AccountId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionAccount(
                (byte[])reader["id"],
                reader.GetUInt32("account_id"),
                reader.IsDBNull("device_name") ? null : reader.GetString("device_name"),
                DateTime.SpecifyKind(reader.GetDateTime("created_at"), DateTimeKind.Utc),
                string.Empty,
                DateTime.SpecifyKind(reader.GetDateTime("absolute_expires_at"), DateTimeKind.Utc),
                (byte[])reader["refresh_hash"],
                reader.GetBoolean("refresh_is_valid"))
            : null;
    }

    private static async Task<SessionAccount?> LockLegacyRefreshSessionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        SessionLocator locator,
        byte[] refreshHash,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, account_id, device_name, created_at, refresh_hash,
                   refresh_expires_at AS absolute_expires_at
            FROM atlas_launcher_session
            WHERE id = @sessionId
              AND account_id = @accountId
              AND refresh_hash = @refreshHash
              AND revoked_at IS NULL
              AND refresh_expires_at > UTC_TIMESTAMP()
            FOR UPDATE;
            """;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = locator.SessionId;
        command.Parameters.AddWithValue("@accountId", locator.AccountId);
        command.Parameters.Add("@refreshHash", MySqlDbType.Binary, 32).Value = refreshHash;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionAccount(
                (byte[])reader["id"],
                reader.GetUInt32("account_id"),
                reader.IsDBNull("device_name") ? null : reader.GetString("device_name"),
                DateTime.SpecifyKind(reader.GetDateTime("created_at"), DateTimeKind.Utc),
                string.Empty,
                DateTime.SpecifyKind(reader.GetDateTime("absolute_expires_at"), DateTimeKind.Utc),
                (byte[])reader["refresh_hash"],
                RefreshIsValid: true)
            : null;
    }

    private static async Task<bool> RevalidateRefreshHistoryForUpdateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        byte[] tokenHash,
        byte[] sessionId,
        DateTime absoluteExpiresAt,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = ReplayHistoryRevalidationSql;
        command.Parameters.Add("@tokenHash", MySqlDbType.Binary, 32).Value = tokenHash;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        command.Parameters.AddWithValue("@absoluteExpiresAt", absoluteExpiresAt);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<LogoutSession?> LockLogoutSessionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        SessionLocator locator,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SessionFamiliesAvailable
            ? """
                SELECT id, account_id, access_hash, refresh_hash, revoked_at,
                       absolute_expires_at
                FROM atlas_launcher_session
                WHERE id = @sessionId
                  AND account_id = @accountId
                FOR UPDATE;
                """
            : """
                SELECT id, account_id, access_hash, refresh_hash, revoked_at,
                       NULL AS absolute_expires_at
                FROM atlas_launcher_session
                WHERE id = @sessionId
                  AND account_id = @accountId
                FOR UPDATE;
                """;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = locator.SessionId;
        command.Parameters.AddWithValue("@accountId", locator.AccountId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LogoutSession(
                (byte[])reader["id"],
                reader.GetUInt32("account_id"),
                (byte[])reader["access_hash"],
                (byte[])reader["refresh_hash"],
                reader.IsDBNull("revoked_at") ? null : reader.GetDateTime("revoked_at"),
                reader.IsDBNull("absolute_expires_at")
                    ? null
                    : DateTime.SpecifyKind(
                        reader.GetDateTime("absolute_expires_at"), DateTimeKind.Utc))
            : null;
    }

    private static async Task<string?> LoadUsernameAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT username FROM account WHERE id = @accountId LIMIT 1;";
        command.Parameters.AddWithValue("@accountId", accountId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<bool> RevokeSessionByIdAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        byte[] sessionId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE atlas_launcher_session
            SET revoked_at = UTC_TIMESTAMP(),
                updated_at = UTC_TIMESTAMP()
            WHERE id = @sessionId
              AND account_id = @accountId
              AND revoked_at IS NULL;
        """;
        command.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        command.Parameters.AddWithValue("@accountId", accountId);
        bool revokedNow = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (revokedNow && SessionFamiliesAvailable)
        {
            await PurgeRefreshHistoryBySessionAsync(
                connection,
                transaction,
                sessionId,
                cancellationToken);
        }

        return revokedNow;
    }

    private static async Task PurgeRefreshHistoryBySessionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        byte[] sessionId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand purge = connection.CreateCommand();
        purge.Transaction = transaction;
        purge.CommandText = PurgeRefreshHistoryBySessionSql;
        purge.Parameters.Add("@sessionId", MySqlDbType.Binary, 16).Value = sessionId;
        await purge.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PasswordSession?> FindActivePasswordSessionForUpdateAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        byte[] accessHash,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, device_name
            FROM atlas_launcher_session
            WHERE account_id = @accountId
              AND access_hash = @accessHash
              AND revoked_at IS NULL
              AND access_expires_at > UTC_TIMESTAMP()
            FOR UPDATE;
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.Add("@accessHash", MySqlDbType.Binary, 32).Value = accessHash;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PasswordSession(
                (byte[])reader["id"],
                reader.IsDBNull("device_name") ? null : reader.GetString("device_name"))
            : null;
    }

    private async Task RevokeActiveDeviceSessionsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        string deviceName,
        CancellationToken cancellationToken)
    {
        // LoginOnce already owns the account row, then locks each matching
        // session before touching that session's refresh history.
        await RevokeSessionBatchesAsync(
            connection,
            transaction,
            accountId,
            SessionGarbageCollectionAvailable
                ? """
                    SELECT id
                    FROM atlas_launcher_session
                        FORCE INDEX (ix_atlas_session_account_active_order)
                    WHERE account_id = @accountId
                      AND revoked_at IS NULL
                      AND refresh_expires_at > UTC_TIMESTAMP()
                      AND TRIM(device_name) = @deviceName
                    ORDER BY created_at ASC, id ASC
                    LIMIT @batchLimit
                    FOR UPDATE;
                    """
                : """
                    SELECT id
                    FROM atlas_launcher_session
                    WHERE account_id = @accountId
                      AND revoked_at IS NULL
                      AND refresh_expires_at > UTC_TIMESTAMP()
                      AND TRIM(device_name) = @deviceName
                    ORDER BY created_at ASC, id ASC
                    LIMIT @batchLimit
                    FOR UPDATE;
                    """,
            command =>
            {
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@deviceName", deviceName);
            },
            cancellationToken);
    }

    private async Task RevokeOlderDeviceSessionsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        byte[] currentSessionId,
        DateTime currentCreatedAt,
        string deviceName,
        CancellationToken cancellationToken)
    {
        await RevokeSessionBatchesAsync(
            connection,
            transaction,
            accountId,
            SessionGarbageCollectionAvailable
                ? """
                    SELECT id
                    FROM atlas_launcher_session
                        FORCE INDEX (ix_atlas_session_account_active_order)
                    WHERE account_id = @accountId
                      AND id <> @currentSessionId
                      AND revoked_at IS NULL
                      AND refresh_expires_at > UTC_TIMESTAMP()
                      AND TRIM(device_name) = @deviceName
                      AND created_at < @currentCreatedAt
                    ORDER BY created_at ASC, id ASC
                    LIMIT @batchLimit
                    FOR UPDATE;
                    """
                : """
                    SELECT id
                    FROM atlas_launcher_session
                    WHERE account_id = @accountId
                      AND id <> @currentSessionId
                      AND revoked_at IS NULL
                      AND refresh_expires_at > UTC_TIMESTAMP()
                      AND TRIM(device_name) = @deviceName
                      AND created_at < @currentCreatedAt
                    ORDER BY created_at ASC, id ASC
                    LIMIT @batchLimit
                    FOR UPDATE;
                    """,
            command =>
            {
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.Add("@currentSessionId", MySqlDbType.Binary, 16).Value =
                    currentSessionId;
                command.Parameters.AddWithValue("@currentCreatedAt", currentCreatedAt);
                command.Parameters.AddWithValue("@deviceName", deviceName);
            },
            cancellationToken);
    }

    internal static string? NormalizeDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;

        string normalized = deviceName.Trim();
        if (normalized.Length > AuthenticationInputValidation.MaximumDeviceNameLength)
            throw new ArgumentOutOfRangeException(nameof(deviceName));
        return normalized;
    }

    private static async Task<bool> EmailExistsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string email,
        uint excludedAccountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM account a
            LEFT JOIN atlas_launcher_profile p ON p.account_id = a.id
            WHERE a.id <> @accountId
              AND (BINARY a.email = BINARY @email
                   OR BINARY p.email_normalized = BINARY @email)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@accountId", excludedAccountId);
        command.Parameters.AddWithValue("@email", email);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<AccountProfile> LoadProfileAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SocialProfilesAvailable
            ? """
            SELECT p.account_id, p.display_username, p.email_normalized,
                   p.email_verified_at, p.avatar_key, p.status_message, p.bio,
                   p.two_factor_enabled, p.recovery_codes_generated,
                   aa.id AS avatar_photo_id, aa.version AS avatar_photo_version
            FROM atlas_launcher_profile p
            LEFT JOIN atlas_launcher_profile_avatar pa ON pa.account_id = p.account_id
            LEFT JOIN atlas_launcher_avatar_asset aa
              ON aa.id = pa.current_avatar_asset_id
             AND aa.status = 1
            WHERE p.account_id = @accountId
            LIMIT 1;
            """
            : """
            SELECT p.account_id, p.display_username, p.email_normalized,
                   p.email_verified_at, p.avatar_key,
                   NULL AS status_message, NULL AS bio,
                   p.two_factor_enabled, p.recovery_codes_generated,
                   aa.id AS avatar_photo_id, aa.version AS avatar_photo_version
            FROM atlas_launcher_profile p
            LEFT JOIN atlas_launcher_profile_avatar pa ON pa.account_id = p.account_id
            LEFT JOIN atlas_launcher_avatar_asset aa
              ON aa.id = pa.current_avatar_asset_id
             AND aa.status = 1
            WHERE p.account_id = @accountId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Profil launcher introuvable.");

        bool emailVerified = !reader.IsDBNull("email_verified_at");
        string? avatar = reader.IsDBNull("avatar_key") ? null : reader.GetString("avatar_key");
        bool twoFactor = reader.GetBoolean("two_factor_enabled");
        bool recovery = reader.GetBoolean("recovery_codes_generated");
        Avatars.AvatarDescriptor? photo = reader.IsDBNull("avatar_photo_id")
            ? null
            : Avatars.AvatarDescriptor.Create(
                new Guid((byte[])reader["avatar_photo_id"], bigEndian: true),
                reader.GetUInt64("avatar_photo_version"));
        int completion = 40
            + (emailVerified ? 25 : 0)
            + (avatar is null && photo is null ? 0 : 10)
            + (twoFactor ? 20 : 0)
            + (recovery ? 5 : 0);

        return new AccountProfile(
            reader.GetUInt32("account_id"),
            reader.GetString("display_username"),
            reader.GetString("email_normalized").ToLowerInvariant(),
            emailVerified,
            avatar,
            twoFactor,
            recovery,
            completion,
            photo,
            reader.IsDBNull("status_message") ? string.Empty : reader.GetString("status_message"),
            reader.IsDBNull("bio") ? string.Empty : reader.GetString("bio"));
    }

    private static AuthResponse ToAuthResponse(SessionTokens session, AccountProfile profile)
        => new(
            session.AccessToken,
            session.AccessExpiresAt,
            session.RefreshToken,
            session.RefreshExpiresAt,
            profile);

    private const string CredentialQuery = """
        SELECT a.id, a.username, a.email, a.salt, a.verifier,
               h.salt AS modern_salt, h.verifier AS modern_verifier,
               p.account_id IS NOT NULL AS has_atlas_profile
        FROM account a
        LEFT JOIN atlas_launcher_profile p ON p.account_id = a.id
        LEFT JOIN hermes_bnet_credentials h
          ON BINARY h.username = BINARY a.username
        """;

    private sealed record AccountCredential(
        uint AccountId,
        string Username,
        string Email,
        byte[] LegacySalt,
        byte[] LegacyVerifier,
        byte[]? ModernSalt,
        byte[]? ModernVerifier,
        bool HasAtlasProfile);

    private sealed record SessionAccount(
        byte[] SessionId,
        uint AccountId,
        string? DeviceName,
        DateTime CreatedAt,
        string Username,
        DateTime AbsoluteExpiresAt,
        byte[]? RefreshHash,
        bool RefreshIsValid);

    private sealed record SessionLocator(byte[] SessionId, uint AccountId);

    private sealed record LogoutCandidate(
        SessionLocator Locator,
        byte[] TokenHash,
        bool IsRefreshProof);

    private sealed record LogoutSession(
        byte[] SessionId,
        uint AccountId,
        byte[] AccessHash,
        byte[] RefreshHash,
        DateTime? RevokedAt,
        DateTime? AbsoluteExpiresAt);

    private sealed record PasswordSession(byte[] SessionId, string? DeviceName);

    internal enum RefreshSessionRevocationReason
    {
        Replay,
        RotationLimitExceeded
    }

    internal sealed record RefreshSessionResult(
        AuthResponse? Response,
        string? RevokedUsername,
        RefreshSessionRevocationReason? RevocationReason)
    {
        internal static RefreshSessionResult Invalid { get; } = new(null, null, null);
        internal static RefreshSessionResult Success(AuthResponse response) =>
            new(response, null, null);
        internal static RefreshSessionResult Revoked(
            string username,
            RefreshSessionRevocationReason reason) => new(null, username, reason);
        internal static RefreshSessionResult Replay(string username) =>
            Revoked(username, RefreshSessionRevocationReason.Replay);
        internal static RefreshSessionResult RotationLimitExceeded(string username) =>
            Revoked(username, RefreshSessionRevocationReason.RotationLimitExceeded);
        internal string? ReplayUsername =>
            RevocationReason == RefreshSessionRevocationReason.Replay
                ? RevokedUsername
                : null;
        internal bool ReplayDetected => ReplayUsername is not null;
        internal bool RotationLimitWasExceeded =>
            RevocationReason == RefreshSessionRevocationReason.RotationLimitExceeded;
    }

    internal sealed record LogoutSessionResult(string Username, bool RevokedNow);
}
