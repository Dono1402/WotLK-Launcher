using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal bool ChatAvailable => _options.MaximumSchemaVersion is null or >= 6;
    private const string ChatMessageColumns = """
        m.id, m.client_message_id, m.sender_account_id, m.recipient_account_id,
        m.sender_username, m.body, m.origin, m.created_at
        """;

    public async Task<LauncherChatSendResult> SendChatMessageAsync(
        uint accountId, uint friendAccountId, SendChatMessageRequest request, CancellationToken cancellationToken)
    {
        RequireChatAvailable();
        string body = ChatMessageValidation.Normalize(request.Body);
        if (request.ClientMessageId == Guid.Empty) throw new ChatOperationException("chat-invalid-request");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using MySqlConnection connection = await OpenAsync(cancellationToken);
                await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                LauncherChatSendResult result = await InsertChatMessageAsync(connection, transaction,
                    accountId, friendAccountId, request.ClientMessageId, body, 0, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (ChatV2Available) ChatV2EventSignal.Pulse();
                return result;
            }
            catch (MySqlException exception) when (exception.Number == 1213 && attempt < 2)
            {
                // A deadlock rolls back the whole transaction. The same UUID preserves send identity.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken);
            }
        }
    }

    public async Task<LauncherChatConversations> ListChatConversationsAsync(
        uint accountId, long? beforeId, int limit, CancellationToken cancellationToken)
    {
        RequireChatAvailable();
        RequireChatPage(beforeId, limit);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken);
        List<LauncherChatConversation> conversations = [];
        string shareFriendRead = ChatV2Available
            ? "COALESCE((SELECT share_read_receipts FROM atlas_launcher_chat_v2_preferences WHERE account_id=p.account_id),TRUE)"
            : "TRUE";
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT p.account_id AS friend_account_id, p.display_username AS friend_username,
                    CASE WHEN c.account_low_id=@account THEN c.low_last_read_message_id ELSE c.high_last_read_message_id END AS own_read_id,
                    CASE WHEN {shareFriendRead} THEN CASE WHEN c.account_low_id=@account THEN c.high_last_read_message_id ELSE c.low_last_read_message_id END ELSE 0 END AS friend_read_id,
                    (SELECT COUNT(*) FROM atlas_launcher_chat_message u
                     WHERE u.account_low_id=c.account_low_id AND u.account_high_id=c.account_high_id
                       AND u.recipient_account_id=@account
                       AND u.id > CASE WHEN c.account_low_id=@account THEN c.low_last_read_message_id ELSE c.high_last_read_message_id END) AS unread_count,
                    {ChatMessageColumns}
                FROM atlas_launcher_chat_conversation c
                INNER JOIN atlas_launcher_friendship f
                    ON f.account_low_id=c.account_low_id AND f.account_high_id=c.account_high_id AND f.accepted_at IS NOT NULL
                INNER JOIN atlas_launcher_profile p
                    ON p.account_id=CASE WHEN c.account_low_id=@account THEN c.account_high_id ELSE c.account_low_id END
                INNER JOIN atlas_launcher_chat_message m ON m.id=c.last_message_id
                WHERE (c.account_low_id=@account OR c.account_high_id=@account)
                    AND (@before IS NULL OR c.last_message_id < @before)
                ORDER BY c.last_message_id DESC
                LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@before", beforeId is null ? DBNull.Value : beforeId.Value);
            command.Parameters.AddWithValue("@limit", limit + 1);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                conversations.Add(new LauncherChatConversation(reader.GetUInt32("friend_account_id"),
                    reader.GetString("friend_username"), ReadChatMessage(reader), ReadChatCount(reader, "unread_count"),
                    reader.GetInt64("own_read_id"), reader.GetInt64("friend_read_id")));
        }
        bool hasMore = conversations.Count > limit;
        if (hasMore) conversations.RemoveAt(conversations.Count - 1);
        long lastMessageId = await GetChatCursorAsync(connection, transaction, accountId, cancellationToken);
        int unreadCount;
        await using (MySqlCommand unread = connection.CreateCommand())
        {
            unread.Transaction = transaction;
            unread.CommandText = """
                SELECT COUNT(*) FROM atlas_launcher_chat_message m
                INNER JOIN atlas_launcher_chat_conversation c
                    ON c.account_low_id=m.account_low_id AND c.account_high_id=m.account_high_id
                INNER JOIN atlas_launcher_friendship f
                    ON f.account_low_id=m.account_low_id AND f.account_high_id=m.account_high_id AND f.accepted_at IS NOT NULL
                WHERE m.recipient_account_id=@account
                    AND m.id > CASE WHEN c.account_low_id=@account THEN c.low_last_read_message_id ELSE c.high_last_read_message_id END;
                """;
            unread.Parameters.AddWithValue("@account", accountId);
            unreadCount = (int)Math.Min(Convert.ToInt64(await unread.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture), int.MaxValue);
        }
        return new LauncherChatConversations(conversations, lastMessageId, unreadCount, hasMore);
    }

    public async Task<LauncherChatMessages> ListChatMessagesAsync(
        uint accountId, uint friendAccountId, long? afterId, long? beforeId, int limit, CancellationToken cancellationToken)
    {
        RequireChatAvailable();
        RequireChatPage(afterId, limit);
        RequireChatPage(beforeId, limit);
        if (afterId is not null && beforeId is not null) throw new ChatOperationException("chat-invalid-query");
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken);
        ChatFriendState friend = await RequireChatFriendAsync(connection, transaction, accountId, friendAccountId, false, cancellationToken);
        List<LauncherChatMessage> messages = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            string order = afterId is null ? "DESC" : "ASC";
            command.CommandText = $"""
                SELECT {ChatMessageColumns} FROM atlas_launcher_chat_message m
                WHERE m.account_low_id=@low AND m.account_high_id=@high
                    AND (@after IS NULL OR m.id > @after) AND (@before IS NULL OR m.id < @before)
                ORDER BY m.id {order} LIMIT @limit;
                """;
            AddChatPair(command, accountId, friendAccountId);
            command.Parameters.AddWithValue("@after", afterId is null ? DBNull.Value : afterId.Value);
            command.Parameters.AddWithValue("@before", beforeId is null ? DBNull.Value : beforeId.Value);
            command.Parameters.AddWithValue("@limit", limit + 1);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) messages.Add(ReadChatMessage(reader));
        }
        bool hasMore = messages.Count > limit;
        if (hasMore) messages.RemoveAt(messages.Count - 1);
        if (afterId is null) messages.Reverse();
        return new LauncherChatMessages(friendAccountId, friend.Username, messages, hasMore, friend.OwnReadId, friend.FriendReadId);
    }

    public async Task<LauncherChatUpdates> PollChatUpdatesAsync(
        uint accountId, long afterId, int limit, CancellationToken cancellationToken)
    {
        RequireChatAvailable();
        RequireChatPage(afterId, limit);
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken);
        List<LauncherChatMessage> messages = [];
        await using (MySqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT {ChatMessageColumns} FROM atlas_launcher_chat_message m
                INNER JOIN atlas_launcher_friendship f
                    ON f.account_low_id=m.account_low_id AND f.account_high_id=m.account_high_id AND f.accepted_at IS NOT NULL
                WHERE (m.sender_account_id=@account OR m.recipient_account_id=@account) AND m.id>@after
                ORDER BY m.id ASC LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@account", accountId);
            command.Parameters.AddWithValue("@after", afterId);
            command.Parameters.AddWithValue("@limit", limit + 1);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) messages.Add(ReadChatMessage(reader));
        }
        bool hasMore = messages.Count > limit;
        if (hasMore) messages.RemoveAt(messages.Count - 1);
        // Both reads share one snapshot. A later commit cannot advance the cursor past unseen messages.
        long cursor = hasMore ? messages[^1].Id
            : Math.Max(afterId, await GetChatCursorAsync(connection, transaction, accountId, cancellationToken));
        return new LauncherChatUpdates(messages, cursor, hasMore);
    }

    public async Task<LauncherChatReadResult> MarkChatReadAsync(
        uint accountId, uint friendAccountId, long throughMessageId, CancellationToken cancellationToken)
    {
        RequireChatAvailable();
        if (throughMessageId <= 0) throw new ChatOperationException("chat-invalid-request");
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (ChatV2Available) await LockV2SequenceAsync(connection, transaction, cancellationToken);
        await RequireChatFriendAsync(connection, transaction, accountId, friendAccountId, true, cancellationToken);
        await using (MySqlCommand exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = """
                SELECT id FROM atlas_launcher_chat_message
                WHERE id=@message AND account_low_id=@low AND account_high_id=@high;
                """;
            AddChatPair(exists, accountId, friendAccountId);
            exists.Parameters.AddWithValue("@message", throughMessageId);
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
                throw new ChatOperationException("chat-invalid-read-cursor");
        }
        string column = accountId < friendAccountId ? "low_last_read_message_id" : "high_last_read_message_id";
        await using (MySqlCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"""
                UPDATE atlas_launcher_chat_conversation SET {column}=GREATEST({column},@message)
                WHERE account_low_id=@low AND account_high_id=@high;
                SELECT {column} FROM atlas_launcher_chat_conversation WHERE account_low_id=@low AND account_high_id=@high;
                """;
            AddChatPair(update, accountId, friendAccountId);
            update.Parameters.AddWithValue("@message", throughMessageId);
            long cursor = Convert.ToInt64(await update.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (ChatV2Available)
            {
                long thread = await EnsureV2DirectAsync(connection, transaction, accountId, friendAccountId, cancellationToken);
                long richCursor = await V2ScalarAsync(connection, transaction,
                    "SELECT COALESCE(MAX(id),0) FROM atlas_launcher_chat_v2_message WHERE thread_id=@thread AND legacy_message_id<=@cursor;",
                    cancellationToken, ("@thread", thread), ("@cursor", cursor));
                await V2ExecuteAsync(connection, transaction,
                    "UPDATE atlas_launcher_chat_v2_member SET last_read_message_id=GREATEST(last_read_message_id,@cursor) WHERE thread_id=@thread AND account_id=@account;",
                    cancellationToken, ("@thread", thread), ("@account", accountId), ("@cursor", richCursor));
                var preferences = await V2PreferencesAsync(connection, transaction, accountId, cancellationToken);
                await EmitV2Async(connection, transaction, "thread", thread, null,
                    preferences.ShareReadReceipts ? null : accountId, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            if (ChatV2Available) ChatV2EventSignal.Pulse();
            return new LauncherChatReadResult(cursor);
        }
    }

    internal async Task<int> ProcessChatGameInboxAsync(int maximum, CancellationToken cancellationToken)
    {
        RequireChatAvailable();
        if (maximum is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximum));
        int processed = 0;
        while (processed < maximum && await ProcessOneChatGameInboxAsync(cancellationToken)) processed++;
        return processed;
    }

    private async Task<bool> ProcessOneChatGameInboxAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        ChatInboxRow? inbox;
        await using (MySqlCommand select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT id, request_id, realm_id, sender_account_id, sender_character_guid, recipient_account_id, body
                FROM atlas_launcher_chat_inbox WHERE status=0 ORDER BY id LIMIT 1 FOR UPDATE SKIP LOCKED;
                """;
            await using MySqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
            inbox = await reader.ReadAsync(cancellationToken)
                ? new ChatInboxRow(reader.GetInt64("id"), new Guid((byte[])reader["request_id"], bigEndian: true),
                    reader.GetUInt32("realm_id"), reader.GetUInt32("sender_account_id"), reader.GetUInt32("sender_character_guid"),
                    reader.GetUInt32("recipient_account_id"), reader.GetString("body")) : null;
        }
        if (inbox is null) return false;
        long? messageId = null;
        string? errorCode = null;
        string? characterName = null;
        try
        {
            if (inbox.RequestId == Guid.Empty || inbox.RealmId == 0 || inbox.CharacterGuid == 0)
                throw new ChatOperationException("chat-invalid-game-identity");
            string body = ChatMessageValidation.Normalize(inbox.Body);
            await using (MySqlCommand character = connection.CreateCommand())
            {
                character.Transaction = transaction;
                character.CommandText = $"SELECT name FROM {ChatCharacterTable()} WHERE guid=@guid AND account=@account LIMIT 1;";
                character.Parameters.AddWithValue("@guid", inbox.CharacterGuid);
                character.Parameters.AddWithValue("@account", inbox.Sender);
                characterName = await character.ExecuteScalarAsync(cancellationToken) as string;
                if (characterName is null)
                    throw new ChatOperationException("chat-invalid-game-identity");
            }
            LauncherChatSendResult result = await InsertChatMessageAsync(connection, transaction, inbox.Sender,
                inbox.Recipient, inbox.RequestId, body, 1, cancellationToken);
            messageId = result.Message.Id;
            if (ChatV2Available)
                await V2ExecuteAsync(connection, transaction,
                    "UPDATE atlas_launcher_chat_v2_message SET sender_character_name=@name WHERE legacy_message_id=@id;",
                    cancellationToken, ("@name", characterName), ("@id", messageId));
        }
        catch (ChatOperationException exception) { errorCode = exception.Code; }
        await using (MySqlCommand acknowledge = connection.CreateCommand())
        {
            acknowledge.Transaction = transaction;
            acknowledge.CommandText = """
                UPDATE atlas_launcher_chat_inbox SET status=@status, message_id=@message,
                    error_code=@error, processed_at=UTC_TIMESTAMP(6) WHERE id=@id AND status=0;
                """;
            acknowledge.Parameters.AddWithValue("@status", errorCode is null ? 1 : 2);
            acknowledge.Parameters.AddWithValue("@message", messageId is null ? DBNull.Value : messageId.Value);
            acknowledge.Parameters.AddWithValue("@error", errorCode is null ? DBNull.Value : errorCode);
            acknowledge.Parameters.AddWithValue("@id", inbox.Id);
            await acknowledge.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (ChatV2Available) ChatV2EventSignal.Pulse();
        return true;
    }

    private async Task<LauncherChatSendResult> InsertChatMessageAsync(MySqlConnection connection,
        MySqlTransaction transaction, uint sender, uint recipient, Guid requestId, string body, byte origin,
        CancellationToken cancellationToken)
    {
        if (ChatV2Available) await LockV2SequenceAsync(connection, transaction, cancellationToken);
        ChatFriendState friend = await RequireChatFriendAsync(connection, transaction, sender, recipient, true, cancellationToken);
        // Every transaction locks both participants in numeric order before allocating an ID.
        // For each account, committed IDs therefore remain ordered even across different friends.
        foreach (uint account in new[] { Math.Min(sender, recipient), Math.Max(sender, recipient) })
        {
            await using MySqlCommand state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO atlas_launcher_chat_account(account_id) VALUES (@account)
                ON DUPLICATE KEY UPDATE account_id=@account;
                """;
            state.Parameters.AddWithValue("@account", account);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        LauncherChatMessage? previous = null;
        await using (MySqlCommand duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = $"""
                SELECT {ChatMessageColumns} FROM atlas_launcher_chat_message m
                WHERE m.sender_account_id=@sender AND m.client_message_id=@request LIMIT 1;
                """;
            duplicate.Parameters.AddWithValue("@sender", sender);
            duplicate.Parameters.Add("@request", MySqlDbType.Binary, 16).Value = requestId.ToByteArray(bigEndian: true);
            await using MySqlDataReader reader = await duplicate.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                previous = ReadChatMessage(reader);
            }
        }
        if (previous is not null)
        {
            bool sameBody = previous.Body == body;
            if (ChatV2Available)
            {
                await using MySqlCommand fingerprint = V2Command(connection, transaction,
                    "SELECT legacy_request_hash FROM atlas_launcher_chat_v2_message WHERE legacy_message_id=@id;", ("@id", previous.Id));
                object? original = await fingerprint.ExecuteScalarAsync(cancellationToken);
                sameBody = original is byte[] hash && hash.SequenceEqual(ChatLegacyHash(recipient, origin, body));
            }
            if (previous.RecipientAccountId != recipient || !sameBody || previous.Origin != (origin == 0 ? "launcher" : "game"))
                throw new ChatOperationException("chat-idempotency-conflict");
            return new LauncherChatSendResult(previous, true);
        }
        await using (MySqlCommand quota = connection.CreateCommand())
        {
            quota.Transaction = transaction;
            quota.CommandText = """
                UPDATE atlas_launcher_chat_account
                SET send_window_count=CASE WHEN send_window_started_at IS NULL OR send_window_started_at<=UTC_TIMESTAMP(6)-INTERVAL 60 SECOND
                        THEN 1 ELSE send_window_count+1 END,
                    send_window_started_at=CASE WHEN send_window_started_at IS NULL OR send_window_started_at<=UTC_TIMESTAMP(6)-INTERVAL 60 SECOND
                        THEN UTC_TIMESTAMP(6) ELSE send_window_started_at END
                WHERE account_id=@sender AND (send_window_started_at IS NULL
                    OR send_window_started_at<=UTC_TIMESTAMP(6)-INTERVAL 60 SECOND OR send_window_count<@maximum);
                """;
            quota.Parameters.AddWithValue("@sender", sender);
            quota.Parameters.AddWithValue("@maximum", ChatMessageValidation.MessagesPerMinute);
            if (await quota.ExecuteNonQueryAsync(cancellationToken) != 1) throw new ChatOperationException("chat-rate-limited");
        }
        await using (MySqlCommand conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                INSERT INTO atlas_launcher_chat_conversation(account_low_id,account_high_id) VALUES (@low,@high)
                ON DUPLICATE KEY UPDATE account_low_id=@low;
                """;
            AddChatPair(conversation, sender, recipient);
            await conversation.ExecuteNonQueryAsync(cancellationToken);
        }
        long id;
        await using (MySqlCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO atlas_launcher_chat_message(client_message_id,account_low_id,account_high_id,
                    sender_account_id,recipient_account_id,sender_username,body,origin,created_at)
                VALUES (@request,@low,@high,@sender,@recipient,@username,@body,@origin,UTC_TIMESTAMP(6));
                """;
            AddChatPair(insert, sender, recipient);
            insert.Parameters.Add("@request", MySqlDbType.Binary, 16).Value = requestId.ToByteArray(bigEndian: true);
            insert.Parameters.AddWithValue("@sender", sender);
            insert.Parameters.AddWithValue("@recipient", recipient);
            insert.Parameters.AddWithValue("@username", friend.SenderUsername);
            insert.Parameters.AddWithValue("@body", body);
            insert.Parameters.AddWithValue("@origin", origin);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            id = insert.LastInsertedId;
        }
        await using (MySqlCommand state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE atlas_launcher_chat_conversation SET last_message_id=@id,updated_at=UTC_TIMESTAMP(6)
                WHERE account_low_id=@low AND account_high_id=@high;
                UPDATE atlas_launcher_chat_account SET last_message_id=@id WHERE account_id IN (@low,@high);
                """;
            AddChatPair(state, sender, recipient);
            state.Parameters.AddWithValue("@id", id);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (MySqlCommand outbox = connection.CreateCommand())
        {
            outbox.Transaction = transaction;
            outbox.CommandText = $"""
                INSERT INTO atlas_launcher_chat_outbox(message_id,recipient_account_id,expires_at)
                SELECT @id,@recipient,UTC_TIMESTAMP(6)+INTERVAL 60 SECOND
                WHERE EXISTS (SELECT 1 FROM {ChatCharacterTable()} WHERE account=@recipient AND online=1);
                """;
            outbox.Parameters.AddWithValue("@id", id);
            outbox.Parameters.AddWithValue("@recipient", recipient);
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }
        LauncherChatMessage message;
        await using (MySqlCommand created = connection.CreateCommand())
        {
            created.Transaction = transaction;
            created.CommandText = $"SELECT {ChatMessageColumns} FROM atlas_launcher_chat_message m WHERE m.id=@id;";
            created.Parameters.AddWithValue("@id", id);
            await using MySqlDataReader createdReader = await created.ExecuteReaderAsync(cancellationToken);
            if (!await createdReader.ReadAsync(cancellationToken)) throw new InvalidDataException("Created chat message is unavailable.");
            message = ReadChatMessage(createdReader);
        }
        if (ChatV2Available) await ImportLegacyV2Async(connection, transaction, message, cancellationToken);
        return new LauncherChatSendResult(message, false);
    }

    private async Task<ChatFriendState> RequireChatFriendAsync(MySqlConnection connection,
        MySqlTransaction transaction, uint accountId, uint friendAccountId, bool lockFriendship, CancellationToken cancellationToken)
    {
        if (accountId == 0 || friendAccountId == 0 || accountId == friendAccountId)
            throw new ChatOperationException("chat-not-friends");
        string shareFriendRead = ChatV2Available
            ? "COALESCE((SELECT share_read_receipts FROM atlas_launcher_chat_v2_preferences WHERE account_id=@friend),TRUE)"
            : "TRUE";
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = lockFriendship ? """
            SELECT p.display_username AS friend_username, own.display_username AS sender_username,
                0 AS own_read_id, 0 AS friend_read_id
            FROM atlas_launcher_friendship f
            INNER JOIN atlas_launcher_profile p ON p.account_id=@friend
            INNER JOIN atlas_launcher_profile own ON own.account_id=@account
            WHERE f.account_low_id=@low AND f.account_high_id=@high AND f.accepted_at IS NOT NULL
            FOR SHARE;
            """ : $"""
            SELECT p.display_username AS friend_username, own.display_username AS sender_username,
                COALESCE(CASE WHEN @account=@low THEN c.low_last_read_message_id ELSE c.high_last_read_message_id END,0) AS own_read_id,
                CASE WHEN {shareFriendRead} THEN COALESCE(CASE WHEN @account=@low THEN c.high_last_read_message_id ELSE c.low_last_read_message_id END,0) ELSE 0 END AS friend_read_id
            FROM atlas_launcher_friendship f
            INNER JOIN atlas_launcher_profile p ON p.account_id=@friend
            INNER JOIN atlas_launcher_profile own ON own.account_id=@account
            LEFT JOIN atlas_launcher_chat_conversation c ON c.account_low_id=f.account_low_id AND c.account_high_id=f.account_high_id
            WHERE f.account_low_id=@low AND f.account_high_id=@high AND f.accepted_at IS NOT NULL
            """;
        AddChatPair(command, accountId, friendAccountId);
        command.Parameters.AddWithValue("@account", accountId);
        command.Parameters.AddWithValue("@friend", friendAccountId);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new ChatOperationException("chat-not-friends");
        return new ChatFriendState(reader.GetString("friend_username"), reader.GetString("sender_username"),
            reader.GetInt64("own_read_id"), reader.GetInt64("friend_read_id"));
    }

    private static async Task<long> GetChatCursorAsync(MySqlConnection connection, MySqlTransaction transaction,
        uint accountId, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_message_id FROM atlas_launcher_chat_account WHERE account_id=@account;";
        command.Parameters.AddWithValue("@account", accountId);
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static LauncherChatMessage ReadChatMessage(MySqlDataReader reader) => new(
        reader.GetInt64("id"), new Guid((byte[])reader["client_message_id"], bigEndian: true),
        reader.GetUInt32("sender_account_id"), reader.GetUInt32("recipient_account_id"),
        reader.GetString("sender_username"), reader.GetString("body"), reader.GetByte("origin") == 0 ? "launcher" : "game",
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("created_at"), DateTimeKind.Utc)));

    private static int ReadChatCount(MySqlDataReader reader, string column) =>
        (int)Math.Min(reader.GetInt64(column), int.MaxValue);

    private static void AddChatPair(MySqlCommand command, uint accountId, uint friendAccountId)
    {
        command.Parameters.AddWithValue("@low", Math.Min(accountId, friendAccountId));
        command.Parameters.AddWithValue("@high", Math.Max(accountId, friendAccountId));
    }

    private static void RequireChatPage(long? cursor, int limit)
    {
        if (cursor < 0 || limit is < 1 or > 100) throw new ChatOperationException("chat-invalid-query");
    }

    private void RequireChatAvailable()
    {
        if (!ChatAvailable) throw new ChatOperationException("chat-unavailable");
    }

    private string ChatCharacterTable()
    {
        string name = _options.CharacterDatabaseName;
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Invalid chat character database name.");
        return $"`{name}`.`characters`";
    }

    private sealed record ChatFriendState(string Username, string SenderUsername, long OwnReadId, long FriendReadId);
    private sealed record ChatInboxRow(long Id, Guid RequestId, uint RealmId, uint Sender, uint CharacterGuid, uint Recipient, string Body);
}
