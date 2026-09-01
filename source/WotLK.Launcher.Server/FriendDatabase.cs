using System.Data;
using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    public async Task<IReadOnlyList<LauncherFriend>> ListFriendsAsync(
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        List<FriendAccountRow> accounts = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    CASE
                        WHEN f.account_low_id = @accountId THEN f.account_high_id
                        ELSE f.account_low_id
                    END AS friend_account_id,
                    p.display_username,
                    p.avatar_key,
                    CASE
                        WHEN f.accepted_at IS NOT NULL THEN 'accepted'
                        WHEN f.requested_by_id = @accountId THEN 'outgoing'
                        ELSE 'incoming'
                    END AS relationship
                FROM atlas_launcher_friendship f
                INNER JOIN atlas_launcher_profile p
                    ON p.account_id = CASE
                        WHEN f.account_low_id = @accountId THEN f.account_high_id
                        ELSE f.account_low_id
                    END
                WHERE f.account_low_id = @accountId
                   OR f.account_high_id = @accountId
                ORDER BY
                    CASE
                        WHEN f.accepted_at IS NULL AND f.requested_by_id <> @accountId THEN 0
                        WHEN f.accepted_at IS NULL THEN 1
                        ELSE 2
                    END,
                    p.display_username;
                """;
            command.Parameters.AddWithValue("@accountId", accountId);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(new FriendAccountRow(
                    reader.GetUInt32("friend_account_id"),
                    reader.GetString("display_username"),
                    reader.IsDBNull("avatar_key") ? null : reader.GetString("avatar_key"),
                    reader.GetString("relationship")));
            }
        }

        List<LauncherFriend> friends = new(accounts.Count);
        foreach (FriendAccountRow account in accounts)
        {
            FriendCharacterRow? character = await LoadFriendCharacterAsync(
                connection,
                account.AccountId,
                cancellationToken);
            friends.Add(new LauncherFriend(
                account.AccountId,
                account.Username,
                account.AvatarKey,
                account.Relationship,
                character?.Online ?? false,
                character?.Name,
                character?.Level,
                character?.ClassId,
                character?.ZoneId,
                character?.LastSeenAt));
        }

        return friends;
    }

    public async Task<FriendRequestResult> SendFriendRequestAsync(
        uint accountId,
        string username,
        CancellationToken cancellationToken)
    {
        string normalizedUsername = username.Trim().ToUpperInvariant();
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction =
            await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        FriendTarget? target = await FindFriendTargetAsync(
            connection,
            transaction,
            normalizedUsername,
            cancellationToken);
        if (target is null)
            return new FriendRequestResult(FriendRequestOutcome.NotFound, null, null);
        if (target.AccountId == accountId)
            return new FriendRequestResult(FriendRequestOutcome.Self, target.AccountId, target.Username);

        uint low = Math.Min(accountId, target.AccountId);
        uint high = Math.Max(accountId, target.AccountId);
        await using (MySqlCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT requested_by_id, accepted_at
                FROM atlas_launcher_friendship
                WHERE account_low_id = @low
                  AND account_high_id = @high
                FOR UPDATE;
                """;
            select.Parameters.AddWithValue("@low", low);
            select.Parameters.AddWithValue("@high", high);
            await using MySqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                uint requestedBy = reader.GetUInt32("requested_by_id");
                bool accepted = !reader.IsDBNull("accepted_at");
                await reader.DisposeAsync();

                if (accepted)
                    return new FriendRequestResult(FriendRequestOutcome.AlreadyFriends, target.AccountId, target.Username);
                if (requestedBy == accountId)
                    return new FriendRequestResult(FriendRequestOutcome.AlreadyPending, target.AccountId, target.Username);

                await using MySqlCommand accept = connection.CreateCommand();
                accept.Transaction = transaction;
                accept.CommandText = """
                    UPDATE atlas_launcher_friendship
                    SET accepted_at = UTC_TIMESTAMP(), updated_at = UTC_TIMESTAMP()
                    WHERE account_low_id = @low
                      AND account_high_id = @high;
                    """;
                accept.Parameters.AddWithValue("@low", low);
                accept.Parameters.AddWithValue("@high", high);
                await accept.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new FriendRequestResult(FriendRequestOutcome.Accepted, target.AccountId, target.Username);
            }
        }

        await using (MySqlCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO atlas_launcher_friendship
                    (account_low_id, account_high_id, requested_by_id)
                VALUES
                    (@low, @high, @requestedBy);
                """;
            insert.Parameters.AddWithValue("@low", low);
            insert.Parameters.AddWithValue("@high", high);
            insert.Parameters.AddWithValue("@requestedBy", accountId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new FriendRequestResult(FriendRequestOutcome.Requested, target.AccountId, target.Username);
    }

    public async Task<bool> AcceptFriendAsync(
        uint accountId,
        uint friendAccountId,
        CancellationToken cancellationToken)
    {
        uint low = Math.Min(accountId, friendAccountId);
        uint high = Math.Max(accountId, friendAccountId);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE atlas_launcher_friendship
            SET accepted_at = UTC_TIMESTAMP(), updated_at = UTC_TIMESTAMP()
            WHERE account_low_id = @low
              AND account_high_id = @high
              AND requested_by_id = @friendAccountId
              AND accepted_at IS NULL;
            """;
        command.Parameters.AddWithValue("@low", low);
        command.Parameters.AddWithValue("@high", high);
        command.Parameters.AddWithValue("@friendAccountId", friendAccountId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> RemoveFriendAsync(
        uint accountId,
        uint friendAccountId,
        CancellationToken cancellationToken)
    {
        uint low = Math.Min(accountId, friendAccountId);
        uint high = Math.Max(accountId, friendAccountId);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM atlas_launcher_friendship
            WHERE account_low_id = @low
              AND account_high_id = @high;
            """;
        command.Parameters.AddWithValue("@low", low);
        command.Parameters.AddWithValue("@high", high);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static async Task<FriendTarget?> FindFriendTargetAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string normalizedUsername,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.account_id, p.display_username
            FROM account a
            INNER JOIN atlas_launcher_profile p ON p.account_id = a.id
            WHERE BINARY a.username = BINARY @username
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@username", normalizedUsername);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new FriendTarget(reader.GetUInt32("account_id"), reader.GetString("display_username"))
            : null;
    }

    private async Task<FriendCharacterRow?> LoadFriendCharacterAsync(
        MySqlConnection connection,
        uint accountId,
        CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT guid, name, level, `class`, zone, online, logout_time
            FROM `{_options.CharacterDatabaseName}`.`characters`
            WHERE account = @accountId
            ORDER BY online DESC, logout_time DESC, guid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@accountId", accountId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        uint logoutTime = reader.GetUInt32("logout_time");
        return new FriendCharacterRow(
            reader.GetString("name"),
            reader.GetByte("level"),
            reader.GetByte("class"),
            reader.GetUInt32("zone"),
            reader.GetBoolean("online"),
            logoutTime == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(logoutTime));
    }

    private sealed record FriendAccountRow(
        uint AccountId,
        string Username,
        string? AvatarKey,
        string Relationship);

    private sealed record FriendTarget(uint AccountId, string Username);

    private sealed record FriendCharacterRow(
        string Name,
        byte Level,
        byte ClassId,
        uint ZoneId,
        bool Online,
        DateTimeOffset? LastSeenAt);
}
