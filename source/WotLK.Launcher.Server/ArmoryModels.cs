using System.Text.Json;

namespace WotLK.Launcher.Server;

public sealed record ArmoryRoster(string ObservedAtUtc, IReadOnlyList<ArmoryRosterCharacter> Characters);

public sealed record ArmoryRosterCharacter(
    ArmoryCharacter Character,
    JsonElement? Snapshot,
    ArmoryBaseStatistics? Values,
    IReadOnlyList<ArmoryEquipment> Equipment);

public sealed record ArmoryCharacter(
    uint Guid, string Name, byte Race, byte ClassId, byte Gender, byte Level,
    byte Skin, byte Face, byte HairStyle, byte HairColor, byte FacialStyle,
    byte Online, uint ZoneId, uint LastLogout);

public sealed record ArmoryBaseStatistics(
    uint Strength, uint Agility, uint Stamina, uint Intellect, uint Spirit,
    uint Armor, uint MaxHealth, uint MaxMana);

public sealed record ArmoryEquipment(
    int Slot, uint ItemId, uint DisplayId, string Name, string? NameFr,
    int Quality, int InventoryType, uint ItemLevel, int RandomPropertyId, string Enchantments);

public sealed record ArmoryCatalog(string CapturedAtUtc, IReadOnlyList<ArmoryCatalogItem> Items);

public sealed record ArmoryLocalizedText(string En, string? Fr);
public sealed record ArmoryItemDamage(double Min, double Max, int School);

public sealed record ArmoryCatalogItem(
    uint ItemId, uint DisplayId, int Quality, uint ItemLevel,
    ArmoryLocalizedText Name, ArmoryLocalizedText Description,
    int ClassId, int SubclassId, int InventoryType, uint RequiredLevel,
    uint Armor, uint Block, int Bonding, uint MaxDurability, uint Delay,
    IReadOnlyList<ArmoryItemDamage> Damage, IReadOnlyList<int[]> Stats,
    IReadOnlyList<int> Resistances, IReadOnlyList<int[]> Spells,
    IReadOnlyList<int> Sockets, uint SocketBonus, uint ScalingDistribution, uint ScalingValue);
