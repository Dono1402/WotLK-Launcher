using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed partial class LauncherSchemaValidator
{
    internal async Task ValidateShopOrdersAsync(MySqlConnection connection, CancellationToken token, bool accountServices = false)
    {
        Dictionary<string, TableExpectation> tables = new(StringComparer.Ordinal)
        {
            ["atlas_shop_order"] = Table(
                [C("sequence_id","bigint","NO",extra:"auto_increment"), FundingId("id"), C("account_id","int unsigned","NO"),
                 C("realm_id","int unsigned","NO"), C("character_guid","int unsigned","NO"), C("character_name","varchar(24)","NO",collation:"utf8mb4_0900_ai_ci"),
                 FundingId("idempotency_key"), C("offer_id","varchar(40)","NO",collation:"ascii_bin"), C("catalog_revision","varchar(80)","NO",collation:"ascii_bin"),
                 C("currency","varchar(7)","NO",collation:"ascii_bin"), C("amount_cents","bigint","NO"), C("status","varchar(10)","NO","pending",collation:"ascii_bin"),
                 C("reason","varchar(40)","YES",collation:"ascii_bin"), C("active_character","int unsigned","YES",extra:"STORED GENERATED"), FundingDate("created_at"), FundingDate("updated_at")],
                [I("PRIMARY",0,1,"sequence_id"), I("uq_atlas_shop_order_id",0,1,"id"),
                 I("uq_atlas_shop_order_key",0,1,"account_id"), I("uq_atlas_shop_order_key",0,2,"idempotency_key"),
                 I("uq_atlas_shop_order_active",0,1,"realm_id"), I("uq_atlas_shop_order_active",0,2,"active_character"),
                 I("ix_atlas_shop_order_worker",1,1,"realm_id"), I("ix_atlas_shop_order_worker",1,2,"status"), I("ix_atlas_shop_order_worker",1,3,"sequence_id"),
                 I("ix_atlas_shop_order_account",1,1,"account_id"), I("ix_atlas_shop_order_account",1,2,"sequence_id")],
                [FundingForeignKey("fk_atlas_shop_order_wallet","account_id","atlas_shop_wallet","account_id")], ["chk_atlas_shop_order_state"]),
            ["atlas_shop_order_ledger"] = Table(
                [C("id","bigint","NO",extra:"auto_increment"), FundingId("order_id"), C("account_id","int unsigned","NO"),
                 C("kind","varchar(8)","NO",collation:"ascii_bin"), C("currency","varchar(7)","NO",collation:"ascii_bin"), C("amount_cents","bigint","NO"),
                 C("euro_after","bigint","NO"), C("credit_after","bigint","NO"), C("held_after","bigint","NO"), C("debt_after","bigint","NO"), FundingDate("created_at")],
                [I("PRIMARY",0,1,"id"), I("uq_atlas_shop_order_event",0,1,"order_id"), I("uq_atlas_shop_order_event",0,2,"kind"),
                 I("ix_atlas_shop_order_ledger_account",1,1,"account_id"), I("ix_atlas_shop_order_ledger_account",1,2,"id")],
                [FundingForeignKey("fk_atlas_shop_order_event_order","order_id","atlas_shop_order","id"),
                 FundingForeignKey("fk_atlas_shop_order_event_wallet","account_id","atlas_shop_wallet","account_id")], ["chk_atlas_shop_order_event"]),
            ["atlas_shop_delivery_health"] = Table(
                [C("realm_id","int unsigned","NO"), C("protocol","int unsigned","NO"), C("character_database","varchar(64)","NO",collation:"ascii_bin"), FundingDate("last_seen_at")],
                [I("PRIMARY",0,1,"realm_id")], [])
        };
        if (accountServices)
        {
            TableExpectation orders = tables["atlas_shop_order"];
            tables["atlas_shop_order"] = orders with
            {
                Columns = [.. orders.Columns, C("redemption_key", "char(32)", "YES", collation: "ascii_bin"),
                    C("requested_name", "varchar(12)", "YES", collation: "utf8mb4_0900_ai_ci")],
                Indexes = [.. orders.Indexes, I("uq_atlas_shop_order_redemption", 0, 1, "account_id"),
                    I("uq_atlas_shop_order_redemption", 0, 2, "redemption_key")]
            };
        }
        await ValidateAsync(connection, tables, token);
        await using MySqlCommand generated = new("""
            SELECT GENERATION_EXPRESSION FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='atlas_shop_order' AND COLUMN_NAME='active_character';
            """, connection);
        string expression = (Convert.ToString(await generated.ExecuteScalarAsync(token)) ?? "").Replace("`", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
        expression = expression.Replace("\\'", "'", StringComparison.Ordinal);
        expression = System.Text.RegularExpressions.Regex.Replace(expression, "_[a-z0-9]+(?=')", "");
        if (expression != "if((status='pending'),character_guid,null)")
            throw new InvalidOperationException("Shop active-character constraint has drifted: " + expression);
    }
}
