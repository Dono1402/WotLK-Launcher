using System.Data;
using System.Security.Cryptography;
using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed class LauncherDatabase
{
    private readonly LauncherServerOptions _options;
    private readonly TokenService _tokens;

    public LauncherDatabase(LauncherServerOptions options, TokenService tokens)
    {
        _options = options;
        _tokens = tokens;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS atlas_launcher_profile (
                account_id INT UNSIGNED NOT NULL PRIMARY KEY,
                display_username VARCHAR(32) NOT NULL,
                email_normalized VARCHAR(254) NOT NULL UNIQUE,
                email_verified_at DATETIME NULL,
                avatar_key VARCHAR(128) NULL,
                two_factor_enabled TINYINT(1) NOT NULL DEFAULT 0,
                recovery_codes_generated TINYINT(1) NOT NULL DEFAULT 0,
                created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                CONSTRAINT fk_atlas_profile_account
                    FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS atlas_launcher_session (
                id BINARY(16) NOT NULL PRIMARY KEY,
                account_id INT UNSIGNED NOT NULL,
                access_hash BINARY(32) NOT NULL UNIQUE,
                refresh_hash BINARY(32) NOT NULL UNIQUE,
                device_name VARCHAR(128) NULL,
                access_expires_at DATETIME NOT NULL,
                refresh_expires_at DATETIME NOT NULL,
                revoked_at DATETIME NULL,
                created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                INDEX ix_atlas_session_account (account_id),
                INDEX ix_atlas_session_refresh_expiry (refresh_expires_at),
                CONSTRAINT fk_atlas_session_account
                    FOREIGN KEY (account_id) REFERENCES account(id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AuthResponse> RegisterAsync(
        RegisterRequest request,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        string displayUsername = request.Username.Trim();
        string username = displayUsername.ToUpperInvariant();
        string email = request.Email.Trim();
        string normalizedEmail = email.ToUpperInvariant();
        (byte[] legacySalt, byte[] legacyVerifier) = SrpCredentials.MakeLegacy(username, request.Password);
        (byte[] modernSalt, byte[] modernVerifier) = SrpCredentials.MakeModern(username, request.Password);

        await using MySqlConnection connection = await OpenAsync(cancellationToken);
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
            connection, transaction, accountId, deviceName, session, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        AccountProfile accountProfile = new(
            accountId, displayUsername, email, false, null, false, false, 40);
        return ToAuthResponse(session, accountProfile);
    }

    public async Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        string username = request.Username.Trim().ToUpperInvariant();
        await using MySqlConnection connection = await OpenAsync(cancellationToken);

        AccountCredential? credential = await LoadCredentialAsync(connection, username, cancellationToken);
        if (credential is null)
            return null;

        bool valid = credential.ModernSalt is not null && credential.ModernVerifier is not null
            ? SrpCredentials.VerifyModern(
                credential.Username, request.Password, credential.ModernSalt, credential.ModernVerifier)
            : SrpCredentials.VerifyLegacy(
                credential.Username, request.Password, credential.LegacySalt, credential.LegacyVerifier);
        if (!valid)
            return null;

        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        if (credential.ModernSalt is null || credential.ModernVerifier is null)
        {
            (byte[] salt, byte[] verifier) =
                SrpCredentials.MakeModern(credential.Username, request.Password);
            await UpsertModernCredentialAsync(
                connection, transaction, credential.Username, salt, verifier, cancellationToken);
        }

        AccountProfile profile = await EnsureAndLoadProfileAsync(
            connection, transaction, credential, cancellationToken);
        SessionTokens session = _tokens.Create(_options.AccessTokenMinutes, _options.RefreshTokenDays);
        await InsertSessionAsync(
            connection, transaction, credential.AccountId, request.DeviceName, session, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToAuthResponse(session, profile);
    }

    public async Task<AuthResponse?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        byte[] refreshHash = TokenService.Hash(refreshToken);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        SessionAccount? account;
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT s.id, s.account_id, s.device_name, a.username
                FROM atlas_launcher_session s
                INNER JOIN account a ON a.id = s.account_id
                WHERE s.refresh_hash = @hash
                  AND s.revoked_at IS NULL
                  AND s.refresh_expires_at > UTC_TIMESTAMP()
                FOR UPDATE;
                """;
            command.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = refreshHash;
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            account = await reader.ReadAsync(cancellationToken)
                ? new SessionAccount(
                    (byte[])reader["id"],
                    reader.GetUInt32("account_id"),
                    reader.IsDBNull("device_name") ? null : reader.GetString("device_name"),
                    reader.GetString("username"))
                : null;
        }

        if (account is null)
            return null;

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
        return ToAuthResponse(session, profile);
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
            INNER JOIN account a ON a.id = s.account_id
            WHERE s.access_hash = @hash
              AND s.revoked_at IS NULL
              AND s.access_expires_at > UTC_TIMESTAMP()
            LIMIT 1;
            """;
        command.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = hash;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AuthenticatedAccount(reader.GetUInt32("account_id"), reader.GetString("username"))
            : null;
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
        command.CommandText = """
            SELECT HEX(id) AS session_id, device_name, created_at, updated_at,
                   refresh_expires_at, access_hash
            FROM atlas_launcher_session
            WHERE account_id = @accountId
              AND revoked_at IS NULL
              AND refresh_expires_at > UTC_TIMESTAMP()
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
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

    public async Task<bool> RevokeSessionAsync(
        uint accountId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId.Length != 32 || !sessionId.All(Uri.IsHexDigit))
            return false;

        byte[] id = Convert.FromHexString(sessionId);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE atlas_launcher_session
            SET revoked_at = UTC_TIMESTAMP(), updated_at = UTC_TIMESTAMP()
            WHERE id = @id
              AND account_id = @accountId
              AND revoked_at IS NULL;
            """;
        command.Parameters.Add("@id", MySqlDbType.Binary, 16).Value = id;
        command.Parameters.AddWithValue("@accountId", accountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE atlas_launcher_session
            SET revoked_at = UTC_TIMESTAMP()
            WHERE access_hash = @hash AND revoked_at IS NULL;
            """;
        command.Parameters.Add("@hash", MySqlDbType.Binary, 32).Value = TokenService.Hash(accessToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AccountProfile> ChangeEmailAsync(
        uint accountId,
        string email,
        CancellationToken cancellationToken)
    {
        string normalized = email.Trim().ToUpperInvariant();
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

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

        AccountProfile profile = await LoadProfileAsync(
            connection, transaction, accountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return profile;
    }

    public async Task<bool> ChangePasswordAsync(
        uint accountId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        AccountCredential? credential = await LoadCredentialByIdAsync(
            connection, accountId, cancellationToken);
        if (credential is null)
            return false;

        bool valid = credential.ModernSalt is not null && credential.ModernVerifier is not null
            ? SrpCredentials.VerifyModern(
                credential.Username, currentPassword, credential.ModernSalt, credential.ModernVerifier)
            : SrpCredentials.VerifyLegacy(
                credential.Username, currentPassword, credential.LegacySalt, credential.LegacyVerifier);
        if (!valid)
            return false;

        (byte[] legacySalt, byte[] legacyVerifier) =
            SrpCredentials.MakeLegacy(credential.Username, newPassword);
        (byte[] modernSalt, byte[] modernVerifier) =
            SrpCredentials.MakeModern(credential.Username, newPassword);

        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
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
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        MySqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
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

    private static async Task<AccountCredential?> LoadCredentialAsync(
        MySqlConnection connection,
        string username,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = CredentialQuery + " WHERE BINARY a.username = BINARY @username LIMIT 1;";
        command.Parameters.AddWithValue("@username", username);
        return await ReadCredentialAsync(command, cancellationToken);
    }

    private static async Task<AccountCredential?> LoadCredentialByIdAsync(
        MySqlConnection connection,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
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
            reader.IsDBNull("modern_verifier") ? null : (byte[])reader["modern_verifier"]);
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

    private static async Task InsertSessionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        string? deviceName,
        SessionTokens session,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO atlas_launcher_session
                (id, account_id, access_hash, refresh_hash, device_name,
                 access_expires_at, refresh_expires_at)
            VALUES
                (@id, @accountId, @accessHash, @refreshHash, @deviceName,
                 @accessExpires, @refreshExpires);
            """;
        command.Parameters.Add("@id", MySqlDbType.Binary, 16).Value = Guid.NewGuid().ToByteArray();
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.Add("@accessHash", MySqlDbType.Binary, 32).Value = session.AccessHash;
        command.Parameters.Add("@refreshHash", MySqlDbType.Binary, 32).Value = session.RefreshHash;
        command.Parameters.AddWithValue("@deviceName", (object?)deviceName ?? DBNull.Value);
        command.Parameters.AddWithValue("@accessExpires", session.AccessExpiresAt.UtcDateTime);
        command.Parameters.AddWithValue("@refreshExpires", session.RefreshExpiresAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<AccountProfile> EnsureAndLoadProfileAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        AccountCredential credential,
        CancellationToken cancellationToken)
    {
        string profileEmail = credential.Email.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(profileEmail)
            || await EmailExistsAsync(
                connection,
                transaction,
                profileEmail,
                credential.AccountId,
                cancellationToken))
        {
            profileEmail =
                $"{credential.Username.ToLowerInvariant()}+{credential.AccountId}@unverified.atlas.local";
        }

        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT IGNORE INTO atlas_launcher_profile
                    (account_id, display_username, email_normalized)
                VALUES
                    (@accountId, @username, @email);
                """;
            command.Parameters.AddWithValue("@accountId", credential.AccountId);
            command.Parameters.AddWithValue("@username", credential.Username);
            command.Parameters.AddWithValue("@email", profileEmail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await LoadProfileAsync(connection, transaction, credential.AccountId, cancellationToken);
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

    private static async Task<AccountProfile> LoadProfileAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.account_id, p.display_username, p.email_normalized,
                   p.email_verified_at, p.avatar_key,
                   p.two_factor_enabled, p.recovery_codes_generated
            FROM atlas_launcher_profile p
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
        int completion = 40
            + (emailVerified ? 25 : 0)
            + (avatar is null ? 0 : 10)
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
            completion);
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
               h.salt AS modern_salt, h.verifier AS modern_verifier
        FROM account a
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
        byte[]? ModernVerifier);

    private sealed record SessionAccount(
        byte[] SessionId,
        uint AccountId,
        string? DeviceName,
        string Username);
}
