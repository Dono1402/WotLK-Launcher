const fs = require('node:fs/promises');
const path = require('node:path');
const {spawn} = require('node:child_process');
const {createHash,randomUUID} = require('node:crypto');
const {readArmory,revisionDirectory} = require('./armory-cache.cjs');
const {writeJsonAtomic} = require('./armory-data.cjs');

const {dataRoot:shared} = require('./runtime-paths.cjs');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const validPng = bytes => bytes.length>8 && bytes.subarray(0,8).toString('hex')==='89504e470d0a1a0a';

async function verifiedPng(file,outputDir,sha256) {
  try {
    const relative = path.relative(await fs.realpath(outputDir),await fs.realpath(file));
    if (relative.startsWith('..') || path.isAbsolute(relative)) return false;
    const bytes = await fs.readFile(file);
    return validPng(bytes) && (!sha256 || digest(bytes)===sha256);
  } catch { return false; }
}

async function cachedIcons(equipment,outputDir) {
  const result = {};
  const wanted = new Map(equipment.map(item => [item.itemId,item]));
  const directories = [];
  try { directories.push(revisionDirectory((await readArmory(outputDir)).revision,outputDir)); } catch { }
  directories.push(outputDir);
  try {
    for (const entry of await fs.readdir(path.join(outputDir,'snapshots'),{withFileTypes:true})) {
      if (entry.isDirectory() && /^[a-f0-9]{32}$/.test(entry.name)) directories.push(revisionDirectory(entry.name,outputDir));
    }
  } catch { }
  for (const directory of new Set(directories)) {
    try {
      const character = JSON.parse(await fs.readFile(path.join(directory,'assets/character.json'),'utf8'));
      for (const item of character.equipment || []) {
        const current = wanted.get(item.itemId);
        const name = `icon-${item.itemId}.png`;
        if (!current || item.displayId!==current.displayId || item.icon!==name || result[name]) continue;
        const file = path.join(directory,'assets',name);
        if (await verifiedPng(file,outputDir)) result[name] = file;
      }
    } catch { }
  }
  try {
    const directory = path.join(outputDir,'launcher-icons');
    const manifest = JSON.parse(await fs.readFile(path.join(directory,'icons.json'),'utf8'));
    if (manifest.schemaVersion!==1 || manifest.source!=='local-client-item' || manifest.build!=='3.4.3.54261') return result;
    for (const entry of manifest.items || []) {
      const name = `icon-${entry.itemId}.png`;
      if (!wanted.has(entry.itemId) || entry.icon!==name || result[name] || !Number.isSafeInteger(entry.iconFileDataId)
        || entry.iconFileDataId<1 || !/^[a-f0-9]{64}$/.test(entry.sha256)) continue;
      const file = path.join(directory,name);
      if (await verifiedPng(file,outputDir,entry.sha256)) result[name] = file;
    }
  } catch { }
  return result;
}

async function iconsFor(row,{outputDir=shared,clientRoot,signal}={}) {
  const equipment = row.snapshot.equipment;
  let icons = await cachedIcons(equipment,outputDir);
  const missing = [...new Set(equipment.filter(item => !icons[`icon-${item.itemId}.png`]).map(item => item.itemId))];
  if (!missing.length || !clientRoot) return icons;
  if (!path.isAbsolute(clientRoot) || missing.length>19 || missing.some(id => !Number.isSafeInteger(id) || id<1 || id>0xffffffff)) return icons;
  signal?.throwIfAborted();
  try {
    await new Promise((resolve,reject) => {
      let failure;
      const child = spawn(process.execPath,[__filename,'--extract',clientRoot,outputDir,missing.join(',')],
        {cwd:__dirname,windowsHide:true,stdio:'ignore',signal:AbortSignal.any([signal || new AbortController().signal,AbortSignal.timeout(120000)])});
      child.once('error',error => { failure = error; });
      child.once('close',code => failure ? reject(failure) : code===0 ? resolve() : reject(new Error('Local icon extraction failed')));
    });
    icons = await cachedIcons(equipment,outputDir);
  } catch { signal?.throwIfAborted(); }
  return icons;
}

async function extract(clientRoot,outputDir,idText) {
  if (!path.isAbsolute(clientRoot) || !path.isAbsolute(outputDir) || !/^[1-9][0-9]*(?:,[1-9][0-9]*){0,18}$/.test(idText)) throw new Error('Invalid local icon extraction');
  const ids = [...new Set(idText.split(',').map(Number))];
  if (ids.some(id => !Number.isSafeInteger(id) || id>0xffffffff)) throw new Error('Invalid item ID');
  const {client,vendor} = await require('./local-client.cjs').openClient(clientRoot);
  const db2 = require(path.join(vendor,'casc/db2.js'));
  const BLP = require(path.join(vendor,'casc/blp.js'));
  const items = await db2.Item.getAllRows();
  const directory = path.join(outputDir,'launcher-icons');
  await fs.mkdir(directory,{recursive:true});
  let previous = [];
  try { previous = JSON.parse(await fs.readFile(path.join(directory,'icons.json'),'utf8')).items || []; } catch { }
  const entries = new Map(previous.filter(item => !ids.includes(item.itemId)).map(item => [item.itemId,item]));
  for (const itemId of ids) {
    const iconFileDataId = items.get(itemId)?.IconFileDataID;
    if (!Number.isSafeInteger(iconFileDataId) || iconFileDataId<1) continue;
    try {
      const blp = new BLP(await client.getFile(iconFileDataId,false,true,false));
      const bytes = (await blp.toPNG(15)).raw;
      if (!validPng(bytes)) continue;
      const icon = `icon-${itemId}.png`;
      const temporary = path.join(directory,icon+'.'+randomUUID()+'.tmp');
      try { await fs.writeFile(temporary,bytes,{flag:'wx'}); await fs.rename(temporary,path.join(directory,icon)); }
      finally { await fs.unlink(temporary).catch(error => { if (error.code!=='ENOENT') throw error; }); }
      entries.set(itemId,{itemId,iconFileDataId,icon,sha256:digest(bytes)});
    } catch { /* Keep every other verified icon if this individual client asset is missing. */ }
  }
  await writeJsonAtomic(path.join(directory,'icons.json'),{schemaVersion:1,source:'local-client-item',build:client.build.Version,items:[...entries.values()]});
}

if (require.main===module) {
  if (process.argv[2]!=='--extract' || process.argv.length!==6) process.exitCode=1;
  else extract(...process.argv.slice(3)).catch(() => { process.exitCode=1; });
}
module.exports = {iconsFor,cachedIcons};
