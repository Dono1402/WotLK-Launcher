const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));

async function main() {
  const output = path.resolve(__dirname,'../../artifacts/armory-prototype');
  const {port} = JSON.parse(await fs.readFile(path.join(output,'server-instance.json'),'utf8'));
  const url = process.env.ARMORY_URL || `http://127.0.0.1:${port}`;
  const read = async name => fs.readFile(path.join(output,'assets',name));
  const baseCharacter = JSON.parse(await read('character.json'));
  const baseDetails = JSON.parse(await read('item-details.json'));
  const baseStats = JSON.parse(await read('statistics.json'));
  const browser = await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader']});
  const results = [];
  try {
    for (const locale of ['fr','en']) {
      const page = await browser.newPage({viewport:{width:1440,height:960}});
      await page.clock.install();
      const a = 'a'.repeat(32), b = 'b'.repeat(32), broken = 'c'.repeat(32), restored = 'd'.repeat(32), removedAgain = 'e'.repeat(32);
      let selected = a, release, started, blocked = false, statisticsRequests = 0;
      const errors = [];
      page.on('pageerror',error => errors.push(error.message));
      const bundle = revision => {
        const character = structuredClone(baseCharacter), details = structuredClone(baseDetails), statistics = structuredClone(baseStats);
        const withoutWeapon = [b,removedAgain].includes(revision);
        if (revision!==a) {
          character.capturedAt = `2026-09-05 16:0${revision===b?1:revision===restored?2:3}:00.000`;
          if (withoutWeapon) {
            character.equipment = character.equipment.filter(item => item.slot!==15);
            character.attached = character.attached.filter(item => item.slot!==15);
          }
          statistics.values.intellect = withoutWeapon ? 175 : 176;
          statistics.savedAt = character.capturedAt.replace(' ','T')+'Z';
          statistics.observedAt = statistics.savedAt;
        }
        statistics.characterCapturedAt = character.capturedAt;
        character.statistics = statistics;
        details.characterCapturedAt = character.capturedAt;
        details.items = character.equipment.map(item => ({...baseDetails.items.find(row => row.itemId===item.itemId),slot:item.slot}));
        return {character,details,statistics};
      };
      await page.route('**/armory.json',route => route.fulfill({json:{revision:selected,assetBase:`/snapshots/${selected}/assets/`}}));
      await page.route('**/statistics.json',route => { statisticsRequests++; return route.fulfill({json:{status:'ready',record:bundle(selected).statistics}}); });
      await page.route('**/snapshots/*/assets/*',async route => {
        const match = new URL(route.request().url()).pathname.match(/^\/snapshots\/([a-f0-9]{32})\/assets\/([a-zA-Z0-9_-]+\.(?:json|gltf|bin|png))$/);
        if (!match) return route.abort();
        const [,revision,name] = match;
        if (revision===b && name==='flowmage.gltf' && blocked) {
          const pending = new Promise(resolve => { release = resolve; }); started();
          await pending;
        }
        if (revision===broken && name==='flowmage.gltf') return route.fulfill({status:404,body:''});
        const value = bundle(revision);
        if (name==='character.json') return route.fulfill({json:value.character});
        if (name==='item-details.json') return route.fulfill({json:value.details});
        return route.fulfill({body:await read(name),contentType:name.endsWith('.png')?'image/png':name.endsWith('.gltf')?'model/gltf+json':'application/octet-stream'});
      });
      try {
        await page.goto(url+'/?lang='+locale);
        await page.waitForFunction(() => window.armory?.ready);
        await page.locator('#stats-school').selectOption('4');
        await page.locator('#zoom-out').click();
        await page.locator('.slot[data-slot="2"]').click();
        const initial = await page.evaluate(() => {
          const armory = window.armory;
          window.initialBody = armory.root.children[0];
          window.initialDisposed = false;
          armory.root.children[0].traverse(mesh => mesh.geometry?.addEventListener('dispose',() => { window.initialDisposed = true; }));
          return {frames:armory.frames,direction:armory.camera.position.clone().sub(armory.controls.target).normalize().toArray(),
            relativeZoom:armory.camera.position.distanceTo(armory.controls.target)/armory.bounds.getSize(armory.camera.position.clone()).y};
        });
        const pending = new Promise(resolve => { started = resolve; });
        selected = b; blocked = true;
        await page.clock.fastForward(5001); await pending;
        assert.equal(await page.evaluate(() => window.armory.revision),a);
        assert.equal(await page.locator('button.slot[data-slot="15"]').count(),1);
        assert.equal(await page.locator('[data-stat="intellect"] dd').textContent(),String(baseStats.values.intellect),'New statistics appeared on the old model');
        await page.waitForTimeout(250);
        assert.ok(await page.evaluate(() => window.armory.frames)>initial.frames);
        blocked = false; release();
        await page.waitForFunction(expected => window.armory.revision===expected,b);
        assert.equal(await page.locator('button.slot[data-slot="15"]').count(),0);
        assert.equal(await page.locator('[data-stat="intellect"] dd').textContent(),'175');
        assert.equal(await page.locator('#item-popover').getAttribute('role'),'dialog');
        assert.equal(await page.locator('#stats-school').inputValue(),'4');
        assert.equal(await page.evaluate(() => window.initialDisposed),true);
        const direction = await page.evaluate(() => window.armory.camera.position.clone().sub(window.armory.controls.target).normalize().toArray());
        assert.ok(direction.every((value,i) => Math.abs(value-initial.direction[i])<0.005),'The camera orientation reset during the swap');
        const relativeZoom = await page.evaluate(() => window.armory.camera.position.distanceTo(window.armory.controls.target)/window.armory.bounds.getSize(window.armory.camera.position.clone()).y);
        assert.ok(Math.abs(relativeZoom-initial.relativeZoom)<0.01,`Zoom changed: ${initial.relativeZoom} -> ${relativeZoom}`);
        assert.equal(await page.evaluate(() => window.armory.root.children.length),1);
        await page.screenshot({path:path.join(output,`test-hot-swap-${locale}.png`)});
        const pixels = await page.evaluate(() => {
          const gl = window.armory.renderer.getContext(), w = gl.drawingBufferWidth, h = gl.drawingBufferHeight;
          const bytes = new Uint8Array(w*h*4); gl.readPixels(0,0,w,h,gl.RGBA,gl.UNSIGNED_BYTE,bytes);
          let visible = 0, minY = h, maxY = 0, minX = w, maxX = 0;
          for (let i=0;i<bytes.length;i+=4) if (Math.abs(bytes[i]-bytes[0])+Math.abs(bytes[i+1]-bytes[1])+Math.abs(bytes[i+2]-bytes[2])>40) {
            visible++; const x=(i/4)%w,y=Math.floor(i/4/w);
            minX=Math.min(minX,x); maxX=Math.max(maxX,x); minY=Math.min(minY,y); maxY=Math.max(maxY,y);
          }
          return {visible,minX,maxX,minY,maxY,w,h};
        });
        assert.ok(pixels.visible>3000,'The swapped model is blank');
        assert.ok(pixels.minX>4 && pixels.maxX<pixels.w-4 && pixels.minY>45 && pixels.maxY<pixels.h-4,'The swapped model is clipped');
        selected = broken;
        const failed = page.waitForResponse(response => response.url().includes(broken) && response.status()===404);
        await page.clock.fastForward(5001); await failed;
        await page.waitForFunction(() => !window.armory.refreshingModel);
        assert.equal(await page.evaluate(() => window.armory.revision),b);
        assert.equal(await page.locator('[data-stat="intellect"] dd').textContent(),'175');
        assert.equal(await page.locator('#item-popover').getAttribute('role'),'dialog');
        selected = restored;
        await page.clock.fastForward(5001);
        await page.waitForFunction(expected => window.armory.revision===expected,restored);
        assert.equal(await page.locator('button.slot[data-slot="15"]').count(),1);
        await page.locator('.slot[data-slot="15"]').click();
        selected = removedAgain;
        await page.clock.fastForward(5001);
        await page.waitForFunction(expected => window.armory.revision===expected,removedAgain);
        assert.equal(await page.locator('#item-popover').isVisible(),false,'Removed items must not retain a stale tooltip');
        const overflow = await page.evaluate(() => ({horizontal:document.documentElement.scrollWidth>innerWidth,text:[...document.querySelectorAll('.slot-copy,.character-stat')].some(el => el.scrollWidth>el.clientWidth+1)}));
        assert.deepEqual(overflow,{horizontal:false,text:false});
        assert.deepEqual(errors,[]);
        results.push({locale,oldModelVisibleWhileLoading:true,atomicEquipmentAndStats:true,modelSwapped:true,geometryDisposed:true,
          cameraPreserved:true,schoolPreserved:true,pinnedTooltipPreserved:true,removedTooltipClosed:true,failedModelRetained:true,pixels,overflow,errors,statisticsRequests});
      } finally { release?.(); await page.close(); }
    }
    await fs.writeFile(path.join(output,'refresh-verification.json'),JSON.stringify(results,null,2));
    console.log(JSON.stringify(results));
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode=1; });
