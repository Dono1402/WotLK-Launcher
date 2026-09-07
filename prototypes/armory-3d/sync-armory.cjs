const fs = require('node:fs/promises');
const path = require('node:path');
const {randomUUID} = require('node:crypto');
const {buildCombatStatistics,captureStatistics,equipmentKey,readRemote,writeJsonAtomic} = require('./capture-statistics.cjs');
const {readArmory,revisionDirectory} = require('./armory-cache.cjs');
const {enrichSnapshot,runExportStep,validateExport,buildCharacterExport} = require('./export-pipeline.cjs');
const {catalogQuery} = require('./legacy-armory-source.cjs');

const {dataRoot:output} = require('./runtime-paths.cjs');
const identityFields = ['guid','name','race','gender','classId'];

function snapshotForExport(baseline,current,verifiedAfter) {
  const raw = current?.snapshot;
  if (!raw) return null;
  if (identityFields.some(key => raw.character?.[key]!==baseline.character[key])) throw new Error('Only the authorized Flowmage identity can be synchronized');
  if (!Number.isInteger(raw.character.level) || raw.character.level<1 || raw.character.level>80) throw new Error('Invalid character level');
  for (const key of ['skin','face','hairStyle','hairColor','facialStyle']) if (!Number.isInteger(raw.character[key]) || raw.character[key]<0 || raw.character[key]>255) throw new Error('Invalid appearance');
  equipmentKey(raw.equipment);
  for (const item of raw.equipment) {
    for (const key of ['itemId','displayId','itemLevel','quality']) if (!Number.isSafeInteger(item[key]) || item[key]<0 || item[key]>0xffffffff) throw new Error('Invalid equipment');
    if (!item.itemId || !item.displayId || item.quality>7 || !Number.isSafeInteger(item.randomPropertyId) || Math.abs(item.randomPropertyId)>0x7fffffff) throw new Error('Invalid equipped instance');
  }
  if (!Number.isSafeInteger(raw.capturedAtMs) || raw.capturedAtMs<0 || raw.capturedAtMs>8.64e15) throw new Error('Invalid capture time');
  const snapshot = {schemaVersion:1,source:'atlas-armory-engine',capturedAtUtc:new Date(raw.capturedAtMs).toISOString().replace('T',' ').replace('Z',''),
    character:Object.fromEntries([...identityFields,'level','skin','face','hairStyle','hairColor','facialStyle'].map(key => [key,raw.character[key]])),
    equipment:raw.equipment.map(({slot,itemId,displayId,itemLevel,quality,randomPropertyId,enchantments}) => ({slot,itemId,displayId,itemLevel,quality,randomPropertyId,enchantments}))};
  if (buildCombatStatistics(snapshot,current,verifiedAfter).status!=='ready') return null;
  return snapshot;
}

async function syncArmory({host,key,verifiedAfter,clientRoot,outputDir=output,signal,force=false,read=readRemote,runStep=runExportStep}={}) {
  const current = await read(await fs.readFile(path.join(__dirname,'combat-statistics-readonly.sql'),'utf8'),{host,key,signal});
  signal?.throwIfAborted();
  const active = await readArmory(outputDir);
  const activeDir = revisionDirectory(active.revision,outputDir);
  const baseline = JSON.parse(await fs.readFile(path.join(activeDir,'flowmage.json'),'utf8'));
  const previous = await require('./statistics-cache.cjs').readStatistics(path.join(activeDir,'assets/statistics.json'));
  if (current.snapshot && Date.parse(previous.record?.savedAt)>current.snapshot.capturedAtMs) return {status:'unchanged',reason:'newer-cache-retained',savedAt:previous.record.savedAt};
  try {
    if (!force) return await captureStatistics({host,key,verifiedAfter,outputDir:activeDir,signal,read:async () => current});
  } catch (error) { if (error.code!=='ARMORY_REFRESH_REQUIRED') throw error; }
  if (!clientRoot || !path.isAbsolute(clientRoot)) throw new Error('A local game client is required for automatic export');
  const snapshot = snapshotForExport(baseline,current,verifiedAfter);
  if (!snapshot) return {status:'unavailable',reason:'missing-compatible-snapshot'};
  const catalog = await read(catalogQuery(await fs.readFile(path.join(__dirname,'catalog-readonly.sql'),'utf8'),snapshot.equipment),{host,key,signal});
  enrichSnapshot(snapshot,catalog);
  const revision = randomUUID().replaceAll('-','');
  const builds = path.join(outputDir,'builds');
  const stage = path.join(builds,revision);
  await fs.mkdir(stage,{recursive:true});
  try {
    const statistics = buildCombatStatistics(snapshot,current,verifiedAfter).record;
    await buildCharacterExport(stage,snapshot,catalog,statistics,{clientRoot,signal,runStep});
    signal?.throwIfAborted();
    if ((await readArmory(outputDir)).revision!==active.revision) throw new Error('Active armory changed during export');
    const latest = await require('./statistics-cache.cjs').readStatistics(path.join(activeDir,'assets/statistics.json'));
    if (Date.parse(latest.record?.savedAt)>Date.parse(statistics.savedAt)) throw new Error('A newer snapshot arrived during export');
    await fs.mkdir(path.join(outputDir,'snapshots'),{recursive:true});
    await fs.rename(stage,revisionDirectory(revision,outputDir));
    await writeJsonAtomic(path.join(outputDir,'armory-current.json'),{schemaVersion:1,revision},signal);
    return {status:'ready',savedAt:statistics.savedAt,revision};
  } catch (error) {
    const log = await fs.readFile(path.join(stage,'export.log'),'utf8').catch(() => '');
    await fs.writeFile(path.join(outputDir,'armory-export-error.log'),`${error.message}\n${log}`).catch(() => {});
    throw error;
  } finally {
    // This directory is created by this invocation; never remove an active or user-selected path.
    if (path.dirname(path.resolve(stage))!==path.resolve(builds) || !/^[a-f0-9]{32}$/.test(path.basename(stage))) throw new Error('Unsafe build cleanup');
    await fs.rm(stage,{recursive:true,force:true});
  }
}

if (require.main===module) (async () => {
  if (process.argv.length!==3 || process.argv[2]!=='--force') throw new Error('Usage: node sync-armory.cjs --force');
  const config = await require('./statistics-sync.cjs').readSyncConfig();
  if (!config) throw new Error('Enable the private local synchronization configuration first');
  console.log(JSON.stringify(await syncArmory({...config,force:true})));
})().catch(error => { console.error(error.message); process.exitCode=1; });

module.exports = {syncArmory,snapshotForExport,catalogQuery,enrichSnapshot,validateExport,buildCharacterExport};
