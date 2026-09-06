const fs = require('node:fs/promises');
const path = require('node:path');
const {createHash} = require('node:crypto');
const {normalizeRoster,summary,accountId} = require('./launcher-roster.cjs');
const {equipmentKey,writeJsonAtomic} = require('./armory-data.cjs');
const {readArmory,revisionDirectory} = require('./armory-cache.cjs');
const {resolveDetails} = require('./item-details.cjs');
const {iconsFor} = require('./launcher-icons.cjs');
const {LauncherModelCache,modelAssetsAvailable} = require('./launcher-models.cjs');

const {dataRoot:shared,publicMode} = require('./runtime-paths.cjs');
const hash = value => createHash('sha256').update(JSON.stringify(value)).digest('hex').slice(0,32);
const revisionFor = state => hash([state.fingerprint,state.modelReady,state.assetDir,state.character.capturedAt,state.details,
  state.character.equipment.map(item => [item.slot,item.icon]),state.iconFiles || null]);

function failureReason(error) {
  if (error?.code==='ENOENT' && String(error.syscall).includes('spawn')) return 'ssh-unavailable';
  if (/timed out|timeout/i.test(error?.message || '')) return 'read-timeout';
  if (/^Invalid |^Missing equipment|^Unknown combat/.test(error?.message || '')) return 'invalid-server-data';
  return 'read-failed';
}

function basicDetails(item,base) {
  const incomplete = Boolean(item.randomPropertyId || /[1-9]/.test(item.enchantments)
    || base.spells?.some(([id]) => id>0) || base.scalingDistribution || base.scalingValue);
  return {slot:item.slot,itemId:item.itemId,name:{en:base.name.en,fr:base.name.fr || base.name.en},
    description:base.description,classId:base.classId,subclassId:base.subclassId,inventoryType:base.inventoryType,
    requiredLevel:base.requiredLevel,armor:base.armor,block:base.block,bonding:base.bonding,
    stats:base.scalingDistribution || base.scalingValue ? [] : base.stats.filter(([,value]) => value!==0).map(([type,value]) => ({type,value})),
    damage:base.damage.filter(d => d.max>0),delay:base.delay,resistances:base.resistances,
    effects:[],enchantments:[],sockets:base.sockets.filter(n => n>0),incomplete};
}

function unresolvedDetails(item) {
  const en = typeof item.name==='string' && item.name ? item.name : `Item #${item.itemId}`;
  const fr = typeof item.nameFr==='string' && item.nameFr ? item.nameFr : typeof item.name==='string' && item.name ? item.name : `Objet n°${item.itemId}`;
  return {slot:item.slot,itemId:item.itemId,name:{en,fr},inventoryType:item.inventoryType,
    stats:[],damage:[],resistances:[],effects:[],enchantments:[],sockets:[],incomplete:true};
}

function dataState(row,account,details=row.snapshot.equipment.map(unresolvedDetails)) {
  return {owner:account,fingerprint:row.fingerprint,modelReady:false,assetDir:null,detailsComplete:false,
    character:{characterId:row.id,name:row.name,classId:row.classId,raceId:row.race,level:row.level,
      realm:'Arthas',capturedAt:row.snapshot.capturedAtUtc,
      equipment:row.snapshot.equipment.map(item => ({slot:item.slot,itemId:item.itemId,displayId:item.displayId,
        quality:item.quality,itemLevel:item.itemLevel,name:details.find(detail => detail.slot===item.slot).name.en,icon:null})),attached:[]},
    details:{characterCapturedAt:row.snapshot.capturedAtUtc,items:details}};
}

async function referenceFor(row) {
  try {
    const manifest = await readArmory(shared);
    const root = revisionDirectory(manifest.revision,shared);
    const baseline = JSON.parse(await fs.readFile(path.join(root,'flowmage.json'),'utf8'));
    if (['guid','name','race','classId','gender','level','skin','face','hairStyle','hairColor','facialStyle']
      .some(key => baseline.character[key]!==row.snapshot.character[key])
      || equipmentKey(baseline.equipment)!==equipmentKey(row.snapshot.equipment)) return null;
    const character = JSON.parse(await fs.readFile(path.join(root,'assets/character.json'),'utf8'));
    if (!await modelAssetsAvailable(path.join(root,'assets'),character)) return null;
    const details = JSON.parse(await fs.readFile(path.join(root,'assets/item-details.json'),'utf8'));
    return {character,details,assetDir:path.join(root,'assets')};
  } catch { return null; }
}

class LauncherArmory {
  constructor(account,config,{root=path.join(shared,'launcher-cache'),read,loadRoster,loadCatalog,reference,prepareModel,icons=iconsFor,now=Date.now}={}) {
    this.account = accountId(account);
    this.config = config;
    this.root = path.join(root,String(account));
    if (config.source==='rpc' && (typeof loadRoster!=='function' || typeof loadCatalog!=='function')) throw new Error('Public armory requires authenticated data providers');
    this.loadRoster = loadRoster || (options => require('./legacy-armory-source.cjs').readRawRoster(this.account,{...this.config,...options},read));
    this.loadCatalog = loadCatalog || ((_id,options) => require('./legacy-armory-source.cjs').readCatalog(options.equipment,{...this.config,...options},read));
    this.models = new LauncherModelCache(this.root,config,{loadCatalog:this.loadCatalog});
    this.reference = reference || (async row => await this.models.reference(row) || (publicMode || config.source==='rpc' ? null : await referenceFor(row)));
    this.prepareModel = prepareModel || ((row,details,signal) => this.models.prepare(row,details,signal));
    this.modelFailures = new Map();
    this.icons = icons;
    this.now = now;
    this.failures = 0;
    this.entries = new Map();
    this.roster = [];
    this.status = 'loading';
    this.controller = new AbortController();
  }

  async start() {
    this.controller.signal.throwIfAborted();
    try {
      const cached = JSON.parse(await fs.readFile(path.join(this.root,'roster.json'),'utf8'));
      if (cached.schemaVersion===1 && cached.account===this.account && Array.isArray(cached.characters)) {
        for (const character of cached.characters) {
          this.controller.signal.throwIfAborted();
          if (!/^[1-9][0-9]{0,9}$/.test(character?.id) || Number(character.id)>0xffffffff || typeof character.name!=='string'
            || this.roster.some(row => row.id===character.id)) continue;
          this.roster.push(summary(character));
          try {
            const state = JSON.parse(await fs.readFile(path.join(this.root,character.id+'.json'),'utf8'));
            this.controller.signal.throwIfAborted();
            if (state.character?.characterId!==character.id || state.character?.name!==character.name || state.owner!==this.account
              || !/^[a-f0-9]{32}$/.test(state.revision) || !Array.isArray(state.character.equipment)
              || !Array.isArray(state.details?.items) || state.details.characterCapturedAt!==state.character.capturedAt) continue;
            if (state.modelReady && !await modelAssetsAvailable(state.assetDir,state.character)) {
              state.modelReady = false; state.assetDir = null; state.modelStatus = 'unavailable';
              state.character = {...state.character,attached:[]};
            }
            await this.attachIcons(state,{snapshot:{equipment:state.character.equipment}},false);
            this.controller.signal.throwIfAborted();
            this.entries.set(character.id,state);
          } catch { }
        }
        this.controller.signal.throwIfAborted();
        this.status = 'cached';
      }
    } catch { }
    this.controller.signal.throwIfAborted();
    void this.poll();
  }

  async poll() {
    if (this.controller.signal.aborted || this.pending) return;
    clearTimeout(this.timer);
    this.lastAttemptAt = this.now();
    this.pending = this.refresh();
    try { await this.pending; this.failures = 0; this.lastFailure = null; }
    catch (error) {
      if (!this.controller.signal.aborted) {
        this.status = this.roster.length ? 'cached' : 'unavailable';
        this.failures++; this.lastFailure = failureReason(error);
      }
    }
    finally {
      this.pending = null;
      if (!this.controller.signal.aborted) {
        const delay = this.failures ? Math.min(60000,5000*2**Math.min(this.failures-1,4)) : this.config.intervalMs || 60000;
        this.timer = setTimeout(() => void this.poll(),delay);
        this.timer.unref?.();
      }
    }
  }

  retry() {
    if (this.controller.signal.aborted || this.pending || (this.lastAttemptAt!==undefined && this.now()-this.lastAttemptAt<5000)) return false;
    void this.poll();
    return true;
  }

  async persist(name,value) {
    try {
      await fs.mkdir(this.root,{recursive:true});
      await writeJsonAtomic(path.join(this.root,name),value,this.controller.signal);
      this.cacheFailure = null;
    } catch {
      this.controller.signal.throwIfAborted();
      // A disk-cache failure must not turn a successful live read into an offline result.
      this.cacheFailure = 'cache-write-failed';
    }
  }

  async attachIcons(state,row,extract=true) {
    if (state.modelReady) return;
    const iconFiles = await this.icons(row,{signal:this.controller.signal,...extract && this.config.clientRoot ? {clientRoot:this.config.clientRoot} : {}});
    this.controller.signal.throwIfAborted();
    state.iconFiles = iconFiles;
    state.character = {...state.character,equipment:state.character.equipment.map(item => {
      const name = `icon-${item.itemId}.png`;
      return {...item,icon:iconFiles[name] ? name : null};
    })};
    state.revision = revisionFor(state);
  }

  async refresh() {
    const config = {...this.config,signal:this.controller.signal};
    config.signal.throwIfAborted();
    const rows = normalizeRoster(await this.loadRoster({signal:config.signal}),{verifiedAfter:config.verifiedAfter});
    config.signal.throwIfAborted();
    const ids = new Set(rows.map(row => row.id));
    // Deleted/transferred characters disappear before any new asset preparation.
    for (const id of this.entries.keys()) if (!ids.has(id)) this.entries.delete(id);
    for (const id of this.modelFailures.keys()) if (!ids.has(id)) this.modelFailures.delete(id);
    this.roster = rows.map(summary);
    this.status = 'ready';
    // Every current character is immediately consultable, even while templates are loading.
    for (const row of rows) {
      if (this.entries.get(row.id)?.fingerprint===row.fingerprint) continue;
      const state = dataState(row,this.account);
      state.character.statistics = row.statistics;
      state.revision = revisionFor(state);
      this.entries.set(row.id,state);
    }
    await this.persist('roster.json',{schemaVersion:1,account:this.account,characters:this.roster});
    for (const row of rows) {
      config.signal.throwIfAborted();
      try {
        const old = this.entries.get(row.id);
        const reference = await this.reference(row);
        config.signal.throwIfAborted();
        let state;
        if (reference) {
          state = {owner:this.account,fingerprint:row.fingerprint,character:{...reference.character,characterId:row.id,classId:row.classId,raceId:row.race},
            details:reference.details,assetDir:reference.assetDir,modelReady:true,detailsComplete:true};
        } else if (old?.fingerprint===row.fingerprint && !old.modelReady && old.detailsComplete) {
          state = dataState(row,this.account,old.details.items);
          state.detailsComplete = true;
        }
        else {
          let catalog;
          try { catalog = row.snapshot.equipment.length ? await this.loadCatalog(Number(row.id),{signal:config.signal,equipment:row.snapshot.equipment}) : {items:[]}; }
          catch { config.signal.throwIfAborted(); }
          let tables;
          try {
            tables = Object.fromEntries(await Promise.all(['fr','en'].map(async lang => [lang,JSON.parse(await fs.readFile(path.join(shared,'item-tables-'+lang+'.json'),'utf8'))])));
          } catch { }
          const details = [];
          let complete = true;
          for (const item of row.snapshot.equipment) {
            const base = catalog?.items?.find(entry => entry.itemId===item.itemId);
            if (!base || ['displayId','quality','itemLevel'].some(key => base[key]!==item[key])) {
              complete = false; details.push(unresolvedDetails(item)); continue;
            }
            let detail;
            try { detail = resolveDetails({equipment:[item]},catalog,tables)[0]; }
            catch {
              try { detail = basicDetails(item,base); }
              catch { complete = false; detail = unresolvedDetails(item); }
            }
            details.push(detail);
          }
          state = dataState(row,this.account,details);
          state.detailsComplete = complete;
        }
        config.signal.throwIfAborted();
        state.character = {...state.character,statistics:row.statistics ? {...row.statistics,characterCapturedAt:state.character.capturedAt} : null};
        await this.attachIcons(state,row);
        state.revision = revisionFor(state);
        this.entries.set(row.id,state);
        await this.persist(row.id+'.json',state);
        const failure = this.modelFailures.get(row.id);
        if (!state.modelReady && this.config.clientRoot
            && (!failure || failure.fingerprint!==row.fingerprint || this.now()>=failure.retryAt)) {
          state.modelStatus = 'building';
          try {
            const prepared = await this.prepareModel(row,state.details,config.signal);
            config.signal.throwIfAborted();
            if (!prepared) throw new Error('Model unavailable');
            state = {owner:this.account,fingerprint:row.fingerprint,
              character:{...prepared.character,characterId:row.id,classId:row.classId,raceId:row.race,
                statistics:row.statistics ? {...row.statistics,characterCapturedAt:prepared.character.capturedAt} : null},
              details:prepared.details,assetDir:prepared.assetDir,modelReady:true,detailsComplete:true,modelStatus:'ready'};
            state.revision = revisionFor(state);
            this.modelFailures.delete(row.id);
          } catch {
            config.signal.throwIfAborted();
            state.modelStatus = 'unavailable';
            const attempts = failure?.fingerprint===row.fingerprint ? failure.attempts+1 : 1;
            this.modelFailures.set(row.id,{fingerprint:row.fingerprint,attempts,
              retryAt:this.now()+Math.min(300000,60000*2**Math.min(attempts-1,3))});
          }
          this.entries.set(row.id,state);
          await this.persist(row.id+'.json',state);
        }
      } catch {
        config.signal.throwIfAborted();
        const old = this.entries.get(row.id);
        if (old) old.stale = true;
      }
    }
    config.signal.throwIfAborted();
    this.status = 'ready';
  }

  list() {
    return {status:this.status,refreshing:Boolean(this.pending),characters:this.roster.map(row => ({...row,available:this.entries.has(row.id)}))};
  }

  entry(id) {
    const state = this.roster.some(row => row.id===id) ? this.entries.get(id) : undefined;
    return state && this.config.source==='rpc' && !this.config.clientRoot && !state.modelReady
      ? {...state,modelStatus:'client-missing'} : state;
  }

  async stop() {
    clearTimeout(this.timer);
    this.controller.abort();
    this.entries.clear(); this.roster = []; this.status = 'unavailable';
    await this.pending?.catch(() => {});
  }
}

module.exports = {LauncherArmory,basicDetails,referenceFor};
