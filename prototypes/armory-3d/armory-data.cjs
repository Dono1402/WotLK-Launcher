const fs = require('node:fs/promises');
const path = require('node:path');
const {randomUUID} = require('node:crypto');
const {parseEnchantments} = require('./item-details.cjs');
const {sanitizeCombatDetails} = require('./combat-statistics.cjs');
const valueFields = ['strength','agility','stamina','intellect','spirit','armor','maxHealth','maxMana'];
const characterFields = ['guid','name','level','classId','race','gender','skin','face','hairStyle','hairColor','facialStyle'];

function equipmentKey(equipment) {
  if (!Array.isArray(equipment)) throw new Error('Missing equipment snapshot');
  const slots = new Set();
  return JSON.stringify(equipment.map(item => {
    if (!Number.isInteger(item.slot) || item.slot<0 || item.slot>18 || slots.has(item.slot)) throw new Error('Invalid equipment slots');
    slots.add(item.slot);
    return [item.slot,item.itemId,item.displayId,item.itemLevel,item.quality,item.randomPropertyId,
      ...parseEnchantments(item.enchantments).flatMap(e => [e.id,e.duration,e.charges])];
  }).sort((a,b) => a[0]-b[0]));
}

function buildCombatStatistics(baseline,current,verifiedAfter,{characterName='Flowmage'}={}) {
  if (!baseline?.capturedAtUtc || !characterName || baseline.character?.name!==characterName) throw new Error('Only the authorized character snapshot is supported');
  const snapshot = current?.snapshot;
  if (!snapshot) return {status:'unavailable',reason:'missing-combat-snapshot',record:null};
  if (snapshot.schemaVersion!==1 || snapshot.source!=='atlas-armory-engine' || !['logout','login','equipment','periodic'].includes(snapshot.reason)) throw new Error('Unknown combat collector');
  if (characterFields.some(key => baseline.character[key]!==snapshot.character?.[key])) throw Object.assign(new Error('Character changed: refresh the full armory snapshot first'),{code:'ARMORY_REFRESH_REQUIRED'});
  if (equipmentKey(baseline.equipment)!==equipmentKey(snapshot.equipment)) throw Object.assign(new Error('Equipment changed: refresh the full armory snapshot first'),{code:'ARMORY_REFRESH_REQUIRED'});
  const observed = Date.parse(current.observedAtUtc?.replace(' ','T')+'Z');
  if (!Number.isFinite(observed) || !Number.isSafeInteger(snapshot.capturedAtMs) || snapshot.capturedAtMs>observed) throw new Error('Invalid combat capture date');
  const cutoff = Date.parse(verifiedAfter);
  if (!Number.isFinite(cutoff) || snapshot.capturedAtMs<cutoff) return {status:'unavailable',reason:'new-capture-required-after-enabling-collection',record:null};
  return {status:'ready',record:{schemaVersion:2,source:'arthas-combat-stats',characterName,characterCapturedAt:baseline.capturedAtUtc,observedAt:new Date(observed).toISOString(),savedAt:new Date(snapshot.capturedAtMs).toISOString(),...sanitizeCombatDetails(snapshot)}};
}

async function writeJsonAtomic(target,value,signal) {
  const temporary = target+`.${process.pid}.${randomUUID()}.tmp`;
  try {
    signal?.throwIfAborted();
    await fs.writeFile(temporary,JSON.stringify(value,null,2),{flag:'wx'});
    signal?.throwIfAborted();
    await fs.rename(temporary,target);
  } finally { await fs.unlink(temporary).catch(error => { if (error.code!=='ENOENT') throw error; }); }
}


module.exports = {equipmentKey,buildCombatStatistics,writeJsonAtomic};
