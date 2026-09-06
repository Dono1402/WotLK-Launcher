const fs = require('node:fs/promises');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {parseEnchantments} = require('./item-details.cjs');
const {sanitizeCombatDetails} = require('./combat-statistics.cjs');
const {readStatistics} = require('./statistics-cache.cjs');
const {equipmentKey,buildCombatStatistics,writeJsonAtomic} = require('./armory-data.cjs');

const {dataRoot:output} = require('./runtime-paths.cjs');
const valueFields = ['strength','agility','stamina','intellect','spirit','armor','maxHealth','maxMana'];
const characterFields = ['guid','name','level','classId','race','gender','skin','face','hairStyle','hairColor','facialStyle'];

function buildStatistics(baseline,current,verifiedAfter) {
  if (!baseline?.capturedAtUtc || baseline.character?.name!=='Flowmage') throw new Error('Only the authorized Flowmage snapshot is supported');
  if (characterFields.some(key => baseline.character[key]!==current?.character?.[key])) throw new Error('Character changed: refresh the full armory snapshot first');
  if (!current.values) return {status:'unavailable',reason:'missing-server-statistics',record:null};
  if (current.character.online!==0) return {status:'unavailable',reason:'character-must-log-out',record:null};
  const observed = Date.parse(current.observedAtUtc.replace(' ','T')+'Z');
  if (!Number.isFinite(observed) || !Number.isSafeInteger(current.character.lastLogout) || current.character.lastLogout*1000>observed) throw new Error('Invalid statistics capture date');
  const cutoff = Date.parse(verifiedAfter);
  if (!Number.isFinite(cutoff) || current.character.lastLogout*1000<cutoff) return {status:'unavailable',reason:'new-logout-required-after-enabling-collection',record:null};
  if (equipmentKey(baseline.equipment)!==equipmentKey(current.equipment)) throw new Error('Equipment changed: refresh the full armory snapshot first');
  const values = {};
  for (const key of valueFields) {
    const value = current.values[key];
    if (!Number.isSafeInteger(value) || value<0) throw new Error(`Invalid saved statistic: ${key}`);
    values[key] = value;
  }
  return {status:'ready',record:{
    schemaVersion:1,source:'arthas-character-stats',characterName:'Flowmage',
    characterCapturedAt:baseline.capturedAtUtc,
    observedAt:new Date(observed).toISOString(),savedAt:new Date(current.character.lastLogout*1000).toISOString(),
    values
  }};
}

function readRemote(sql,{host,key,timeoutMs=30000,signal}={}) {
  if (process.env.ATLAS_ARMORY_SOURCE==='rpc') throw new Error('SSH is disabled in public armory runtime');
  if (!/^[A-Za-z0-9_.-]+@[A-Za-z0-9.-]+$/.test(host || '') || !key) throw new Error('Provide an explicit SSH user@host and identity path');
  signal?.throwIfAborted();
  const remote = 'sudo -n docker exec -i arthas-mysql sh -c \'export MYSQL_PWD="$MYSQL_ROOT_PASSWORD"; exec mysql -uroot --default-character-set=utf8mb4 --batch --raw --skip-column-names\'';
  return new Promise((resolve,reject) => {
    const child = spawn('ssh.exe',['-i',key,'-o','IdentitiesOnly=yes','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ConnectTimeout=10',host,remote],{windowsHide:true,stdio:['pipe','pipe','pipe'],signal});
    let stdout = '', stderr = '', error;
    const timer = setTimeout(() => { error = new Error('Read-only statistics capture timed out'); child.kill(); },timeoutMs);
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data',chunk => {
      stdout += chunk;
      if (stdout.length>1024*1024) { error = new Error('Statistics response is too large'); child.kill(); }
    });
    child.stderr.on('data',chunk => { stderr = (stderr+chunk).slice(-8192); });
    child.on('error',err => { clearTimeout(timer); reject(err); });
    child.stdin.on('error',err => { error = err; });
    child.on('close',code => {
      clearTimeout(timer);
      if (error) reject(error);
      else if (code!==0) reject(new Error(`Read-only SSH query failed (${code}): ${stderr.trim()}`));
      else {
        try { resolve(JSON.parse(stdout.trim())); }
        catch { reject(new Error('The server did not return a single character snapshot')); }
      }
    });
    child.stdin.end(sql);
  });
}

async function captureStatistics({host,key,verifiedAfter,combat=true,outputDir=output,signal,read=readRemote}={}) {
  signal?.throwIfAborted();
  const baselineFile = path.join(outputDir,'flowmage.json');
  const baselineText = await fs.readFile(baselineFile,'utf8');
  const baseline = JSON.parse(baselineText);
  const sql = await fs.readFile(path.join(__dirname,combat?'combat-statistics-readonly.sql':'statistics-readonly.sql'),'utf8');
  const current = await read(sql,{host,key,signal});
  signal?.throwIfAborted();
  const result = combat ? buildCombatStatistics(baseline,current,verifiedAfter) : buildStatistics(baseline,current,verifiedAfter);
  if (result.status!=='ready') return result;
  const target = path.join(outputDir,'assets/statistics.json');
  const previous = (await readStatistics(target)).record;
  if (previous?.characterCapturedAt===result.record.characterCapturedAt) {
    if (previous.schemaVersion>result.record.schemaVersion || Date.parse(previous.savedAt)>Date.parse(result.record.savedAt)) {
      return {status:'unchanged',reason:'newer-cache-retained',savedAt:previous.savedAt};
    }
    const content = ({observedAt,...record}) => JSON.stringify(record);
    if (content(previous)===content(result.record)) return {status:'unchanged',savedAt:previous.savedAt};
  }
  if (await fs.readFile(baselineFile,'utf8')!==baselineText) throw Object.assign(new Error('Armory export changed during capture'),{code:'ARMORY_REFRESH_REQUIRED'});
  await writeJsonAtomic(target,result.record,signal);
  return {status:'ready',savedAt:result.record.savedAt,fields:Object.keys(result.record.values),file:target};
}

async function main(args=process.argv.slice(2)) {
  const combat = args.length===7 && args[6]==='--combat';
  if ((!combat && args.length!==6) || args[0]!=='--host' || args[2]!=='--key' || args[4]!=='--verified-after') throw new Error('Usage: node capture-statistics.cjs --host user@host --key path --verified-after ISO_DATE_OF_COLLECTION_ACTIVATION [--combat]');
  console.log(JSON.stringify(await captureStatistics({host:args[1],key:args[3],verifiedAfter:args[5],combat})));
}
if (require.main===module) main().catch(error => { console.error(error.message); process.exitCode=1; });
module.exports = {buildStatistics,buildCombatStatistics,equipmentKey,readRemote,captureStatistics,writeJsonAtomic};
