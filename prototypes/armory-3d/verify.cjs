const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const {combatDetails} = require('./tests/fixtures/combat.cjs');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || path.join(process.env.USERPROFILE, '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));

async function main() {
  const output = path.resolve(__dirname, '../../artifacts/armory-prototype');
  const instance = JSON.parse(await fs.readFile(path.join(output, 'server-instance.json'), 'utf8').catch(() => '{"port":4387}'));
  const url = process.env.ARMORY_URL || `http://127.0.0.1:${instance.port}`;
  const {characterStatsRows,statisticsModes,defaultStatisticsMode} = await import('./character-stats.mjs');
  const manifest = await (await fetch(url+'/armory.json')).json();
  const characterResponse = await fetch(url+manifest.assetBase+'character.json');
  const statisticsResponse = await fetch(url+'/statistics.json');
  assert.equal(characterResponse.ok,true);
  assert.equal(statisticsResponse.ok,true);
  const character = await characterResponse.json();
  const statistics = await statisticsResponse.json();
  if (statistics.status==='ready' && statistics.record.characterName===character.name && statistics.record.characterCapturedAt===character.capturedAt) {
    character.statistics = statistics.record;
  }
  const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--enable-unsafe-swiftshader'] });
  const results = [];
  try {
    for (const locale of ['fr','en']) {
    const names = locale==='fr'
      ? {staff:'Bâton du soleil',robe:"Robe chatoyante de l'aigle",shoulders:'Mantelet félin'}
      : {staff:'Staff of the Sun',robe:'Shimmering Robe of the Eagle',shoulders:'Feline Mantle'};
    for (const viewport of [{ width:1440, height:960 }, { width:1920, height:1080 }, { width:1280, height:720 }, { width:1024, height:768 }]) {
      const page = await browser.newPage({ viewport });
      const errors = [];
      page.on('pageerror', error => { errors.push(error.message); console.error(error.message); });
      page.on('response', response => { if (response.status() >= 400 && !response.url().endsWith('favicon.ico')) errors.push(response.status() + ' ' + response.url()); });
      await page.goto(url+'/?lang='+locale);
      await page.waitForFunction(() => window.armory?.statisticsLoaded, null, { timeout:30000 });
      assert.equal(await page.locator('html').getAttribute('lang'),locale);
      const state = await page.evaluate(() => {
        const a = window.armory;
        if (!a.ready) return a;
        return { ready:a.ready, bounds:{min:a.bounds.min.toArray(),max:a.bounds.max.toArray()}, animationTracks:a.mixer._actions[0]._clip.tracks.length, frames:a.frames, meshes:a.renderer.info.render.calls };
      });
      assert.equal(state.ready, true, JSON.stringify(state));
      assert.equal(await page.evaluate(() => window.armory.data.equipment.filter(i => i.details).length),13);
      assert.equal(await page.locator('#average-item-level').textContent(),locale==='fr'?'22,7':'22.7');
      assert.equal(await page.locator('#character-stats dd').count(),8);
      const expectedStats = characterStatsRows(character,locale);
      assert.deepEqual(await page.locator('#character-stats dd').allTextContents(),expectedStats.map(row => row.value));
      const knownStats = expectedStats.filter(row => row.known).length;
      assert.equal(await page.locator('#stats-status').isVisible(),knownStats<expectedStats.length);
      if (knownStats<expectedStats.length) {
        const expectedStatus = knownStats
          ? (locale==='fr'?'Données serveur incomplètes':'Server data incomplete')
          : (locale==='fr'?'Données serveur indisponibles':'Server data unavailable');
        assert.equal(await page.locator('#stats-status').textContent(),expectedStatus);
      }
      const summaryBounds = await page.locator('#character-summary').boundingBox();
      const armoryBounds = await page.locator('.armory').boundingBox();
      assert.ok(summaryBounds.x>=armoryBounds.x+armoryBounds.width,'Statistics overlap equipment or model');
      assert.ok(summaryBounds.x+summaryBounds.width<=viewport.width && summaryBounds.y+summaryBounds.height<=viewport.height,'Statistics leave the PC viewport');
      assert.equal(await page.locator('#character-summary .item-popover, #character-summary img, #character-summary button:not(#stats-modes button)').count(),0);
      const sidebarText = await page.locator('#character-summary').textContent();
      const inspectPixels = async () => page.evaluate(() => {
        const { renderer } = window.armory;
        const gl = renderer.getContext();
        const w = gl.drawingBufferWidth, h = gl.drawingBufferHeight;
        const pixels = new Uint8Array(w*h*4);
        gl.readPixels(0,0,w,h,gl.RGBA,gl.UNSIGNED_BYTE,pixels);
        let foreground = 0, changed = 0, minX=w, maxX=0, minY=h, maxY=0;
        for (let i=0;i<pixels.length;i+=4) {
          if (Math.abs(pixels[i]-pixels[0])+Math.abs(pixels[i+1]-pixels[1])+Math.abs(pixels[i+2]-pixels[2])>40) {
            foreground++;
            const x=(i/4)%w,y=Math.floor(i/4/w);
            minX=Math.min(minX,x);maxX=Math.max(maxX,x);minY=Math.min(minY,y);maxY=Math.max(maxY,y);
          }
          if (window.previousPixels && Math.abs(pixels[i]-window.previousPixels[i])+Math.abs(pixels[i+1]-window.previousPixels[i+1])+Math.abs(pixels[i+2]-window.previousPixels[i+2])>30) changed++;
        }
        window.previousPixels=pixels;
        return { foreground, changed, minX,maxX,minY,maxY,w,h, background:Array.from(pixels.slice(0,3)) };
      });
      await page.waitForTimeout(150);
      const pixels = await inspectPixels();
      assert.ok(pixels.foreground > 3000, 'Blank canvas');
      assert.ok(pixels.minX > 4 && pixels.maxX < pixels.w-4 && pixels.minY > 45 && pixels.maxY < pixels.h-4, 'Default model is clipped or overlaps the toolbar: '+JSON.stringify(pixels));
      const overflow = await page.evaluate(() => ({
        horizontal:document.documentElement.scrollWidth > innerWidth,
        text:[...document.querySelectorAll('.slot-copy,.scene-toolbar,.character-header,.character-stat,#character-summary')].some(el => el.scrollWidth>el.clientWidth+1)
      }));
      assert.deepEqual(overflow, { horizontal:false, text:false });
      const overflowingSlots = await page.locator('button.slot').evaluateAll(slots => slots.filter(el => el.scrollHeight > el.clientHeight + 1).map(el => el.dataset.slot));
      assert.deepEqual(overflowingSlots, [], 'Item text exceeds its slot');
      assert.equal(await page.evaluate(() => document.documentElement.scrollHeight > innerHeight), false, 'Desktop requires vertical scrolling');
      assert.equal(await page.locator('.item-detail, #detail-id, #detail-quality').count(),0);
      assert.equal(await page.locator('.empty-slot').count(),6);
      assert.ok((await page.locator('.empty-slot').allTextContents()).every(text => text.trim() === ''));
      assert.equal(await page.locator('.weapons .slot').count(),3);
      assert.equal(await page.locator('#item-popover').isVisible(),false);
      assert.deepEqual(pixels.background,[16,20,24]);
      await page.screenshot({ path:path.join(output, `view-${locale}-${viewport.width}.png`), fullPage:true });
      const beforeTime=await page.evaluate(() => window.armory.animatedTime);
      await page.waitForTimeout(450);
      assert.ok(await page.evaluate(time => window.armory.animatedTime>time, beforeTime));
      const animated=await inspectPixels();
      assert.ok(animated.changed>20, 'Animation pixels do not move');
      await page.locator('#animate').click();
      const pausedTime=await page.evaluate(() => window.armory.animatedTime);
      await page.waitForTimeout(180);
      assert.equal(await page.evaluate(() => window.armory.animatedTime),pausedTime);
      const canvas=page.locator('#scene canvas');
      const rect=await canvas.boundingBox();
      await page.mouse.move(rect.x+rect.width/2,rect.y+rect.height/2);
      await page.mouse.down();
      await page.mouse.move(rect.x+rect.width/2+85,rect.y+rect.height/2,{steps:12});
      await page.mouse.up();
      await page.waitForTimeout(300);
      const rotated=await inspectPixels();
      assert.ok(rotated.changed>200,'Rotation pixels do not move');
      const distance=await page.evaluate(() => window.armory.camera.position.distanceTo(window.armory.controls.target));
      await page.locator('#zoom-in').click();
      assert.ok(await page.evaluate(d => window.armory.camera.position.distanceTo(window.armory.controls.target)<d,distance));
      await page.locator('#reset').click();
      await page.locator('.slot[data-slot="15"]').click();
      assert.equal(await page.locator('#detail-name').textContent(),names.staff);
      const staffLines = await page.locator('#detail-lines p').allTextContents();
      assert.ok(staffLines.includes(locale==='fr'?'18,3 dégâts par seconde':'18.3 damage per second'));
      assert.ok(staffLines.includes(locale==='fr'?'+10 Intelligence':'+10 Intellect'));
      assert.equal(await page.locator('#item-popover').getAttribute('role'),'dialog');
      assert.equal(await page.locator('#character-summary').textContent(),sidebarText,'Selecting an item replaces the character stats');
      assert.equal(await page.locator('.slot[data-slot="15"]').getAttribute('aria-expanded'),'true');
      const popoverBounds = await page.locator('#item-popover').boundingBox();
      assert.ok(popoverBounds.x>=0 && popoverBounds.y>=0 && popoverBounds.x+popoverBounds.width<=viewport.width && popoverBounds.y+popoverBounds.height<=viewport.height);
      await page.locator('.slot[data-slot="9"]').hover();
      await page.waitForTimeout(220);
      assert.equal(await page.locator('#detail-name').textContent(),names.staff,'Hover replaces pinned item');
      if (viewport.width===1440) await page.screenshot({path:path.join(output,`view-details-${locale}-${viewport.width}.png`)});
      await page.keyboard.press('Escape');
      await page.waitForTimeout(220);
      assert.equal(await page.locator('#item-popover').isVisible(),false,'Escape reopens the preview');
      await page.locator('.slot[data-slot="4"]').focus();
      await page.keyboard.press('Enter');
      assert.equal(await page.locator('#detail-name').textContent(),names.robe);
      const robeLines = await page.locator('#detail-lines p').allTextContents();
      assert.ok(robeLines.includes(locale==='fr'?'+6 Intelligence':'+6 Intellect'));
      assert.ok(robeLines.includes(locale==='fr'?'+6 Endurance':'+6 Stamina'));
      assert.equal(await page.locator('#detail-lines .stat').first().evaluate(el => getComputedStyle(el).color),'rgb(255, 255, 255)');
      assert.equal(await page.locator('#close-item').evaluate(el => el===document.activeElement),true);
      await page.keyboard.press('Escape');
      await page.waitForTimeout(220);
      assert.equal(await page.locator('.slot[data-slot="4"]').evaluate(el => el===document.activeElement),true);
      assert.equal(await page.locator('#item-popover').isVisible(),false);
      await page.locator('.slot[data-slot="4"]').click();
      await page.locator('.brand').click();
      assert.equal(await page.locator('#item-popover').isVisible(),false,'Outside click does not dismiss');
      await page.locator('.slot[data-slot="2"]').hover();
      await page.waitForTimeout(220);
      assert.equal(await page.locator('#item-popover').getAttribute('role'),'tooltip');
      assert.equal(await page.locator('#detail-name').textContent(),names.shoulders);
      assert.match(await page.locator('#detail-lines .effect').textContent(),/\b2\b/);
      assert.equal(await page.locator('#detail-lines .effect').evaluate(el => getComputedStyle(el).color),'rgb(0, 255, 0)');
      await page.locator('#item-popover').hover();
      await page.waitForTimeout(220);
      assert.equal(await page.locator('#item-popover').isVisible(),true,'Tooltip is not hoverable');
      await page.locator('.brand').hover();
      await page.waitForTimeout(220);
      assert.equal(await page.locator('#item-popover').isVisible(),false);
      await page.locator('.slot[data-slot="11"]').click();
      assert.equal(await page.locator('#detail-lines .effect').textContent(),locale==='fr'?'Équipé : Augmente le score de coup critique de 2.':'Equip: Improves critical strike rating by 2.');
      assert.equal(await page.locator('#detail-lines .effect').evaluate(el => getComputedStyle(el).color),'rgb(0, 255, 0)');
      assert.equal(await page.locator('#detail-lines .stat').evaluate(el => getComputedStyle(el).color),'rgb(255, 255, 255)');
      assert.equal(await page.locator('#detail-name').evaluate(el => getComputedStyle(el).color),await page.locator('.slot[data-slot="11"] .slot-name').evaluate(el => getComputedStyle(el).color));
      if (viewport.width===1440) await page.screenshot({path:path.join(output,`view-colors-${locale}.png`)});
      await page.keyboard.press('Escape');
      await page.locator('#reset').click();
      if(viewport.width===1440) {
        await page.evaluate(() => {
          const a=window.armory;
          a.camera.position.x=a.controls.target.x-(a.camera.position.x-a.controls.target.x);
          a.controls.update();
        });
        await page.waitForTimeout(200);
        await page.screenshot({path:path.join(output,`view-back-${locale}.png`),fullPage:true});
      }
      assert.deepEqual(errors,[]);
      results.push({ locale, viewport, state, pixels, animationChangedPixels:animated.changed, rotationChangedPixels:rotated.changed, statistics:expectedStats, overflow, errors });
      console.log(JSON.stringify(results.at(-1)));
      await page.close();
    }
    }
    const page = await browser.newPage({viewport:{width:1440,height:960},locale:'en-US'});
    let launcherLocale = 'fr';
    await page.route('**/viewer-config.json',route => route.fulfill({json:{locale:launcherLocale,source:'launcher-local'}}));
    await page.goto(url);
    await page.waitForFunction(() => window.armory?.ready);
    assert.equal(await page.locator('html').getAttribute('lang'),'fr','Launcher locale must override browser locale');
    assert.equal(await page.locator('[data-stat="intellect"] dt').textContent(),'Intelligence');
    await page.locator('.slot[data-slot="15"]').click();
    launcherLocale = 'en';
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await page.waitForFunction(() => document.documentElement.lang==='en');
    assert.equal(await page.locator('#detail-name').textContent(),'Staff of the Sun');
    assert.equal(await page.locator('#item-popover').getAttribute('role'),'dialog');
    assert.equal(await page.locator('#close-item').getAttribute('aria-label'),'Close item details');
    assert.equal(await page.locator('[data-stat="intellect"] dt').textContent(),'Intellect');
    launcherLocale = null;
    await page.reload();
    await page.waitForFunction(() => window.armory?.ready);
    assert.equal(await page.locator('html').getAttribute('lang'),'en','Browser locale must work without launcher settings');
    // Synthetic server totals exist only in this browser test, never in the published snapshot.
    await page.route('**/statistics.json',route => route.fulfill({json:{status:'unavailable',record:null}}));
    let stale = false;
    await page.route('**/assets/character.json',async route => {
      const response = await route.fetch();
      const character = await response.json();
      character.statistics = {
        capturedAt:stale?'older snapshot':character.capturedAt,
        values:{intellect:150,stamina:80,spirit:42,spellPower:62,spellCritPct:3.5,spellHitPct:0,spellHastePct:120,armor:300}
      };
      await route.fulfill({response,json:character});
    });
    await page.reload();
    await page.waitForFunction(() => window.armory?.ready);
    assert.equal(await page.locator('[data-stat="intellect"] dd').textContent(),'150');
    assert.equal(await page.locator('[data-stat="spellCritPct"] dd').textContent(),'3.5%');
    assert.equal(await page.locator('[data-stat="spellHitPct"] dd').textContent(),'0.0%');
    assert.equal(await page.locator('#stats-status').isVisible(),false);
    launcherLocale = 'fr';
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await page.waitForFunction(() => document.documentElement.lang==='fr');
    assert.equal(await page.locator('[data-stat="spellCritPct"] dd').textContent(),'3,5\u00a0%');
    stale = true;
    await page.reload();
    await page.waitForFunction(() => window.armory?.ready);
    assert.ok((await page.locator('#character-stats dd').allTextContents()).every(value => value==='—'));
    assert.equal(await page.locator('#stats-status').isVisible(),true);
    await page.close();
    results.push({localeSwitch:true,browserFallback:true,summaryValues:true,staleStatisticsRejected:true});
    const asyncPage = await browser.newPage({viewport:{width:1440,height:960}});
    await asyncPage.route('**/assets/character.json',async route => {
      const response = await route.fetch(); const next = await response.json(); delete next.statistics;
      await route.fulfill({response,json:next});
    });
    let releaseStatistics, record;
    const pendingStatistics = new Promise(resolve => { releaseStatistics = resolve; });
    let modelRequests = 0;
    asyncPage.on('request',request => { if (request.url().endsWith('.gltf')) modelRequests++; });
    await asyncPage.route('**/statistics.json',async route => {
      await pendingStatistics;
      await route.fulfill({json:{status:'ready',record}});
    });
    try {
      await asyncPage.goto(url+'/?lang=fr');
      await asyncPage.waitForFunction(() => window.armory?.ready);
      assert.equal(await asyncPage.evaluate(() => window.armory.statisticsLoaded),false,'The model waited for the statistics request');
      const initialModelRequests = modelRequests;
      await asyncPage.locator('.slot[data-slot="15"]').click();
      assert.equal(await asyncPage.locator('#item-popover').getAttribute('role'),'dialog');
      record = {
        source:'arthas-character-stats',characterName:'Flowmage',
        characterCapturedAt:await asyncPage.evaluate(() => window.armory.data.capturedAt),
        values:{intellect:150,stamina:80,spirit:42,armor:300}
      };
      releaseStatistics();
      await asyncPage.waitForFunction(() => window.armory.statisticsLoaded);
      assert.equal(await asyncPage.locator('[data-stat="intellect"] dd').textContent(),'150');
      assert.equal(await asyncPage.locator('[data-stat="spellCritPct"] dd').textContent(),'—');
      assert.equal(await asyncPage.locator('#stats-status').textContent(),'Données serveur incomplètes');
      assert.equal(await asyncPage.locator('#item-popover').getAttribute('role'),'dialog');
      assert.equal(modelRequests,initialModelRequests,'Statistics reloaded the 3D assets');
      await asyncPage.unroute('**/statistics.json');
      await asyncPage.route('**/statistics.json',route => route.fulfill({status:503,body:''}));
      await asyncPage.reload();
      await asyncPage.waitForFunction(() => window.armory?.ready && window.armory.statisticsLoaded);
      assert.equal(await asyncPage.locator('[data-stat="intellect"] dd').textContent(),'—');
      results.push({statisticsNonBlocking:true,statisticsFailureIsolated:true,modelNotReloaded:true});
    } finally { releaseStatistics(); await asyncPage.close(); }
    const pollingPage = await browser.newPage({viewport:{width:1440,height:960}});
    let pollingRecord = {schemaVersion:2,source:'arthas-combat-stats',characterName:character.name,characterCapturedAt:character.capturedAt,
      savedAt:'2026-09-05T12:00:00Z',observedAt:'2026-09-05T12:01:00Z',...combatDetails()};
    let pollingRequests = 0, pollingModels = 0, pollingFailure = false, blockedRequest, releasePoll;
    pollingPage.on('request',request => { if (request.url().endsWith('.gltf')) pollingModels++; });
    await pollingPage.clock.install();
    await pollingPage.route('**/statistics.json',async route => {
      pollingRequests++;
      if (blockedRequest) {
        const gate = new Promise(resolve => { releasePoll = resolve; });
        blockedRequest(); blockedRequest = undefined;
        await gate;
      }
      await route.fulfill(pollingFailure ? {status:503,body:''} : {json:{status:'ready',record:pollingRecord}}).catch(() => {});
    });
    try {
      await pollingPage.goto(url+'/?lang=fr');
      await pollingPage.waitForFunction(() => window.armory?.ready && window.armory.statisticsLoaded);
      const models = pollingModels;
      await pollingPage.locator('#stats-school').selectOption('4');
      await pollingPage.locator('.slot[data-slot="15"]').click();
      await pollingPage.evaluate(() => { window.originalRoot = window.armory.root; });
      pollingRecord.values.intellect = 175;
      pollingRecord.savedAt = '2026-09-05T12:01:00Z';
      await pollingPage.clock.fastForward(5001);
      await pollingPage.waitForFunction(() => document.querySelector('[data-stat="intellect"] dd').textContent==='175');
      assert.equal(await pollingPage.locator('#stats-school').inputValue(),'4');
      assert.equal(await pollingPage.locator('#item-popover').getAttribute('role'),'dialog');
      assert.equal(await pollingPage.evaluate(() => window.originalRoot===window.armory.root),true);
      assert.equal(pollingModels,models);
      await pollingPage.evaluate(() => { window.originalStatRow = document.querySelector('#character-stats').firstChild; });
      pollingRecord.observedAt = '2026-09-05T12:02:00Z';
      const unchanged = pollingPage.waitForResponse('**/statistics.json');
      await pollingPage.clock.fastForward(5001); await unchanged;
      await pollingPage.waitForTimeout(100);
      assert.equal(await pollingPage.evaluate(() => window.originalStatRow===document.querySelector('#character-stats').firstChild),true,'An unchanged poll rebuilt the statistics controls');
      pollingRecord = {...pollingRecord,savedAt:'2026-09-05T11:59:00Z',values:{...pollingRecord.values,intellect:1}};
      const older = pollingPage.waitForResponse('**/statistics.json');
      await pollingPage.clock.fastForward(5001); await older;
      await pollingPage.waitForTimeout(100);
      assert.equal(await pollingPage.locator('[data-stat="intellect"] dd').textContent(),'175');
      pollingFailure = true;
      const failure = pollingPage.waitForResponse('**/statistics.json');
      await pollingPage.clock.fastForward(5001); await failure;
      await pollingPage.waitForTimeout(100);
      assert.equal(await pollingPage.locator('[data-stat="intellect"] dd').textContent(),'175');
      pollingFailure = false;
      const started = new Promise(resolve => { blockedRequest = resolve; });
      await pollingPage.clock.fastForward(5001); await started;
      const timedOut = pollingPage.waitForEvent('requestfailed',{predicate:request => request.url().endsWith('/statistics.json')});
      await pollingPage.clock.fastForward(10001); await timedOut;
      releasePoll();
      pollingRecord = {...pollingRecord,savedAt:'2026-09-05T12:02:00Z',values:{...pollingRecord.values,intellect:176}};
      await pollingPage.clock.fastForward(5001);
      await pollingPage.waitForFunction(() => document.querySelector('[data-stat="intellect"] dd').textContent==='176');
      await pollingPage.evaluate(() => {
        Object.defineProperty(document,'hidden',{configurable:true,value:true});
        document.dispatchEvent(new Event('visibilitychange'));
      });
      const hiddenRequests = pollingRequests;
      await pollingPage.clock.fastForward(20000);
      assert.equal(pollingRequests,hiddenRequests,'A hidden viewer kept polling');
      pollingRecord = {...pollingRecord,savedAt:'2026-09-05T12:03:00Z',values:{...pollingRecord.values,intellect:177}};
      await pollingPage.evaluate(() => {
        Object.defineProperty(document,'hidden',{configurable:true,value:false});
        document.dispatchEvent(new Event('visibilitychange'));
      });
      await pollingPage.waitForFunction(() => document.querySelector('[data-stat="intellect"] dd').textContent==='177');
      assert.equal(await pollingPage.locator('#stats-school').inputValue(),'4');
      assert.equal(await pollingPage.locator('#item-popover').getAttribute('role'),'dialog');
      assert.equal(pollingModels,models);
      await pollingPage.evaluate(() => window.dispatchEvent(new Event('pagehide')));
      const closedRequests = pollingRequests;
      await pollingPage.clock.fastForward(20000);
      assert.equal(pollingRequests,closedRequests,'A suspended page kept polling');
      const resumed = pollingPage.waitForResponse('**/statistics.json');
      await pollingPage.evaluate(() => window.dispatchEvent(new Event('pageshow'))); await resumed;
      results.push({statisticsAutoRefresh:true,unchangedControlsPreserved:true,olderSnapshotRejected:true,offlineCacheRetained:true,
        requestTimeoutRecovered:true,hiddenPollingPaused:true,pageLifecycle:true,modelNotReloaded:true,pinnedTooltipPreserved:true});
    } finally { releasePoll?.(); await pollingPage.close(); }
    for (const locale of ['fr','en']) {
      const modePage = await browser.newPage({viewport:{width:1280,height:720}});
      let classId = 8;
      const record = {schemaVersion:2,source:'arthas-combat-stats',characterName:character.name,characterCapturedAt:character.capturedAt,savedAt:'2026-09-05T12:00:00Z',...combatDetails()};
      await modePage.route('**/assets/character.json',async route => {
        const response = await route.fetch();
        await route.fulfill({response,json:{...await response.json(),classId}});
      });
      await modePage.route('**/statistics.json',route => route.fulfill({json:{status:'ready',record}}));
      try {
        for (classId of [1,2,3,4,5,6,7,8,9,11]) {
          await modePage.goto(url+'/?lang='+locale);
          await modePage.waitForFunction(() => window.armory?.ready && window.armory.statisticsLoaded);
          const fixture = {...character,classId,statistics:record};
          const choices = statisticsModes(fixture,locale);
          assert.deepEqual(await modePage.locator('#character-stats dd').allTextContents(),characterStatsRows(fixture,locale).map(row => row.value));
          assert.equal(await modePage.locator('#stats-modes button svg').count(),choices.length,'A category icon is missing');
          for (const choice of choices) {
            if (choices.length>1) await modePage.locator(`#stats-modes [data-mode="${choice.key}"]`).click();
            const magic = ['spell','healing'].includes(choice.key);
            assert.equal(await modePage.locator('#stats-school').isVisible(),magic);
            if (magic) await modePage.locator('#stats-school').selectOption('4');
            assert.deepEqual(await modePage.locator('#character-stats dd').allTextContents(),characterStatsRows(fixture,locale,choice.key,magic?4:0).map(row => row.value));
            const overflow = await modePage.evaluate(() => ({horizontal:document.documentElement.scrollWidth>innerWidth,vertical:document.documentElement.scrollHeight>innerHeight,text:[...document.querySelectorAll('.character-stat,#stats-modes,.stats-school')].some(el => el.scrollWidth>el.clientWidth+1)}));
            assert.deepEqual(overflow,{horizontal:false,vertical:false,text:false},`Statistics overflow: ${classId}/${choice.key}/${locale}`);
            if (classId===2 && choice.key==='healing') await modePage.screenshot({path:path.join(output,`test-healing-panel-${locale}.png`)});
          }
          if (choices.length>1) {
            const selected = await modePage.locator('#stats-modes [aria-pressed="true"]').getAttribute('data-mode');
            await modePage.evaluate(() => window.dispatchEvent(new Event('focus')));
            await modePage.waitForTimeout(150);
            assert.equal(await modePage.locator('#stats-modes [aria-pressed="true"]').getAttribute('data-mode'),selected,'Background refresh changed the selected category');
          }
        }
        results.push({locale,combatCategories:true,classesTested:10,schoolSelection:true});
      } finally { await modePage.close(); }
    }
    await fs.writeFile(path.join(output, 'verification.json'), JSON.stringify(results,null,2));
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode=1; });
