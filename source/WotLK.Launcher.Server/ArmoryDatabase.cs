using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WotLK.Launcher.Server;

public sealed partial class LauncherDatabase
{
    internal const int ArmoryMaximumCharacters = 50;
    internal const int ArmoryMaximumEquipmentSlots = 19;
    internal const int ArmoryMaximumCatalogItems = ArmoryMaximumEquipmentSlots * 2;
    internal const int ArmoryCommandTimeoutSeconds = 10;

    public async Task<ArmoryRoster> ListArmoryCharactersAsync(uint accountId, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken);
        return await ReadArmoryRosterAsync(connection, transaction, accountId, null, cancellationToken);
    }

    public async Task<ArmoryCatalog?> GetArmoryCatalogAsync(uint accountId, uint characterGuid, CancellationToken cancellationToken)
    {
        if (characterGuid == 0) return null;
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await using MySqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, isReadOnly: true, cancellationToken);
        ArmoryRoster roster = await ReadArmoryRosterAsync(connection, transaction, accountId, characterGuid, cancellationToken);
        ArmoryRosterCharacter? character = roster.Characters.SingleOrDefault();
        if (character is null) return null;

        HashSet<uint> itemIds = character.Equipment.Select(item => item.ItemId).ToHashSet();
        if (character.Snapshot is JsonElement snapshot)
            foreach (JsonElement item in snapshot.GetProperty("equipment").EnumerateArray())
                itemIds.Add(item.GetProperty("itemId").GetUInt32());
        RequireArmoryData(itemIds.Count <= ArmoryMaximumCatalogItems);
        if (itemIds.Count == 0) return new ArmoryCatalog(roster.ObservedAtUtc, []);

        string world = QuoteArmoryDatabase(_options.WorldDatabaseName);
        await using MySqlCommand command = CreateArmoryCommand(connection, transaction);
        string[] parameters = itemIds.Order().Select((id, index) =>
        {
            string parameter = $"@item{index}";
            command.Parameters.AddWithValue(parameter, id);
            return parameter;
        }).ToArray();
        // IDs originate only from the owned character's equipped instances and validated server capture.
        command.CommandText = $"""
            SELECT it.entry AS item_id, it.displayid AS display_id, it.Quality AS quality,
                   it.ItemLevel AS item_level, it.name AS name_en, loc.Name AS name_fr,
                   it.description AS description_en, loc.Description AS description_fr,
                   it.`class` AS class_id, it.subclass AS subclass_id, it.InventoryType AS inventory_type,
                   it.RequiredLevel AS required_level, it.armor, it.block, it.bonding,
                   it.MaxDurability AS max_durability, it.delay,
                   it.dmg_min1, it.dmg_max1, it.dmg_type1, it.dmg_min2, it.dmg_max2, it.dmg_type2,
                   it.stat_type1, it.stat_value1, it.stat_type2, it.stat_value2,
                   it.stat_type3, it.stat_value3, it.stat_type4, it.stat_value4,
                   it.stat_type5, it.stat_value5, it.stat_type6, it.stat_value6,
                   it.stat_type7, it.stat_value7, it.stat_type8, it.stat_value8,
                   it.stat_type9, it.stat_value9, it.stat_type10, it.stat_value10,
                   it.holy_res, it.fire_res, it.nature_res, it.frost_res, it.shadow_res, it.arcane_res,
                   it.spellid_1, it.spelltrigger_1, it.spellid_2, it.spelltrigger_2,
                   it.spellid_3, it.spelltrigger_3, it.spellid_4, it.spelltrigger_4,
                   it.spellid_5, it.spelltrigger_5,
                   it.socketColor_1, it.socketColor_2, it.socketColor_3, it.socketBonus,
                   it.ScalingStatDistribution AS scaling_distribution, it.ScalingStatValue AS scaling_value
            FROM {world}.item_template it
            LEFT JOIN {world}.item_template_locale loc ON loc.ID = it.entry AND loc.locale = 'frFR'
            WHERE it.entry IN ({string.Join(",", parameters)})
            ORDER BY it.entry
            LIMIT @maximumItems;
            """;
        command.Parameters.AddWithValue("@maximumItems", ArmoryMaximumCatalogItems + 1);
        List<ArmoryCatalogItem> items = [];
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            RequireArmoryData(items.Count < ArmoryMaximumCatalogItems);
            uint id = reader.GetUInt32("item_id");
            RequireArmoryData(itemIds.Contains(id) && items.All(item => item.ItemId != id));
            int[][] stats = Enumerable.Range(1, 10).Select(index => new[]
                { reader.GetInt32($"stat_type{index}"), reader.GetInt32($"stat_value{index}") }).ToArray();
            ArmoryItemDamage[] damage = Enumerable.Range(1, 2).Select(index => new ArmoryItemDamage(
                reader.GetDouble($"dmg_min{index}"), reader.GetDouble($"dmg_max{index}"), reader.GetInt32($"dmg_type{index}"))).ToArray();
            RequireArmoryData(damage.All(value => double.IsFinite(value.Min) && double.IsFinite(value.Max)));
            int[][] spells = Enumerable.Range(1, 5).Select(index => new[]
                { reader.GetInt32($"spellid_{index}"), reader.GetInt32($"spelltrigger_{index}") }).ToArray();
            int[] resistances = new[] { "holy_res", "fire_res", "nature_res", "frost_res", "shadow_res", "arcane_res" }
                .Select(reader.GetInt32).ToArray();
            items.Add(new ArmoryCatalogItem(
                id, reader.GetUInt32("display_id"), reader.GetInt32("quality"), reader.GetUInt32("item_level"),
                new ArmoryLocalizedText(ReadArmoryText(reader, "name_en", 512) ?? $"Item #{id}", ReadArmoryText(reader, "name_fr", 512)),
                new ArmoryLocalizedText(ReadArmoryText(reader, "description_en", 4096) ?? "", ReadArmoryText(reader, "description_fr", 4096)),
                reader.GetInt32("class_id"), reader.GetInt32("subclass_id"), reader.GetInt32("inventory_type"),
                reader.GetUInt32("required_level"), reader.GetUInt32("armor"), reader.GetUInt32("block"),
                reader.GetInt32("bonding"), reader.GetUInt32("max_durability"), reader.GetUInt32("delay"),
                damage, stats, resistances, spells,
                Enumerable.Range(1, 3).Select(index => reader.GetInt32($"socketColor_{index}")).ToArray(),
                reader.GetUInt32("socketBonus"), reader.GetUInt32("scaling_distribution"), reader.GetUInt32("scaling_value")));
        }
        return new ArmoryCatalog(roster.ObservedAtUtc, items);
    }

    private async Task<ArmoryRoster> ReadArmoryRosterAsync(
        MySqlConnection connection, MySqlTransaction transaction, uint accountId, uint? characterGuid,
        CancellationToken cancellationToken)
    {
        string characters = QuoteArmoryDatabase(_options.CharacterDatabaseName);
        string world = QuoteArmoryDatabase(_options.WorldDatabaseName);
        (bool hasStatistics, bool hasSnapshot) = await ReadArmoryCapabilitiesAsync(connection, transaction, cancellationToken);
        string statisticsColumns = hasStatistics
            ? "s.guid AS statistics_guid, s.strength, s.agility, s.stamina, s.intellect, s.spirit, s.armor, s.maxhealth AS max_health, s.maxpower1 AS max_mana"
            : "NULL AS statistics_guid";
        string snapshotColumn = hasSnapshot
            ? "CASE WHEN OCTET_LENGTH(a.snapshot) <= @maximumSnapshotBytes THEN CAST(a.snapshot AS CHAR CHARACTER SET utf8mb4) ELSE NULL END AS combat_snapshot"
            : "NULL AS combat_snapshot";
        List<ArmoryRosterCharacter> rows = [];
        DateTime observed = DateTime.UtcNow;
        await using (MySqlCommand command = CreateArmoryCommand(connection, transaction))
        {
            command.CommandText = $"""
                SELECT UTC_TIMESTAMP(6) AS observed_at_utc,
                       c.guid, c.name, c.race, c.`class` AS class_id, c.gender, c.level,
                       c.skin, c.face, c.hairStyle AS hair_style, c.hairColor AS hair_color,
                       c.facialStyle AS facial_style, c.online, c.zone AS zone_id, c.logout_time,
                       {statisticsColumns}, {snapshotColumn}
                FROM {characters}.characters c
                {(hasStatistics ? $"LEFT JOIN {characters}.character_stats s ON s.guid = c.guid" : "")}
                {(hasSnapshot ? $"LEFT JOIN {characters}.atlas_armory_combat_snapshot a ON a.guid = c.guid" : "")}
                WHERE c.account = @accountId {(characterGuid.HasValue ? "AND c.guid = @characterGuid" : "")}
                ORDER BY c.guid
                LIMIT @maximumCharacters;
                """;
            command.Parameters.AddWithValue("@accountId", accountId);
            if (characterGuid.HasValue) command.Parameters.AddWithValue("@characterGuid", characterGuid.Value);
            command.Parameters.AddWithValue("@maximumCharacters", ArmoryMaximumCharacters);
            if (hasSnapshot) command.Parameters.AddWithValue("@maximumSnapshotBytes", ArmorySnapshotValidator.MaximumBytes);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                RequireArmoryData(rows.Count < ArmoryMaximumCharacters);
                observed = DateTime.SpecifyKind(reader.GetDateTime("observed_at_utc"), DateTimeKind.Utc);
                ArmoryCharacter character = new(
                    reader.GetUInt32("guid"), ReadArmoryText(reader, "name", 24) ?? "", reader.GetByte("race"),
                    reader.GetByte("class_id"), reader.GetByte("gender"), reader.GetByte("level"),
                    reader.GetByte("skin"), reader.GetByte("face"), reader.GetByte("hair_style"),
                    reader.GetByte("hair_color"), reader.GetByte("facial_style"), reader.GetByte("online"),
                    reader.GetUInt32("zone_id"), reader.GetUInt32("logout_time"));
                RequireArmoryData(character.Guid > 0 && character.Name.Length > 0 && character.Gender <= 1
                    && character.Level is >= 1 and <= 80 && character.Online <= 1
                    && rows.All(row => row.Character.Guid != character.Guid));
                ArmoryBaseStatistics? statistics = null;
                if (!reader.IsDBNull("statistics_guid"))
                {
                    try
                    {
                        statistics = new ArmoryBaseStatistics(reader.GetUInt32("strength"), reader.GetUInt32("agility"),
                            reader.GetUInt32("stamina"), reader.GetUInt32("intellect"), reader.GetUInt32("spirit"),
                            reader.GetUInt32("armor"), reader.GetUInt32("max_health"), reader.GetUInt32("max_mana"));
                    }
                    catch (Exception exception) when (exception is InvalidCastException or OverflowException) { }
                }
                string? rawSnapshot = reader.IsDBNull("combat_snapshot") ? null : reader.GetString("combat_snapshot");
                rows.Add(new ArmoryRosterCharacter(character, ArmorySnapshotValidator.Sanitize(rawSnapshot, character, observed), statistics, []));
            }
        }
        if (rows.Count == 0) return new ArmoryRoster(ArmoryDate(observed), []);

        Dictionary<uint, List<ArmoryEquipment>> equipment = rows.ToDictionary(row => row.Character.Guid, _ => new List<ArmoryEquipment>());
        await using (MySqlCommand command = CreateArmoryCommand(connection, transaction))
        {
            string[] parameters = rows.Select((row, index) =>
            {
                string parameter = $"@character{index}";
                command.Parameters.AddWithValue(parameter, row.Character.Guid);
                return parameter;
            }).ToArray();
            command.CommandText = $"""
                SELECT c.guid AS character_guid, ci.slot, ii.itemEntry AS item_id,
                       COALESCE(it.displayid, 0) AS display_id, it.name AS name_en, loc.Name AS name_fr,
                       COALESCE(it.Quality, 0) AS quality, COALESCE(it.InventoryType, 0) AS inventory_type,
                       COALESCE(it.ItemLevel, 0) AS item_level, ii.randomPropertyId AS random_property_id, ii.enchantments
                FROM {characters}.characters c
                INNER JOIN {characters}.character_inventory ci
                    ON ci.guid = c.guid AND ci.bag = 0 AND ci.slot BETWEEN 0 AND 18
                INNER JOIN {characters}.item_instance ii ON ii.guid = ci.item AND ii.owner_guid = c.guid
                LEFT JOIN {world}.item_template it ON it.entry = ii.itemEntry
                LEFT JOIN {world}.item_template_locale loc ON loc.ID = it.entry AND loc.locale = 'frFR'
                WHERE c.account = @accountId AND c.guid IN ({string.Join(",", parameters)})
                ORDER BY c.guid, ci.slot
                LIMIT @maximumEquipmentRows;
                """;
            command.Parameters.AddWithValue("@accountId", accountId);
            command.Parameters.AddWithValue("@maximumEquipmentRows", ArmoryMaximumCharacters * ArmoryMaximumEquipmentSlots + 1);
            await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                uint owner = reader.GetUInt32("character_guid");
                if (!equipment.TryGetValue(owner, out List<ArmoryEquipment>? ownedEquipment))
                    throw new InvalidDataException("Equipment does not belong to the requested character set.");
                int slot = reader.GetInt32("slot");
                uint itemId = reader.GetUInt32("item_id");
                string enchantments = ReadArmoryText(reader, "enchantments", 512) ?? "";
                RequireArmoryData(ownedEquipment.Count < ArmoryMaximumEquipmentSlots && slot is >= 0 and <= 18
                    && ownedEquipment.All(item => item.Slot != slot) && itemId > 0
                    && ArmorySnapshotValidator.IsValidEnchantments(enchantments));
                ownedEquipment.Add(new ArmoryEquipment(slot, itemId, reader.GetUInt32("display_id"),
                    ReadArmoryText(reader, "name_en", 512) ?? $"Item #{itemId}", ReadArmoryText(reader, "name_fr", 512),
                    reader.GetInt32("quality"), reader.GetInt32("inventory_type"), reader.GetUInt32("item_level"),
                    reader.GetInt32("random_property_id"), enchantments));
            }
        }
        return new ArmoryRoster(ArmoryDate(observed), rows.Select(row => row with { Equipment = equipment[row.Character.Guid] }).ToArray());
    }

    private async Task<(bool Statistics, bool Snapshot)> ReadArmoryCapabilitiesAsync(
        MySqlConnection connection, MySqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using MySqlCommand command = CreateArmoryCommand(connection, transaction);
        command.CommandText = """
            SELECT TABLE_NAME, COLUMN_NAME FROM information_schema.columns
            WHERE TABLE_SCHEMA = @characterDatabase
              AND TABLE_NAME IN ('character_stats', 'atlas_armory_combat_snapshot')
            LIMIT 256;
            """;
        command.Parameters.AddWithValue("@characterDatabase", _options.CharacterDatabaseName);
        HashSet<string> fields = new(StringComparer.OrdinalIgnoreCase);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) fields.Add(reader.GetString(0) + "." + reader.GetString(1));
        bool statistics = new[] { "guid", "strength", "agility", "stamina", "intellect", "spirit", "armor", "maxhealth", "maxpower1" }
            .All(column => fields.Contains("character_stats." + column));
        bool snapshot = fields.Contains("atlas_armory_combat_snapshot.guid") && fields.Contains("atlas_armory_combat_snapshot.snapshot");
        return (statistics, snapshot);
    }

    private static MySqlCommand CreateArmoryCommand(MySqlConnection connection, MySqlTransaction transaction)
    {
        MySqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = ArmoryCommandTimeoutSeconds;
        return command;
    }

    private static string QuoteArmoryDatabase(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Invalid armory database configuration.");
        return $"`{name}`";
    }

    private static string? ReadArmoryText(MySqlDataReader reader, string field, int maximumLength)
    {
        if (reader.IsDBNull(field)) return null;
        string value = reader.GetString(field);
        RequireArmoryData(value.Length <= maximumLength);
        return value;
    }

    private static string ArmoryDate(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);

    private static void RequireArmoryData(bool condition)
    {
        if (!condition) throw new InvalidDataException("Armory data exceeds the supported contract.");
    }
}
