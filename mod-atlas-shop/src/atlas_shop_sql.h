#pragma once
#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>

namespace AtlasShop
{
inline bool Identifier(std::string_view value)
{
    if (value.empty() || value.size() > 64) return false;
    for (char c : value)
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')) return false;
    return true;
}

// Shared by the actual core worker and the MySQL integration-test SQL emitter.
// The entitlement and its receipt MUST be changed in one InnoDB statement.
inline std::string DeliverySql(std::string const& characters, std::uint32_t realm, std::uint64_t sequence,
    std::uint32_t account, std::uint32_t guid, std::string const& auth = "")
{
    if (!Identifier(characters) || (!auth.empty() && !Identifier(auth)) || !realm || !sequence || !account || !guid) throw std::invalid_argument("Invalid shop delivery identity");
    std::string orders = auth.empty() ? "atlas_shop_order" : "`" + auth + "`.atlas_shop_order";
    std::string predicate = "o.sequence_id=" + std::to_string(sequence) + " AND o.realm_id=" + std::to_string(realm)
        + " AND o.account_id=" + std::to_string(account) + " AND o.character_guid=" + std::to_string(guid)
        + " AND o.offer_id='character-rename' AND o.status='pending'";
    return "UPDATE " + orders + " o JOIN `" + characters + "`.characters c ON c.guid=o.character_guid "
        "SET c.at_login=c.at_login|1,o.status='delivered',o.updated_at=UTC_TIMESTAMP(6) WHERE " + predicate
        + " AND c.account=o.account_id AND c.deleteDate IS NULL AND c.online=0 AND (c.at_login&1)=0;\n"
        "UPDATE " + orders + " o LEFT JOIN `" + characters + "`.characters c ON c.guid=o.character_guid "
        "SET o.status='rejected',o.reason=IF(c.guid IS NULL OR c.account<>o.account_id OR c.deleteDate IS NOT NULL,'character-unavailable','rename-already-pending'),"
        "o.updated_at=UTC_TIMESTAMP(6) WHERE " + predicate
        + " AND (c.guid IS NULL OR c.account<>o.account_id OR c.deleteDate IS NOT NULL OR (c.online=0 AND (c.at_login&1)<>0));";
}
}
