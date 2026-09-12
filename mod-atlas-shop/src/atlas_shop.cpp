#include "Define.h"
#include "atlas_shop_sql.h"
#include "atlas_shop_native.h"
#include "AsyncCallbackProcessor.h"
#include "Config.h"
#include "DatabaseEnv.h"
#include "Log.h"
#include "ObjectAccessor.h"
#include "Opcodes.h"
#include "QueryCallback.h"
#include "QueryResult.h"
#include "ServerScript.h"
#include "SharedDefines.h"
#include "Transaction.h"
#include "WorldPacket.h"
#include "WorldScript.h"
#include "WorldSession.h"
#include "WorldSessionMgr.h"
#include <atomic>

namespace
{
class Delivery
{
public:
    static Delivery& Instance() { static Delivery instance; return instance; }
    void Configure()
    {
        _enabled = sConfigMgr->GetOption<bool>("AtlasShop.Enable", false)
            && !sConfigMgr->GetOption<bool>("AtlasShop.AccountServices", false);
        _realm = sConfigMgr->GetOption<uint32>("RealmID", 1);
        if (!_configuredOnce)
        {
            _singleCharacterWorker = sConfigMgr->GetOption<uint32>("CharacterDatabase.WorkerThreads", 1) == 1;
            _configuredOnce = true;
        }
        auto const* auth = LoginDatabase.GetConnectionInfo();
        auto const* chars = CharacterDatabase.GetConnectionInfo();
        _characters = chars->database;
        _auth = auth->database;
        _ready = false;
        _schemaChecked = false;
        if (!AtlasShop::Identifier(_characters) || !AtlasShop::Identifier(_auth) || !_realm || !_singleCharacterWorker
            || auth->host != chars->host || auth->port_or_socket != chars->port_or_socket)
        {
            if (_enabled) LOG_ERROR("module", "AtlasShop requires one character database worker, validated database names and auth/characters on the same MySQL instance.");
            _enabled = false;
        }
        // Never clear an in-flight account guard on a config reload.
    }
    bool Guarded(uint32 account) const { return account != 0 && _guard.load() == account; }
    void Stop() { _enabled = false; }
    void Update(uint32 diff)
    {
        _queries.ProcessReadyCallbacks();
        _transactions.ProcessReadyCallbacks();
        if (!_enabled) return;
        if (!_schemaChecked)
        {
            if (_schemaChecking) return;
            if (_poll > diff) { _poll -= diff; return; }
            _poll = 5000; _schemaChecking = true;
            _queries.AddCallback(LoginDatabase.AsyncQuery("SELECT COUNT(*) FROM information_schema.TABLES WHERE ENGINE='InnoDB' AND ((TABLE_SCHEMA='"
                + _auth + "' AND TABLE_NAME IN ('atlas_shop_order','atlas_shop_delivery_health')) OR (TABLE_SCHEMA='" + _characters
                + "' AND TABLE_NAME='characters')) AND (SELECT COUNT(*) FROM atlas_shop_order WHERE sequence_id=0 AND realm_id=0"
                " AND account_id=0 AND character_guid=0 AND status='pending')=0").WithCallback([this](QueryResult result)
            {
                _schemaChecking = false;
                _schemaChecked = result && result->Fetch()[0].Get<uint64>() == 3;
                if (!_schemaChecked) LOG_ERROR("module", "AtlasShop remains disabled until its order, heartbeat and character tables are all InnoDB.");
            }));
            return;
        }
        if (_heartbeat > diff) _heartbeat -= diff;
        else if (!_heartbeating)
        {
            _heartbeat = 5000; _heartbeating = true;
            // These no-op writes check required grants before advertising availability.
            // A missing schema or insufficient write grant rolls the heartbeat back.
            auto transaction = CharacterDatabase.BeginTransaction();
            transaction->Append("UPDATE `" + _auth + "`.atlas_shop_order SET status=status,reason=reason,updated_at=updated_at"
                " WHERE sequence_id=0 AND realm_id=0 AND account_id=0 AND character_guid=0 AND offer_id='character-rename'");
            transaction->Append("UPDATE `" + _characters + "`.characters SET at_login=at_login WHERE guid=0 AND account=0 AND online=0 AND deleteDate IS NULL");
            transaction->Append("INSERT INTO `" + _auth + "`.atlas_shop_delivery_health(realm_id,protocol,character_database,last_seen_at) VALUES("
                + std::to_string(_realm) + ",1,'" + _characters + "',UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE protocol=1,character_database=VALUES(character_database),last_seen_at=VALUES(last_seen_at)");
            _transactions.AddCallback(CharacterDatabase.AsyncCommitTransaction(transaction)).AfterComplete([this](bool success)
            { _ready = success; _heartbeating = false; });
        }
        if (_poll > diff) { _poll -= diff; return; }
        if (!_ready || _busy) return;
        _poll = 1000; _busy = true;
        _queries.AddCallback(LoginDatabase.AsyncQuery("SELECT sequence_id,account_id,character_guid FROM atlas_shop_order WHERE realm_id="
            + std::to_string(_realm) + " AND status='pending' AND sequence_id>" + std::to_string(_cursor) + " ORDER BY sequence_id LIMIT 16").WithCallback([this](QueryResult result)
        {
            _busy = false;
            if (!_enabled) return;
            if (!result) { _cursor = 0; return; }
            do
            {
                Field* row = result->Fetch();
                _cursor = row[0].Get<uint64>();
                uint32 account = row[1].Get<uint32>(), guid = row[2].Get<uint32>();
                ObjectGuid character = ObjectGuid::Create<HighGuid::Player>(guid);
                // An offline SQL flag alone does not exclude a character currently loading.
                // Wait until the account has completely left the realm, including offline sessions.
                if (sWorldSessionMgr->FindSession(account) || sWorldSessionMgr->FindOfflineSession(account)
                    || ObjectAccessor::FindConnectedPlayer(character) || sWorldSessionMgr->FindOfflineSessionForCharacterGUID(guid)) continue;
                _guard.store(account); _busy = true;
                // The one character DB worker preserves FIFO with all queued logout saves.
                auto transaction = CharacterDatabase.BeginTransaction();
                std::string sql = AtlasShop::DeliverySql(_characters, _realm, _cursor, account, guid, _auth);
                auto separator = sql.find(';');
                transaction->Append(sql.substr(0, separator));
                transaction->Append(sql.substr(separator + 1));
                _transactions.AddCallback(CharacterDatabase.AsyncCommitTransaction(transaction)).AfterComplete([this](bool success)
                {
                    // Release only after commit/rollback, including during config disable.
                    _guard.store(0); _busy = false;
                    if (!success) LOG_ERROR("module", "AtlasShop delivery transaction failed; the durable order will be retried.");
                });
                return;
            } while (result->NextRow());
        }));
    }
private:
    bool _enabled = false, _ready = false, _busy = false, _heartbeating = false;
    bool _configuredOnce = false, _singleCharacterWorker = false;
    bool _schemaChecked = false, _schemaChecking = false;
    uint32 _realm = 0, _heartbeat = 0, _poll = 0;
    uint64 _cursor = 0;
    std::string _characters, _auth;
    std::atomic<uint32> _guard{0};
    AsyncCallbackProcessor<QueryCallback> _queries;
    AsyncCallbackProcessor<TransactionCallback> _transactions;
};

class ShopWorld : public WorldScript
{
public:
    ShopWorld() : WorldScript("AtlasShopWorld", { WORLDHOOK_ON_AFTER_CONFIG_LOAD, WORLDHOOK_ON_UPDATE, WORLDHOOK_ON_SHUTDOWN }) { }
    void OnAfterConfigLoad(bool) override { Delivery::Instance().Configure(); }
    void OnUpdate(uint32 diff) override { Delivery::Instance().Update(diff); }
    void OnShutdown() override { Delivery::Instance().Stop(); }
};

class ShopPackets : public ServerScript
{
public:
    ShopPackets() : ServerScript("AtlasShopPackets", { SERVERHOOK_CAN_PACKET_RECEIVE }) { }
    // The pinned Atlas core dispatches its older const overload, while current
    // upstream headers expose only the mutable overload. Keep both entry points;
    // the const form intentionally omits override for upstream compatibility.
    bool CanPacketReceive(WorldSession* session, WorldPacket const& packet)
    {
        if (!session) return true;
        if (AtlasShop::IsNativePacket(packet))
        {
            WorldPacket copy(packet);
            return AtlasShop::NativeCanReceive(session, copy);
        }
        return AllowOrdinaryPacket(session, packet);
    }
    bool CanPacketReceive(WorldSession* session, WorldPacket& packet) override
    {
        return CanPacketReceive(session, static_cast<WorldPacket const&>(packet));
    }
private:
    bool AllowOrdinaryPacket(WorldSession* session, WorldPacket const& packet)
    {
        bool guarded = Delivery::Instance().Guarded(session->GetAccountId()) || AtlasShop::NativeGuarded(session->GetAccountId());
        bool namesPaused = AtlasShop::NativeNamesPaused();
        if (!guarded && !namesPaused) return true;
        uint16 response; uint8 error;
        switch (packet.GetOpcode())
        {
            case CMSG_PLAYER_LOGIN:
                if (!guarded) return true;
                response = SMSG_CHARACTER_LOGIN_FAILED; error = CHAR_LOGIN_FAILED; break;
            case CMSG_CHAR_CREATE: response = SMSG_CHAR_CREATE; error = CHAR_CREATE_ERROR; break;
            case CMSG_CHAR_RENAME: response = SMSG_CHAR_RENAME; error = CHAR_CREATE_ERROR; break;
            case CMSG_CHAR_DELETE:
                if (!guarded) return true;
                response = SMSG_CHAR_DELETE; error = CHAR_DELETE_FAILED; break;
            case CMSG_CHAR_CUSTOMIZE: response = SMSG_CHAR_CUSTOMIZE; error = CHAR_CREATE_ERROR; break;
            case CMSG_CHAR_FACTION_CHANGE:
            case CMSG_CHAR_RACE_CHANGE: response = SMSG_CHAR_FACTION_CHANGE; error = CHAR_CREATE_ERROR; break;
            default: return true;
        }
        WorldPacket reply(response, 1); reply << error; session->SendPacket(&reply);
        return false;
    }
};
}

void AddAtlasShopScripts() { new ShopWorld(); new ShopPackets(); AtlasShop::AddNativeScripts(); AtlasShop::AddGoldConversionScripts(); }
