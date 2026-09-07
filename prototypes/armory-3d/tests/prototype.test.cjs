const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');
const { createServer, resolveFile } = require('../server.cjs');
const output = path.resolve(__dirname, '../../../artifacts/armory-prototype');

test('only the explicit public routes can be served', () => {
  for (const url of ['/', '/app.js', '/assets/character.json', '/assets/flowmage.gltf', '/three/build/three.core.js']) assert.ok(resolveFile(url), url);
  for (const url of ['/flowmage.json','/prepared.json','/export.cjs','/.git/config','/assets/../flowmage.json','/assets/%2e%2e/flowmage.json','/assets/%2e%2e%5cflowmage.json','/assets/%00.png','/assets/%ZZ.png','/three/../../package.json']) assert.equal(resolveFile(url), null, url);
});

test('HTTP is read-only and rejects untrusted hosts', async () => {
  const server = createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    assert.equal((await fetch(base)).status, 200);
    assert.equal((await fetch(base, { method:'POST', body:'test' })).status, 405);
    const rejectedStatus = await new Promise((resolve, reject) => {
      http.get(base, { headers:{Host:'external.example'} }, response => { response.resume(); resolve(response.statusCode); }).on('error', reject);
    });
    assert.equal(rejectedStatus, 403);
    assert.equal((await fetch(base + '/flowmage.json')).status, 404);
    const response = await fetch(base, { method:'HEAD' });
    assert.equal(response.status, 200);
    assert.equal(await response.text(), '');
    assert.equal(response.headers.get('access-control-allow-origin'), null);
  } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});

test('public equipment is the real snapshot, without account or secret fields', async () => {
  const source = JSON.parse(await fs.readFile(path.join(output, 'flowmage.json')));
  const data = JSON.parse(await fs.readFile(path.join(output, 'assets/character.json')));
  assert.equal(data.name, 'Flowmage');
  assert.equal(data.level, source.character.level);
  assert.equal(data.equipment.length, 13);
  assert.equal(data.capturedAt, source.capturedAtUtc);
  assert.deepEqual(data.equipment.map(i => [i.slot,i.itemId,i.displayId]), source.equipment.map(i => [i.slot,i.itemId,i.displayId]));
  assert.ok(!/"(guid|email|account|password|token|enchantments)"/i.test(JSON.stringify(data)));
  assert.equal(data.modelFileId, 116921);
  assert.equal(data.appearance.length, 5);
});

test('all glTF buffers, images and animation tracks are available locally', async () => {
  const directory = path.join(output, 'assets');
  const files = await fs.readdir(directory);
  for (const file of files.filter(f => f.endsWith('.gltf'))) {
    const gltf = JSON.parse(await fs.readFile(path.join(directory, file)));
    for (const resource of [...gltf.buffers, ...gltf.images]) {
      assert.match(resource.uri, /^[a-zA-Z0-9_-]+\.(bin|png)$/);
      const bytes = await fs.readFile(path.join(directory, resource.uri));
      assert.ok(bytes.length > 0);
      if (resource.byteLength) assert.equal(bytes.length, resource.byteLength);
    }
    for (const mesh of gltf.meshes) for (const primitive of mesh.primitives) assert.equal(typeof primitive.material, 'number');
    if (file === 'flowmage.gltf') {
      assert.equal(gltf.animations.length, 1);
      assert.ok(gltf.animations[0].channels.length >= 60);
      assert.match(gltf.animations[0].name, /ID 0 /);
      assert.ok(gltf.nodes.some(node => node.name === 'bone_157'));
    }
  }
});

test('every item icon exists and the texture composition is traceable', async () => {
  const data = JSON.parse(await fs.readFile(path.join(output, 'assets/character.json')));
  for (const item of data.equipment) {
    const png = await fs.readFile(path.join(output, 'assets', item.icon));
    assert.equal(png.subarray(1,4).toString(), 'PNG');
  }
  const layers = JSON.parse(await fs.readFile(path.join(output, 'texture-layers.json')));
  assert.equal(layers.length, 20);
  assert.ok(layers.every(layer => [0,1,9,15].includes(layer.mode) && layer.id > 0));
});
