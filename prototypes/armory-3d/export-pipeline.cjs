const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {spawn} = require('node:child_process');
const {writeJsonAtomic} = require('./armory-data.cjs');
const {resolveDetails} = require('./item-details.cjs');

function enrichItemDetails(snapshot,catalog,tables,fallback) {
  return {...fallback,characterCapturedAt:snapshot.capturedAtUtc,items:snapshot.equipment.map(item => {
    try { return resolveDetails({equipment:[item]},catalog,tables)[0]; }
    catch {
      const detail = fallback.items.find(value => value.slot===item.slot && value.itemId===item.itemId);
      if (!detail) throw new Error('Missing fallback item details');
      return detail;
    }
  })};
}

function enrichSnapshot(snapshot,catalog) {
  if (!Array.isArray(catalog.items)) {
    if (!snapshot.equipment.length && catalog.items===null) catalog.items = [];
    else throw new Error('Invalid item catalog');
  }
  for (const item of snapshot.equipment) {
    const base = catalog.items.find(row => row.itemId===item.itemId);
    if (!base || ['displayId','quality','itemLevel'].some(key => base[key]!==item[key])) throw new Error('Item template changed since the captured equipment');
    if (typeof base.name?.en!=='string' || !Number.isInteger(base.inventoryType)) throw new Error('Incomplete item template');
    item.name = base.name.en;
    item.inventoryType = base.inventoryType;
  }
  return snapshot;
}

async function runExportStep(script,args,stage,signal) {
  signal?.throwIfAborted();
  const timeout = AbortSignal.timeout(10*60*1000);
  const combined = signal ? AbortSignal.any([signal,timeout]) : timeout;
  let tail = '';
  try {
    await new Promise((resolve,reject) => {
      let failure;
      const child = spawn(process.execPath,[path.join(__dirname,script),...args],{cwd:__dirname,windowsHide:true,signal:combined,
        env:{...process.env,ARMORY_EXPORT_DIR:stage},stdio:['ignore','pipe','pipe']});
      try { os.setPriority(child.pid,os.constants.priority.PRIORITY_BELOW_NORMAL); } catch { }
      const append = chunk => { tail = (tail+chunk).slice(-64000); };
      child.stdout.on('data',append); child.stderr.on('data',append);
      child.once('error',error => { failure = error; });
      child.once('close',code => failure ? reject(failure) : code===0 ? resolve() : reject(new Error(`Armory export failed: ${script} (${code})`)));
    });
  } finally { await fs.appendFile(path.join(stage,'export.log'),`\n${script}\n${tail}`); }
}

async function validateExport(stage,snapshot,statistics) {
  const assets = path.join(stage,'assets');
  const read = async name => JSON.parse(await fs.readFile(path.join(assets,name),'utf8'));
  const character = await read('character.json');
  const details = await read('item-details.json');
  if (character.name!==snapshot.character.name || character.level!==snapshot.character.level || character.capturedAt!==snapshot.capturedAtUtc || details.characterCapturedAt!==snapshot.capturedAtUtc) throw new Error('Export identity mismatch');
  const keys = rows => JSON.stringify(rows.map(i => [i.slot,i.itemId,i.displayId,i.quality,i.itemLevel]).sort((a,b) => a[0]-b[0]));
  if (keys(character.equipment)!==keys(snapshot.equipment)) throw new Error('Export equipment mismatch');
  const resource = async (name,bytes) => {
    if (!/^[a-zA-Z0-9_-]+\.(bin|png|gltf)$/.test(name)) throw new Error('Unsafe export resource');
    const data = await fs.readFile(path.join(assets,name));
    if (!data.length || (bytes!==undefined && data.length!==bytes)) throw new Error('Incomplete export resource');
    if (name.endsWith('.png') && data.subarray(0,8).toString('hex')!=='89504e470d0a1a0a') throw new Error('Invalid exported image');
  };
  for (const item of character.equipment) {
    await resource(item.icon);
    if (!details.items.some(row => row.slot===item.slot && row.itemId===item.itemId && row.name?.fr && row.name?.en)) throw new Error('Missing localized item details');
  }
  const modelNames = ['flowmage.gltf',...character.attached.map(a => a.url)];
  for (const name of modelNames) {
    await resource(name);
    const gltf = await read(name);
    if (!gltf.meshes?.length) throw new Error('Empty exported model');
    for (const entry of [...gltf.buffers ?? [],...gltf.images ?? []]) await resource(entry.uri,entry.byteLength);
    if (name==='flowmage.gltf') {
      if (!gltf.animations?.[0]?.channels?.length) throw new Error('Missing idle animation');
      for (const attachment of character.attached) if (!gltf.nodes?.some(node => node.name===attachment.bone)) throw new Error('Missing attachment bone');
    }
  }
  character.statistics = statistics;
  await writeJsonAtomic(path.join(assets,'character.json'),character);
  await writeJsonAtomic(path.join(assets,'statistics.json'),statistics);
}

async function buildCharacterExport(stage,snapshot,catalog,statistics,{clientRoot,signal,runStep=runExportStep,details}={}) {
  enrichSnapshot(snapshot,catalog);
  await writeJsonAtomic(path.join(stage,'flowmage.json'),snapshot,signal);
  await writeJsonAtomic(path.join(stage,'item-catalog.json'),catalog,signal);
  await runStep('prepare.cjs',[clientRoot],stage,signal);
  await runStep('export.cjs',[clientRoot],stage,signal);
  if (details) {
    let resolved = {...details,characterCapturedAt:snapshot.capturedAtUtc};
    try {
      for (const locale of ['fr','en']) await runStep('resolve-item-tables.cjs',[clientRoot,locale],stage,signal);
      const tables = Object.fromEntries(await Promise.all(['fr','en'].map(async locale =>
        [locale,JSON.parse(await fs.readFile(path.join(stage,'item-tables-'+locale+'.json'),'utf8'))])));
      resolved = enrichItemDetails(snapshot,catalog,tables,details);
    } catch {
      signal?.throwIfAborted();
      // Missing optional client tables do not discard valid geometry; unresolved
      // tooltips retain their explicit incomplete state from the catalog fallback.
    }
    await writeJsonAtomic(path.join(stage,'assets/item-details.json'),resolved,signal);
  } else {
    for (const [script,args] of [['resolve-item-tables.cjs',[clientRoot,'fr']],['resolve-item-tables.cjs',[clientRoot,'en']],['item-details.cjs',[]]]) {
      await runStep(script,args,stage,signal);
    }
  }
  await validateExport(stage,snapshot,statistics);
}


module.exports = {enrichSnapshot,enrichItemDetails,runExportStep,validateExport,buildCharacterExport};
