SET SESSION TRANSACTION READ ONLY;
START TRANSACTION WITH CONSISTENT SNAPSHOT;
SELECT JSON_OBJECT(
  'observedAtUtc', UTC_TIMESTAMP(6),
  'character', JSON_OBJECT(
    'guid', c.guid, 'name', c.name, 'level', c.level, 'classId', c.class,
    'race', c.race, 'gender', c.gender, 'skin', c.skin, 'face', c.face,
    'hairStyle', c.hairStyle, 'hairColor', c.hairColor, 'facialStyle', c.facialStyle,
    'online', c.online, 'lastLogout', c.logout_time
  ),
  'equipment', (
    SELECT JSON_ARRAYAGG(JSON_OBJECT(
      'slot', inv.slot, 'itemId', ii.itemEntry, 'displayId', it.displayid,
      'itemLevel', it.ItemLevel, 'quality', it.Quality,
      'randomPropertyId', ii.randomPropertyId, 'enchantments', ii.enchantments
    ))
    FROM arthas_chars.character_inventory inv
    JOIN arthas_chars.item_instance ii ON ii.guid=inv.item
    JOIN arthas_world.item_template it ON it.entry=ii.itemEntry
    WHERE inv.guid=c.guid AND inv.bag=0 AND inv.slot BETWEEN 0 AND 18
  ),
  'values', IF(s.guid IS NULL, NULL, JSON_OBJECT(
    'strength', s.strength, 'agility', s.agility, 'stamina', s.stamina,
    'intellect', s.intellect, 'spirit', s.spirit, 'armor', s.armor,
    'maxHealth', s.maxhealth, 'maxMana', s.maxpower1
  ))
)
FROM arthas_chars.characters c
LEFT JOIN arthas_chars.character_stats s ON s.guid=c.guid
WHERE c.name='Flowmage';
ROLLBACK;
