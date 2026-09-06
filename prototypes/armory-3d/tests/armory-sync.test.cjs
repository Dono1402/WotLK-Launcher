const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {syncArmory,snapshotForExport,catalogQuery,validateExport} = require('../sync-armory.cjs');
const {readArmory,readActiveStatistics} = require('../armory-cache.cjs');
const {createServer,resolveFile} = require('../server.cjs');
const {combatDetails} = require('./fixtures/combat.cjs');
const original = path.resolve(__dirname,'../../../artifacts/armory-prototype');

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-armory-sync-'));
  t.after(async () => {
    assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));
    assert.ok(path.basename(root).startsWith('atlas-armory-sync-'));
    await fs.rm(root,{recursive:true,force:true});
  });
  const baseline = JSON.parse(await fs.readFile(path.join(original,'flowmage.json'),'utf8'));
  await fs.copyFile(path.join(original,'flowmage.json'),path.join(root,'flowmage.json'));
  await fs.cp(path.join(original,'assets'),path.join(root,'assets'),{recursive:true});
  const catalog = JSON.parse(await fs.readFile(path.join(original,'item-catalog.json'),'utf8'));
  for (const base of catalog.items) {
    const item = baseline.equipment.find(item => item.itemId===base.itemId);
    Object.assign(base,{displayId:item.displayId,quality:item.quality,itemLevel:item.itemLevel});
  }
  const current = {observedAtUtc:'2026-09-05 16:00:00.000000',snapshot:{schemaVersion:1,source:'atlas-armory-engine',reason:'logout',capturedAtMs:Date.parse('2026-09-05T15:00:00Z'),
    character:structuredClone(baseline.character),equipment:structuredClone(baseline.equipment),...combatDetails()}};
  const after = '2026-09-05T07:17:06Z';
  let steps = 0;
  const runStep = async (script,args,stage) => {
    steps++;
    if (script!=='item-details.cjs') return;
    await fs.cp(path.join(original,'assets'),path.join(stage,'assets'),{recursive:true});
    const next = JSON.parse(await fs.readFile(path.join(stage,'flowmage.json'),'utf8'));
    const character = JSON.parse(await fs.readFile(path.join(stage,'assets/character.json'),'utf8'));
    character.capturedAt = next.capturedAtUtc; character.level = next.character.level;
    character.equipment = character.equipment.filter(item => next.equipment.some(e => e.slot===item.slot));
    character.attached = character.attached.filter(item => next.equipment.some(e => e.slot===item.slot));
    const details = JSON.parse(await fs.readFile(path.join(stage,'assets/item-details.json'),'utf8'));
    details.characterCapturedAt = next.capturedAtUtc;
    details.items = character.equipment.map(item => ({...details.items.find(row => row.itemId===item.itemId),slot:item.slot}));
    await fs.writeFile(path.join(stage,'assets/character.json'),JSON.stringify(character));
    await fs.writeFile(path.join(stage,'assets/item-details.json'),JSON.stringify(details));
  };
  const read = async sql => sql.includes('atlas_armory_combat_snapshot') ? current : structuredClone(catalog);
  return {root,baseline,current,after,catalog,runStep,read,get steps() { return steps; },
    sync:options => syncArmory({outputDir:root,verifiedAfter:after,clientRoot:path.join(root,'client'),read,runStep,...options})};
}

test('export input accepts equipped changes but rejects another identity, future dates and invalid IDs',async t => {
  const f = await fixture(t);
  f.current.snapshot.equipment.pop();
  const next = snapshotForExport(f.baseline,f.current,f.after);
  assert.equal(next.equipment.length,12);
  assert.equal(next.capturedAtUtc,'2026-09-05 15:00:00.000');
  f.current.snapshot.character.name = 'SomeoneElse';
  assert.throws(() => snapshotForExport(f.baseline,f.current,f.after),/identity/);
  f.current.snapshot.character.name = 'Flowmage';
  f.current.snapshot.equipment[0].itemId = '1); DROP TABLE characters;';
  assert.throws(() => snapshotForExport(f.baseline,f.current,f.after),/equipment/);
  f.current.snapshot.equipment[0].itemId = 3748;
  f.current.snapshot.capturedAtMs += 7200000;
  assert.throws(() => snapshotForExport(f.baseline,f.current,f.after),/date/);
});

test('catalog query is limited to at most nineteen validated equipped IDs',async () => {
  const template = await fs.readFile(path.resolve(__dirname,'../catalog-readonly.sql'),'utf8');
  assert.match(catalogQuery(template,[{itemId:5},{itemId:7},{itemId:5}]),/WHERE it.entry IN \(5,7\)/);
  assert.match(catalogQuery(template,[]),/IN \(0\)/);
  for (const itemId of ['5 OR 1=1',NaN,-1,1.5,2**32]) assert.throws(() => catalogQuery(template,[{itemId}]),/IDs/);
  assert.throws(() => catalogQuery(template,Array.from({length:20},(_,i) => ({itemId:i+1}))),/IDs/);
});

test('statistic-only changes do not rebuild models or read the item catalog',async t => {
  const f = await fixture(t);
  let queries = 0;
  const result = await f.sync({read:async sql => { queries++; return f.read(sql); }});
  assert.equal(result.status,'ready');
  assert.equal(queries,1); assert.equal(f.steps,0);
  assert.equal((await readArmory(f.root)).revision,'legacy');
});

test('changed equipment is prepared privately and published as a complete version',async t => {
  const f = await fixture(t);
  f.current.snapshot.equipment = f.current.snapshot.equipment.filter(item => item.slot!==15);
  const legacy = await fs.readFile(path.join(f.root,'assets/character.json'),'utf8');
  let queries = 0;
  const result = await f.sync({read:async sql => { queries++; return f.read(sql); },runStep:async (...args) => {
    assert.equal((await readArmory(f.root)).revision,'legacy');
    assert.equal(await fs.readFile(path.join(f.root,'assets/character.json'),'utf8'),legacy);
    await f.runStep(...args);
  }});
  assert.match(result.revision,/^[a-f0-9]{32}$/); assert.equal(queries,2);
  const manifest = await readArmory(f.root);
  assert.equal(manifest.revision,result.revision);
  const next = JSON.parse(await fs.readFile(path.join(f.root,'snapshots',result.revision,'assets/character.json'),'utf8'));
  assert.equal(next.equipment.length,12); assert.equal(next.attached.some(item => item.slot===15),false);
  assert.equal(next.statistics.characterCapturedAt,next.capturedAt);
  assert.equal((await readActiveStatistics(f.root)).record.savedAt,'2026-09-05T15:00:00.000Z');
  assert.ok(!/"(guid|account|password|token|enchantments)"/.test(JSON.stringify(next)));
  assert.deepEqual(await fs.readdir(path.join(f.root,'builds')),[]);
  const steps = f.steps;
  assert.equal((await f.sync()).status,'unchanged'); assert.equal(f.steps,steps);
});

test('online equipment capture swaps the model once, then periodic stats reuse it',async t => {
  const f = await fixture(t);
  f.current.snapshot.reason = 'equipment';
  f.current.snapshot.character.online = 1;
  f.current.snapshot.equipment = f.current.snapshot.equipment.filter(item => item.slot!==15);
  const result = await f.sync();
  assert.equal(result.status,'ready');
  const steps = f.steps;
  for (const reason of ['periodic','login','equipment']) {
    f.current.snapshot.reason = reason;
    f.current.snapshot.capturedAtMs += 60000;
    f.current.snapshot.values.intellect++;
    const next = await f.sync();
    assert.equal(next.status,'ready');
    assert.equal(f.steps,steps);
    assert.equal((await readArmory(f.root)).revision,result.revision);
    assert.equal((await readActiveStatistics(f.root)).record.values.intellect,f.current.snapshot.values.intellect);
  }
  f.current.snapshot.capturedAtMs -= 60000;
  assert.equal((await f.sync()).reason,'newer-cache-retained');
  assert.equal((await readArmory(f.root)).revision,result.revision);
});

test('export failure or cancellation preserves the previous model and cleans only its staging directory',async t => {
  const f = await fixture(t);
  f.current.snapshot.equipment.pop();
  const old = await fs.readFile(path.join(f.root,'assets/statistics.json'),'utf8');
  await assert.rejects(f.sync({runStep:async () => { throw new Error('Missing local texture'); }}),/texture/);
  assert.equal((await readArmory(f.root)).revision,'legacy');
  assert.equal(await fs.readFile(path.join(f.root,'assets/statistics.json'),'utf8'),old);
  assert.deepEqual(await fs.readdir(path.join(f.root,'builds')),[]);
  const controller = new AbortController();
  await assert.rejects(f.sync({signal:controller.signal,runStep:async (...args) => { await f.runStep(...args); controller.abort(); }}),{name:'AbortError'});
  assert.equal((await readArmory(f.root)).revision,'legacy');
});

test('missing or unsafe model resources cannot become the current version',async t => {
  const f = await fixture(t);
  f.current.snapshot.equipment.pop();
  await assert.rejects(f.sync({runStep:async (...args) => {
    await f.runStep(...args);
    if (args[0]==='item-details.cjs') {
      const file = path.join(args[2],'assets/flowmage.gltf');
      const model = JSON.parse(await fs.readFile(file,'utf8'));
      model.buffers[0].uri = '../../flowmage.json';
      await fs.writeFile(file,JSON.stringify(model));
    }
  }}),/Unsafe/);
  assert.equal((await readArmory(f.root)).revision,'legacy');
});

test('published assets remain readable but private manifests, raw snapshots and caches are not exposed',async t => {
  const f = await fixture(t);
  f.current.snapshot.equipment.pop();
  const {revision} = await f.sync();
  const server = createServer({outputDir:f.root});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const base = `http://127.0.0.1:${server.address().port}`;
  const manifest = await (await fetch(base+'/armory.json')).json();
  assert.deepEqual(Object.keys(manifest),['revision','assetBase']);
  assert.equal((await fetch(base+manifest.assetBase+'character.json')).status,200);
  assert.equal((await fetch(base+'/assets/character.json')).status,200);
  for (const url of [`/snapshots/${revision}/flowmage.json`,manifest.assetBase+'statistics.json',manifest.assetBase+'STATISTICS.json','/armory-current.json','/builds/test/assets/character.json']) assert.equal((await fetch(base+url)).status,404);
  for (const url of [`/snapshots/${revision}/assets/%2e%2e/flowmage.json`,`/snapshots/${revision}/assets/%00.png`]) assert.equal(resolveFile(url,f.root),null);
});
