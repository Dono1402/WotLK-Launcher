const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {combatDetails} = require('./fixtures/combat.cjs');
const {captureStatistics} = require('../capture-statistics.cjs');
const {readStatistics} = require('../statistics-cache.cjs');
const {readSyncConfig,createStatisticsSync,attachStatisticsSync} = require('../statistics-sync.cjs');
const {createServer} = require('../server.cjs');

async function fixture(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-sync-test-'));
  const assets = path.join(root,'assets');
  await fs.mkdir(assets);
  t.after(async () => {
    for (const dir of [assets,root]) {
      for (const file of await fs.readdir(dir,{withFileTypes:true})) if (file.isFile()) await fs.unlink(path.join(dir,file.name));
      await fs.rmdir(dir);
    }
  });
  const baseline = JSON.parse(await fs.readFile(path.resolve(__dirname,'../../../artifacts/armory-prototype/flowmage.json'),'utf8'));
  await fs.writeFile(path.join(root,'flowmage.json'),JSON.stringify(baseline));
  const snapshot = {schemaVersion:1,source:'atlas-armory-engine',reason:'logout',capturedAtMs:Date.parse('2026-09-05T12:00:00Z'),character:structuredClone(baseline.character),equipment:structuredClone(baseline.equipment),...combatDetails()};
  const current = {observedAtUtc:'2026-09-05 12:01:00.000000',snapshot};
  return {root,current,file:path.join(assets,'statistics.json'),capture:options => captureStatistics({outputDir:root,verifiedAfter:'2026-09-05T11:00:00Z',read:async () => current,...options})};
}

test('automatic capture writes only a newer compatible snapshot and skips unchanged polls',async t => {
  const f = await fixture(t);
  assert.equal((await f.capture()).status,'ready');
  const first = await fs.readFile(f.file,'utf8');
  const modified = (await fs.stat(f.file)).mtimeMs;
  f.current.observedAtUtc = '2026-09-05 12:03:00.000000';
  assert.equal((await f.capture()).status,'unchanged');
  assert.equal((await fs.stat(f.file)).mtimeMs,modified);
  assert.equal(await fs.readFile(f.file,'utf8'),first);
  f.current.snapshot.capturedAtMs -= 60000;
  f.current.snapshot.values.intellect++;
  assert.equal((await f.capture()).reason,'newer-cache-retained');
  assert.equal(await fs.readFile(f.file,'utf8'),first);
  f.current.snapshot.capturedAtMs += 180000;
  assert.equal((await f.capture()).status,'ready');
  assert.equal((await readStatistics(f.file)).record.values.intellect,f.current.snapshot.values.intellect);
});

test('missing, mismatched or failed captures retain the last valid cache',async t => {
  const f = await fixture(t);
  await f.capture();
  const first = await fs.readFile(f.file,'utf8');
  assert.equal((await f.capture({read:async () => ({snapshot:null})})).status,'unavailable');
  await assert.rejects(f.capture({read:async () => { throw new Error('offline'); }}),/offline/);
  f.current.snapshot.equipment[0].itemId++;
  await assert.rejects(f.capture(),{code:'ARMORY_REFRESH_REQUIRED'});
  assert.equal(await fs.readFile(f.file,'utf8'),first);
});

test('cancellation and concurrent export changes cannot publish a late snapshot',async t => {
  const f = await fixture(t);
  await f.capture();
  const first = await fs.readFile(f.file,'utf8');
  f.current.snapshot.capturedAtMs += 1000;
  const controller = new AbortController();
  await assert.rejects(f.capture({signal:controller.signal,read:async () => {
    controller.abort();
    return f.current;
  }}),{name:'AbortError'});
  await assert.rejects(f.capture({read:async () => {
    await fs.appendFile(path.join(f.root,'flowmage.json'),'\n');
    return f.current;
  }}),{code:'ARMORY_REFRESH_REQUIRED'});
  assert.equal(await fs.readFile(f.file,'utf8'),first);
  assert.deepEqual(await fs.readdir(path.dirname(f.file)),['statistics.json']);
});

test('private configuration is opt-in, validates cadence and never accepts shell arguments',async t => {
  const f = await fixture(t);
  const file = path.join(f.root,'statistics-sync.json');
  assert.equal(await readSyncConfig(file),null);
  await fs.writeFile(file,JSON.stringify({enabled:false}));
  assert.equal(await readSyncConfig(file),null);
  const valid = {schemaVersion:1,enabled:true,host:'user@example.test',key:path.join(f.root,'identity'),verifiedAfter:'2026-09-05T07:17:06Z'};
  await fs.writeFile(file,JSON.stringify({...valid,secret:'not forwarded'}));
  assert.deepEqual(await readSyncConfig(file),{host:valid.host,key:valid.key,verifiedAfter:valid.verifiedAfter,intervalMs:60000});
  for (const change of [{host:'user@example.test; rm'},{key:'relative-key'},{intervalMs:5000},{verifiedAfter:'yesterday'},{enabled:'true'}]) {
    await fs.writeFile(file,JSON.stringify({...valid,...change}));
    await assert.rejects(readSyncConfig(file));
  }
});

function scheduler(capture) {
  let next, clearCount = 0;
  const reports = [];
  const sync = createStatisticsSync({capture,now:() => Date.parse('2026-09-05T12:00:00Z'),
    onStatus:async status => reports.push(status),
    setTimer:(fn,delay) => { next = {fn,delay}; return 1; },
    clearTimer:() => { next = undefined; clearCount++; }
  });
  return {sync,reports,get next() { return next; },get clearCount() { return clearCount; }};
}

test('sync runs once at startup, then once per minute, with no overlapping calls',async () => {
  let calls = 0, release;
  const pending = new Promise(resolve => { release = resolve; });
  const s = scheduler(async () => { calls++; await pending; return {status:'unchanged'}; });
  const first = s.sync.start();
  assert.equal(s.sync.start(),first);
  assert.equal(calls,1);
  assert.equal(s.next,undefined);
  release(); await first;
  assert.equal(s.next.delay,60000);
  await s.next.fn();
  assert.equal(calls,2);
  await s.sync.stop();
  assert.equal(s.next,undefined);
});

test('failures back off to five minutes, hide remote error text and recover automatically',async () => {
  let fail = true;
  const s = scheduler(async () => {
    if (fail) throw new Error('private ssh key path and server response');
    return {status:'unchanged',savedAt:'2026-09-05T11:00:00Z'};
  });
  await s.sync.start();
  assert.equal(s.next.delay,120000);
  await s.next.fn(); assert.equal(s.next.delay,240000);
  await s.next.fn(); assert.equal(s.next.delay,300000);
  assert.ok(!JSON.stringify(s.reports).includes('private'));
  fail = false;
  await s.next.fn();
  assert.equal(s.next.delay,60000);
  assert.equal(s.reports.at(-1).status,'unchanged');
  await s.sync.stop();
});

test('stopping aborts an in-flight sync without rescheduling or publishing diagnostics',async () => {
  let aborted = false;
  const s = scheduler(({signal}) => new Promise((resolve,reject) => {
    signal.addEventListener('abort',() => { aborted = true; reject(signal.reason); },{once:true});
  }));
  const pending = s.sync.start();
  await s.sync.stop(); await pending;
  assert.equal(aborted,true);
  assert.equal(s.next,undefined);
  assert.deepEqual(s.reports,[]);
});

test('HTTP cache stays available during a blocked remote read; requests do not trigger captures',async t => {
  const f = await fixture(t);
  await f.capture();
  let calls = 0;
  const s = scheduler(({signal}) => { calls++; return new Promise((resolve,reject) => signal.addEventListener('abort',() => reject(signal.reason),{once:true})); });
  const pending = s.sync.start();
  const server = createServer({getStatistics:() => readStatistics(f.file)});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  t.after(async () => { await s.sync.stop(); await pending; server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); });
  const base = `http://127.0.0.1:${server.address().port}`;
  for (let i=0;i<3;i++) {
    const response = await fetch(base+'/statistics.json',{signal:AbortSignal.timeout(1000)});
    assert.equal((await response.json()).record.source,'arthas-combat-stats');
  }
  assert.equal(calls,1);
  for (const url of ['/statistics-sync.json','/statistics-sync-status.json','/capture-statistics.cjs','/assets/statistics.json']) assert.equal((await fetch(base+url)).status,404);
  assert.equal(await attachStatisticsSync(server,{configFile:path.join(f.root,'absent.json')}),null);
});
