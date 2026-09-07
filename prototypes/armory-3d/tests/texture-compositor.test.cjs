const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const zlib = require('node:zlib');
const {composeTextureLayers} = require('../texture-compositor.cjs');

function image(width, height, values) { return {width, height, data: Buffer.from(values)}; }
function layer(pixels, {width = pixels.width, height = pixels.height, x = 0, y = 0,
  targetWidth = width, targetHeight = height, mode = 15, type = 1, order = 1} = {}) {
  return {type, order, mode, material: {Width: targetWidth, Height: targetHeight},
    section: {X: x, Y: y, Width: width, Height: height}, image: pixels};
}
function pixels(entries, type = 1) { return [...composeTextureLayers(entries).get(type).data]; }

test('opaque textures preserve RGBA and source inputs without image or native dependencies', () => {
  const source = image(2, 1, [10, 20, 30, 255, 40, 50, 60, 255]);
  const before = Buffer.from(source.data);
  assert.deepEqual(pixels([layer(source)]), [...source.data]);
  assert.deepEqual(source.data, before);
  assert.equal(composeTextureLayers([]).size, 0);
});

test('source-over composites colors in premultiplied alpha and discards invisible source RGB', () => {
  const blue = layer(image(1, 1, [0, 0, 255, 255]));
  const red = layer(image(1, 1, [255, 0, 0, 128]), {order: 2});
  assert.deepEqual(pixels([blue, red]), [128, 0, 127, 255]);
  assert.deepEqual(pixels([layer(image(1, 1, [255, 20, 90, 0]))]), [0, 0, 0, 0]);
  assert.deepEqual(pixels([blue, layer(image(1, 1, [255, 20, 90, 0]), {order: 2})]), [0, 0, 255, 255]);
});

test('modes zero and one clear only their clipped destination section before drawing', () => {
  for (const mode of [0, 1]) {
    const base = layer(image(3, 1, [10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 255]));
    const clear = layer(image(1, 1, [255, 0, 0, 0]), {x: 1, targetWidth: 3, mode, order: 2});
    assert.deepEqual(pixels([base, clear]), [10, 20, 30, 255, 0, 0, 0, 0, 10, 20, 30, 255]);
  }
});

test('ascending order is stable on ties, independent across materials, and does not sort the input', () => {
  const a = layer(image(1, 1, [1, 2, 3, 255]), {order: 2});
  const b = layer(image(1, 1, [4, 5, 6, 255]), {order: 1});
  const c = layer(image(1, 1, [7, 8, 9, 255]), {order: 2});
  const d = layer(image(1, 1, [10, 11, 12, 255]), {type: 8});
  const entries = [a, b, c, d];
  assert.deepEqual(pixels(entries), [7, 8, 9, 255]);
  assert.deepEqual(pixels(entries, 8), [10, 11, 12, 255]);
  assert.deepEqual(entries, [a, b, c, d]);
});

test('bilinear scaling uses texel centers and premultiplied colors across transparent edges', () => {
  const source = image(2, 1, [255, 0, 0, 255, 0, 255, 0, 0]);
  assert.deepEqual(pixels([layer(source, {width: 4})]),
    [255, 0, 0, 255, 255, 0, 0, 191, 255, 0, 0, 64, 0, 0, 0, 0]);
  assert.deepEqual(pixels([layer(image(2, 2, [0, 0, 0, 255, 100, 0, 0, 255, 0, 100, 0, 255, 100, 100, 0, 255]), {width: 1, height: 1})]),
    [50, 50, 0, 255]);
});

test('clipping preserves the uncut source mapping on negative and oversized sections', () => {
  const source = image(3, 1, [10, 0, 0, 255, 20, 0, 0, 255, 30, 0, 0, 255]);
  assert.deepEqual(pixels([layer(source, {x: -1, targetWidth: 1})]), [20, 0, 0, 255]);
  assert.deepEqual(pixels([layer(source, {x: 2, targetWidth: 1})]), [0, 0, 0, 0]);
});

test('invalid dimensions, buffers, rectangles and unsupported blend modes fail before export', () => {
  assert.throws(() => composeTextureLayers([layer(image(0, 1, []))]), /dimensions/);
  assert.throws(() => composeTextureLayers([layer(image(1, 1, [0, 0]))]), /RGBA/);
  assert.throws(() => composeTextureLayers([layer(image(1, 1, [0, 0, 0, 0]), {x: .5})]), /section/);
  assert.throws(() => composeTextureLayers([layer(image(1, 1, [0, 0, 0, 0]), {mode: 2})]), /mode/);
  assert.throws(() => composeTextureLayers([layer(image(1, 1, [0, 0, 0, 0]), {targetWidth: 8193})]), /dimensions/);
});

// Optional oracle checks use the former browser implementation only in this test
// process. The production compositor and exporter never import Playwright.
const oracleEnabled = process.env.ATLAS_TEXTURE_ORACLE === '1';
function pngBytes(PNGWriter, source) {
  const writer = new PNGWriter(source.width, source.height);
  writer.getPixelData().set(source.data);
  return writer.getBuffer().raw;
}
async function withOracle(run) {
  process.env.NODE_PATH = path.resolve(__dirname, '../node_modules');
  require('node:module').Module._initPaths();
  const vendor = require('../runtime-paths.cjs').vendorRoot;
  const PNGWriter = require(path.join(vendor, 'png-writer.js'));
  const playwright = require(process.env.PLAYWRIGHT_MODULE || path.join(process.env.USERPROFILE,
    '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));
  const browser = await playwright.chromium.launch({headless: true, channel: 'msedge'});
  try {
    const page = await browser.newPage();
    await run(async entries => {
      const data = entries.map(({image: source, ...entry}) => ({...entry,
        uri: 'data:image/png;base64,' + pngBytes(PNGWriter, source).toString('base64')}));
      const result = await page.evaluate(async entries => {
        const canvases = new Map();
        for (const entry of entries.sort((a, b) => a.order - b.order)) {
          let canvas = canvases.get(entry.type);
          if (!canvas) {
            canvas = document.createElement('canvas');
            canvas.width = entry.material.Width; canvas.height = entry.material.Height;
            canvases.set(entry.type, canvas);
          }
          const context = canvas.getContext('2d');
          const image = new Image(); image.src = entry.uri; await image.decode();
          const r = entry.section;
          if ([0, 1].includes(entry.mode)) context.clearRect(r.X, r.Y, r.Width, r.Height);
          context.drawImage(image, r.X, r.Y, r.Width, r.Height);
        }
        return Array.from(canvases, ([type, canvas]) => [type, canvas.toDataURL('image/png').split(',')[1]]);
      }, data);
      return new Map(result.map(([type, data]) => [type, decodePng(Buffer.from(data, 'base64'))]));
    }, PNGWriter);
  } finally { await browser.close(); }
}

function decodePng(bytes) {
  assert.equal(bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a');
  const width = bytes.readUInt32BE(16), height = bytes.readUInt32BE(20), channels = bytes[25] === 6 ? 4 : 3;
  assert.equal(bytes[24], 8); assert.ok([2, 6].includes(bytes[25])); assert.equal(bytes[28], 0);
  const parts = [];
  for (let offset = 8; offset < bytes.length;) {
    const length = bytes.readUInt32BE(offset), type = bytes.toString('ascii', offset + 4, offset + 8);
    if (type === 'IDAT') parts.push(bytes.subarray(offset + 8, offset + 8 + length));
    offset += length + 12;
  }
  const raw = zlib.inflateSync(Buffer.concat(parts)), stride = width * channels, unfiltered = Buffer.alloc(width * height * channels);
  function paeth(a, b, c) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)]; assert.ok(filter <= 4);
    for (let x = 0; x < stride; x++) {
      const offset = y * stride + x, a = x >= channels ? unfiltered[offset - channels] : 0;
      const b = y ? unfiltered[offset - stride] : 0, c = y && x >= channels ? unfiltered[offset - stride - channels] : 0;
      const predictor = [0, a, b, Math.floor((a + b) / 2), paeth(a, b, c)][filter];
      unfiltered[offset] = raw[y * (stride + 1) + x + 1] + predictor;
    }
  }
  const data = Buffer.alloc(width * height * 4, 255);
  for (let i = 0; i < width * height; i++) unfiltered.copy(data, i * 4, i * channels, (i + 1) * channels);
  return {width, height, data};
}

function difference(actual, expected) {
  assert.equal(actual.width, expected.width); assert.equal(actual.height, expected.height);
  let changedPixels = 0, maxChannelDelta = 0, totalDelta = 0, alphaChanged = 0;
  let maxOnBlack = 0, maxOnWhite = 0, totalOnBlack = 0, totalOnWhite = 0;
  for (let i = 0; i < actual.data.length; i += 4) {
    let changed = false;
    for (let channel = 0; channel < 4; channel++) {
      const delta = Math.abs(actual.data[i + channel] - expected.data[i + channel]);
      changed ||= delta !== 0; maxChannelDelta = Math.max(maxChannelDelta, delta); totalDelta += delta;
      if (channel === 3 && delta) alphaChanged++;
      if (channel < 3) {
        const actualAlpha = actual.data[i + 3] / 255, expectedAlpha = expected.data[i + 3] / 255;
        const actualColor = actual.data[i + channel] * actualAlpha, expectedColor = expected.data[i + channel] * expectedAlpha;
        const onBlack = Math.abs(Math.round(actualColor) - Math.round(expectedColor));
        const onWhite = Math.abs(Math.round(actualColor + 255 * (1 - actualAlpha)) - Math.round(expectedColor + 255 * (1 - expectedAlpha)));
        maxOnBlack = Math.max(maxOnBlack, onBlack); maxOnWhite = Math.max(maxOnWhite, onWhite);
        totalOnBlack += onBlack; totalOnWhite += onWhite;
      }
    }
    if (changed) changedPixels++;
  }
  const colorChannels = actual.width * actual.height * 3;
  return {pixels: actual.width * actual.height, changedPixels, maxChannelDelta, meanChannelDelta: totalDelta / actual.data.length, alphaChanged,
    opaqueBlack: {maxChannelDelta: maxOnBlack, meanChannelDelta: totalOnBlack / colorChannels},
    opaqueWhite: {maxChannelDelta: maxOnWhite, meanChannelDelta: totalOnWhite / colorChannels}};
}

test('optional Canvas oracle measures integer scale, transparency and clipping parity', {skip: !oracleEnabled}, async () => {
  const source = image(3, 2, [10, 60, 210, 255, 200, 70, 10, 160, 170, 20, 250, 0,
    80, 230, 40, 80, 220, 190, 10, 220, 20, 210, 160, 255]);
  await withOracle(async oracle => {
    for (const [width, height] of [[3, 2], [6, 4], [4, 3], [1, 1]]) {
      const entries = [layer(source, {width, height})];
      const delta = difference(composeTextureLayers(entries).get(1), (await oracle(entries)).get(1));
      console.log(JSON.stringify({case: `synthetic-${width}x${height}`, ...delta}));
      assert.ok(delta.maxChannelDelta <= 8, 'Bilinear Canvas rounding must stay bounded on synthetic transparent pixels');
    }
  });
});

test('optional real BLP layer comparison uses prior exported PNGs and the former Canvas oracle', {skip: !oracleEnabled || !process.env.ATLAS_TEXTURE_REFERENCE_DIRS}, async () => {
  const directories = JSON.parse(process.env.ATLAS_TEXTURE_REFERENCE_DIRS);
  assert.ok(Array.isArray(directories) && directories.length > 0 && directories.length <= 8);
  const oldFetch = global.fetch;
  global.fetch = async () => { throw new Error('Texture comparison requires already-cached metadata; external requests are forbidden'); };
  try {
    const {client, vendor} = await require('../local-client.cjs').openClient(process.env.ATLAS_TEXTURE_CLIENT_ROOT);
    const BLP = require(path.join(vendor, 'casc/blp.js')), cache = new Map();
    await withOracle(async (oracle, PNGWriter) => {
      for (const directory of directories) {
        const entries = JSON.parse(await fs.readFile(path.join(directory, 'texture-layers.json'), 'utf8'));
        for (const entry of entries) {
          if (!cache.has(entry.id)) {
            const blp = new BLP(await client.getFile(entry.id, false, true, false));
            const decoded = image(blp.width, blp.height, blp.toUInt8Array(0, 15));
            assert.deepEqual(decoded, decodePng((await blp.toPNG(15)).raw), `BLP ${entry.id} RGBA must match the former PNG path`);
            assert.deepEqual(decoded, decodePng(pngBytes(PNGWriter, decoded)), `BLP ${entry.id} PNGWriter round trip must preserve RGBA`);
            cache.set(entry.id, decoded);
          }
          entry.image = cache.get(entry.id);
        }
        const actual = composeTextureLayers(entries), reference = await oracle(entries);
        for (const [type, bitmap] of actual) {
          const previous = decodePng(await fs.readFile(path.join(directory, 'assets', `data-${type}.png`)));
          const oracleDelta = difference(bitmap, reference.get(type)), previousDelta = difference(bitmap, previous);
          const baselineCanvas = difference(reference.get(type), previous);
          console.log(JSON.stringify({case: path.basename(directory), type, layers: entries.filter(entry => entry.type === type).length,
            canvas: oracleDelta, previous: previousDelta, baselineCanvas}));
          assert.equal(baselineCanvas.changedPixels, 0, 'The former Canvas implementation must exactly reproduce the saved baseline');
          assert.equal(oracleDelta.alphaChanged, 0, 'Real texture alpha must remain identical to Canvas');
          assert.ok(oracleDelta.opaqueBlack.maxChannelDelta <= 3 && oracleDelta.opaqueWhite.maxChannelDelta <= 3,
            'Visible texture color over opaque black and white must remain within three byte levels of Canvas');
        }
      }
    });
  } finally { global.fetch = oldFetch; }
});
