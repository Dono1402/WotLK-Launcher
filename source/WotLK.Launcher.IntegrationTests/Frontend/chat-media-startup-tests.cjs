// Real Chromium decoding with fixture-only responses and a controlled header delay.
// Optional ATLAS_CHAT_MEDIA_BASELINE points to an older chat-media.js for comparison.
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');
const {chromium} = require(path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));
const {viewerAudioBytes, viewerVideoBytes} = require('./chat-fixtures.cjs');
const repo = path.resolve(__dirname, '../../..');
const output = path.resolve(repo, process.env.ATLAS_CHAT_TEST_OUTPUT || 'artifacts/media-startup');
const checks = [], errors = [], samples = [];
function check(name, condition) { assert.ok(condition, name); checks.push(name); console.log('PASS '+name); }
const delay = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

(async () => {
  const script = await fs.readFile(path.join(repo, 'source/WotLK.Launcher/Assets/Chat/chat-media.js'), 'utf8');
  const css = await fs.readFile(path.join(repo, 'source/WotLK.Launcher/Assets/Chat/chat-media.css'), 'utf8');
  const baseline = process.env.ATLAS_CHAT_MEDIA_BASELINE ? await fs.readFile(process.env.ATLAS_CHAT_MEDIA_BASELINE, 'utf8') : null;
  const browser = await chromium.launch({executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true,
    args: ['--disable-gpu', '--no-first-run', '--disable-background-networking']});
  async function fixturePage(source, kind, {latency = 250, distant = false, hidden = false} = {}) {
    const page = await browser.newPage({viewport: {width: 900, height: 700}}), requests = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
      if (!route.request().url().startsWith('https://atlas-chat-media.invalid/startup/')) return route.abort();
      requests.push(route.request().url());
      await delay(latency);
      const bytes = kind === 'audio' ? viewerAudioBytes : viewerVideoBytes;
      const range = /^bytes=(\d+)-(\d*)$/.exec(route.request().headers().range || '');
      const start = range ? Number(range[1]) : 0, end = range?.[2] ? Math.min(Number(range[2]), bytes.length-1) : bytes.length-1;
      await route.fulfill({status: range ? 206 : 200, body: bytes.subarray(start, end+1), headers: {
        'Content-Type': kind === 'audio' ? 'audio/wav' : 'video/webm', 'Accept-Ranges': 'bytes',
        'Cache-Control': 'no-store', 'Access-Control-Allow-Origin': '*',
        ...(range ? {'Content-Range': `bytes ${start}-${end}/${bytes.length}`} : {})
      }});
    });
    await page.setContent('<!doctype html><html><body style="margin:30px"><main style="width:400px"></main><button id="outside">Outside</button></body></html>');
    await page.addStyleTag({content: css}); await page.addScriptTag({content: source});
    await page.evaluate(({kind, distant, hidden}) => {
      if (hidden) Object.defineProperty(document, 'hidden', {configurable: true, value: true});
      const player = document.createElement(kind); player.preload = 'none'; player.muted = true;
      player.src = 'https://atlas-chat-media.invalid/startup/fixture.'+(kind === 'audio' ? 'wav' : 'webm');
      const shell = AtlasChatMedia.create(player); if (distant) shell.style.cssText = 'position:absolute;top:6000px;width:400px';
      document.querySelector('main').append(shell); window.media = {player, shell};
    }, {kind, distant, hidden});
    return {page, requests};
  }
  async function clickToPlaying(page) {
    return page.evaluate(() => new Promise((resolve, reject) => {
      const {player, shell} = media, start = performance.now();
      const timeout = setTimeout(() => reject(new Error('Native playing event timed out')), 5000);
      player.addEventListener('playing', () => { clearTimeout(timeout); resolve(performance.now()-start); }, {once: true});
      shell.querySelector('.media-play').click();
    }));
  }
  try {
    for (let run = 0; run < 3; run++) for (const kind of ['audio', 'video']) {
      for (const [version, source] of baseline ? [['before', baseline], ['after', script]] : [['after', script]]) {
        const {page, requests} = await fixturePage(source, kind);
        await page.waitForTimeout(600);
        const beforeClick = await page.evaluate(() => ({paused: media.player.paused, time: media.player.currentTime,
          readyState: media.player.readyState, preload: media.player.preload}));
        const prefetchedRequests = requests.length, clickToPlayingMs = await clickToPlaying(page);
        await page.waitForFunction(() => media.player.currentTime > .03);
        if (version === 'after') {
          check(`${kind} run ${run}: prepares before the click without autoplay`, beforeClick.paused && beforeClick.time === 0 && beforeClick.readyState >= 3 && prefetchedRequests > 0);
          check(`${kind} run ${run}: starts without waiting for another fixture response`, clickToPlayingMs < 200);
        }
        await page.evaluate(() => AtlasChatMedia.pause(media.shell));
        const resumeMs = await clickToPlaying(page);
        samples.push({version, kind, run, headerDelayMs: 250, preparationLeadMs: 600, beforeClick, prefetchedRequests, clickToPlayingMs, resumeMs});
        await page.close();
      }
    }
    for (const kind of ['audio', 'video']) {
      const {page} = await fixturePage(script, kind, {latency: 900});
      await page.waitForFunction(() => media.player.preload === 'metadata');
      await page.locator('.media-player').hover();
      check(`${kind}: hover promotes only the paused target`, await page.evaluate(() => media.player.preload === 'auto' && media.player.paused && media.player.currentTime === 0));
      await page.mouse.move(880, 680);
      check(`${kind}: leaving cancels extra preparation`, await page.evaluate(() => media.player.preload === 'metadata'));
      await page.locator('.media-play').focus();
      check(`${kind}: keyboard focus prepares playback`, await page.evaluate(() => media.player.preload === 'auto' && media.player.paused));
      await page.evaluate(() => { Object.defineProperty(document, 'hidden', {configurable: true, value: true}); document.dispatchEvent(new Event('visibilitychange')); });
      check(`${kind}: hiding stops extra preparation`, await page.evaluate(() => media.player.preload === 'metadata'));
      await page.locator('#outside').focus(); await page.locator('.media-play').focus();
      check(`${kind}: hidden document ignores preparation intent`, await page.evaluate(() => media.player.preload === 'metadata'));
      await page.evaluate(() => { delete document.hidden; document.dispatchEvent(new Event('visibilitychange')); });
      await page.waitForFunction(() => media.player.readyState >= 3);
      await page.evaluate(() => { media.player.currentTime = 1; media.player.volume = .4; media.player.playbackRate = 1.25; });
      await page.locator('.media-player').hover();
      check(`${kind}: ready media keeps time, volume, rate and preload`, await page.evaluate(() => media.player.currentTime === 1 && media.player.volume === .4 && media.player.playbackRate === 1.25 && media.player.preload === 'metadata' && media.player.paused));
      await page.close();
      const distant = await fixturePage(script, kind, {distant: true});
      await distant.page.waitForTimeout(100);
      check(`${kind}: distant history performs no request`, distant.requests.length === 0 && await distant.page.evaluate(() => media.player.preload === 'none'));
      await distant.page.close();
      const hidden = await fixturePage(script, kind, {hidden: true});
      await hidden.page.waitForTimeout(100);
      check(`${kind}: hidden initial view performs no request`, hidden.requests.length === 0);
      await hidden.page.evaluate(() => { delete document.hidden; document.dispatchEvent(new Event('visibilitychange')); });
      await hidden.page.waitForFunction(() => media.player.readyState >= 3);
      check(`${kind}: becoming visible prepares the pending player`, hidden.requests.length > 0 && await hidden.page.evaluate(() => media.player.paused && media.player.preload === 'metadata'));
      await hidden.page.close();
    }
    const {page: disposal} = await fixturePage(script, 'audio', {latency: 900});
    await disposal.locator('.media-player').hover();
    await disposal.evaluate(() => AtlasChatMedia.dispose(media.shell));
    check('Disposal cancels preparation and disconnects the controller', await disposal.evaluate(() => media.player.preload === 'metadata' && media.player.paused && AtlasChatMedia.shellFor(media.player) === null));
    await disposal.close();
    const {page: prepared} = await fixturePage(script, 'video', {latency: 900});
    await prepared.locator('.media-player').hover();
    check('An unready video prepares on hover', await prepared.evaluate(() => media.player.preload === 'auto'));
    await prepared.waitForFunction(() => media.player.readyState >= 3 && media.player.preload === 'metadata');
    check('Canplay ends extra preparation while hover remains, without autoplay', await prepared.evaluate(() => media.player.paused && media.player.currentTime === 0));
    await prepared.close();
    check('No uncaught browser errors', errors.length === 0);
    await fs.mkdir(output, {recursive: true});
    await fs.writeFile(path.join(output, 'media-startup.json'), JSON.stringify({passed: checks.length, checks, samples, errors,
      browser: browser.version(), externalNetwork: false, userSession: false, limitations: 'Synthetic 250ms header delay; small WAV/WebM fixtures; does not measure production files or audible device startup.'}, null, 2));
    console.log(JSON.stringify({passed: checks.length, samples}, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
