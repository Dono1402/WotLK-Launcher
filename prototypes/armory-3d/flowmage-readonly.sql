SET SESSION TRANSACTION READ ONLY;
START TRANSACTION WITH CONSISTENT SNAPSHOT;
SELECT JSON_OBJECT(
    'schemaVersion', 1,
    'source', 'arthas-readonly',
    'capturedAtUtc', UTC_TIMESTAMP(),
    'character', JSON_OBJECT(
        'guid', c.guid, 'name', c.name, 'race', c.race,
        'classId', c.class, 'gender', c.gender, 'level', c.level,
        'skin', c.skin, 'face', c.face, 'hairStyle', c.hairStyle,
        'hairColor', c.hairColor, 'facialStyle', c.facialStyle,
        'playerFlags', c.playerFlags, 'lastLogout', c.logout_time
    ),
    'equipment', (
        SELECT JSON_ARRAYAGG(JSON_OBJECT(
            'slot', inv.slot, 'itemId', ii.itemEntry,
            'displayId', it.displayid, 'name', it.name,
            'quality', it.Quality, 'inventoryType', it.InventoryType,
            'itemLevel', it.ItemLevel, 'enchantments', ii.enchantments,
            'randomPropertyId', ii.randomPropertyId
        ))
        FROM arthas_chars.character_inventory inv
        JOIN arthas_chars.item_instance ii ON ii.guid = inv.item
        JOIN arthas_world.item_template it ON it.entry = ii.itemEntry
        WHERE inv.guid = c.guid AND inv.bag = 0 AND inv.slot BETWEEN 0 AND 18
    )
)
FROM arthas_chars.characters c
WHERE c.name = 'Flowmage';
ROLLBACK;
