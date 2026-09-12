#include "atlas_shop_native.h"
#include "atlas_shop_gold_sql.h"
#include "AsyncCallbackProcessor.h"
#include "Config.h"
#include "DatabaseEnv.h"
#include "Log.h"
#include "ObjectAccessor.h"
#include "QueryCallback.h"
#include "QueryResult.h"
#include "Transaction.h"
#include "WorldScript.h"
#include "WorldSession.h"
#include "WorldSessionMgr.h"

namespace AtlasShop
{
namespace
{
class GoldConversions
{
public:
    static GoldConversions& Instance() { static GoldConversions instance; return instance; }
    void Configure()
    {
        _enabled = sConfigMgr->GetOption<bool>("AtlasShop.Enable", false)
            && sConfigMgr->GetOption<bool>("AtlasShop.GoldConversion", false);
        _ready = false; _schemaChecked = false;
        _realm = sConfigMgr->GetOption<uint32>("RealmID", 1);
        auto const* auth = LoginDatabase.GetConnectionInfo();
        auto const* characters = CharacterDatabase.GetConnectionInfo();
        _auth = auth->database; _characters = characters->database;
        if (!Identifier(_auth) || !Identifier(_characters) || !_realm
            || auth->host != characters->host || auth->port_or_socket != characters->port_or_socket
            || StorageHooksVersion() != 2 || CharacterHooksVersion() != 2 || SessionHooksVersion() != 3)
        {
            if (_enabled) LOG_ERROR("module", "AtlasShop gold conversion requires the verified core hooks and one MySQL instance.");
            _enabled = false;
        }
        // A reload never releases the guard of an outstanding transaction.
    }
    void Stop() { _enabled = false; _ready = false; }
    void Update(uint32 diff)
    {
        _queries.ProcessReadyCallbacks();
        _transactions.ProcessReadyCallbacks();
        if (!_enabled) return;
        if (_heartbeat > diff) _heartbeat -= diff;
        else if (!_heartbeating)
        {
            _heartbeat = 5000;
            if (!_schemaChecked) CheckSchema(); else Heartbeat();
        }
        if (_poll > diff) { _poll -= diff; return; }
        if (!_ready || _busy) return;
        _poll = 500; _busy = true;
        _queries.AddCallback(LoginDatabase.AsyncQuery("SELECT sequence_id,account_id,character_guid,expires_at<=UTC_TIMESTAMP(6)"
            " FROM atlas_shop_gold_conversion WHERE realm_id=" + std::to_string(_realm)
            + " AND status='pending' AND sequence_id>" + std::to_string(_cursor)
            + " ORDER BY sequence_id LIMIT 16").WithCallback([this](QueryResult result)
        {
            _busy = false;
            if (!_enabled || !_ready) return;
            if (!result) { _cursor = 0; return; }
            do
            {
                auto* row = result->Fetch();
                _cursor = row[0].Get<uint64>();
                uint32 account = row[1].Get<uint32>(), guid = row[2].Get<uint32>();
                if (!account || !guid) continue;
                if (row[3].Get<uint8>()) { Commit(account, guid, "request-expired"); return; }
                auto* session = sWorldSessionMgr->FindSession(account);
                if ((session && (session->GetPlayer() || session->PlayerLoading() || session->PlayerLogout()))
                    || sWorldSessionMgr->FindOfflineSession(account)
                    || ObjectAccessor::FindConnectedPlayer(ObjectGuid::Create<HighGuid::Player>(guid))
                    || sWorldSessionMgr->FindOfflineSessionForCharacterGUID(guid))
                { Commit(account, guid, "character-online"); return; }
                // The shared barrier drains this character's previous saves, then
                // excludes login/deletion/name changes until COMMIT or ROLLBACK.
                if (!TryAcquireCharacterWriteGuard(account, guid)) continue;
                _guardHeld = true;
                Commit(account, guid);
                return;
            } while (result->NextRow());
        }));
    }
private:
    void Commit(uint32 account, uint32 guid, std::string const& rejection = "")
    {
        _busy = true;
        auto transaction = CharacterDatabase.BeginTransaction();
        for (auto const& sql : GoldConversionSql(_characters, _auth, _realm, _cursor, account, guid, rejection))
            transaction->Append(sql);
        _transactions.AddCallback(CharacterDatabase.AsyncCommitTransaction(transaction)).AfterComplete([this](bool success)
        {
            // There is no in-memory Player money to update: the worker only accepts
            // an unloaded character. Its next login reads the committed SQL balance.
            if (_guardHeld) { ReleaseCharacterWriteGuard(); _guardHeld = false; }
            _busy = false;
            if (!success) LOG_ERROR("module", "AtlasShop gold conversion transaction failed; its durable request will be retried.");
        });
    }
    void CheckSchema()
    {
        _heartbeating = true;
        _queries.AddCallback(LoginDatabase.AsyncQuery("SELECT COUNT(*) FROM information_schema.TABLES WHERE ENGINE='InnoDB'"
            " AND ((TABLE_SCHEMA='" + _auth + "' AND TABLE_NAME IN ('atlas_shop_wallet','atlas_shop_order','atlas_shop_gold_conversion','atlas_shop_conversion_health'))"
            " OR (TABLE_SCHEMA='" + _characters + "' AND TABLE_NAME='characters'))").WithCallback([this](QueryResult result)
        {
            _heartbeating = false;
            _schemaChecked = result && result->Fetch()[0].Get<uint64>() == 5;
            if (!_schemaChecked) LOG_ERROR("module", "AtlasShop gold conversion requires schema 0014 and five InnoDB tables.");
        }));
    }
    void Heartbeat()
    {
        _heartbeating = true;
        auto transaction = CharacterDatabase.BeginTransaction();
        transaction->Append("UPDATE `" + _auth + "`.atlas_shop_wallet SET credit_cents=credit_cents,updated_at=updated_at WHERE account_id=0");
        transaction->Append("UPDATE `" + _auth + "`.atlas_shop_gold_conversion SET status=status,reason=reason,gold_before=gold_before,"
            "gold_after=gold_after,credit_before=credit_before,credit_after=credit_after,character_name=character_name,updated_at=updated_at WHERE sequence_id=0");
        transaction->Append("UPDATE `" + _characters + "`.characters SET money=money WHERE guid=0");
        transaction->Append("INSERT INTO `" + _auth + "`.atlas_shop_conversion_health(realm_id,protocol,character_database,copper_per_cent,last_seen_at) VALUES("
            + std::to_string(_realm) + ",1,'" + _characters + "',10000,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE protocol=1,"
            "character_database=VALUES(character_database),copper_per_cent=VALUES(copper_per_cent),last_seen_at=VALUES(last_seen_at)");
        _transactions.AddCallback(CharacterDatabase.AsyncCommitTransaction(transaction)).AfterComplete([this](bool success)
        {
            _heartbeating = false; _ready = success && _enabled;
            if (!success) LOG_ERROR("module", "AtlasShop gold conversion heartbeat failed; conversions remain closed.");
        });
    }
    bool _enabled = false, _ready = false, _schemaChecked = false, _heartbeating = false, _busy = false, _guardHeld = false;
    uint32 _realm = 0, _poll = 0, _heartbeat = 0;
    uint64 _cursor = 0;
    std::string _auth, _characters;
    AsyncCallbackProcessor<QueryCallback> _queries;
    AsyncCallbackProcessor<TransactionCallback> _transactions;
};

class GoldWorld : public WorldScript
{
public:
    GoldWorld() : WorldScript("AtlasShopGold", { WORLDHOOK_ON_AFTER_CONFIG_LOAD, WORLDHOOK_ON_UPDATE, WORLDHOOK_ON_SHUTDOWN }) { }
    void OnAfterConfigLoad(bool) override { GoldConversions::Instance().Configure(); }
    void OnUpdate(uint32 diff) override { GoldConversions::Instance().Update(diff); }
    void OnShutdown() override { GoldConversions::Instance().Stop(); }
};
}
void AddGoldConversionScripts() { new GoldWorld(); }
}
