#include "atlas_shop_native.h"
#include "atlas_shop_barrier.h"
#include "atlas_shop_native_sql.h"
#include "AsyncCallbackProcessor.h"
#include "CharacterCache.h"
#include "Config.h"
#include "DatabaseEnv.h"
#include "Log.h"
#include "ObjectAccessor.h"
#include "ObjectMgr.h"
#include "Opcodes.h"
#include "Player.h"
#include "QueryCallback.h"
#include "QueryResult.h"
#include "SharedDefines.h"
#include "Transaction.h"
#include "WorldPacket.h"
#include "WorldScript.h"
#include "WorldSession.h"
#include "WorldSessionMgr.h"
#include <array>
#include <chrono>
#include <cstring>
#include <optional>
#include <unordered_map>

namespace AtlasShop
{
namespace
{
constexpr std::array<uint8, 6> Magic{ 0xff, 'A', 'T', 'L', 'S', 2 };
constexpr uint32 PendingFlags = AT_LOGIN_RENAME | AT_LOGIN_CUSTOMIZE | AT_LOGIN_CHANGE_FACTION | AT_LOGIN_CHANGE_RACE;
WriteBarrier& Barrier() { static WriteBarrier instance; return instance; }
uint64 Now()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

struct Connection
{
    uint32 Account = 0;
    WorldSession* Session = nullptr;
    uint64 Generation = 0, Nonce = 0;
};
struct Subscription
{
    Connection Owner;
    uint32 Request = 0, Available = 0;
    uint64 LastRequest = 0;
    bool Querying = false, Known = false;
    uint64 Revision = 0;
};
struct RequestWindow { WorldSession* Session = nullptr; uint64 Started = 0; uint32 Count = 0; };
struct Assignment
{
    Connection Owner;
    uint32 Request = 0, Token = 0, Guid = 0;
    uint64 Distribution = 0, Deadline = 0;
    std::string Name, OldName;
    bool Validate = false, Locked = false, Querying = false, Receipt = false;
};

// All protocol callbacks run on the world thread (CMSG_CHAR_ENUM is
// PROCESS_THREADUNSAFE). Save tracking alone is shared with other threads.
class NativeServices
{
public:
    static NativeServices& Instance() { static NativeServices instance; return instance; }
    void Configure()
    {
        _enabled = sConfigMgr->GetOption<bool>("AtlasShop.Enable", false)
            && sConfigMgr->GetOption<bool>("AtlasShop.AccountServices", false);
        _ready = false;
        _schemaChecked = false;
        _realm = sConfigMgr->GetOption<uint32>("RealmID", 1);
        auto const* auth = LoginDatabase.GetConnectionInfo();
        auto const* chars = CharacterDatabase.GetConnectionInfo();
        _auth = auth->database; _characters = chars->database;
        if (!Identifier(_auth) || !Identifier(_characters) || !_realm
            || auth->host != chars->host || auth->port_or_socket != chars->port_or_socket
            || StorageHooksVersion() != 2 || CharacterHooksVersion() != 2 || SessionHooksVersion() != 3)
        {
            if (_enabled) LOG_ERROR("module", "AtlasShop account services require the verified core hooks and one MySQL instance.");
            _enabled = false;
        }
    }
    void Stop() { _enabled = false; _ready = false; }
    void Forget(WorldSession* session)
    {
        auto found = _subscriptions.find(session->GetAccountId());
        if (found != _subscriptions.end() && found->second.Owner.Session == session)
            _subscriptions.erase(found);
        auto rate = _requestWindows.find(session->GetAccountId());
        if (rate != _requestWindows.end() && rate->second.Session == session) _requestWindows.erase(rate);
        // An already-confirmed transaction continues even if its socket closes.
        // Its durable receipt handles a retry from a later session.
    }
    bool Allow(WorldSession* session)
    {
        auto& window = _requestWindows[session->GetAccountId()];
        uint64 now = Now();
        if (window.Session != session || now - window.Started >= 10000)
            window = { session, now, 0 };
        // Native validation/confirmation are distinct from a character-list
        // refresh. Retain a bounded per-session budget; nonce changes do not reset it.
        return ++window.Count <= 20;
    }
    bool Receive(WorldSession* session, WorldPacket& packet)
    {
        if (!IsNativePacket(packet)) return true;
        // Reserved extension: never let malformed requests reach vanilla enum.
        if (packet.size() < 19 || packet.size() > 90 || packet.contents()[5] != Magic[5]) return false;
        packet.rpos(6);
        uint8 kind; uint32 request; uint64 nonce;
        packet >> kind >> request >> nonce;
        if (!nonce || (kind != 1 && kind != 2)) return false;
        auto& subscription = _subscriptions[session->GetAccountId()];
        if (subscription.Owner.Session != session || subscription.Owner.Nonce != nonce)
        {
            subscription = {};
            subscription.Owner = { session->GetAccountId(), session, ++_generation, nonce };
        }
        Connection owner = subscription.Owner;
        if (kind == 1)
        {
            if (packet.rpos() != packet.size()) return false;
            subscription.Request = request;
            if (!_enabled || !_ready) { SendList(owner, request, nullptr, 1); return false; }
            // Polling the launcher never translates into unbounded SQL work.
            if (!subscription.Querying && Now() - subscription.LastRequest >= 500)
                Refresh(owner.Account);
            return false;
        }
        if (packet.size() < 37) return false;
        Assignment assignment;
        assignment.Owner = owner; assignment.Request = request;
        uint8 validate, length;
        packet >> assignment.Distribution >> assignment.Guid >> assignment.Token >> validate >> length;
        if (!assignment.Distribution || !assignment.Guid || validate > 1 || !length || length > 48
            || packet.size() - packet.rpos() != length) return false;
        assignment.Name.assign(reinterpret_cast<char const*>(packet.contents() + packet.rpos()), length);
        assignment.Validate = validate != 0;
        if (assignment.Name.find('\0') != std::string::npos || !_enabled || !_ready || _job
            || !AtSelection(owner) || !normalizePlayerName(assignment.Name)
            || ObjectMgr::CheckPlayerName(assignment.Name, true) != CHAR_NAME_SUCCESS)
        {
            Reply(assignment, false);
            return false;
        }
        assignment.Deadline = Now() + 5000;
        _job = std::move(assignment);
        return false;
    }
    void Update(uint32 diff)
    {
        _queries.ProcessReadyCallbacks();
        _transactions.ProcessReadyCallbacks();
        if (_job) ProcessJob();
        if (_prune > diff) _prune -= diff;
        else { _prune = 5000; Barrier().PruneExpired(); }
        if (!_enabled) return;
        if (_heartbeat > diff) _heartbeat -= diff;
        else if (!_heartbeating)
        {
            _heartbeat = 5000;
            if (!_receiptUnavailable)
            {
                if (!_schemaChecked) CheckSchema(); else Heartbeat();
            }
        }
        if (_refresh > diff) { _refresh -= diff; return; }
        _refresh = 5000;
        if (!_ready) return;
        for (auto const& entry : _subscriptions)
            if (!entry.second.Querying) Refresh(entry.first);
    }
private:
    WorldSession* Session(Connection const& owner) const
    {
        auto found = _subscriptions.find(owner.Account);
        if (found == _subscriptions.end() || found->second.Owner.Generation != owner.Generation
            || found->second.Owner.Session != owner.Session || found->second.Owner.Nonce != owner.Nonce)
            return nullptr;
        WorldSession* current = sWorldSessionMgr->FindSession(owner.Account);
        return current == owner.Session ? current : nullptr;
    }
    bool AtSelection(Connection const& owner) const
    {
        auto* session = Session(owner);
        return session && !session->GetPlayer() && !session->PlayerLoading() && !session->PlayerLogout()
            && !sWorldSessionMgr->FindOfflineSession(owner.Account);
    }
    WorldPacket Header(uint8 kind, Connection const& owner, uint32 request, bool success) const
    {
        WorldPacket response(SMSG_CHAR_ENUM, 64);
        response.append(Magic.data(), Magic.size());
        response << kind << request << owner.Nonce << uint8(success ? 0 : 1);
        return response;
    }
    void Reply(Assignment const& job, bool success)
    {
        if (auto* session = Session(job.Owner))
        {
            auto response = Header(2, job.Owner, job.Request, success);
            response << job.Distribution << job.Guid << job.Token << uint8(job.Validate) << uint8(job.Name.size());
            response.append(job.Name.data(), job.Name.size());
            session->SendPacket(&response);
        }
    }
    void Finish(bool success)
    {
        auto found = _subscriptions.find(_job->Owner.Account);
        if (found != _subscriptions.end() && !_job->Validate && success) ++found->second.Revision;
        Reply(*_job, success);
        if (_job->Locked) Barrier().Release();
        _job.reset();
        _receiptUnavailable = false;
    }
    void SendList(Connection const& owner, uint32 request, QueryResult result, uint8 error = 0)
    {
        auto* session = Session(owner);
        if (!session) return;
        auto response = Header(1, owner, request, error == 0);
        uint32 count = !error && result ? uint32(result->GetRowCount()) : 0;
        if (count > 100) { count = 0; response = Header(1, owner, request, false); }
        response << count;
        if (count) do { response << result->Fetch()[0].Get<uint64>(); } while (result->NextRow());
        session->SendPacket(&response);
        auto& sub = _subscriptions.at(owner.Account);
        if (!error && sub.Known && count > sub.Available && session->GetPlayer())
        {
            WorldPacket notice(SMSG_NOTIFICATION, 140);
            notice << "Un service de changement de nom est disponible. Revenez a la selection des personnages pour l'utiliser.";
            session->SendPacket(&notice);
        }
        sub.Known = true; sub.Available = count;
    }
    void Refresh(uint32 account)
    {
        auto& sub = _subscriptions.at(account);
        sub.Querying = true; sub.LastRequest = Now();
        Connection owner = sub.Owner;
        uint32 request = sub.Request;
        uint64 revision = sub.Revision;
        _queries.AddCallback(LoginDatabase.AsyncQuery("SELECT sequence_id FROM atlas_shop_order WHERE account_id="
            + std::to_string(account) + " AND realm_id=" + std::to_string(_realm)
            + " AND offer_id='character-rename' AND status='available' ORDER BY sequence_id LIMIT 101").WithCallback(
            [this, owner, request, revision](QueryResult result)
            {
                if (!Session(owner)) return;
                _subscriptions.at(owner.Account).Querying = false;
                if (_subscriptions.at(owner.Account).Revision != revision) return;
                SendList(owner, request, result, _enabled && _ready ? 0 : 1);
            }));
    }
    void CheckSchema()
    {
        _heartbeating = true;
        _queries.AddCallback(LoginDatabase.AsyncQuery("SELECT COUNT(*) FROM information_schema.TABLES WHERE ENGINE='InnoDB'"
            " AND ((TABLE_SCHEMA='" + _auth + "' AND TABLE_NAME IN ('atlas_shop_wallet','atlas_shop_order','atlas_shop_delivery_health'))"
            " OR (TABLE_SCHEMA='" + _characters + "' AND TABLE_NAME IN ('characters','character_declinedname')))"
            " AND (SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='" + _auth
            + "' AND TABLE_NAME='atlas_shop_order' AND COLUMN_NAME IN ('redemption_key','requested_name'))=2").WithCallback(
            [this](QueryResult result)
            {
                _heartbeating = false;
                _schemaChecked = result && result->Fetch()[0].Get<uint64>() == 5;
                if (!_schemaChecked) LOG_ERROR("module", "AtlasShop native services require schema 0013 and five InnoDB tables.");
            }));
    }
    void Heartbeat()
    {
        _heartbeating = true;
        auto transaction = CharacterDatabase.BeginTransaction();
        transaction->Append("UPDATE `" + _auth + "`.atlas_shop_wallet SET account_id=account_id WHERE account_id=0");
        transaction->Append("UPDATE `" + _auth + "`.atlas_shop_order SET status=status,character_guid=character_guid,character_name=character_name,"
            "requested_name=requested_name,redemption_key=redemption_key,updated_at=updated_at WHERE sequence_id=0");
        transaction->Append("UPDATE `" + _characters + "`.characters SET name=name WHERE guid=0");
        transaction->Append("DELETE FROM `" + _characters + "`.character_declinedname WHERE guid=0");
        transaction->Append("INSERT INTO `" + _auth + "`.atlas_shop_delivery_health(realm_id,protocol,character_database,last_seen_at) VALUES("
            + std::to_string(_realm) + ",2,'" + _characters + "',UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE protocol=2,"
            "character_database=VALUES(character_database),last_seen_at=VALUES(last_seen_at)");
        _transactions.AddCallback(CharacterDatabase.AsyncCommitTransaction(transaction)).AfterComplete([this](bool success)
        {
            _heartbeating = false;
            _ready = success && _enabled;
            if (!success) LOG_ERROR("module", "AtlasShop native heartbeat failed; purchases remain closed.");
        });
    }
    void ProcessJob()
    {
        auto& job = *_job;
        if (job.Querying) return;
        if (job.Receipt) { ReadReceipt(); return; }
        if (!_enabled || !_ready || !AtSelection(job.Owner) || Now() >= job.Deadline)
        { Finish(false); return; }
        ObjectGuid guid = ObjectGuid::Create<HighGuid::Player>(job.Guid);
        if (ObjectAccessor::FindConnectedPlayer(guid) || sWorldSessionMgr->FindOfflineSessionForCharacterGUID(job.Guid))
        { Finish(false); return; }
        if (!Barrier().Acquire(job.Owner.Account, job.Guid)) return;
        job.Locked = true; job.Querying = true;
        std::string name = Utf8Literal(job.Name);
        std::string sql = "SELECT o.status,o.character_guid,COALESCE(o.requested_name,''),c.guid,c.account,c.name,c.online,c.level,c.at_login,"
            "c.deleteDate IS NULL,(SELECT COUNT(*) FROM `" + _characters + "`.characters taken WHERE taken.name=" + name
            + " AND taken.guid<>" + std::to_string(job.Guid) + ")+IF(c.name=" + name + ",1,0) FROM `" + _auth + "`.atlas_shop_order o LEFT JOIN `"
            + _characters + "`.characters c ON c.guid=" + std::to_string(job.Guid) + " WHERE "
            + NativeOrderPredicate(_realm, job.Distribution, job.Owner.Account);
        _queries.AddCallback(CharacterDatabase.AsyncQuery(sql).WithCallback([this](QueryResult result)
        {
            auto& current = *_job;
            current.Querying = false;
            if (!result) { Finish(false); return; }
            Field* fields = result->Fetch();
            std::string status = fields[0].Get<std::string>();
            if (status == "consumed")
            {
                bool same = fields[1].Get<uint32>() == current.Guid && fields[2].Get<std::string>() == current.Name;
                Finish(same); // Replayed confirmation never rewrites the character.
                return;
            }
            ObjectGuid guid = ObjectGuid::Create<HighGuid::Player>(current.Guid);
            auto const* cached = sCharacterCache->GetCharacterCacheByGuid(guid);
            if (!_enabled || !_ready || !AtSelection(current.Owner) || status != "available" || fields[3].IsNull()
                || fields[4].Get<uint32>() != current.Owner.Account || fields[6].Get<uint8>() != 0
                || fields[7].Get<uint8>() < 10 || (fields[8].Get<uint32>() & PendingFlags) != 0
                || !fields[9].Get<uint8>() || fields[10].Get<uint64>() != 0 || !cached
                || cached->AccountId != current.Owner.Account || cached->Name != fields[5].Get<std::string>()
                || ObjectAccessor::FindConnectedPlayer(guid) || sWorldSessionMgr->FindOfflineSessionForCharacterGUID(current.Guid))
            { Finish(false); return; }
            current.OldName = fields[5].Get<std::string>();
            if (current.OldName == current.Name) { Finish(false); return; }
            if (current.Validate) { Finish(true); return; }
            auto transaction = CharacterDatabase.BeginTransaction();
            ++_subscriptions.at(current.Owner.Account).Revision;
            for (auto const& statement : NativeConsumeSql(_characters, _auth, _realm, current.Distribution,
                current.Owner.Account, current.Guid, current.OldName, current.Name, PendingFlags))
                transaction->Append(statement);
            current.Querying = true;
            _transactions.AddCallback(CharacterDatabase.AsyncCommitTransaction(transaction)).AfterComplete([this](bool success)
            {
                _job->Querying = false; _job->Receipt = true;
                if (!success) LOG_ERROR("module", "AtlasShop native transaction failed; checking its durable receipt before releasing the guard.");
                ReadReceipt();
            });
        }));
    }
    void ReadReceipt()
    {
        auto& job = *_job;
        if (job.Querying || Now() < _receiptRetry) return;
        job.Querying = true;
        _queries.AddCallback(LoginDatabase.AsyncQuery("SELECT o.status,o.character_guid,COALESCE(o.requested_name,'') FROM atlas_shop_order o WHERE "
            + NativeOrderPredicate(_realm, job.Distribution, job.Owner.Account)).WithCallback([this](QueryResult result)
        {
            _job->Querying = false;
            if (!result)
            {
                // A lost DB response cannot justify releasing a stale name cache.
                _ready = false; _receiptUnavailable = true; _receiptRetry = Now() + 1000;
                LOG_ERROR("module", "AtlasShop native receipt unavailable; retaining the character write guard until the database recovers.");
                return;
            }
            Field* fields = result->Fetch();
            bool success = fields[0].Get<std::string>() == "consumed" && fields[1].Get<uint32>() == _job->Guid
                && fields[2].Get<std::string>() == _job->Name;
            if (success)
            {
                sCharacterCache->UpdateCharacterData(ObjectGuid::Create<HighGuid::Player>(_job->Guid), _job->Name);
                LOG_INFO("module", "AtlasShop account {} consumed rename service {} for character {}.",
                    _job->Owner.Account, _job->Distribution, _job->Guid);
            }
            Finish(success);
        }));
    }
    bool _enabled = false, _ready = false, _heartbeating = false, _schemaChecked = false, _receiptUnavailable = false;
    uint32 _realm = 0, _heartbeat = 0, _refresh = 0, _prune = 0;
    uint64 _generation = 0, _receiptRetry = 0;
    std::string _auth, _characters;
    std::unordered_map<uint32, Subscription> _subscriptions;
    std::unordered_map<uint32, RequestWindow> _requestWindows;
    std::optional<Assignment> _job;
    AsyncCallbackProcessor<QueryCallback> _queries;
    AsyncCallbackProcessor<TransactionCallback> _transactions;
};

class NativeWorld : public WorldScript
{
public:
    NativeWorld() : WorldScript("AtlasShopNative", { WORLDHOOK_ON_AFTER_CONFIG_LOAD, WORLDHOOK_ON_UPDATE, WORLDHOOK_ON_SHUTDOWN }) { }
    void OnAfterConfigLoad(bool) override { NativeServices::Instance().Configure(); }
    void OnUpdate(uint32 diff) override { NativeServices::Instance().Update(diff); }
    void OnShutdown() override { NativeServices::Instance().Stop(); }
};
}

void TrackSave(uint32 guid, std::shared_ptr<void> const& transaction, bool nameWrite) { Barrier().TrackSave(guid, transaction, nameWrite); }
void TrackNameWork(std::shared_ptr<void> const& work) { Barrier().TrackNameWork(work); }
bool CharacterWritesAllowed(uint32 account) { return !Barrier().NamesPaused() && !Barrier().Guarded(account); }
void ForgetSession(WorldSession* session) { NativeServices::Instance().Forget(session); }
bool NativeCanReceive(WorldSession* session, WorldPacket& packet) { return NativeServices::Instance().Receive(session, packet); }
bool IsNativePacket(WorldPacket const& packet)
{
    return packet.GetOpcode() == CMSG_CHAR_ENUM && packet.size() >= 5 && std::memcmp(packet.contents(), Magic.data(), 5) == 0;
}
bool AllowNativePacket(WorldSession* session) { return NativeServices::Instance().Allow(session); }
bool NativeGuarded(uint32 account) { return Barrier().Guarded(account); }
bool NativeNamesPaused() { return Barrier().NamesPaused(); }
void AddNativeScripts() { new NativeWorld(); }
}
