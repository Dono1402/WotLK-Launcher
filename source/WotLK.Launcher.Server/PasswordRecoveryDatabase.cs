using System.Data;
using System.Security.Cryptography;
using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    public bool PasswordRecoveryAvailable => _options.MaximumSchemaVersion is null or >= 15;

    internal Task<EmailVerificationChallenge?> CreatePasswordResetAsync(string email, CancellationToken token)
        => ExecuteWithAuthDeadlockRetryAsync(ct => CreatePasswordResetOnceAsync(email, ct), token);

    private async Task<EmailVerificationChallenge?> CreatePasswordResetOnceAsync(string email, CancellationToken token)
    {
        if (!PasswordRecoveryAvailable) return null;
        email = email.Trim().ToUpperInvariant();
        await using MySqlConnection connection = await OpenAsync(token);
        uint accountId;
        await using (MySqlCommand lookup = new("SELECT account_id FROM atlas_launcher_profile WHERE email_normalized=@email;", connection))
        {
            lookup.Parameters.AddWithValue("@email", email);
            object? id = await lookup.ExecuteScalarAsync(token);
            if (id is null) return null;
            accountId = Convert.ToUInt32(id);
        }
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        if (!await LockAccountByIdAsync(connection, transaction, accountId, token)) return null;
        AccountCredential? credential = await LoadCredentialByIdAsync(connection, transaction, accountId, token);
        if (credential is null || !credential.HasAtlasProfile) return null;
        // The profile address is authoritative; a game-only identity can never reset through Atlas.
        await using (MySqlCommand check = new("SELECT email_normalized FROM atlas_launcher_profile WHERE account_id=@id FOR UPDATE;", connection, transaction))
        {
            check.Parameters.AddWithValue("@id", accountId);
            if (!string.Equals(Convert.ToString(await check.ExecuteScalarAsync(token)), email, StringComparison.OrdinalIgnoreCase)) return null;
        }
        await using (MySqlCommand cooldown = new("SELECT account_id FROM atlas_launcher_password_reset WHERE account_id=@id AND created_at > UTC_TIMESTAMP() - INTERVAL 5 MINUTE FOR UPDATE;", connection, transaction))
        {
            cooldown.Parameters.AddWithValue("@id", accountId);
            if (await cooldown.ExecuteScalarAsync(token) is not null) return null;
        }
        string secret = TokenService.CreatePasswordResetToken();
        byte[] hash = TokenService.Hash(secret);
        DateTimeOffset expires = DateTimeOffset.UtcNow.AddMinutes(30);
        await using (MySqlCommand save = new("""
            INSERT INTO atlas_launcher_password_reset
                (account_id,email_normalized,token_hash,credential_hash,created_at,expires_at,consumed_at)
            VALUES (@id,@email,@hash,@credential,UTC_TIMESTAMP(),@expires,NULL)
            ON DUPLICATE KEY UPDATE email_normalized=VALUES(email_normalized), token_hash=VALUES(token_hash),
                credential_hash=VALUES(credential_hash), created_at=VALUES(created_at),
                expires_at=VALUES(expires_at), consumed_at=NULL;
            """, connection, transaction))
        {
            save.Parameters.AddWithValue("@id", accountId);
            save.Parameters.AddWithValue("@email", email);
            save.Parameters.AddWithValue("@hash", hash);
            save.Parameters.AddWithValue("@credential", RecoveryCredentialHash(credential));
            save.Parameters.AddWithValue("@expires", expires.UtcDateTime);
            await save.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return new(accountId, credential.Username, email, secret, hash, expires);
    }

    internal async Task CancelPasswordResetAsync(byte[] hash, CancellationToken token)
    {
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlCommand command = new("UPDATE atlas_launcher_password_reset SET consumed_at=UTC_TIMESTAMP() WHERE token_hash=@hash;", connection);
        command.Parameters.AddWithValue("@hash", hash);
        await command.ExecuteNonQueryAsync(token);
    }

    internal Task<string?> ResetPasswordAsync(string? secret, string? password, CancellationToken token)
        => ExecuteWithAuthDeadlockRetryAsync(ct => ResetPasswordOnceAsync(secret, password, ct), token);

    private async Task<string?> ResetPasswordOnceAsync(string? secret, string? password, CancellationToken token)
    {
        if (!PasswordRecoveryAvailable || !TokenService.IsPasswordResetToken(secret) || password is null || password.Length is < 10 or > 128) return null;
        byte[] hash = TokenService.Hash(secret!);
        await using MySqlConnection connection = await OpenAsync(token);
        uint accountId;
        await using (MySqlCommand lookup = new("SELECT account_id FROM atlas_launcher_password_reset WHERE token_hash=@hash;", connection))
        {
            lookup.Parameters.AddWithValue("@hash", hash);
            object? id = await lookup.ExecuteScalarAsync(token);
            if (id is null) return null;
            accountId = Convert.ToUInt32(id);
        }
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        if (!await LockAccountByIdAsync(connection, transaction, accountId, token)) return null;
        AccountCredential? credential = await LoadCredentialByIdAsync(connection, transaction, accountId, token);
        if (credential is null || !credential.HasAtlasProfile) return null;
        await using (MySqlCommand validate = new("""
            SELECT r.credential_hash FROM atlas_launcher_password_reset r
            JOIN atlas_launcher_profile p ON p.account_id=r.account_id AND p.email_normalized=r.email_normalized
            WHERE r.account_id=@id AND r.token_hash=@hash AND r.consumed_at IS NULL
                AND r.expires_at > UTC_TIMESTAMP() FOR UPDATE;
            """, connection, transaction))
        {
            validate.Parameters.AddWithValue("@id", accountId);
            validate.Parameters.AddWithValue("@hash", hash);
            object? expected = await validate.ExecuteScalarAsync(token);
            if (expected is not byte[] fingerprint || !CryptographicOperations.FixedTimeEquals(fingerprint, RecoveryCredentialHash(credential))) return null;
        }
        (byte[] legacySalt, byte[] legacyVerifier) = SrpCredentials.MakeLegacy(credential.Username, password);
        (byte[] modernSalt, byte[] modernVerifier) = SrpCredentials.MakeModern(credential.Username, password);
        await using (MySqlCommand update = new("UPDATE account SET salt=@salt,verifier=@verifier,session_key=NULL WHERE id=@id;", connection, transaction))
        {
            update.Parameters.AddWithValue("@salt", legacySalt);
            update.Parameters.AddWithValue("@verifier", legacyVerifier);
            update.Parameters.AddWithValue("@id", accountId);
            await update.ExecuteNonQueryAsync(token);
        }
        await UpsertModernCredentialAsync(connection, transaction, credential.Username, modernSalt, modernVerifier, token);
        await RevokeSessionBatchesAsync(connection, transaction, accountId, """
            SELECT id FROM atlas_launcher_session FORCE INDEX (ix_atlas_session_account_active_order)
            WHERE account_id=@accountId AND revoked_at IS NULL
            ORDER BY created_at ASC,id ASC LIMIT @batchLimit FOR UPDATE;
            """, command => command.Parameters.AddWithValue("@accountId", accountId), token);
        await using (MySqlCommand consume = new("UPDATE atlas_launcher_password_reset SET consumed_at=UTC_TIMESTAMP() WHERE account_id=@id;", connection, transaction))
        {
            consume.Parameters.AddWithValue("@id", accountId);
            await consume.ExecuteNonQueryAsync(token);
        }
        await EnforceSessionTombstoneLimitAsync(connection, transaction, accountId, token);
        await transaction.CommitAsync(token);
        return credential.Username;
    }

    private static byte[] RecoveryCredentialHash(AccountCredential credential)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(credential.LegacySalt);
        hash.AppendData(credential.LegacyVerifier);
        if (credential.ModernSalt is not null) hash.AppendData(credential.ModernSalt);
        if (credential.ModernVerifier is not null) hash.AppendData(credential.ModernVerifier);
        return hash.GetHashAndReset();
    }
}
