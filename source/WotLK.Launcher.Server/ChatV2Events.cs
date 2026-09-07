using System.Collections.Concurrent;
using System.Data;
using MySqlConnector;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

internal static class ChatV2EventSignal
{
    private static readonly object Gate=new();
    private static TaskCompletionSource _next=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static Task Observe(){lock(Gate)return _next.Task;}
    internal static void Pulse(){TaskCompletionSource previous;lock(Gate){previous=_next;_next=new(TaskCreationOptions.RunContinuationsAsynchronously);}previous.TrySetResult();}
}

public sealed partial class LauncherDatabase
{
    private static readonly ConcurrentDictionary<(uint Account,long Thread),DateTimeOffset> V2TypingSent=new();

    internal async Task<ChatEventsDto> PollChatV2EventsAsync(uint account,long after,int limit,int waitSeconds,CancellationToken token)
    {
        RequireChatPage(after,limit);if(waitSeconds is <0 or >25)throw new ChatOperationException("chat-invalid-query");
        DateTimeOffset deadline=DateTimeOffset.UtcNow.AddSeconds(waitSeconds);
        while(true)
        {
            // Observe before the snapshot to close the race between replay and sleep.
            Task changed=ChatV2EventSignal.Observe();
            ChatEventsDto page=await ReadV2EventsAsync(account,after,limit,token);
            if(page.Events.Count>0||page.EventCursor>after||page.RequiresResync||DateTimeOffset.UtcNow>=deadline)return page;
            TimeSpan remaining=deadline-DateTimeOffset.UtcNow;
            // A second process has its own signal; bounded DB replay covers it as well.
            using CancellationTokenSource wait=CancellationTokenSource.CreateLinkedTokenSource(token);
            Task timer=Task.Delay(remaining<TimeSpan.FromSeconds(2)?remaining:TimeSpan.FromSeconds(2),wait.Token);
            await Task.WhenAny(changed,timer);wait.Cancel();token.ThrowIfCancellationRequested();
        }
    }

    private Task<ChatEventsDto> ReadV2EventsAsync(uint account,long after,int limit,CancellationToken token)=>ReadV2Async(async(c,t)=>
    {
        long current=await V2CursorAsync(c,t,account,token);
        if(after>current)return new ChatEventsDto(){EventCursor=current,RequiresResync=true};
        List<(long Id,string Kind,long? Thread,long? Message,string? Payload)> rows=[];
        await using(MySqlCommand command=V2Command(c,t,"SELECT id,kind,thread_id,message_id,payload_json FROM atlas_launcher_chat_v2_event WHERE account_id=@account AND id>@after ORDER BY id LIMIT @limit;",("@account",account),("@after",after),("@limit",limit+1)))
        {
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
            while(await r.ReadAsync(token))rows.Add((r.GetInt64("id"),r.GetString("kind"),r.IsDBNull("thread_id")?null:r.GetInt64("thread_id"),r.IsDBNull("message_id")?null:r.GetInt64("message_id"),r.IsDBNull("payload_json")?null:r.GetString("payload_json")));
        }
        bool more=rows.Count>limit;if(more)rows.RemoveAt(rows.Count-1);List<ChatEventDto> events=[];
        foreach(var row in rows)
        {
            ChatEventDto item=new(){Id=row.Id,Kind=row.Kind,ThreadId=row.Thread is null?null:ChatThreadId(row.Thread.Value)};
            if(row.Kind=="preferences")item=item with{Preferences=await V2PreferencesAsync(c,t,account,token)};
            else if(row.Thread is not null&&row.Kind!="access")
            {
                V2Access? access=null;try{access=await RequireV2AccessAsync(c,t,account,row.Thread.Value,token,true);}catch(ChatOperationException e)when(e.Code=="chat-not-found"){}
                if(access is null)item=item with{Kind="access"};
                else if(row.Kind=="typing")
                {
                    ChatTypingDto? typing=row.Payload is null?null:ChatDeserialize<ChatTypingDto>(row.Payload);
                    if(access.Status!=1||typing is null)continue;
                    if(!(await V2PreferencesAsync(c,t,typing.AccountId,token)).ShareTyping)typing=typing with{ExpiresAt=DateTimeOffset.UtcNow};
                    if(PresenceAvailable && (await V2ProfileAsync(c,t,typing.AccountId,token)).Presence=="offline")typing=typing with{ExpiresAt=DateTimeOffset.UtcNow};
                    item=item with{Typing=typing};
                }
                else
                {
                    item=item with{Thread=await V2ThreadAsync(c,t,account,row.Thread.Value,token)};
                    if(row.Message is not null&&access.Status==1&&row.Message>access.HistoryAfter)item=item with{Message=await V2MessageAsync(c,t,account,access,row.Message.Value,token)};
                }
            }
            events.Add(item);
        }
        return new ChatEventsDto(){Events=events,EventCursor=more?rows[^1].Id:current,HasMore=more};
    },token);

    internal Task<ChatThreadDto> ReadChatV2ThreadAsync(uint account,long thread,ChatReadRequest request,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        V2Access access=await RequireV2AccessAsync(c,t,account,thread,token);
        _=await V2MessageAsync(c,t,account,access,request.ThroughMessageId,token);
        long cursor=Math.Max(access.LastRead,request.ThroughMessageId);
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_member SET last_read_message_id=GREATEST(last_read_message_id,@cursor) WHERE thread_id=@thread AND account_id=@account;",token,("@cursor",cursor),("@thread",thread),("@account",account));
        if(access.Kind==0)
        {
            long legacy=await V2ScalarAsync(c,t,"SELECT COALESCE(MAX(legacy_message_id),0) FROM atlas_launcher_chat_v2_message WHERE thread_id=@thread AND id<=@cursor;",token,("@thread",thread),("@cursor",cursor));
            string column=access.Low==account?"low_last_read_message_id":"high_last_read_message_id";
            await V2ExecuteAsync(c,t,$"UPDATE atlas_launcher_chat_conversation SET {column}=GREATEST({column},@cursor) WHERE account_low_id=@low AND account_high_id=@high;",token,("@cursor",legacy),("@low",access.Low),("@high",access.High));
        }
        ChatPreferencesDto prefs=await V2PreferencesAsync(c,t,account,token);
        await EmitV2Async(c,t,"thread",thread,null,prefs.ShareReadReceipts?null:account,token);
        return await V2ThreadAsync(c,t,account,thread,token);
    },token);

    internal Task<bool> SetChatV2TypingAsync(uint account,long thread,bool isTyping,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        await RequireV2AccessAsync(c,t,account,thread,token);
        ChatPreferencesDto preferences=await V2PreferencesAsync(c,t,account,token);
        if(PresenceAvailable && (await V2ProfileAsync(c,t,account,token)).Presence=="offline")isTyping=false;
        DateTimeOffset now=DateTimeOffset.UtcNow;
        if(isTyping&&!preferences.ShareTyping)return true;
        if(isTyping&&V2TypingSent.TryGetValue((account,thread),out DateTimeOffset previous)&&previous>now.AddSeconds(-2))return true;
        V2TypingSent[(account,thread)]=now;
        if(V2TypingSent.Count>10000)foreach(var old in V2TypingSent.Where(p=>p.Value<now.AddMinutes(-1)).Take(1000))V2TypingSent.TryRemove(old.Key,out _);
        ChatTypingDto typing=new(){ThreadId=ChatThreadId(thread),AccountId=account,Username=(await V2ProfileAsync(c,t,account,token)).Username,ExpiresAt=isTyping?now.AddSeconds(8):now};
        await EmitV2Async(c,t,"typing",thread,null,null,token,ChatSerialize(typing));return true;
    },token);
}
