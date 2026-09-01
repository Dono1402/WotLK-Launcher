using System.Data;
using MySqlConnector;

namespace WotLK.Launcher.Server.Avatars;

internal interface IAvatarRepository
{
    Task<AvatarRateLimitDecision> TryConsumeUploadPermitAsync(uint accountId, CancellationToken cancellationToken);
    Task<AvatarAssetRecord> CreatePendingAsync(uint accountId, CancellationToken cancellationToken);
    Task<AvatarPublicationResult> PublishReadyAsync(
        uint accountId,
        AvatarAssetRecord pending,
        IReadOnlyList<AvatarStoredVariant> variants,
        CancellationToken cancellationToken);
    Task MarkPendingDeletedAsync(uint accountId, Guid avatarId, CancellationToken cancellationToken);
    Task<AvatarDeletionResult> DeleteActiveAsync(uint accountId, CancellationToken cancellationToken);
    Task<AvatarDescriptor?> GetActiveDescriptorAsync(uint accountId, CancellationToken cancellationToken);
    Task<AvatarMediaRecord?> GetMediaAsync(
        Guid avatarId,
        ulong version,
        int size,
        CancellationToken cancellationToken);
    Task<AvatarRepositoryInventory> InspectAsync(CancellationToken cancellationToken);
}

internal sealed class AvatarRepository : IAvatarRepository
{
    private readonly string _connectionString;

    internal AvatarRepository(LauncherServerOptions options)
    {
        _connectionString = options.ConnectionString;
    }

    public async Task<AvatarRateLimitDecision> TryConsumeUploadPermitAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using (MySqlCommand profileLock = connection.CreateCommand())
        {
            profileLock.Transaction = transaction;
            profileLock.CommandText =
                "SELECT account_id FROM atlas_launcher_profile WHERE account_id = @accountId FOR UPDATE";
            profileLock.Parameters.AddWithValue("@accountId", accountId);
            if (await profileLock.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException("Profil Atlas introuvable.");
        }

        await using (MySqlCommand cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM atlas_launcher_avatar_upload_attempt
                WHERE account_id = @accountId
                  AND attempted_at < UTC_TIMESTAMP(6) - INTERVAL 2 DAY
                """;
            cleanup.Parameters.AddWithValue("@accountId", accountId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        int recent;
        int daily;
        await using (MySqlCommand count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = """
                SELECT
                    COALESCE(SUM(attempted_at > UTC_TIMESTAMP(6) - INTERVAL 10 MINUTE), 0),
                    COALESCE(SUM(attempted_at > UTC_TIMESTAMP(6) - INTERVAL 1 DAY), 0)
                FROM atlas_launcher_avatar_upload_attempt
                WHERE account_id = @accountId
                """;
            count.Parameters.AddWithValue("@accountId", accountId);
            await using MySqlDataReader reader = await count.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            recent = Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture);
            daily = Convert.ToInt32(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
        }

        if (recent >= AvatarLimits.UploadsPerTenMinutes || daily >= AvatarLimits.UploadsPerDay)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AvatarRateLimitDecision.Reject(
                recent >= AvatarLimits.UploadsPerTenMinutes ? 600 : 86400);
        }

        await using (MySqlCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO atlas_launcher_avatar_upload_attempt (account_id, attempted_at)
                VALUES (@accountId, UTC_TIMESTAMP(6))
                """;
            insert.Parameters.AddWithValue("@accountId", accountId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return AvatarRateLimitDecision.Permit();
    }

    public async Task<AvatarAssetRecord> CreatePendingAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await EnsureProfileExistsAsync(
            connection,
            transaction,
            accountId,
            forUpdate: true,
            cancellationToken);
        ulong version;
        await using (MySqlCommand versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = """
                SELECT COALESCE(MAX(version), 0)
                FROM atlas_launcher_avatar_asset
                WHERE owner_account_id = @accountId
                FOR UPDATE
                """;
            versionCommand.Parameters.AddWithValue("@accountId", accountId);
            ulong current = Convert.ToUInt64(
                await versionCommand.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            version = checked(current + 1);
        }

        Guid id = Guid.NewGuid();
        AvatarStorageKey storageKey = AvatarStorageKey.Create(id, version);
        await using (MySqlCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO atlas_launcher_avatar_asset
                    (id, owner_account_id, version, status, storage_key, created_at, updated_at)
                VALUES
                    (@id, @accountId, @version, @status, @storageKey, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6))
                """;
            AddGuid(insert, "@id", id);
            insert.Parameters.AddWithValue("@accountId", accountId);
            insert.Parameters.AddWithValue("@version", version);
            insert.Parameters.AddWithValue("@status", (byte)AvatarAssetStatus.Pending);
            insert.Parameters.AddWithValue("@storageKey", storageKey.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new AvatarAssetRecord(
            id,
            accountId,
            version,
            AvatarAssetStatus.Pending,
            storageKey,
            now,
            now);
    }

    public async Task<AvatarPublicationResult> PublishReadyAsync(
        uint accountId,
        AvatarAssetRecord pending,
        IReadOnlyList<AvatarStoredVariant> variants,
        CancellationToken cancellationToken)
    {
        ValidateVariants(variants);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        AvatarAssetRecord locked = await LoadAssetAsync(
            connection,
            transaction,
            pending.Id,
            accountId,
            forUpdate: true,
            cancellationToken)
            ?? throw new InvalidOperationException("Asset avatar Pending introuvable.");
        if (locked.Status != AvatarAssetStatus.Pending)
            throw new InvalidOperationException("L'asset avatar n'est plus Pending.");

        AvatarAssetRecord? previous = await LoadActiveAssetAsync(
            connection,
            transaction,
            accountId,
            forUpdate: true,
            cancellationToken);

        foreach (AvatarStoredVariant variant in variants)
        {
            await using MySqlCommand insertVariant = connection.CreateCommand();
            insertVariant.Transaction = transaction;
            insertVariant.CommandText = """
                INSERT INTO atlas_launcher_avatar_variant
                    (avatar_asset_id, size, content_type, byte_length, sha256)
                VALUES
                    (@assetId, @size, @contentType, @byteLength, @sha256)
                """;
            AddGuid(insertVariant, "@assetId", pending.Id);
            insertVariant.Parameters.AddWithValue("@size", variant.Size);
            insertVariant.Parameters.AddWithValue("@contentType", variant.ContentType);
            insertVariant.Parameters.AddWithValue("@byteLength", checked((uint)variant.ByteLength));
            insertVariant.Parameters.Add("@sha256", MySqlDbType.Binary, 32).Value = variant.Sha256;
            await insertVariant.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (MySqlCommand ready = connection.CreateCommand())
        {
            ready.Transaction = transaction;
            ready.CommandText = """
                UPDATE atlas_launcher_avatar_asset
                SET status = @ready, updated_at = UTC_TIMESTAMP(6)
                WHERE id = @assetId
                  AND owner_account_id = @accountId
                  AND status = @pending
                """;
            AddGuid(ready, "@assetId", pending.Id);
            ready.Parameters.AddWithValue("@accountId", accountId);
            ready.Parameters.AddWithValue("@ready", (byte)AvatarAssetStatus.Ready);
            ready.Parameters.AddWithValue("@pending", (byte)AvatarAssetStatus.Pending);
            if (await ready.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException("Transition Pending vers Ready refusee.");
        }

        await using (MySqlCommand activate = connection.CreateCommand())
        {
            activate.Transaction = transaction;
            activate.CommandText = """
                INSERT INTO atlas_launcher_profile_avatar
                    (account_id, current_avatar_asset_id, updated_at)
                VALUES
                    (@accountId, @assetId, UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE
                    current_avatar_asset_id = VALUES(current_avatar_asset_id),
                    updated_at = VALUES(updated_at)
                """;
            activate.Parameters.AddWithValue("@accountId", accountId);
            AddGuid(activate, "@assetId", pending.Id);
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }

        if (previous is not null && previous.Id != pending.Id)
        {
            await using MySqlCommand retire = connection.CreateCommand();
            retire.Transaction = transaction;
            retire.CommandText = """
                UPDATE atlas_launcher_avatar_asset
                SET status = @retired, updated_at = UTC_TIMESTAMP(6)
                WHERE id = @assetId AND status = @ready
                """;
            AddGuid(retire, "@assetId", previous.Id);
            retire.Parameters.AddWithValue("@retired", (byte)AvatarAssetStatus.Retired);
            retire.Parameters.AddWithValue("@ready", (byte)AvatarAssetStatus.Ready);
            await retire.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AvatarPublicationResult(
            AvatarDescriptor.Create(pending.Id, pending.Version),
            previous);
    }

    public async Task MarkPendingDeletedAsync(
        uint accountId,
        Guid avatarId,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE atlas_launcher_avatar_asset
            SET status = @deleted, updated_at = UTC_TIMESTAMP(6)
            WHERE id = @assetId
              AND owner_account_id = @accountId
              AND status = @pending
            """;
        AddGuid(command, "@assetId", avatarId);
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@deleted", (byte)AvatarAssetStatus.Deleted);
        command.Parameters.AddWithValue("@pending", (byte)AvatarAssetStatus.Pending);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AvatarDeletionResult> DeleteActiveAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        AvatarAssetRecord? active = await LoadActiveAssetAsync(
            connection,
            transaction,
            accountId,
            forUpdate: true,
            cancellationToken);
        if (active is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AvatarDeletionResult(false, null);
        }

        await using (MySqlCommand detach = connection.CreateCommand())
        {
            detach.Transaction = transaction;
            detach.CommandText = """
                UPDATE atlas_launcher_profile_avatar
                SET current_avatar_asset_id = NULL, updated_at = UTC_TIMESTAMP(6)
                WHERE account_id = @accountId
                """;
            detach.Parameters.AddWithValue("@accountId", accountId);
            await detach.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (MySqlCommand deleted = connection.CreateCommand())
        {
            deleted.Transaction = transaction;
            deleted.CommandText = """
                UPDATE atlas_launcher_avatar_asset
                SET status = @deleted, updated_at = UTC_TIMESTAMP(6)
                WHERE id = @assetId AND owner_account_id = @accountId
                """;
            AddGuid(deleted, "@assetId", active.Id);
            deleted.Parameters.AddWithValue("@accountId", accountId);
            deleted.Parameters.AddWithValue("@deleted", (byte)AvatarAssetStatus.Deleted);
            await deleted.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new AvatarDeletionResult(true, active);
    }

    public async Task<AvatarDescriptor?> GetActiveDescriptorAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        AvatarAssetRecord? asset = await LoadActiveAssetAsync(
            connection,
            null,
            accountId,
            forUpdate: false,
            cancellationToken);
        return asset is null ? null : AvatarDescriptor.Create(asset.Id, asset.Version);
    }

    public async Task<AvatarMediaRecord?> GetMediaAsync(
        Guid avatarId,
        ulong version,
        int size,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.version, a.storage_key,
                   v.size, v.content_type, v.byte_length, v.sha256
            FROM atlas_launcher_avatar_asset a
            INNER JOIN atlas_launcher_avatar_variant v ON v.avatar_asset_id = a.id
            WHERE a.id = @assetId
              AND a.version = @version
              AND a.status = @ready
              AND v.size = @size
            LIMIT 1
            """;
        AddGuid(command, "@assetId", avatarId);
        command.Parameters.AddWithValue("@version", version);
        command.Parameters.AddWithValue("@ready", (byte)AvatarAssetStatus.Ready);
        command.Parameters.AddWithValue("@size", size);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return new AvatarMediaRecord(
            ReadGuid(reader, "id"),
            reader.GetUInt64("version"),
            AvatarStorageKey.Parse(reader.GetString("storage_key")),
            reader.GetUInt16("size"),
            reader.GetString("content_type"),
            reader.GetUInt32("byte_length"),
            (byte[])reader["sha256"]);
    }

    public async Task<AvatarRepositoryInventory> InspectAsync(CancellationToken cancellationToken)
    {
        List<AvatarRepositoryAssetState> assets = [];
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.owner_account_id, a.version, a.status, a.storage_key,
                   a.created_at, a.updated_at,
                   CASE WHEN pa.current_avatar_asset_id = a.id THEN 1 ELSE 0 END AS is_active
            FROM atlas_launcher_avatar_asset a
            LEFT JOIN atlas_launcher_profile_avatar pa ON pa.current_avatar_asset_id = a.id
            ORDER BY a.owner_account_id, a.version
            """;
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            assets.Add(new AvatarRepositoryAssetState(
                ReadAsset(reader),
                reader.GetBoolean("is_active")));
        }
        return new AvatarRepositoryInventory(assets);
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        MySqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<AvatarAssetRecord?> LoadActiveAssetAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        uint accountId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT a.id, a.owner_account_id, a.version, a.status, a.storage_key,
                   a.created_at, a.updated_at
            FROM atlas_launcher_profile_avatar pa
            INNER JOIN atlas_launcher_avatar_asset a ON a.id = pa.current_avatar_asset_id
            WHERE pa.account_id = @accountId
              AND a.status = @ready
            LIMIT 1
            """ + (forUpdate ? " FOR UPDATE" : "");
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@ready", (byte)AvatarAssetStatus.Ready);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAsset(reader) : null;
    }

    private static async Task<AvatarAssetRecord?> LoadAssetAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        Guid avatarId,
        uint accountId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, owner_account_id, version, status, storage_key, created_at, updated_at
            FROM atlas_launcher_avatar_asset
            WHERE id = @assetId AND owner_account_id = @accountId
            LIMIT 1
            """ + (forUpdate ? " FOR UPDATE" : "");
        AddGuid(command, "@assetId", avatarId);
        command.Parameters.AddWithValue("@accountId", accountId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAsset(reader) : null;
    }

    private static async Task EnsureProfileExistsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        uint accountId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT account_id
            FROM atlas_launcher_profile
            WHERE account_id = @accountId
            LIMIT 1
            """ + (forUpdate ? " FOR UPDATE" : "");
        command.Parameters.AddWithValue("@accountId", accountId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new InvalidOperationException("Profil Atlas introuvable.");
    }

    private static AvatarAssetRecord ReadAsset(MySqlDataReader reader)
        => new(
            ReadGuid(reader, "id"),
            reader.GetUInt32("owner_account_id"),
            reader.GetUInt64("version"),
            (AvatarAssetStatus)reader.GetByte("status"),
            AvatarStorageKey.Parse(reader.GetString("storage_key")),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("created_at"), DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("updated_at"), DateTimeKind.Utc)));

    private static void ValidateVariants(IReadOnlyList<AvatarStoredVariant> variants)
    {
        int[] sizes = variants.Select(item => item.Size).Order().ToArray();
        if (!sizes.SequenceEqual(AvatarVariantSizes.All)
            || variants.Any(item => item.ContentType != "image/png" || item.ByteLength <= 0 || item.Sha256.Length != 32))
        {
            throw new InvalidOperationException("Les quatre variantes PNG sont obligatoires avant publication.");
        }
    }

    private static void AddGuid(MySqlCommand command, string name, Guid value)
        => command.Parameters.Add(name, MySqlDbType.Binary, 16).Value = value.ToByteArray(bigEndian: true);

    private static Guid ReadGuid(MySqlDataReader reader, string name)
        => new((byte[])reader[name], bigEndian: true);
}
