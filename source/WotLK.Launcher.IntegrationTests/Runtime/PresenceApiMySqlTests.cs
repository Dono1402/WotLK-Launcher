using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Server;
using WotLK.Launcher.Server.Database;

internal static class PresenceApiMySqlTests
{
    private static int _checks;
    private static readonly CancellationToken None=CancellationToken.None;
    internal static async Task<int> RunAsync()
    {
        _checks=0;
        MySqlConnectionStringBuilder builder=new(Environment.GetEnvironmentVariable("ATLAS_PRESENCE_TEST_DB")??throw new InvalidOperationException("Disposable ATLAS_PRESENCE_TEST_DB required."));
        if(builder.Server!="127.0.0.1"||builder.Port!=13307||!Regex.IsMatch(builder.Database,"^atlas_presence_test_[a-z0-9_]{1,30}$",RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Only a fresh atlas_presence_test_ database on 127.0.0.1:13307 is permitted.");
        string auth=builder.Database,characters=auth+"_chars";builder.Pooling=false;
        LauncherServerOptions options=new(){ConnectionString=builder.ConnectionString,CharacterDatabaseName=characters,MaximumSchemaVersion=7};
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
                INSERT INTO account VALUES(1,'PRESENCE1'),(2,'PRESENCE2'),(3,'PRESENCE3');
                """);
            await new LauncherSchemaMigrator(options).MigrateAsync();
            for(uint id=1;id<=3;id++)await Sql(c,$"""
                INSERT INTO atlas_launcher_profile(account_id,display_username,email_normalized) VALUES(@id,@name,@email);
                INSERT INTO atlas_launcher_session(id,account_id,access_hash,refresh_hash,access_expires_at,refresh_expires_at) VALUES(@session,@id,@access,@refresh,UTC_TIMESTAMP()+INTERVAL 1 HOUR,UTC_TIMESTAMP()+INTERVAL 1 DAY);
                INSERT INTO `{characters}`.characters(guid,account,online,name,zone,logout_time) VALUES(@id,@id,1,@character,1519,UNIX_TIMESTAMP());
                """,("@id",id),("@name","Presence"+id),("@email",$"presence-{id}@fixture.invalid"),("@session",Guid.NewGuid().ToByteArray()),("@access",TokenService.Hash(Token(id))),("@refresh",TokenService.Hash("presence-refresh-"+id)),("@character","Character"+id));
            await Sql(c,"""
                INSERT INTO atlas_launcher_friendship(account_low_id,account_high_id,requested_by_id,accepted_at) VALUES(1,2,1,UTC_TIMESTAMP()),(1,3,1,NULL);
                INSERT INTO atlas_launcher_chat_v2_preferences(account_id,do_not_disturb) VALUES(2,TRUE);
                """);
            LauncherDatabase db=new(options,new TokenService(),new LauncherSchemaMigrator(options));
            await Error(()=>db.UpdatePresenceAsync(1,new("offline",0),None),"presence-unavailable");
            options.MaximumSchemaVersion=8;await new LauncherSchemaMigrator(options).MigrateAsync();await new LauncherSchemaMigrator(options).MigrateAsync();
            await new LauncherSchemaValidator().ValidatePresenceAsync(c,None);
            Check(Convert.ToInt32(await Scalar(c,"SELECT COUNT(*) FROM atlas_launcher_schema_history;"))==8,"Eight migrations exactly once.");
            Check((string?)await Scalar(c,"SELECT manual_status FROM atlas_launcher_presence WHERE account_id=2;")=="dnd","Migration preserves existing DND preference.");
            Check((await db.UpdatePresenceAsync(2,new(null,0),None)).ManualStatus=="dnd","First heartbeat retains migrated DND.");
            ChatThreadDto direct=await db.CreateChatV2ThreadAsync(1,new(){RequestId=Guid.NewGuid(),ParticipantAccountIds=[2]},None);
            LauncherPresenceStateDto first=await db.UpdatePresenceAsync(1,new(null,1199),None);
            Check(first.Status=="online"&&!first.IsAutomaticAway,"Initial activity just before20min remains online.");
            await Sql(c,"UPDATE atlas_launcher_presence SET last_active_at=UTC_TIMESTAMP(6)-INTERVAL 1200 SECOND WHERE account_id=1;");
            LauncherPresenceStateDto away=await db.UpdatePresenceAsync(1,new(null,1200),None);
            Check(away.Status=="away"&&away.ManualStatus=="online"&&away.IsAutomaticAway,"Exactly20min yields automatic away.");
            Check((await db.GetChatV2StateAsync(2,None)).Contacts.Single().Presence=="away","Automatic away reaches other account while character remains online.");
            Check((await db.UpdatePresenceAsync(1,new(null,0),None)).Status=="online","Activity restores automatic away.");
            foreach(string status in new[]{"away","dnd","offline"})
            {
                LauncherPresenceStateDto selected=await db.UpdatePresenceAsync(1,new(status,0),None);
                await Sql(c,"UPDATE atlas_launcher_presence SET last_active_at=UTC_TIMESTAMP(6)-INTERVAL 1 HOUR WHERE account_id=1;");
                Check((await db.UpdatePresenceAsync(1,new(null,3600),None)).Status==status,"Idle preserves manual "+status);
                Check((await db.UpdatePresenceAsync(1,new(null,0),None)).Status==status,"Activity preserves manual "+status);
                Check((await db.GetChatV2PreferencesAsync(1,None)).DoNotDisturb==(status=="dnd"),"Shared DND preference matches "+status);
            }
            ChatStateDto observer=await db.GetChatV2StateAsync(2,None);
            ChatProfileDto hidden=observer.Contacts.Single();
            Check(hidden.Presence=="offline"&&hidden.CharacterGuid is null&&hidden.CharacterName is null&&hidden.CharacterClass is null,"Invisible hides game activity in contacts.");
            Check(observer.Threads.Single().Members.Single(m=>m.Profile.AccountId==1).Profile.Presence=="offline","Invisible applies to thread member profiles.");
            long typingCursor=observer.EventCursor;
            await db.SetChatV2TypingAsync(1,long.Parse(direct.Id,CultureInfo.InvariantCulture),true,None);
            ChatEventsDto privateTyping=await db.PollChatV2EventsAsync(2,typingCursor,100,0,None);
            Check(privateTyping.Events.Where(item=>item.Typing is not null).All(item=>item.Typing!.ExpiresAt<=DateTimeOffset.UtcNow),"Invisible does not leak current activity through typing indicators.");
            LauncherFriend friend=(await db.ListFriendsAsync(2,None)).Single();
            Check(friend.Presence=="offline"&&!friend.Online&&!friend.LauncherOnline&&friend.LastSeenAt is null&&friend.LauncherLastSeenAt is null,"Invisible hides both presence transports and timestamps in friends.");
            Check(friend.Characters?.Single() is {Online:false,ZoneId:0,LastSeenAt:null} && friend.Characters.Single().Name=="Character1","Invisible keeps roster while hiding character activity.");
            LauncherDatabase reloaded=new(options,new TokenService(),new LauncherSchemaMigrator(options));
            Check((await reloaded.UpdatePresenceAsync(1,new(null,0),None)).ManualStatus=="offline","Preference persists across independent database instances.");
            await db.UpdateChatV2PreferencesAsync(1,new(){DoNotDisturb=true},None);
            Check((await db.UpdatePresenceAsync(1,new(null,0),None)).ManualStatus=="dnd","Older chat preference API changes global DND.");
            await db.UpdatePresenceAsync(1,new("away",0),None);
            await db.UpdateChatV2PreferencesAsync(1,new(){ShareTyping=false},None);
            Check((await db.UpdatePresenceAsync(1,new(null,0),None)).ManualStatus=="away","Unrelated preference patch preserves manual presence.");
            await db.UpdateChatV2PreferencesAsync(1,new(){DoNotDisturb=false},None);
            Check((await db.UpdatePresenceAsync(1,new(null,0),None)).ManualStatus=="online","Older DND clear returns online.");
            long cursor=(await db.GetChatV2StateAsync(2,None)).EventCursor;
            await db.UpdatePresenceAsync(1,new("offline",0),None);
            Check((await db.PollChatV2EventsAsync(2,cursor,100,0,None)).Events.Any(e=>e.Thread?.Members.Any(m=>m.Profile.AccountId==1&&m.Profile.Presence=="offline")==true),"Observer receives updated member presence in event replay.");
            await db.UpdatePresenceAsync(1,new("online",0),None);
            await db.UpdatePresenceAsync(1,new(null,3600),None);
            Check((await db.GetChatV2StateAsync(2,None)).Contacts.Single().Presence=="online","An idle second launcher cannot overwrite a recently active launcher.");
            await Sql(c,$"UPDATE atlas_launcher_session SET updated_at=UTC_TIMESTAMP()-INTERVAL 61 SECOND WHERE account_id=1; UPDATE `{characters}`.characters SET online=0 WHERE account=1;");
            Check((await db.GetChatV2StateAsync(2,None)).Contacts.Single().Presence=="offline","Disconnected sessions with no game character expire offline.");
            await Error(()=>db.UpdatePresenceAsync(1,new("game",0),None),"presence-invalid-request");
            await Error(()=>db.UpdatePresenceAsync(1,new(null,-1),None),"presence-invalid-request");
            await ValidateHttp(options,db);
            await Sql(c,"ALTER TABLE atlas_launcher_presence ADD COLUMN fixture_drift INT NULL;");
            bool drift=false;try{await new LauncherSchemaValidator().ValidatePresenceAsync(c,None);}catch(InvalidOperationException){drift=true;}Check(drift,"Presence schema drift rejected.");
            Console.WriteLine($"Presence API MySQL {version} PASS: {_checks} assertions. Migration8, DND carryover,20min boundary, manual persistence, cross-session activity, invisible game/friends projection, events, bidirectional legacy preferences and authenticated HTTP. Synthetic loopback fixture only.");
            return 0;
        }
        finally{for(int i=created.Count-1;i>=0;i--)await Sql(admin,$"DROP DATABASE `{created[i]}`;");}
    }
    private static async Task ValidateHttp(LauncherServerOptions options,LauncherDatabase db)
    {
        WebApplicationBuilder builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Development"});builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(options);builder.Services.AddSingleton(db);builder.Services.AddSingleton<ChatRequestLimiter>();
        await using WebApplication app=builder.Build();app.MapPresenceEndpoints();await app.StartAsync();
        try
        {
            using HttpClient http=new(){BaseAddress=new Uri(app.Urls.Single())};
            using(var response=await Request(http,"{}",false))Check(response.StatusCode==HttpStatusCode.Unauthorized,"Presence HTTP requires authentication.");
            using(var response=await Request(http,"{\"status\":\"dnd\",\"idleSeconds\":0}"))
            {Check(response.IsSuccessStatusCode,"Authenticated presence HTTP accepted.");LauncherPresenceStateDto? state=await response.Content.ReadFromJsonAsync<LauncherPresenceStateDto>();Check(state is{AccountId:1,ManualStatus:"dnd",Status:"dnd"},"HTTP ownership comes from bearer account.");}
            foreach(string json in new[]{"{\"status\":\"game\",\"idleSeconds\":0}","{\"status\":\"online\",\"idleSeconds\":-1}","{\"status\":\"online\",\"idleSeconds\":0,\"accountId\":2}",new string('x',1025)})
                using(var response=await Request(http,json))Check(response.StatusCode==HttpStatusCode.BadRequest,"Invalid bounded presence payload rejected.");
            options.MaximumSchemaVersion=7;
            using(var response=await Request(http,"{\"status\":\"offline\",\"idleSeconds\":0}"))Check(response.StatusCode==HttpStatusCode.NotImplemented,"Schema7 explicitly reports unsupported presence.");
            options.MaximumSchemaVersion=8;
        }
        finally{await app.StopAsync();}
    }
    private static async Task<HttpResponseMessage> Request(HttpClient http,string json,bool authorized=true)
    {using HttpRequestMessage request=new(HttpMethod.Put,"/api/v1/me/presence"){Content=new StringContent(json,Encoding.UTF8,"application/json")};if(authorized)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",Token(1));return await http.SendAsync(request);}
    private static string Token(uint account)=>"presence-fixture-token-"+account+"-0123456789abcdef0123456789abcdef";
    private static async Task Error(Func<Task> action,string code){try{await action();throw new InvalidOperationException("Expected "+code);}catch(ChatOperationException error){Check(error.Code==code,"Expected error "+code);}}
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException(message);_checks++;}
    private static async Task Sql(MySqlConnection c,string sql,params(string Name,object Value)[] args){await using MySqlCommand command=c.CreateCommand();command.CommandText=sql;foreach(var(name,value)in args)command.Parameters.AddWithValue(name,value);await command.ExecuteNonQueryAsync();}
    private static async Task<object?> Scalar(MySqlConnection c,string sql){await using MySqlCommand command=c.CreateCommand();command.CommandText=sql;return await command.ExecuteScalarAsync();}
}
