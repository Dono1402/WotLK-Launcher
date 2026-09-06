const fs = require('node:fs/promises');
const path = require('node:path');
const {captureStatistics,writeJsonAtomic} = require('./capture-statistics.cjs');

const {dataRoot:output} = require('./runtime-paths.cjs');

async function readSyncConfig(file=path.join(output,'statistics-sync.json')) {
  let config;
  try { config = JSON.parse(await fs.readFile(file,'utf8')); }
  catch (error) { if (error.code==='ENOENT') return null; throw error; }
  if (config?.enabled===false) return null;
  if (config?.schemaVersion!==1 || config.enabled!==true ||
      !/^[A-Za-z0-9_.-]+@[A-Za-z0-9.-]+$/.test(config.host || '') ||
      typeof config.key!=='string' || !path.isAbsolute(config.key) ||
      !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{3})?Z$/.test(config.verifiedAfter || '') ||
      !Number.isFinite(Date.parse(config.verifiedAfter))) throw new Error('Invalid private statistics sync configuration');
  const intervalMs = config.intervalMs ?? 60000;
  if (!Number.isInteger(intervalMs) || intervalMs<60000 || intervalMs>3600000) throw new Error('Statistics sync interval must be at least 60 seconds');
  if (config.clientRoot!==undefined && (typeof config.clientRoot!=='string' || !path.isAbsolute(config.clientRoot))) throw new Error('Invalid local game client path');
  return {host:config.host,key:config.key,verifiedAfter:config.verifiedAfter,intervalMs,...config.clientRoot ? {clientRoot:config.clientRoot} : {}};
}

function createStatisticsSync({capture,onStatus=async () => {},intervalMs=60000,now=Date.now,setTimer=setTimeout,clearTimer=clearTimeout}) {
  let timer, running, controller, stopped = true, failures = 0;
  function run() {
    if (stopped || running) return running;
    controller = new AbortController();
    const signal = controller.signal;
    running = (async () => {
      const started = now();
      let status;
      try {
        const result = await capture({signal});
        failures = 0;
        status = {status:result.status,savedAt:result.savedAt ?? null,reason:result.reason ?? null};
      } catch (error) {
        failures++;
        status = {status:'error',reason:error.code==='ARMORY_REFRESH_REQUIRED'?'snapshot-mismatch':'capture-failed'};
      }
      if (stopped) return;
      const delay = Math.max(0,Math.min(intervalMs*2**Math.min(failures,3),Math.max(intervalMs,300000))-(now()-started));
      const checkedAt = new Date(now()).toISOString();
      try { await onStatus({...status,checkedAt,nextCheckAt:new Date(now()+delay).toISOString()}); }
      catch { /* Local diagnostic failures must not stop cache refreshes. */ }
      if (!stopped) {
        timer = setTimer(run,delay);
        timer?.unref?.();
      }
    })().finally(() => { running = undefined; });
    return running;
  }
  return {
    start() { if (stopped) { stopped = false; return run(); } return running; },
    async stop() { stopped = true; clearTimer(timer); controller?.abort(); await running; }
  };
}

async function attachStatisticsSync(server,{configFile=path.join(output,'statistics-sync.json'),outputDir=output}={}) {
  let config;
  try { config = await readSyncConfig(configFile); }
  catch { console.error('Statistics sync disabled: invalid private configuration'); return null; }
  if (!config || !server.listening) return null;
  const sync = createStatisticsSync({
    intervalMs:config.intervalMs,
    capture:({signal}) => config.clientRoot
      ? require('./sync-armory.cjs').syncArmory({...config,outputDir,signal})
      : captureStatistics({...config,outputDir,signal}),
    onStatus:status => writeJsonAtomic(path.join(outputDir,'statistics-sync-status.json'),status)
  });
  server.once('close',() => { void sync.stop(); });
  void sync.start();
  return sync;
}

module.exports = {readSyncConfig,createStatisticsSync,attachStatisticsSync};
