const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const {randomBytes} = require('node:crypto');
const {createLauncherServer} = require('./launcher-server.cjs');
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));

async function main() {
  const output = path.resolve(__dirname,'../../artifacts/atlas-social-corrections/friend-web');
  await fs.mkdir(output,{recursive:true});
  const key = randomBytes(32).toString('hex');
  const character = {characterId:'910',name:'MageAmi',classId:8,raceId:1,level:80,realm:'Arthas',capturedAt:'2026-09-06 19:00:00',equipment:[],attached:[]};
  const server = createLauncherServer({key,armory:{
    list:() => ({status:'ready',characters:[{id:'910',name:'MageAmi',classId:8,level:80,online:true,available:true}]}),
    entry:id => id==='910' ? {revision:'1'.repeat(32),modelReady:false,character,details:{characterCapturedAt:character.capturedAt,items:[]}} : null
  }});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const browser = await chromium.launch({channel:'msedge',headless:true});
  const evidence = [];
  try {
    for (const locale of ['fr','en']) {
      const viewport = {width:1598,height:997};
      const context = await browser.newContext({viewport,extraHTTPHeaders:{'X-Atlas-Armory-Key':key}});
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror',error => errors.push(error.message));
      await page.addInitScript(() => {
        window.__actions = [];
        Object.defineProperty(window,'chrome',{configurable:true,value:{webview:{
          postMessage:message => window.__actions.push(message),
          addEventListener:(_,callback) => { window.__profile = data => callback({data}); }
        }}});
      });
      await page.goto(`${base}/?lang=${locale}`);
      await page.waitForFunction(() => window.__actions.some(message => message.action==='ready'));
      const own = {type:'profile',locale,username:'Mon compte',statusMessage:'Mon statut',bio:'Ma bio',canUpdateSocialProfile:true,canModifyAvatar:true,canModifyBanner:true};
      const friend = {type:'profile',locale,readOnly:true,username:'AmiAtlas',statusMessage:'Disponible pour un donjon',bio:'Une aventure partagée en Azeroth.',canSendMessage:true,canUpdateSocialProfile:false,canModifyAvatar:false,canRemoveAvatar:false,canModifyBanner:false};
      await page.evaluate(data => window.__profile(data),own);
      assert.equal(await page.locator('.banner-image').isVisible(),false);
      assert.equal(await page.locator('.banner-image').getAttribute('src'),null);
      await page.locator('#edit-profile').click();
      assert.equal(await page.locator('#profile-editor').isVisible(),true);
      await page.evaluate(data => window.__profile(data),friend);
      await page.locator('.character').first().waitFor();
      assert.equal(await page.locator('#profile-name').textContent(),'AmiAtlas');
      assert.equal(await page.locator('#profile-editor').isVisible(),false);
      assert.equal(await page.locator('#edit-profile').isVisible(),false);
      assert.equal(await page.locator('.banner-controls').isVisible(),false);
      assert.equal(await page.locator('.banner-image').isVisible(),false);
      assert.equal(await page.locator('.banner-backdrop[src]').count(),0);
      assert.equal(await page.locator('#change-avatar').isDisabled(),true);
      assert.equal(await page.locator('#change-avatar').evaluate(element => getComputedStyle(element).opacity),'1');
      assert.equal(await page.locator('[data-label=characters]').textContent(),locale==='fr'?'Personnages':'Characters');
      await page.locator('#send-message').click();
      assert.deepEqual(await page.evaluate(() => window.__actions.at(-1)),{action:'send-message'});
      await page.locator('#back-to-friends').click();
      assert.deepEqual(await page.evaluate(() => window.__actions.at(-1)),{action:'back-to-friends'});
      await page.evaluate(() => { document.getElementById('edit-profile').click(); document.getElementById('change-avatar').click(); window.__profile({type:'profile-editor-open'}); });
      assert.equal(await page.locator('#profile-editor').isVisible(),false);
      assert.equal(await page.evaluate(() => window.__actions.some(message => ['save-profile','change-avatar','choose-banner'].includes(message.action))),false);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth<=innerWidth),true);
      await page.screenshot({path:path.join(output,`friend-${locale}-${viewport.width}.png`)});
      await page.evaluate(data => window.__profile({...data,username:'<img src=x onerror=alert(1)>'}),friend);
      assert.equal(await page.locator('#profile-name').textContent(),'<img src=x onerror=alert(1)>');
      assert.equal(await page.locator('#profile-name img').count(),0);
      await page.evaluate(data => window.__profile(data),own);
      assert.equal(await page.locator('#friend-actions').isVisible(),false);
      await page.locator('#edit-profile').click();
      assert.equal(await page.locator('#profile-editor').isVisible(),true);
      assert.deepEqual(errors,[]);
      evidence.push({locale,...viewport,passed:true});
      await context.close();
    }
    await fs.writeFile(path.join(output,'results.json'),JSON.stringify(evidence,null,2));
    console.log('Friend web profile OK: FR/EN at fixed launcher dimensions, plain default banner, readonly controls, editor transition, actions, safe text rendering, no overflow or script errors.');
  } finally { await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
}
main().catch(error => { console.error(error); process.exitCode=1; });
