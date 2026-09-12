#pragma once
#include "atlas_shop_sql.h"
#include <vector>

namespace AtlasShop
{
inline std::string Utf8Literal(std::string const& value)
{
    if (value.empty() || value.size() > 96) throw std::invalid_argument("Invalid name length");
    static constexpr char digits[] = "0123456789abcdef";
    std::string hex;
    for (unsigned char c : value) { hex += digits[c >> 4]; hex += digits[c & 15]; }
    return "CONVERT(0x" + hex + " USING utf8mb4)";
}

inline std::string NativeOrderPredicate(uint32_t realm, uint64_t sequence, uint32_t account)
{
    if (!realm || !sequence || !account) throw std::invalid_argument("Invalid account service identity");
    return "o.sequence_id=" + std::to_string(sequence) + " AND o.realm_id=" + std::to_string(realm)
        + " AND o.account_id=" + std::to_string(account) + " AND o.offer_id='character-rename'";
}

// A wallet no-op takes the same first row lock as purchase/refund. No wallet
// amount or ledger entry changes when an already-paid service is consumed.
inline std::vector<std::string> NativeConsumeSql(std::string const& characters, std::string const& auth,
    uint32_t realm, uint64_t sequence, uint32_t account, uint32_t guid,
    std::string const& oldName, std::string const& newName, uint32_t pendingFlags)
{
    if (!Identifier(characters) || !Identifier(auth) || !guid || !pendingFlags)
        throw std::invalid_argument("Invalid native service database identity");
    std::string orders = "`" + auth + "`.atlas_shop_order", chars = "`" + characters + "`.characters";
    std::string predicate = NativeOrderPredicate(realm, sequence, account);
    std::string oldValue = Utf8Literal(oldName), newValue = Utf8Literal(newName);
    std::string target = std::to_string(guid);
    return {
        "UPDATE `" + auth + "`.atlas_shop_wallet SET account_id=account_id WHERE account_id=" + std::to_string(account),
        "UPDATE " + orders + " o SET o.id=o.id WHERE " + predicate,
        // Both the character and receipt change in one statement. Capture the
        // old name as a literal: multi-table assignment order is unspecified.
        "UPDATE " + orders + " o JOIN " + chars + " c ON c.guid=" + target
            + " LEFT JOIN " + chars + " taken ON taken.name=" + newValue + " AND taken.guid<>c.guid"
            " SET o.status='consumed',o.character_guid=c.guid,o.character_name=" + oldValue
            + ",o.requested_name=" + newValue + ",o.redemption_key=o.id,o.updated_at=UTC_TIMESTAMP(6),c.name=" + newValue
            + " WHERE " + predicate + " AND o.status='available' AND o.character_guid=0"
            " AND c.account=o.account_id AND c.online=0 AND c.deleteDate IS NULL AND c.level>=10"
            " AND (c.at_login&" + std::to_string(pendingFlags) + ")=0 AND BINARY c.name=BINARY " + oldValue
            + " AND c.name<>" + newValue + " AND taken.guid IS NULL",
        "DELETE d FROM `" + characters + "`.character_declinedname d JOIN " + orders
            + " o ON o.character_guid=d.guid JOIN " + chars + " c ON c.guid=d.guid WHERE " + predicate
            + " AND o.status='consumed' AND o.character_guid=" + target + " AND BINARY o.character_name=BINARY " + oldValue
            + " AND BINARY o.requested_name=BINARY " + newValue + " AND BINARY c.name=BINARY " + newValue
    };
}
}
