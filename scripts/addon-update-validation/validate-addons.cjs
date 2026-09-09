// Offline audit only. Never executes addon code or writes to the live installation.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const {stage, live, writeReport} = require('./validation-paths.cjs');
const luaparse = require('luaparse');
const {lua,lauxlib,to_luastring,to_jsstring} = require('fengari');

const digest = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const slash = value => value.split(path.sep).join('/');
const walk = dir => fs.readdirSync(dir, {withFileTypes:true}).flatMap(e => {
  const file = path.join(dir, e.name);
  if (e.isSymbolicLink()) throw new Error(`Unexpected extracted symlink ${file}`);
  return e.isDirectory() ? walk(file) : [file];
});
const read = file => fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '');
const arrayField = (metadata, field) => (metadata[field] || '').split(',').map(x => x.trim()).filter(Boolean);
// luaparse 0.3.1 rejects semicolons after break in 5.1 mode. Mask exactly these
// token-delimited semicolons in memory; do not rewrite downloaded source files.
function maskBreakSemicolons(source) {
  const lexer=luaparse.parse(source,{wait:true,ranges:true,luaVersion:'5.1',encodingMode:'none'});
  let previous=null,current,positions=[];
  while((current=lexer.lex()).type!==luaparse.tokenTypes.EOF) {
    if(previous?.type===luaparse.tokenTypes.Keyword && previous.value==='break' && current.value===';') positions.push(current.range[0]);
    previous=current;
  }
  for(const position of positions) source=source.slice(0,position)+' '+source.slice(position+1);
  return {source,positions};
}
function parseToc(file) {
  const metadata = {}, references = [];
  for (const text of read(file).split(/\r?\n/)) {
    const line = text.trim(), match = line.match(/^##\s*([^:]+):\s*(.*)$/);
    if (match) metadata[match[1].trim()] = match[2];
    else if (line && !line.startsWith('#')) references.push(line);
  }
  return {metadata, references};
}
function loadClosure(start, root) {
  const visited = new Set(), missing = [], escaped = [];
  const visit = file => {
    if (visited.has(file)) return;
    visited.add(file);
    let references = [];
    if (file.endsWith('.toc')) references = parseToc(file).references;
    if (file.endsWith('.xml')) {
      const xml = read(file).replace(/<!--[\s\S]*?-->/g, '');
      references = [...xml.matchAll(/<(?:Include|Script)\b[^>]*\bfile\s*=\s*['"]([^'"]+)['"][^>]*>/gi)].map(x => x[1]);
    }
    for (const ref of references) {
      const normalized = ref.replace(/[\\/]/g, path.sep);
      const target = path.resolve(path.dirname(file), normalized);
      const relative = path.relative(root, target);
      if (relative.startsWith('..') || path.isAbsolute(relative)) {
        escaped.push({from:slash(path.relative(root,file)),reference:ref});
      } else if (!fs.existsSync(target) || !fs.statSync(target).isFile()) {
        missing.push({from:slash(path.relative(root,file)),reference:ref});
      } else visit(target);
    }
  };
  visit(start);
  return {files:[...visited].map(f=>slash(path.relative(root,f))).sort(),missing,escaped};
}
const report = {auditedAtUtc:new Date().toISOString(),scope:'offline staging only',luaParser:'luaparse 0.3.1',luaVersion:'5.1',packages:[],protectedBaseline:[]};
const pinnedPackages = JSON.parse(read(path.join(__dirname,'../../docs/update-preparation/2026-09-09/addons-manifest.json'))).packages;
for (const id of ['weakauras','auctionator','weakauras-legacy']) {
  const version = {'weakauras':'5.13.1','auctionator':'10.2.24-wrath','weakauras-legacy':'5.12.9'}[id];
  const pinned = pinnedPackages.find(p=>p.version===version && p.id===(id==='weakauras-legacy'?'weakauras':id));
  if (!pinned) throw new Error(`Missing pinned package: ${id}`);
  const archive = {version,expectedRoots:pinned.roots,sha256:pinned.sha256,zipPath:path.join(stage,'downloads',pinned.filename)};
  const root = path.join(stage,'extracted',`${id}-${version}`);
  if (path.relative(root,fs.realpathSync(root)) !== '' || path.relative(archive.zipPath,fs.realpathSync(archive.zipPath)) !== '') {
    throw new Error(`Redirected archive/extraction path: ${id}`);
  }
  if (digest(archive.zipPath) !== archive.sha256) throw new Error(`Archive changed: ${id}`);
  const files = walk(root), fileRecords = files.map(f=>({path:slash(path.relative(root,f)),bytes:fs.statSync(f).size,sha256:digest(f)}));
  const luaErrors = [],rawLua51Warnings=[],fengariCompileErrors=[];
  const compileState=lauxlib.luaL_newstate();
  for (const file of files.filter(f=>f.endsWith('.lua'))) {
    try { luaparse.parse(read(file),{luaVersion:'5.1',encodingMode:'none'}); }
    catch (error) {
      const rel=slash(path.relative(root,file));
      const masked=maskBreakSemicolons(read(file));
      try {
        luaparse.parse(masked.source,{luaVersion:'5.1',encodingMode:'none'});
        let sameWarningInstalled=false;
        if(fs.existsSync(path.join(live,rel))) {
          try{luaparse.parse(read(path.join(live,rel)),{luaVersion:'5.1',encodingMode:'none'});}catch{sameWarningInstalled=true;}
        }
        rawLua51Warnings.push({path:rel,error:error.message,breakSemicolonsMasked:masked.positions.length,sameWarningInstalled});
      } catch (finalError) { luaErrors.push({path:rel,error:finalError.message}); }
    }
    const compileStatus=lauxlib.luaL_loadbuffer(compileState,to_luastring(read(file)),null,to_luastring('@'+file));
    if(compileStatus!==lua.LUA_OK) fengariCompileErrors.push({path:slash(path.relative(root,file)),error:to_jsstring(lua.lua_tostring(compileState,-1))});
    lua.lua_settop(compileState,0);
  }
  lua.lua_close(compileState);
  const tocs = files.filter(f=>f.endsWith('.toc')).map(file=>{
    const parsed = parseToc(file);
    const topLevel = path.relative(root,file).split(path.sep).length === 2;
    const required = [...arrayField(parsed.metadata,'Dependencies'),...arrayField(parsed.metadata,'RequiredDeps'),...arrayField(parsed.metadata,'RequiredDependencies')];
    return {path:slash(path.relative(root,file)),topLevel,metadata:parsed.metadata,
      required:required.map(name=>({name,packaged:fs.existsSync(path.join(root,name)),installed:fs.existsSync(path.join(live,name))})),
      optional:arrayField(parsed.metadata,'OptionalDeps'),...loadClosure(file,root)};
  });
  const wrathTocs = tocs.filter(t=>t.topLevel && t.path.endsWith('_Wrath.toc'));
  if (wrathTocs.length !== archive.expectedRoots.length) throw new Error(`Missing top-level Wrath TOCs for ${id}`);
  const allDepsPresent = wrathTocs.every(t=>t.required.every(d=>d.packaged || d.installed));
  const topLoadHealthy = wrathTocs.every(t=>t.missing.length===0 && t.escaped.length===0 && arrayField(t.metadata,'Interface').includes('30403'));
  const changed = [], added = [], removed = [], unchanged = [];
  const newMap = new Map(fileRecords.map(f=>[f.path,f]));
  for (const file of fileRecords) {
    const old = path.join(live,file.path);
    if (!fs.existsSync(old)) added.push(file.path);
    else if (digest(old) !== file.sha256) changed.push(file.path);
    else unchanged.push(file.path);
  }
  for (const addonRoot of archive.expectedRoots) {
    if (!fs.existsSync(path.join(live,addonRoot))) continue;
    for (const file of walk(path.join(live,addonRoot))) {
      const rel=slash(path.relative(live,file));
      if(!newMap.has(rel)) removed.push(rel);
    }
  }
  const candidateLoadedLua = new Set(wrathTocs.flatMap(t=>t.files).filter(f=>f.endsWith('.lua')));
  const oldTocs = archive.expectedRoots.map(r=>path.join(live,r,`${r}_Wrath.toc`)).filter(f=>fs.existsSync(f));
  const installedLoadedLua = new Set(oldTocs.flatMap(t=>loadClosure(t,live).files).filter(f=>f.endsWith('.lua')));
  const apiCalls = (base,list) => {
    const calls = new Set();
    for(const name of list) {
      const source = read(path.join(base,name));
      for(const match of source.matchAll(/\b(C_[A-Za-z0-9_]+\.[A-Za-z0-9_]+|[A-Z][A-Za-z0-9_]+)\s*\(/g)) calls.add(match[1]);
    }
    return calls;
  };
  const newApis=apiCalls(root,candidateLoadedLua),oldApis=apiCalls(live,installedLoadedLua);
  const result = {id,version:archive.version,zipSha256:archive.sha256,fileCount:files.length,luaCount:files.filter(f=>f.endsWith('.lua')).length,
    luaErrors,rawLua51Warnings,fengariCompileErrors,fengariNote:'Secondary parse of unmodified sources with Fengari Lua 5.3, compile only, no execution.',tocCount:tocs.length,tocs,wrathTocCount:wrathTocs.length,wrathRequiredDependenciesPresent:allDepsPresent,wrathLoadGraphHealthy:topLoadHealthy,
    loadedWrathLuaFiles:candidateLoadedLua.size,delta:{changed,added,removed,unchangedCount:unchanged.length},
    candidateCallNamesAdded:[...newApis].filter(x=>!oldApis.has(x)).sort(),candidateCallNamesRemoved:[...oldApis].filter(x=>!newApis.has(x)).sort(),
    apiScanLimitation:'Textual candidate list from Wrath load closures; includes shared and conditional code and is not proof of actual WoW API calls or runtime incompatibility.',fileRecords};
  report.packages.push(result);
  console.log(JSON.stringify({id,files:result.fileCount,lua:result.luaCount,luaErrors:luaErrors.length,rawLua51Warnings:rawLua51Warnings.length,fengariCompileErrors:fengariCompileErrors.length,tocs:tocs.length,wrathTocs:wrathTocs.length,wrathLoadGraphHealthy:topLoadHealthy,requiredDeps:allDepsPresent,changed:changed.length,added:added.length,removed:removed.length}));
}
for(const relative of ['ElvUI/Wrath/Modules/Skins/Inspect.lua','SimpleDungeonMap/SimpleDungeonMap.toc','SimpleDungeonMap/SimpleDungeonMap.lua','SimpleDungeonMap/DungeonData.lua','.atlas-addons.json']) {
  const file=path.join(live,relative);
  report.protectedBaseline.push({path:file,sha256:digest(file),bytes:fs.statSync(file).size});
}
const baseline=path.join(stage,'protected-baseline.json');
if(fs.existsSync(baseline)) {
  const expected=JSON.parse(read(baseline));
  report.protectedBaselineUnchanged=expected.every(e=>report.protectedBaseline.some(a=>a.path===e.path && a.sha256===e.sha256));
  if(!report.protectedBaselineUnchanged) throw new Error('A protected live baseline changed');
} else {
  writeReport('protected-baseline.json',report.protectedBaseline);
  report.protectedBaselineUnchanged=true;
}
writeReport('validation.json',report);
if(report.packages.some(p=>p.luaErrors.length || p.fengariCompileErrors.length || !p.wrathLoadGraphHealthy || !p.wrathRequiredDependenciesPresent)) process.exitCode=1;
