using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using MySqlConnector;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal bool ChatV2Available => _options.MaximumSchemaVersion is null or >= 7;
    private static string ChatThreadId(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string ChatSerialize<T>(T value) => JsonSerializer.Serialize(value, ChatJson.Options);
    private static T? ChatDeserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, ChatJson.Options);
    private static byte[] ChatHash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static byte[] ChatLegacyHash(uint recipient, byte origin, string body) => ChatHash($"{recipient}:{origin}:{body}");

    private async Task<T> ReadV2Async<T>(Func<MySqlConnection, MySqlTransaction, Task<T>> operation, CancellationToken token)
    {
        if (!ChatV2Available) throw new ChatOperationException("chat-unavailable");
        await using MySqlConnection connection = await OpenAsync(token);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, isReadOnly: true, token);
        return await operation(connection, transaction);
    }

    private async Task<T> MutateV2Async<T>(Func<MySqlConnection, MySqlTransaction, Task<T>> operation, CancellationToken token)
    {
        if (!ChatV2Available) throw new ChatOperationException("chat-unavailable");
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using MySqlConnection connection = await OpenAsync(token);
                await using MySqlTransaction transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
                await LockV2SequenceAsync(connection, transaction, token);
                T result = await operation(connection, transaction);
                await transaction.CommitAsync(token);
                ChatV2EventSignal.Pulse();
                return result;
            }
            catch (MySqlException exception) when (exception.Number == 1213 && attempt < 2)
            { await Task.Delay(20 * (attempt + 1), token); }
        }
    }

    private static async Task LockV2SequenceAsync(MySqlConnection c, MySqlTransaction t, CancellationToken token) =>
        _ = await V2ExecuteAsync(c, t, "UPDATE atlas_launcher_chat_v2_sequence SET revision=revision+1 WHERE id=1;", token);

    private static MySqlCommand V2Command(MySqlConnection c, MySqlTransaction t, string sql, params (string Name, object? Value)[] args)
    {
        MySqlCommand command = c.CreateCommand(); command.Transaction = t; command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static async Task<long> V2ExecuteAsync(MySqlConnection c, MySqlTransaction t, string sql, CancellationToken token,
        params (string Name, object? Value)[] args)
    { await using MySqlCommand command = V2Command(c, t, sql, args); return await command.ExecuteNonQueryAsync(token); }

    private static async Task<long> V2ScalarAsync(MySqlConnection c, MySqlTransaction t, string sql, CancellationToken token,
        params (string Name, object? Value)[] args)
    {
        await using MySqlCommand command = V2Command(c, t, sql, args);
        object? value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<long> V2InsertAsync(MySqlConnection c, MySqlTransaction t, string sql, CancellationToken token,
        params (string Name, object? Value)[] args)
    { await using MySqlCommand command = V2Command(c, t, sql, args); await command.ExecuteNonQueryAsync(token); return command.LastInsertedId; }

    private static async Task EmitV2Async(MySqlConnection c, MySqlTransaction t, string kind, long? thread, long? message,
        uint? account, CancellationToken token, string? payload = null)
    {
        string recipients = account is not null ? "SELECT @account AS account_id" : "SELECT account_id FROM atlas_launcher_chat_v2_member WHERE thread_id=@thread AND status IN(0,1)";
        await V2ExecuteAsync(c, t, $"INSERT INTO atlas_launcher_chat_v2_event(account_id,thread_id,message_id,kind,payload_json) SELECT r.account_id,@thread,@message,@kind,@payload FROM ({recipients}) r;", token,
            ("@account", account), ("@thread", thread), ("@message", message), ("@kind", kind), ("@payload", payload));
    }

    private static Task<long> V2CursorAsync(MySqlConnection c, MySqlTransaction t, uint account, CancellationToken token) =>
        V2ScalarAsync(c, t, "SELECT COALESCE(MAX(id),0) FROM atlas_launcher_chat_v2_event WHERE account_id=@account;", token, ("@account", account));

    private sealed record V2Access(long Id, byte Kind, uint? Low, uint? High, uint Owner, string Title,
        string? AvatarJson, long LastMessage, long Version, string Role, byte Status, long HistoryAfter, long LastRead, bool Pinned, bool Archived);

    private static async Task<V2Access> RequireV2AccessAsync(MySqlConnection c, MySqlTransaction t, uint account, long thread,
        CancellationToken token, bool allowInvited = false)
    {
        await using MySqlCommand command = V2Command(c, t, """
            SELECT th.*,me.role,me.status,me.history_after_id,me.last_read_message_id,me.is_pinned,me.is_archived
            FROM atlas_launcher_chat_v2_thread th JOIN atlas_launcher_chat_v2_member me ON me.thread_id=th.id AND me.account_id=@account
            WHERE th.id=@thread AND (me.status=1 OR (@invited AND me.status=0))
            AND (th.kind=1 OR EXISTS(SELECT 1 FROM atlas_launcher_friendship f WHERE f.account_low_id=th.account_low_id AND f.account_high_id=th.account_high_id AND f.accepted_at IS NOT NULL));
            """, ("@account", account), ("@thread", thread), ("@invited", allowInvited));
        await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token)) throw new ChatOperationException("chat-not-found");
        return new(r.GetInt64("id"), r.GetByte("kind"), r.IsDBNull("account_low_id") ? null : r.GetUInt32("account_low_id"),
            r.IsDBNull("account_high_id") ? null : r.GetUInt32("account_high_id"), r.GetUInt32("owner_account_id"), r.GetString("title"),
            r.IsDBNull("avatar_json") ? null : r.GetString("avatar_json"), r.GetInt64("last_message_id"), r.GetInt64("version"),
            r.GetString("role"), r.GetByte("status"), r.GetInt64("history_after_id"), r.GetInt64("last_read_message_id"), r.GetBoolean("is_pinned"), r.GetBoolean("is_archived"));
    }

    private static readonly ConditionalWeakTable<MySqlTransaction, Dictionary<uint,ChatProfileDto>> V2Profiles=new();

    private async Task<ChatProfileDto> V2ProfileAsync(MySqlConnection c, MySqlTransaction t, uint account, CancellationToken token)
    {
        Dictionary<uint,ChatProfileDto> cache=V2Profiles.GetOrCreateValue(t);
        if(cache.TryGetValue(account,out ChatProfileDto? cached))return cached;
        ChatProfileDto profile;
        string manual="online"; bool idle=false; bool launcherOnline=false;
        if(PresenceAvailable)
        {
            await using MySqlCommand presence=V2Command(c,t,"SELECT manual_status,last_active_at<=UTC_TIMESTAMP(6)-INTERVAL 20 MINUTE is_idle FROM atlas_launcher_presence WHERE account_id=@account;",("@account",account));
            await using MySqlDataReader reader=await presence.ExecuteReaderAsync(token);
            if(await reader.ReadAsync(token)){manual=reader.GetString("manual_status");idle=reader.GetBoolean("is_idle");}
        }
        await using(MySqlCommand command = V2Command(c, t, """
            SELECT p.display_username,aa.id avatar_id,aa.version avatar_version,COALESCE(pref.do_not_disturb,FALSE) do_not_disturb,
              EXISTS(SELECT 1 FROM atlas_launcher_session s WHERE s.account_id=p.account_id AND s.revoked_at IS NULL AND s.access_expires_at>UTC_TIMESTAMP() AND s.updated_at>UTC_TIMESTAMP()-INTERVAL 60 SECOND) launcher_online
            FROM atlas_launcher_profile p LEFT JOIN atlas_launcher_profile_avatar pa ON pa.account_id=p.account_id
            LEFT JOIN atlas_launcher_avatar_asset aa ON aa.id=pa.current_avatar_asset_id AND aa.status=1
            LEFT JOIN atlas_launcher_chat_v2_preferences pref ON pref.account_id=p.account_id WHERE p.account_id=@account;
            """, ("@account", account)))
        {
            await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
            if (!await r.ReadAsync(token)) throw new ChatOperationException("chat-not-found");
            launcherOnline=r.GetBoolean("launcher_online");
            Avatars.AvatarDescriptor? avatar = r.IsDBNull("avatar_id") ? null : Avatars.AvatarDescriptor.Create(new Guid((byte[])r["avatar_id"], bigEndian: true), r.GetUInt64("avatar_version"));
            profile = new ChatProfileDto { AccountId = account, Username = r.GetString("display_username"), Presence = r.GetBoolean("launcher_online") ? (r.GetBoolean("do_not_disturb")?"dnd":"online") : "offline", AvatarUrl = avatar?.Url64, AvatarVersion = avatar?.Version.ToString(CultureInfo.InvariantCulture) };
        }
        await using(MySqlCommand command=V2Command(c,t,$"SELECT guid,name,`class` FROM {ChatCharacterTable()} WHERE account=@account AND online=1 ORDER BY guid LIMIT 1;",("@account",account)))
        {
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
            if(await r.ReadAsync(token))profile=profile with{Presence=profile.Presence=="dnd"?"dnd":"game",CharacterGuid=r.GetUInt32("guid"),CharacterName=r.GetString("name"),CharacterClass=r.GetByte("class").ToString(CultureInfo.InvariantCulture)};
        }
        if(PresenceAvailable)
        {
            bool connected=launcherOnline||profile.CharacterGuid is not null;
            string status=LauncherPresenceStatus.Resolve(manual,connected,idle);
            profile=profile with{Presence=status};
            if(manual=="offline")profile=profile with{CharacterGuid=null,CharacterName=null,CharacterClass=null};
        }
        cache[account]=profile;return profile;
    }

    private static async Task<ChatPreferencesDto> V2PreferencesAsync(MySqlConnection c, MySqlTransaction t, uint account, CancellationToken token)
    {
        await using MySqlCommand command = V2Command(c, t, "SELECT * FROM atlas_launcher_chat_v2_preferences WHERE account_id=@account;", ("@account", account));
        await using MySqlDataReader r = await command.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? new ChatPreferencesDto { DoNotDisturb = r.GetBoolean("do_not_disturb"), ShareReadReceipts = r.GetBoolean("share_read_receipts"), ShareTyping = r.GetBoolean("share_typing") } : new();
    }

    internal Task<ChatPreferencesDto> GetChatV2PreferencesAsync(uint account, CancellationToken token) => ReadV2Async((c,t) => V2PreferencesAsync(c,t,account,token), token);

    internal Task<ChatPreferencesDto> UpdateChatV2PreferencesAsync(uint account, ChatPreferencesRequest request, CancellationToken token) => MutateV2Async(async (c,t) =>
    {
        if(PresenceAvailable && request.DoNotDisturb is bool requestedDnd)
            await V2ExecuteAsync(c,t,"""
                INSERT INTO atlas_launcher_presence(account_id,manual_status,last_active_at) VALUES(@account,@status,UTC_TIMESTAMP(6))
                ON DUPLICATE KEY UPDATE manual_status=@status,version=version+1,updated_at=UTC_TIMESTAMP(6);
                """,token,("@account",account),("@status",requestedDnd?"dnd":"online"));
        await V2ExecuteAsync(c,t,"""
            INSERT INTO atlas_launcher_chat_v2_preferences(account_id,do_not_disturb,share_read_receipts,share_typing) VALUES(@account,COALESCE(@dnd,FALSE),COALESCE(@read,TRUE),COALESCE(@typing,TRUE))
            ON DUPLICATE KEY UPDATE do_not_disturb=COALESCE(@dnd,do_not_disturb),share_read_receipts=COALESCE(@read,share_read_receipts),share_typing=COALESCE(@typing,share_typing);
            """,token,("@account",account),("@dnd",request.DoNotDisturb),("@read",request.ShareReadReceipts),("@typing",request.ShareTyping));
        await EmitV2Async(c,t,"preferences",null,null,account,token);
        if(request.ShareReadReceipts is not null||request.DoNotDisturb is not null||request.ShareTyping==false)
        {
            foreach(long thread in await V2IdsAsync(c,t,"SELECT thread_id FROM atlas_launcher_chat_v2_member WHERE account_id=@account AND status=1;",token,("@account",account)))
            {
                await EmitV2Async(c,t,"thread",thread,null,null,token);
                if(request.ShareTyping==false)
                {
                    ChatTypingDto stopped=new(){ThreadId=ChatThreadId(thread),AccountId=account,Username=(await V2ProfileAsync(c,t,account,token)).Username,ExpiresAt=DateTimeOffset.UtcNow};
                    await EmitV2Async(c,t,"typing",thread,null,null,token,ChatSerialize(stopped));
                }
            }
        }
        return await V2PreferencesAsync(c,t,account,token);
    },token);

    private static async Task<List<long>> V2IdsAsync(MySqlConnection c, MySqlTransaction t, string sql, CancellationToken token, params (string Name, object? Value)[] args)
    {
        List<long> result=[]; await using MySqlCommand command=V2Command(c,t,sql,args); await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
        while(await r.ReadAsync(token)) result.Add(r.GetInt64(0)); return result;
    }

    internal Task<ChatStateDto> GetChatV2StateAsync(uint account,CancellationToken token) => ReadV2Async(async(c,t)=>
    {
        List<long> ids=await V2IdsAsync(c,t,"""
            SELECT th.id FROM atlas_launcher_chat_v2_thread th JOIN atlas_launcher_chat_v2_member me ON me.thread_id=th.id
            WHERE me.account_id=@account AND me.status IN(0,1) AND (th.kind=1 OR EXISTS(SELECT 1 FROM atlas_launcher_friendship f WHERE f.account_low_id=th.account_low_id AND f.account_high_id=th.account_high_id AND f.accepted_at IS NOT NULL))
            ORDER BY me.is_pinned DESC,th.updated_at DESC,th.id DESC;
            """,token,("@account",account));
        List<ChatThreadDto> threads=[]; foreach(long id in ids) threads.Add(await V2ThreadAsync(c,t,account,id,token));
        List<long> contacts=await V2IdsAsync(c,t,"SELECT CASE WHEN account_low_id=@account THEN account_high_id ELSE account_low_id END FROM atlas_launcher_friendship WHERE (account_low_id=@account OR account_high_id=@account) AND accepted_at IS NOT NULL;",token,("@account",account));
        List<ChatProfileDto> profiles=[]; foreach(long id in contacts) profiles.Add(await V2ProfileAsync(c,t,(uint)id,token));
        return new ChatStateDto {Self=await V2ProfileAsync(c,t,account,token), Threads=threads,Contacts=profiles,Preferences=await V2PreferencesAsync(c,t,account,token),EventCursor=await V2CursorAsync(c,t,account,token),Capabilities=["groups","attachments","markdown","replies","reactions","edit-delete","pins","cards","typing","read-receipts","preferences","link-previews","archive"]};
    },token);

    private async Task<ChatThreadDto> V2ThreadAsync(MySqlConnection c,MySqlTransaction t,uint account,long id,CancellationToken token)
    {
        V2Access access=await RequireV2AccessAsync(c,t,account,id,token,true);
        List<(uint Account,string Role,byte Status,DateTimeOffset Joined,long Read)> rows=[];
        await using(MySqlCommand command=V2Command(c,t,"""
            SELECT m.*,COALESCE(p.share_read_receipts,TRUE) share_read FROM atlas_launcher_chat_v2_member m
            LEFT JOIN atlas_launcher_chat_v2_preferences p ON p.account_id=m.account_id WHERE m.thread_id=@id AND m.status IN(0,1) ORDER BY m.joined_at,m.account_id;
            """,("@id",id)))
        {
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
            while(await r.ReadAsync(token)) rows.Add((r.GetUInt32("account_id"),r.GetString("role"),r.GetByte("status"),V2Date(r,"joined_at"), r.GetUInt32("account_id")==account||r.GetBoolean("share_read")?r.GetInt64("last_read_message_id"):0));
        }
        List<ChatMemberDto> members=[]; foreach(var m in rows) members.Add(new(){Profile=await V2ProfileAsync(c,t,m.Account,token),Role=m.Role,Status=m.Status==0?"invited":"active",JoinedAt=m.Joined,LastReadMessageId=m.Read});
        ChatMessageDto? last=access.Status==1 && access.LastMessage>access.HistoryAfter ? await V2MessageAsync(c,t,account,access,access.LastMessage,token):null;
        List<ChatMessageDto> pinned=[];
        if(access.Status==1) foreach(long mid in await V2IdsAsync(c,t,"SELECT id FROM atlas_launcher_chat_v2_message WHERE thread_id=@id AND id>@after AND is_pinned AND deleted_at IS NULL ORDER BY id DESC LIMIT 50;",token,("@id",id),("@after",access.HistoryAfter))) pinned.Add(await V2MessageAsync(c,t,account,access,mid,token));
        long unread=access.Status==1? await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_message WHERE thread_id=@id AND id>GREATEST(@history,@read) AND sender_account_id<>@account AND deleted_at IS NULL;",token,("@id",id),("@history",access.HistoryAfter),("@read",access.LastRead),("@account",account)):0;
        ChatAttachmentDto? avatar=access.AvatarJson is null?null:ChatDeserialize<ChatAttachmentDto>(access.AvatarJson);
        return new(){Id=ChatThreadId(id),Kind=access.Kind==0?"direct":"group",Title=access.Kind==0?members.FirstOrDefault(m=>m.Profile.AccountId!=account)?.Profile.Username??"":access.Title,AvatarAttachmentId=avatar?.Id,AvatarUrl=avatar?.Url,Members=members,LastMessage=last,UnreadCount=(int)Math.Min(unread,int.MaxValue),LastReadMessageId=access.LastRead,IsPinned=access.Pinned,IsArchived=access.Archived,CanSend=access.Status==1,CanManage=access.Kind==1&&access.Status==1&&access.Role is "owner" or "admin",IsInvited=access.Status==0,Version=access.Version,PinnedMessages=pinned};
    }

    private static DateTimeOffset V2Date(MySqlDataReader r,string name)=>new(DateTime.SpecifyKind(r.GetDateTime(name),DateTimeKind.Utc));

    private async Task<ChatMessageDto> V2MessageAsync(MySqlConnection c,MySqlTransaction t,uint account,V2Access access,long message,CancellationToken token)
    {
        if(access.Status!=1||message<=access.HistoryAfter) throw new ChatOperationException("chat-not-found");
        ChatMessageDto result; long? reply;
        await using(MySqlCommand command=V2Command(c,t,"SELECT * FROM atlas_launcher_chat_v2_message WHERE thread_id=@thread AND id=@id;",("@thread",access.Id),("@id",message)))
        {
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token); if(!await r.ReadAsync(token)) throw new ChatOperationException("chat-not-found");
            bool deleted=!r.IsDBNull("deleted_at"); reply=r.IsDBNull("reply_to_message_id")?null:r.GetInt64("reply_to_message_id");
            result=new(){Id=message,ThreadId=ChatThreadId(access.Id),ClientMessageId=new Guid((byte[])r["client_message_id"],bigEndian:true),Sender=new(){AccountId=r.GetUInt32("sender_account_id"),Username=r.GetString("sender_username")},Body=deleted?"":r.GetString("body"),Origin=r.GetByte("origin")==1?"game":"launcher",SenderCharacterName=r.IsDBNull("sender_character_name")?null:r.GetString("sender_character_name"),CreatedAt=V2Date(r,"created_at"),EditedAt=r.IsDBNull("edited_at")?null:V2Date(r,"edited_at"),DeletedAt=deleted?V2Date(r,"deleted_at"):null,Version=r.GetInt64("version"),Attachments=deleted?[]:ChatDeserialize<ChatAttachmentDto[]>(r.GetString("attachments_json"))??[],LinkPreviews=deleted?[]:ChatDeserialize<ChatLinkPreviewDto[]>(r.GetString("previews_json"))??[],IsPinned=!deleted&&r.GetBoolean("is_pinned"),Card=deleted||r.IsDBNull("card_json")?null:ChatDeserialize<ChatCardDto>(r.GetString("card_json"))};
        }
        if(reply is not null && reply>access.HistoryAfter)
        {
            await using MySqlCommand command=V2Command(c,t,"SELECT id,sender_username,body,deleted_at FROM atlas_launcher_chat_v2_message WHERE id=@id AND thread_id=@thread;",("@id",reply),("@thread",access.Id));
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
            if(await r.ReadAsync(token)) result=result with{ReplyTo=new(){MessageId=reply.Value,SenderUsername=r.GetString("sender_username"),Body=r.IsDBNull("deleted_at")?r.GetString("body"):"",IsDeleted=!r.IsDBNull("deleted_at")}};
        }
        Dictionary<string,List<uint>> reactions=new(StringComparer.Ordinal);
        if(result.DeletedAt is null)
        {
            await using MySqlCommand command=V2Command(c,t,"SELECT emoji,account_id FROM atlas_launcher_chat_v2_reaction WHERE message_id=@id ORDER BY emoji,account_id;",("@id",message));
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token); while(await r.ReadAsync(token)){string emoji=r.GetString(0);if(!reactions.TryGetValue(emoji,out List<uint>? users)) reactions[emoji]=users=[];users.Add(r.GetUInt32(1));}
        }
        List<long> reads=await V2IdsAsync(c,t,"""
            SELECT m.account_id FROM atlas_launcher_chat_v2_member m LEFT JOIN atlas_launcher_chat_v2_preferences p ON p.account_id=m.account_id
            WHERE m.thread_id=@thread AND m.status=1 AND m.account_id<>@sender AND m.history_after_id<@id AND m.last_read_message_id>=@id AND (m.account_id=@account OR COALESCE(p.share_read_receipts,TRUE));
            """,token,("@thread",access.Id),("@id",message),("@sender",result.Sender.AccountId),("@account",account));
        return result with{Sender=await V2ProfileAsync(c,t,result.Sender.AccountId,token),Reactions=reactions.Select(x=>new ChatReactionDto(){Emoji=x.Key,AccountIds=x.Value}).ToArray(),ReadByAccountIds=reads.Select(x=>(uint)x).ToArray()};
    }

    internal Task<ChatMessagesPageDto> GetChatV2MessagesAsync(uint account,long thread,long? before,int limit,CancellationToken token) => ReadV2Async(async(c,t)=>
    {
        RequireChatPage(before,limit); V2Access access=await RequireV2AccessAsync(c,t,account,thread,token);
        List<long> ids=await V2IdsAsync(c,t,"SELECT id FROM atlas_launcher_chat_v2_message WHERE thread_id=@thread AND id>@history AND (@before IS NULL OR id<@before) ORDER BY id DESC LIMIT @limit;",token,("@thread",thread),("@history",access.HistoryAfter),("@before",before),("@limit",limit+1));
        bool more=ids.Count>limit;if(more)ids.RemoveAt(ids.Count-1);ids.Reverse();List<ChatMessageDto> messages=[];
        foreach(long id in ids)messages.Add(await V2MessageAsync(c,t,account,access,id,token));
        return new ChatMessagesPageDto(){Thread=await V2ThreadAsync(c,t,account,thread,token),Messages=messages,HasEarlier=more,EventCursor=await V2CursorAsync(c,t,account,token)};
    },token);

    internal Task<ChatSendMessageResult> GetChatV2MessageByClientAsync(uint account,long thread,Guid request,CancellationToken token) => ReadV2Async(async(c,t)=>
    {
        V2Access access=await RequireV2AccessAsync(c,t,account,thread,token);long id=await V2ScalarAsync(c,t,"SELECT id FROM atlas_launcher_chat_v2_message WHERE sender_account_id=@account AND thread_id=@thread AND client_message_id=@request;",token,("@account",account),("@thread",thread),("@request",request.ToByteArray(bigEndian:true)));
        return new ChatSendMessageResult(){Message=await V2MessageAsync(c,t,account,access,id,token),IsDuplicate=true};
    },token);
}
