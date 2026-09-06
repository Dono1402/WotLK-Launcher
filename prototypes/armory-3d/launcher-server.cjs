const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const {timingSafeEqual} = require('node:crypto');
const {LauncherArmory} = require('./launcher-armory.cjs');
const {resolveFile} = require('./viewer-assets.cjs');
const {createLauncherRpc} = require('./launcher-rpc.cjs');
const runtimePaths = require('./runtime-paths.cjs');

const {assetRoot} = runtimePaths;
const types = {'.html':'text/html; charset=utf-8','.css':'text/css','.js':'text/javascript','.mjs':'text/javascript',
  '.json':'application/json','.png':'image/png','.jpg':'image/jpeg','.ttf':'font/ttf','.gltf':'model/gltf+json','.bin':'application/octet-stream'};
const fixed = new Map([
  ['/',path.join(__dirname,'launcher.html')],['/launcher.js',path.join(__dirname,'launcher.js')],
  ['/launcher.css',path.join(__dirname,'launcher.css')],['/character-labels.mjs',path.join(__dirname,'character-labels.mjs')],
  ['/inter-fonts.css',path.join(__dirname,'inter-fonts.css')],
  ...['Regular','Medium','SemiBold','ExtraBold'].map(face => [`/fonts/Inter-${face}.ttf`,
    path.join(assetRoot,`Fonts/Inter-${face}.ttf`)]),
  ['/banner.png',path.join(assetRoot,'Launcher/visuals/icecrown-citadel.png')]
]);

function createLauncherServer({key,armory}) {
  if (!/^[a-f0-9]{64}$/.test(key)) throw new Error('Missing private launcher bridge key');
  return http.createServer(async (req,res) => {
    res.setHeader('Cache-Control','no-store'); res.setHeader('X-Content-Type-Options','nosniff');
    res.setHeader('Referrer-Policy','no-referrer');
    res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; frame-src 'self'; frame-ancestors 'self'; worker-src 'self' blob:; object-src 'none'; base-uri 'none'; form-action 'none'");
    const provided = req.headers['x-atlas-armory-key'];
    if (!['127.0.0.1','localhost'].includes(req.headers.host?.split(':')[0]) || typeof provided!=='string'
      || !/^[a-f0-9]{64}$/.test(provided) || !timingSafeEqual(Buffer.from(provided),Buffer.from(key))) { res.writeHead(403); return res.end(); }
    if (!['GET','HEAD'].includes(req.method)) { res.writeHead(405); return res.end(); }
    const send = value => {
      const body = JSON.stringify(value);
      res.writeHead(200,{'Content-Type':'application/json','Content-Length':Buffer.byteLength(body)});
      res.end(req.method==='HEAD' ? undefined : body);
    };
    const url = req.url.split('?')[0];
    try {
      if (url==='/health.json') return send({protocol:'atlas-launcher-armory',version:1});
      if (url==='/characters.json') {
        if (req.method==='GET' && new URLSearchParams(req.url.split('?')[1]).get('refresh')==='1') armory.retry?.();
        return send(armory.list());
      }
      if (url==='/viewer-config.json') return send({locale:'fr',source:'launcher'});
      let file = fixed.get(url);
      const character = url.match(/^\/characters\/([1-9][0-9]{0,9})\/(view|armory\.json|statistics\.json|snapshots\/([a-f0-9]{32})\/assets\/([a-zA-Z0-9_-]+\.(?:json|png|gltf|bin)))$/);
      if (character) {
        const [,,route,revision,name] = character;
        const state = armory.entry(character[1]);
        if (!state) { res.writeHead(404); return res.end(); }
        const base = `/characters/${character[1]}/snapshots/${state.revision}/assets/`;
        if (route==='armory.json') return send({revision:state.revision,assetBase:base,modelReady:state.modelReady,
          modelStatus:state.modelStatus || (state.modelReady ? 'ready' : 'unavailable'),characterId:character[1]});
        if (route==='statistics.json') return send({status:state.character.statistics?'ready':'unavailable',record:state.character.statistics});
        if (route==='view') file = path.join(__dirname,'index.html');
        else {
          if (revision!==state.revision) { res.writeHead(404); return res.end(); }
          if (name==='character.json') return send(state.character);
          if (name==='item-details.json') return send(state.details);
          if (name.endsWith('.json')) { res.writeHead(404); return res.end(); }
          const itemIcon = state.character.equipment.some(item => item.icon===name && name===`icon-${item.itemId}.png`);
          if (itemIcon && state.iconFiles?.[name]) file = state.iconFiles[name];
          else if (state.modelReady && state.assetDir) file = path.join(state.assetDir,name);
          else { res.writeHead(404); return res.end(); }
        }
      }
      const icon = url.match(/^\/class-icons\/(1|2|3|4|5|6|7|8|9|11)\.jpg$/);
      if (icon) file = path.join(assetRoot,'Launcher/class-icons',icon[1]+'.jpg');
      if (!file && ['/app.js','/i18n.mjs','/character-stats.mjs','/style.css','/lucide.js'].includes(url)) file = resolveFile(url);
      if (!file && url.startsWith('/three/')) file = resolveFile(url);
      if (!file) { res.writeHead(404); return res.end(); }
      const bytes = await fs.readFile(file);
      res.writeHead(200,{'Content-Type':types[path.extname(file)] || 'application/octet-stream','Content-Length':bytes.length});
      res.end(req.method==='HEAD' ? undefined : bytes);
    } catch (error) { res.writeHead(error.code==='ENOENT' ? 404 : 503); res.end(); }
  });
}

async function main() {
  const config = runtimePaths.publicMode
    ? {source:'rpc',clientRoot:runtimePaths.clientRoot,intervalMs:60000}
    : await require('./statistics-sync.cjs').readSyncConfig();
  if (!config) throw new Error('Local armory configuration unavailable');
  const rpc = createLauncherRpc({onShutdown:() => { void stop(); }});
  const armory = new LauncherArmory(Number(process.env.ATLAS_ARMORY_ACCOUNT_ID),config,
    runtimePaths.publicMode ? {loadRoster:rpc.loadRoster,loadCatalog:rpc.loadCatalog} : {});
  const server = createLauncherServer({key:process.env.ATLAS_ARMORY_BRIDGE_KEY,armory});
  let stopping = false;
  const stop = async () => {
    if (stopping) return;
    stopping = true;
    rpc.close();
    server.closeAllConnections(); server.close();
    await armory.stop();
    process.stdin.destroy();
  };
  process.stdin.resume();
  process.once('SIGTERM',stop); process.once('SIGINT',stop);
  server.listen(0,'127.0.0.1',() => {
    console.log('ATLAS_ARMORY_READY '+JSON.stringify({port:server.address().port}));
    void armory.start().catch(stop);
  });
}
if (require.main===module) main().catch(() => { console.error('Local armory startup failed'); process.exitCode=1; });
module.exports = {createLauncherServer};
