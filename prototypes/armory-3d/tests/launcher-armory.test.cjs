const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const {accountId,rosterQuery,normalizeRoster} = require('../launcher-roster.cjs');
const {LauncherArmory} = require('../launcher-armory.cjs');
const {createLauncherServer} = require('../launcher-server.cjs');
const {cachedIcons} = require('../launcher-icons.cjs');
const {createHash} = require('node:crypto');
const {combatDetails} = require('./fixtures/combat.cjs');

const observedAtUtc = '2026-09-05 16:00:00.000000';
const logout = Date.parse('2026-09-05T15:00:00Z')/1000;
const instance = (slot=0,itemId=100) => ({slot,itemId,displayId:101,quality:2,itemLevel:40,
  inventoryType:1,name:'Test hood',nameFr:'Capuche de test',randomPropertyId:0,enchantments:Array(36).fill(0).join(' ')});
function character(guid=1,name='Premier',equipment=[instance()]) {
  return {character:{guid,name,race:1,classId:1,gender:0,level:40,skin:0,face:0,hairStyle:0,hairColor:0,facialStyle:0,
    online:0,zoneId:12,lastLogout:logout},equipment,snapshot:null,
    values:{strength:50,agility:20,stamina:40,intellect:10,spirit:15,armor:100,maxHealth:600,maxMana:0}};
}
function capture(row,overrides={}) {
  return {schemaVersion:1,source:'atlas-armory-engine',reason:'periodic',capturedAtMs:(logout+60)*1000,
    character:structuredClone(row.character),equipment:structuredClone(row.equipment),...combatDetails(),...overrides};
}
const roster = (...characters) => ({observedAtUtc,characters});
const catalog = {items:[{itemId:100,displayId:101,quality:2,itemLevel:40,name:{en:'Test hood',fr:'Capuche de test'},
  description:{en:'',fr:''},classId:4,subclassId:1,inventoryType:1,requiredLevel:20,armor:15,block:0,bonding:1,
  stats:[[3,2]],damage:[],delay:0,resistances:[],spells:[],sockets:[],scalingDistribution:0,scalingValue:0}]};

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-launcher-armory-'));
  const armories = [];
  t.after(async () => {
    for (const armory of armories) await armory.stop();
    assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));
    assert.ok(path.basename(root).startsWith('atlas-launcher-armory-'));
    await fs.rm(root,{recursive:true,force:true});
  });
  return {root,create(account=1,options={}) {
    const armory = new LauncherArmory(account,{verifiedAfter:'2026-09-05T14:00:00Z'},
      {root,reference:async () => null,icons:async () => ({}),read:async sql => sql.includes('WHERE c.account=') ? roster(character()) : structuredClone(catalog),...options});
    armories.push(armory); return armory;
  }};
}

test('roster SQL is scoped to a validated account and includes characters without captures or items',() => {
  for (const value of [0,-1,1.5,NaN,Infinity,2**32,'1 OR 1=1']) assert.throws(() => accountId(value),/account/);
  assert.equal(accountId(0xffffffff),0xffffffff);
  assert.match(rosterQuery(42),/WHERE c.account=42;/);
  assert.match(rosterQuery(42),/LEFT JOIN arthas_chars.atlas_armory_combat_snapshot/);
  assert.doesNotMatch(rosterQuery(42),/Flowmage|c.name\s*=/);
  assert.deepEqual(normalizeRoster({observedAtUtc,characters:null}),[]);
  const rows = normalizeRoster(roster(character(1,'SansEquipement',[]),character(2,'Autre')));
  assert.equal(rows.length,2); assert.equal(rows.find(row => row.id==='1').snapshot.equipment.length,0);
});

test('combat captures support all characters, keep saved labels and sort online characters first',() => {
  const offline = character(1,'Premier');
  const online = character(2,'Deuxième'); online.character.online = 1; online.character.level = 20;
  online.snapshot = capture(online);
  delete online.snapshot.equipment[0].name; delete online.snapshot.equipment[0].nameFr;
  const [row] = normalizeRoster(roster(offline,online));
  assert.equal(row.id,'2'); assert.equal(row.statistics.characterName,'Deuxième');
  assert.equal(row.statistics.source,'arthas-combat-stats');
  assert.equal(row.snapshot.equipment[0].nameFr,'Capuche de test');
  online.snapshot = JSON.stringify(online.snapshot);
  assert.equal(normalizeRoster(roster(online))[0].statistics.characterName,'Deuxième');
});

test('bad, stale, foreign or unverified combat captures fall back without hiding other characters',() => {
  const source = character();
  const invalid = [capture(source,{source:'unknown'}),capture(source,{capturedAtMs:Date.parse('2026-09-06T00:00:00Z')}),
    capture(source,{character:{...source.character,guid:999}}),capture(source,{equipment:[{...instance(),enchantments:'bad'}]}),
    capture(source,{values:{}}),'{broken'];
  for (const snapshot of invalid) {
    const rows = normalizeRoster(roster({...source,snapshot},character(2,'Autre')));
    assert.equal(rows.length,2); assert.equal(rows.find(row => row.id==='1').statistics.source,'arthas-character-stats');
  }
  assert.equal(normalizeRoster(roster({...source,snapshot:capture(source)}),{verifiedAfter:'2026-09-05T15:30:00Z'})[0].statistics,null);
  source.character.lastLogout = 0;
  assert.equal(normalizeRoster(roster(source))[0].statistics,null);
});

test('all account characters remain consultable when model preparation or item catalogs are unavailable',async t => {
  const f = await fixture(t);
  const armory = f.create(12,{read:async sql => {
    if (sql.includes('WHERE c.account=12;')) return roster(character(),character(2,'SansEquipement',[]));
    throw new Error('Catalog temporarily unavailable');
  }});
  await armory.refresh();
  assert.deepEqual(armory.list().characters.map(row => row.available),[true,true]);
  assert.equal(armory.entry('1').modelReady,false);
  assert.equal(armory.entry('1').details.items[0].name.fr,'Capuche de test');
  assert.equal(armory.entry('1').details.items[0].incomplete,true);
  assert.equal(armory.entry('1').character.statistics.values.strength,50);
  assert.deepEqual(armory.entry('2').character.equipment,[]);
  assert.equal(armory.entry('999'),undefined);
});

test('incomplete item details retry and recover without changing character equipment',async t => {
  const f = await fixture(t);
  let available = false,queries = 0;
  const armory = f.create(1,{read:async sql => {
    if (sql.includes('WHERE c.account=')) return roster(character());
    queries++; if (!available) throw new Error('Offline'); return structuredClone(catalog);
  }});
  await armory.refresh(); const revision = armory.entry('1').revision;
  assert.equal(armory.entry('1').detailsComplete,false);
  available = true; await armory.refresh();
  assert.equal(armory.entry('1').detailsComplete,true);
  assert.equal(armory.entry('1').details.items[0].armor,15);
  assert.notEqual(armory.entry('1').revision,revision);
  await armory.refresh(); assert.equal(queries,2);
});

test('transferred characters and outdated equipment disappear before slow preparation completes',async t => {
  const f = await fixture(t);
  let rows = roster(character(),character(2,'Autre'));
  let prepare = async () => null;
  const armory = f.create(1,{read:async sql => sql.includes('WHERE c.account=') ? rows : structuredClone(catalog),reference:row => prepare(row)});
  await armory.refresh();
  rows = roster(character(2,'Autre',[]));
  let release,entered;
  const started = new Promise(resolve => { entered = resolve; });
  prepare = async () => { entered(); return new Promise(resolve => { release = resolve; }); };
  const refreshing = armory.refresh(); await started;
  assert.equal(armory.entry('1'),undefined);
  assert.deepEqual(armory.entry('2').character.equipment,[]);
  release(null); await refreshing;
  const manifest = JSON.parse(await fs.readFile(path.join(armory.root,'roster.json'),'utf8'));
  assert.deepEqual(manifest.characters.map(row => row.id),['2']);
});

test('persistent caches stay within their account and preserve roster entries without a prepared file',async t => {
  const f = await fixture(t);
  const first = f.create(1); await first.refresh(); await first.stop();
  const offline = async () => { throw new Error('Offline'); };
  const sameAccount = f.create(1,{read:offline}); await sameAccount.start(); await sameAccount.pending?.catch(() => {});
  assert.equal(sameAccount.list().status,'cached'); assert.equal(sameAccount.entry('1').owner,1);
  const anotherAccount = f.create(2,{read:offline}); await anotherAccount.start(); await anotherAccount.pending?.catch(() => {});
  assert.deepEqual(anotherAccount.list().characters,[]);
  await sameAccount.stop();
  await fs.unlink(path.join(first.root,'1.json'));
  const noEntry = f.create(1,{read:offline}); await noEntry.start(); await noEntry.pending?.catch(() => {});
  assert.equal(noEntry.list().characters.length,1); assert.equal(noEntry.list().characters[0].available,false);
});

test('cache files from another owner are never served even when placed in the selected account folder',async t => {
  const f = await fixture(t); const original = f.create(); await original.refresh(); await original.stop();
  const filename = path.join(original.root,'1.json');
  const cache = JSON.parse(await fs.readFile(filename,'utf8')); cache.owner = 2;
  await fs.writeFile(filename,JSON.stringify(cache));
  const armory = f.create(1,{read:async () => { throw new Error('Offline'); }});
  await armory.start(); await armory.pending?.catch(() => {});
  assert.equal(armory.entry('1'),undefined); assert.equal(armory.list().characters[0].available,false);
});

test('offline startup keeps character data but invalidates models from an older renderer or missing files',async t => {
  const f = await fixture(t); const first = f.create(); await first.refresh(); await first.stop();
  const filename = path.join(first.root,'1.json');
  const cached = JSON.parse(await fs.readFile(filename,'utf8'));
  for (const version of [1,2,3]) {
    const stale = {...cached,modelReady:true,assetDir:path.join(f.root,'missing-assets'),
      character:{...cached.character,renderSchemaVersion:version,attached:[{url:'old.gltf'}]}};
    await fs.writeFile(filename,JSON.stringify(stale));
    const offline = f.create(1,{read:async () => { throw new Error('Offline'); }});
    await offline.start(); await offline.pending?.catch(() => {});
    assert.equal(offline.entry('1').modelReady,false); assert.equal(offline.entry('1').assetDir,null);
    assert.equal(offline.entry('1').modelStatus,'unavailable');
    assert.equal(offline.entry('1').character.equipment.length,1);
    assert.equal(offline.entry('1').character.statistics.values.strength,50);
    await offline.stop();
  }
});

test('logout clears account data immediately and prevents late network responses from republishing it',async t => {
  const f = await fixture(t);
  let release; const response = new Promise(resolve => { release = resolve; });
  const armory = f.create(1,{read:async () => response});
  const pending = armory.refresh(); await armory.stop();
  release(roster(character())); await assert.rejects(pending,{name:'AbortError'});
  assert.deepEqual(armory.list(),{status:'unavailable',refreshing:false,characters:[]});
  assert.equal(armory.entry('1'),undefined);
  await assert.rejects(armory.start(),{name:'AbortError'});
});

test('compatible model revisions change with assets while statistic-only captures reuse the model',async t => {
  const f = await fixture(t); const row = character(); row.snapshot = capture(row);
  let assetDir = path.join(f.root,'assets-v1');
  const armory = f.create(1,{read:async () => roster(row),reference:async normalized => ({assetDir,
    character:{characterId:normalized.id,name:normalized.name,capturedAt:'2026-09-05 14:00:00.000',equipment:[],attached:[]},
    details:{characterCapturedAt:'2026-09-05 14:00:00.000',items:[]}})});
  await armory.refresh(); const revision = armory.entry('1').revision;
  row.snapshot.capturedAtMs += 60000; row.snapshot.values.strength++;
  await armory.refresh();
  assert.equal(armory.entry('1').revision,revision);
  assert.equal(armory.entry('1').character.statistics.values.strength,row.snapshot.values.strength);
  assetDir = path.join(f.root,'assets-v2'); await armory.refresh();
  assert.notEqual(armory.entry('1').revision,revision);
});

test('launcher HTTP bridge requires its secret and only exposes current owned public resources',async t => {
  const f = await fixture(t); const armory = f.create(); await armory.refresh();
  const key = 'a'.repeat(64); const server = createLauncherServer({key,armory});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const base = `http://127.0.0.1:${server.address().port}`;
  const get = (url,options={}) => fetch(base+url,{...options,headers:{'x-atlas-armory-key':key,...options.headers}});
  const rawStatus = (url,headers={}) => new Promise((resolve,reject) => {
    const request = http.get(base,{path:url,headers:{'x-atlas-armory-key':key,...headers}},response => { response.resume(); resolve(response.statusCode); });
    request.on('error',reject);
  });
  assert.equal((await fetch(base+'/characters.json')).status,403);
  for (const secret of ['b'.repeat(64),'é'.repeat(64),'short']) assert.equal((await get('/health.json',{headers:{'x-atlas-armory-key':secret}})).status,403);
  assert.equal(await rawStatus('/health.json',{Host:'example.com'}),403);
  assert.equal((await get('/characters.json',{method:'POST'})).status,405);
  const response = await get('/characters.json'); assert.equal(response.headers.get('cache-control'),'no-store');
  const publicRoster = await response.json(); assert.equal(publicRoster.characters[0].id,'1');
  const manifest = await (await get('/characters/1/armory.json')).json();
  assert.equal(manifest.modelReady,false);
  const publicCharacter = await (await get(manifest.assetBase+'character.json')).json();
  assert.equal(publicCharacter.characterId,'1');
  assert.doesNotMatch(JSON.stringify(publicCharacter),/"(?:owner|account|guid|enchantments|assetDir|fingerprint)"/);
  const head = await get('/characters.json',{method:'HEAD'}); assert.equal(await head.text(),''); assert.ok(Number(head.headers.get('content-length'))>0);
  for (const url of ['/characters/2/armory.json','/launcher-cache/1/roster.json','/flowmage.json',manifest.assetBase+'statistics.json',
    manifest.assetBase+'flowmage.gltf','/characters/1/snapshots/'+('0'.repeat(32))+'/assets/character.json','/three/../launcher-server.cjs']) {
    assert.equal((await get(url)).status,404,url);
  }
  // Raw paths ensure URL normalization cannot disguise traversal in this check.
  const status = await rawStatus('/characters/1/snapshots/'+manifest.revision+'/assets/%2e%2e/roster.json');
  assert.equal(status,404);
  await armory.stop(); assert.equal((await get('/characters/1/armory.json')).status,404);
});

test('embedded Inter faces require the private key and serve the exact native font bytes without exposing other files',async t => {
  const key = 'c'.repeat(64);
  const server = createLauncherServer({key,armory:{list:() => ({characters:[]})}});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const base = `http://127.0.0.1:${server.address().port}`;
  const headers = {'x-atlas-armory-key':key};
  const rawStatus = route => new Promise((resolve,reject) => {
    const request = http.get(base,{path:route,headers},response => { response.resume(); resolve(response.statusCode); });
    request.on('error',reject);
  });
  assert.equal((await fetch(base+'/inter-fonts.css')).status,403);
  assert.equal((await fetch(base+'/inter-fonts.css',{headers})).status,200);
  for (const face of ['Regular','Medium','SemiBold','ExtraBold']) {
    const route = `/fonts/Inter-${face}.ttf`;
    assert.equal((await fetch(base+route)).status,403,`${face}: anonymous font requests stay forbidden`);
    assert.equal((await fetch(base+route,{headers:{'x-atlas-armory-key':'d'.repeat(64)}})).status,403);
    const response = await fetch(base+route,{headers});
    assert.equal(response.status,200,face);
    assert.equal(response.headers.get('content-type'),'font/ttf');
    assert.equal(response.headers.get('x-content-type-options'),'nosniff');
    const native = await fs.readFile(path.resolve(__dirname,`../../../source/WotLK.Launcher/Assets/Fonts/Inter-${face}.ttf`));
    const served = Buffer.from(await response.arrayBuffer());
    assert.equal(createHash('sha256').update(served).digest('hex'),createHash('sha256').update(native).digest('hex'),
      `${face}: WebView must receive the same complete face used by WPF`);
    const head = await fetch(base+route,{headers,method:'HEAD'});
    assert.equal(head.status,200); assert.equal(head.headers.get('content-type'),'font/ttf');
    assert.equal(Number(head.headers.get('content-length')),native.length); assert.equal(await head.text(),'');
  }
  for (const route of ['/fonts/Inter-Bold.ttf','/fonts/Manrope-Regular.ttf','/fonts/Inter-Regular.ttf%00',
    '/fonts/../launcher-server.cjs','/fonts/%2e%2e/launcher-server.cjs',
    '/fonts/%2e%2e/%2e%2e/source/WotLK.Launcher/Assets/Fonts/Inter-Regular.ttf']) {
    assert.equal((await fetch(base+route)).status,403,`${route}: the key is required even for unknown paths`);
    assert.equal(await rawStatus(route),404,`${route}: authenticated requests cannot broaden the font allowlist`);
  }
});

test('verified equipment icons remain available without a compatible 3D model, including after offline restart',async t => {
  const f = await fixture(t);
  const assets = path.join(f.root,'assets'); await fs.mkdir(assets);
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=','base64');
  await fs.writeFile(path.join(assets,'icon-100.png'),png);
  await fs.writeFile(path.join(assets,'character.json'),JSON.stringify({equipment:[{...instance(),icon:'icon-100.png'}]}));
  const icons = row => cachedIcons(row.snapshot.equipment,f.root);
  const armory = f.create(1,{icons}); await armory.refresh();
  const state = armory.entry('1');
  assert.equal(state.modelReady,false); assert.equal(state.assetDir,null); assert.equal(state.character.equipment[0].icon,'icon-100.png');
  const key = 'b'.repeat(64); const server = createLauncherServer({key,armory});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const base = `http://127.0.0.1:${server.address().port}/characters/1/snapshots/${state.revision}/assets/`;
  const get = name => fetch(base+name,{headers:{'x-atlas-armory-key':key}});
  const response = await get('icon-100.png'); assert.equal(response.status,200); assert.equal(response.headers.get('content-type'),'image/png');
  assert.deepEqual(Buffer.from(await response.arrayBuffer()),png);
  for (const file of ['icon-999.png','flowmage.gltf','statistics.json','icons.json']) assert.equal((await get(file)).status,404);
  await armory.stop();
  const offline = f.create(1,{icons,read:async () => { throw new Error('Offline'); }});
  await offline.start(); await offline.pending?.catch(() => {});
  assert.equal(offline.list().status,'cached'); assert.equal(offline.entry('1').character.equipment[0].icon,'icon-100.png');
});

test('icon reuse requires exact item display metadata or hashed local-client provenance',async t => {
  const f = await fixture(t); const assets = path.join(f.root,'assets'); await fs.mkdir(assets);
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=','base64');
  await fs.writeFile(path.join(assets,'icon-100.png'),png);
  const metadata = path.join(assets,'character.json');
  await fs.writeFile(metadata,JSON.stringify({equipment:[{...instance(),displayId:999,icon:'icon-100.png'}]}));
  assert.deepEqual(await cachedIcons([instance()],f.root),{});
  await fs.writeFile(metadata,JSON.stringify({equipment:[{...instance(),icon:'../../private.png'}]}));
  assert.deepEqual(await cachedIcons([instance()],f.root),{});
  const extracted = path.join(f.root,'launcher-icons'); await fs.mkdir(extracted);
  await fs.writeFile(path.join(extracted,'icon-100.png'),png);
  const manifest = {schemaVersion:1,source:'local-client-item',build:'3.4.3.54261',items:[{itemId:100,iconFileDataId:123,
    icon:'icon-100.png',sha256:createHash('sha256').update(png).digest('hex')}]};
  await fs.writeFile(path.join(extracted,'icons.json'),JSON.stringify(manifest));
  assert.equal((await cachedIcons([instance()],f.root))['icon-100.png'],path.join(extracted,'icon-100.png'));
  await fs.writeFile(path.join(extracted,'icon-100.png'),Buffer.concat([png,Buffer.from('changed')]));
  assert.deepEqual(await cachedIcons([instance()],f.root),{});
});

test('cached roster failures recover through a bounded retry without overlapping reads or exposing errors',async t => {
  const f = await fixture(t); let now = 10000,mode = 'ready',calls = 0,release;
  const armory = f.create(1,{now:() => now,read:async sql => {
    if (!sql.includes('WHERE c.account=')) return structuredClone(catalog);
    calls++; if (mode==='offline') throw new Error('SSH secret must not be exposed');
    if (mode==='pending') return new Promise(resolve => { release = resolve; });
    return roster(character());
  }});
  await armory.poll(); mode = 'offline'; now += 5000;
  assert.equal(armory.retry(),true); await armory.pending?.catch(() => {});
  assert.equal(armory.list().status,'cached'); assert.equal(armory.lastFailure,'read-failed');
  assert.doesNotMatch(JSON.stringify(armory.list()),/SSH|secret/);
  assert.equal(armory.retry(),false);
  mode = 'pending'; now += 5000; assert.equal(armory.retry(),true);
  assert.equal(armory.list().refreshing,true); assert.equal(armory.retry(),false);
  const pending = armory.pending; release(roster(character(2,'Actualisé'))); await pending;
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(calls,3); assert.equal(armory.list().status,'ready'); assert.equal(armory.list().refreshing,false);
  assert.equal(armory.lastFailure,null); assert.equal(armory.entry('1'),undefined); assert.ok(armory.entry('2'));
});

test('successful live reads remain ready even when the local disk cache cannot be written',async t => {
  const f = await fixture(t); const armory = f.create();
  await fs.writeFile(armory.root,'A file blocks this cache directory');
  await armory.poll();
  assert.equal(armory.list().status,'ready'); assert.equal(armory.cacheFailure,'cache-write-failed');
  assert.equal(armory.list().characters[0].available,true); assert.equal(armory.entry('1').character.statistics.values.strength,50);
});

test('only an explicit authenticated GET retry starts a backend refresh and responds immediately',async t => {
  let retries = 0,refreshing = false;
  const key = 'c'.repeat(64);
  const server = createLauncherServer({key,armory:{list:() => ({status:'cached',refreshing,characters:[]}),
    retry:() => { retries++; refreshing=true; },entry:() => undefined}});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(async () => { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const base = `http://127.0.0.1:${server.address().port}`;
  const get = (url,method='GET') => fetch(base+url,{method,headers:{'x-atlas-armory-key':key}});
  await get('/characters.json'); await get('/characters.json?refresh=1','HEAD'); await get('/characters.json?refresh=0');
  assert.equal(retries,0);
  const response = await get('/characters.json?refresh=1');
  assert.equal(response.status,200); assert.equal((await response.json()).refreshing,true); assert.equal(retries,1);
});

test('automatic model export keeps all character data available while building and reuses the finished model',async t => {
  const f = await fixture(t); let release,entered,prepared,builds=0;
  const started = new Promise(resolve => { entered=resolve; });
  const armory = f.create(1,{reference:async () => prepared,prepareModel:async (row,details) => {
    builds++; entered(); await new Promise(resolve => { release=resolve; });
    prepared = {character:{...armory.entry(row.id).character,renderSchemaVersion:3},details,assetDir:f.root};
    return prepared;
  }});
  armory.config.clientRoot = path.join(f.root,'client');
  const polling = armory.poll(); await started;
  const pending = armory.entry('1');
  assert.equal(pending.modelReady,false); assert.equal(pending.modelStatus,'building');
  assert.equal(pending.character.statistics.values.strength,50);
  assert.equal(pending.details.items[0].name.fr,'Capuche de test');
  assert.equal(armory.list().characters[0].available,true); assert.equal(armory.retry(),false);
  release(); await polling;
  assert.equal(armory.entry('1').modelReady,true); assert.equal(armory.entry('1').modelStatus,'ready');
  await armory.refresh(); assert.equal(builds,1);
});

test('automatic model failures back off and logout discards a late completed model',async t => {
  const f = await fixture(t); let now=10000,builds=0;
  const armory = f.create(1,{now:() => now,prepareModel:async () => { builds++; throw new Error('Local model absent'); }});
  armory.config.clientRoot = path.join(f.root,'client');
  await armory.refresh(); assert.equal(builds,1); assert.equal(armory.entry('1').modelStatus,'unavailable');
  await armory.refresh(); assert.equal(builds,1); assert.equal(armory.list().status,'ready');
  now+=60000; await armory.refresh(); assert.equal(builds,2);
  let entered,release;
  const started = new Promise(resolve => { entered=resolve; });
  armory.prepareModel = async (row,details) => {
    const prepared = {character:armory.entry(row.id).character,details,assetDir:f.root};
    entered(); await new Promise(resolve => { release=resolve; }); return prepared;
  };
  now+=120000;
  const pending = armory.refresh(); await started;
  await armory.stop(); release();
  await assert.rejects(pending,{name:'AbortError'});
  assert.equal(armory.entry('1'),undefined); assert.deepEqual(armory.list().characters,[]);
});
