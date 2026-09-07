using System.Text.Json;
using System.Text.Json.Serialization;
using MySqlConnector;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

internal static class PresenceEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=4 };

    internal static void MapPresenceEndpoints(this WebApplication app) => app.MapPut("/api/v1/me/presence",
        async (HttpContext context,LauncherDatabase database,ChatRequestLimiter limiter,CancellationToken token) =>
        {
            context.Response.Headers.CacheControl="no-store";
            try
            {
                AuthenticatedAccount? account=await AtlasRequestAuthentication.AuthenticateAsync(context,database,token);
                if(account is null)return Results.Unauthorized();
                if(!database.PresenceAvailable)return Results.Json(new{error="presence-unavailable"},statusCode:501);
                using var lease=limiter.Acquire(account.AccountId);
                if(!lease.IsAcquired)return Results.Json(new{error="presence-rate-limited"},statusCode:429);
                if(context.Request.Query.Count>0 || !context.Request.HasJsonContentType() || context.Request.ContentLength>1024)
                    return Results.Json(new{error="presence-invalid-request"},statusCode:400);
                using MemoryStream bytes=new();byte[] buffer=new byte[512];int count;
                while((count=await context.Request.Body.ReadAsync(buffer,token))!=0)
                {if(bytes.Length+count>1024)return Results.Json(new{error="presence-invalid-request"},statusCode:400);bytes.Write(buffer,0,count);}
                LauncherPresenceUpdateRequest request=JsonSerializer.Deserialize<LauncherPresenceUpdateRequest>(bytes.ToArray(),Json)??throw new JsonException();
                return Results.Json(await database.UpdatePresenceAsync(account.AccountId,request,token),Json);
            }
            catch(ChatOperationException error){return Results.Json(new{error=error.Code},statusCode:error.Code=="presence-unavailable"?501:400);}
            catch(JsonException){return Results.Json(new{error="presence-invalid-request"},statusCode:400);}
            catch(MySqlException){return Results.Json(new{error="presence-unavailable"},statusCode:503);}
        });
}
