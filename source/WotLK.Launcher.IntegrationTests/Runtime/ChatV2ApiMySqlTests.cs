using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static class ChatV2ApiMySqlTests
{
    private static readonly CancellationToken None=CancellationToken.None;
    private static int _checks;

    internal static async Task<int> RunAsync()
    {
        _checks=0;
        MySqlConnectionStringBuilder builder=new(Environment.GetEnvironmentVariable("ATLAS_CHAT_TEST_DB")??throw new InvalidOperationException("Disposable local ATLAS_CHAT_TEST_DB required."));
        if(builder.Server!="127.0.0.1"||builder.Port!=13307||!Regex.IsMatch(builder.Database,"^atlas_chat_test_[a-z0-9_]{1,30}$",RegexOptions.CultureInvariant))throw new InvalidOperationException("Only a fresh atlas_chat_test_ database on loopback port13307 is allowed.");
        string auth=builder.Database,characters=auth+"_chars";builder.Pooling=false;
        string media=Path.Combine(Path.GetTempPath(),"atlas-chat-v2-fixture-"+Guid.NewGuid().ToString("N"));
        LauncherServerOptions options=new(){ConnectionString=builder.ConnectionString,CharacterDatabaseName=characters,MaximumSchemaVersion=6,ChatMediaRoot=media};
        builder.Database="";await using MySqlConnection admin=new(builder.ConnectionString);await admin.OpenAsync();
        string version=Convert.ToString(await Scalar(admin,"SELECT VERSION();"),CultureInfo.InvariantCulture)??"";
        Check(version.StartsWith("8.4.",StringComparison.Ordinal),"MySQL8.4 fixture required.");
        List<string> created=[];
        try
        {
            foreach(string name in new[]{auth,characters}){await Sql(admin,$"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;");created.Add(name);}
            await using MySqlConnection c=new(options.ConnectionString);await c.OpenAsync();
            await Sql(c,$"""
                CREATE TABLE account(id INT UNSIGNED NOT NULL PRIMARY KEY,username VARCHAR(32) NOT NULL) ENGINE=InnoDB;
                CREATE TABLE `{characters}`.characters(guid INT UNSIGNED NOT NULL PRIMARY KEY,account INT UNSIGNED NOT NULL,online TINYINT UNSIGNED NOT NULL DEFAULT 0,name VARCHAR(12) NOT NULL,level TINYINT UNSIGNED NOT NULL DEFAULT 80,`class` TINYINT UNSIGNED NOT NULL DEFAULT 1,zone INT UNSIGNED NOT NULL DEFAULT 1,logout_time INT UNSIGNED NOT NULL DEFAULT 0,INDEX(account));
                """);
            for(uint i=1;i<=8;i++)await Sql(c,"INSERT INTO account VALUES(@id,@name);",("@id",i),("@name","LOGIN"+i));
            await new LauncherSchemaMigrator(options).MigrateAsync();
            for(uint i=1;i<=8;i++)await Sql(c,$"""
                INSERT INTO atlas_launcher_profile(account_id,display_username,email_normalized) VALUES(@id,@name,@email);
                INSERT INTO atlas_launcher_session(id,account_id,access_hash,refresh_hash,access_expires_at,refresh_expires_at) VALUES(@session,@id,@access,@refresh,UTC_TIMESTAMP()+INTERVAL 1 HOUR,UTC_TIMESTAMP()+INTERVAL 1 DAY);
                INSERT INTO `{characters}`.characters(guid,account,online,name) VALUES(@guid,@id,@online,@character);
                """,("@id",i),("@name","Atlas"+i),("@email",$"v2-{i}@fixture.invalid"),("@session",Guid.NewGuid().ToByteArray()),("@access",TokenService.Hash(Token(i))),("@refresh",TokenService.Hash("v2-fixture-refresh-"+i)),("@guid",i*100+1),("@online",i==3?0:1),("@character","Character"+i));
            foreach(var pair in new[]{(1U,2U),(1U,3U),(1U,4U),(2U,3U),(5U,6U),(5U,7U)})await Sql(c,"INSERT INTO atlas_launcher_friendship(account_low_id,account_high_id,requested_by_id,accepted_at) VALUES(@low,@high,@low,UTC_TIMESTAMP());",("@low",pair.Item1),("@high",pair.Item2));
            LauncherDatabase db=new(options,new TokenService(),new LauncherSchemaMigrator(options));
            Guid oldUuid=Guid.NewGuid();LauncherChatSendResult old=await db.SendChatMessageAsync(1,2,new(oldUuid,"Avant migration"),None);
            await Error(()=>db.GetChatV2StateAsync(1,None),"chat-unavailable");
            options.MaximumSchemaVersion=7;await new LauncherSchemaMigrator(options).MigrateAsync();await new LauncherSchemaMigrator(options).MigrateAsync();
            await new LauncherSchemaValidator().ValidateChatAsync(c,None);await new LauncherSchemaValidator().ValidateChatV2Async(c,None);
            Check(Convert.ToInt64(await Scalar(c,"SELECT COUNT(*) FROM atlas_launcher_schema_history;"))==7,"Seven migrations once.");
            ChatStateDto state=await db.GetChatV2StateAsync(1,None);ChatThreadDto direct=state.Threads.Single();long thread=long.Parse(direct.Id,CultureInfo.InvariantCulture);
            Check(direct.LastMessage?.Id==old.Message.Id&&direct.LastMessage.Body=="Avant migration","Legacy history backfill preserves original ID/body.");
            Check(!state.Preferences.DoNotDisturb&&!state.Preferences.MessageSoundEnabled,"Global preferences start silent.");
            Check(state.Contacts.Count==3,"Accepted contacts only.");
            Check(state.Self.Presence=="game"&&state.Self.CharacterGuid==101&&state.Self.CharacterName=="Character1","Current in-game character enriches account presence.");
            await Sql(c,"ALTER TABLE atlas_launcher_chat_v2_preferences ADD COLUMN fixture_drift INT NULL;");
            bool drift=false;try{await new LauncherSchemaValidator().ValidateChatV2Async(c,None);}catch(InvalidOperationException){drift=true;}Check(drift,"V2 schema drift rejected.");
            await Sql(c,"ALTER TABLE atlas_launcher_chat_v2_preferences DROP COLUMN fixture_drift;");
            await ValidateDirect(db,c,thread);
            await ValidateGroups(db,c);
            await ValidateEvents(db,c,thread);
            await ValidateHttp(options,db,c,thread);
            Console.WriteLine($"Chat v2 MySQL {version} PASS: {_checks} assertions. Additive migration/backfill, v1/game projection, authorization, group join history, edits/deletes/reactions/previews, replay/read privacy, long-poll wakeup and authenticated upload/range/download revocation. Synthetic loopback fixtures only.");
            return 0;
        }
        finally
        {
            for(int i=created.Count-1;i>=0;i--)await Sql(admin,$"DROP DATABASE `{created[i]}`;");
            string expected=Path.GetFullPath(media),temp=Path.GetFullPath(Path.GetTempPath());
            if(!expected.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(expected).StartsWith("atlas-chat-v2-fixture-",StringComparison.Ordinal))throw new InvalidOperationException("Unsafe fixture cleanup path.");
            if(Directory.Exists(expected))Directory.Delete(expected,true);
        }
    }

    private static async Task ValidateDirect(LauncherDatabase db,MySqlConnection c,long thread)
    {
        ChatCreateThreadRequest open=new(){RequestId=Guid.NewGuid(),ParticipantAccountIds=[2]};
        Check((await db.CreateChatV2ThreadAsync(1,open,None)).Id==thread.ToString(),"Opening existing DM preserves thread identity.");
        await Error(()=>db.CreateChatV2ThreadAsync(1,open with{ParticipantAccountIds=[3]},None),"chat-idempotency-conflict");
        const string markdown="**Texte riche** [Atlas](https://example.com/)\n\n- ||secret||\n- `code`";
        ChatSendMessageResult rich=await Send(db,1,thread,markdown);
        Check(rich.Message.Body==markdown,"V2 retains original Markdown after transactional projection.");
        LauncherChatMessage projected=(await db.ListChatMessagesAsync(2,1,null,null,50,None)).Messages.Single(x=>x.ClientMessageId==rich.Message.ClientMessageId);
        Check(projected.Body=="Texte riche Atlas (https://example.com/)\n- secret\n- code","V1/game projection contains readable text with URL retained.");
        await db.EditChatV2MessageAsync(1,thread,rich.Message.Id,new("**Édité** [lien](https://example.org/)"),[],None);
        Check((await db.ListChatMessagesAsync(2,1,null,null,50,None)).Messages.Single(x=>x.ClientMessageId==rich.Message.ClientMessageId).Body=="Édité lien (https://example.org/)","Edits keep the legacy mirror in plain text.");
        ChatSendMessageRequest request=new(){ClientMessageId=Guid.NewGuid(),Body="Bonjour 世界 👋 https://fixture.invalid/page https://youtu.be/abcdefghijk"};
        ChatLinkPreviewDto[] previews=[new(){Id="page",Url="https://fixture.invalid/page",Title="Page"},new(){Id="video",Url="https://youtu.be/abcdefghijk",Kind="video",CanRemove=false,EmbedUrl="https://www.youtube-nocookie.com/embed/abcdefghijk"}];
        ChatSendMessageResult[] retries=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>db.SendChatV2MessageAsync(1,thread,request,[],previews,None)));
        ChatMessageDto message=retries[0].Message;Check(retries.Select(x=>x.Message.Id).Distinct().Count()==1&&retries.Count(x=>!x.IsDuplicate)==1,"Eight concurrent UUID retries produce one message.");
        Check((await db.ListChatMessagesAsync(2,1,null,null,50,None)).Messages.Any(x=>x.ClientMessageId==request.ClientMessageId&&x.Body==request.Body),"Rich direct text is projected into v1.");
        await Error(()=>db.SendChatV2MessageAsync(1,thread,request with{Body="changed UUID payload"},[],previews,None),"chat-idempotency-conflict");
        await Error(()=>db.GetChatV2MessagesAsync(8,thread,null,50,None),"chat-not-found");
        ChatMessageDto removed=await db.RemoveChatV2PreviewAsync(2,thread,message.Id,"page",None);
        Check(removed.LinkPreviews[0].IsRemoved&&!removed.LinkPreviews[1].IsRemoved&&removed.Body==request.Body,"Recipient removes just one nonvideo preview globally.");
        await Error(()=>db.RemoveChatV2PreviewAsync(2,thread,message.Id,"video",None),"chat-preview-not-removable");
        ChatMessageDto edited=await db.EditChatV2MessageAsync(1,thread,message.Id,new("Texte modifié https://fixture.invalid/page",removed.Version),previews,None);
        Check(edited.LinkPreviews[0].IsRemoved&&edited.EditedAt is not null,"Editing cannot recreate a dismissed preview.");
        await Error(()=>db.EditChatV2MessageAsync(1,thread,message.Id,new("stale",message.Version),[],None),"chat-version-conflict");
        await Error(()=>db.EditChatV2MessageAsync(2,thread,message.Id,new("not author"),[],None),"chat-forbidden");
        Check((await db.SendChatV2MessageAsync(1,thread,request,[],previews,None)).Message.Body==edited.Body,"Retry after edit returns current value without restoring text.");
        Check((await db.SendChatMessageAsync(1,2,new(request.ClientMessageId,request.Body),None)).Message.Body==edited.Body,"V1 retry after rich edit uses immutable original fingerprint.");
        ChatMessageDto reaction=await db.ReactChatV2MessageAsync(2,thread,message.Id,new("👍",true),None);
        Check(reaction.Reactions.Single().AccountIds.SequenceEqual(new[]{2U}),"Recipient reaction saved.");
        reaction=await db.ReactChatV2MessageAsync(2,thread,message.Id,new("👍",true),None);Check(reaction.Reactions.Single().Count==1,"Reaction set is idempotent.");
        reaction=await db.ReactChatV2MessageAsync(2,thread,message.Id,new("👍",false),None);Check(reaction.Reactions.Count==0,"Reaction removed.");
        Check((await db.PinChatV2MessageAsync(2,thread,message.Id,new(true),None)).IsPinned,"Shared pin.");
        ChatSendMessageResult reply=await Send(db,2,thread,"Réponse",message.Id);Check(reply.Message.ReplyTo?.MessageId==message.Id,"Reply target captured in same thread.");
        await db.ReadChatV2ThreadAsync(2,thread,new(message.Id),None);
        Check((await db.GetChatV2MessagesAsync(1,thread,null,50,None)).Messages.Single(m=>m.Id==message.Id).ReadByAccountIds.Contains(2U),"Read receipt visible by default.");
        await db.UpdateChatV2PreferencesAsync(2,new(){DoNotDisturb=true,ShareReadReceipts=false,ShareTyping=false},None);
        ChatMessagesPageDto privateRead=await db.GetChatV2MessagesAsync(1,thread,null,50,None);Check(!privateRead.Messages.Single(m=>m.Id==message.Id).ReadByAccountIds.Contains(2U)&&privateRead.Thread.Members.Single(m=>m.Profile.AccountId==2).LastReadMessageId==0,"Private read cursor never exposed.");
        Check((await db.ListChatMessagesAsync(1,2,null,null,50,None)).FriendLastReadMessageId==0&&(await db.ListChatConversationsAsync(1,null,50,None)).Conversations.Single(x=>x.FriendAccountId==2).FriendLastReadMessageId==0,"Global read privacy also filters older v1 endpoints.");
        Check((await db.GetChatV2StateAsync(2,None)).Preferences.DoNotDisturb,"DND survives independent state reload.");
        await db.UpdateChatV2ThreadSelfAsync(2,thread,new(IsArchived:true),None);
        Check((await db.GetChatV2StateAsync(2,None)).Threads.Single(x=>x.Id==thread.ToString()).IsArchived,"Personal archive retained.");
        await Send(db,1,thread,"Réveil des archives");Check(!(await db.GetChatV2StateAsync(2,None)).Threads.Single(x=>x.Id==thread.ToString()).IsArchived,"New message restores archived thread.");
        ChatMessageDto deleted=await db.DeleteChatV2MessageAsync(1,thread,message.Id,None);Check(deleted.DeletedAt is not null&&deleted.Body==""&&deleted.LinkPreviews.Count==0&&!deleted.IsPinned,"Delete is a content-free tombstone.");
        Check((await db.GetChatV2MessageByClientAsync(1,thread,request.ClientMessageId,None)).Message.DeletedAt is not null,"UUID lookup reconciles deleted message.");
        Check((await db.SendChatV2MessageAsync(1,thread,request,[],previews,None)).Message.DeletedAt is not null,"Retry cannot resurrect deletion.");
        Check(Convert.ToInt64(await Scalar(c,"SELECT COUNT(*) FROM atlas_launcher_chat_outbox o JOIN atlas_launcher_chat_v2_message m ON m.legacy_message_id=o.message_id WHERE m.id=@id AND o.status=4;",("@id",message.Id)))==1,"Pending game copy cancelled on delete.");
        Check((await db.GetChatV2MessagesAsync(2,thread,null,50,None)).Messages.Single(x=>x.Id==reply.Message.Id).ReplyTo?.IsDeleted==true,"Reply quotes reflect deletion.");
        long revokeCursor=(await db.GetChatV2StateAsync(2,None)).EventCursor;
        Check(await db.RemoveFriendAsync(1,2,None),"Product friendship removal succeeds.");
        Check((await db.PollChatV2EventsAsync(2,revokeCursor,100,0,None)).Events.Any(e=>e.Kind=="access"&&e.ThreadId==thread.ToString()),"Friendship removal immediately invalidates an open direct thread.");
        await Error(()=>db.GetChatV2MessagesAsync(2,thread,null,50,None),"chat-not-found");
        Check(!(await db.GetChatV2StateAsync(1,None)).Threads.Any(t=>t.Id==thread.ToString()),"Unfriending hides direct history.");
        await db.SendFriendRequestAsync(1,"LOGIN2",None);Check(await db.AcceptFriendAsync(2,1,None),"Friendship restored through product acceptance.");
    }

    private static async Task ValidateGroups(LauncherDatabase db,MySqlConnection c)
    {
        ChatCreateThreadRequest create=new(){RequestId=Guid.NewGuid(),IsGroup=true,Title="Sortie Atlas",ParticipantAccountIds=[2,3]};
        ChatThreadDto group=await db.CreateChatV2ThreadAsync(1,create,None);long id=long.Parse(group.Id);
        Check(group.Members.Count==3&&group.Members.Count(m=>m.Status=="invited")==2,"Group starts with explicit invitations.");
        Check((await db.CreateChatV2ThreadAsync(1,create,None)).Id==group.Id,"Group creation idempotent.");
        await Error(()=>db.CreateChatV2ThreadAsync(1,create with{Title="Other"},None),"chat-idempotency-conflict");
        await Error(()=>Send(db,2,id,"Before acceptance"),"chat-not-found");
        ChatSendMessageResult before=await Send(db,1,id,"Privé avant entrée");
        await db.ChangeChatV2MemberAsync(2,id,new(2,"accept"),None);
        Check((await db.GetChatV2MessagesAsync(2,id,null,50,None)).Messages.Count==0,"Prejoin history is hidden after acceptance.");
        await Error(()=>Send(db,2,id,"Quote hidden",before.Message.Id),"chat-not-found");
        await db.ChangeChatV2MemberAsync(3,id,new(3,"accept"),None);
        ChatSendMessageResult current=await Send(db,2,id,"Bonjour groupe");
        Check((await db.GetChatV2MessagesAsync(3,id,null,50,None)).Messages.Single().Id==current.Message.Id,"Joined participants receive subsequent history.");
        Check(Convert.ToInt64(await Scalar(c,"SELECT COUNT(*) FROM atlas_launcher_chat_message WHERE client_message_id=@id;",("@id",current.Message.ClientMessageId.ToByteArray(bigEndian:true))))==0,"Groups never enter v1/game outbox.");
        await Error(()=>db.UpdateChatV2ThreadAsync(2,id,new(Title:"Unauthorized"),null,None),"chat-forbidden");
        await Error(()=>db.ChangeChatV2MemberAsync(1,id,new(1,"leave"),None),"chat-owner-transfer-required");
        ChatCardDto card=new(){Kind="event",Title="Citadelle",Fields=new Dictionary<string,string>{{"date","2026-09-12T20:00:00+02:00"}}};
        ChatSendMessageResult outing=await db.SendChatV2MessageAsync(1,id,new(){ClientMessageId=Guid.NewGuid(),Card=card},[],[],None);
        ChatMessageDto response=await db.RespondChatV2CardAsync(2,id,outing.Message.Id,new("joining","healer"),None);Check(response.Card?.Responses.Single().Role=="healer","Typed event response saved.");
        await db.ChangeChatV2MemberAsync(1,id,new(3,"remove"),None);await Error(()=>db.GetChatV2MessagesAsync(3,id,null,50,None),"chat-not-found");
        ChatSendMessageResult absent=await Send(db,1,id,"Absent member cannot read this");
        await db.ChangeChatV2MemberAsync(1,id,new(3,"invite"),None);await db.ChangeChatV2MemberAsync(3,id,new(3,"accept"),None);
        Check((await db.GetChatV2MessagesAsync(3,id,null,50,None)).Messages.Count==0,"Rejoin resets visibility beyond messages sent while absent.");
        await db.ChangeChatV2MemberAsync(1,id,new(2,"role","owner"),None);
        await db.ChangeChatV2MemberAsync(1,id,new(1,"leave"),None);
        Check((await db.GetChatV2StateAsync(2,None)).Threads.Single(t=>t.Id==group.Id).CanManage,"Owner transfer permits previous owner departure.");
    }

    private static async Task ValidateEvents(LauncherDatabase db,MySqlConnection c,long thread)
    {
        ChatStateDto state=await db.GetChatV2StateAsync(2,None);long cursor=state.EventCursor;
        Stopwatch elapsed=Stopwatch.StartNew();Task<ChatEventsDto> waiting=db.PollChatV2EventsAsync(2,cursor,100,25,None);
        await Task.Delay(120);ChatSendMessageResult sent=await Send(db,1,thread,"Réveil immédiat");
        ChatEventsDto wake=await waiting.WaitAsync(TimeSpan.FromSeconds(4));
        Check(wake.Events.Any(e=>e.Message?.Id==sent.Message.Id)&&elapsed.Elapsed<TimeSpan.FromSeconds(3),"Long poll awakens immediately on committed send.");
        long after=wake.EventCursor;
        await db.EditChatV2MessageAsync(1,thread,sent.Message.Id,new("Événement sur ancien ID"),[],None);
        ChatEventsDto edit=await db.PollChatV2EventsAsync(2,after,100,0,None);
        Check(edit.Events.Any(e=>e.Message?.Id==sent.Message.Id&&e.Message.Body=="Événement sur ancien ID")&&edit.EventCursor>after,"Mutation event cursor independent of message ID.");
        after=edit.EventCursor;
        ChatSendMessageResult[] concurrent=await Task.WhenAll(Enumerable.Range(0,6).Select(i=>Send(db,1,thread,"Concurrent "+i)));
        List<long> seen=[];bool more;do{ChatEventsDto page=await db.PollChatV2EventsAsync(2,after,2,0,None);seen.AddRange(page.Events.Where(e=>e.Message is not null).Select(e=>e.Message!.Id));Check(page.EventCursor>=after,"Event cursor monotone.");after=page.EventCursor;more=page.HasMore;}while(more);
        Check(concurrent.All(m=>seen.Contains(m.Message.Id)),"Paginated replay cannot skip concurrent commits.");
        Check((await db.PollChatV2EventsAsync(2,long.MaxValue,100,0,None)).RequiresResync,"Future cursor requests explicit resync.");
        Guid gameRequest=Guid.NewGuid();await Sql(c,"INSERT INTO atlas_launcher_chat_inbox(request_id,realm_id,sender_account_id,sender_character_guid,recipient_account_id,body) VALUES(@request,1,1,101,2,'Depuis le jeu');",("@request",gameRequest.ToByteArray(bigEndian:true)));
        Check(await db.ProcessChatGameInboxAsync(5,None)==1,"Existing game inbox consumed under v7.");
        ChatSendMessageResult game=await db.GetChatV2MessageByClientAsync(1,thread,gameRequest,None);
        Check(game.Message.Origin=="game"&&game.Message.SenderCharacterName=="Character1","Game origin and sending character preserved.");
        long typingCursor=(await db.GetChatV2StateAsync(2,None)).EventCursor;
        await db.SetChatV2TypingAsync(1,thread,true,None);await db.SetChatV2TypingAsync(1,thread,false,None);
        ChatEventsDto typing=await db.PollChatV2EventsAsync(2,typingCursor,100,0,None);
        Check(typing.Events.Count(e=>e.Typing?.AccountId==1)==2&&typing.Events.Last(e=>e.Typing is not null).Typing!.ExpiresAt<=DateTimeOffset.UtcNow,"Typing stop event clears indicator immediately.");
    }

    private static async Task ValidateHttp(LauncherServerOptions options,LauncherDatabase db,MySqlConnection c,long thread)
    {
        WebApplicationBuilder builder=WebApplication.CreateBuilder(new WebApplicationOptions(){EnvironmentName="Development"});builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);builder.Services.AddSingleton(db);builder.Services.AddSingleton<ChatRequestLimiter>();builder.Services.AddSingleton<ChatAttachmentStorage>();builder.Services.AddSingleton<ChatLinkPreviewService>();
        await using WebApplication app=builder.Build();app.MapChatEndpoints();app.MapChatV2Endpoints();await app.StartAsync();
        try
        {
            using HttpClient http=new(){BaseAddress=new Uri(app.Urls.Single())};
            using(var anonymous=await Request(http,HttpMethod.Get,"/api/v2/chat/state",null))Check(anonymous.StatusCode==HttpStatusCode.Unauthorized,"HTTP state requires authentication.");
            using(var state=await Request(http,HttpMethod.Get,"/api/v2/chat/state",1))
            {
                Check(state.IsSuccessStatusCode&&state.Headers.CacheControl?.NoStore==true,"Private state no-store.");using JsonDocument json=JsonDocument.Parse(await state.Content.ReadAsStringAsync());Check(json.RootElement.GetProperty("eventCursor").ValueKind==JsonValueKind.String,"Cursor encoded as decimal string.");
            }
            using(var invalid=await Request(http,HttpMethod.Patch,"/api/v2/chat/preferences",1,"{\"doNotDisturb\":true,\"doNotDisturb\":false}"))Check(invalid.StatusCode==HttpStatusCode.BadRequest,"Duplicate JSON properties rejected.");
            using(var sound=await Request(http,HttpMethod.Patch,"/api/v2/chat/preferences",1,"{\"messageSoundEnabled\":true}"))Check(sound.StatusCode==HttpStatusCode.BadRequest,"No sound enabling API before user choice.");
            using(var huge=await Request(http,HttpMethod.Post,"/api/v2/chat/uploads",1,ChatSerialize(new ChatUploadRequest("huge.txt","text/plain",500_000_001))))Check(huge.StatusCode==HttpStatusCode.RequestEntityTooLarge,"500Mo maximum enforced before transfer.");
            using(var archive=await Request(http,HttpMethod.Post,"/api/v2/chat/uploads",1,ChatSerialize(new ChatUploadRequest("archive.zip","application/zip",5))))Check(archive.StatusCode==HttpStatusCode.BadRequest,"Archive upload excluded.");
            ChatUploadDto upload;using(var begin=await Request(http,HttpMethod.Post,"/api/v2/chat/uploads",1,ChatSerialize(new ChatUploadRequest("sample.txt","text/plain",5))))upload=await Read<ChatUploadDto>(begin);
            using(var wrongOwner=await Request(http,HttpMethod.Get,$"/api/v2/chat/uploads/{upload.Id}",2))Check(wrongOwner.StatusCode==HttpStatusCode.NotFound,"Upload ownership enforced.");
            using(HttpRequestMessage append=new(HttpMethod.Put,$"/api/v2/chat/uploads/{upload.Id}?offset=0")){append.Headers.Authorization=new("Bearer",Token(1));append.Content=new ByteArrayContent("HELLO"u8.ToArray());append.Content.Headers.ContentType=new("application/octet-stream");using HttpResponseMessage response=await http.SendAsync(append);Check((await Read<ChatUploadDto>(response)).Offset==5,"Binary chunk written.");}
            using(var complete=await Request(http,HttpMethod.Post,$"/api/v2/chat/uploads/{upload.Id}/complete",1))upload=await Read<ChatUploadDto>(complete);
            Check(upload.IsComplete&&upload.Attachment?.Sha256 is not null,"Completion publishes validated attachment.");
            using(var unbound=await Request(http,HttpMethod.Get,$"/api/v2/chat/attachments/{upload.Id}",1))Check(unbound.StatusCode==HttpStatusCode.NotFound,"Unbound completed attachment not downloadable through chat.");
            ChatSendMessageResult sent;using(var send=await Request(http,HttpMethod.Post,$"/api/v2/chat/threads/{thread}/messages",1,ChatSerialize(new ChatSendMessageRequest(){ClientMessageId=Guid.NewGuid(),AttachmentIds=[upload.Id]})))sent=await Read<ChatSendMessageResult>(send);
            Check(sent.Message.Attachments.Single().Id==upload.Id,"Complete attachment bound transactionally to message.");
            using(HttpRequestMessage range=new(HttpMethod.Get,$"/api/v2/chat/attachments/{upload.Id}")){range.Headers.Authorization=new("Bearer",Token(2));range.Headers.Range=new(1,3);using HttpResponseMessage response=await http.SendAsync(range);Check(response.StatusCode==HttpStatusCode.PartialContent&&await response.Content.ReadAsStringAsync()=="ELL","Participant authenticated byte-range download.");}
            using(var stranger=await Request(http,HttpMethod.Get,$"/api/v2/chat/attachments/{upload.Id}",8))Check(stranger.StatusCode==HttpStatusCode.NotFound,"Nonparticipant attachment access denied.");
            using(var lookup=await Request(http,HttpMethod.Get,$"/api/v2/chat/threads/{thread}/messages/by-client/{sent.Message.ClientMessageId}",1))Check((await Read<ChatSendMessageResult>(lookup)).Message.Id==sent.Message.Id,"HTTP UUID reconciliation route.");
            using(var deleted=await Request(http,HttpMethod.Delete,$"/api/v2/chat/threads/{thread}/messages/{sent.Message.Id}",1))Check((await Read<ChatMessageDto>(deleted)).DeletedAt is not null,"HTTP delete tombstone.");
            using(var revoked=await Request(http,HttpMethod.Get,$"/api/v2/chat/attachments/{upload.Id}",2))Check(revoked.StatusCode==HttpStatusCode.NotFound,"Deleted message revokes attachment download.");
        }
        finally{await app.StopAsync();}
    }

    private static string Token(uint account)=>"chat-v2-fixture-access-"+account;
    private static string ChatSerialize<T>(T value)=>JsonSerializer.Serialize(value,ChatJson.Options);
    private static async Task<T> Read<T>(HttpResponseMessage response){string payload=await response.Content.ReadAsStringAsync();if(!response.IsSuccessStatusCode)throw new InvalidOperationException($"HTTP fixture returned {(int)response.StatusCode}: {payload}");return JsonSerializer.Deserialize<T>(payload,ChatJson.Options)??throw new InvalidOperationException("Empty HTTP fixture response.");}
    private static Task<HttpResponseMessage> Request(HttpClient http,HttpMethod method,string path,uint? account,string? json=null){HttpRequestMessage message=new(method,path);if(account is not null)message.Headers.Authorization=new("Bearer",Token(account.Value));if(json is not null)message.Content=new StringContent(json,Encoding.UTF8,"application/json");return http.SendAsync(message);}
    private static Task<ChatSendMessageResult> Send(LauncherDatabase db,uint account,long thread,string body,long? reply=null)=>db.SendChatV2MessageAsync(account,thread,new(){ClientMessageId=Guid.NewGuid(),Body=body,ReplyToMessageId=reply},[],[],None);
    private static async Task Error(Func<Task> call,string code){try{await call();}catch(ChatOperationException error)when(error.Code==code){_checks++;return;}throw new InvalidOperationException("Expected "+code);}
    private static void Check(bool value,string description){if(!value)throw new InvalidOperationException(description);_checks++;}
    private static async Task Sql(MySqlConnection c,string sql,params(string Name,object Value)[] args){await using MySqlCommand command=c.CreateCommand();command.CommandText=sql;foreach(var(name,value)in args)command.Parameters.AddWithValue(name,value);await command.ExecuteNonQueryAsync();}
    private static async Task<object?> Scalar(MySqlConnection c,string sql,params(string Name,object Value)[] args){await using MySqlCommand command=c.CreateCommand();command.CommandText=sql;foreach(var(name,value)in args)command.Parameters.AddWithValue(name,value);return await command.ExecuteScalarAsync();}
}
