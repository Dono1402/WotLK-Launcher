const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const {randomBytes} = require('node:crypto');
const {createLauncherServer} = require('./launcher-server.cjs');
const {readArmory,revisionDirectory} = require('./armory-cache.cjs');
const {combatDetails} = require('./tests/fixtures/combat.cjs');
const {chromium} = require(process.env.PLAYWRIGHT_MODULE || path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));

async function verifyInlineProfileEditor(page,locale,avatar,output,width) {
  const original = {type:'profile',locale,avatar,username:'Compte de test',statusMessage:'Disponible',bio:'Profil local de vérification',
    canUpdateSocialProfile:true,canModifyAvatar:true,canRemoveAvatar:true,profileBusy:false,avatarBusy:false,profileError:'',profileNotice:'',avatarError:'',avatarNotice:'',
    banner:null,bannerPositionX:.5,bannerPositionY:.3,canModifyBanner:true,bannerBusy:false,bannerError:''};
  const publish = data => page.evaluate(data => window.__sendProfile(data),data);
  const messages = () => page.evaluate(() => window.__bridgeMessages);
  const lastAction = () => page.evaluate(() => window.__bridgeMessages.at(-1));
  const draft = {statusMessage:'Disponible pour un donjon',bio:'Une bio personnelle.\nÀ bientôt en Azeroth !'};
  await page.evaluate(() => { document.getElementById('character-view').contentWindow.__profileEditorSentinel='unchanged'; });
  assert.equal(await page.locator('#customize').count(),0,'The generic Customize button must be removed');
  await page.locator('#edit-profile').click();
  assert.equal(await page.locator('#profile-editor').isVisible(),true);
  assert.equal(await page.locator('#edit-profile').getAttribute('aria-expanded'),'true');
  assert.equal(await page.locator('#edit-status').inputValue(),original.statusMessage);
  assert.equal(await page.locator('#edit-bio').inputValue(),original.bio);
  assert.equal(await page.locator('#edit-status').getAttribute('maxlength'),'80');
  assert.equal(await page.locator('#edit-bio').getAttribute('maxlength'),'280');
  await page.locator('#edit-status').fill(draft.statusMessage);
  await page.locator('#edit-bio').fill(draft.bio);
  await publish({...original,avatarNotice:'Avatar disponible'});
  assert.equal(await page.locator('#edit-status').inputValue(),draft.statusMessage,'A native refresh overwrote the status draft');
  assert.equal(await page.locator('#edit-bio').inputValue(),draft.bio,'A native refresh overwrote the biography draft');
  await page.screenshot({path:path.join(output,`launcher-edit-${locale}-${width}.png`),fullPage:true});
  const oldCount=(await messages()).length;
  await page.locator('#save-profile').click();
  assert.deepEqual(await lastAction(),{action:'save-profile',...draft});
  await publish({type:'profile-save-result',accepted:true});
  assert.equal(await page.locator('#profile-editor').isVisible(),true,'Accepting a request must not claim a completed save');
  await publish({...original,profileBusy:true});
  assert.equal(await page.locator('#save-profile').isDisabled(),true);
  assert.equal((await messages()).length,oldCount+1,'A save emitted duplicate bridge messages');
  await publish({...original,profileError:'Enregistrement temporairement indisponible.'});
  assert.equal(await page.locator('#profile-editor').isVisible(),true);
  assert.equal(await page.locator('#edit-bio').inputValue(),draft.bio,'A failed save lost the draft');
  assert.match(await page.locator('#notification-text').textContent(),/indisponible/);
  await page.locator('#dismiss-notification').click();
  assert.equal(await page.locator('#profile-notice').isVisible(),false);
  await publish({...original,profileError:'Enregistrement temporairement indisponible.'});
  assert.equal(await page.locator('#profile-notice').isVisible(),false,'A dismissed error returned after an unchanged profile refresh');
  await page.locator('#save-profile').click();
  await publish({type:'profile-save-result',accepted:false,message:'Nouvelle tentative nécessaire.'});
  assert.equal(await page.locator('#profile-editor').isVisible(),true);
  assert.equal(await page.locator('#save-profile').isEnabled(),true);
  await page.locator('#save-profile').click();
  await publish({type:'profile-save-result',accepted:true});
  await publish({...original,profileBusy:true});
  const saved={...original,...draft,profileNotice:locale==='fr'?'Profil enregistré.':'Profile saved.'};
  await publish(saved);
  await page.waitForFunction(() => document.getElementById('profile-editor').hidden);
  assert.equal(await page.locator('#profile-status').textContent(),draft.statusMessage);
  assert.equal(await page.locator('#profile-bio').textContent(),draft.bio);
  await page.locator('#edit-profile').click();
  await page.locator('#edit-status').fill('Brouillon à annuler');
  await page.locator('#cancel-profile').click();
  assert.equal(await page.locator('#profile-editor').isVisible(),false);
  assert.equal(await page.locator('#profile-status').textContent(),draft.statusMessage);
  assert.equal(await page.evaluate(() => document.activeElement.id),'edit-profile');
  assert.equal(await page.locator('#avatar-menu-toggle').count(),0,'The redundant avatar ellipsis button remains');
  await page.locator('#change-avatar').click();
  assert.equal((await lastAction()).action,'change-avatar');
  await publish({...saved,avatarBusy:true,canModifyAvatar:false});
  assert.equal(await page.locator('#change-avatar').isDisabled(),true);
  await publish(saved);
  assert.equal(await page.locator('#avatar-menu').count(),0,'The redundant avatar menu remains');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#profile-editor').isVisible(),false);
  assert.equal(await page.evaluate(() => document.getElementById('character-view').contentWindow.__profileEditorSentinel),'unchanged','Inline customization recreated the character viewer');
  await publish(original);
}

async function verifyBannerEditor(page,locale,avatar,output,width,bannerImage) {
  const original={type:'profile',locale,avatar,username:'Compte de test',statusMessage:'Disponible',bio:'Profil local de vérification',
    canUpdateSocialProfile:true,canModifyAvatar:true,canRemoveAvatar:true,profileBusy:false,avatarBusy:false,profileError:'',profileNotice:'',avatarError:'',avatarNotice:'',
    banner:null,bannerPositionX:.5,bannerPositionY:.3,bannerZoom:1,canModifyBanner:true,bannerBusy:false,bannerError:''};
  const publish=data => page.evaluate(data => window.__sendProfile(data),data);
  const lastAction=() => page.evaluate(() => window.__bridgeMessages.at(-1));
  const headerImage=page.locator('.banner-image'), preview=page.locator('#banner-crop-image');
  const zoom=page.locator('#banner-zoom'), stage=page.locator('#banner-crop-stage');
  const setZoom=value => zoom.evaluate((input,value) => {
    input.value=String(value); input.dispatchEvent(new Event('input',{bubbles:true}));
  },value);
  const geometry=() => page.evaluate(() => {
    const bounds=selector => {
      const r=document.querySelector(selector).getBoundingClientRect();
      return {left:r.left,top:r.top,right:r.right,bottom:r.bottom,width:r.width,height:r.height};
    };
    const image=document.querySelector('#banner-crop-image');
    return {image:bounds('#banner-crop-image'),frame:bounds('#banner-crop-frame'),hero:bounds('#profile-hero'),
      naturalRatio:image.naturalWidth/image.naturalHeight};
  });
  const checkCoverage=async reason => {
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const g=await geometry(), tolerance=1;
    assert.ok(g.image.width>0 && g.image.height>0,reason+': image is empty');
    assert.ok(g.image.left<=g.frame.left+tolerance && g.image.top<=g.frame.top+tolerance &&
      g.image.right>=g.frame.right-tolerance && g.image.bottom>=g.frame.bottom-tolerance,reason+': '+JSON.stringify(g));
    assert.ok(Math.abs(g.frame.width/g.frame.height-g.hero.width/g.hero.height)<.04,'The crop preview does not match the real banner ratio: '+reason+' '+JSON.stringify(g));
    assert.ok(Math.abs(g.image.width/g.image.height-g.naturalRatio)<.02,'The crop preview distorts the source image');
    return g;
  };
  const headerState=async () => {
    await page.waitForFunction(() => {
      const image=document.querySelector('.banner-image');
      return image.complete && image.naturalWidth>0;
    });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    return headerImage.evaluate(image => ({
      src:image.getAttribute('src'),width:image.getBoundingClientRect().width,height:image.getBoundingClientRect().height,
      left:image.getBoundingClientRect().left,top:image.getBoundingClientRect().top,
      objectPosition:getComputedStyle(image).objectPosition,transform:getComputedStyle(image).transform
    }));
  };
  const drag=async (dx,dy) => {
    const box=await page.locator('#banner-crop-frame').boundingBox();
    const x=box.x+box.width*.72,y=box.y+box.height*.5;
    await page.mouse.move(x,y); await page.mouse.down();
    await page.mouse.move(x+dx,y+dy,{steps:4}); await page.mouse.up();
  };
  await publish(original);
  const originalHeader=await headerState();
  await page.locator('#search').focus();
  await page.locator('.roster').hover();
  await page.waitForFunction(() => Number(getComputedStyle(document.getElementById('edit-banner')).opacity)===0);
  await page.locator('#profile-hero').hover();
  await page.waitForFunction(() => Number(getComputedStyle(document.getElementById('edit-banner')).opacity)===1);
  await page.locator('#edit-banner').click();
  assert.equal(await page.locator('#banner-menu').isVisible(),true);
  await page.locator('#reposition-banner').click();
  assert.equal(await page.locator('#banner-editor').isVisible(),true,'The crop command did not open the crop dialog');
  assert.equal(await page.locator('#banner-position-x,#banner-position-y').count(),0,'The old axis sliders remain');
  assert.equal(await page.locator('#banner-editor #choose-banner,#banner-editor #reset-banner,#banner-editor #recenter-banner').count(),0,'Secondary actions still clutter the crop dialog');
  assert.equal(await page.locator('#save-banner').textContent(),locale==='fr'?'Appliquer':'Apply');
  assert.equal(await zoom.getAttribute('min'),'1');
  assert.equal(await zoom.getAttribute('max'),'3');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#banner-editor').isVisible(),false);
  assert.equal(await page.evaluate(() => document.activeElement.id),'edit-banner');
  await page.locator('#edit-banner').press('Enter');
  await page.locator('#choose-banner').click();
  assert.equal((await lastAction()).action,'choose-banner');
  await publish({type:'banner-selected',image:bannerImage,positionX:.5,positionY:.3,zoom:1});
  await preview.waitFor();
  await page.waitForFunction(() => {
    const image=document.getElementById('banner-crop-image');
    return image.complete && image.naturalWidth>0;
  });
  assert.equal(await preview.getAttribute('src'),bannerImage);
  const minimum=await checkCoverage('Minimum zoom');
  await stage.focus();
  await page.keyboard.press('ArrowLeft');
  assert.ok(Math.abs((await geometry()).image.left-minimum.image.left)<1,'The width-constrained image moved sideways at minimum zoom');
  await setZoom(2);
  await zoom.focus(); await page.keyboard.press('ArrowLeft');
  assert.ok(Number(await zoom.inputValue())<2,'Zoom is not keyboard accessible');
  await setZoom(2);
  const beforeKeyboard=await geometry();
  await stage.focus(); await page.keyboard.press('ArrowRight');
  assert.notEqual((await geometry()).image.left,beforeKeyboard.image.left,'Keyboard panning did not move the zoomed image');
  const beforeDrag=await geometry();
  await drag(35,25);
  const afterDrag=await checkCoverage('Two-axis drag');
  assert.notEqual(afterDrag.image.left,beforeDrag.image.left,'Horizontal dragging did not move the crop');
  assert.notEqual(afterDrag.image.top,beforeDrag.image.top,'Vertical dragging did not move the crop');
  for (const [dx,dy] of [[3000,3000],[-3000,-3000],[3000,-3000],[-3000,3000]]) {
    await drag(dx,dy); await checkCoverage('Clamped corner '+dx+','+dy);
  }
  await setZoom(0); assert.equal(Number(await zoom.inputValue()),1);
  await checkCoverage('Zooming out after dragging to an edge');
  await setZoom(9); assert.equal(Number(await zoom.inputValue()),3);
  await checkCoverage('Maximum zoom');
  if (locale==='fr' && width===1228) {
    const currentViewport=page.viewportSize();
    await page.setViewportSize({width:908,height:620});
    await checkCoverage('Window resize while editing');
    await page.setViewportSize(currentViewport);
    await checkCoverage('Restored window size');
  }
  await stage.focus(); await page.keyboard.press('Home');
  assert.equal(Number(await zoom.inputValue()),1);
  const centered=await checkCoverage('Recenter');
  assert.ok(Math.abs((centered.image.top+centered.image.bottom)-(centered.frame.top+centered.frame.bottom))<2,'Recenter did not center the image');
  await setZoom(1.65); await drag(28,18);
  const draftGeometry=await geometry();
  await page.screenshot({path:path.join(output,'launcher-banner-edit-'+locale+'-'+width+'.png')});
  if (locale==='fr' && width===1228) {
    await page.locator('#banner-editor').screenshot({path:path.join(output,'launcher-banner-crop-dialog.png')});
  }
  await publish(original);
  assert.equal(await preview.getAttribute('src'),bannerImage,'A profile refresh replaced the chosen banner');
  assert.deepEqual((await geometry()).image,draftGeometry.image,'A profile refresh changed the crop draft');
  await page.locator('#cancel-banner').click();
  assert.equal((await lastAction()).action,'cancel-banner');
  assert.equal(await page.locator('#banner-editor').isVisible(),false);
  assert.equal(await page.evaluate(() => document.activeElement.id),'edit-banner');
  assert.deepEqual(await headerState(),originalHeader,'Cancelling changed the persisted header');

  await page.locator('#edit-banner').click();
  await page.locator('#choose-banner').click();
  await publish({type:'banner-selected',image:bannerImage,positionX:.5,positionY:.3,zoom:1});
  await setZoom(1.75); await drag(-24,12);
  await page.locator('#save-banner').click();
  const requested=await lastAction();
  assert.equal(requested.action,'save-banner'); assert.equal(requested.zoom,1.75);
  assert.ok(requested.positionX>=0 && requested.positionX<=1 && requested.positionY>=0 && requested.positionY<=1);
  await publish({type:'banner-save-result',accepted:true,completed:true,succeeded:false,error:'Échec de sauvegarde test'});
  assert.equal(await page.locator('#banner-editor').isVisible(),true);
  assert.equal(await preview.getAttribute('src'),bannerImage,'A failed save lost the selected image');
  assert.equal(Number(await zoom.inputValue()),1.75,'A failed save lost the zoom');
  assert.match(await page.locator('#notification-text').textContent(),/Échec/);
  assert.equal(await page.locator('#notification-text').isVisible(),true,'The error is hidden behind the crop dialog');
  await page.locator('#save-banner').click();
  await publish({type:'banner-save-result',accepted:true,completed:false,succeeded:false,error:null});
  assert.equal(await page.locator('#banner-editor').isVisible(),true,'A pending save was reported as successful');
  await publish({type:'banner-save-result',accepted:true,completed:true,succeeded:true,error:null});
  const saved={...original,banner:bannerImage,bannerPositionX:requested.positionX,bannerPositionY:requested.positionY,bannerZoom:requested.zoom,hasBannerCustomization:true};
  await publish(saved);
  await page.waitForFunction(() => !document.getElementById('banner-editor').open && document.getElementById('banner-editor').getClientRects().length===0);
  assert.equal(await headerImage.getAttribute('src'),bannerImage,'The saved banner was not displayed');
  const persistedHeader=await headerState();
  if (locale==='fr' && width===1228) {
    assert.match(await page.locator('.hero-feedbacks').textContent(),/Enregistré/);
    assert.equal(await page.locator('.hero-feedbacks [role=status]').evaluateAll(nodes => nodes.filter(node =>
      node.textContent.trim() && node.getClientRects().length>0).length),1,'Multiple feedback messages are visible');
    await publish(saved);
    await page.waitForFunction(() => ![...document.querySelectorAll('.hero-feedbacks [role=status]')].some(node =>
      node.textContent.trim() && node.getClientRects().length>0),null,{timeout:6500});
    await publish(saved);
    assert.equal(await page.locator('.hero-feedbacks [role=status]').evaluateAll(nodes => nodes.filter(node =>
      node.textContent.trim() && node.getClientRects().length>0).length),0,'A profile refresh resurrected an expired success message');
  }
  await page.locator('#edit-banner').click();
  await page.locator('#reposition-banner').click();
  assert.equal(Number(await zoom.inputValue()),1.75,'Reopening lost the persisted zoom');
  await setZoom(2.4);
  await page.keyboard.press('Escape');
  assert.deepEqual(await headerState(),persistedHeader,'Escape changed the persisted zoom or position');
  await page.locator('#edit-banner').click();
  await page.locator('#reset-banner').click();
  assert.equal(await page.locator('#reset-banner-confirm').isVisible(),true);
  await page.locator('#cancel-reset-banner').click();
  assert.equal(await headerImage.getAttribute('src'),bannerImage,'Cancelling reset changed the image');
  await page.locator('#reset-banner').click();
  await page.locator('#confirm-reset-banner').click();
  assert.deepEqual(await lastAction(),{action:'reset-banner',confirmed:true});
  await publish({type:'banner-save-result',accepted:true,completed:true,succeeded:true,error:null});
  await publish(original);
  assert.deepEqual(await headerState(),originalHeader,'Reset did not restore the original image, zoom and position');
  if (locale==='fr' && width===1228) {
    const panoramicImage=await page.evaluate(() => {
      const canvas=document.createElement('canvas'); canvas.width=1600; canvas.height=100;
      const context=canvas.getContext('2d');
      const gradient=context.createLinearGradient(0,0,1600,100);
      gradient.addColorStop(0,'#267986'); gradient.addColorStop(1,'#dfb958');
      context.fillStyle=gradient; context.fillRect(0,0,1600,100);
      return canvas.toDataURL('image/png');
    });
    await page.locator('#edit-banner').click();
    await page.locator('#choose-banner').click();
    await publish({type:'banner-selected',image:panoramicImage,positionX:.5,positionY:.5,zoom:1});
    await page.waitForFunction(() => document.getElementById('banner-crop-image').naturalWidth===1600);
    const wide=await checkCoverage('Panoramic source at minimum zoom');
    await stage.focus(); await page.keyboard.press('ArrowDown');
    assert.ok(Math.abs((await geometry()).image.top-wide.image.top)<1,'A height-constrained image moved vertically at minimum zoom');
    await page.keyboard.press('ArrowRight');
    assert.notEqual((await geometry()).image.left,wide.image.left,'A panoramic source cannot be panned horizontally');
    await setZoom(2.5); await drag(-3000,3000);
    await checkCoverage('Panoramic source zoomed and dragged to an edge');
    await setZoom(1); await checkCoverage('Panoramic source zoomed out at an edge');
    await page.locator('#cancel-banner').click();
    assert.deepEqual(await headerState(),originalHeader,'Cancelling the panoramic draft changed the saved banner');
  }
  await page.locator('.roster').hover(); await page.locator('#search').focus();
}

async function main() {
  const output = path.resolve(__dirname,'../../artifacts/armory-prototype');
  const current = await readArmory(output);
  const assetDir = path.join(revisionDirectory(current.revision,output),'assets');
  const original = JSON.parse(await fs.readFile(path.join(assetDir,'character.json'),'utf8'));
  const originalDetails = JSON.parse(await fs.readFile(path.join(assetDir,'item-details.json'),'utf8'));
  const ready = {revision:'a'.repeat(32),modelReady:true,assetDir,
    character:{...original,characterId:'101',raceId:10},details:originalDetails};
  const equipment = original.equipment.map((item,index) => ({...item,icon:index===original.equipment.length-1?null:item.icon}));
  const unavailable = {revision:'b'.repeat(32),modelReady:false,assetDir:null,
    iconFiles:Object.fromEntries(equipment.filter(item => item.icon).map(item => [item.icon,path.join(assetDir,item.icon)])),
    character:{...original,characterId:'102',name:'Autreperso',level:80,raceId:1,classId:1,equipment,attached:[]},details:originalDetails};
  for (const entry of [ready,unavailable]) entry.character.statistics = {
    schemaVersion:2,source:'arthas-combat-stats',characterName:entry.character.name,
    characterCapturedAt:entry.character.capturedAt,savedAt:new Date().toISOString(),...combatDetails()
  };
  const avatar = 'data:image/png;base64,'+await fs.readFile(path.join(assetDir,original.equipment.find(item => item.icon).icon),'base64');
  const bannerImage='data:image/png;base64,'+await fs.readFile(path.join(__dirname,'../../source/WotLK.Launcher/Assets/Launcher/visuals/icecrown-citadel.png'),'base64');
  let status = 'ready', refreshing = false, retryCount = 0;
  let roster = [
    {id:'101',name:ready.character.name,classId:8,level:ready.character.level,online:true,available:true},
    {id:'102',name:'Autreperso',classId:1,level:80,online:false,available:true},
    {id:'103',name:'Enattente',classId:6,level:55,online:false,available:false}
  ];
  const entries = new Map([['101',ready],['102',unavailable]]);
  const key = randomBytes(32).toString('hex');
  const server = createLauncherServer({key,armory:{list:() => ({status,refreshing,characters:roster}),
    retry:async () => { retryCount++; refreshing=true; },entry:id => entries.get(id)}});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  const browser = await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader']});
  const results = [];
  try {
    for (const locale of ['fr','en']) {
      for (const viewport of [{width:1228,height:794},{width:1068,height:654},{width:868,height:614},{width:808,height:554}]) {
        const context = await browser.newContext({viewport,extraHTTPHeaders:{'X-Atlas-Armory-Key':key}});
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror',error => errors.push(error.message));
        page.on('response',response => { if (response.status()>=400 && !response.url().endsWith('favicon.ico')) errors.push(response.status()+' '+response.url()); });
        await page.addInitScript(() => {
          window.__bridgeMessages = [];
          window.__sendProfile = () => {};
          Object.defineProperty(window,'chrome',{configurable:true,value:{webview:{
            postMessage:message => window.__bridgeMessages.push(message),
            addEventListener:(_,callback) => { window.__sendProfile = data => callback({data}); }
          }}});
        });
        await page.goto(base+'/?lang='+locale);
        await page.waitForFunction(() => window.__bridgeMessages.some(message => message.action==='ready'));
        await page.evaluate(({locale,avatar}) => window.__sendProfile({type:'profile',locale,avatar,username:'Compte de test',statusMessage:'Disponible',bio:'Profil local de vérification',canUpdateSocialProfile:true,canModifyAvatar:true,canRemoveAvatar:true,canModifyBanner:true,bannerPositionX:.5,bannerPositionY:.3}),{locale,avatar});
        assert.equal(await page.locator('#profile-name').textContent(),'Compte de test');
        assert.equal(await page.locator('#profile-status').textContent(),'Disponible');
        assert.equal(await page.locator('#profile-status').isVisible(),true);
        assert.equal(await page.locator('#profile-bio').textContent(),'Profil local de vérification');
        assert.equal(await page.locator('#profile-bio').isVisible(),true);
        assert.equal(await page.locator('#profile-avatar').isVisible(),true);
        await page.waitForFunction(() => document.querySelector('#profile-avatar').complete && document.querySelector('#profile-avatar').naturalWidth>0);
        await page.locator('.character').first().waitFor();
        assert.equal(await page.locator('.character').count(),3);
        assert.equal(await page.locator('#character-count').textContent(),'3');
        assert.equal(await page.locator('#search').isVisible(),true);
        assert.equal(await page.locator('.profile-eyebrow').count(),0,'The redundant profile label remains');
        assert.equal(await page.locator('html').getAttribute('lang'),locale);
        assert.equal(await page.locator('#customize').count(),0);
        const frame = () => page.frames().find(candidate => /\/characters\/\d+\/view/.test(candidate.url()));
        await page.waitForFunction(() => document.querySelector('iframe').contentWindow.armory?.ready===true);
        const active = frame();
        assert.equal(await active.locator('html').getAttribute('lang'),locale);
        assert.equal(await active.locator('h1').textContent(),original.name);
        assert.equal(await active.locator('#snapshot,.snapshot').count(),0,'The snapshot date is still shown');
        assert.equal(await active.locator('button.slot').count(),original.equipment.length);
        if (locale==='fr' && viewport.width===1228) {
          await page.locator('#profile-hero').hover();
          await page.waitForFunction(() => Number(getComputedStyle(document.getElementById('edit-banner')).opacity)===1);
          await page.locator('#profile-hero').screenshot({path:path.join(output,'launcher-profile-header.png')});
        }
        await verifyInlineProfileEditor(page,locale,avatar,output,viewport.width);
        await verifyBannerEditor(page,locale,avatar,output,viewport.width,bannerImage);
        const editedFrameCount = await active.evaluate(() => window.armory.frames);
        await active.waitForFunction(previous => window.armory.frames>=previous+2,editedFrameCount);
        const header = await page.evaluate(() => {
          const banner=document.querySelector('.banner').getBoundingClientRect();
          const image=document.querySelector('.banner-image');
          const name=document.getElementById('profile-name').getBoundingClientRect();
          const avatar=document.querySelector('.avatar').getBoundingClientRect();
          return {banner:{x:banner.x,y:banner.y,width:banner.width,height:banner.height},
            name:{x:name.x,y:name.y,width:name.width,height:name.height},avatarWidth:avatar.width,
            imageFit:getComputedStyle(image).objectFit,imageReady:image.complete && image.naturalWidth>0};
        });
        assert.ok(header.avatarWidth>=100,'Profile avatar was not enlarged');
        assert.equal(header.imageFit,'cover','The banner must fill the whole header');
        assert.equal(header.imageReady,true,'The banner image did not load');
        assert.ok(header.name.y>=header.banner.y && header.name.y+header.name.height<=header.banner.y+header.banner.height+1,'The nickname is outside the banner');
        const pixels = await active.evaluate(() => {
          const gl = window.armory.renderer.getContext();
          const bytes = new Uint8Array(gl.drawingBufferWidth*gl.drawingBufferHeight*4);
          gl.readPixels(0,0,gl.drawingBufferWidth,gl.drawingBufferHeight,gl.RGBA,gl.UNSIGNED_BYTE,bytes);
          let colored = 0;
          for (let i=0;i<bytes.length;i+=4) if (Math.abs(bytes[i]-bytes[0])+Math.abs(bytes[i+1]-bytes[1])+Math.abs(bytes[i+2]-bytes[2])>40) colored++;
          return colored;
        });
        assert.ok(pixels>1000,'Blank integrated WebGL canvas');
        const layout = async target => target.evaluate(() => {
          const bounds = selector => { const {x,y,width,height}=document.querySelector(selector).getBoundingClientRect(); return {x,y,width,height}; };
          return {width:innerWidth,height:innerHeight,scrollWidth:document.documentElement.scrollWidth,scrollHeight:document.documentElement.scrollHeight,
            ...(document.querySelector('.armory') ? {armory:bounds('.armory'),stats:bounds('#character-summary'),scene:bounds('#scene')} : {})};
        });
        const shellLayout = await layout(page), modelLayout = await layout(active);
        assert.ok(shellLayout.scrollWidth<=shellLayout.width,JSON.stringify({shellLayout}));
        assert.ok(shellLayout.scrollHeight<=shellLayout.height,JSON.stringify({shellLayout}));
        assert.ok(modelLayout.scrollWidth<=modelLayout.width,JSON.stringify({modelLayout}));
        assert.ok(modelLayout.scrollHeight<=modelLayout.height,JSON.stringify({modelLayout}));
        assert.ok(modelLayout.stats.x>=modelLayout.armory.x+modelLayout.armory.width-1,'Statistics overlap equipment');
        assert.ok(modelLayout.scene.width>=100 && modelLayout.scene.height>=150,'Scene too small');
        assert.deepEqual(await active.evaluate(() => [...document.querySelectorAll('.equipment .slot-copy')].filter(copy => {
          const text = copy.getBoundingClientRect(), slot = copy.closest('.slot').getBoundingClientRect();
          return text.top<slot.top-1 || text.bottom>slot.bottom+1;
        }).map(copy => copy.textContent)),[],'Equipment labels overlap adjacent rows');
        await page.screenshot({path:path.join(output,`launcher-${locale}-${viewport.width}.png`)});
        await page.locator('[data-id="102"]').click();
        await page.waitForFunction(() => document.querySelector('iframe').contentWindow.armory?.data?.characterId==='102');
        await page.waitForFunction(() => document.querySelector('iframe').contentWindow.armory?.ready===true);
        const noModel = frame();
        assert.equal(await noModel.locator('#loading').textContent(),locale==='fr'?'Modèle 3D indisponible pour cet équipement':'3D model unavailable for this equipment');
        assert.equal(await noModel.locator('.scene-toolbar').isVisible(),false);
        assert.equal(await noModel.locator('button.slot').count(),original.equipment.length);
        assert.equal(await noModel.locator('button.slot img').count(),equipment.filter(item => item.icon).length);
        await noModel.waitForFunction(() => [...document.querySelectorAll('button.slot img')].every(image => image.complete && image.naturalWidth>0));
        assert.equal(await noModel.locator('button.slot img').evaluateAll(images => images.every(image => image.complete && image.naturalWidth>0)),true);
        assert.ok(await noModel.locator('#character-stats dd').count()>0);
        const slot = noModel.locator('button.slot').first();
        await slot.click();
        assert.equal(await noModel.locator('#item-popover').isVisible(),true);
        assert.equal(await noModel.locator('#detail-icon').isVisible(),true);
        assert.equal(await noModel.locator('#detail-icon').evaluate(image => image.complete && image.naturalWidth>0),true);
        await page.keyboard.press('Escape');
        assert.equal(await noModel.locator('#item-popover').isVisible(),false);
        await noModel.locator(`button.slot[data-slot="${equipment.at(-1).slot}"]`).click();
        assert.equal(await noModel.locator('#item-popover').isVisible(),true);
        assert.equal(await noModel.locator('#detail-icon').isVisible(),false);
        assert.equal(await noModel.locator('#detail-icon').getAttribute('src'),null);
        await page.keyboard.press('Escape');
        await page.screenshot({path:path.join(output,`launcher-no-model-${locale}-${viewport.width}.png`)});
        await page.locator('[data-id="103"]').click();
        assert.equal(await page.locator('#character-view').isVisible(),false);
        assert.equal(await page.locator('#character-view').getAttribute('src'),null);
        assert.equal(await page.locator('#empty-state').textContent(),locale==='fr'?'Données du personnage en cours de récupération':'Retrieving character data');
        await page.locator('#search').fill('introuvable');
        assert.equal(await page.locator('.character').count(),0);
        assert.equal(await page.locator('#roster-status').textContent(),locale==='fr'?'Aucun résultat':'No results');
        await page.locator('#search').fill('Autreperso');
        assert.equal(await page.locator('.character').count(),1);
        assert.equal(await page.locator('#search').isVisible(),true,'Filtering to one result hid the search field');
        assert.equal(await page.locator('#character-count').textContent(),'3','The counter reflects filtered rows instead of owned characters');
        if (viewport.width===1068) {
          await page.locator('[data-id="102"]').click();
          await page.waitForFunction(() => document.querySelector('iframe').contentWindow.armory?.ready===true);
          const currentFrame = frame();
          await currentFrame.evaluate(() => { window.__persist = 'unchanged'; });
          const refresh = async () => {
            await Promise.all([
              page.waitForResponse(response => response.url()===base+'/characters.json'),
              page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')))
            ]);
          };
          status = 'cached';
          await refresh();
          await page.locator('#retry').waitFor();
          assert.equal(await page.locator('#roster-status').textContent(),locale==='fr'?'Dernières données enregistrées':'Last saved data');
          assert.equal(await currentFrame.evaluate(() => window.__persist),'unchanged','Roster polling recreated the selected viewer');
          const retriesBefore = retryCount;
          await Promise.all([
            page.waitForResponse(response => response.url()===base+'/characters.json?refresh=1'),
            page.locator('#retry').click()
          ]);
          await page.waitForFunction(() => document.querySelector('#retry').disabled);
          assert.equal(retryCount,retriesBefore+1,'Retry did not trigger a server read');
          assert.equal(await page.locator('#roster-status').textContent(),locale==='fr'?'Actualisation…':'Refreshing…');
          await refresh();
          assert.equal(retryCount,retriesBefore+1,'Ordinary polling triggered another server read');
          assert.equal(await currentFrame.evaluate(() => window.__persist),'unchanged','Retry recreated the selected viewer');
          refreshing = false;
          await refresh();
          await page.waitForFunction(() => !document.querySelector('#retry').disabled);
          const nextLocale = locale==='fr'?'en':'fr';
          await page.evaluate(locale => window.__sendProfile({type:'profile',locale,username:'Compte de test'}),nextLocale);
          await page.waitForFunction(locale => document.querySelector('iframe').contentWindow.armory?.ready===true
            && document.querySelector('iframe').contentDocument.documentElement.lang===locale,nextLocale);
          assert.equal(await page.locator('[data-id="102"]').getAttribute('aria-pressed'),'true');
          assert.equal(await page.locator('#profile-avatar').isVisible(),false);
          assert.equal(await page.locator('#profile-avatar').getAttribute('src'),null);
          assert.equal(await page.locator('#profile-initial').textContent(),'C');
          assert.equal(await page.locator('#profile-bio').isVisible(),false);
          assert.equal(await page.locator('#profile-status').isVisible(),false);
          const savedStatistics = unavailable.character.statistics;
          unavailable.character.statistics = null;
          await frame().evaluate(() => window.dispatchEvent(new Event('focus')));
          await frame().waitForFunction(() => !window.armory.data.statistics);
          assert.equal(await frame().locator('#stats-status').isVisible(),true);
          unavailable.character.statistics = savedStatistics;
          const fullRoster = roster;
          roster = [fullRoster[0]];
          await refresh();
          await page.waitForFunction(() => document.querySelectorAll('.character').length===1 && document.getElementById('character-count').textContent==='1');
          assert.equal(await page.locator('#search').isVisible(),false,'Search remains visible for a one-character account');
          assert.equal(await page.locator('#search').inputValue(),'','A hidden search still filters the only character');
          assert.equal(await page.locator('#character-count').isVisible(),true,'The one-character counter was hidden');
          await page.waitForFunction(() => document.getElementById('character-view').contentWindow.armory?.ready===true);
          await page.screenshot({path:path.join(output,'launcher-single-character-'+locale+'-'+viewport.width+'.png')});
          roster = fullRoster;
          await refresh();
          await page.waitForFunction(() => document.querySelectorAll('.character').length===3);
          assert.equal(await page.locator('#search').isVisible(),true,'Search did not return after another character appeared');
          roster = [];
          status = 'ready';
          await page.locator('#search').fill('');
          await refresh();
          await page.waitForFunction(() => document.querySelectorAll('.character').length===0);
          assert.equal(await page.locator('#character-view').getAttribute('src'),null,'Removed character remains displayed');
          assert.equal(await page.locator('#empty-state').textContent(),nextLocale==='fr'?'Aucun personnage sur ce compte':'No characters on this account');
          roster = fullRoster;
          await refresh();
          await page.waitForFunction(() => document.querySelectorAll('.character').length===3);
        }
        assert.deepEqual(errors,[]);
        results.push({locale,viewport,pixels,shellLayout,modelLayout,errors});
        await context.close();
      }
    }
    await fs.writeFile(path.join(output,'launcher-verification.json'),JSON.stringify(results,null,2)+'\n');
    console.log(JSON.stringify({passed:results.length,results},null,2));
  } finally {
    await browser.close();
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
  }
}
main().catch(error => {console.error(error);process.exitCode=1;});
