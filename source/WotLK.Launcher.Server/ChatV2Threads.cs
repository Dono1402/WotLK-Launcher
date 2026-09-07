using MySqlConnector;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal Task<ChatThreadDto> CreateChatV2ThreadAsync(uint account, ChatCreateThreadRequest request, CancellationToken token) => MutateV2Async(async(c,t)=>
    {
        if(request.RequestId==Guid.Empty||request.ParticipantAccountIds is null)throw new ChatOperationException("chat-invalid-request");
        uint[] members=request.ParticipantAccountIds.Distinct().Order().ToArray();
        if(members.Length<1||members.Length>=ChatLimits.MaximumGroupMembers||members.Contains(account)||members.Contains(0U)||(!request.IsGroup&&members.Length!=1))throw new ChatOperationException("chat-invalid-members");
        string title=request.IsGroup?ValidateV2Title(request.Title):"";
        byte[] hash=ChatHash(ChatSerialize(new{request.IsGroup,Title=title,Members=members}));
        long existing;
        await using(MySqlCommand command=V2Command(c,t,"SELECT thread_id id,request_hash FROM atlas_launcher_chat_v2_request WHERE account_id=@account AND request_id=@request;",("@account",account),("@request",request.RequestId.ToByteArray(bigEndian:true))))
        {
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
            if(await r.ReadAsync(token)){if(!((byte[])r["request_hash"]).SequenceEqual(hash))throw new ChatOperationException("chat-idempotency-conflict");existing=r.GetInt64("id");}else existing=0;
        }
        if(existing!=0)return await V2ThreadAsync(c,t,account,existing,token);
        foreach(uint member in members)await RequireChatFriendAsync(c,t,account,member,true,token);
        if(!request.IsGroup)
        {
            long direct=await EnsureV2DirectAsync(c,t,account,members[0],token);
            await RecordV2ThreadRequestAsync(c,t,account,request.RequestId,hash,direct,token);
            await EmitV2Async(c,t,"thread",direct,null,null,token);
            return await V2ThreadAsync(c,t,account,direct,token);
        }
        long id=await V2InsertAsync(c,t,"""
            INSERT INTO atlas_launcher_chat_v2_thread(kind,owner_account_id,created_by_account_id,title,request_id,request_hash) VALUES(1,@account,@account,@title,@request,@hash);
            """,token,("@account",account),("@title",title),("@request",request.RequestId.ToByteArray(bigEndian:true)),("@hash",hash));
        await V2ExecuteAsync(c,t,"INSERT INTO atlas_launcher_chat_v2_member(thread_id,account_id,role) VALUES(@thread,@account,'owner');",token,("@thread",id),("@account",account));
        foreach(uint member in members)await V2ExecuteAsync(c,t,"INSERT INTO atlas_launcher_chat_v2_member(thread_id,account_id,status) VALUES(@thread,@account,0);",token,("@thread",id),("@account",member));
        await RecordV2ThreadRequestAsync(c,t,account,request.RequestId,hash,id,token);
        await EmitV2Async(c,t,"thread",id,null,null,token);
        return await V2ThreadAsync(c,t,account,id,token);
    },token);

    private static Task<long> RecordV2ThreadRequestAsync(MySqlConnection c,MySqlTransaction t,uint account,Guid request,byte[] hash,long thread,CancellationToken token)=>
        V2ExecuteAsync(c,t,"INSERT INTO atlas_launcher_chat_v2_request(account_id,request_id,request_hash,thread_id) VALUES(@account,@request,@hash,@thread);",token,("@account",account),("@request",request.ToByteArray(bigEndian:true)),("@hash",hash),("@thread",thread));

    private static string ValidateV2Title(string? value)
    {
        string title=value?.Trim()??"";
        if(title.Length is <1 or >120||title.Any(char.IsControl))throw new ChatOperationException("chat-invalid-title");
        return title;
    }

    private static async Task<long> EnsureV2DirectAsync(MySqlConnection c,MySqlTransaction t,uint account,uint friend,CancellationToken token)
    {
        await V2ExecuteAsync(c,t,"""
            INSERT INTO atlas_launcher_chat_v2_thread(kind,account_low_id,account_high_id,owner_account_id,created_by_account_id) VALUES(0,@low,@high,@low,@low)
            ON DUPLICATE KEY UPDATE id=id;
            """,token,("@low",Math.Min(account,friend)),("@high",Math.Max(account,friend)));
        long id=await V2ScalarAsync(c,t,"SELECT id FROM atlas_launcher_chat_v2_thread WHERE account_low_id=@low AND account_high_id=@high;",token,("@low",Math.Min(account,friend)),("@high",Math.Max(account,friend)));
        foreach(uint member in new[]{account,friend})await V2ExecuteAsync(c,t,"INSERT IGNORE INTO atlas_launcher_chat_v2_member(thread_id,account_id) VALUES(@thread,@account);",token,("@thread",id),("@account",member));
        return id;
    }

    internal Task<ChatThreadDto> UpdateChatV2ThreadSelfAsync(uint account,long thread,ChatThreadSelfRequest request,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        await RequireV2AccessAsync(c,t,account,thread,token,true);
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_member SET is_pinned=COALESCE(@pin,is_pinned),is_archived=COALESCE(@archive,is_archived) WHERE thread_id=@thread AND account_id=@account;",token,("@thread",thread),("@account",account),("@pin",request.IsPinned),("@archive",request.IsArchived));
        await EmitV2Async(c,t,"thread",thread,null,account,token);return await V2ThreadAsync(c,t,account,thread,token);
    },token);

    internal Task<ChatThreadDto> UpdateChatV2ThreadAsync(uint account,long thread,ChatThreadUpdateRequest request,ChatAttachmentDto? avatar,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        V2Access access=await RequireV2AccessAsync(c,t,account,thread,token);RequireV2Manage(access);
        if(request.ExpectedVersion is not null&&request.ExpectedVersion!=access.Version)throw new ChatOperationException("chat-version-conflict");
        if(avatar is not null&&avatar.Kind is not ("image" or "animated-image" or "gif"))throw new ChatOperationException("chat-invalid-avatar");
        string? title=request.Title is null?null:ValidateV2Title(request.Title);
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_thread SET title=COALESCE(@title,title),avatar_json=CASE WHEN @changeAvatar THEN @avatar ELSE avatar_json END,version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@thread;",token,("@thread",thread),("@title",title),("@changeAvatar",request.AvatarAttachmentId is not null),("@avatar",avatar is null?null:ChatSerialize(avatar)));
        await EmitV2Async(c,t,"thread",thread,null,null,token);return await V2ThreadAsync(c,t,account,thread,token);
    },token);

    private static void RequireV2Manage(V2Access access)
    {if(access.Kind!=1||access.Role is not ("owner" or "admin"))throw new ChatOperationException("chat-forbidden");}

    internal Task<ChatThreadDto> ChangeChatV2MemberAsync(uint account,long thread,ChatMemberRequest request,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        V2Access access=await RequireV2AccessAsync(c,t,account,thread,token,true);
        if(access.Kind!=1)throw new ChatOperationException("chat-forbidden");
        uint target=request.AccountId;
        switch(request.Action)
        {
            case "invite":
                if(access.Status!=1)throw new ChatOperationException("chat-forbidden");RequireV2Manage(access);
                await RequireChatFriendAsync(c,t,account,target,true,token);
                long status=await V2ScalarAsync(c,t,"SELECT COALESCE(MAX(status),3) FROM atlas_launcher_chat_v2_member WHERE thread_id=@thread AND account_id=@target;",token,("@thread",thread),("@target",target));
                if(status is 0 or 1)break;
                if(await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_member WHERE thread_id=@thread AND status IN(0,1);",token,("@thread",thread))>=ChatLimits.MaximumGroupMembers)throw new ChatOperationException("chat-group-full");
                await V2ExecuteAsync(c,t,"""
                    INSERT INTO atlas_launcher_chat_v2_member(thread_id,account_id,status,history_after_id) VALUES(@thread,@target,0,@history)
                    ON DUPLICATE KEY UPDATE status=0,role='member',history_after_id=@history,last_read_message_id=0,is_archived=FALSE,is_pinned=FALSE;
                    """,token,("@thread",thread),("@target",target),("@history",access.LastMessage));break;
            case "accept":
                if(target!=account||access.Status!=0)throw new ChatOperationException("chat-forbidden");
                await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_member SET status=1,joined_at=UTC_TIMESTAMP(6),history_after_id=@history,last_read_message_id=@history WHERE thread_id=@thread AND account_id=@account;",token,("@history",access.LastMessage),("@thread",thread),("@account",account));break;
            case "decline":
                if(target!=account||access.Status!=0)throw new ChatOperationException("chat-forbidden");
                await RemoveV2MemberAsync(c,t,thread,target,token);break;
            case "leave":
                if(target!=account||access.Status!=1)throw new ChatOperationException("chat-forbidden");
                if(access.Role=="owner"&&await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_member WHERE thread_id=@thread AND account_id<>@account AND status IN(0,1);",token,("@thread",thread),("@account",account))>0)throw new ChatOperationException("chat-owner-transfer-required");
                await RemoveV2MemberAsync(c,t,thread,target,token);break;
            case "remove":
                if(access.Status!=1)throw new ChatOperationException("chat-forbidden");RequireV2Manage(access);
                if(target==access.Owner||target==account)throw new ChatOperationException("chat-forbidden");
                if(access.Role!="owner"&&await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_member WHERE thread_id=@thread AND account_id=@target AND role='admin';",token,("@thread",thread),("@target",target))>0)throw new ChatOperationException("chat-forbidden");
                await RemoveV2MemberAsync(c,t,thread,target,token);break;
            case "role":
                if(access.Status!=1||access.Role!="owner"||target==account||request.Role is not("owner" or "admin" or "member"))throw new ChatOperationException("chat-forbidden");
                if(await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_member WHERE thread_id=@thread AND account_id=@target AND status=1;",token,("@thread",thread),("@target",target))==0)throw new ChatOperationException("chat-not-found");
                await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_member SET role=@role WHERE thread_id=@thread AND account_id=@target;",token,("@thread",thread),("@target",target),("@role",request.Role));
                if(request.Role=="owner")await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_thread SET owner_account_id=@target WHERE id=@thread; UPDATE atlas_launcher_chat_v2_member SET role='member' WHERE thread_id=@thread AND account_id=@account;",token,("@thread",thread),("@target",target),("@account",account));break;
            default: throw new ChatOperationException("chat-invalid-request");
        }
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_thread SET version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@thread;",token,("@thread",thread));
        await EmitV2Async(c,t,"thread",thread,null,null,token);
        if(target==account&&request.Action is "decline" or "leave")return new ChatThreadDto(){Id=ChatThreadId(thread),Kind="group",Title=access.Title,CanSend=false,Version=access.Version+1};
        return await V2ThreadAsync(c,t,account,thread,token);
    },token);

    private static async Task RemoveV2MemberAsync(MySqlConnection c,MySqlTransaction t,long thread,uint target,CancellationToken token)
    {
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_member SET status=2 WHERE thread_id=@thread AND account_id=@target;",token,("@thread",thread),("@target",target));
        await EmitV2Async(c,t,"access",thread,null,target,token);
    }
}
