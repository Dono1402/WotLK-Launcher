using MySqlConnector;

namespace WotLK.Launcher.Server.Database;

internal sealed partial class LauncherSchemaValidator
{
    internal Task ValidateShopFundingAsync(MySqlConnection connection, CancellationToken token) => ValidateAsync(connection,
        new Dictionary<string, TableExpectation>(StringComparer.Ordinal)
        {
            ["atlas_shop_wallet"] = Table(
                [C("account_id","int unsigned","NO"), C("euro_cents","bigint","NO","0"), C("credit_cents","bigint","NO","0"),
                 C("held_cents","bigint","NO","0"), C("debt_cents","bigint","NO","0"), FundingDate("updated_at")],
                [I("PRIMARY",0,1,"account_id")], [FundingForeignKey("fk_atlas_shop_wallet_profile","account_id","atlas_launcher_profile","account_id")],
                ["chk_atlas_shop_wallet_amounts"]),
            ["atlas_shop_top_up"] = Table(
                [C("sequence_id","bigint","NO",extra:"auto_increment"), FundingId("id"), C("account_id","int unsigned","NO"),
                 FundingId("idempotency_key"), C("amount_cents","bigint","NO"), C("status","varchar(10)","NO","pending",collation:"ascii_bin"),
                 C("version","bigint","NO","1"), C("paypal_transaction_id","varchar(64)","YES",collation:"ascii_bin"),
                 C("paypal_case_id","varchar(100)","YES",collation:"utf8mb4_0900_ai_ci"), C("held_cents","bigint","NO","0"),
                 FundingDate("created_at"), FundingDate("updated_at")],
                [I("PRIMARY",0,1,"sequence_id"), I("uq_atlas_shop_top_up_id",0,1,"id"),
                 I("uq_atlas_shop_top_up_key",0,1,"account_id"), I("uq_atlas_shop_top_up_key",0,2,"idempotency_key"),
                 I("uq_atlas_shop_paypal_transaction",0,1,"paypal_transaction_id"),
                 I("ix_atlas_shop_top_up_account",1,1,"account_id"), I("ix_atlas_shop_top_up_account",1,2,"sequence_id"),
                 I("ix_atlas_shop_top_up_status",1,1,"status"), I("ix_atlas_shop_top_up_status",1,2,"sequence_id"),
                 I("ix_atlas_shop_top_up_daily",1,1,"account_id"), I("ix_atlas_shop_top_up_daily",1,2,"created_at")],
                [FundingForeignKey("fk_atlas_shop_top_up_wallet","account_id","atlas_shop_wallet","account_id")],
                ["chk_atlas_shop_top_up_amount","chk_atlas_shop_top_up_state"]),
            ["atlas_shop_ledger"] = Table(
                [C("id","bigint","NO",extra:"auto_increment"), FundingId("request_id"), C("request_version","bigint","NO"),
                 C("account_id","int unsigned","NO"), C("actor_account_id","int unsigned","NO"), C("action","varchar(20)","NO",collation:"ascii_bin"),
                 C("amount_cents","bigint","NO","0"), C("euro_delta","bigint","NO","0"), C("held_delta","bigint","NO","0"),
                 C("debt_delta","bigint","NO","0"), C("euro_after","bigint","NO"), C("held_after","bigint","NO"), C("debt_after","bigint","NO"),
                 C("note","varchar(1000)","NO",collation:"utf8mb4_0900_ai_ci"), C("paypal_case_id","varchar(100)","YES",collation:"utf8mb4_0900_ai_ci"), FundingDate("created_at")],
                [I("PRIMARY",0,1,"id"), I("uq_atlas_shop_ledger_transition",0,1,"request_id"), I("uq_atlas_shop_ledger_transition",0,2,"request_version"),
                 I("ix_atlas_shop_ledger_account",1,1,"account_id"), I("ix_atlas_shop_ledger_account",1,2,"id"),
                 I("fk_atlas_shop_ledger_actor",1,1,"actor_account_id")],
                [FundingForeignKey("fk_atlas_shop_ledger_request","request_id","atlas_shop_top_up","id"),
                 FundingForeignKey("fk_atlas_shop_ledger_wallet","account_id","atlas_shop_wallet","account_id"),
                 FundingForeignKey("fk_atlas_shop_ledger_actor","actor_account_id","atlas_launcher_profile","account_id")],
                ["chk_atlas_shop_ledger_state"])
        }, token);

    private static string FundingId(string name) => C(name,"char(32)","NO",collation:"ascii_bin");
    private static string FundingDate(string name) => C(name,"datetime(6)","NO");
    private static string FundingForeignKey(string name, string column, string table, string reference)
        => $"{name}|1|{column}|{table}|{reference}|NO ACTION";
}
