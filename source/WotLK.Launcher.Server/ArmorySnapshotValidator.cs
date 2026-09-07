using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WotLK.Launcher.Server;

internal static class ArmorySnapshotValidator
{
    internal const int MaximumBytes = 32768;
    private static readonly string[] AppearanceFields = ["level", "skin", "face", "hairStyle", "hairColor", "facialStyle"];
    private static readonly string[] BaseFields = ["strength", "agility", "stamina", "intellect", "spirit", "armor", "maxHealth", "maxMana"];
    private static readonly string[] CombatFields =
    [
        "attackPower", "rangedAttackPower", "meleeCritPct", "rangedCritPct", "meleeHitPct", "rangedHitPct",
        "spellHitPct", "meleeHastePct", "rangedHastePct", "spellHastePct", "expertise", "healingPower",
        "manaRegenCasting", "defenseSkill", "dodgePct", "parryPct", "blockPct", "resilience"
    ];
    private static readonly HashSet<string> SignedFields = new(StringComparer.Ordinal)
    {
        "meleeHitPct", "rangedHitPct", "spellHitPct", "meleeHastePct", "rangedHastePct", "spellHastePct",
        "healingPower", "spellPower"
    };

    internal static JsonElement? Sanitize(string? text, ArmoryCharacter owner, DateTime observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaximumBytes) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement root = document.RootElement;
            Require(root.GetProperty("schemaVersion").GetInt32() == 1
                && root.GetProperty("source").GetString() == "atlas-armory-engine");
            string? reason = root.GetProperty("reason").GetString();
            Require(reason is "logout" or "login" or "equipment" or "periodic");
            long captured = root.GetProperty("capturedAtMs").GetInt64();
            long observed = new DateTimeOffset(DateTime.SpecifyKind(observedAtUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            Require(captured >= 0 && captured <= observed && captured >= owner.LastLogout * 1000L);
            JsonElement identity = root.GetProperty("character");
            Require(identity.GetProperty("guid").GetUInt32() == owner.Guid
                && identity.GetProperty("name").GetString() == owner.Name
                && identity.GetProperty("race").GetByte() == owner.Race
                && identity.GetProperty("classId").GetByte() == owner.ClassId
                && identity.GetProperty("gender").GetByte() == owner.Gender);
            Dictionary<string, object?> character = new(StringComparer.Ordinal)
            {
                ["guid"] = owner.Guid, ["name"] = owner.Name, ["race"] = owner.Race,
                ["classId"] = owner.ClassId, ["gender"] = owner.Gender
            };
            foreach (string field in AppearanceFields) character[field] = identity.GetProperty(field).GetByte();
            Require((byte)character["level"]! is >= 1 and <= 80);

            JsonElement equipmentArray = root.GetProperty("equipment");
            Require(equipmentArray.ValueKind == JsonValueKind.Array && equipmentArray.GetArrayLength() <= 19);
            List<object> equipment = [];
            HashSet<int> slots = [];
            foreach (JsonElement item in equipmentArray.EnumerateArray())
            {
                int slot = item.GetProperty("slot").GetInt32();
                uint itemId = item.GetProperty("itemId").GetUInt32();
                uint displayId = item.GetProperty("displayId").GetUInt32();
                uint itemLevel = item.GetProperty("itemLevel").GetUInt32();
                uint quality = item.GetProperty("quality").GetUInt32();
                int randomPropertyId = item.GetProperty("randomPropertyId").GetInt32();
                string enchantments = item.GetProperty("enchantments").GetString() ?? "";
                Require(slot is >= 0 and <= 18 && slots.Add(slot) && itemId > 0 && quality <= 7
                    && IsValidEnchantments(enchantments));
                equipment.Add(new { slot, itemId, displayId, itemLevel, quality, randomPropertyId, enchantments });
            }

            JsonElement rawValues = root.GetProperty("values");
            Dictionary<string, double> values = new(StringComparer.Ordinal);
            foreach (string field in BaseFields) values[field] = Statistic(rawValues.GetProperty(field), field, integer: true);
            foreach (string field in CombatFields) values[field] = Statistic(rawValues.GetProperty(field), field);
            JsonElement schoolArray = root.GetProperty("schools");
            Require(schoolArray.ValueKind == JsonValueKind.Array && schoolArray.GetArrayLength() == 6);
            Dictionary<int, object> schools = [];
            foreach (JsonElement school in schoolArray.EnumerateArray())
            {
                int id = school.GetProperty("id").GetInt32();
                Require(id is >= 1 and <= 6 && !schools.ContainsKey(id));
                schools[id] = new
                {
                    id,
                    spellPower = Statistic(school.GetProperty("spellPower"), "spellPower", integer: true),
                    spellCritPct = Statistic(school.GetProperty("spellCritPct"), "spellCritPct")
                };
            }
            JsonElement talentArray = root.GetProperty("talentPoints");
            Require(talentArray.ValueKind == JsonValueKind.Array && talentArray.GetArrayLength() == 3);
            int[] talentPoints = talentArray.EnumerateArray().Select(value => (int)value.GetByte()).ToArray();
            byte form = root.GetProperty("form").GetByte();
            Require(root.GetProperty("includesTemporaryEffects").GetBoolean());
            // Re-project the allowed fields: unknown data in a stored JSON document is not exposed.
            return JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, source = "atlas-armory-engine", reason, capturedAtMs = captured,
                character, equipment, values, schools = schools.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray(),
                talentPoints, form, includesTemporaryEffects = true
            });
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException or OverflowException)
        {
            return null;
        }
    }

    internal static bool IsValidEnchantments(string text)
    {
        if (text.Length > 512) return false;
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 36 && tokens.All(token => uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static double Statistic(JsonElement element, string field, bool integer = false)
    {
        double value = element.GetDouble();
        Require(double.IsFinite(value) && Math.Abs(value) <= 1e9 && (SignedFields.Contains(field) || value >= 0)
            && (!integer || Math.Truncate(value) == value)
            && (!field.EndsWith("HastePct", StringComparison.Ordinal) || value > -100));
        return value;
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Invalid armory snapshot.");
    }
}
