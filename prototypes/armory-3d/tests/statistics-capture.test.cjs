const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {buildStatistics,equipmentKey,readRemote} = require('../capture-statistics.cjs');
const {readStatistics} = require('../statistics-cache.cjs');
const {createServer,resolveFile} = require('../server.cjs');

async function fixture() {
  const baseline = JSON.parse(await fs.readFile(path.resolve(__dirname,'../../../artifacts/armory-prototype/flowmage.json'),'utf8'));
  const current = {
    observedAtUtc:'2026-09-05 12:05:00.000000',
    character:{...baseline.character,online:0,lastLogout:Date.parse('2026-09-05T12:00:00Z')/1000},
    equipment:structuredClone(baseline.equipment),
    values:{strength:20,agility:30,stamina:40,intellect:150,spirit:60,armor:300,maxHealth:600,maxMana:1400}
  };
  return {baseline,current,after:'2026-09-05T11:00:00Z'};
}

test('native statistics are matched against character identity and full equipped instances',async () => {
  const {baseline,current,after} = await fixture();
  const result = buildStatistics(baseline,current,after);
  assert.equal(result.status,'ready');
  assert.equal(result.record.characterCapturedAt,baseline.capturedAtUtc);
  assert.equal(result.record.observedAt,'2026-09-05T12:05:00.000Z');
  assert.equal(result.record.savedAt,'2026-09-05T12:00:00.000Z');
  assert.equal(result.record.values.intellect,150);
  assert.equal(equipmentKey([...current.equipment].reverse()),equipmentKey(baseline.equipment));
  current.equipment[0].itemId++;
  assert.throws(() => buildStatistics(baseline,current,after),/Equipment changed/);
  current.equipment = structuredClone(baseline.equipment);
  current.character.level++;
  assert.throws(() => buildStatistics(baseline,current,after),/Character changed/);
});

test('absent, online or pre-activation statistics are not published as fresh totals',async () => {
  const {baseline,current,after} = await fixture();
  assert.equal(buildStatistics(baseline,{...current,values:null},after).reason,'missing-server-statistics');
  assert.equal(buildStatistics(baseline,{...current,character:{...current.character,online:1}},after).reason,'character-must-log-out');
  assert.equal(buildStatistics(baseline,current,'2026-09-05T12:01:00Z').reason,'new-logout-required-after-enabling-collection');
  assert.equal(buildStatistics(baseline,current,undefined).status,'unavailable');
  current.values.intellect = '150';
  assert.throws(() => buildStatistics(baseline,current,after),/Invalid saved statistic/);
});

test('native partial combat fields and private data are never exposed as totals',async () => {
  const {baseline,current,after} = await fixture();
  Object.assign(current.values,{spellPower:8,spellCritPct:0,spellHitPct:0,spellHastePct:0,token:'private',guid:123});
  const result = buildStatistics(baseline,current,after);
  for (const name of ['spellPower','spellCritPct','spellHitPct','spellHastePct','token','guid']) assert.equal(result.record.values[name],undefined);
  assert.ok(!/"(guid|token|password|email|enchantments)"/.test(JSON.stringify(result)));
  const {characterStatsRows} = await import('../character-stats.mjs');
  const rows = characterStatsRows({class:'Mage',capturedAt:baseline.capturedAtUtc,statistics:result.record},'fr');
  assert.equal(rows.filter(row => row.known).length,4);
  assert.equal(rows.find(row => row.key==='intellect').value,'150');
});

test('SSH capture rejects shell-like destinations before starting a process',() => {
  assert.throws(() => readRemote('SELECT 1;',{host:'user@host;whoami',key:'test'}),/explicit SSH/);
  assert.throws(() => readRemote('SELECT 1;',{host:'-oProxyCommand=test',key:'test'}),/explicit SSH/);
});

test('statistics endpoint handles missing cache, sanitized data and read-only access',async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-stats-test-'));
  const file = path.join(root,'statistics.json');
  const server = createServer({getStatistics:() => readStatistics(file)});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    assert.deepEqual(await (await fetch(base+'/statistics.json')).json(),{status:'unavailable',record:null});
    const {baseline,current,after} = await fixture();
    const {record} = buildStatistics(baseline,current,after);
    await fs.writeFile(file,JSON.stringify({...record,token:'private',values:{...record.values,password:'private'}}));
    assert.deepEqual((await (await fetch(base+'/statistics.json')).json()).record,record);
    assert.equal((await fetch(base+'/statistics.json',{method:'POST'})).status,405);
    assert.equal(await (await fetch(base+'/statistics.json',{method:'HEAD'})).text(),'');
    assert.equal(resolveFile('/assets/statistics.json'),null);
    assert.equal(resolveFile('/assets/STATISTICS.json'),null);
    assert.equal(resolveFile('/assets/%73tatistics.json'),null);
    assert.equal((await fetch(base+'/assets/statistics.json')).status,404);
    await fs.writeFile(file,'{"bad":true}');
    assert.equal((await fetch(base+'/statistics.json')).status,503);
  } finally {
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    await fs.unlink(file).catch(error => { if (error.code!=='ENOENT') throw error; });
    await fs.rmdir(root);
  }
});
