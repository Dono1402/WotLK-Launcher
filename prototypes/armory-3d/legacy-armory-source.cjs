// Development-only SQL transport. The distributable helper uses launcher-rpc.cjs.
const fs = require('node:fs/promises');
const path = require('node:path');
const {accountId} = require('./launcher-roster.cjs');
const {readRemote} = require('./capture-statistics.cjs');

function rosterQuery(account) {
  accountId(account);
  return `SET SESSION TRANSACTION READ ONLY;
START TRANSACTION WITH CONSISTENT SNAPSHOT;
SELECT JSON_OBJECT('observedAtUtc',UTC_TIMESTAMP(6),'characters',JSON_ARRAYAGG(JSON_OBJECT(
  'character',JSON_OBJECT('guid',c.guid,'name',c.name,'race',c.race,'classId',c.class,'gender',c.gender,
    'level',c.level,'skin',c.skin,'face',c.face,'hairStyle',c.hairStyle,'hairColor',c.hairColor,'facialStyle',c.facialStyle,
    'online',c.online,'zoneId',c.zone,'lastLogout',c.logout_time),
  'snapshot',a.snapshot,
  'values',IF(s.guid IS NULL,NULL,JSON_OBJECT('strength',s.strength,'agility',s.agility,'stamina',s.stamina,
    'intellect',s.intellect,'spirit',s.spirit,'armor',s.armor,'maxHealth',s.maxhealth,'maxMana',s.maxpower1)),
  'equipment',(SELECT JSON_ARRAYAGG(JSON_OBJECT('slot',i.slot,'itemId',ii.itemEntry,'displayId',it.displayid,
    'name',it.name,'nameFr',l.Name,'quality',it.Quality,'inventoryType',it.InventoryType,'itemLevel',it.ItemLevel,
    'randomPropertyId',ii.randomPropertyId,'enchantments',ii.enchantments))
    FROM arthas_chars.character_inventory i JOIN arthas_chars.item_instance ii ON ii.guid=i.item AND ii.owner_guid=c.guid
    JOIN arthas_world.item_template it ON it.entry=ii.itemEntry
    LEFT JOIN arthas_world.item_template_locale l ON l.ID=it.entry AND l.locale='frFR'
    WHERE i.guid=c.guid AND i.bag=0 AND i.slot BETWEEN 0 AND 18))))
FROM arthas_chars.characters c
LEFT JOIN arthas_chars.atlas_armory_combat_snapshot a ON a.guid=c.guid
LEFT JOIN arthas_chars.character_stats s ON s.guid=c.guid
WHERE c.account=${account};
ROLLBACK;`;
}

function catalogQuery(template,equipment) {
  const ids = [...new Set(equipment.map(item => item.itemId))];
  if (ids.length>19 || ids.some(id => !Number.isInteger(id) || id<1 || id>0xffffffff)) throw new Error('Invalid catalog item IDs');
  const marker = /\/\* EQUIPPED_IDS \*\/[\d,]+\/\* END_EQUIPPED_IDS \*\//;
  if (!marker.test(template)) throw new Error('Missing catalog query marker');
  return template.replace(marker,ids.length ? ids.join(',') : '0');
}


async function readRawRoster(account,config,read=readRemote) {
  return read(rosterQuery(account),config);
}

async function readCatalog(equipment,config,read=readRemote) {
  if (!equipment.length) return {items:[]};
  const template = await fs.readFile(path.join(__dirname,'catalog-readonly.sql'),'utf8');
  return read(catalogQuery(template,equipment),config);
}

module.exports = {rosterQuery,catalogQuery,readRawRoster,readCatalog};
