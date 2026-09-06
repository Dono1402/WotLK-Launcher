const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const {spawn,execFileSync} = require('node:child_process');
const {readRuntimePaths} = require('../runtime-paths.cjs');
const source = path.resolve(__dirname,'..');
const appFiles = ['launcher-server.cjs','launcher-rpc.cjs','runtime-paths.cjs','launcher-armory.cjs','launcher-roster.cjs',
  'launcher-models.cjs','launcher-icons.cjs','armory-data.cjs','armory-cache.cjs','statistics-cache.cjs',
  'combat-statistics.cjs','item-details.cjs','export-pipeline.cjs','viewer-assets.cjs','local-client.cjs',
  'launcher.html','launcher.js','launcher.css','index.html','app.js','i18n.mjs','character-stats.mjs','character-labels.mjs',
  'style.css','inter-fonts.css'];

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-public-runtime-'));
  const result = {};
  t.after(async () => {
    await result.beforeCleanup?.();
    assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));
    assert.ok(path.basename(root).startsWith('atlas-public-runtime-'));
    await fs.rm(root,{recursive:true,force:true});
  });
  const app = path.join(root,'package','app'),data = path.join(root,'user-data'),assets = path.join(root,'package','assets'),
    metadata = path.join(root,'package','metadata'),vendor = path.join(root,'package','vendor');
  for (const directory of [app,data,assets,metadata,vendor]) await fs.mkdir(directory,{recursive:true});
  await Promise.all(appFiles.map(file => fs.copyFile(path.join(source,file),path.join(app,file))));
  const environment = {...process.env,ATLAS_ARMORY_SOURCE:'rpc',ATLAS_ARMORY_ACCOUNT_ID:'42',ATLAS_ARMORY_BRIDGE_KEY:'a'.repeat(64),
    ATLAS_ARMORY_DATA_ROOT:data,ATLAS_ARMORY_VENDOR_ROOT:vendor,ATLAS_ARMORY_ASSET_ROOT:assets,ATLAS_ARMORY_METADATA_ROOT:metadata};
  delete environment.ATLAS_ARMORY_CLIENT_ROOT; delete environment.ARMORY_EXPORT_DIR;
  return Object.assign(result,{root,app,data,assets,metadata,vendor,environment});
}

test('public runtime paths require explicit absolute roots and never inherit developer folders',() => {
  const root = path.resolve(os.tmpdir(),'atlas-paths');
  const environment = {ATLAS_ARMORY_SOURCE:'rpc',ATLAS_ARMORY_DATA_ROOT:path.join(root,'data'),ATLAS_ARMORY_VENDOR_ROOT:path.join(root,'vendor'),
    ATLAS_ARMORY_ASSET_ROOT:path.join(root,'assets'),ATLAS_ARMORY_METADATA_ROOT:path.join(root,'metadata')};
  const paths = readRuntimePaths(environment);
  assert.equal(paths.publicMode,true); assert.equal(paths.outputRoot,environment.ATLAS_ARMORY_DATA_ROOT); assert.equal(paths.clientRoot,undefined);
  assert.doesNotMatch(JSON.stringify(paths),/armory-prototype|Documents/);
  for (const key of ['ATLAS_ARMORY_DATA_ROOT','ATLAS_ARMORY_VENDOR_ROOT','ATLAS_ARMORY_ASSET_ROOT','ATLAS_ARMORY_METADATA_ROOT']) {
    assert.throws(() => readRuntimePaths({...environment,[key]:undefined}),/Missing/);
    assert.throws(() => readRuntimePaths({...environment,[key]:'relative/path'}),/Invalid/);
    assert.throws(() => readRuntimePaths({...environment,[key]:root+'\0bad'}),/Invalid/);
  }
  assert.throws(() => readRuntimePaths({...environment,ATLAS_ARMORY_SOURCE:'ssh'}),/Invalid armory/);
  assert.equal(readRuntimePaths({...environment,ARMORY_EXPORT_DIR:path.join(root,'stage')}).outputRoot,path.join(root,'stage'));
});

test('public metadata is read-only, unavailable metadata never downloads, and SSH fails closed',async t => {
  const f = await fixture(t);
  await fs.writeFile(path.join(f.metadata,'manifest.json'),'[{"tableName":"test"}]');
  const script = `const assert=require('node:assert/strict');global.fetch=()=>{throw new Error('Unexpected network')};
    (async()=>{const {cachedDownload}=require('./local-client.cjs');
    assert.equal((await cachedDownload('https://example.invalid/manifest.json','manifest.json')).toString(),'[{"tableName":"test"}]');
    await assert.rejects(cachedDownload('https://example.invalid/missing.json','missing.json'),/Packaged client metadata missing/);
    await assert.rejects(cachedDownload('https://example.invalid/bad.json','../bad.json'),/Invalid metadata/);
    const {readRemote}=require(${JSON.stringify(path.join(source,'capture-statistics.cjs'))});
    assert.throws(()=>readRemote('SELECT 1',{host:'nobody@example.invalid',key:'unused'}),/SSH is disabled/);
    console.log('PUBLIC_METADATA_OK');})().catch(()=>process.exitCode=1);`;
  const output = execFileSync(process.execPath,['-e',script],{cwd:f.app,env:f.environment,encoding:'utf8',windowsHide:true,timeout:10000});
  assert.match(output,/PUBLIC_METADATA_OK/); assert.deepEqual(await fs.readdir(f.metadata),['manifest.json']);
  assert.deepEqual(await fs.readdir(f.data),[]);
});

test('isolated packaged server serves authenticated RPC data and relocated assets without client, SSH or private snapshots',async t => {
  const f = await fixture(t);
  const staticFiles = [['Fonts/Inter-Regular.ttf','font-marker'],['Launcher/class-icons/1.jpg','icon-marker'],['Launcher/visuals/icecrown-citadel.png','banner-marker']];
  for (const [file,body] of staticFiles) { await fs.mkdir(path.dirname(path.join(f.assets,file)),{recursive:true}); await fs.writeFile(path.join(f.assets,file),body); }
  const child = spawn(process.execPath,[path.join(f.app,'launcher-server.cjs')],{cwd:f.root,env:f.environment,windowsHide:true,stdio:['pipe','pipe','pipe']});
  let stderr = '',buffer = '',port; const requests = [];
  child.stderr.on('data',chunk => { stderr += chunk; });
  const closed = new Promise(resolve => child.once('close',(code,signal) => resolve({code,signal})));
  f.beforeCleanup = async () => { if (child.exitCode===null) { child.kill(); await closed; } };
  const character = (guid,name,equipment=[]) => ({character:{guid,name,race:1,classId:1,gender:0,level:40,skin:0,face:0,hairStyle:0,hairColor:0,facialStyle:0,
    online:0,zoneId:12,lastLogout:Math.floor(Date.parse('2026-09-05T15:00:00Z')/1000)},equipment,snapshot:null,
    values:{strength:50,agility:20,stamina:40,intellect:10,spirit:15,armor:100,maxHealth:600,maxMana:0}});
  const item = {slot:0,itemId:100,displayId:101,quality:2,itemLevel:40,inventoryType:1,name:'Test hood',nameFr:'Capuche',randomPropertyId:0,enchantments:Array(36).fill(0).join(' ')};
  const catalog = {items:[{itemId:100,displayId:101,quality:2,itemLevel:40,name:{en:'Test hood',fr:'Capuche'},description:{en:'',fr:''},
    classId:4,subclassId:1,inventoryType:1,requiredLevel:20,armor:15,block:0,bonding:1,stats:[[3,2]],damage:[],delay:0,resistances:[],spells:[],sockets:[],scalingDistribution:0,scalingValue:0}]};
  child.stdout.on('data',chunk => {
    buffer += chunk;
    let newline;
    while ((newline=buffer.indexOf('\n'))>=0) {
      const line = buffer.slice(0,newline); buffer = buffer.slice(newline+1);
      if (line.startsWith('ATLAS_ARMORY_READY ')) port = JSON.parse(line.slice(19)).port;
      else if (line.startsWith('ATLAS_ARMORY_REQUEST ')) {
        const request = JSON.parse(line.slice(21)); requests.push(request);
        const result = request.operation==='roster' ? {observedAtUtc:'2026-09-05 16:00:00.000000',characters:[character(7,'Équipé',[item]),character(8,'SansObjet')]} : catalog;
        child.stdin.write(JSON.stringify({id:request.id,result})+'\n');
      }
    }
  });
  async function until(check) {
    const deadline = Date.now()+10000;
    while (Date.now()<deadline) { if (await check()) return; if (child.exitCode!==null) throw new Error('Packaged server exited: '+stderr); await new Promise(resolve => setTimeout(resolve,15)); }
    throw new Error('Packaged server timed out: '+stderr);
  }
  await until(() => port);
  const base = `http://127.0.0.1:${port}`,headers = {'x-atlas-armory-key':f.environment.ATLAS_ARMORY_BRIDGE_KEY};
  const get = async url => fetch(base+url,{headers});
  await until(async () => { const response = await (await get('/characters.json')).json(); return response.status==='ready' && !response.refreshing; });
  assert.deepEqual(requests,[{id:1,operation:'roster'},{id:2,operation:'catalog',characterId:7}]);
  const characters = await (await get('/characters.json')).json(); assert.equal(characters.characters.length,2);
  assert.ok(characters.characters.every(row => row.available));
  const armory = await (await get('/characters/7/armory.json')).json(); assert.equal(armory.modelReady,false); assert.equal(armory.modelStatus,'client-missing');
  const details = await (await get(armory.assetBase+'item-details.json')).json(); assert.equal(details.items[0].armor,15);
  const stats = await (await get('/characters/7/statistics.json')).json(); assert.equal(stats.record.values.strength,50);
  assert.equal((await fetch(base+'/characters.json')).status,403);
  assert.equal(await new Promise((resolve,reject) => {
    http.get(base+'/characters.json',{headers:{...headers,Host:'evil.invalid'}},response => {
      response.resume(); resolve(response.statusCode);
    }).on('error',reject);
  }),403);
  assert.equal((await fetch(base+'/characters.json',{headers,method:'POST'})).status,405);
  for (const url of ['/flowmage.json','/item-catalog.json','/statistics-sync.json','/characters/999/view','/assets/character.json']) assert.equal((await get(url)).status,404);
  for (const [url,marker] of [['/fonts/Inter-Regular.ttf','font-marker'],['/class-icons/1.jpg','icon-marker'],['/banner.png','banner-marker']]) assert.equal(await (await get(url)).text(),marker);
  assert.equal((await get('/')).status,200); assert.equal((await get('/characters/7/view')).status,200);
  const accountCache = path.join(f.data,'launcher-cache','42'); assert.equal(JSON.parse(await fs.readFile(path.join(accountCache,'roster.json'),'utf8')).account,42);
  assert.deepEqual(await fs.readdir(f.data),['launcher-cache']); assert.deepEqual(await fs.readdir(f.metadata),[]);
  child.stdin.write('shutdown\n');
  assert.deepEqual(await closed,{code:0,signal:null}); assert.equal(stderr,'');
});
