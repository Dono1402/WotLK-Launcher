const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {writeClientCacheAtomic} = require('../local-client.cjs');

test('concurrent local CASC cache publications never expose truncated or mixed bytes',async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-client-cache-'));
  t.after(async () => {
    assert.equal(path.dirname(root),path.resolve(os.tmpdir()));
    assert.ok(path.basename(root).startsWith('atlas-client-cache-'));
    await fs.rm(root,{recursive:true,force:true});
  });
  const target = path.join(root,'content-key');
  const bytesForKey = Buffer.alloc(8*1024*1024,0x37);
  let finished = false,reads = 0;
  const reader = (async () => {
    while (!finished) {
      const bytes = await fs.readFile(target).catch(error => {
        if (error.code==='ENOENT') return null;
        throw error;
      });
      if (bytes) {
        assert.ok(bytes.equals(bytesForKey),'readers only observe the complete bytes for this CASC content key');
        reads++;
      }
    }
  })();
  try {
    const results = await Promise.allSettled([1,2].map(async () => {
      for (let attempt=0;attempt<6;attempt++) await writeClientCacheAtomic(target,bytesForKey);
    }));
    for (const result of results) assert.equal(result.status,'fulfilled',result.reason?.message);
  } finally { finished=true; await reader; }
  assert.ok(reads>0,'the reader ran while writers were active');
  assert.deepEqual(await fs.readdir(root),['content-key'],'all unique staging files were removed');
  const final = await fs.readFile(target);
  assert.ok(final.equals(bytesForKey));
});
