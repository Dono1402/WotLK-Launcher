using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using MySqlConnector;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

internal static class ChatV2Endpoints
{
    private const int MaximumJsonBytes=32*1024;
    private static readonly JsonSerializerOptions RequestJson=CreateRequestOptions();
    private static JsonSerializerOptions CreateRequestOptions(){var options=ChatJson.CreateOptions();options.PropertyNameCaseInsensitive=false;options.UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow;options.MaxDepth=12;return options;}

    internal static void MapChatV2Endpoints(this WebApplication app)
    {
        const string root="/api/v2/chat";
        app.MapGet(root+"/state",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx);return db.GetChatV2StateAsync(a,ct);},ct));
        app.MapGet(root+"/preferences",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx);return db.GetChatV2PreferencesAsync(a,ct);},ct));
        app.MapPatch(root+"/preferences",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.UpdateChatV2PreferencesAsync(a,await Read<ChatPreferencesRequest>(ctx,ct),ct);},ct));
        app.MapPost(root+"/threads",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.CreateChatV2ThreadAsync(a,await Read<ChatCreateThreadRequest>(ctx,ct),ct);},ct));
        app.MapPatch(root+"/threads/{threadId:long}",(long threadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,CancellationToken ct)=>Run(ctx,db,limiter,async a=>
        {
            Query(ctx);ChatThreadUpdateRequest request=await Read<ChatThreadUpdateRequest>(ctx,ct);
            ChatAttachmentDto? avatar=string.IsNullOrEmpty(request.AvatarAttachmentId)?null:await files.RequireCompleteAsync(a,request.AvatarAttachmentId,ct);
            return await db.UpdateChatV2ThreadAsync(a,threadId,request,avatar,ct);
        },ct));
        app.MapPatch(root+"/threads/{threadId:long}/self",(long threadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.UpdateChatV2ThreadSelfAsync(a,threadId,await Read<ChatThreadSelfRequest>(ctx,ct),ct);},ct));
        app.MapPost(root+"/threads/{threadId:long}/members",(long threadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.ChangeChatV2MemberAsync(a,threadId,await Read<ChatMemberRequest>(ctx,ct),ct);},ct));
        app.MapGet(root+"/threads/{threadId:long}/messages",(long threadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx,"beforeId","limit");return db.GetChatV2MessagesAsync(a,threadId,Cursor(ctx,"beforeId"),Limit(ctx),ct);},ct));
        app.MapGet(root+"/threads/{threadId:long}/messages/by-client/{clientMessageId:guid}",(long threadId,Guid clientMessageId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx);return db.GetChatV2MessageByClientAsync(a,threadId,clientMessageId,ct);},ct));
        app.MapPost(root+"/threads/{threadId:long}/messages",(long threadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,ChatLinkPreviewService links,CancellationToken ct)=>Run(ctx,db,limiter,async a=>
        {
            Query(ctx);ChatSendMessageRequest request=await Read<ChatSendMessageRequest>(ctx,ct);
            if(request.AttachmentIds is null||request.AttachmentIds.Count>ChatLimits.MaximumAttachmentsPerMessage)throw new ChatOperationException("chat-invalid-request");
            List<ChatAttachmentDto> attachments=[];foreach(string id in request.AttachmentIds)attachments.Add(await files.RequireCompleteAsync(a,id,ct));
            IReadOnlyList<ChatLinkPreviewDto> previews=await links.BuildAsync(request.Body,ct);
            return await db.SendChatV2MessageAsync(a,threadId,request,attachments,previews,ct);
        },ct));
        app.MapPatch(root+"/threads/{threadId:long}/messages/{messageId:long}",(long threadId,long messageId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatLinkPreviewService links,CancellationToken ct)=>Run(ctx,db,limiter,async a=>
        {
            Query(ctx);ChatEditMessageRequest request=await Read<ChatEditMessageRequest>(ctx,ct);
            return await db.EditChatV2MessageAsync(a,threadId,messageId,request,await links.BuildAsync(request.Body,ct),ct);
        },ct));
        app.MapDelete(root+"/threads/{threadId:long}/messages/{messageId:long}",(long threadId,long messageId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx);return db.DeleteChatV2MessageAsync(a,threadId,messageId,ct);},ct));
        app.MapPut(root+"/threads/{threadId:long}/messages/{messageId:long}/reaction",(long threadId,long messageId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.ReactChatV2MessageAsync(a,threadId,messageId,await Read<ChatReactionRequest>(ctx,ct),ct);},ct));
        app.MapPut(root+"/threads/{threadId:long}/messages/{messageId:long}/pin",(long threadId,long messageId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.PinChatV2MessageAsync(a,threadId,messageId,await Read<ChatPinRequest>(ctx,ct),ct);},ct));
        app.MapDelete(root+"/threads/{threadId:long}/messages/{messageId:long}/previews/{previewId}",(long threadId,long messageId,string previewId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx);return db.RemoveChatV2PreviewAsync(a,threadId,messageId,previewId,ct);},ct));
        app.MapPut(root+"/threads/{threadId:long}/messages/{messageId:long}/card-response",(long threadId,long messageId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.RespondChatV2CardAsync(a,threadId,messageId,await Read<ChatCardResponseRequest>(ctx,ct),ct);},ct));
        app.MapPost(root+"/threads/{threadId:long}/read",(long threadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await db.ReadChatV2ThreadAsync(a,threadId,await Read<ChatReadRequest>(ctx,ct),ct);},ct));
        app.MapPut(root+"/threads/{threadId:long}/typing",(long threadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);await db.SetChatV2TypingAsync(a,threadId,(await Read<ChatTypingRequest>(ctx,ct)).IsTyping,ct);return new{accepted=true};},ct));
        app.MapGet(root+"/events",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx,"afterId","limit","waitSeconds");long wait=Cursor(ctx,"waitSeconds")??25;if(wait>25)throw new ChatOperationException("chat-invalid-query");return db.PollChatV2EventsAsync(a,Cursor(ctx,"afterId")??0,Limit(ctx,100),(int)wait,ct);},ct));

        app.MapPost(root+"/uploads",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);return await files.BeginAsync(a,await Read<ChatUploadRequest>(ctx,ct),ct);},ct));
        app.MapGet(root+"/uploads/{uploadId}",(string uploadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx);return files.GetAsync(a,uploadId,ct);},ct));
        app.MapPut(root+"/uploads/{uploadId}",(string uploadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,CancellationToken ct)=>Run(ctx,db,limiter,a=>
        {
            Query(ctx,"offset");if(!string.Equals(ctx.Request.ContentType,"application/octet-stream",StringComparison.OrdinalIgnoreCase))throw new ChatOperationException("chat-invalid-request");
            var feature=ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();if(feature is not null&&!feature.IsReadOnly)feature.MaxRequestBodySize=ChatLimits.UploadChunkBytes;
            return files.AppendAsync(a,uploadId,Cursor(ctx,"offset")??throw new ChatOperationException("chat-invalid-query"),ctx.Request.Body,ctx.Request.ContentLength,ct);
        },ct));
        app.MapPost(root+"/uploads/{uploadId}/complete",(string uploadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,CancellationToken ct)=>Run(ctx,db,limiter,a=>{Query(ctx);return files.CompleteAsync(a,uploadId,ct);},ct));
        app.MapDelete(root+"/uploads/{uploadId}",(string uploadId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,CancellationToken ct)=>Run(ctx,db,limiter,async a=>{Query(ctx);await files.CancelAsync(a,uploadId,ct);return new{accepted=true};},ct));
        app.MapGet(root+"/attachments/{attachmentId}",(string attachmentId,HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatAttachmentStorage files,CancellationToken ct)=>Run(ctx,db,limiter,async a=>
        {
            Query(ctx);if(!await db.CanReadChatV2AttachmentAsync(a,attachmentId,ct))throw new ChatOperationException("chat-not-found");
            ChatAttachmentRead file=await files.OpenReadAsync(attachmentId,ct);ctx.Response.Headers.XContentTypeOptions="nosniff";
            return Results.Stream(file.Stream,file.ContentType,enableRangeProcessing:true);
        },ct));
        app.MapGet(root+"/preview-image",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatLinkPreviewService links,CancellationToken ct)=>Run(ctx,db,limiter,async a=>
        {
            Query(ctx,"url");string url=ctx.Request.Query["url"].ToString();if(url.Length is <1 or >4096)throw new ChatOperationException("chat-invalid-query");
            ChatPreviewImage? image=await links.FetchImageAsync(url,ct);if(image is null)throw new ChatOperationException("chat-not-found");
            ctx.Response.Headers.XContentTypeOptions="nosniff";return Results.Bytes(image.Bytes,image.ContentType);
        },ct));
        app.MapGet(root+"/linked-media",(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,ChatLinkPreviewService links,CancellationToken ct)=>Run(ctx,db,limiter,async a=>
        {
            Query(ctx,"url");string url=ctx.Request.Query["url"].ToString();if(url.Length is <1 or >4096)throw new ChatOperationException("chat-invalid-query");
            return new LinkedMediaResult(await links.OpenMediaAsync(url,ctx.Request.Headers.Range.ToString(),ct));
        },ct));
    }

    private sealed class LinkedMediaResult(ChatLinkedMediaRead media):IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            await using Stream stream=media.Stream;
            context.Response.StatusCode=media.StatusCode;context.Response.ContentType=media.ContentType;
            if(media.Length is not null)context.Response.ContentLength=media.Length;
            if(media.ContentRange is not null)context.Response.Headers.ContentRange=media.ContentRange;
            context.Response.Headers.AcceptRanges="bytes";context.Response.Headers.XContentTypeOptions="nosniff";
            await stream.CopyToAsync(context.Response.Body,context.RequestAborted);
        }
    }

    private static async Task<IResult> Run<T>(HttpContext ctx,LauncherDatabase db,ChatRequestLimiter limiter,Func<uint,Task<T>> operation,CancellationToken ct)
    {
        ctx.Response.Headers.CacheControl="no-store";
        try
        {
            AuthenticatedAccount? account=await AtlasRequestAuthentication.AuthenticateAsync(ctx,db,ct);if(account is null)return Results.Unauthorized();
            if(!db.ChatV2Available)throw new ChatOperationException("chat-unavailable");
            using RateLimitLease lease=limiter.Acquire(account.AccountId);if(!lease.IsAcquired)throw new ChatOperationException("chat-rate-limited");
            T result=await operation(account.AccountId);return result is IResult http?http:Results.Json(result,ChatJson.Options);
        }
        catch(ChatOperationException error)
        {
            int status=error.Code switch
            {
                "chat-not-found" or "chat-not-friends" or "chat-upload-not-found"=>404,
                "chat-forbidden" or "chat-preview-not-removable"=>403,
                "chat-idempotency-conflict" or "chat-version-conflict" or "chat-upload-offset-conflict"=>409,
                "chat-rate-limited"=>429,"chat-unavailable"=>503,"chat-request-too-large"=>413,_=>400
            };
            if(status==429)ctx.Response.Headers.RetryAfter="60";
            return Results.Json(new ChatErrorDto(error.Code),ChatJson.Options,statusCode:status);
        }
        catch(BadHttpRequestException error)when(error.StatusCode==StatusCodes.Status413PayloadTooLarge)
        {return Results.Json(new ChatErrorDto("chat-request-too-large"),ChatJson.Options,statusCode:413);}
        catch(Exception error)when(error is JsonException or NotSupportedException or FormatException or ArgumentException)
        {return Results.Json(new ChatErrorDto("chat-invalid-request"),ChatJson.Options,statusCode:400);}
        catch(Exception error)when(error is MySqlException or InvalidDataException or OverflowException or InvalidCastException or IOException)
        {return Results.Json(new ChatErrorDto("chat-unavailable"),ChatJson.Options,statusCode:503);}
    }

    private static async Task<T> Read<T>(HttpContext ctx,CancellationToken ct)
    {
        if(!ctx.Request.HasJsonContentType())throw new ChatOperationException("chat-invalid-request");
        if(ctx.Request.ContentLength>MaximumJsonBytes)throw new ChatOperationException("chat-request-too-large");
        using MemoryStream bytes=new();byte[] buffer=new byte[4096];int count;
        while((count=await ctx.Request.Body.ReadAsync(buffer,ct))>0){if(bytes.Length+count>MaximumJsonBytes)throw new ChatOperationException("chat-request-too-large");bytes.Write(buffer,0,count);}
        using JsonDocument json=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions(){MaxDepth=12});
        if(json.RootElement.ValueKind!=JsonValueKind.Object)throw new ChatOperationException("chat-invalid-request");
        Unique(json.RootElement);return json.RootElement.Deserialize<T>(RequestJson)??throw new ChatOperationException("chat-invalid-request");
    }
    private static void Unique(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object){HashSet<string> names=new(StringComparer.Ordinal);foreach(JsonProperty property in element.EnumerateObject()){if(!names.Add(property.Name))throw new ChatOperationException("chat-invalid-request");Unique(property.Value);}}
        else if(element.ValueKind==JsonValueKind.Array)foreach(JsonElement item in element.EnumerateArray())Unique(item);
    }
    private static void Query(HttpContext ctx,params string[] allowed){foreach(var field in ctx.Request.Query)if(!allowed.Contains(field.Key,StringComparer.Ordinal)||field.Value.Count!=1)throw new ChatOperationException("chat-invalid-query");}
    private static long? Cursor(HttpContext ctx,string name){if(!ctx.Request.Query.TryGetValue(name,out var value))return null;if(!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out long parsed)||parsed<0)throw new ChatOperationException("chat-invalid-query");return parsed;}
    private static int Limit(HttpContext ctx,int fallback=50){long limit=Cursor(ctx,"limit")??fallback;return limit is >=1 and <=100?(int)limit:throw new ChatOperationException("chat-invalid-query");}
}
