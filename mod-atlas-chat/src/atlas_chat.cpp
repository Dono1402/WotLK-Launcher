#include "Define.h"
#include "atlas_chat_policy.h"

#include "AsyncCallbackProcessor.h"
#include "Chat.h"
#include "CommandScript.h"
#include "Config.h"
#include "CryptoRandom.h"
#include "DatabaseEnv.h"
#include "Log.h"
#include "ObjectAccessor.h"
#include "Player.h"
#include "PlayerScript.h"
#include "QueryCallback.h"
#include "Transaction.h"
#include "WorldPacket.h"
#include "WorldScript.h"
#include "WorldSession.h"

#include <chrono>
#include <functional>
#include <map>
#include <utility>

using namespace Acore::ChatCommands;

namespace
{
using AtlasChat::SessionStamp;

std::uint64_t SteadyMs()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

std::int64_t UtcMicros()
{
    return std::chrono::duration_cast<std::chrono::microseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
}

std::string Token()
{
    auto bytes = Acore::Crypto::GetRandomBytes<16>();
    bytes[6] = (bytes[6] & 0x0F) | 0x40;
    bytes[8] = (bytes[8] & 0x3F) | 0x80;
    return AtlasChat::Hex(std::string_view(reinterpret_cast<char const*>(bytes.data()), bytes.size()));
}

// A hex literal has no SQL quoting or connection-charset dependency, and does
// not acquire a synchronous LoginDatabase connection merely to escape text.
std::string SqlText(std::string_view text)
{
    return "CONVERT(X'" + AtlasChat::Hex(text) + "' USING utf8mb4)";
}

void PrivateLine(Player* player, std::string const& line)
{
    WorldPacket packet;
    ChatHandler::BuildChatPacket(packet, CHAT_MSG_SYSTEM, LANG_UNIVERSAL,
        ObjectGuid(), player->GetGUID(), line, CHAT_TAG_NONE);
    player->GetSession()->SendPacket(&packet);
}

void NamedWhisper(Player* player, std::string const& username, std::string const& line, bool sent = false)
{
    auto name = AtlasChat::WhisperName(username);
    if (!name)
        return;
    WorldPacket packet;
    // The legacy GM-message opcode carries an explicit name. No GM flag or
    // invented character GUID is assigned. The paired Hermes adapter preserves
    // the name as an ordinary modern WHISPER / WHISPER_INFORM.
    ChatHandler::BuildChatPacket(packet, sent ? CHAT_MSG_WHISPER_INFORM : CHAT_MSG_WHISPER,
        LANG_UNIVERSAL, ObjectGuid(), player->GetGUID(), line, CHAT_TAG_NONE, *name, "", 0, true);
    player->GetSession()->SendPacket(&packet);
}

class Bridge
{
public:
    static Bridge& Instance()
    {
        static Bridge bridge;
        return bridge;
    }

    void Start()
    {
        _enabled = sConfigMgr->GetOption<bool>("AtlasChat.Enable", false);
        _realm = sConfigMgr->GetOption<uint32>("RealmID", 1);
        _pollInterval = std::clamp(sConfigMgr->GetOption<uint32>("AtlasChat.PollIntervalMs", 1000), 250u, 5000u);
        if (!_enabled || !_realm)
            return;
        Query("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND "
            "table_name IN ('atlas_launcher_chat_inbox','atlas_launcher_chat_outbox',"
            "'atlas_launcher_chat_message','atlas_launcher_profile','atlas_launcher_friendship')",
            [this](QueryResult result)
            {
                _ready = result && result->Fetch()[0].Get<uint64>() == 5;
                if (!_ready)
                    LOG_ERROR("module", "Atlas chat disabled: launcher auth migration 0006 is required.");
                else
                    LOG_INFO("module", "Atlas chat bridge ready for realm {} (named #Launcher whispers).", _realm);
            });
    }

    void Login(Player* player)
    {
        if (!_enabled || !Human(player))
            return;
        auto account = player->GetSession()->GetAccountId();
        _sessions[account] = { { account, uint32(player->GetGUID().GetCounter()), ++_generation, UtcMicros() } };
    }

    void Logout(Player* player)
    {
        if (!player || !player->GetSession())
            return;
        auto entry = _sessions.find(player->GetSession()->GetAccountId());
        if (entry != _sessions.end() && entry->second.Stamp.Character == player->GetGUID().GetCounter())
            _sessions.erase(entry);
    }

    void Update(uint32 diff)
    {
        // CMSG_MESSAGECHAT/commands and player login/logout run on the world
        // thread. All async results are consumed here; no Player* crosses an await.
        _queries.ProcessReadyCallbacks();
        _transactions.ProcessReadyCallbacks();
        if (!_enabled || !_ready)
            return;
        _elapsed += diff;
        if (_elapsed < _pollInterval)
            return;
        _elapsed = 0;
        auto now = SteadyMs();
        _limiter.Prune(now);
        for (auto it = _delivered.begin(); it != _delivered.end();)
            if (it->second.ExpiresMs <= now)
                it = _delivered.erase(it);
            else
                ++it;
        for (auto it = _requests.begin(); it != _requests.end();)
            if (now - it->second.StartedMs > 30000)
            {
                Notify(it->second.Sender, "[Atlas] Confirmation indisponible. Consultez le launcher avant de renvoyer.");
                it = _requests.erase(it);
            }
            else
                ++it;
        PollReceipts();
        if (!_outboxBusy)
            PollOutbox();
    }

    bool Send(Player* player, std::string_view args)
    {
        if (!Human(player))
            return false;
        if (!_enabled || !_ready)
        {
            PrivateLine(player, "[Atlas] La messagerie est indisponible pour le moment.");
            return true;
        }
        if (!player->CanSpeak())
        {
            PrivateLine(player, "[Atlas] Vous ne pouvez pas envoyer de message pendant une restriction de discussion.");
            return true;
        }
        auto start = args.find_first_not_of(' ');
        auto split = start == std::string_view::npos ? start : args.find(' ', start);
        auto bodyStart = split == std::string_view::npos ? split : args.find_first_not_of(' ', split);
        if (bodyStart == std::string_view::npos)
        {
            PrivateLine(player, "[Atlas] Utilisation : .atlasmsg PseudoAtlas votre message");
            return true;
        }
        auto target = args.substr(start, split - start);
        auto body = AtlasChat::NormalizeBody(args.substr(bodyStart));
        if (!AtlasChat::ValidUsername(target) || !body)
        {
            PrivateLine(player, "[Atlas] Pseudo ou texte invalide (1000 caracteres maximum).");
            return true;
        }
        auto session = _sessions.find(player->GetSession()->GetAccountId());
        if (session == _sessions.end() || !CurrentPlayer(session->second.Stamp))
            return true;
        if (_requests.size() >= 128 || !_limiter.TryTake(session->first, SteadyMs()))
        {
            PrivateLine(player, "[Atlas] Trop de messages en attente. Patientez avant de reessayer.");
            return true;
        }
        auto token = Token();
        auto account = session->first;
        _requests.emplace(token, Request{ session->second.Stamp, std::string(target), *body, 0, SteadyMs(), 0, false });
        Query("SELECT p.account_id,p.display_username FROM atlas_launcher_profile p "
            "JOIN atlas_launcher_friendship f ON f.account_low_id=LEAST(p.account_id," + std::to_string(account) + ") "
            "AND f.account_high_id=GREATEST(p.account_id," + std::to_string(account) + ") "
            "JOIN atlas_launcher_profile owner ON owner.account_id=" + std::to_string(account) + " "
            "WHERE p.display_username=" + SqlText(target) + " AND p.account_id<>" + std::to_string(account) +
            " AND f.accepted_at IS NOT NULL LIMIT 1", [this, token](QueryResult result)
            {
                auto request = _requests.find(token);
                if (request == _requests.end())
                    return;
                if (!result || !CurrentPlayer(request->second.Sender))
                {
                    Notify(request->second.Sender, "[Atlas] Ce pseudo ne correspond pas a un ami Atlas accepte.");
                    _requests.erase(request);
                    return;
                }
                request->second.Recipient = result->Fetch()[0].Get<uint32>();
                request->second.TargetName = result->Fetch()[1].Get<std::string>();
                if (!AtlasChat::ValidUsername(request->second.TargetName))
                {
                    Notify(request->second.Sender, "[Atlas] Ce profil ne peut pas utiliser la messagerie en jeu.");
                    _requests.erase(request);
                    return;
                }
                InsertInbox(token);
            });
        return true;
    }

private:
    struct Session { SessionStamp Stamp; };
    struct Request
    {
        SessionStamp Sender;
        std::string TargetName;
        std::string Body;
        uint32 Recipient;
        std::uint64_t StartedMs;
        unsigned Attempts;
        bool Submitted;
    };
    struct Delivered { uint32 Character; std::uint64_t ExpiresMs; };

    static bool Human(Player* player)
    {
        return player && player->GetSession() && !player->GetSession()->IsBot() && player->IsInWorld();
    }

    Player* CurrentPlayer(SessionStamp const& stamp)
    {
        auto session = _sessions.find(stamp.Account);
        if (session == _sessions.end() || !session->second.Stamp.Matches(stamp))
            return nullptr;
        auto player = ObjectAccessor::FindPlayer(ObjectGuid::Create<HighGuid::Player>(stamp.Character));
        return Human(player) && player->GetSession()->GetAccountId() == stamp.Account ? player : nullptr;
    }

    void Notify(SessionStamp const& stamp, std::string const& text)
    {
        if (auto player = CurrentPlayer(stamp))
            PrivateLine(player, text);
    }

    void Query(std::string sql, std::function<void(QueryResult)> callback)
    {
        _queries.AddCallback(LoginDatabase.AsyncQuery(sql).WithCallback(std::move(callback)));
    }

    void InsertInbox(std::string const& token)
    {
        auto entry = _requests.find(token);
        if (entry == _requests.end())
            return;
        auto& request = entry->second;
        ++request.Attempts;
        auto transaction = LoginDatabase.BeginTransaction();
        transaction->Append("INSERT INTO atlas_launcher_chat_inbox "
            "(request_id,realm_id,sender_account_id,sender_character_guid,recipient_account_id,body,created_at) "
            "VALUES (X'{}',{},{},{},{},{},UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE request_id=request_id",
            token, _realm, request.Sender.Account, request.Sender.Character, request.Recipient, SqlText(request.Body));
        _transactions.AddCallback(LoginDatabase.AsyncCommitTransaction(transaction)).AfterComplete([this, token](bool success)
        {
            auto entry = _requests.find(token);
            if (entry == _requests.end())
                return;
            if (success)
                entry->second.Submitted = true;
            else if (entry->second.Attempts < 2)
                InsertInbox(token); // the same UUID is mandatory for an uncertain retry
            else
            {
                Notify(entry->second.Sender, "[Atlas] Envoi non confirme. Consultez le launcher avant de renvoyer.");
                _requests.erase(entry);
            }
        });
    }

    void PollReceipts()
    {
        if (_receiptBusy)
            return;
        std::string ids;
        for (auto const& [token, request] : _requests)
            if (request.Submitted)
                ids += (ids.empty() ? "" : ",") + std::string("X'") + token + "'";
        if (ids.empty())
            return;
        _receiptBusy = true;
        Query("SELECT LOWER(HEX(request_id)),status FROM atlas_launcher_chat_inbox WHERE realm_id=" +
            std::to_string(_realm) + " AND request_id IN (" + ids + ") AND status<>0 LIMIT 128",
            [this](QueryResult result)
            {
                _receiptBusy = false;
                if (!result)
                    return;
                do
                {
                    auto fields = result->Fetch();
                    auto request = _requests.find(fields[0].Get<std::string>());
                    if (request == _requests.end())
                        continue;
                    if (fields[1].Get<uint8>() == 1)
                    {
                        if (auto player = CurrentPlayer(request->second.Sender))
                            for (auto const& line : AtlasChat::RenderLines("", request->second.Body))
                                NamedWhisper(player, request->second.TargetName, line, true);
                    }
                    else
                        Notify(request->second.Sender, "[Atlas] Message refuse : verifiez votre amitie Atlas ou patientez avant de reessayer.");
                    _requests.erase(request);
                } while (result->NextRow());
            });
    }

    void PollOutbox()
    {
        std::map<uint32, SessionStamp> claimedSessions;
        std::string accounts;
        for (auto const& [account, session] : _sessions)
        {
            if (claimedSessions.size() >= 1024)
                break;
            if (CurrentPlayer(session.Stamp))
            {
                claimedSessions.emplace(account, session.Stamp);
                accounts += (accounts.empty() ? "" : ",") + std::to_string(account);
            }
        }
        if (accounts.empty())
            accounts = "0";
        auto token = Token();
        auto startedMs = SteadyMs();
        _outboxBusy = true;
        auto transaction = LoginDatabase.BeginTransaction();
        transaction->Append("UPDATE atlas_launcher_chat_outbox SET status=4,last_error='expired',lease_token=NULL,lease_until=NULL "
            "WHERE (realm_id=0 OR realm_id={}) AND status IN (0,1) AND expires_at<=UTC_TIMESTAMP(6) LIMIT 128", _realm);
        transaction->Append("UPDATE atlas_launcher_chat_outbox SET status=1,realm_id={},lease_token=X'{}',"
            "lease_until=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 30 SECOND),attempts=LEAST(attempts+1,65535) "
            "WHERE (realm_id={} OR (realm_id=0 AND recipient_account_id IN ({}))) "
            "AND expires_at>UTC_TIMESTAMP(6) AND (status=0 OR (status=1 AND lease_until<UTC_TIMESTAMP(6))) "
            "ORDER BY message_id LIMIT 64", _realm, token, _realm, accounts);
        _transactions.AddCallback(LoginDatabase.AsyncCommitTransaction(transaction)).AfterComplete(
            [this, token, startedMs, claimedSessions = std::move(claimedSessions)](bool success)
            {
                if (!success || SteadyMs() - startedMs >= 20000)
                {
                    _outboxBusy = false;
                    return;
                }
                Query("SELECT o.message_id,m.sender_account_id,m.recipient_account_id,m.sender_username,m.body,"
                    "IF(f.accepted_at IS NOT NULL,1,0),TIMESTAMPDIFF(MICROSECOND,'1970-01-01 00:00:00',m.created_at),"
                    "TIMESTAMPDIFF(MICROSECOND,'1970-01-01 00:00:00',o.expires_at) "
                    "FROM atlas_launcher_chat_outbox o JOIN atlas_launcher_chat_message m ON m.id=o.message_id "
                    "AND m.recipient_account_id=o.recipient_account_id "
                    "LEFT JOIN atlas_launcher_friendship f ON f.account_low_id=m.account_low_id AND f.account_high_id=m.account_high_id "
                    "WHERE o.realm_id=" + std::to_string(_realm) + " AND o.status=1 AND o.lease_token=X'" + token +
                    "' AND o.lease_until>UTC_TIMESTAMP(6) ORDER BY o.message_id LIMIT 64",
                    [this, token, startedMs, claimedSessions](QueryResult result)
                    {
                        DeliverBatch(token, startedMs, claimedSessions, result);
                    });
            });
    }

    void DeliverBatch(std::string const& token, std::uint64_t startedMs,
        std::map<uint32, SessionStamp> const& claimedSessions, QueryResult result)
    {
        if (!result || SteadyMs() - startedMs >= 20000)
        {
            _outboxBusy = false;
            return;
        }
        auto acknowledgements = LoginDatabase.BeginTransaction();
        do
        {
            auto fields = result->Fetch();
            auto id = fields[0].Get<uint64>();
            auto sender = fields[1].Get<uint32>();
            auto recipient = fields[2].Get<uint32>();
            auto name = fields[3].Get<std::string>();
            auto body = fields[4].Get<std::string>();
            uint32 status = 3, character = 0;
            std::string error = "recipient_offline_or_session_changed";
            auto previous = _delivered.find(id);
            auto claimed = claimedSessions.find(recipient);
            auto current = _sessions.find(recipient);
            if (previous != _delivered.end())
            {
                status = 2;
                character = previous->second.Character;
                error.clear();
            }
            else if (claimed != claimedSessions.end() && current != _sessions.end() &&
                AtlasChat::MayDeliver(current->second.Stamp, claimed->second, sender, recipient,
                    fields[5].Get<uint8>() != 0, fields[6].Get<uint64>(), fields[7].Get<uint64>(), UtcMicros()))
            {
                auto player = CurrentPlayer(current->second.Stamp);
                auto lines = AtlasChat::ValidUsername(name) ? AtlasChat::RenderLines("", body) : std::vector<std::string>{};
                if (player && !lines.empty() && _delivered.size() < 8192)
                {
                    for (auto const& line : lines)
                        NamedWhisper(player, name, line);
                    character = current->second.Stamp.Character;
                    _delivered.emplace(id, Delivered{ character, SteadyMs() + 90000 });
                    status = 2;
                    error.clear();
                }
                else
                {
                    status = 4;
                    error = "invalid_payload_or_bridge_capacity";
                }
            }
            acknowledgements->Append("UPDATE atlas_launcher_chat_outbox SET status={},last_error={},"
                "delivered_at={},delivered_character_guid={},lease_token=NULL,lease_until=NULL "
                "WHERE message_id={} AND realm_id={} AND status=1 AND lease_token=X'{}'",
                status, error.empty() ? "NULL" : SqlText(error), status == 2 ? "UTC_TIMESTAMP(6)" : "NULL",
                character ? std::to_string(character) : "NULL", id, _realm, token);
        } while (result->NextRow());
        _transactions.AddCallback(LoginDatabase.AsyncCommitTransaction(acknowledgements)).AfterComplete([this](bool success)
        {
            _outboxBusy = false;
            if (!success)
                LOG_WARN("module", "Atlas chat delivery acknowledgement failed; live-process deduplication retained.");
        });
    }

    bool _enabled = false, _ready = false, _receiptBusy = false, _outboxBusy = false;
    uint32 _realm = 0, _elapsed = 0, _pollInterval = 1000;
    std::uint64_t _generation = 0;
    std::map<uint32, Session> _sessions;
    std::map<std::string, Request> _requests;
    std::unordered_map<uint64, Delivered> _delivered;
    AtlasChat::SubmissionLimiter _limiter;
    AsyncCallbackProcessor<QueryCallback> _queries;
    AsyncCallbackProcessor<TransactionCallback> _transactions;
};

class AtlasChatWorldScript final : public WorldScript
{
public:
    AtlasChatWorldScript() : WorldScript("AtlasChatWorldScript") { }
    void OnStartup() override { Bridge::Instance().Start(); }
    void OnUpdate(uint32 diff) override { Bridge::Instance().Update(diff); }
};

class AtlasChatPlayerScript final : public PlayerScript
{
public:
    AtlasChatPlayerScript() : PlayerScript("AtlasChatPlayerScript") { }
    void OnPlayerLogin(Player* player) override { Bridge::Instance().Login(player); }
    void OnPlayerLogout(Player* player) override { Bridge::Instance().Logout(player); }
};

class AtlasChatCommandScript final : public CommandScript
{
public:
    AtlasChatCommandScript() : CommandScript("AtlasChatCommandScript") { }
    ChatCommandTable GetCommands() const override
    {
        static ChatCommandTable table = { { "atlasmsg", HandleMessage, SEC_PLAYER, Console::No } };
        return table;
    }
    static bool HandleMessage(ChatHandler* handler, char const* args)
    {
        return Bridge::Instance().Send(handler->GetPlayer(), args ? std::string_view(args) : std::string_view{});
    }
};
}

void AddAtlasChatScripts()
{
    new AtlasChatWorldScript();
    new AtlasChatPlayerScript();
    new AtlasChatCommandScript();
}
