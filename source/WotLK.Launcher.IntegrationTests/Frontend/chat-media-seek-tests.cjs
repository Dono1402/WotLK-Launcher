// Real pointer/keyboard gestures and native decoding; no real account or network.
// ATLAS_CHAT_SEEK_VIDEO can supply a longer synthetic WebM (70s used for release QA).
const fs = require('node:fs/promises'), path = require('node:path'), os = require('node:os');
const assert = require('node:assert/strict');
const {chromium} = require(path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));
const {viewerVideoBytes} = require('./chat-fixtures.cjs');
const repo = path.resolve(__dirname, '../../..');
const output = path.resolve(repo, process.env.ATLAS_CHAT_TEST_OUTPUT || 'artifacts/media-seek');
const checks = [], samples = [], errors = [];
function check(name, condition) { assert.ok(condition, name); checks.push(name); console.log('PASS '+name); }
function silentWave(seconds = 70) {
  const dataSize = seconds * 16000, bytes = Buffer.alloc(44+dataSize);
  bytes.write('RIFF'); bytes.writeUInt32LE(36+dataSize, 4); bytes.write('WAVEfmt ', 8);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22);
  bytes.writeUInt32LE(8000, 24); bytes.writeUInt32LE(16000, 28); bytes.writeUInt16LE(2, 32);
  bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(dataSize, 40); return bytes;
}
(async () => {
  const script = await fs.readFile(path.join(repo, 'source/WotLK.Launcher/Assets/Chat/chat-media.js'), 'utf8');
  const css = await fs.readFile(path.join(repo, 'source/WotLK.Launcher/Assets/Chat/chat-media.css'), 'utf8');
  const video = process.env.ATLAS_CHAT_SEEK_VIDEO ? await fs.readFile(process.env.ATLAS_CHAT_SEEK_VIDEO) : viewerVideoBytes;
  const browser = await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless:true,
    args:['--disable-gpu','--no-first-run','--disable-background-networking']});
  try {
    for (const kind of ['audio', 'video']) {
      const page = await browser.newPage({viewport:{width:1000,height:700}});
      page.on('pageerror', error => errors.push(error.message));
      await page.route('**/*', route => route.abort());
      await page.setContent('<!doctype html><html><body style="margin:30px"><main style="width:800px"></main><button id="outside">Outside</button></body></html>');
      await page.addStyleTag({content:css}); await page.addScriptTag({content:script});
      await page.evaluate(({kind, base64}) => {
        const player = document.createElement(kind); player.preload='auto'; player.muted=true;
        player.src='data:'+(kind==='audio'?'audio/wav':'video/webm')+';base64,'+base64;
        const shell=AtlasChatMedia.create(player); document.querySelector('main').append(shell);
        window.media={player,shell}; window.trace=[];
        // Forward every operation to the native property; only record duplicates.
        const nativeTime=Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype,'currentTime');
        Object.defineProperty(player,'currentTime',{configurable:true,get(){return nativeTime.get.call(this)},set(value){
          trace.push({type:'set-time',value,at:performance.now()}); nativeTime.set.call(this,value);
        }});
        for (const type of ['seeking','seeked','playing','pause']) player.addEventListener(type,()=>trace.push({type,time:player.currentTime,at:performance.now()}));
      }, {kind, base64:(kind==='audio'?silentWave():video).toString('base64')});
      await page.waitForFunction(()=>media.player.readyState>=3);
      const duration=await page.evaluate(()=>media.player.duration);
      const slider=page.locator('.media-seek');
      async function pointerAt(fraction) {
        await slider.hover(); const box=await slider.boundingBox();
        return {x:box.x+4.5+(box.width-9)*fraction,y:box.y+box.height/2};
      }
      async function settled() { await page.waitForFunction(()=>!media.player.seeking&&media.player.readyState>=3); }
      async function clear() { await page.evaluate(()=>trace=[]); }
      async function writeCount() { return page.evaluate(()=>trace.filter(event=>event.type==='set-time').length); }
      async function clickAt(fraction) { const point=await pointerAt(fraction); await page.mouse.click(point.x,point.y); await settled(); }
      await page.locator('.media-play').click(); await page.waitForFunction(()=>media.player.currentTime>.03);
      for (const target of [28,52,35]) {
        await pointerAt(target/70); await clear(); await clickAt(target/70);
        const trace=await page.evaluate(()=>window.trace), expected=duration*target/70;
        check(`${kind}: one native seek for the click at ${expected.toFixed(2)}s`, trace.filter(event=>event.type==='set-time').length===1 && trace.filter(event=>event.type==='seeking').length===1);
        check(`${kind}: playback resumes at the requested position`, await page.evaluate(expected=>!media.player.paused&&Math.abs(media.player.currentTime-expected)<.3, expected));
        const first=trace.find(event=>event.type==='set-time'), done=trace.find(event=>event.type==='seeked');
        samples.push({kind,duration,requestedSeconds:expected,seekToSeekedMs:done.at-first.at,trace});
      }
      // During a real drag, the label follows the pointer and decoding stays put.
      const start=await pointerAt(.15), end=await pointerAt(.8); await clear();
      await page.mouse.move(start.x,start.y); await page.mouse.down();
      await page.mouse.move(end.x,end.y,{steps:20});
      check(`${kind}: dragging previews time without repeated decoding`, await writeCount()===0 && await page.evaluate(duration=>Math.abs(Number(media.shell.querySelector('.media-seek').value)-duration*.8)<.1,duration));
      await page.mouse.up(); await settled();
      check(`${kind}: release commits the final drag position once`, await writeCount()===1 && await page.evaluate(duration=>!media.player.paused&&Math.abs(media.player.currentTime-duration*.8)<.3,duration));
      // A paused media must stay paused; the thumb also handles release outside.
      await page.evaluate(()=>AtlasChatMedia.pause(media.shell)); await clear();
      await clickAt(.4);
      check(`${kind}: seeking while paused does not autoplay`, await page.evaluate(()=>media.player.paused));
      const middle=await pointerAt(.5); await clear();
      await page.mouse.move(middle.x,middle.y); await page.mouse.down();
      await page.mouse.move(middle.x+5,middle.y+100); await page.mouse.up(); await settled();
      check(`${kind}: release outside the track completes without a stuck preview`, await writeCount()===1 && await page.evaluate(()=>media.player.paused&&Math.abs(Number(media.shell.querySelector('.media-seek').value)-media.player.currentTime)<.01));
      // Keyboard navigation during a held gesture must survive pointer release.
      const beforeKey=await pointerAt(.5); await clear();
      await page.mouse.move(beforeKey.x,beforeKey.y); await page.mouse.down(); await page.keyboard.press('ArrowLeft');
      const keyboardTime=await page.evaluate(()=>media.player.currentTime);
      await page.mouse.up(); await settled();
      check(`${kind}: pointer release preserves the keyboard destination`, await page.evaluate(expected=>Math.abs(media.player.currentTime-expected)<.001, keyboardTime) && await writeCount()===1);
      await slider.press('Home'); await settled();
      check(`${kind}: Home still seeks to zero`, await page.evaluate(()=>media.player.currentTime===0));
      await slider.press('End'); await settled();
      check(`${kind}: End still seeks to duration`, await page.evaluate(()=>Math.abs(media.player.currentTime-media.player.duration)<.001));
      // If playback naturally ends while the thumb is held, committing a prior
      // destination must keep the user's original intent to continue playing.
      await page.evaluate(()=>{media.player.currentTime=media.player.duration-.3;media.player.play();});
      await page.waitForFunction(()=>!media.player.paused&&!media.player.seeking);
      const held=await pointerAt(.5); await page.mouse.move(held.x,held.y); await page.mouse.down();
      await page.waitForFunction(()=>media.player.ended);
      await page.mouse.up(); await page.waitForFunction(()=>!media.player.paused&&!media.player.seeking&&media.player.readyState>=3);
      check(`${kind}: playback resumes if it ended during a held drag`, await page.evaluate(()=>Math.abs(media.player.currentTime-media.player.duration*.5)<.3));
      // Leaving the media scope cancels a pending gesture and cannot restart it.
      const cancel=await pointerAt(.25); await clear();
      await page.mouse.move(cancel.x,cancel.y); await page.mouse.down();
      await page.evaluate(()=>AtlasChatMedia.pause(media.shell)); await page.mouse.up();
      check(`${kind}: scope pause cancels the unfinished seek`, await writeCount()===0 && await page.evaluate(()=>media.player.paused));
      const blur=await pointerAt(.3); await clear();
      await page.mouse.move(blur.x,blur.y); await page.mouse.down(); await page.locator('#outside').focus(); await page.mouse.up(); await settled();
      check(`${kind}: losing focus commits only one final position`, await writeCount()===1 && await page.evaluate(()=>media.player.paused));
      await page.close();
    }
    check('No uncaught browser errors', errors.length===0);
    await fs.mkdir(output,{recursive:true});
    await fs.writeFile(path.join(output,'media-seek.json'),JSON.stringify({passed:checks.length,checks,samples,errors,browser:browser.version(),externalNetwork:false,userSession:false,
      limitations:'Fully buffered synthetic media. Measures native seeking, not network delay or audible hardware output.'},null,2));
    console.log('Media seeking: '+checks.length+' checks passed.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
