const {test} = require('node:test');
const assert = require('node:assert/strict');
const {PassThrough} = require('node:stream');
const {createLauncherRpc,REQUEST_PREFIX,MAX_LINE_BYTES,MAX_REQUEST_BYTES} = require('../launcher-rpc.cjs');

function fixture(t,options={}) {
  const input = new PassThrough(),output = new PassThrough();
  let written = '',shutdowns = 0;
  output.on('data',chunk => { written += chunk.toString('utf8'); });
  const rpc = createLauncherRpc({input,output,onShutdown:() => { shutdowns++; },...options});
  t.after(() => { rpc.close(); input.destroy(); output.destroy(); });
  return {rpc,input,output,get shutdowns() { return shutdowns; },
    requests() { return written.trimEnd().split('\n').filter(Boolean).map(line => {
      assert.ok(Buffer.byteLength(line+'\n')<=MAX_REQUEST_BYTES);
      assert.ok(line.startsWith(REQUEST_PREFIX));
      return JSON.parse(line.slice(REQUEST_PREFIX.length));
    }); },reply(id,result) { input.write(JSON.stringify({id,result})+'\n'); }};
}

test('RPC frames requests, accepts split UTF-8 and CRLF, and correlates out-of-order replies',async t => {
  const f = fixture(t),first = f.rpc.loadRoster(),second = f.rpc.loadCatalog(0xffffffff);
  assert.deepEqual(f.requests(),[{id:1,operation:'roster'},{id:2,operation:'catalog',characterId:0xffffffff}]);
  const bytes = Buffer.from(JSON.stringify({id:2,result:{name:'Épée 🧊'}})+'\r\n'+JSON.stringify({id:1,result:{characters:[]}})+'\n');
  for (const byte of bytes) f.input.write(Buffer.from([byte]));
  assert.deepEqual(await second,{name:'Épée 🧊'}); assert.deepEqual(await first,{characters:[]});
  assert.equal(f.rpc.pendingCount,0); assert.equal(f.shutdowns,0);
});

test('RPC limits concurrency to four and rejects malformed operations before writing',async t => {
  const f = fixture(t),pending = Array.from({length:4},() => f.rpc.loadRoster());
  await assert.rejects(f.rpc.loadRoster(),{code:'ARMORY_RPC_CONCURRENCY_LIMIT'});
  for (const [operation,id] of [['sql',undefined],['roster',1],['catalog',0],['catalog',2**32],['catalog',1.2],['catalog','1']])
    await assert.rejects(f.rpc.request(operation,id),{code:'ARMORY_RPC_INVALID_REQUEST'});
  assert.equal(f.requests().length,4);
  for (let id=1;id<=4;id++) f.reply(id,{characters:[]});
  await Promise.all(pending);
});

test('RPC cancels requests, ignores late responses and removes abort listeners',async t => {
  const f = fixture(t),controller = new AbortController();
  const cancelled = f.rpc.loadRoster({signal:controller.signal});
  const rejected = assert.rejects(cancelled,{name:'AbortError'});
  controller.abort(); await rejected;
  assert.equal(f.rpc.pendingCount,0); f.reply(1,{characters:['late']});
  await assert.rejects(f.rpc.loadRoster({signal:controller.signal}),{name:'AbortError'});
  const next = f.rpc.loadCatalog(8); f.reply(2,{items:[]});
  assert.deepEqual(await next,{items:[]}); assert.equal(f.shutdowns,0);
});

test('RPC times out requests and remains usable after a delayed response',async t => {
  const f = fixture(t,{timeoutMs:15});
  await assert.rejects(f.rpc.loadRoster(),{code:'ARMORY_RPC_TIMEOUT'});
  assert.equal(f.rpc.pendingCount,0); f.reply(1,{characters:[]});
  const next = f.rpc.loadRoster(); f.reply(2,{characters:[]}); await next;
});

test('RPC unavailable is retryable while unauthorized terminates every pending request',async t => {
  const f = fixture(t),unavailable = f.rpc.loadRoster();
  f.input.write('{"id":1,"error":"unavailable"}\n');
  await assert.rejects(unavailable,{code:'ARMORY_RPC_UNAVAILABLE'}); assert.equal(f.shutdowns,0);
  const one = assert.rejects(f.rpc.loadRoster(),{code:'ARMORY_RPC_UNAUTHORIZED'});
  const two = assert.rejects(f.rpc.loadCatalog(4),{code:'ARMORY_RPC_UNAUTHORIZED'});
  f.input.write('{"id":2,"error":"unauthorized"}\n');
  await Promise.all([one,two]); assert.equal(f.shutdowns,1); assert.equal(f.rpc.pendingCount,0);
  await assert.rejects(f.rpc.loadRoster(),/closed/);
});

test('RPC malformed envelopes reject their request without accepting array or dual results',async t => {
  const f = fixture(t);
  for (const response of [{result:[]},{result:null},{result:{},error:'unavailable'},{error:'debug details'},{}]) {
    const responsePromise = f.rpc.loadRoster(),id = f.requests().at(-1).id;
    f.input.write(JSON.stringify({id,...response})+'\n');
    await assert.rejects(responsePromise,/Invalid/);
  }
  assert.equal(f.shutdowns,0);
});

test('RPC rejects malformed JSON and invalid UTF-8 without continuing the stream',async t => {
  for (const input of [Buffer.from('{bad}\n'),Buffer.from('{"id":0,"result":{}}\n'),Buffer.from([0xc0,0xaf,10])]) {
    const f = fixture(t),pending = assert.rejects(f.rpc.loadRoster(),/Invalid/);
    f.input.write(input); await pending; assert.equal(f.shutdowns,1);
    f.input.write('shutdown\n'); assert.equal(f.shutdowns,1);
  }
});

test('RPC allows exactly four MiB including its response envelope and rejects oversized streaming input',async t => {
  assert.equal(MAX_LINE_BYTES,4*1024*1024);
  const f = fixture(t),pending = f.rpc.loadRoster();
  const empty = JSON.stringify({id:1,result:{data:''}}),padding = 'x'.repeat(MAX_LINE_BYTES-Buffer.byteLength(empty));
  f.input.write(JSON.stringify({id:1,result:{data:padding}})+'\n');
  assert.equal((await pending).data.length,padding.length); assert.equal(f.shutdowns,0);
  const oversized = assert.rejects(f.rpc.loadRoster(),/too large/);
  const chunk = Buffer.alloc(64*1024,32);
  for (let index=0;index<MAX_LINE_BYTES/chunk.length+1;index++) f.input.write(chunk);
  await oversized; assert.equal(f.shutdowns,1); assert.equal(f.rpc.pendingCount,0);
  f.input.write(Buffer.alloc(8*1024*1024,32)); assert.equal(f.shutdowns,1);
});

test('RPC size checks count UTF-8 bytes and not characters',async t => {
  const f = fixture(t,{maxLineBytes:64});
  const pending = assert.rejects(f.rpc.loadRoster(),/too large/);
  f.input.write(JSON.stringify({id:1,result:{data:'🧊'.repeat(12)}})+'\n');
  await pending; assert.equal(f.shutdowns,1);
});

test('RPC exact shutdown line, input EOF and stream errors terminate pending work once',async t => {
  for (const action of [f => f.input.write('shutdown\r\n'),f => f.input.end(),f => f.input.emit('error',new Error('test')),
    f => f.output.emit('error',new Error('test'))]) {
    const f = fixture(t),pending = assert.rejects(f.rpc.loadRoster(),/closed|ended|failed/);
    action(f); await pending; assert.equal(f.shutdowns,1); assert.equal(f.rpc.pendingCount,0);
  }
  const f = fixture(t),pending = f.rpc.loadRoster();
  f.reply(1,{note:'not a shutdown request'}); await pending; assert.equal(f.shutdowns,0);
});
