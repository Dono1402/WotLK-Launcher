const fs=require('node:fs'),path=require('node:path');
const {stage, live, writeReport} = require('./validation-paths.cjs');
const {lua,lauxlib,lualib,to_luastring,to_jsstring}=require('fengari');
const lp=require('luaparse');
const read=p=>fs.readFileSync(p,'utf8').replace(/^\uFEFF/,'');
function luaRun(source,globals={}) {
  const state=lauxlib.luaL_newstate();
  // Do not open io/os/package/debug at all: deleting their globals after
  // luaL_openlibs would leave capabilities reachable from the debug registry.
  for (const [name,open] of [['_G',lualib.luaopen_base],['table',lualib.luaopen_table],
    ['string',lualib.luaopen_string],['math',lualib.luaopen_math],
    ['utf8',lualib.luaopen_utf8],['coroutine',lualib.luaopen_coroutine]]) {
    lauxlib.luaL_requiref(state,to_luastring(name),open,1);lua.lua_pop(state,1);
  }
  for(const name of ['dofile','loadfile']) {lua.lua_pushnil(state);lua.lua_setglobal(state,to_luastring(name));}
  for(const [name,value] of Object.entries(globals)) {lua.lua_pushstring(state,to_luastring(value));lua.lua_setglobal(state,to_luastring(name));}
  // Tests use only the fact that newproxy returns a non-string userdata.
  lua.lua_newuserdata(state,1);lua.lua_setglobal(state,to_luastring('SYNTHETIC_PROXY'));
  const compiled=lauxlib.luaL_loadbuffer(state,to_luastring(source),null,to_luastring('@offline-synthetic-test'));
  let status=compiled;if(status===lua.LUA_OK) status=lua.lua_pcall(state,0,lua.LUA_MULTRET,0);
  if(status!==lua.LUA_OK){const err=to_jsstring(lua.lua_tostring(state,-1));lua.lua_close(state);throw new Error(err);}
  lua.lua_getglobal(state,to_luastring('SYNTHETIC_CHECK_COUNT'));
  const checks=lua.lua_tonumber(state,-1);lua.lua_close(state);return checks;
}
const target=path.join(stage,'extracted/auctionator-10.2.24-wrath/Auctionator');
const wa=path.join(stage,'extracted/weakauras-legacy-5.12.9/WeakAuras');

function registration(file){
  const source=read(file).replace(/\r\n/g,'\n');
  const loadFrameDeclaration=source.indexOf('local loadFrame = CreateFrame("Frame")');
  const start=source.indexOf('if WeakAuras.IsRetail() then',loadFrameDeclaration);
  const end=source.indexOf('if WeakAuras.IsWrathOrCata() then',start);
  if(start<0 || end<start) throw new Error('Cannot identify exact event-registration block');
  return source.slice(start,end);
}
const result={runAtUtc:new Date().toISOString(),runtime:'Fengari 0.1.5 (Lua 5.3), bounded mocks, no real WoW runtime',syntheticChecks:0,libStubTests:[],inlineLua:[]};
result.syntheticChecks=luaRun(read(path.join(__dirname,'test-migrations.lua')),{
  NEW_VARIABLES:read(path.join(target,'Source/Variables/Main.lua')),
  OLD_VARIABLES:read(path.join(live,'Auctionator/Source/Variables/Main.lua')),
  NEW_GROUPS_MAIN:read(path.join(target,'Source/Groups/Main.lua')),
  WA_NEW_REGISTRATION:registration(path.join(wa,'WeakAuras.lua')),
  WA_OLD_REGISTRATION:registration(path.join(live,'WeakAuras/WeakAuras.lua')),
});
for(const version of ['weakauras-legacy-5.12.9','weakauras-5.13.1']) {
  const lib=path.join(stage,'extracted',version,'WeakAuras/Libs/LibStub');
  for(const test of ['test.lua','test2.lua','test3.lua','test4.lua']) {
    // Upstream tests assign debugstack = debug.traceback but never call it.
    // Supply only that inert mock, never a registry/introspection capability.
    const wrapper=`local realAssert=assert\nSYNTHETIC_CHECK_COUNT=0\ndebug={traceback=function() return 'offline synthetic traceback' end}\nfunction assert(...) SYNTHETIC_CHECK_COUNT=SYNTHETIC_CHECK_COUNT+1 return realAssert(...) end\nfunction newproxy() return SYNTHETIC_PROXY end\nfunction loadfile(p) realAssert(p=='../LibStub.lua') return load(LIBSTUB_SOURCE,'@LibStub.lua') end\n`;
    const assertions=luaRun(wrapper+read(path.join(lib,'tests',test)),{LIBSTUB_SOURCE:read(path.join(lib,'LibStub.lua'))});
    result.libStubTests.push({version,test,assertions,status:'pass'});
  }
}
const xml=JSON.parse(read(path.join(stage,'xml-validation.json')));
for(const snippet of xml.inlineLua) {
  let status='pass',error=null;
  try{lp.parse(snippet.code,{luaVersion:'5.1',encodingMode:'none'});}catch(e){status='fail';error=e.message;}
  result.inlineLua.push({path:snippet.path,node:snippet.node,status,error});
}
writeReport('offline-tests.json',result);
console.log(JSON.stringify({syntheticChecks:result.syntheticChecks,libStubTests:result.libStubTests.length,libStubAssertions:result.libStubTests.reduce((n,t)=>n+t.assertions,0),xmlFiles:xml.xmlCount,inlineLua:result.inlineLua.length,inlineLuaErrors:result.inlineLua.filter(s=>s.status==='fail').length}));
if(result.inlineLua.some(s=>s.status==='fail'))process.exitCode=1;
