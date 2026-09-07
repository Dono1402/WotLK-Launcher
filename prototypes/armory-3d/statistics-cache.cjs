const fs = require('node:fs/promises');
const path = require('node:path');
const {sanitizeCombatDetails} = require('./combat-statistics.cjs');
const valueFields = ['strength','agility','stamina','intellect','spirit','armor','maxHealth','maxMana'];

async function readStatistics(file=path.join(require('./runtime-paths.cjs').dataRoot,'assets/statistics.json')) {
  let raw;
  try { raw = JSON.parse(await fs.readFile(file,'utf8')); }
  catch (error) { if (error.code==='ENOENT') return {status:'unavailable',record:null}; throw error; }
  const combat = raw.schemaVersion===2 && raw.source==='arthas-combat-stats';
  if ((!combat && (raw.schemaVersion!==1 || raw.source!=='arthas-character-stats')) || raw.characterName!=='Flowmage' || typeof raw.characterCapturedAt!=='string' || !raw.characterCapturedAt || !Number.isFinite(Date.parse(raw.savedAt)) || !Number.isFinite(Date.parse(raw.observedAt)) || Date.parse(raw.savedAt)>Date.parse(raw.observedAt)) throw new Error('Invalid statistics cache');
  if (combat) return {status:'ready',record:{schemaVersion:2,source:raw.source,characterName:raw.characterName,characterCapturedAt:raw.characterCapturedAt,observedAt:raw.observedAt,savedAt:raw.savedAt,...sanitizeCombatDetails(raw)}};
  const values = {};
  for (const key of valueFields) {
    if (!Number.isSafeInteger(raw.values?.[key]) || raw.values[key]<0) throw new Error('Invalid cached statistic');
    values[key] = raw.values[key];
  }
  return {status:'ready',record:{schemaVersion:1,source:raw.source,characterName:raw.characterName,characterCapturedAt:raw.characterCapturedAt,observedAt:raw.observedAt,savedAt:raw.savedAt,values}};
}
module.exports = {readStatistics};
