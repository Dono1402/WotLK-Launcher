const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {LauncherModelCache,renderSchemaVersion} = require('../launcher-models.cjs');

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-launcher-models-'));
  t.after(async () => {
    assert.equal(path.dirname(root),path.resolve(os.tmpdir()));
    assert.ok(path.basename(root).startsWith('atlas-launcher-models-'));
    await fs.rm(root,{recursive:true,force:true});
  });
  const row = {id:'42',name:'Autre',fingerprint:'a'.repeat(32),statistics:null,
    snapshot:{capturedAtUtc:'2026-09-05 15:00:00.000',
      character:{guid:42,name:'Autre',race:1,classId:1,gender:0,level:40,skin:0,face:0,hairStyle:0,hairColor:0,facialStyle:0},
      equipment:[]}};
  const details = {characterCapturedAt:row.snapshot.capturedAtUtc,items:[]};
  let builds = 0;
  const build = async (stage,snapshot,catalog,statistics,options) => {
    builds++;
    await fs.mkdir(path.join(stage,'assets'),{recursive:true});
    await fs.writeFile(path.join(stage,'flowmage.json'),JSON.stringify(snapshot));
    await fs.writeFile(path.join(stage,'assets/character.json'),JSON.stringify({name:snapshot.character.name,
      renderSchemaVersion,capturedAt:snapshot.capturedAtUtc,equipment:snapshot.equipment}));
    await fs.writeFile(path.join(stage,'assets/item-details.json'),JSON.stringify({...options.details,characterCapturedAt:snapshot.capturedAtUtc}));
    await fs.writeFile(path.join(stage,'assets/flowmage.gltf'),'{}');
  };
  const config = {clientRoot:path.join(root,'read-only-client')};
  return {root,row,details,build,config,get builds() { return builds; },
    create:options => new LauncherModelCache(root,config,{build,read:async () => { throw new Error('Empty equipment needs no catalog'); },...options})};
}

test('a complete generated character is reused across captures but never across appearance or renderer changes',async t => {
  const f = await fixture(t); const cache = f.create();
  assert.equal(await cache.reference(f.row),null);
  const first = await cache.prepare(f.row,f.details);
  assert.ok(first); assert.equal(f.builds,1);
  const changedStats = {...f.row,snapshot:{...f.row.snapshot,capturedAtUtc:'2026-09-05 15:01:00.000'},statistics:{values:{strength:99}}};
  assert.deepEqual(await cache.prepare(changedStats,f.details),first); assert.equal(f.builds,1);
  assert.equal(await cache.reference({...f.row,snapshot:{...f.row.snapshot,character:{...f.row.snapshot.character,skin:1}}}),null);
  const metadata = path.join(first.assetDir,'character.json');
  const original = JSON.parse(await fs.readFile(metadata,'utf8'));
  await fs.writeFile(metadata,JSON.stringify({...original,renderSchemaVersion:1}));
  assert.equal(await cache.reference(f.row),null);
  assert.ok(await cache.prepare(f.row,f.details)); assert.equal(f.builds,2);
  const model = path.join(first.assetDir,'flowmage.gltf');
  await fs.writeFile(model,JSON.stringify({buffers:[{uri:'missing.bin',byteLength:20}]}));
  assert.equal(await cache.reference(f.row),null,'A missing buffer must invalidate the cached model');
  assert.deepEqual(await fs.readdir(path.join(cache.root,'builds')),[]);
});

test('failed or cancelled builds cannot publish partial models or remove another cached character',async t => {
  const f = await fixture(t); const cache = f.create();
  const original = await cache.prepare(f.row,f.details);
  const next = {...f.row,fingerprint:'b'.repeat(32)};
  const broken = f.create({build:async stage => {
    await fs.writeFile(path.join(stage,'partial'),'incomplete');
    throw new Error('Missing local model');
  }});
  await assert.rejects(broken.prepare(next,f.details),/Missing local model/);
  assert.equal(await broken.reference(next),null);
  assert.deepEqual(await broken.reference(f.row),original);
  const controller = new AbortController();
  const cancelled = f.create({build:async (...args) => { await f.build(...args); controller.abort(); }});
  await assert.rejects(cancelled.prepare(next,f.details,controller.signal),{name:'AbortError'});
  assert.equal(await cancelled.reference(next),null);
  assert.deepEqual(await fs.readdir(path.join(cache.root,'builds')),[]);
  for (const row of [{...f.row,id:'../42'},{...f.row,fingerprint:'../escape'}]) assert.throws(() => cache.directory(row),/Invalid/);
});
