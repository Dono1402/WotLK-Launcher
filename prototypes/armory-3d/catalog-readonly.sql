SET SESSION TRANSACTION READ ONLY;
START TRANSACTION WITH CONSISTENT SNAPSHOT;
SELECT JSON_OBJECT(
  'capturedAtUtc', UTC_TIMESTAMP(),
  'items', JSON_ARRAYAGG(JSON_OBJECT(
    'itemId', it.entry, 'displayId', it.displayid, 'quality', it.Quality, 'itemLevel', it.ItemLevel,
    'name', JSON_OBJECT('en', it.name, 'fr', loc.Name),
    'description', JSON_OBJECT('en', it.description, 'fr', loc.Description),
    'classId', it.class, 'subclassId', it.subclass, 'inventoryType', it.InventoryType,
    'requiredLevel', it.RequiredLevel, 'armor', it.armor, 'block', it.block,
    'bonding', it.bonding, 'maxDurability', it.MaxDurability, 'delay', it.delay,
    'damage', JSON_ARRAY(JSON_OBJECT('min',it.dmg_min1,'max',it.dmg_max1,'school',it.dmg_type1),
                         JSON_OBJECT('min',it.dmg_min2,'max',it.dmg_max2,'school',it.dmg_type2)),
    'stats', JSON_ARRAY(JSON_ARRAY(it.stat_type1,it.stat_value1),JSON_ARRAY(it.stat_type2,it.stat_value2),
                       JSON_ARRAY(it.stat_type3,it.stat_value3),JSON_ARRAY(it.stat_type4,it.stat_value4),
                       JSON_ARRAY(it.stat_type5,it.stat_value5),JSON_ARRAY(it.stat_type6,it.stat_value6),
                       JSON_ARRAY(it.stat_type7,it.stat_value7),JSON_ARRAY(it.stat_type8,it.stat_value8),
                       JSON_ARRAY(it.stat_type9,it.stat_value9),JSON_ARRAY(it.stat_type10,it.stat_value10)),
    'resistances', JSON_ARRAY(it.holy_res,it.fire_res,it.nature_res,it.frost_res,it.shadow_res,it.arcane_res),
    'spells', JSON_ARRAY(JSON_ARRAY(it.spellid_1,it.spelltrigger_1),JSON_ARRAY(it.spellid_2,it.spelltrigger_2),
                        JSON_ARRAY(it.spellid_3,it.spelltrigger_3),JSON_ARRAY(it.spellid_4,it.spelltrigger_4),
                        JSON_ARRAY(it.spellid_5,it.spelltrigger_5)),
    'sockets', JSON_ARRAY(it.socketColor_1,it.socketColor_2,it.socketColor_3), 'socketBonus',it.socketBonus,
    'scalingDistribution', it.ScalingStatDistribution, 'scalingValue', it.ScalingStatValue
  ))
)
FROM arthas_world.item_template it
LEFT JOIN arthas_world.item_template_locale loc ON loc.ID = it.entry AND loc.locale = 'frFR'
WHERE it.entry IN (/* EQUIPPED_IDS */3748,6096,6569,6392,14125,23413,23407,28156,6414,28303,6340,22980,5252/* END_EQUIPPED_IDS */);
ROLLBACK;
