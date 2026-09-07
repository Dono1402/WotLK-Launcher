using MySqlConnector;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal bool PresenceAvailable => _options.MaximumSchemaVersion is null or >= 8;

    internal Task<LauncherPresenceStateDto> UpdatePresenceAsync(uint account, LauncherPresenceUpdateRequest request, CancellationToken token)
    {
        if (!PresenceAvailable) throw new ChatOperationException("presence-unavailable");
        if (request.IdleSeconds < 0 || (request.Status is not null && !LauncherPresenceStatus.IsValid(request.Status)))
            throw new ChatOperationException("presence-invalid-request");
        return MutateV2Async(async (c,t) =>
        {
            await V2ExecuteAsync(c,t,"""
                INSERT IGNORE INTO atlas_launcher_presence(account_id,manual_status,last_active_at)
                SELECT p.account_id,IF(COALESCE(pref.do_not_disturb,FALSE),'dnd','online'),TIMESTAMPADD(SECOND,-@idle,UTC_TIMESTAMP(6))
                FROM atlas_launcher_profile p LEFT JOIN atlas_launcher_chat_v2_preferences pref ON pref.account_id=p.account_id WHERE p.account_id=@account;
                """,token,("@account",account),("@idle",request.IdleSeconds));
            PresenceRow before = await ReadPresenceRowAsync(c,t,account,token);
            await V2ExecuteAsync(c,t,"""
                UPDATE atlas_launcher_presence SET manual_status=COALESCE(@status,manual_status),
                    last_active_at=GREATEST(last_active_at,TIMESTAMPADD(SECOND,-@idle,UTC_TIMESTAMP(6))),updated_at=UTC_TIMESTAMP(6)
                WHERE account_id=@account;
                """,token,("@account",account),("@status",request.Status),("@idle",request.IdleSeconds));
            PresenceRow after = await ReadPresenceRowAsync(c,t,account,token);
            string effective = LauncherPresenceStatus.Resolve(after.ManualStatus, true, after.IsIdle);
            bool changed = before.ManualStatus != after.ManualStatus || before.EffectiveStatus != effective;
            await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_presence SET effective_status=@effective,version=version+@changed WHERE account_id=@account;",token,
                ("@account",account),("@effective",effective),("@changed",changed ? 1 : 0));
            bool dnd = after.ManualStatus == "dnd";
            await V2ExecuteAsync(c,t,"""
                INSERT INTO atlas_launcher_chat_v2_preferences(account_id,do_not_disturb) VALUES(@account,@dnd)
                ON DUPLICATE KEY UPDATE do_not_disturb=@dnd;
                """,token,("@account",account),("@dnd",dnd));
            if (changed)
            {
                await EmitV2Async(c,t,"preferences",null,null,account,token);
                await EmitPresenceChangedAsync(c,t,account,token);
                if(after.ManualStatus=="offline")
                    foreach(long thread in await V2IdsAsync(c,t,"SELECT thread_id FROM atlas_launcher_chat_v2_member WHERE account_id=@account AND status=1;",token,("@account",account)))
                        await EmitV2Async(c,t,"typing",thread,null,null,token,ChatSerialize(new ChatTypingDto{ThreadId=ChatThreadId(thread),AccountId=account,
                            Username=(await V2ProfileAsync(c,t,account,token)).Username,ExpiresAt=DateTimeOffset.UtcNow}));
            }
            return new LauncherPresenceStateDto { AccountId=account,Status=effective,ManualStatus=after.ManualStatus,
                IsAutomaticAway=after.ManualStatus=="online" && effective=="away",Version=after.Version+(changed?1:0) };
        },token);
    }

    private sealed record PresenceRow(string ManualStatus,string EffectiveStatus,bool IsIdle,long Version);
    private static async Task<PresenceRow> ReadPresenceRowAsync(MySqlConnection c,MySqlTransaction t,uint account,CancellationToken token)
    {
        await using MySqlCommand command=V2Command(c,t,"SELECT manual_status,effective_status,last_active_at<=UTC_TIMESTAMP(6)-INTERVAL 20 MINUTE is_idle,version FROM atlas_launcher_presence WHERE account_id=@account;",("@account",account));
        await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
        if(!await r.ReadAsync(token))throw new ChatOperationException("presence-not-found");
        return new(r.GetString("manual_status"),r.GetString("effective_status"),r.GetBoolean("is_idle"),r.GetInt64("version"));
    }

    private static async Task EmitPresenceChangedAsync(MySqlConnection c,MySqlTransaction t,uint account,CancellationToken token)
    {
        foreach(long thread in await V2IdsAsync(c,t,"SELECT thread_id FROM atlas_launcher_chat_v2_member WHERE account_id=@account AND status=1;",token,("@account",account)))
            await EmitV2Async(c,t,"thread",thread,null,null,token);
        // Contacts without a conversation are refreshed by the normal directory poll.
    }
}
