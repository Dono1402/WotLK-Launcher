using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed partial class LauncherSchemaValidator
{
    internal async Task ValidateShopGoldConversionsAsync(MySqlConnection connection, CancellationToken token)
    {
        Dictionary<string, TableExpectation> tables = new(StringComparer.Ordinal)
        {
            ["atlas_shop_gold_conversion"] = Table(
                [C("sequence_id","bigint","NO",extra:"auto_increment"), FundingId("id"),
                 C("account_id","int unsigned","NO"), C("realm_id","int unsigned","NO"), C("character_guid","int unsigned","NO"),
                 C("character_name","varchar(24)","NO",collation:"utf8mb4_0900_ai_ci"), FundingId("idempotency_key"),
                 C("catalog_revision","varchar(80)","NO",collation:"ascii_bin"), C("offered_copper","int unsigned","NO"),
                 C("copper_per_cent","int unsigned","NO"), C("credit_cents","bigint","NO"),
                 C("status","varchar(10)","NO","pending",collation:"ascii_bin"), C("reason","varchar(40)","YES",collation:"ascii_bin"),
                 C("gold_before","int unsigned","YES"), C("gold_after","int unsigned","YES"),
                 C("credit_before","bigint","YES"), C("credit_after","bigint","YES"),
                 C("active_account","int unsigned","YES",extra:"STORED GENERATED"),
                 FundingDate("created_at"), FundingDate("updated_at"), FundingDate("expires_at")],
                [I("PRIMARY",0,1,"sequence_id"), I("uq_atlas_shop_conversion_id",0,1,"id"),
                 I("uq_atlas_shop_conversion_key",0,1,"account_id"), I("uq_atlas_shop_conversion_key",0,2,"idempotency_key"),
                 I("uq_atlas_shop_conversion_active",0,1,"active_account"),
                 I("ix_atlas_shop_conversion_worker",1,1,"realm_id"), I("ix_atlas_shop_conversion_worker",1,2,"status"),
                 I("ix_atlas_shop_conversion_worker",1,3,"sequence_id"),
                 I("ix_atlas_shop_conversion_account",1,1,"account_id"), I("ix_atlas_shop_conversion_account",1,2,"sequence_id"),
                 I("ix_atlas_shop_conversion_daily",1,1,"account_id"), I("ix_atlas_shop_conversion_daily",1,2,"created_at")],
                [FundingForeignKey("fk_atlas_shop_conversion_wallet","account_id","atlas_shop_wallet","account_id")],
                ["chk_atlas_shop_conversion_quote", "chk_atlas_shop_conversion_receipt", "chk_atlas_shop_conversion_reason"]),
            ["atlas_shop_conversion_health"] = Table(
                [C("realm_id","int unsigned","NO"), C("protocol","int unsigned","NO"),
                 C("character_database","varchar(64)","NO",collation:"ascii_bin"), C("copper_per_cent","int unsigned","NO"),
                 FundingDate("last_seen_at")], [I("PRIMARY",0,1,"realm_id")], [])
        };
        await ValidateAsync(connection, tables, token);
        await using MySqlCommand generated = new("""
            SELECT GENERATION_EXPRESSION FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='atlas_shop_gold_conversion' AND COLUMN_NAME='active_account';
            """, connection);
        string expression = (Convert.ToString(await generated.ExecuteScalarAsync(token)) ?? "")
            .Replace("`", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()
            .Replace("\\'", "'", StringComparison.Ordinal);
        expression = System.Text.RegularExpressions.Regex.Replace(expression, "_[a-z0-9]+(?=')", "");
        if (expression != "if((status='pending'),account_id,null)")
            throw new InvalidOperationException("Shop pending-conversion constraint has drifted: " + expression);
    }
}
