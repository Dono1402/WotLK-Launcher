using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed partial class LauncherSchemaValidator
{
    // Ordered names, exact MySQL types and nullability detect accidental schema drift.
    private static readonly IReadOnlyDictionary<string,string> ChatV2Columns=new Dictionary<string,string>(StringComparer.Ordinal)
    {
        ["sequence"]="id:tinyint unsigned:NO|revision:bigint unsigned:NO",
        ["thread"]="id:bigint unsigned:NO|kind:tinyint unsigned:NO|account_low_id:int unsigned:YES|account_high_id:int unsigned:YES|owner_account_id:int unsigned:NO|created_by_account_id:int unsigned:NO|title:varchar(120):NO|avatar_json:json:YES|request_id:binary(16):YES|request_hash:binary(32):YES|last_message_id:bigint unsigned:NO|version:bigint unsigned:NO|created_at:datetime(6):NO|updated_at:datetime(6):NO",
        ["member"]="thread_id:bigint unsigned:NO|account_id:int unsigned:NO|role:varchar(8):NO|status:tinyint unsigned:NO|joined_at:datetime(6):NO|history_after_id:bigint unsigned:NO|last_read_message_id:bigint unsigned:NO|is_pinned:tinyint(1):NO|is_archived:tinyint(1):NO",
        ["request"]="account_id:int unsigned:NO|request_id:binary(16):NO|request_hash:binary(32):NO|thread_id:bigint unsigned:NO",
        ["message"]="id:bigint unsigned:NO|thread_id:bigint unsigned:NO|sender_account_id:int unsigned:NO|client_message_id:binary(16):NO|request_hash:binary(32):NO|legacy_request_hash:binary(32):YES|legacy_message_id:bigint unsigned:YES|sender_username:varchar(32):NO|sender_character_name:varchar(12):YES|body:varchar(1000):NO|origin:tinyint unsigned:NO|reply_to_message_id:bigint unsigned:YES|attachments_json:json:NO|previews_json:json:NO|card_json:json:YES|is_pinned:tinyint(1):NO|version:bigint unsigned:NO|created_at:datetime(6):NO|edited_at:datetime(6):YES|deleted_at:datetime(6):YES",
        ["reaction"]="message_id:bigint unsigned:NO|account_id:int unsigned:NO|emoji:varchar(32):NO",
        ["preferences"]="account_id:int unsigned:NO|do_not_disturb:tinyint(1):NO|share_read_receipts:tinyint(1):NO|share_typing:tinyint(1):NO",
        ["event"]="id:bigint unsigned:NO|account_id:int unsigned:NO|thread_id:bigint unsigned:YES|message_id:bigint unsigned:YES|kind:varchar(32):NO|payload_json:json:YES|created_at:datetime(6):NO"
    };

    private static readonly IReadOnlyDictionary<string,string[]> ChatV2UniqueKeys=new Dictionary<string,string[]>
    {
        ["sequence"]=["PRIMARY:id"],
        ["thread"]=["PRIMARY:id","uq_chat_v2_direct:account_low_id,account_high_id","uq_chat_v2_thread_request:created_by_account_id,request_id"],
        ["member"]=["PRIMARY:thread_id,account_id"],
        ["request"]=["PRIMARY:account_id,request_id"],
        ["message"]=["PRIMARY:id","uq_chat_v2_legacy:legacy_message_id","uq_chat_v2_sender_request:sender_account_id,client_message_id"],
        ["reaction"]=["PRIMARY:message_id,account_id,emoji"],
        ["preferences"]=["PRIMARY:account_id"],
        ["event"]=["PRIMARY:id"]
    };

    internal async Task ValidateChatV2Async(MySqlConnection connection,CancellationToken token)
    {
        foreach(var (suffix,expected) in ChatV2Columns)
        {
            string table="atlas_launcher_chat_v2_"+suffix;
            await using(MySqlCommand command=connection.CreateCommand())
            {
                command.CommandText="SELECT ENGINE,TABLE_COLLATION FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table;";
                command.Parameters.AddWithValue("@table",table);await using MySqlDataReader reader=await command.ExecuteReaderAsync(token);
                if(!await reader.ReadAsync(token)||reader.GetString(0)!="InnoDB"||reader.GetString(1)!="utf8mb4_0900_ai_ci")throw new InvalidOperationException($"Schema chat v2 invalide : {table}.");
            }
            List<string> columns=[];
            await using(MySqlCommand command=connection.CreateCommand())
            {
                command.CommandText="SELECT COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table ORDER BY ORDINAL_POSITION;";
                command.Parameters.AddWithValue("@table",table);await using MySqlDataReader reader=await command.ExecuteReaderAsync(token);
                while(await reader.ReadAsync(token))columns.Add($"{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetString(2)}");
            }
            if(string.Join('|',columns)!=expected)throw new InvalidOperationException($"Colonnes chat v2 invalides : {table}.");
            List<string> keys=[];
            await using(MySqlCommand command=connection.CreateCommand())
            {
                command.CommandText="SELECT INDEX_NAME,GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR ',') FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table AND NON_UNIQUE=0 GROUP BY INDEX_NAME ORDER BY INDEX_NAME;";
                command.Parameters.AddWithValue("@table",table);await using MySqlDataReader reader=await command.ExecuteReaderAsync(token);
                while(await reader.ReadAsync(token))keys.Add(reader.GetString(0)+":"+reader.GetString(1));
            }
            if(!keys.SequenceEqual(ChatV2UniqueKeys[suffix].Order(StringComparer.Ordinal),StringComparer.Ordinal))throw new InvalidOperationException($"Unicite chat v2 invalide : {table}.");
        }
        await using MySqlCommand invariants=connection.CreateCommand();
        invariants.CommandText="""
            SELECT (SELECT COUNT(*) FROM atlas_launcher_chat_v2_sequence WHERE id=1)=1
              AND(SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND CONSTRAINT_NAME IN('fk_chat_v2_thread_owner','fk_chat_v2_member_thread','fk_chat_v2_member_profile','fk_chat_v2_request_profile','fk_chat_v2_request_thread','fk_chat_v2_message_thread','fk_chat_v2_message_sender','fk_chat_v2_reaction_message','fk_chat_v2_reaction_profile','fk_chat_v2_preferences_profile','fk_chat_v2_event_profile'))=11
              AND(SELECT COUNT(*) FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND CONSTRAINT_NAME IN('chk_chat_v2_sequence','chk_chat_v2_thread_kind','chk_chat_v2_member_status','chk_chat_v2_member_role','chk_chat_v2_message_origin','chk_chat_v2_message_body'))=6;
            """;
        if(!Convert.ToBoolean(await invariants.ExecuteScalarAsync(token)))throw new InvalidOperationException("Contraintes chat v2 invalides.");
    }
}
