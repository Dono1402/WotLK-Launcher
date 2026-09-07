const fs = require('node:fs/promises');
const path = require('node:path');
const {randomUUID} = require('node:crypto');
const {equipmentKey,writeJsonAtomic} = require('./armory-data.cjs');
const {buildCharacterExport} = require('./export-pipeline.cjs');

const renderSchemaVersion = 3;
const identity = ['guid','name','race','classId','gender','level','skin','face','hairStyle','hairColor','facialStyle'];

async function modelAssetsAvailable(assetDir,character) {
  if (character?.renderSchemaVersion!==renderSchemaVersion || !path.isAbsolute(assetDir || '')) return false;
  try {
    const resource = async (name,bytes) => {
      if (!/^[a-zA-Z0-9_-]+\.(bin|png|gltf)$/.test(name)) throw new Error('Invalid cached model resource');
      const stat = await fs.stat(path.join(assetDir,name));
      if (!stat.isFile() || !stat.size || (bytes!==undefined && stat.size!==bytes)) throw new Error('Incomplete cached model resource');
    };
    for (const name of ['flowmage.gltf',...(character.attached || []).map(item => item.url)]) {
      await resource(name);
      const gltf = JSON.parse(await fs.readFile(path.join(assetDir,name),'utf8'));
      for (const entry of [...gltf.buffers ?? [],...gltf.images ?? []]) await resource(entry.uri,entry.byteLength);
    }
    for (const item of character.equipment || []) await resource(item.icon);
    return true;
  } catch { return false; }
}

class LauncherModelCache {
  constructor(root,config,{read,loadCatalog,build=buildCharacterExport}={}) {
    this.root = path.resolve(root,'models');
    this.config = config;
    if (config.source==='rpc' && typeof loadCatalog!=='function') throw new Error('Public models require an authenticated catalog provider');
    this.loadCatalog = loadCatalog || ((_id,options) => require('./legacy-armory-source.cjs').readCatalog(options.equipment,{...this.config,...options},read));
    this.build = build;
  }

  directory(row) {
    if (!/^[1-9][0-9]{0,9}$/.test(row.id) || !/^[a-f0-9]{32}$/.test(row.fingerprint)) throw new Error('Invalid model cache key');
    return path.join(this.root,row.id,`v${renderSchemaVersion}-${row.fingerprint}`);
  }

  async reference(row) {
    const target = this.directory(row);
    try {
      const snapshot = JSON.parse(await fs.readFile(path.join(target,'flowmage.json'),'utf8'));
      const character = JSON.parse(await fs.readFile(path.join(target,'assets/character.json'),'utf8'));
      const details = JSON.parse(await fs.readFile(path.join(target,'assets/item-details.json'),'utf8'));
      if (character.renderSchemaVersion!==renderSchemaVersion || character.name!==row.name
          || identity.some(key => snapshot.character[key]!==row.snapshot.character[key])
          || equipmentKey(snapshot.equipment)!==equipmentKey(row.snapshot.equipment)
          || details.characterCapturedAt!==character.capturedAt) return null;
      if (!await modelAssetsAvailable(path.join(target,'assets'),character)) return null;
      return {character,details,assetDir:path.join(target,'assets')};
    } catch { return null; }
  }

  async prepare(row,details,signal) {
    signal?.throwIfAborted();
    if (!this.config.clientRoot) return null;
    const existing = await this.reference(row);
    if (existing) return existing;
    const target = this.directory(row);
    const builds = path.join(this.root,'builds');
    const stage = path.join(builds,randomUUID().replaceAll('-',''));
    await fs.mkdir(stage,{recursive:true});
    try {
      const catalog = row.snapshot.equipment.length
        ? await this.loadCatalog(Number(row.id),{signal,equipment:row.snapshot.equipment}) : {items:[]};
      const snapshot = structuredClone(row.snapshot);
      await this.build(stage,snapshot,catalog,row.statistics,{clientRoot:this.config.clientRoot,signal,details});
      signal?.throwIfAborted();
      await fs.mkdir(path.dirname(target),{recursive:true});
      // Only this module's versioned generated cache can be replaced. A valid
      // duplicate completed by another process is kept, avoiding mixed assets.
      if (await this.reference(row)) return await this.reference(row);
      if (path.dirname(target)!==path.join(this.root,row.id) || path.basename(target)!==`v${renderSchemaVersion}-${row.fingerprint}`) throw new Error('Unsafe model cache target');
      await fs.rm(target,{recursive:true,force:true});
      await fs.rename(stage,target);
      return await this.reference(row);
    } catch (error) {
      const log = await fs.readFile(path.join(stage,'export.log'),'utf8').catch(() => '');
      await writeJsonAtomic(path.join(this.root,'last-export-error.json'),{characterId:row.id,fingerprint:row.fingerprint,
        reason:error.code==='ABORT_ERR'?'cancelled':'export-failed',log}).catch(() => {});
      throw error;
    } finally {
      if (path.dirname(stage)!==builds || !/^[a-f0-9]{32}$/.test(path.basename(stage))) throw new Error('Unsafe model build cleanup');
      await fs.rm(stage,{recursive:true,force:true});
    }
  }
}

module.exports = {LauncherModelCache,renderSchemaVersion,modelAssetsAvailable};
