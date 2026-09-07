#include "CombatMath.h"
#include "CaptureSchedule.h"
#include "SqlJson.h"
#include "Config.h"
#include "DatabaseEnv.h"
#include "Item.h"
#include "Log.h"
#include "Player.h"
#include "ScriptMgr.h"
#include "WorldSession.h"

#include <chrono>
#include <string>
#include <vector>
#include <fmt/format.h>

namespace
{
using AtlasArmory::Array;
using AtlasArmory::Number;
using AtlasArmory::Object;
using AtlasArmory::Text;

std::string Snapshot(Player* p, char const* reason)
{
    static_assert(MAX_ENCHANTMENT_SLOT == 12, "Armory import expects twelve enchantment slots");
    auto const capturedAt = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::system_clock::now().time_since_epoch()).count();
    auto const character = Object({
        {"guid", Number(p->GetGUID().GetCounter())}, {"name", Text(p->GetName())},
        {"classId", Number(p->getClass())}, {"race", Number(p->getRace())},
        {"gender", Number(p->getGender())}, {"level", Number(p->GetLevel())},
        {"skin", Number(p->GetByteValue(PLAYER_BYTES, 0))},
        {"face", Number(p->GetByteValue(PLAYER_BYTES, 1))},
        {"hairStyle", Number(p->GetByteValue(PLAYER_BYTES, 2))},
        {"hairColor", Number(p->GetByteValue(PLAYER_BYTES, 3))},
        {"facialStyle", Number(p->GetByteValue(PLAYER_BYTES_2, 0))}
    });
    std::vector<std::string> equipment;
    for (uint8 slot = EQUIPMENT_SLOT_START; slot < EQUIPMENT_SLOT_END; ++slot)
    {
        Item* item = p->GetItemByPos(INVENTORY_SLOT_BAG_0, slot);
        if (!item)
            continue;
        ItemTemplate const* proto = item->GetTemplate();
        std::string enchantments;
        for (uint8 index = 0; index < MAX_ENCHANTMENT_SLOT; ++index)
        {
            auto const enchant = static_cast<EnchantmentSlot>(index);
            enchantments += fmt::format("{} {} {} ", item->GetEnchantmentId(enchant),
                item->GetEnchantmentDuration(enchant), item->GetEnchantmentCharges(enchant));
        }
        equipment.push_back(Object({
            {"slot", Number(slot)}, {"itemId", Number(item->GetEntry())},
            {"displayId", Number(proto->DisplayInfoID)}, {"itemLevel", Number(proto->ItemLevel)},
            {"quality", Number(proto->Quality)}, {"randomPropertyId", Number(item->GetItemRandomPropertyId())},
            {"enchantments", Text(enchantments)}
        }));
    }
    std::vector<std::string> schools;
    for (uint8 school = SPELL_SCHOOL_HOLY; school < MAX_SPELL_SCHOOL; ++school)
    {
        schools.push_back(Object({
            {"id", Number(school)},
            {"spellPower", Number(p->SpellBaseDamageBonusDone(static_cast<SpellSchoolMask>(1 << school)))},
            {"spellCritPct", Number(p->GetFloatValue(PLAYER_SPELL_CRIT_PERCENTAGE1 + school))}
        }));
    }
    uint8 points[3] = {0, 0, 0};
    p->GetTalentTreePoints(points);
    auto const values = Object({
        {"strength", Number(p->GetStat(STAT_STRENGTH))}, {"agility", Number(p->GetStat(STAT_AGILITY))},
        {"stamina", Number(p->GetStat(STAT_STAMINA))}, {"intellect", Number(p->GetStat(STAT_INTELLECT))},
        {"spirit", Number(p->GetStat(STAT_SPIRIT))}, {"armor", Number(p->GetArmor())},
        {"maxHealth", Number(p->GetMaxHealth())}, {"maxMana", Number(p->GetMaxPower(POWER_MANA))},
        {"attackPower", Number(p->GetTotalAttackPowerValue(BASE_ATTACK))},
        {"rangedAttackPower", Number(p->GetTotalAttackPowerValue(RANGED_ATTACK))},
        {"meleeCritPct", Number(p->GetFloatValue(PLAYER_CRIT_PERCENTAGE))},
        {"rangedCritPct", Number(p->GetFloatValue(PLAYER_RANGED_CRIT_PERCENTAGE))},
        {"meleeHitPct", Number(p->m_modMeleeHitChance)}, {"rangedHitPct", Number(p->m_modRangedHitChance)},
        {"spellHitPct", Number(p->m_modSpellHitChance)},
        {"meleeHastePct", Number(AtlasArmory::HastePercent(p->m_modAttackSpeedPct[BASE_ATTACK]))},
        {"rangedHastePct", Number(AtlasArmory::HastePercent(p->m_modAttackSpeedPct[RANGED_ATTACK]))},
        {"spellHastePct", Number(AtlasArmory::HastePercent(p->GetFloatValue(UNIT_MOD_CAST_SPEED)))},
        {"expertise", Number(p->GetUInt32Value(PLAYER_EXPERTISE))},
        {"healingPower", Number(p->SpellBaseHealingBonusDone(SPELL_SCHOOL_MASK_ALL))},
        {"manaRegenCasting", Number(p->GetFloatValue(UNIT_FIELD_POWER_REGEN_INTERRUPTED_FLAT_MODIFIER + static_cast<uint16>(POWER_MANA)) * 5.0f)},
        {"defenseSkill", Number(p->GetDefenseSkillValue())},
        {"dodgePct", Number(p->GetFloatValue(PLAYER_DODGE_PERCENTAGE))},
        {"parryPct", Number(p->GetFloatValue(PLAYER_PARRY_PERCENTAGE))},
        {"blockPct", Number(p->GetFloatValue(PLAYER_BLOCK_PERCENTAGE))},
        {"resilience", Number(p->GetUInt32Value(PLAYER_FIELD_COMBAT_RATING_1 + static_cast<uint16>(CR_CRIT_TAKEN_SPELL)))}
    });
    return Object({
        {"schemaVersion", "1"}, {"source", Text("atlas-armory-engine")}, {"reason", Text(reason)},
        {"capturedAtMs", Number(capturedAt)}, {"character", character}, {"equipment", Array(equipment)},
        {"values", values}, {"schools", Array(schools)},
        {"talentPoints", Array({Number(points[0]), Number(points[1]), Number(points[2])})},
        {"form", Number(static_cast<uint32>(p->GetShapeshiftForm()))},
        {"includesTemporaryEffects", "CAST('true' AS JSON)"}
    });
}

std::uint64_t SteadyNow()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now().time_since_epoch()).count();
}

struct CaptureState final : DataMap::Base
{
    AtlasArmory::CaptureSchedule schedule{SteadyNow()};
};

std::string const StateKey = "AtlasArmory.LiveCapture";

bool Eligible(Player* player)
{
    if (!player || !player->GetSession() || player->GetSession()->IsBot()
        || !sConfigMgr->GetOption<bool>("AtlasArmory.Enable", false))
        return false;
    auto const onlyGuid = sConfigMgr->GetOption<uint32>("AtlasArmory.OnlyGuid", 0);
    return !onlyGuid || player->GetGUID().GetCounter() == onlyGuid;
}

class AtlasArmoryPlayerScript final : public PlayerScript
{
public:
    AtlasArmoryPlayerScript() : PlayerScript("AtlasArmoryPlayerScript") { }

    void OnPlayerAfterSetVisibleItemSlot(Player* player, uint8 slot, Item*) override
    {
        if (slot >= EQUIPMENT_SLOT_END || !Eligible(player) || !player->IsInWorld()
            || !sConfigMgr->GetOption<bool>("AtlasArmory.LiveEnable", false))
            return;
        // The hook also runs on removal, before all bonuses are recalculated. Only mark dirty here.
        player->CustomData.GetDefault<CaptureState>(StateKey)->schedule.EquipmentChanged(SteadyNow());
    }

    void OnPlayerAfterUpdate(Player* player, uint32) override
    {
        if (!Eligible(player) || !player->IsInWorld())
            return;
        if (!sConfigMgr->GetOption<bool>("AtlasArmory.LiveEnable", false))
        {
            player->CustomData.Erase(StateKey);
            return;
        }
        auto const reason = player->CustomData.GetDefault<CaptureState>(StateKey)->schedule.Poll(SteadyNow());
        switch (reason)
        {
            case AtlasArmory::CaptureReason::Login: Capture(player, "login"); break;
            case AtlasArmory::CaptureReason::Equipment: Capture(player, "equipment"); break;
            case AtlasArmory::CaptureReason::Periodic: Capture(player, "periodic"); break;
            case AtlasArmory::CaptureReason::None: break;
        }
    }

    void OnPlayerBeforeLogout(Player* player) override
    {
        if (Eligible(player))
            Capture(player, "logout");
    }

private:
    void Capture(Player* player, char const* reason)
    {
        try
        {
            // One asynchronous, self-contained record. No synchronous queries or game-state mutations.
            CharacterDatabase.Execute(
                "INSERT INTO atlas_armory_combat_snapshot (guid,snapshot) VALUES ({},{}) "
                "ON DUPLICATE KEY UPDATE snapshot={}",
                player->GetGUID().GetCounter(), Snapshot(player, reason),
                AtlasArmory::NewestSnapshot("snapshot", "VALUES(snapshot)"));
        }
        catch (std::exception const& error)
        {
            LOG_ERROR("module", "Atlas armory capture failed: {}", error.what());
        }
    }
};
}

void AddAtlasArmoryScripts()
{
    new AtlasArmoryPlayerScript();
}
