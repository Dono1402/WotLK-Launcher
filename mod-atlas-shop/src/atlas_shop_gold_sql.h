#pragma once
#include "atlas_shop_sql.h"
#include <vector>

namespace AtlasShop
{
constexpr uint32_t GoldCopperPerCent = 10000;

inline std::string GoldRequestPredicate(uint32_t realm, uint64_t sequence, uint32_t account, uint32_t guid)
{
    if (!realm || !sequence || !account || !guid) throw std::invalid_argument("Invalid gold conversion identity");
    return "o.sequence_id=" + std::to_string(sequence) + " AND o.realm_id=" + std::to_string(realm)
        + " AND o.account_id=" + std::to_string(account) + " AND o.character_guid=" + std::to_string(guid);
}

inline std::vector<std::string> GoldConversionSql(std::string const& characters, std::string const& auth,
    uint32_t realm, uint64_t sequence, uint32_t account, uint32_t guid, std::string const& reject = "")
{
    if (!Identifier(characters) || !Identifier(auth)
        || (!reject.empty() && reject != "character-online" && reject != "request-expired"))
        throw std::invalid_argument("Invalid gold conversion database or rejection");
    std::string wallet = "`" + auth + "`.atlas_shop_wallet";
    std::string orders = "`" + auth + "`.atlas_shop_gold_conversion";
    std::string chars = "`" + characters + "`.characters";
    std::string predicate = GoldRequestPredicate(realm, sequence, account, guid);
    std::vector<std::string> sql{
        // Same wallet -> request lock order as the API and other shop operations.
        "UPDATE " + wallet + " SET credit_cents=credit_cents WHERE account_id=" + std::to_string(account),
        "UPDATE " + orders + " o SET o.status=o.status WHERE " + predicate
    };
    if (!reject.empty())
    {
        sql.push_back("UPDATE " + orders + " o SET o.status='rejected',o.reason='" + reject
            + "',o.updated_at=UTC_TIMESTAMP(6) WHERE " + predicate + " AND o.status='pending'");
        return sql;
    }
    // Every statement runs on the same transaction connection. Reset ALL scratch
    // variables on every attempt, including a database-worker deadlock retry.
    sql.push_back("SET @atlas_gold_before=NULL,@atlas_credit_before=NULL,@atlas_gold_amount=0,"
        "@atlas_gold_credit=0,@atlas_gold_reason=NULL,@atlas_gold_name=NULL");
    sql.push_back("SELECT c.money,w.credit_cents,o.offered_copper,o.credit_cents,c.name,"
        "CASE WHEN o.expires_at<=UTC_TIMESTAMP(6) THEN 'request-expired'"
        " WHEN c.guid IS NULL OR c.account<>o.account_id OR c.deleteDate IS NOT NULL THEN 'character-unavailable'"
        " WHEN c.online<>0 THEN 'character-online'"
        " WHEN o.copper_per_cent<>10000 OR o.credit_cents<>o.offered_copper DIV 10000 THEN 'rate-changed'"
        " WHEN c.money<o.offered_copper THEN 'insufficient-gold'"
        " WHEN w.debt_cents<>0 THEN 'wallet-debt'"
        " WHEN w.credit_cents>1000000000-o.credit_cents-(SELECT COALESCE(SUM(r.amount_cents),0) FROM `" + auth
        + "`.atlas_shop_order r WHERE r.account_id=o.account_id AND r.currency='credits' AND r.status IN ('available','pending','rejected'))"
        " THEN 'credit-limit' ELSE NULL END"
        " INTO @atlas_gold_before,@atlas_credit_before,@atlas_gold_amount,@atlas_gold_credit,@atlas_gold_name,@atlas_gold_reason"
        " FROM " + orders + " o JOIN " + wallet + " w ON w.account_id=o.account_id LEFT JOIN " + chars
        + " c ON c.guid=o.character_guid WHERE " + predicate + " AND o.status='pending' FOR UPDATE");
    // Capture the locked old amounts first instead of depending on MySQL's
    // unspecified assignment order in a multi-table UPDATE.
    sql.push_back("UPDATE " + chars + " SET money=@atlas_gold_before-@atlas_gold_amount WHERE guid="
        + std::to_string(guid) + " AND account=" + std::to_string(account)
        + " AND @atlas_gold_amount>0 AND @atlas_gold_reason IS NULL");
    sql.push_back("UPDATE " + wallet + " SET credit_cents=@atlas_credit_before+@atlas_gold_credit,updated_at=UTC_TIMESTAMP(6)"
        " WHERE account_id=" + std::to_string(account) + " AND @atlas_gold_amount>0 AND @atlas_gold_reason IS NULL");
    sql.push_back("UPDATE " + orders + " o SET o.status='completed',o.character_name=@atlas_gold_name,"
        "o.gold_before=@atlas_gold_before,o.gold_after=@atlas_gold_before-@atlas_gold_amount,"
        "o.credit_before=@atlas_credit_before,o.credit_after=@atlas_credit_before+@atlas_gold_credit,o.updated_at=UTC_TIMESTAMP(6)"
        " WHERE " + predicate + " AND o.status='pending' AND @atlas_gold_amount>0 AND @atlas_gold_reason IS NULL");
    sql.push_back("UPDATE " + orders + " o SET o.status='rejected',o.reason=@atlas_gold_reason,o.updated_at=UTC_TIMESTAMP(6)"
        " WHERE " + predicate + " AND o.status='pending' AND @atlas_gold_reason IS NOT NULL");
    return sql;
}
}
