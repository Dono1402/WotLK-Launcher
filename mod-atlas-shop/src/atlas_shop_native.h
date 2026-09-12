#pragma once
#include "Define.h"
#include <memory>

class WorldSession;
class WorldPacket;
namespace AtlasShop
{
// Implemented in three patched core translation units: missing hooks fail link.
uint32 StorageHooksVersion();
uint32 CharacterHooksVersion();
uint32 SessionHooksVersion();
void TrackSave(uint32 guid, std::shared_ptr<void> const& transaction, bool nameWrite);
void TrackNameWork(std::shared_ptr<void> const& work);
bool CharacterWritesAllowed(uint32 account);
void ForgetSession(WorldSession* session);
bool NativeCanReceive(WorldSession* session, WorldPacket& packet);
bool IsNativePacket(WorldPacket const& packet);
bool AllowNativePacket(WorldSession* session);
bool NativeGuarded(uint32 account);
bool NativeNamesPaused();
void AddNativeScripts();
}
