const {createHash} = require('node:crypto');
const {equipmentKey,buildCombatStatistics} = require('./armory-data.cjs');

function accountId(value) {
  if (!Number.isSafeInteger(value) || value<1 || value>0xffffffff) throw new Error('Invalid account');
  return value;
}

function rosterQuery(account) { return require('./legacy-armory-source.cjs').rosterQuery(account); }

const identity = ['guid','name','race','classId','gender'];
const appearance = ['level','skin','face','hairStyle','hairColor','facialStyle'];
function validateAppearance(character) {
  for (const key of ['race','classId','gender',...appearance]) if (!Number.isInteger(character[key]) || character[key]<0 || character[key]>255) throw new Error('Invalid character attributes');
  if (character.level<1 || character.level>80 || ![0,1].includes(character.gender)) throw new Error('Invalid character attributes');
}

function validateEquipment(equipment) {
  equipmentKey(equipment);
  for (const item of equipment) {
    for (const key of ['itemId','displayId','quality','itemLevel']) if (!Number.isSafeInteger(item[key]) || item[key]<0 || item[key]>0xffffffff) throw new Error('Invalid item');
    if (item.itemId===0 || !Number.isSafeInteger(item.randomPropertyId) || item.randomPropertyId< -0x80000000 || item.randomPropertyId>0x7fffffff) throw new Error('Invalid item instance');
  }
}

function normalizeRoster(raw,{verifiedAfter='1970-01-01T00:00:00Z'}={}) {
  const observed = Date.parse(raw?.observedAtUtc?.replace(' ','T')+'Z');
  if (!Number.isFinite(observed) || (raw.characters!==null && !Array.isArray(raw.characters))) throw new Error('Invalid roster');
  const ids = new Set();
  return (raw.characters || []).map(row => {
    const c = row.character;
    if (!c || !Number.isSafeInteger(c.guid) || c.guid<1 || c.guid>0xffffffff || ids.has(c.guid) || typeof c.name!=='string' || !c.name.length || c.name.length>24) throw new Error('Invalid character');
    ids.add(c.guid);
    validateAppearance(c);
    if (![0,1].includes(c.online) || !Number.isSafeInteger(c.lastLogout) || c.lastLogout<0 || c.lastLogout>0xffffffff || !Number.isSafeInteger(c.zoneId) || c.zoneId<0) throw new Error('Invalid presence');
    let equipment = row.equipment || [];
    let captured = c.lastLogout>0 && c.lastLogout*1000<=observed ? c.lastLogout*1000 : observed;
    let character = Object.fromEntries([...identity,...appearance].map(key => [key,c[key]]));
    let statistics = null;
    let live = row.snapshot;
    // The combat collector is optional; a malformed capture must not hide the account roster.
    try {
      if (typeof live==='string') live = JSON.parse(live);
      if (live) { validateAppearance(live.character); validateEquipment(live.equipment); }
    } catch { live = null; }
    if (live && identity.every(key => live.character?.[key]===c[key]) && Number.isSafeInteger(live.capturedAtMs)
        && live.capturedAtMs<=observed && live.capturedAtMs>=c.lastLogout*1000 && live.capturedAtMs>=Date.parse(verifiedAfter)) {
      try {
        const baseline = {character:live.character,equipment:live.equipment,capturedAtUtc:new Date(live.capturedAtMs).toISOString().replace('T',' ').replace('Z','')};
        statistics = buildCombatStatistics(baseline,{observedAtUtc:raw.observedAtUtc,snapshot:live},verifiedAfter,{characterName:c.name}).record;
        character = Object.fromEntries([...identity,...appearance].map(key => [key,live.character[key]]));
        // Preserve template labels from the same equipped instance when the collector omits them.
        equipment = live.equipment.map(item => ({...row.equipment?.find(saved => saved.slot===item.slot && saved.itemId===item.itemId),...item}));
        captured = live.capturedAtMs;
      } catch { statistics = null; }
    }
    validateEquipment(equipment);
    const capturedAtUtc = new Date(captured).toISOString().replace('T',' ').replace('Z','');
    if (!statistics && c.online===0 && c.lastLogout>0 && c.lastLogout*1000<=observed && c.lastLogout*1000>=Date.parse(verifiedAfter) && row.values) {
      const keys = ['strength','agility','stamina','intellect','spirit','armor','maxHealth','maxMana'];
      if (keys.every(key => Number.isSafeInteger(row.values[key]) && row.values[key]>=0)) statistics = {
        schemaVersion:1,source:'arthas-character-stats',characterName:c.name,characterCapturedAt:capturedAtUtc,
        savedAt:new Date(captured).toISOString(),observedAt:new Date(observed).toISOString(),
        values:Object.fromEntries(keys.map(key => [key,row.values[key]]))
      };
    }
    const snapshot = {schemaVersion:1,source:'arthas-readonly',capturedAtUtc,character,equipment};
    const fingerprint = createHash('sha256').update(JSON.stringify([character,equipmentKey(equipment)])).digest('hex').slice(0,32);
    return {id:String(c.guid),name:c.name,classId:c.classId,race:c.race,gender:c.gender,level:character.level,
      online:c.online===1,zoneId:c.zoneId,lastSeenAt:c.lastLogout?new Date(c.lastLogout*1000).toISOString():null,
      snapshot,statistics,fingerprint};
  }).sort((a,b) => Number(b.online)-Number(a.online) || b.level-a.level || a.name.localeCompare(b.name));
}

function summary(row) {
  const {id,name,classId,race,gender,level,online,zoneId,lastSeenAt} = row;
  return {id,name,classId,race,gender,level,online,zoneId,lastSeenAt};
}

async function readRoster(account,config,read) {
  return normalizeRoster(await require('./legacy-armory-source.cjs').readRawRoster(account,config,read),{verifiedAfter:config?.verifiedAfter});
}

module.exports = {accountId,rosterQuery,normalizeRoster,summary,readRoster};
