using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed partial class LauncherSchemaValidator
{
    private static readonly IReadOnlyDictionary<string, TableExpectation> ChatV6Tables = CreateChatTables();

    internal Task ValidateChatAsync(MySqlConnection connection, CancellationToken cancellationToken) =>
        ValidateAsync(connection, ChatV6Tables, cancellationToken);

    private static IReadOnlyDictionary<string, TableExpectation> CreateChatTables() =>
        new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_launcher_chat_account"] = Table(
                [C("account_id", "int unsigned", "NO"), C("last_message_id", "bigint unsigned", "NO", "0"),
                 C("send_window_started_at", "datetime(6)", "YES"), C("send_window_count", "smallint unsigned", "NO", "0")],
                [I("PRIMARY", 0, 1, "account_id")],
                [F("fk_atlas_chat_account_profile", 1, "account_id", "atlas_launcher_profile", "account_id")]),
            ["atlas_launcher_chat_conversation"] = Table(
                [C("account_low_id", "int unsigned", "NO"), C("account_high_id", "int unsigned", "NO"),
                 C("last_message_id", "bigint unsigned", "NO", "0"), C("low_last_read_message_id", "bigint unsigned", "NO", "0"),
                 C("high_last_read_message_id", "bigint unsigned", "NO", "0"),
                 C("updated_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED")],
                [I("PRIMARY", 0, 1, "account_low_id"), I("PRIMARY", 0, 2, "account_high_id"),
                 I("ix_atlas_chat_conversation_high", 1, 1, "account_high_id"), I("ix_atlas_chat_conversation_high", 1, 2, "last_message_id"),
                 I("ix_atlas_chat_conversation_low", 1, 1, "account_low_id"), I("ix_atlas_chat_conversation_low", 1, 2, "last_message_id")],
                [F("fk_atlas_chat_conversation_low", 1, "account_low_id", "atlas_launcher_profile", "account_id"),
                 F("fk_atlas_chat_conversation_high", 1, "account_high_id", "atlas_launcher_profile", "account_id")],
                ["chk_atlas_chat_conversation_pair"]),
            ["atlas_launcher_chat_message"] = Table(
                [C("id", "bigint unsigned", "NO", extra: "auto_increment"), C("client_message_id", "binary(16)", "NO"),
                 C("account_low_id", "int unsigned", "NO"), C("account_high_id", "int unsigned", "NO"),
                 C("sender_account_id", "int unsigned", "NO"), C("recipient_account_id", "int unsigned", "NO"),
                 C("sender_username", "varchar(32)", "NO", collation: "utf8mb4_0900_ai_ci"),
                 C("body", "varchar(1000)", "NO", collation: "utf8mb4_0900_ai_ci"), C("origin", "tinyint unsigned", "NO"),
                 C("created_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED")],
                [I("PRIMARY", 0, 1, "id"),
                 I("uq_atlas_chat_sender_request", 0, 1, "sender_account_id"), I("uq_atlas_chat_sender_request", 0, 2, "client_message_id"),
                 I("uq_atlas_chat_message_recipient", 0, 1, "id"), I("uq_atlas_chat_message_recipient", 0, 2, "recipient_account_id"),
                 I("ix_atlas_chat_message_conversation", 1, 1, "account_low_id"), I("ix_atlas_chat_message_conversation", 1, 2, "account_high_id"),
                 I("ix_atlas_chat_message_conversation", 1, 3, "id"),
                 I("ix_atlas_chat_message_recipient", 1, 1, "recipient_account_id"), I("ix_atlas_chat_message_recipient", 1, 2, "id"),
                 I("ix_atlas_chat_message_sender", 1, 1, "sender_account_id"), I("ix_atlas_chat_message_sender", 1, 2, "id")],
                [F("fk_atlas_chat_message_conversation", 1, "account_low_id", "atlas_launcher_chat_conversation", "account_low_id"),
                 F("fk_atlas_chat_message_conversation", 2, "account_high_id", "atlas_launcher_chat_conversation", "account_high_id"),
                 F("fk_atlas_chat_message_sender", 1, "sender_account_id", "atlas_launcher_profile", "account_id"),
                 F("fk_atlas_chat_message_recipient", 1, "recipient_account_id", "atlas_launcher_profile", "account_id")],
                ["chk_atlas_chat_message_pair", "chk_atlas_chat_message_origin", "chk_atlas_chat_message_body"]),
            ["atlas_launcher_chat_outbox"] = Table(
                [C("message_id", "bigint unsigned", "NO"), C("recipient_account_id", "int unsigned", "NO"),
                 C("realm_id", "int unsigned", "NO", "0"), C("status", "tinyint unsigned", "NO", "0"),
                 C("lease_token", "binary(16)", "YES"), C("lease_until", "datetime(6)", "YES"),
                 C("attempts", "smallint unsigned", "NO", "0"), C("last_error", "varchar(64)", "YES", collation: "ascii_bin"),
                 C("created_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED"),
                 C("expires_at", "datetime(6)", "NO"), C("delivered_at", "datetime(6)", "YES"),
                 C("delivered_character_guid", "int unsigned", "YES")],
                [I("PRIMARY", 0, 1, "message_id"),
                 I("ix_atlas_chat_outbox_pending", 1, 1, "status"), I("ix_atlas_chat_outbox_pending", 1, 2, "realm_id"),
                 I("ix_atlas_chat_outbox_pending", 1, 3, "message_id"), I("ix_atlas_chat_outbox_lease", 1, 1, "lease_token"),
                 I("ix_atlas_chat_outbox_recipient", 1, 1, "message_id"), I("ix_atlas_chat_outbox_recipient", 1, 2, "recipient_account_id")],
                [F("fk_atlas_chat_outbox_message", 1, "message_id", "atlas_launcher_chat_message", "id"),
                 F("fk_atlas_chat_outbox_message", 2, "recipient_account_id", "atlas_launcher_chat_message", "recipient_account_id")],
                ["chk_atlas_chat_outbox_status"]),
            ["atlas_launcher_chat_inbox"] = Table(
                [C("id", "bigint unsigned", "NO", extra: "auto_increment"), C("request_id", "binary(16)", "NO"),
                 C("realm_id", "int unsigned", "NO"), C("sender_account_id", "int unsigned", "NO"),
                 C("sender_character_guid", "int unsigned", "NO"), C("recipient_account_id", "int unsigned", "NO"),
                 C("body", "varchar(1000)", "NO", collation: "utf8mb4_0900_ai_ci"), C("status", "tinyint unsigned", "NO", "0"),
                 C("message_id", "bigint unsigned", "YES"), C("error_code", "varchar(64)", "YES", collation: "ascii_bin"),
                 C("created_at", "datetime(6)", "NO", "CURRENT_TIMESTAMP(6)", "DEFAULT_GENERATED"), C("processed_at", "datetime(6)", "YES")],
                [I("PRIMARY", 0, 1, "id"), I("uq_atlas_chat_inbox_request", 0, 1, "request_id"),
                 I("ix_atlas_chat_inbox_pending", 1, 1, "status"), I("ix_atlas_chat_inbox_pending", 1, 2, "id"),
                 I("ix_atlas_chat_inbox_sender", 1, 1, "sender_account_id"), I("ix_atlas_chat_inbox_sender", 1, 2, "id"),
                 I("ix_atlas_chat_inbox_recipient", 1, 1, "recipient_account_id")],
                [F("fk_atlas_chat_inbox_sender", 1, "sender_account_id", "atlas_launcher_profile", "account_id"),
                 F("fk_atlas_chat_inbox_recipient", 1, "recipient_account_id", "atlas_launcher_profile", "account_id")],
                ["chk_atlas_chat_inbox_status", "chk_atlas_chat_inbox_body", "chk_atlas_chat_inbox_identity"])
        };
}
