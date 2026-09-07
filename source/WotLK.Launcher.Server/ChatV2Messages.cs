using System.Globalization;
using MySqlConnector;
using WotLK.Launcher.Chat;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal Task<ChatSendMessageResult> SendChatV2MessageAsync(uint account,long thread,ChatSendMessageRequest request,
        IReadOnlyList<ChatAttachmentDto> attachments,IReadOnlyList<ChatLinkPreviewDto> previews,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        V2Access access=await RequireV2AccessAsync(c,t,account,thread,token);
        if(request.ClientMessageId==Guid.Empty||request.AttachmentIds is null||request.AttachmentIds.Count>ChatLimits.MaximumAttachmentsPerMessage||request.AttachmentIds.Count!=attachments.Count||request.AttachmentIds.Distinct(StringComparer.Ordinal).Count()!=attachments.Count||!request.AttachmentIds.SequenceEqual(attachments.Select(x=>x.Id),StringComparer.Ordinal))throw new ChatOperationException("chat-invalid-request");
        string body=string.IsNullOrWhiteSpace(request.Body)?"":ChatMessageValidation.Normalize(request.Body);
        ChatCardDto? card=ValidateV2Card(request.Card);
        if(body.Length==0&&attachments.Count==0&&card is null)throw new ChatOperationException("chat-invalid-message");
        byte[] hash=ChatHash(ChatSerialize(new{Thread=thread,Body=body,request.ReplyToMessageId,request.AttachmentIds,Card=card}));
        long duplicate=0;
        await using(MySqlCommand command=V2Command(c,t,"SELECT id,thread_id,request_hash FROM atlas_launcher_chat_v2_message WHERE sender_account_id=@account AND client_message_id=@request;",("@account",account),("@request",request.ClientMessageId.ToByteArray(bigEndian:true))))
        {
            await using MySqlDataReader r=await command.ExecuteReaderAsync(token);
            if(await r.ReadAsync(token)){if(r.GetInt64("thread_id")!=thread||!((byte[])r["request_hash"]).SequenceEqual(hash))throw new ChatOperationException("chat-idempotency-conflict");duplicate=r.GetInt64("id");}
        }
        if(duplicate!=0)return new ChatSendMessageResult(){Message=await V2MessageAsync(c,t,account,access,duplicate,token),IsDuplicate=true};
        if(request.ReplyToMessageId is not null) _=await V2MessageAsync(c,t,account,access,request.ReplyToMessageId.Value,token);
        if(attachments.Any(a=>a.Size is <=0 or >ChatLimits.MaximumAttachmentBytes))throw new ChatOperationException("chat-invalid-attachment");
        long id;
        if(access.Kind==0&&body.Length>0)
        {
            uint recipient=access.Low==account?access.High!.Value:access.Low!.Value;
            string legacyBody=ChatPlainTextProjection.ForLegacy(body);
            LauncherChatSendResult legacy=await InsertChatMessageAsync(c,t,account,recipient,request.ClientMessageId,legacyBody,0,token);
            id=await V2ScalarAsync(c,t,"SELECT id FROM atlas_launcher_chat_v2_message WHERE legacy_message_id=@legacy;",token,("@legacy",legacy.Message.Id));
            await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_message SET body=@body,request_hash=@hash,reply_to_message_id=@reply,attachments_json=@attachments,previews_json=@previews,card_json=@card WHERE id=@id;",token,("@id",id),("@body",body),("@hash",hash),("@reply",request.ReplyToMessageId),("@attachments",ChatSerialize(attachments)),("@previews",ChatSerialize(previews.Take(ChatLimits.MaximumPreviewsPerMessage).ToArray())),("@card",card is null?null:ChatSerialize(card)));
        }
        else
        {
            await ConsumeV2QuotaAsync(c,t,account,token);
            ChatProfileDto sender=await V2ProfileAsync(c,t,account,token);
            id=await V2InsertAsync(c,t,"""
                INSERT INTO atlas_launcher_chat_v2_message(thread_id,sender_account_id,client_message_id,request_hash,sender_username,body,reply_to_message_id,attachments_json,previews_json,card_json)
                VALUES(@thread,@account,@request,@hash,@username,@body,@reply,@attachments,@previews,@card);
                """,token,("@thread",thread),("@account",account),("@request",request.ClientMessageId.ToByteArray(bigEndian:true)),("@hash",hash),("@username",sender.Username),("@body",body),("@reply",request.ReplyToMessageId),("@attachments",ChatSerialize(attachments)),("@previews",ChatSerialize(previews.Take(ChatLimits.MaximumPreviewsPerMessage).ToArray())),("@card",card is null?null:ChatSerialize(card)));
            await TouchV2NewMessageAsync(c,t,thread,id,token);
        }
        return new ChatSendMessageResult(){Message=await V2MessageAsync(c,t,account,access,id,token)};
    },token);

    private static ChatCardDto? ValidateV2Card(ChatCardDto? card)
    {
        if(card is null)return null;
        if(card.Kind is not("item" or "character" or "quest" or "location" or "event")||card.Title is null||card.Description is null||card.Fields is null||card.Responses is null||card.Title.Length is <1 or >120||card.Description.Length>1000||card.ReferenceId?.Length>128||card.Fields.Count>12||card.Responses.Count!=0||card.Fields.Any(x=>x.Key.Length>40||x.Value is null||x.Value.Length>500))throw new ChatOperationException("chat-invalid-card");
        foreach(string? url in new[]{card.Url,card.ImageUrl})if(url is not null&&(!Uri.TryCreate(url,UriKind.Absolute,out Uri? uri)||uri.Scheme is not("http" or "https")||url.Length>2048))throw new ChatOperationException("chat-invalid-card");
        return card with{Fields=card.Fields.OrderBy(x=>x.Key,StringComparer.Ordinal).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.Ordinal),Responses=[]};
    }

    private static async Task ConsumeV2QuotaAsync(MySqlConnection c,MySqlTransaction t,uint account,CancellationToken token)
    {
        await V2ExecuteAsync(c,t,"INSERT INTO atlas_launcher_chat_account(account_id) VALUES(@account) ON DUPLICATE KEY UPDATE account_id=@account;",token,("@account",account));
        long changed=await V2ExecuteAsync(c,t,"""
            UPDATE atlas_launcher_chat_account SET send_window_count=CASE WHEN send_window_started_at IS NULL OR send_window_started_at<=UTC_TIMESTAMP(6)-INTERVAL 60 SECOND THEN 1 ELSE send_window_count+1 END,
              send_window_started_at=CASE WHEN send_window_started_at IS NULL OR send_window_started_at<=UTC_TIMESTAMP(6)-INTERVAL 60 SECOND THEN UTC_TIMESTAMP(6) ELSE send_window_started_at END
            WHERE account_id=@account AND(send_window_started_at IS NULL OR send_window_started_at<=UTC_TIMESTAMP(6)-INTERVAL 60 SECOND OR send_window_count<30);
            """,token,("@account",account));
        if(changed!=1)throw new ChatOperationException("chat-rate-limited");
    }

    private static async Task TouchV2NewMessageAsync(MySqlConnection c,MySqlTransaction t,long thread,long message,CancellationToken token)
    {
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_thread SET last_message_id=@message,version=version+1,updated_at=UTC_TIMESTAMP(6) WHERE id=@thread; UPDATE atlas_launcher_chat_v2_member SET is_archived=FALSE WHERE thread_id=@thread AND status=1;",token,("@thread",thread),("@message",message));
        await EmitV2Async(c,t,"message",thread,message,null,token);
    }

    private static async Task ImportLegacyV2Async(MySqlConnection c,MySqlTransaction t,LauncherChatMessage legacy,CancellationToken token)
    {
        long thread=await EnsureV2DirectAsync(c,t,legacy.SenderAccountId,legacy.RecipientAccountId,token);
        long existing=await V2ScalarAsync(c,t,"SELECT id FROM atlas_launcher_chat_v2_message WHERE legacy_message_id=@id;",token,("@id",legacy.Id));
        if(existing!=0)return;
        long id=await V2InsertAsync(c,t,"""
            INSERT INTO atlas_launcher_chat_v2_message(thread_id,sender_account_id,client_message_id,request_hash,legacy_request_hash,legacy_message_id,sender_username,body,origin,attachments_json,previews_json,created_at)
            VALUES(@thread,@sender,@request,@hash,@hash,@legacy,@name,@body,@origin,JSON_ARRAY(),JSON_ARRAY(),@created);
            """,token,("@thread",thread),("@sender",legacy.SenderAccountId),("@request",legacy.ClientMessageId.ToByteArray(bigEndian:true)),("@hash",ChatLegacyHash(legacy.RecipientAccountId,legacy.Origin=="game"?(byte)1:(byte)0,legacy.Body)),("@legacy",legacy.Id),("@name",legacy.SenderUsername),("@body",legacy.Body),("@origin",legacy.Origin=="game"?1:0),("@created",legacy.CreatedAt.UtcDateTime));
        await TouchV2NewMessageAsync(c,t,thread,id,token);
    }

    internal Task<ChatMessageDto> EditChatV2MessageAsync(uint account,long thread,long id,ChatEditMessageRequest request,IReadOnlyList<ChatLinkPreviewDto> previews,CancellationToken token)=>MutateV2MessageAsync(account,thread,id,async(c,t,access,message)=>
    {
        RequireV2Author(account,message);
        if(message.DeletedAt is not null)throw new ChatOperationException("chat-message-deleted");
        if(request.ExpectedVersion is not null&&request.ExpectedVersion!=message.Version)throw new ChatOperationException("chat-version-conflict");
        string body=string.IsNullOrWhiteSpace(request.Body)?"":ChatMessageValidation.Normalize(request.Body);
        if(body.Length==0&&message.Attachments.Count==0&&message.Card is null)throw new ChatOperationException("chat-invalid-message");
        HashSet<string> removed=message.LinkPreviews.Where(p=>p.IsRemoved).Select(p=>p.Id).ToHashSet(StringComparer.Ordinal);
        List<ChatLinkPreviewDto> updated=previews.Take(ChatLimits.MaximumPreviewsPerMessage).Select(p=>removed.Contains(p.Id)?p with{IsRemoved=true}:p).ToList();
        // Keep suppression tombstones even when the link temporarily disappears from the body.
        updated.AddRange(message.LinkPreviews.Where(p=>p.IsRemoved&&updated.All(x=>x.Id!=p.Id)));
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_message SET body=@body,previews_json=@previews,version=version+1,edited_at=UTC_TIMESTAMP(6) WHERE id=@id;",token,("@body",body),("@previews",ChatSerialize(updated)),("@id",id));
        await V2ExecuteAsync(c,t,"""
            UPDATE atlas_launcher_chat_message legacy JOIN atlas_launcher_chat_v2_message m ON m.legacy_message_id=legacy.id SET legacy.body=@body WHERE m.id=@id;
            """,token,("@body",ChatPlainTextProjection.ForLegacy(body)),("@id",id));
    },token);

    internal Task<ChatMessageDto> DeleteChatV2MessageAsync(uint account,long thread,long id,CancellationToken token)=>MutateV2MessageAsync(account,thread,id,async(c,t,access,message)=>
    {
        RequireV2Author(account,message);if(message.DeletedAt is not null)return;
        await V2ExecuteAsync(c,t,"""
            UPDATE atlas_launcher_chat_v2_message SET body='',attachments_json=JSON_ARRAY(),previews_json=JSON_ARRAY(),card_json=NULL,is_pinned=FALSE,deleted_at=UTC_TIMESTAMP(6),version=version+1 WHERE id=@id;
            DELETE FROM atlas_launcher_chat_v2_reaction WHERE message_id=@id;
            UPDATE atlas_launcher_chat_message legacy JOIN atlas_launcher_chat_v2_message m ON m.legacy_message_id=legacy.id SET legacy.body='[Message supprimé]' WHERE m.id=@id;
            UPDATE atlas_launcher_chat_outbox o JOIN atlas_launcher_chat_v2_message m ON m.legacy_message_id=o.message_id SET o.status=4,o.last_error='message-deleted',o.lease_token=NULL,o.lease_until=NULL WHERE m.id=@id AND o.status IN(0,1);
            """,token,("@id",id));
    },token);

    private static void RequireV2Author(uint account,ChatMessageDto message)
    {if(message.Sender.AccountId!=account)throw new ChatOperationException("chat-forbidden");}

    private Task<ChatMessageDto> MutateV2MessageAsync(uint account,long thread,long id,Func<MySqlConnection,MySqlTransaction,V2Access,ChatMessageDto,Task> change,CancellationToken token)=>MutateV2Async(async(c,t)=>
    {
        V2Access access=await RequireV2AccessAsync(c,t,account,thread,token);ChatMessageDto message=await V2MessageAsync(c,t,account,access,id,token);
        await change(c,t,access,message);await EmitV2Async(c,t,"message",thread,id,null,token);return await V2MessageAsync(c,t,account,access,id,token);
    },token);

    internal Task<ChatMessageDto> ReactChatV2MessageAsync(uint account,long thread,long id,ChatReactionRequest request,CancellationToken token)=>MutateV2MessageAsync(account,thread,id,async(c,t,access,message)=>
    {
        if(message.DeletedAt is not null)throw new ChatOperationException("chat-message-deleted");
        string emoji=request.Emoji??"";
        if(emoji.Length is <1 or >32||emoji.Any(ch=>char.IsControl(ch)||char.IsWhiteSpace(ch)))throw new ChatOperationException("chat-invalid-reaction");
        if(request.Active&&await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_reaction WHERE message_id=@id AND account_id=@account;",token,("@id",id),("@account",account))>=20&&await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_reaction WHERE message_id=@id AND account_id=@account AND emoji=@emoji;",token,("@id",id),("@account",account),("@emoji",emoji))==0)throw new ChatOperationException("chat-too-many-reactions");
        await V2ExecuteAsync(c,t,request.Active?"INSERT IGNORE INTO atlas_launcher_chat_v2_reaction(message_id,account_id,emoji) VALUES(@id,@account,@emoji);":"DELETE FROM atlas_launcher_chat_v2_reaction WHERE message_id=@id AND account_id=@account AND emoji=@emoji;",token,("@id",id),("@account",account),("@emoji",emoji));
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_message SET version=version+1 WHERE id=@id;",token,("@id",id));
    },token);

    internal Task<ChatMessageDto> PinChatV2MessageAsync(uint account,long thread,long id,ChatPinRequest request,CancellationToken token)=>MutateV2MessageAsync(account,thread,id,async(c,t,access,message)=>
    {
        if(message.DeletedAt is not null)throw new ChatOperationException("chat-message-deleted");
        if(request.Pinned&&!message.IsPinned&&await V2ScalarAsync(c,t,"SELECT COUNT(*) FROM atlas_launcher_chat_v2_message WHERE thread_id=@thread AND is_pinned;",token,("@thread",thread))>=50)throw new ChatOperationException("chat-too-many-pins");
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_message SET is_pinned=@pin,version=version+1 WHERE id=@id;",token,("@id",id),("@pin",request.Pinned));
    },token);

    internal Task<ChatMessageDto> RemoveChatV2PreviewAsync(uint account,long thread,long id,string previewId,CancellationToken token)=>MutateV2MessageAsync(account,thread,id,async(c,t,access,message)=>
    {
        ChatLinkPreviewDto? preview=message.LinkPreviews.FirstOrDefault(p=>p.Id==previewId);
        if(preview is null)throw new ChatOperationException("chat-not-found");
        if(!preview.CanRemove||preview.Kind=="video")throw new ChatOperationException("chat-preview-not-removable");
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_message SET previews_json=@previews,version=version+1 WHERE id=@id;",token,("@id",id),("@previews",ChatSerialize(message.LinkPreviews.Select(p=>p.Id==previewId?p with{IsRemoved=true}:p).ToArray())));
    },token);

    internal Task<ChatMessageDto> RespondChatV2CardAsync(uint account,long thread,long id,ChatCardResponseRequest request,CancellationToken token)=>MutateV2MessageAsync(account,thread,id,async(c,t,access,message)=>
    {
        if(message.DeletedAt is not null||message.Card is null||message.Card.Kind!="event")throw new ChatOperationException("chat-invalid-card");
        if(request.Status is not("joining" or "maybe" or "declined" or "none")||request.Role is not("tank" or "healer" or "damage"))throw new ChatOperationException("chat-invalid-request");
        List<ChatCardResponseDto> responses=message.Card.Responses.Where(x=>x.AccountId!=account).ToList();
        if(request.Status!="none")responses.Add(new(){AccountId=account,Username=(await V2ProfileAsync(c,t,account,token)).Username,Status=request.Status,Role=request.Role});
        await V2ExecuteAsync(c,t,"UPDATE atlas_launcher_chat_v2_message SET card_json=@card,version=version+1 WHERE id=@id;",token,("@id",id),("@card",ChatSerialize(message.Card with{Responses=responses})));
    },token);

    internal Task<bool> CanReadChatV2AttachmentAsync(uint account,string attachmentId,CancellationToken token)=>ReadV2Async(async(c,t)=>
    {
        if(!Guid.TryParseExact(attachmentId,"N",out _)&&!Guid.TryParse(attachmentId,out _))return false;
        List<long> threads=await V2IdsAsync(c,t,"""
            SELECT th.id FROM atlas_launcher_chat_v2_thread th JOIN atlas_launcher_chat_v2_member me ON me.thread_id=th.id AND me.account_id=@account AND me.status=1
            WHERE(th.kind=1 OR EXISTS(SELECT 1 FROM atlas_launcher_friendship f WHERE f.account_low_id=th.account_low_id AND f.account_high_id=th.account_high_id AND f.accepted_at IS NOT NULL))
            AND ((JSON_UNQUOTE(JSON_EXTRACT(th.avatar_json,'$.id'))=@attachment) OR EXISTS(SELECT 1 FROM atlas_launcher_chat_v2_message m WHERE m.thread_id=th.id AND m.id>me.history_after_id AND m.deleted_at IS NULL AND JSON_CONTAINS(m.attachments_json,JSON_OBJECT('id',@attachment))));
            """,token,("@account",account),("@attachment",attachmentId));return threads.Count>0;
    },token);
}
