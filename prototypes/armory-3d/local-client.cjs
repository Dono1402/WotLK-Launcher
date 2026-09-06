const fs = require('node:fs');
const fsp = fs.promises;
const path = require('node:path');
const Module = require('node:module');
const { EventEmitter } = require('node:events');

const {dataRoot:sharedOutput,outputRoot:output,vendorRoot:vendor,metadataRoot,publicMode} = require('./runtime-paths.cjs');

function inject(relative, exports) {
  const filename = path.join(vendor, relative);
  require.cache[filename] = { id: filename, filename, loaded: true, exports };
}

async function cachedDownload(url, filename) {
  if (!/^[a-zA-Z0-9_-]+\.(?:dbd|json|txt)$/.test(filename)) throw new Error('Invalid metadata filename');
  const target = path.join(metadataRoot,filename);
  try { return await fsp.readFile(target); } catch (error) {
    if (error.code !== 'ENOENT') throw error;
  }
  if (publicMode) throw new Error(`Packaged client metadata missing: ${filename}`);
  if (!url.startsWith('https://raw.githubusercontent.com/wowdev/')) {
    throw new Error('Only public wowdev metadata is permitted');
  }
  const response = await fetch(url, { signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error(`${response.status}: ${url}`);
  const data = Buffer.from(await response.arrayBuffer());
  await fsp.mkdir(path.dirname(target), { recursive: true });
  await fsp.writeFile(target, data);
  return data;
}

async function openClient(clientRoot, { locale = 'fr' } = {}) {
  if (!['fr', 'en'].includes(locale)) throw new Error('Unsupported client locale');
  process.env.NODE_PATH = path.join(__dirname, 'node_modules');
  Module._initPaths();
  global.BUILD_RELEASE = true;
  global.nw = { App: { dataPath: sharedOutput, manifest: { version: 'local-probe' } } };
  const config = {
    cascLocale: locale === 'en' ? 0x2 : 0x10,
    dbdURL: 'https://raw.githubusercontent.com/wowdev/WoWDBDefs/refs/heads/master/definitions/%s.dbd',
    dbdFallbackURL: 'https://raw.githubusercontent.com/wowdev/WoWDBDefs/refs/heads/master/definitions/%s.dbd'
  };
  const core = {
    view: { config, $watch: (_key, callback) => { callback(config.cascLocale); return () => {}; } },
    events: new EventEmitter(),
    progressLoadingScreen: async text => console.log(text),
    showLoadingScreen() {}, hideLoadingScreen() {}, setToast() {}
  };
  // Keep the upstream binary parsers; replace only desktop UI and cache plumbing.
  inject('core.js', core);
  inject('log.js', { write(format, ...args) {
    if (/Loading table definitions|data loaded|Loaded character|Failed|Unable/.test(format)) {
      console.log(require('node:util').format(format, ...args));
    }
  }, timeLog() {}, timeEnd() {} });
  inject('mmap.js', { create_virtual_file() { throw new Error('Unexpected native mmap call'); } });
  inject('casc/listfile.js', {
    getByID: id => `file-${id}`, getByIDOrUnknown: (id, ext) => `file-${id}${ext}`,
    getByFilename: () => undefined
  });
  const CASCLocal = require(path.join(vendor, 'casc/casc-source-local.js'));
  const BufferWrapper = require(path.join(vendor, 'buffer.js'));
  const generics = require(path.join(vendor, 'generics.js'));
  const tact = await cachedDownload('https://raw.githubusercontent.com/wowdev/TACTKeys/master/WoW.txt', 'public-tact.txt');
  const publicKeys = new Map(tact.toString('utf8').trim().split(/\r?\n/).map(line => line.trim().split(/\s+/)));
  const tactKeys = require(path.join(vendor, 'casc/tact-keys.js'));
  tactKeys.getKey = key => publicKeys.get(key.toLowerCase());
  generics.downloadFile = async urls => {
    const url = Array.isArray(urls) ? urls[0] : urls;
    return BufferWrapper.from(await cachedDownload(url, path.basename(new URL(url).pathname)));
  };
  const client = new CASCLocal(clientRoot);
  await client.init();
  client.build = client.builds.find(build => build.Version === '3.4.3.54261');
  if (!client.build) throw new Error('This probe is pinned to client 3.4.3.54261');
  client.cache = {
    async getFile(file) {
      const cacheRoot = publicMode && !file.endsWith('.dbd') ? path.join(sharedOutput,'casc-cache') : metadataRoot;
      try { return BufferWrapper.from(await fsp.readFile(path.join(cacheRoot,path.basename(file)))); }
      catch (error) { if (error.code === 'ENOENT') return null; throw error; }
    },
    async storeFile(file, data) {
      if (publicMode && file.endsWith('.dbd')) throw new Error('Packaged metadata is read-only');
      const target = path.join(publicMode ? path.join(sharedOutput,'casc-cache') : metadataRoot,path.basename(file));
      await fsp.mkdir(path.dirname(target), { recursive: true });
      await fsp.writeFile(target, data.raw);
    }
  };
  // No asset download fallback: fail explicitly if the installed client is incomplete.
  client.getDataFileWithRemoteFallback = key => client.getDataFile(key);
  client.initializeRemoteCASC = async () => { throw new Error('Remote game asset access is disabled'); };
  await client.loadConfigs();
  await client.loadIndexes();
  await client.loadEncoding();
  await client.loadRoot();
  core.view.casc = client;
  const manifest = JSON.parse(await cachedDownload(
    'https://raw.githubusercontent.com/wowdev/WoWDBDefs/refs/heads/master/manifest.json', 'manifest.json'));
  const tableIds = new Map(manifest.map(table => [table.tableName.toLowerCase(), table.db2FileDataID]));
  client.getVirtualFileByName = async file => {
    const id = tableIds.get(path.basename(file, '.db2').toLowerCase());
    if (!id) throw new Error(`Unknown DB2 table: ${file}`);
    // Match upstream DB2 loading: encrypted, unavailable sections are skipped.
    // Callers must still require every record used for the selected character.
    return client.getFile(id, true, true, false);
  };
  client.getVirtualFileByID = id => client.getFile(id, false, true, false);
  return { client, core, tableIds, vendor, output };
}

module.exports = { openClient, cachedDownload, output, vendor };
