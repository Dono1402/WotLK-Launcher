using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MySqlConnector;
using WotLK.Launcher.Server;

internal static class ChatBridgeSqlMySqlTests
{
    internal static async Task<int> RunAsync(LauncherDatabase database, MySqlConnection connection)
    {
        string path = FindContract();
        string content = await File.ReadAllTextAsync(path);
        Dictionary<string, string> sql = Regex.Matches(content,
            @"(?ms)^-- (EXPIRE|CLAIM|READ|ACK|INBOX|RECEIPT)\b[^\r\n]*\r?\n(.*?)(?=^-- (?:EXPIRE|CLAIM|READ|ACK|INBOX|RECEIPT)\b|\z)")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value, StringComparer.Ordinal);
        if (sql.Count != 6) throw new InvalidDataException("The core bridge SQL fixture contract changed.");
        int checks = 0;
        void Require(bool value, string message)
        {
            checks++;
            if (!value) throw new InvalidOperationException(message);
        }
        await ExecuteAsync(connection, null, "UPDATE atlas_launcher_chat_outbox SET status=3 WHERE status=0;");
        LauncherChatMessage leaseMessage = (await database.SendChatMessageAsync(13, 12, new(Guid.NewGuid(), "SQL lease fixture"), CancellationToken.None)).Message;
        LauncherChatMessage expiredMessage = (await database.SendChatMessageAsync(13, 12, new(Guid.NewGuid(), "SQL expiry fixture"), CancellationToken.None)).Message;
        LauncherChatMessage otherRealm = (await database.SendChatMessageAsync(13, 12, new(Guid.NewGuid(), "SQL realm fixture"), CancellationToken.None)).Message;
        await ExecuteAsync(connection, null, """
            UPDATE atlas_launcher_chat_outbox SET expires_at=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE message_id=@expired;
            UPDATE atlas_launcher_chat_outbox SET realm_id=2 WHERE message_id=@other;
            SET SESSION time_zone='+05:30';
            """, ("@expired", expiredMessage.Id), ("@other", otherRealm.Id));
        byte[] firstToken = Guid.NewGuid().ToByteArray(bigEndian: true);
        byte[] secondToken = Guid.NewGuid().ToByteArray(bigEndian: true);
        try
        {
            await ClaimAsync(firstToken);
            Require(await StatusAsync(leaseMessage.Id) == 1, "A live recipient's realm-zero outbox row must be leased.");
            Require(await StatusAsync(expiredMessage.Id) == 4, "The exact core EXPIRE statement must terminate an expired row.");
            Require(await StatusAsync(otherRealm.Id) == 0, "The exact core CLAIM must not claim another realm's assigned row.");
            Require(await ReadAsync(firstToken, expectedFriendship: true) == 1, "The core READ must expose only its current lease.");
            await ClaimAsync(secondToken);
            Require(await ReadAsync(secondToken, expectedFriendship: true) == 0, "A fresh lease must not be stolen by a second worker.");
            Require(await AckAsync(secondToken, leaseMessage.Id, 2) == 0 && await StatusAsync(leaseMessage.Id) == 1,
                "An ACK with the wrong lease token must leave the row unchanged.");
            await ExecuteAsync(connection, null, "UPDATE atlas_launcher_chat_outbox SET lease_until=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE message_id=@id;", ("@id", leaseMessage.Id));
            await ClaimAsync(secondToken);
            Require(await ReadAsync(firstToken, expectedFriendship: true) == 0 && await ReadAsync(secondToken, expectedFriendship: true) == 1,
                "Only an expired lease may transfer to a fresh token.");
            Require(await AckAsync(firstToken, leaseMessage.Id, 2) == 0, "A stale worker must not acknowledge a transferred lease.");
            await ExecuteAsync(connection, null, "DELETE FROM atlas_launcher_friendship WHERE account_low_id=12 AND account_high_id=13;");
            Require(await ReadAsync(secondToken, expectedFriendship: false) == 1, "The core READ must reflect friendship removal before delivery.");
            await ExecuteAsync(connection, null, "INSERT INTO atlas_launcher_friendship(account_low_id,account_high_id,requested_by_id,accepted_at) VALUES(12,13,12,UTC_TIMESTAMP());");
            Require(await AckAsync(secondToken, leaseMessage.Id, 2) == 1 && await StatusAsync(leaseMessage.Id) == 2,
                "The current token must be able to acknowledge delivery once.");
            Require(await AckAsync(secondToken, leaseMessage.Id, 2) == 0, "Repeating an ACK must not mutate an already delivered row.");
            await using (MySqlCommand delivered = Command(connection, null,
                "SELECT delivered_character_guid,lease_token,lease_until,attempts FROM atlas_launcher_chat_outbox WHERE message_id=@id;", ("@id", leaseMessage.Id)))
            await using (MySqlDataReader reader = await delivered.ExecuteReaderAsync())
            {
                Require(await reader.ReadAsync() && reader.GetUInt32(0) == 1201 && reader.IsDBNull(1) && reader.IsDBNull(2) && reader.GetUInt16(3) == 2,
                    "ACK must record the receiving character, clear its lease and preserve the attempt count.");
            }
            Guid request = Guid.NewGuid();
            var parameters = new (string Name, object Value)[] { ("@request", request.ToByteArray(bigEndian: true)), ("@realm", 1),
                ("@sender", 13), ("@sender_character", 1301), ("@recipient", 12), ("@body", "first game body") };
            await ExecuteAsync(connection, null, sql["INBOX"], parameters);
            await ExecuteAsync(connection, null, sql["INBOX"], parameters.Select(x => x.Name == "@body" ? (x.Name, (object)"forbidden replacement") : x).ToArray());
            await using (MySqlCommand original = Command(connection, null,
                "SELECT body,COUNT(*) OVER() FROM atlas_launcher_chat_inbox WHERE request_id=@request;", ("@request", request.ToByteArray(bigEndian: true))))
            await using (MySqlDataReader reader = await original.ExecuteReaderAsync())
                Require(await reader.ReadAsync() && reader.GetString(0) == "first game body" && reader.GetInt64(1) == 1,
                    "The exact core inbox retry statement must preserve the original identity and body.");
            Require(await database.ProcessChatGameInboxAsync(32, CancellationToken.None) == 1, "The core SQL inbox row must be imported through the API code.");
            await using (MySqlCommand receipt = Command(connection, null, sql["RECEIPT"], ("@realm", 1), ("@request", request.ToByteArray(bigEndian: true))))
            await using (MySqlDataReader reader = await receipt.ExecuteReaderAsync())
                Require(await reader.ReadAsync() && reader.GetString(0) == request.ToString("N") && reader.GetByte(1) == 1,
                    "The core receipt must return the same UUID in network byte order after durable acceptance.");
            Console.WriteLine($"Chat game bridge SQL PASS: {checks} assertions; exact contract SHA256={Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant()}; leases, token ACK, TTL, realm, friendship, inbox idempotence and UTC arithmetic exercised on MySQL with session timezone +05:30.");
            return checks;
        }
        finally { await ExecuteAsync(connection, null, "SET SESSION time_zone='+00:00';"); }

        async Task ClaimAsync(byte[] token)
        {
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await ExecuteAsync(connection, transaction, sql["EXPIRE"], ("@realm", 1));
            await ExecuteAsync(connection, transaction, sql["CLAIM"], ("@realm", 1), ("@account", 12), ("@token", token));
            await transaction.CommitAsync();
        }
        async Task<int> ReadAsync(byte[] token, bool expectedFriendship)
        {
            await using MySqlCommand command = Command(connection, null, sql["READ"], ("@realm", 1), ("@token", token));
            await using MySqlDataReader reader = await command.ExecuteReaderAsync();
            int rows = 0;
            while (await reader.ReadAsync())
            {
                rows++;
                Require(reader.GetInt64("message_id") == leaseMessage.Id && reader.GetUInt32("recipient_account_id") == 12
                    && reader.GetString("sender_username") == "Atlas13" && reader.GetBoolean("still_friends") == expectedFriendship,
                    "The core claim query returned unexpected ownership or friendship.");
                long expectedMicros = (leaseMessage.CreatedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10;
                Require(reader.GetInt64("created_utc_micros") == expectedMicros, "The bridge UTC conversion must not depend on the connection timezone.");
            }
            return rows;
        }
        async Task<int> AckAsync(byte[] token, long message, int status) =>
            await ExecuteAsync(connection, null, sql["ACK"], ("@realm", 1), ("@token", token), ("@message", message),
                ("@status", status), ("@error", DBNull.Value), ("@character", 1201));
        async Task<int> StatusAsync(long message)
        {
            await using MySqlCommand command = Command(connection, null,
                "SELECT status FROM atlas_launcher_chat_outbox WHERE message_id=@message;", ("@message", message));
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }

    private static string FindContract()
    {
        string? configured = Environment.GetEnvironmentVariable("ATLAS_CHAT_BRIDGE_SQL");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        for (int level = 0; directory is not null && level < 10; directory = directory.Parent, level++)
        {
            string candidate = Path.Combine(directory.FullName, "mod-atlas-chat", "tests", "bridge-contract.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Set ATLAS_CHAT_BRIDGE_SQL to the local mod-atlas-chat/tests/bridge-contract.sql file.");
    }

    private static MySqlCommand Command(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object Value)[] values)
    {
        MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return command;
    }
    private static async Task<int> ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object Value)[] values)
    {
        await using MySqlCommand command = Command(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync();
    }
}
