const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');
const {chromium} = require(path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));
const repo = path.resolve(__dirname, '../../..');
const output = path.resolve(repo, process.env.ATLAS_CHAT_TEST_OUTPUT || 'artifacts/atlas-profile-video-whisper-20260907/media');
const assets = path.join(repo, 'source/WotLK.Launcher/Assets/Chat');
const fixture = require(path.join(repo, 'source/WotLK.Launcher.IntegrationTests/Frontend/chat-fixtures.cjs'));
const checks = [], errors = [];
const check = (name, condition) => { assert.ok(condition, name); checks.push(name); console.log('PASS ' + name); };
(async () => {
  const browser = await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true,args:['--disable-gpu','--no-first-run','--disable-background-networking']});
  try {
    await fs.mkdir(output, {recursive:true});
    const page = await browser.newPage({viewport:{width:1080,height:850}});
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', route => route.abort());
    await page.setContent('<!doctype html><html lang="fr"><head><meta charset="utf-8"></head><body style="margin:0;padding:28px;background:#132333;color:#ecf0f4;font:14px Segoe UI"><main style="display:flex;align-items:flex-start;gap:30px;flex-wrap:wrap"><div id="audio"></div><div id="video" style="width:450px"></div><div id="draft-audio" style="width:250px;height:104px;overflow:hidden"></div><div id="draft-video" style="width:180px;height:104px;overflow:hidden"></div></main><button id="outside">Outside</button><dialog id="viewer" style="width:90vw;max-height:90vh;border:0;padding:0;background:#081523"><div id="viewer-host"></div></dialog></body></html>');
    await page.addStyleTag({content:'*{box-sizing:border-box}[hidden]{display:none!important}button,input{font:inherit}'});
    await page.addStyleTag({content:'@font-face{font-family:Inter;src:url(data:font/ttf;base64,'+(await fs.readFile(path.join(assets,'fonts/Inter-Regular.ttf'))).toString('base64')+');font-weight:400}'});
    await page.addStyleTag({content:await fs.readFile(path.join(assets,'chat-media.css'),'utf8')});
    await page.addScriptTag({content:await fs.readFile(path.join(assets,'chat-media.js'),'utf8')});
    await page.evaluate(data => {
      window.calls = {play:0,expand:0,save:0}; window.instances = {};
      for (const [name,kind,mode] of [['audio','audio','inline'],['video','video','inline'],['draft-audio','audio','draft'],['draft-video','video','draft']]) {
        const player = document.createElement(kind); player.preload = 'none'; player.muted = true;
        player.src = 'data:'+(kind==='audio'?'audio/wav':'video/webm')+';base64,'+data[kind];
        const shell = AtlasChatMedia.create(player,{kind,mode,locale:'fr',fileName:'private-test.'+(kind==='audio'?'wav':'webm'),onPlay:(p,s)=>{calls.play++;calls.lastPlay=p===player&&s===shell;},onExpand:(p,b,s)=>{calls.expand++;calls.lastExpand=p===player&&s===shell&&b.classList.contains('media-expand');}});
        instances[name] = {player,shell}; document.getElementById(name).append(shell);
      }
    },{audio:fixture.viewerAudioBytes.toString('base64'),video:fixture.viewerVideoBytes.toString('base64')});
    await page.waitForFunction(()=>Object.values(instances).every(({player})=>player.readyState>=1));
    await page.evaluate(()=>document.fonts.ready);
    check('Stable shell holds the original player with native controls disabled',await page.evaluate(()=>Object.values(instances).every(({player,shell})=>shell.contains(player)&&!player.controls&&AtlasChatMedia.shellFor(player)===shell)));
    check('No visible filenames or file size chrome',await page.evaluate(()=>Object.values(instances).every(({shell})=>!shell.innerText.includes('private-test')&&shell.getAttribute('aria-label').startsWith('private-test'))));
    check('Finite metadata enables the real seek controls',await page.evaluate(()=>Object.values(instances).every(({shell})=>!shell.querySelector('.media-seek').disabled&&Number(shell.querySelector('.media-seek').max)>3)));
    check('Visible audio prepares metadata without playback despite its hidden media element',await page.evaluate(()=>instances.audio.player.preload==='metadata'&&instances.audio.player.paused&&instances.audio.player.currentTime===0&&instances.audio.player.getBoundingClientRect().height===0));
    check('Draft audio and video fit their exact reserved dimensions',await page.evaluate(()=>['draft-audio','draft-video'].every(name=>{const {shell}=instances[name],r=shell.getBoundingClientRect(),host=shell.parentElement.getBoundingClientRect();return Math.abs(r.height-104)<1&&Math.abs(r.width-host.width)<1&&shell.scrollWidth<=Math.ceil(r.width)&&shell.scrollHeight<=Math.ceil(r.height);} )));
    const draftLongTimes = await page.evaluate(()=>{
      const {player,shell}=instances['draft-video'];
      const descriptors=Object.fromEntries(['currentTime','duration'].map(name=>[name,Object.getOwnPropertyDescriptor(player,name)]));
      try {
        return [754,5025,445556].map(seconds=>{
          for(const name of ['currentTime','duration']) Object.defineProperty(player,name,{configurable:true,value:seconds});
          AtlasChatMedia.update(shell,{});
          const bounds=shell.getBoundingClientRect();
          const nodes=[...shell.querySelectorAll('.media-play,.media-elapsed,.media-duration,.media-seek,.media-volume-button,.media-expand,.media-fullscreen')]
            .filter(node=>!node.hidden).map(node=>({name:node.className,rect:node.getBoundingClientRect()}));
          const collisions=nodes.flatMap((first,index)=>nodes.slice(index+1).filter(second=>
            Math.min(first.rect.right,second.rect.right)-Math.max(first.rect.left,second.rect.left)>.5
            && Math.min(first.rect.bottom,second.rect.bottom)-Math.max(first.rect.top,second.rect.top)>.5)
            .map(second=>[first.name,second.name]));
          return {label:shell.querySelector('.media-duration').textContent,fontSize:getComputedStyle(shell.querySelector('.media-times')).fontSize,
            collisions,inside:nodes.every(({rect})=>rect.width>0&&rect.height>0&&rect.left>=bounds.left&&rect.top>=bounds.top&&rect.right<=bounds.right&&rect.bottom<=bounds.bottom)};
        });
      } finally {
        for(const name of ['currentTime','duration']) {if(descriptors[name]) Object.defineProperty(player,name,descriptors[name]);else delete player[name];}
        AtlasChatMedia.update(shell,{});
      }
    });
    check('Long draft video timestamps stay at 12px without overlapping controls or leaving the 180px preview '+JSON.stringify(draftLongTimes),
      draftLongTimes.every(result=>result.fontSize==='12px'&&result.inside&&result.collisions.length===0));
    await page.waitForFunction(()=>instances.video.player.readyState>=2);
    check('Visible videos decode their first frame before any play click',await page.evaluate(()=>instances.video.player.preload==='metadata'&&instances.video.player.paused&&instances.video.player.currentTime===0&&instances.video.player.videoWidth>0));
    check('Video time and controls overlay the image without a footer',await page.evaluate(()=>{const {shell}=instances.video,box=shell.getBoundingClientRect(),stage=shell.querySelector('.media-stage').getBoundingClientRect(),controls=shell.querySelector('.media-controls').getBoundingClientRect();return Math.abs(box.height-stage.height)<=2&&controls.top>=stage.top&&controls.bottom<=stage.bottom+1&&getComputedStyle(shell.querySelector('.media-controls')).backgroundImage.includes('linear-gradient');}));
    await page.screenshot({path:path.join(output,'video-overlay-paused.png'),fullPage:true});
    await page.evaluate(()=>{instances.video.player.loop=true;});
    await page.locator('#video .media-play').click();
    await page.waitForFunction(()=>instances.video.shell.classList.contains('controls-hidden'));
    check('Controls disappear during real playback after a mouse click',await page.evaluate(()=>!instances.video.player.paused&&getComputedStyle(instances.video.shell.querySelector('.media-controls')).pointerEvents==='none'));
    await page.locator('#video .media-stage').hover();
    check('Pointer movement reveals controls over the same playing video',await page.evaluate(()=>!instances.video.shell.classList.contains('controls-hidden')&&!instances.video.player.paused));
    await page.locator('#video .media-seek').focus();await page.keyboard.press('ArrowRight');
    await page.waitForTimeout(2350);
    check('Keyboard interaction keeps the focused timeline visible',await page.evaluate(()=>!instances.video.shell.classList.contains('controls-hidden')));
    await page.evaluate(()=>instances.video.player.dispatchEvent(new Event('waiting')));
    check('Buffering has accessible busy state without visible loading text or layout shift',await page.evaluate(()=>{const {shell}=instances.video;return shell.getAttribute('aria-busy')==='true'&&shell.querySelector('.media-status').hidden&&Math.abs(shell.getBoundingClientRect().height-shell.querySelector('.media-stage').getBoundingClientRect().height)<=2;}));
    await page.evaluate(()=>{AtlasChatMedia.pause(instances.video.shell);instances.video.player.currentTime=0;instances.video.player.loop=false;calls.play=0;});
    check('Pausing restores the controls',await page.evaluate(()=>!instances.video.shell.classList.contains('controls-hidden')));
    await page.evaluate(data=>{const player=document.createElement('video');player.preload='none';player.src='data:video/webm;base64,'+data;const shell=AtlasChatMedia.create(player);shell.style.cssText='position:absolute;top:6000px;width:300px';document.body.append(shell);instances.distant={player,shell};},fixture.viewerVideoBytes.toString('base64'));
    await page.waitForTimeout(100);
    check('Distant history videos keep preload none',await page.evaluate(()=>instances.distant.player.preload==='none'));
    await page.evaluate(()=>{window.distantVideoReference=new WeakRef(instances.distant.player);instances.distant.shell.remove();delete instances.distant;});
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    const previewCdp = await page.context().newCDPSession(page);await previewCdp.send('HeapProfiler.collectGarbage');await previewCdp.detach();
    check('Removing an unplayed distant video releases its preview observer',await page.evaluate(()=>distantVideoReference.deref()===undefined));
    await page.evaluate(data=>{const player=document.createElement('audio');player.preload='none';player.src='data:audio/wav;base64,'+data;const shell=AtlasChatMedia.create(player);shell.style.cssText='position:absolute;top:6000px;width:300px';document.body.append(shell);instances.distantAudio={player,shell};},fixture.viewerAudioBytes.toString('base64'));
    await page.waitForTimeout(100);
    check('Distant history audio keeps preload none',await page.evaluate(()=>instances.distantAudio.player.preload==='none'));
    await page.evaluate(()=>{window.distantAudioReference=new WeakRef(instances.distantAudio.player);instances.distantAudio.shell.remove();delete instances.distantAudio;});
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    const audioPreviewCdp=await page.context().newCDPSession(page);await audioPreviewCdp.send('HeapProfiler.collectGarbage');await audioPreviewCdp.detach();
    check('Removing an unplayed distant audio releases its preview observer',await page.evaluate(()=>distantAudioReference.deref()===undefined));
    await page.locator('#audio .media-play').click();
    await page.waitForFunction(()=>!instances.audio.player.paused&&calls.play===1);
    check('Play operates the real audio and invokes onPlay with stable arguments',await page.evaluate(()=>calls.lastPlay&&instances.audio.shell.classList.contains('is-playing')&&instances.audio.shell.querySelector('.media-play').getAttribute('aria-label')==='Mettre en pause'));
    await page.locator('#audio .media-play').click();
    check('Pause stops the real audio',await page.evaluate(()=>instances.audio.player.paused));
    const progressRun = await page.evaluate(async()=>{
      const {player,shell}=instances.audio,seek=shell.querySelector('.media-seek');let mutations=0,timeUpdates=0;
      const observer=new MutationObserver(records=>{mutations+=records.filter(record=>record.attributeName==='style').length;});
      observer.observe(seek,{attributes:true});const tick=()=>timeUpdates++;player.addEventListener('timeupdate',tick);
      await player.play();await new Promise(resolve=>setTimeout(resolve,230));const playing={mutations,timeUpdates};
      AtlasChatMedia.pause(shell);await new Promise(resolve=>setTimeout(resolve,30));mutations=0;
      await new Promise(resolve=>setTimeout(resolve,150));const pausedMutations=mutations;
      observer.disconnect();player.removeEventListener('timeupdate',tick);return {playing,pausedMutations};
    });
    check('Progress animates between native timeupdate events during playback',progressRun.playing.mutations>progressRun.playing.timeUpdates+3);
    check('Progress animation stops when the media is paused',progressRun.pausedMutations===0);
    await page.locator('#audio .media-seek').evaluate(node=>{node.value='1';node.dispatchEvent(new Event('input',{bubbles:true}));});
    check('Seeking sets real currentTime',await page.evaluate(()=>Math.abs(instances.audio.player.currentTime-1)<.1));
    await page.locator('#audio .media-seek').press('Home');
    check('Seek keyboard Home reaches the beginning',await page.evaluate(()=>instances.audio.player.currentTime===0));
    await page.locator('#audio .media-seek').press('End');
    check('Seek keyboard End reaches the duration',await page.evaluate(()=>Math.abs(instances.audio.player.currentTime-instances.audio.player.duration)<.1));
    await page.locator('#audio .media-seek').press('ArrowLeft');
    check('Seek keyboard arrows clamp at valid media bounds',await page.evaluate(()=>instances.audio.player.currentTime===0));
    await page.locator('#draft-audio .media-volume-button').click();
    check('Volume popover escapes the clipped draft while keeping DOM ownership',await page.evaluate(()=>{const panel=instances['draft-audio'].shell.querySelector('.media-volume-panel'),r=panel.getBoundingClientRect();return panel.matches(':popover-open')&&instances['draft-audio'].shell.contains(panel)&&document.elementFromPoint(r.left+20,r.top+20)?.closest('.media-volume-panel')===panel;}));
    await page.locator('#draft-audio .media-volume').evaluate(node=>{node.value='.35';node.dispatchEvent(new Event('input',{bubbles:true}));});
    check('Custom volume changes the actual media and unmutes it',await page.evaluate(()=>Math.abs(instances['draft-audio'].player.volume-.35)<.001&&!instances['draft-audio'].player.muted));
    await page.locator('#draft-audio .media-mute').click();
    check('Mute control changes the actual muted property',await page.evaluate(()=>instances['draft-audio'].player.muted));
    await page.locator('#draft-audio .media-volume').focus();
    await page.locator('#draft-audio .media-volume').press('Escape');
    check('Escape closes volume and restores its trigger focus',await page.evaluate(()=>instances['draft-audio'].shell.querySelector('.media-volume-panel').hidden&&document.activeElement===instances['draft-audio'].shell.querySelector('.media-volume-button')));
    await page.locator('#draft-video .media-expand').click();
    check('Expand calls the supplied callback with player, opener and shell',await page.evaluate(()=>calls.expand===1&&calls.lastExpand));
    check('Save is hidden by default and never appears for video',await page.evaluate(()=>{AtlasChatMedia.update(instances.video.shell,{onSave:()=>calls.save++});return instances.audio.shell.querySelector('.media-save').hidden&&instances.video.shell.querySelector('.media-save').hidden;}));
    await page.evaluate(()=>AtlasChatMedia.update(instances.audio.shell,{locale:'en',onSave:()=>calls.save++}));
    check('Partial updates localize controls while preserving mode and media',await page.evaluate(()=>instances.audio.shell.dataset.mode==='inline'&&instances.audio.shell.querySelector('.media-play').getAttribute('aria-label')==='Play'&&AtlasChatMedia.shellFor(instances.audio.player)===instances.audio.shell));
    await page.locator('#audio .media-save').click(); check('Optional audio Save callback is invoked',await page.evaluate(()=>calls.save===1));
    await page.evaluate(()=>{const {player,shell}=instances.video;player.currentTime=1.2;player.volume=.45;player.muted=true;player.playbackRate=1.25;const viewer=document.getElementById('viewer');viewer.showModal();document.getElementById('viewer-host').moveBefore(shell,null);AtlasChatMedia.update(shell,{mode:'viewer',locale:'en'});});
    check('Whole shell movement preserves player identity, time, volume and rate',await page.evaluate(()=>{const {player,shell}=instances.video;return shell.parentElement.id==='viewer-host'&&shell.contains(player)&&Math.abs(player.currentTime-1.2)<.1&&player.volume===.45&&player.playbackRate===1.25;}));
    check('Viewer expand becomes a return action without removing the callback',await page.evaluate(()=>instances.video.shell.querySelector('.media-expand').getAttribute('aria-label')==='Return to conversation'&&!instances.video.shell.querySelector('.media-expand').hidden));
    await page.locator('#viewer .media-volume-button').click();
    check('Volume is linked accessibly and stays usable inside the viewer dialog',await page.evaluate(()=>{const shell=instances.video.shell,panel=shell.querySelector('.media-volume-panel'),button=shell.querySelector('.media-volume-button');return panel.matches(':popover-open')&&button.getAttribute('aria-controls')===panel.id&&document.activeElement===shell.querySelector('.media-volume');}));
    await page.locator('#viewer .media-volume').press('Escape');
    check('Closing the volume popover leaves the viewer dialog open',await page.evaluate(()=>document.getElementById('viewer').open&&instances.video.shell.querySelector('.media-volume-panel').hidden));
    check('Viewer portrait ratios keep the complete controls inside 90vh',await page.evaluate(()=>{const shell=instances.video.shell;shell.style.setProperty('--media-aspect','9 / 16');const r=shell.getBoundingClientRect(),c=shell.querySelector('.media-controls').getBoundingClientRect();return r.height<=innerHeight*.9+1&&c.bottom<=r.bottom+1;}));
    if(await page.evaluate(()=>document.fullscreenEnabled)) {
      await page.locator('#viewer .media-fullscreen').click(); await page.waitForFunction(()=>document.fullscreenElement===instances.video.shell);
      check('Fullscreen targets the stable shell and retains custom controls',await page.evaluate(()=>document.fullscreenElement===instances.video.shell&&instances.video.shell.querySelector('.media-fullscreen').getAttribute('aria-label')==='Exit fullscreen'));
      check('Fullscreen overrides the viewer height cap',await page.evaluate(()=>{const r=instances.video.shell.getBoundingClientRect();return Math.abs(r.width-innerWidth)<1&&Math.abs(r.height-innerHeight)<1;}));
      await page.locator('#viewer .media-fullscreen').click();await page.waitForFunction(()=>!document.fullscreenElement);
    }
    await page.evaluate(()=>{const {shell}=instances.video;document.getElementById('video').moveBefore(shell,null);AtlasChatMedia.update(shell,{mode:'inline'});document.getElementById('viewer').close();});
    await page.emulateMedia({reducedMotion:'reduce'});
    check('Reduced motion removes control transitions',await page.locator('#audio .media-play').evaluate(node=>getComputedStyle(node).transitionDuration==='0s'));
    const reducedRun=await page.evaluate(async()=>{const {player,shell}=instances.audio;player.currentTime=0;await player.play();await new Promise(resolve=>setTimeout(resolve,50));let mutations=0,timeUpdates=0;const observer=new MutationObserver(records=>{mutations+=records.filter(record=>record.attributeName==='style').length;});observer.observe(shell.querySelector('.media-seek'),{attributes:true});const tick=()=>timeUpdates++;player.addEventListener('timeupdate',tick);await new Promise(resolve=>setTimeout(resolve,220));observer.disconnect();player.removeEventListener('timeupdate',tick);AtlasChatMedia.pause(shell);return {mutations,timeUpdates};});
    check('Reduced motion keeps functional progress without continuous animation '+JSON.stringify(reducedRun),reducedRun.mutations<=reducedRun.timeUpdates+1);
    await page.emulateMedia({reducedMotion:'no-preference'});
    const visibilityRun=await page.evaluate(async()=>{
      const {player,shell}=instances.audio;player.currentTime=0;await player.play();await new Promise(resolve=>setTimeout(resolve,30));
      Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));
      let mutations=0,timeUpdates=0;const observer=new MutationObserver(records=>{mutations+=records.filter(record=>record.attributeName==='style').length;});observer.observe(shell.querySelector('.media-seek'),{attributes:true});const tick=()=>timeUpdates++;player.addEventListener('timeupdate',tick);
      await new Promise(resolve=>setTimeout(resolve,300));const hidden={mutations,timeUpdates};
      mutations=0;timeUpdates=0;delete document.hidden;document.dispatchEvent(new Event('visibilitychange'));
      await new Promise(resolve=>setTimeout(resolve,180));const visible={mutations,timeUpdates};observer.disconnect();player.removeEventListener('timeupdate',tick);AtlasChatMedia.pause(shell);return {hidden,visible};
    });
    check('Hidden document stops continuous progress animation',visibilityRun.hidden.mutations<=visibilityRun.hidden.timeUpdates+1);
    check('Visible document resumes smooth progress for media still playing '+JSON.stringify(visibilityRun),visibilityRun.visible.mutations>visibilityRun.visible.timeUpdates+3);
    await page.evaluate(()=>{const {player,shell}=instances.audio;AtlasChatMedia.pause(shell);window.priorPlayCount=calls.play;AtlasChatMedia.dispose(shell);player.dispatchEvent(new Event('play'));});
    check('Dispose pauses media, removes callbacks and unregisters its shell',await page.evaluate(()=>instances.audio.player.paused&&calls.play===priorPlayCount&&AtlasChatMedia.shellFor(instances.audio.player)===null));
    await page.evaluate(()=>{const player=document.createElement('audio');player.src='data:audio/wav;base64,aW52YWxpZA==';player.preload='auto';const shell=AtlasChatMedia.create(player,{fileName:'broken.wav'});document.body.append(shell);instances.broken={player,shell};});
    await page.waitForFunction(()=>!!instances.broken.player.error);
    check('Decoder failure shows a localized status and disables invalid seek',await page.evaluate(()=>instances.broken.shell.classList.contains('is-error')&&instances.broken.shell.querySelector('.media-status').textContent==='Lecture intégrée indisponible.'&&instances.broken.shell.querySelector('.media-seek').disabled));
    await page.evaluate(()=>{const player=document.createElement('audio'),shell=AtlasChatMedia.create(player);document.body.append(shell);window.detachedShellReference=new WeakRef(shell);shell.remove();});
    const cdp = await page.context().newCDPSession(page); await cdp.send('HeapProfiler.collectGarbage');
    check('Shared document listeners do not retain a removed shell',await page.evaluate(()=>detachedShellReference.deref()===undefined));
    await page.evaluate(()=>{const player=document.createElement('audio'),saved=HTMLElement.prototype.showPopover;HTMLElement.prototype.showPopover=undefined;const shell=AtlasChatMedia.create(player);HTMLElement.prototype.showPopover=saved;document.body.append(shell);instances.fallback={player,shell};});
    await page.evaluate(()=>instances.fallback.shell.querySelector('.media-volume-button').click());
    check('Older popover fallback still exposes volume without throwing',await page.evaluate(()=>!instances.fallback.shell.querySelector('.media-volume-panel').hidden&&!instances.fallback.shell.querySelector('.media-volume-panel').hasAttribute('popover')));
    await page.evaluate(()=>AtlasChatMedia.dispose(instances.fallback.shell));
    check('No uncaught browser errors',errors.length===0);
    await page.screenshot({path:path.join(output,'media-controls.png'),fullPage:true});
    await fs.writeFile(path.join(output,'media-controls.json'),JSON.stringify({passed:checks.length,checks,errors,browser:browser.version(),network:false,userSession:false},null,2));
    console.log('Media module: '+checks.length+' checks passed.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
