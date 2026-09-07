const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {LauncherArmory} = require('../launcher-armory.cjs');
const {createLauncherServer} = require('../launcher-server.cjs');
const {normalizeRoster} = require('../launcher-roster.cjs');
const {renderSchemaVersion} = require('../launcher-models.cjs');

const raw = () => ({observedAtUtc:'2026-09-06 12:00:00.000',characters:[{
  character:{guid:42,name:'Ami',race:1,classId:1,gender:0,level:40,skin:0,face:0,hairStyle:0,hairColor:0,
    facialStyle:0,online:0,zoneId:12,lastLogout:1788692400},equipment:[],snapshot:null,
  values:{strength:50,agility:20,stamina:40,intellect:10,spirit:15,armor:100,maxHealth:600,maxMana:0}
}]});

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-friend-persistent-'));
  const armories = [];
  let references=0,builds=0;
  const create = loadRoster => {
    const armory = new LauncherArmory(1918,{source:'rpc',requireFreshRoster:true,clientRoot:path.join(root,'client')},{
      root,loadRoster,loadCatalog:async () => ({items:[]}),icons:async () => ({}),
      reference:async row => { references++; return armory.models.reference(row); },
      prepareModel:async () => { builds++; return null; }
    });
    armories.push(armory); return armory;
  };
  t.after(async () => {
    for (const armory of armories) await armory.stop();
    assert.equal(path.dirname(root),path.resolve(os.tmpdir()));
    assert.match(path.basename(root),/^atlas-friend-persistent-/);
    await fs.rm(root,{recursive:true,force:true});
  });
  // Publish an immutable model fixture using the production cache key. All
  // subsequent instances use the real disk reader and HTTP access checks.
  const first = create(async () => raw());
  const row = normalizeRoster(raw())[0];
  const model = first.models.directory(row);
  const assets = path.join(model,'assets');
  await fs.mkdir(assets,{recursive:true});
  await fs.writeFile(path.join(model,'flowmage.json'),JSON.stringify(row.snapshot));
  await fs.writeFile(path.join(assets,'character.json'),JSON.stringify({name:'Ami',level:40,
    renderSchemaVersion,capturedAt:row.snapshot.capturedAtUtc,equipment:[],attached:[]}));
  await fs.writeFile(path.join(assets,'item-details.json'),JSON.stringify({characterCapturedAt:row.snapshot.capturedAtUtc,items:[]}));
  await fs.writeFile(path.join(assets,'flowmage.gltf'),JSON.stringify({asset:{version:'2.0'},buffers:[{uri:'mesh.bin',byteLength:4}]}));
  await fs.writeFile(path.join(assets,'mesh.bin'),Buffer.from([1,2,3,4]));
  await first.refresh();
  assert.equal(first.entry('42').modelReady,true);
  await first.stop();
  references=0; builds=0;
  return {create,get references() { return references; },get builds() { return builds; }};
}

async function serve(t,armory) {
  const key = 'd'.repeat(64);
  const server = createLauncherServer({key,armory});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  return route => fetch(`http://127.0.0.1:${server.address().port}${route}`,{headers:{'x-atlas-armory-key':key}});
}

test('a reopened friend model waits for fresh authorization and uses current statistics without rebuilding',async t => {
  const f = await fixture(t);
  let release;
  const response = new Promise(resolve => { release=resolve; });
  const reopened = f.create(async () => response);
  const get = await serve(t,reopened);
  await reopened.start();
  assert.equal(f.references,0);
  assert.deepEqual((await (await get('/characters.json')).json()).characters,[]);
  assert.equal((await get('/characters/42/armory.json')).status,404);
  const fresh = raw();
  fresh.characters[0].values.strength = 75;
  fresh.characters[0].character.lastLogout += 60;
  release(fresh); await reopened.pending;
  const manifest = await (await get('/characters/42/armory.json')).json();
  assert.equal(manifest.modelReady,true);
  assert.equal(f.references,1); assert.equal(f.builds,0);
  assert.deepEqual(Buffer.from(await (await get(manifest.assetBase+'mesh.bin')).arrayBuffer()),Buffer.from([1,2,3,4]));
  const statistics = await (await get('/characters/42/statistics.json')).json();
  assert.equal(statistics.record.values.strength,75);
  assert.equal(statistics.record.savedAt,new Date(fresh.characters[0].character.lastLogout*1000).toISOString());
});

test('a persisted friend model stays inaccessible after rejected authorization or changed appearance and equipment',async t => {
  const f = await fixture(t);
  const rejected = f.create(async () => { throw Object.assign(new Error('No longer a friend'),{code:'ARMORY_RPC_UNAUTHORIZED'}); });
  const get = await serve(t,rejected);
  await rejected.start(); await rejected.pending?.catch(() => {});
  assert.equal(f.references,0); assert.equal(f.builds,0);
  assert.equal((await get('/characters/42/armory.json')).status,404);
  assert.deepEqual((await (await get('/characters.json')).json()).characters,[]);
  const changed = raw(); changed.characters[0].character.skin=1;
  const reopened = f.create(async () => changed);
  await reopened.start(); await reopened.pending;
  assert.equal(reopened.entry('42').modelReady,false);
  assert.equal(reopened.entry('42').assetDir,null);
  assert.equal(f.builds,1,'a changed appearance must prepare a new model');
  const equipped = raw();
  equipped.characters[0].equipment = [{slot:0,itemId:100,displayId:101,quality:2,itemLevel:40,
    randomPropertyId:0,enchantments:Array(36).fill(0).join(' ')}];
  const newEquipment = f.create(async () => equipped);
  await newEquipment.start(); await newEquipment.pending;
  assert.equal(newEquipment.entry('42').modelReady,false);
  assert.equal(newEquipment.entry('42').assetDir,null);
  assert.equal(f.builds,2,'changed equipment must prepare a new model');
});
