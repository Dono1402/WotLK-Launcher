const fs = require('node:fs/promises');
const { existsSync } = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { createHash } = require('node:crypto');
const assert = require('node:assert/strict');
const fixtures = require('./chat-fixtures.cjs');
const repo = path.resolve(process.env.ATLAS_CHAT_REPO_ROOT || path.join(__dirname, '../../..'));
const assets = path.join(repo, 'source/WotLK.Launcher/Assets/Chat');
const output = path.resolve(repo, process.env.ATLAS_CHAT_TEST_OUTPUT || 'artifacts/atlas-chat-player-20260907/dom');
// Measured innerWidth/innerHeight beneath the 124-DIP header in the fixed V2 shell.
const fixedViewport = { width: 1597, height: 872 };
const bundledPlaywright = path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
function loadPlaywright() {
  if (process.env.ATLAS_CHAT_PLAYWRIGHT) return require(process.env.ATLAS_CHAT_PLAYWRIGHT);
  try { return require('playwright'); }
  catch (error) {
    if (error.code !== 'MODULE_NOT_FOUND') throw error;
    if (existsSync(bundledPlaywright)) return require(bundledPlaywright);
    throw new Error('Install Playwright or set ATLAS_CHAT_PLAYWRIGHT to its absolute module path.', { cause: error });
  }
}
const { chromium } = loadPlaywright();
const edgeCandidates = [process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)', process.env.ProgramFiles || 'C:/Program Files']
  .map(root => path.join(root, 'Microsoft/Edge/Application/msedge.exe'));
const edgePath = process.env.ATLAS_CHAT_EDGE_PATH || edgeCandidates.find(candidate => existsSync(candidate));
const appUrl = 'https://animeclub.fr/atlas-messages/';
const checks = [];
let sequence = 1;
let fixtureBrowser;
const clone = value => JSON.parse(JSON.stringify(value));
const check = (name, value) => { assert.ok(value, name); checks.push(name); console.log('PASS ' + name); };
const svgAvatar = id => `<svg xmlns="http://www.w3.org/2000/svg" width="84" height="84" viewBox="0 0 84 84"><defs><linearGradient id="a" x2="1" y2="1"><stop stop-color="${id==='91'?'#758362':id==='42'?'#627c9e':'#926d67'}"/><stop offset="1" stop-color="#233347"/></linearGradient></defs><rect width="84" height="84" rx="42" fill="url(#a)"/><circle cx="42" cy="32" r="16" fill="#d1d9dd" opacity=".76"/><path d="M10 84c2-39 62-39 64 0" fill="#a6b8c4" opacity=".76"/></svg>`;
const landscape = `<svg xmlns="http://www.w3.org/2000/svg" width="960" height="480" viewBox="0 0 960 480"><defs><linearGradient id="s" x2="0" y2="1"><stop stop-color="#45647d"/><stop offset="1" stop-color="#b9c8cb"/></linearGradient><linearGradient id="m" x2="0" y2="1"><stop stop-color="#aec7d3"/><stop offset="1" stop-color="#314954"/></linearGradient></defs><rect width="960" height="480" fill="url(#s)"/><circle cx="735" cy="110" r="52" fill="#dce4dc" opacity=".6"/><path d="M0 330 188 80 305 264 390 143 570 350 734 138 960 365V480H0" fill="#67818c"/><path d="m0 355 219-251 159 240 142-140 222 270H0Z" fill="url(#m)"/><path d="m153 180 66-76 86 131-69-36-24-29-27 22Z" fill="#d6e1e1"/><path d="m680 213 54-75 94 115-73-21-22-28-25 22Z" fill="#c7d8dc"/><path d="M0 402q144-49 337 8t308-16 315 44v42H0" fill="#233c46"/><path d="M0 459q210-25 440-6t520 2v25H0" fill="#112d39"/></svg>`;

(async () => {
  await fs.mkdir(output,{recursive:true});
  const hostSource = await fs.readFile(path.join(repo,'source/WotLK.Launcher/UI/V2/Views/ChatViewV2.Rich.cs'),'utf8');
  const policyBlock = hostSource.slice(hostSource.indexOf('internal const string RichContentSecurityPolicy'),hostSource.indexOf('private WebView2CompositionControl'));
  const policy = Array.from(policyBlock.matchAll(/"([^"\n]*)"/g), match=>match[1]).join('');
  if (!edgePath || !existsSync(edgePath)) throw new Error('Microsoft Edge was not found. Set ATLAS_CHAT_EDGE_PATH to its executable.');
  const browser = await chromium.launch({executablePath:edgePath,headless:true,args:['--disable-gpu','--no-first-run','--disable-background-networking']});
  fixtureBrowser = browser;
  const page = await browser.newPage({viewport:fixedViewport,deviceScaleFactor:1});
  await page.emulateMedia({reducedMotion:'no-preference'});
  const capture = async name => { await settleMotion(page); return page.screenshot({path:path.join(output,name),omitBackground:true}); };
  const iconOnlySend = expected => page.locator('#send-button').evaluate((node,label)=>{
    const box=node.getBoundingClientRect();
    return node.getAttribute('aria-label')===label&&node.title===label&&node.innerText.trim()===''
      &&node.querySelectorAll('svg').length===1&&Math.abs(box.width-box.height)<1;
  },expected);
  const composerOrder = () => page.evaluate(()=>{
    const composer=document.querySelector('#composer-box').getBoundingClientRect();
    const ids=['attach-button','share-game-button','send-button'];
    const nodes=ids.map(id=>document.getElementById(id)),boxes=nodes.map(node=>node.getBoundingClientRect());
    return Array.from(document.querySelectorAll('.composer-toolbar button')).map(node=>node.id).join(',')===ids.join(',')
      &&boxes[0].left>composer.left+composer.width/2&&boxes[0].right<=boxes[1].left&&boxes[1].right<=boxes[2].left
      &&boxes[1].left-boxes[0].right<=16&&boxes[2].left-boxes[1].right<=20&&composer.right-boxes[2].right<=20
      &&boxes.every(box=>Math.abs(box.top+box.height/2-boxes[2].top-boxes[2].height/2)<=1);
  });
  const errors = [];
  page.on('pageerror',error=>errors.push(error.message));
  // Match the native resolver's byte ranges so Chromium can seek local media.
  const fulfillMedia = (route,contentType,bytes) => {
    const range=/^bytes=(\d+)-(\d*)$/.exec(route.request().headers().range||'');
    const start=range?Number(range[1]):0,end=range&&range[2]?Math.min(Number(range[2]),bytes.length-1):bytes.length-1;
    const headers={'Accept-Ranges':'bytes','Content-Length':String(end-start+1)};
    if(range)headers['Content-Range']=`bytes ${start}-${end}/${bytes.length}`;
    return route.fulfill({status:range?206:200,contentType,headers,body:bytes.subarray(start,end+1)});
  };
  const routeFixture = async route => {
    const url = new URL(route.request().url());
    if (url.origin === 'https://animeclub.fr' && url.pathname.startsWith('/atlas-messages/')) {
      const relative = decodeURIComponent(url.pathname.slice('/atlas-messages/'.length)) || 'index.html';
      const target = path.resolve(assets, relative);
      if (!target.startsWith(assets + path.sep)) return route.abort();
      const type = target.endsWith('.css')?'text/css':target.endsWith('.js')?'application/javascript':target.endsWith('.ttf')?'font/ttf':'text/html';
      return route.fulfill({status:200,contentType:type,headers:{'Content-Security-Policy':policy},body:await fs.readFile(target)});
    }
    if (url.origin === 'https://atlas-chat-media.invalid') {
      if (url.pathname==='/linked-media'&&url.searchParams.get('url')?.endsWith('viewer-video.webm')) return fulfillMedia(route,'video/webm',fixtures.viewerVideoBytes);
      if (url.pathname==='/linked-media'&&url.searchParams.get('url')?.endsWith('viewer-audio.wav')) return fulfillMedia(route,'audio/wav',fixtures.viewerAudioBytes);
      if (url.pathname.endsWith('viewer-video.webm')) return fulfillMedia(route,'video/webm',fixtures.viewerVideoBytes);
      if (url.pathname.endsWith('viewer-audio.wav')) return fulfillMedia(route,'audio/wav',fixtures.viewerAudioBytes);
      if (url.pathname.endsWith('fixture-video.webm')) return fulfillMedia(route,'video/webm',fixtures.videoBytes);
      if (url.pathname.endsWith('fixture-audio.wav')) return fulfillMedia(route,'audio/wav',fixtures.audioBytes);
      if (url.pathname.endsWith('fixture-broken.mkv')) return route.fulfill({status:200,contentType:'video/x-matroska',body:'Synthetic unreadable media payload.'});
      if (url.pathname.includes('avatar')) return route.fulfill({status:200,contentType:'image/svg+xml',body:svgAvatar(url.pathname.split('/').at(-1))});
      if (url.pathname.includes('landscape')) return route.fulfill({status:200,contentType:'image/svg+xml',body:landscape});
      if (url.pathname.includes('portrait')) return route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="240" height="960"><rect width="240" height="960" fill="#476978"/><circle cx="120" cy="140" r="62" fill="#bec9c3"/><path d="M0 960V600L120 270l120 330v360Z" fill="#273f4b"/></svg>'});
      if (url.pathname.includes('tiny')) return route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="96" height="64"><rect width="96" height="64" fill="#466e68"/><circle cx="48" cy="32" r="18" fill="#bdd5cd"/></svg>'});
      return route.fulfill({status:200,contentType:'application/octet-stream',body:''});
    }
    if (url.origin === 'https://www.youtube-nocookie.com' || url.origin === 'https://player.vimeo.com') return route.fulfill({status:200,contentType:'text/html; charset=utf-8',body:'<!doctype html><meta charset="utf-8"><title>Fixture video</title><body style="margin:0;background:#081826;color:#ccddeb;display:grid;place-items:center;height:100vh;font:16px sans-serif">Lecteur de test isolé</body>'});
    return route.abort();
  };
  await page.route('**/*', routeFixture);
  await page.addInitScript(installFixtureBridge);
  await page.goto(appUrl); await page.waitForFunction(()=>!!window.AtlasChat);
  const runtime={node:process.version,browser:browser.version(),moveBefore:await page.evaluate(()=>typeof Element.prototype.moveBefore==='function')};
  check('Boot emits ready without synthetic account content',await page.evaluate(()=>__actions[0].action==='ready'&&document.querySelectorAll('.message').length===0));
  const apply = async value => { value.sequence=String(++sequence); await page.evaluate(value=>AtlasChat.applySnapshot(value),value); await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))); };
  let state=clone(fixtures.snapshot); await apply(state); await page.waitForTimeout(80);
  check('The native composer handshake enables files only for its current session and thread',await page.evaluate(()=>__actions.some(a=>a.action==='composerState'&&a.payload.threadId==='d:42:91'&&a.payload.acceptsFiles===true&&a.ownerAccountId===42&&a.sessionId==='43e20968-4137-4f23-9174-4a097cc6a873')));
  check('The fixed launcher content keeps both columns and native Atlas typography',await page.evaluate(()=>getComputedStyle(document.querySelector('.chat-layout')).gridTemplateColumns.split(' ').length===2&&getComputedStyle(document.querySelector('.sidebar-heading')).fontFamily.includes('Inter')));
  check('Messages uses the page directly without a title, subtitle or reserved heading band',await page.locator('.page-heading,#page-subtitle').count()===0&&await page.evaluate(()=>document.querySelector('.chat-layout').getBoundingClientRect().top<=24));
  check('The document remains transparent around the panel for the native Citadel backdrop',await page.evaluate(()=>[document.documentElement,document.body,document.querySelector('#chat-app')].every(node=>{const style=getComputedStyle(node);return style.backgroundColor==='rgba(0, 0, 0, 0)'&&style.backgroundImage==='none';})));
  check('No global error slot remains below the composer',await page.locator('#composer-error').count()===0);
  check('The idle French send control is icon-only with an accessible label and tooltip',await iconOnlySend('Envoyer'));
  check('Attach, Armory and send form one aligned group at the right of the composer',await composerOrder());
  check('Int64 identifiers above 2^53 remain exact and ordered',JSON.stringify(await page.locator('.message[data-message-id]').evaluateAll(nodes=>nodes.map(n=>n.dataset.messageId)))===JSON.stringify(state.messages.map(m=>m.id)));
  check('Same author and origin group consecutive messages',await page.locator('[data-message-id="9007199254740994"]').evaluate(node=>node.classList.contains('is-continuation')));
  check('Unread and date separators are visible',await page.locator('.unread-divider').count()===1&&await page.locator('.date-divider').count()===1);
  check('Actual bottom after rendering emits last rendered cursor as string',await page.evaluate(()=>__actions.some(a=>a.action==='read'&&a.payload.throughMessageId==='9007199254740999')));
  await capture('chat-fr-fixed.png');
  check('The fixed launcher content has no horizontal overflow or clipped composer',await page.evaluate(()=>document.documentElement.scrollWidth===innerWidth&&document.querySelector('#composer-box').getBoundingClientRect().bottom<=innerHeight&&document.querySelector('.conversation-sidebar').getBoundingClientRect().width>=248));

  const safety=await page.evaluate(()=>{
    const R=AtlasChatRender,node=R.renderMarkdown('**gras** *italique* ~~barré~~ ||secret||\n\n<img src=x onerror="window.__xss=1">\n\n![photo](https://outside.test/a.png) [piège](javascript:alert(1)) [ok](https://example.test)',key=>key);
    document.body.append(node);const before={strong:node.querySelectorAll('strong').length,em:node.querySelectorAll('em').length,strike:node.querySelectorAll('s').length,images:node.querySelectorAll('img').length,scripts:node.querySelectorAll('script').length,badLinks:Array.from(node.querySelectorAll('a')).some(a=>a.href.startsWith('javascript:')),spoilerHidden:node.querySelector('.spoiler').getAttribute('aria-expanded')==='false'};
    node.querySelector('.spoiler').click();before.spoilerRevealed=node.querySelector('.spoiler').getAttribute('aria-expanded')==='true';node.remove();return before;
  });
  check('Markdown formats text while raw HTML and remote image loading stay disabled',safety.strong===1&&safety.em===1&&safety.strike===1&&safety.images===0&&safety.scripts===0&&!safety.badLinks);
  check('Spoiler requires an explicit reveal action',safety.spoilerHidden&&safety.spoilerRevealed);
  check('URL policy blocks credentials, script schemes and untrusted media hosts',await page.evaluate(()=>!AtlasChatRender.safeUrl('javascript:alert(1)')&&!AtlasChatRender.safeUrl('https://user:pass@example.test')&&!AtlasChatRender.mediaUrl('https://example.test/a.png')&&!AtlasChatRender.embedUrl('https://evil.test/embed/123')));

  check('The sidebar has no contact search, All/Unread filters or global unread count',await page.locator('.conversation-sidebar input,.conversation-sidebar .search-field,.conversation-filters,[data-filter],#unread-filter-count').count()===0);
  check('Unread badges remain attached to their actual conversations',await page.locator('.conversation-unread').count()===2&&await page.locator('.conversation-row[data-key="d:42:91"] .conversation-unread').innerText()==='2'&&await page.locator('.conversation-row[data-key="group:raid"] .conversation-unread').innerText()==='5');
  check('The conversation list follows its heading without reserved search/filter space',await page.evaluate(()=>document.querySelector('#conversation-list').getBoundingClientRect().top-document.querySelector('.sidebar-heading').getBoundingClientRect().bottom<=24));
  await page.locator('#new-conversation-button').click();await page.locator('#app-dialog input[type=search]').fill('kael');
  check('Contact search still works inside the new-conversation dialog',await page.locator('.contact-picker-row').count()===1&&await page.locator('.contact-picker-row').getAttribute('aria-label')==='Kael');await page.locator('#app-dialog input[type=search]').fill('');
  await page.getByRole('button',{name:'Groupe',exact:true}).click(); await page.getByRole('textbox',{name:'Nom du groupe',exact:true}).fill('La compagnie'); await page.getByRole('button',{name:'Kael',exact:true}).click(); await page.getByRole('button',{name:'Mira',exact:true}).click(); await page.getByRole('button',{name:'Créer le groupe',exact:true}).click();
  check('Group creation sends selected account IDs and an idempotency UUID',await page.evaluate(()=>__actions.some(a=>a.action==='createThread'&&a.payload.isGroup&&a.payload.title==='La compagnie'&&a.payload.participantAccountIds.join(',')==='92,93'&&/^[a-f0-9-]{36}$/.test(a.payload.requestId))));
  check('Messages has no local status, settings, Markdown-help or archive controls',await page.locator('#dnd-button,#settings-button,#format-button,[data-filter=archived]').count()===0);
  check('New conversation uses a plus icon and all existing conversations remain accessible',await page.locator('#new-conversation-button use').getAttribute('href')==='#i-plus'&&await page.locator('.conversation-row').count()===state.state.threads.length);
  state.state.preferences.doNotDisturb=true;await apply(state);
  check('Global DND snapshots do not recreate a local control or write another preference',await page.locator('#dnd-button,#settings-button,[role=switch]').count()===0&&await page.evaluate(()=>!__actions.some(a=>a.action==='preferences')));


  await page.waitForFunction(()=>!document.querySelector('#app-dialog').open);
  await page.locator('#composer-input').fill('Brouillon conservé'); await page.waitForTimeout(310);
  const tabOrder=[];await page.locator('#composer-input').focus();for(let index=0;index<3;index++){await page.keyboard.press('Tab');tabOrder.push(await page.evaluate(()=>document.activeElement.id));}
  if(tabOrder.join(',')!=='attach-button,share-game-button,send-button')console.error('Focus diagnostics: '+JSON.stringify({tabOrder,errors,...await page.evaluate(()=>({active:document.activeElement?.tagName,composer:document.querySelector('#composer-input').value,dialogs:[...document.querySelectorAll('dialog')].map(node=>({id:node.id,open:node.open,inert:node.inert,className:node.className})),buttons:[...document.querySelectorAll('.composer-toolbar button')].map(node=>({id:node.id,disabled:node.disabled,hidden:node.hidden,tabIndex:node.tabIndex}))}))}));
  check('Keyboard focus follows the right-hand attach, Armory and send order',tabOrder.join(',')==='attach-button,share-game-button,send-button');
  check('Draft text persists through native bridge with thread/session identity',await page.evaluate(()=>__actions.some(a=>a.action==='draft'&&a.payload.body==='Brouillon conservé'&&a.payload.threadId==='d:42:91'&&a.sessionId&&a.ownerAccountId===42)));
  await page.locator('#composer-input').fill(Array.from({length:12},(_,index)=>'Ligne '+index+' '+'.'.repeat(62)).join('\n'));
  check('Long multiline input grows within its height limit without displacing the right action group',await page.locator('#composer-input').evaluate(node=>node.getBoundingClientRect().height<=148&&node.getBoundingClientRect().height>47&&document.documentElement.scrollWidth===innerWidth)&&await page.locator('#composer-counter').innerText().then(text=>text.includes('/ 1000'))&&await composerOrder());
  await page.locator('#composer-input').fill('Brouillon conservé');
  await apply(state);check('Stale model draft does not overwrite fresh local input',await page.locator('#composer-input').inputValue()==='Brouillon conservé');
  await page.locator('#composer-input').press('Shift+Enter');check('Shift+Enter remains text input',await page.locator('#composer-input').inputValue().then(value=>value.includes('\n')));
  await page.locator('#composer-input').fill('Un seul envoi');await page.locator('#composer-input').press('Enter');await page.locator('#composer-input').press('Enter');
  check('Double Enter does not create duplicate in-flight logical sends',await page.evaluate(()=>__actions.filter(a=>a.action==='send'&&a.payload.body==='Un seul envoi').length===1));
  check('An in-flight French send keeps its icon and exposes its sending label',await iconOnlySend('Envoi…'));
  const send=await page.evaluate(()=>__actions.find(a=>a.action==='send'&&a.payload.body==='Un seul envoi'));
  await page.locator('#composer-input').fill('Un texte plus récent');await page.evaluate(send=>AtlasChat.receive({type:'result',requestId:send.requestId,payload:{accepted:true}}),send);
  check('Send acknowledgement preserves text typed after submitting',await page.locator('#composer-input').inputValue()==='Un texte plus récent');

  state.messages.push(fixtures.message('9007199254741000',fixtures.lyra,'Deux aperçus dans le même message :\nhttps://example.test/guide\nhttps://www.youtube.com/watch?v=dQw4w9WgXcQ',15,{linkPreviews:[{id:'ordinary',url:'https://example.test/guide',kind:'link',title:'Un guide utile',canRemove:true,isRemoved:false},{id:'youtube',url:'https://www.youtube.com/watch?v=dQw4w9WgXcQ',kind:'video',title:'Vidéo de test',embedUrl:'https://www.youtube.com/embed/dQw4w9WgXcQ',provider:'YouTube',canRemove:true,isRemoved:false}],attachments:[{id:'audio',fileName:'Note vocale.ogg',kind:'audio',contentType:'audio/ogg',size:'15000',url:fixtures.mediaOrigin+'attachments/audio'}]}));
  await apply(state);const rich=page.locator('[data-message-id="9007199254741000"]');await rich.scrollIntoViewIfNeeded();
  check('Multiple previews stay in source order and video never has a dismiss cross',JSON.stringify(await rich.locator('.link-preview').evaluateAll(nodes=>nodes.map(n=>[n.dataset.previewId,!!n.querySelector('.preview-dismiss')])))===JSON.stringify([['ordinary',true],['youtube',false]]));
  state.isActive=false;state.isMediaActive=true;await apply(state);
  await rich.getByRole('button',{name:'Lire la vidéo',exact:true}).click();await page.waitForTimeout(100);
  check('A first YouTube click requests playback even while UI focus is inactive but Messages media is allowed',await rich.locator('iframe').evaluate(frame=>{const url=new URL(frame.src);return url.hostname==='www.youtube-nocookie.com'&&url.pathname==='/embed/dQw4w9WgXcQ'&&url.searchParams.get('autoplay')==='1'&&url.searchParams.get('origin')===location.origin&&frame.loading==='eager'&&frame.allow.includes('autoplay')&&frame.referrerPolicy==='strict-origin-when-cross-origin'&&location.origin==='https://animeclub.fr';}));
  check('YouTube has no duplicate provider heading, title, description or footer outside its iframe',await rich.locator('.is-video .link-preview-main,.is-video .link-preview-provider,.is-video .link-preview-title,.is-video .link-preview-description,.is-video .preview-source-action,.is-video .media-placeholder').count()===0);
  state.isActive=true;await apply(state);
  await rich.evaluate(node=>{window.__frame=node.querySelector('iframe');window.__audio=node.querySelector('audio');window.__body=node.querySelector('.message-content');});
  state.messages.at(-1).reactions=[{emoji:'🔥',accountIds:[42],count:1}]; await apply(state);
  check('Reaction update preserves media instances and the selectable body DOM',await rich.evaluate(node=>node.querySelector('iframe')===window.__frame&&node.querySelector('audio')===window.__audio&&node.querySelector('.message-content')===window.__body));
  await rich.locator('.preview-dismiss').click();check('Non-video cross requests shared deletion without deleting text',await page.evaluate(()=>__actions.some(a=>a.action==='dismissPreview'&&a.payload.messageId==='9007199254741000'&&a.payload.previewId==='ordinary')&&!__actions.some(a=>a.action==='deleteMessage')));
  state.messages.at(-1).linkPreviews[0].isRemoved=true;await apply(state);
  check('Persisted removal affects only the matching preview',await rich.locator('.link-preview').count()===1&&await rich.locator('iframe').count()===1&&await rich.locator('.message-content').innerText().then(text=>text.includes('https://example.test/guide')));
  state.isActive=false;state.isMediaActive=false;await apply(state);check('Leaving Messages media scope removes active embedded players',await rich.locator('iframe').count()===0);state.isActive=true;

  state=clone(fixtures.snapshot);state.sessionId='43e20968-4137-4f23-9174-4a097cc6a874';await apply(state);
  const incoming=page.locator('[data-message-id="9007199254740999"]');await incoming.hover();await incoming.getByRole('button',{name:'Répondre',exact:true}).click();
  check('Reply context holds the exact source message ID',await page.locator('#composer-context').isVisible()&&await page.locator('#composer-context-title').innerText().then(text=>text.includes('Aster')));
  await page.locator('#composer-input').fill('Une réponse liée');await page.locator('#send-button').click();
  check('Reply action sends its exact decimal parent ID',await page.evaluate(()=>__actions.some(a=>a.action==='send'&&a.payload.replyToMessageId==='9007199254740999'&&a.payload.body==='Une réponse liée')));
  const replySend=await page.evaluate(()=>__actions.find(a=>a.action==='send'&&a.payload.body==='Une réponse liée'));await page.evaluate(send=>AtlasChat.receive({type:'result',requestId:send.requestId,payload:{accepted:true}}),replySend);
  const own=page.locator('[data-message-id="9007199254740995"]');await own.scrollIntoViewIfNeeded();await own.hover();await own.getByRole('button',{name:'Actions du message',exact:true}).click();await page.getByRole('menuitem',{name:'Modifier',exact:true}).click();
  check('Entering message editing immediately disables native file intake',await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').at(-1)?.payload.acceptsFiles===false)&&await page.locator('#attach-button').isDisabled());
  check('French editing keeps an icon-only Save control with an accessible name',await iconOnlySend('Enregistrer'));
  await capture('chat-fr-edit-fixed.png');
  const blockedEditFiles=await page.evaluate(value=>{
    AtlasChat.receive({type:'dropState',active:true,sessionId:value.sessionId,ownerAccountId:value.ownerAccountId});
    const data=new DataTransfer();data.items.add(new File(['image'],'blocked.png',{type:'image/png'}));const before=__actions.length;
    document.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));document.querySelector('#composer-input').dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:data}));
    return document.querySelector('#drop-overlay').hidden&&!__actions.slice(before).some(a=>['dropFiles','pasteImage','pickFiles'].includes(a.action));
  },state);
  check('Editing refuses DOM drops and image paste and keeps the native drag overlay hidden',blockedEditFiles);
  await page.locator('#composer-input').fill('Message corrigé');await page.locator('#send-button').click();
  const edit=await page.evaluate(()=>__actions.find(a=>a.action==='editMessage'&&a.payload.body==='Message corrigé'));
  check('Editing sends the message ID and optimistic version as strings',edit?.payload.messageId==='9007199254740995'&&edit.payload.expectedVersion==='1');
  await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{accepted:true}}),edit);
  check('Successful editing restores native file intake for the draft',await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').at(-1)?.payload.acceptsFiles===true));
  check('Leaving French edit mode restores the icon-only Send label',await iconOnlySend('Envoyer'));
  await own.hover();await own.getByRole('button',{name:'Actions du message',exact:true}).click();await page.getByRole('menuitem',{name:'Modifier',exact:true}).click();await page.locator('#cancel-context-button').click();
  check('Cancelling an edit sends a fresh enable signal and restores the attachment button',await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').slice(-2).map(a=>a.payload.acceptsFiles).join(',')==='false,true')&&await page.locator('#attach-button').isEnabled());
  await own.hover();await own.getByRole('button',{name:'Actions du message',exact:true}).click();await page.getByRole('menuitem',{name:'Supprimer',exact:true}).click();await page.getByRole('button',{name:'Supprimer',exact:true}).click();
  check('Delete confirmation targets only the author-owned message',await page.evaluate(()=>__actions.some(a=>a.action==='deleteMessage'&&a.payload.messageId==='9007199254740995')));
  state.messages[2].deletedAt=fixtures.at(30);state.messages[2].body='';await apply(state);check('Server deletion removes the entire message without a tombstone or remaining controls',await own.count()===0&&await page.locator('.message-deleted').count()===0);
  const first=page.locator('[data-message-id="9007199254740993"]');await first.scrollIntoViewIfNeeded();await first.hover();await first.getByRole('button',{name:'Ajouter une réaction',exact:true}).click();await page.getByRole('button',{name:'Ajouter la réaction ⚔️',exact:true}).click();
  check('Reaction picker sends the complete Unicode emoji',await page.evaluate(()=>__actions.some(a=>a.action==='reaction'&&a.payload.emoji==='⚔️'&&a.payload.active===true)));

  const group=state.state.threads.find(thread=>thread.kind==='group');state.selectedThreadId=group.id;state.messages=[];await apply(state);await page.locator('#thread-details-button').click();
  check('Changing conversation sends a composer handshake for the new selected thread',await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').at(-1)?.payload.threadId==='group:raid'&&__actions.filter(a=>a.action==='composerState').at(-1)?.payload.acceptsFiles===true));
  check('Group details show actual members and management fields',await page.getByRole('textbox',{name:'Nom du groupe',exact:true}).inputValue()==='Les aventuriers du soir'&&await page.locator('.details-member').count()===4);
  await page.locator('.details-member').filter({hasText:'Kael'}).getByRole('button',{name:'Actions de la conversation',exact:true}).click();await page.getByRole('menuitem',{name:'Nommer administrateur',exact:true}).click();
  check('Group role menu remains interactive inside the modal top layer',await page.evaluate(()=>__actions.some(a=>a.action==='member'&&a.payload.threadId==='group:raid'&&a.payload.accountId===92&&a.payload.action==='role'&&a.payload.role==='admin')));
  await page.getByRole('textbox',{name:'Nom du groupe',exact:true}).fill('Nouveau nom');await page.getByRole('button',{name:'Enregistrer',exact:true}).click();
  check('Group title update includes its original version',await page.evaluate(()=>__actions.some(a=>a.action==='threadUpdate'&&a.payload.title==='Nouveau nom'&&a.payload.expectedVersion==='3')));

  state.selectedThreadId='d:42:91';state.messages=clone(fixtures.snapshot.messages);state.sessionId='43e20968-4137-4f23-9174-4a097cc6a875';state.draft={body:'',attachments:[{id:'local-image',fileName:'Capture.png',contentType:'image/png',size:'4000000',offset:'1200000',status:'uploading',isComplete:false,previewUrl:fixtures.mediaOrigin+'attachments/fixture-landscape'}]};await apply(state);
  check('An uploading image has only its preview and compact progress without name, size or Ready',await page.locator('.queued-file.is-image .queued-preview img').count()===1&&await page.locator('.queued-progress > span').evaluate(node=>Math.abs(parseFloat(node.style.width)-30)<1)&&await page.locator('.queued-file').innerText().then(text=>text.trim()===''));
  await page.locator('.queued-file-remove').click();check('Removing an in-progress file removes its draft reference by local ID',await page.evaluate(()=>__actions.some(a=>a.action==='removeAttachment'&&a.payload.uploadId==='local-image')));
  state.isAvailable=false;await apply(state);check('A file can be queued while offline and still uploading',await page.locator('#send-button').isEnabled());await page.locator('#send-button').click();
  check('Queued attachment send carries opaque local IDs and no binary data',await page.evaluate(()=>__actions.some(a=>a.action==='send'&&a.payload.attachmentIds?.[0]==='local-image'&&!('data' in a.payload)&&!('path' in a.payload))));
  state.draft={};state.isAvailable=true;await apply(state);
  const dropResult=await page.evaluate(()=>{
    const data=new DataTransfer();data.items.add(new File(['tiny'],'one.png',{type:'image/png'}));data.items.add(new File(['document'],'two.pdf',{type:'application/pdf'}));
    const before=__actions.length;document.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));const action=__actions.slice(before).find(a=>a.action==='dropFiles');
    const archive=new DataTransfer();archive.items.add(new File(['zip'],'archive.zip',{type:'application/zip'}));const count=__actions.filter(a=>a.action==='dropFiles').length;document.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:archive}));
    return{action,archiveRejected:__actions.filter(a=>a.action==='dropFiles').length===count};
  });
  check('Multiple dropped files use AdditionalObjects without paths in JSON',dropResult.action?.additionalObjectCount===2&&Object.keys(dropResult.action.payload).join(',')==='threadId');
  check('Archive files are rejected before crossing the native bridge',dropResult.archiveRejected);
  const pasteResult=await page.evaluate(()=>{
    const data=new DataTransfer();data.items.add(new File(['image'],'capture.png',{type:'image/png'}));const image=new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:data});document.querySelector('#composer-input').dispatchEvent(image);
    const textData=new DataTransfer();textData.setData('text/plain','Texte collé');const text=new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:textData});document.querySelector('#composer-input').dispatchEvent(text);
    return{imagePrevented:image.defaultPrevented,textPrevented:text.defaultPrevented,native:__actions.some(a=>a.action==='pasteImage')};
  });
  check('Image paste delegates to native clipboard while text paste stays native browser input',pasteResult.imagePrevented&&!pasteResult.textPrevented&&pasteResult.native);
  // The inline Armory contract is exercised separately below.
  state.pending=[{clientMessageId:'10000000-0000-4000-8000-000000000001',threadId:state.selectedThreadId,body:'Déjà soumis',status:'queued',canCancel:false,createdAt:fixtures.at(31),attachments:[]},{clientMessageId:'10000000-0000-4000-8000-000000000002',threadId:state.selectedThreadId,body:'Encore en attente',status:'uploading',canCancel:true,progress:0.42,createdAt:fixtures.at(32),attachments:[]}];await apply(state);
  const submitted=page.locator('[data-client-message-id="10000000-0000-4000-8000-000000000001"]'),unsent=page.locator('[data-client-message-id="10000000-0000-4000-8000-000000000002"]');
  check('A send already submitted cannot display the cancel action after reconnection',await submitted.getByRole('button',{name:'Annuler l’envoi en attente',exact:true}).count()===0&&await unsent.getByRole('button',{name:'Annuler l’envoi en attente',exact:true}).count()===1);
  check('Queued media messages show native transfer progress and creation time',await unsent.locator('.message-state').innerText().then(text=>text.includes('42 %'))&&await unsent.locator('.message-time').innerText().then(text=>text.length>0));state.pending=[];

  state=clone(fixtures.snapshot);state.sessionId='followup-media-previews';state.draft={body:'Trois aperçus avant l’envoi.',attachments:clone(fixtures.mediaDraft)};await apply(state);
  await page.waitForFunction(()=>[...document.querySelectorAll('.queued-preview video,.queued-preview audio')].every(media=>media.readyState>=1));
  check('Image, video and audio use their actual preview elements before sending',await page.locator('.queued-file.is-image .queued-preview img').count()===1&&await page.locator('.queued-file.is-video .queued-preview video').count()===1&&await page.locator('.queued-file.is-audio .queued-preview audio').count()===1);
  check('Ready media previews show only player times without filename, size, Ready text or metadata rows',await page.locator('.queued-file').evaluateAll(nodes=>nodes.every(node=>node.innerText.replace(/\d+:\d{2}|--:--/g,'').trim()===''&&!node.querySelector('.queued-file-name,.queued-file-status'))));
  check('Audio and video metadata decode from isolated local fixture bytes',await page.locator('.queued-preview video,.queued-preview audio').evaluateAll(nodes=>nodes.every(media=>media.readyState>=1&&!media.error&&media.preload!=='none')&&nodes.find(media=>media.tagName==='VIDEO').videoWidth===160&&nodes.find(media=>media.tagName==='AUDIO').duration===1));
  await page.evaluate(()=>window.__queuedPlayers=[...document.querySelectorAll('.queued-preview video,.queued-preview audio')]);state.draft.attachments=state.draft.attachments.map(upload=>({...upload,isComplete:false,status:'uploading',offset:String(Math.floor(Number(upload.size)/2))}));await apply(state);
  check('Upload progress updates keep the existing audio and video players and their decoded metadata',await page.evaluate(()=>[...document.querySelectorAll('.queued-preview video,.queued-preview audio')].every((media,index)=>media===__queuedPlayers[index]&&media.readyState>=1)));
  state.draft.attachments=clone(fixtures.mediaDraft);await apply(state);
  check('Preview controls retain a removal action for each exact local attachment',await page.locator('.queued-file-remove').count()===3&&await page.locator('.queued-file-remove').evaluateAll(nodes=>nodes.every(node=>node.getAttribute('aria-label'))));
  check('Multiple media previews keep the composer inside the fixed launcher',await page.evaluate(()=>document.documentElement.scrollWidth===innerWidth&&document.querySelector('#composer-box').getBoundingClientRect().bottom<=innerHeight)&&await composerOrder());
  await capture('chat-fr-queued-media-fixed.png');
  await page.locator('.queued-file.is-video .queued-file-remove').click();
  check('Removing a video preview targets only that upload and never deletes a sent message',await page.evaluate(()=>{const last=__actions.at(-1);return last.action==='removeAttachment'&&last.payload.threadId==='d:42:91'&&last.payload.uploadId==='preview-video'&&!('messageId' in last.payload);}));
  const refusedRemove=await page.evaluate(()=>__actions.at(-1));await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,error:'chat-unavailable'}),refusedRemove);
  check('A refused preview removal reports the connection error on that upload alone',await page.locator('.queued-file.is-video .queued-file-error').innerText().then(text=>text.includes('indisponible'))&&await page.locator('.queued-file-error:visible').count()===1&&!await page.locator('#toast').isVisible());
  await page.locator('.queued-file.is-video .queued-file-remove').click();const acceptedRemove=await page.evaluate(()=>__actions.at(-1));await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{removed:true}}),acceptedRemove);
  check('A successful later preview action clears only its earlier local feedback',await page.locator('.queued-file-error:visible').count()===0);
  state.draft.attachments=[{...clone(fixtures.mediaDraft[1]),status:'failed',isComplete:false,errorCode:'chat-file-type-not-supported'}];await apply(state);
  check('A failed attachment explains the error on its own preview and offers an inline retry',await page.locator('.queued-file.is-failed .queued-file-error').isVisible()&&await page.locator('.queued-file.is-failed .queued-file-retry').count()===1&&await page.locator('#composer-error').count()===0);
  await capture('chat-fr-queued-error-fixed.png');
  state.draft={};state.messages=[fixtures.message('4999',fixtures.lyra,'Un fichier reste accessible même si le lecteur ne peut pas le décoder.',1,{attachments:[{id:'fixture-broken.mkv',fileName:'Vidéo non décodable.mkv',kind:'video',contentType:'video/x-matroska',size:'38',url:fixtures.mediaOrigin+'attachments/fixture-broken.mkv'}]})];await apply(state);
  await page.locator('.media-playback-unavailable').waitFor({state:'visible'});
  check('An undecodable received video shows local feedback without a download action or composer error',await page.locator('.media-playback-unavailable').innerText().then(text=>text.trim().length>0&&!text.includes('mediaPlaybackUnavailable'))&&await page.locator('[data-attachment-id="fixture-broken.mkv"] button').count()===0&&await page.locator('#composer-error').count()===0);
  await exerciseDraftMediaViewer(page,apply,capture);
  await exerciseMediaMenusAndLinks(page,apply,capture);

  state=clone(fixtures.snapshot);state.sessionId='followup-pending-errors';state.messages=[fixtures.message('5001',fixtures.lyra,'Le message de mon ami reste intact.',1)];
  const failedPending=fixtures.pending(1,{body:'Cet envoi a été refusé.',status:'failed',canCancel:true,errorCode:'chat-forbidden'});
  const uncertainPending=fixtures.pending(2,{body:'Réponse perdue après soumission.',status:'failed',canCancel:false,errorCode:'chat-unavailable'});
  const waitingPending=fixtures.pending(3,{body:'Cet envoi attend la reconnexion.'});
  state.pending=[failedPending,uncertainPending,waitingPending];await apply(state);
  const failedRow=page.locator('[data-client-message-id="'+failedPending.clientMessageId+'"]'), uncertainRow=page.locator('[data-client-message-id="'+uncertainPending.clientMessageId+'"]');
  check('Each rejected or uncertain send has its own error and leaves unrelated messages untouched',await failedRow.locator('.message-state.is-failed').innerText().then(text=>text.includes('pouvez plus écrire'))&&await uncertainRow.locator('.message-state.is-failed').innerText().then(text=>text.includes('indisponible'))&&await page.locator('[data-message-id="5001"] .message-state.is-failed').count()===0&&await page.locator('#composer-error').count()===0);
  check('A certainly rejected pending send exposes removal, while an uncertain submission stays non-cancellable',await failedRow.locator('.message-state button').count()===2&&await uncertainRow.locator('.message-state button').count()===1);
  await capture('chat-fr-pending-errors-fixed.png');
  const beforeCancel=await page.evaluate(()=>__actions.length);await failedRow.locator('.message-state button').last().click();
  check('Removing a failed send passes only its exact client ID and cannot delete the friend message',await page.evaluate(({before,id})=>{const actions=__actions.slice(before);return actions.length===1&&actions[0].action==='cancelSend'&&Object.keys(actions[0].payload).join(',')==='clientMessageId'&&actions[0].payload.clientMessageId===id;},{before:beforeCancel,id:failedPending.clientMessageId}));
  state.pending=state.pending.filter(item=>item.clientMessageId!==failedPending.clientMessageId);await apply(state);
  check('Removing the failed local send preserves both the uncertain submission and the other account message',await failedRow.count()===0&&await uncertainRow.count()===1&&await page.locator('[data-message-id="5001"]').count()===1);
  const deletableUncertain=fixtures.pending(5,{body:'Envoi incertain avec suppression durable.',status:'failed',canCancel:false,canDelete:true,errorCode:'chat-unavailable'});state.pending.push(deletableUncertain);await apply(state);
  const deletableRow=page.locator('[data-client-message-id="'+deletableUncertain.clientMessageId+'"]');await deletableRow.getByRole('button',{name:'Supprimer',exact:true}).click();
  check('A native-authorized deletion of an uncertain failed send emits deleteFailedSend with only its client ID',await page.evaluate(id=>{const last=__actions.at(-1);return last.action==='deleteFailedSend'&&Object.keys(last.payload).join(',')==='clientMessageId'&&last.payload.clientMessageId===id;},deletableUncertain.clientMessageId));
  Object.assign(deletableUncertain,{status:'deleting',deleteRequested:true,canDelete:false});await apply(state);
  check('A durable deletion in progress shows its state and exposes neither retry, cancel nor another delete',await deletableRow.locator('.message-state').innerText().then(text=>text.includes('Suppression'))&&await deletableRow.locator('.message-state button').count()===0);
  const queuedDeletion=fixtures.pending(6,{body:'Une réponse perdue garde ce message en attente.',status:'queued',canCancel:false,canDelete:true,errorCode:'chat-unavailable'});state.pending.push(queuedDeletion);await apply(state);
  const queuedDeletionRow=page.locator('[data-client-message-id="'+queuedDeletion.clientMessageId+'"]');
  check('A queued send with a native deletion capability shows its error and an actual Delete action',await queuedDeletionRow.locator('.message-state.is-failed').innerText().then(text=>text.includes('indisponible'))&&await queuedDeletionRow.getByRole('button',{name:'Supprimer',exact:true}).count()===1);
  await queuedDeletionRow.getByRole('button',{name:'Supprimer',exact:true}).click();
  check('Deleting an errored queued send uses the dedicated native deletion operation',await page.evaluate(id=>{const action=__actions.at(-1);return action.action==='deleteFailedSend'&&action.payload.clientMessageId===id;},queuedDeletion.clientMessageId));
  Object.assign(queuedDeletion,{status:'deleting',deleteRequested:true,canDelete:false,errorCode:'chat-forbidden'});const postsBeforeDeletionFailure=await page.evaluate(()=>__actions.filter(action=>['send','retrySend'].includes(action.action)).length);await apply(state);
  check('A refused durable deletion keeps its state and inline reason without exposing or issuing another send',await queuedDeletionRow.locator('.message-state.is-failed').innerText().then(text=>text.includes('Suppression')&&text.includes('pouvez plus écrire'))&&await queuedDeletionRow.locator('.message-state button').count()===0&&await page.evaluate(before=>__actions.filter(action=>['send','retrySend'].includes(action.action)).length===before,postsBeforeDeletionFailure));
  await capture('chat-fr-deletion-errors-fixed.png');
  const pendingColors=[];
  for(const presence of ['online','away','dnd','offline']){
    state.state.self.presence=presence;state.state.self.avatarUrl=fixtures.mediaOrigin+'avatars/42?revision='+presence;await apply(state);
    const current=await uncertainRow.locator('.message-avatar').evaluate(node=>({presence:node.querySelector('.presence-dot')?.dataset.presence,color:getComputedStyle(node.querySelector('.presence-dot')).backgroundColor,avatar:node.querySelector('img')?.src}));
    check('An unchanged pending message refreshes the current self '+presence+' avatar and status',current.presence===presence&&current.avatar.endsWith('revision='+presence));pendingColors.push(current.color);
  }
  check('Pending self indicators use distinct colors for all four global statuses',new Set(pendingColors).size===4);
  state.messages.push(fixtures.message('5002',fixtures.self,uncertainPending.body,42,{clientMessageId:uncertainPending.clientMessageId}));await apply(state);
  check('A server confirmation replaces the uncertain local send without a duplicate or failed state',await page.locator('[data-message-id="5002"]').count()===1&&await uncertainRow.count()===0&&await page.locator('[data-message-id="5002"] .message-state.is-failed').count()===0);
  state.selectedThreadId='d:42:92';state.messages=[clone(fixtures.snapshot.state.threads.find(thread=>thread.id==='d:42:92').lastMessage)];state.draft={};await apply(state);
  check('Changing conversation removes previous pending errors without carrying a composer or toast error',await page.locator('.message-state.is-failed,#composer-error').count()===0&&!await page.locator('#toast').isVisible()&&await page.locator('#message-list').innerText().then(text=>!text.includes('refusé')&&!text.includes('Réponse perdue')));

  state=clone(fixtures.snapshot);state.sessionId='followup-before-outbox';await apply(state);await page.locator('#composer-input').fill('Texte préservé avant la file native.');await page.locator('#send-button').click();
  const localSend=await page.evaluate(()=>__actions.filter(action=>action.action==='send').at(-1));await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,error:'chat-forbidden'}),localSend);
  const localFailed=page.locator('[data-client-message-id="'+localSend.payload.clientMessageId+'"]');
  check('A rejection before native persistence remains attached to that exact local send and keeps the draft',await localFailed.locator('.message-state.is-failed').isVisible()&&await page.locator('#composer-input').inputValue()==='Texte préservé avant la file native.'&&await page.locator('#composer-error').count()===0);
  const retriesBefore=await page.evaluate(()=>__actions.filter(action=>action.action==='send').length);
  await localFailed.locator('.message-state button').first().evaluate(button=>{button.click();button.click();});
  const localRetry=await page.evaluate(()=>__actions.filter(action=>action.action==='send').at(-1));
  check('Retrying a pre-outbox failure keeps the exact payload and client ID and blocks a double click',JSON.stringify(localRetry.payload)===JSON.stringify(localSend.payload)&&await page.evaluate(before=>__actions.filter(action=>action.action==='send').length===before+1,retriesBefore));
  await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,error:'chat-forbidden'}),localRetry);
  const deleteBefore=await page.evaluate(()=>__actions.length);await localFailed.locator('.message-state button').last().click();
  check('Deleting an unpersisted failed send only clears its local entry and never calls native cancel or server delete',await localFailed.count()===0&&await page.evaluate(before=>!__actions.slice(before).some(action=>['cancelSend','deleteMessage'].includes(action.action)),deleteBefore)&&await page.locator('#composer-input').inputValue()==='Texte préservé avant la file native.');
  await page.locator('#composer-input').fill('Un refus tardif appartient à Lyra.');await page.locator('#send-button').click();const delayedSend=await page.evaluate(()=>__actions.filter(action=>action.action==='send').at(-1));
  state.selectedThreadId='d:42:92';state.messages=[clone(fixtures.snapshot.state.threads.find(thread=>thread.id==='d:42:92').lastMessage)];state.draft={};await apply(state);await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,error:'chat-forbidden'}),delayedSend);
  check('A late send failure cannot attach an error or toast to the newly selected conversation',await page.locator('.message-state.is-failed,#composer-error').count()===0&&!await page.locator('#toast').isVisible()&&await page.locator('#message-list').innerText().then(text=>!text.includes('refus tardif')));

  state=clone(fixtures.snapshot);state.sessionId='followup-message-error';await apply(state);const editErrorOwn=page.locator('.message[data-message-id="9007199254740995"]');await editErrorOwn.scrollIntoViewIfNeeded();await editErrorOwn.hover();await editErrorOwn.getByRole('button',{name:'Actions du message',exact:true}).click();await page.getByRole('menuitem',{name:'Modifier',exact:true}).click();await page.locator('#composer-input').fill('Modification refusée.');await page.locator('#send-button').click();
  const refusedEdit=await page.evaluate(()=>__actions.filter(action=>action.action==='editMessage').at(-1));await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,error:'chat-forbidden'}),refusedEdit);
  check('A rejected edit displays feedback only on the original message and preserves its text',await editErrorOwn.locator('.message-state.is-failed').isVisible()&&await editErrorOwn.innerText().then(text=>text.includes('Avec plaisir. Je peux venir avec mon prêtre'))&&await page.locator('.message-state.is-failed').count()===1&&await page.locator('#composer-error').count()===0);
  state.selectedThreadId='d:42:92';state.messages=[clone(fixtures.snapshot.state.threads.find(thread=>thread.id==='d:42:92').lastMessage)];state.draft={};await apply(state);
  check('A message action failure is cleared when changing conversation',await page.locator('.message-state.is-failed,#composer-error').count()===0&&!await page.locator('#toast').isVisible());

  state=clone(fixtures.snapshot);state.sessionId='polish-presence';await apply(state);await page.locator('#toast').waitFor({state:'hidden'});
  const presenceColors=[];
  for (const [presence,label] of [['online','En ligne'],['away','Absent'],['dnd','Ne pas déranger'],['offline','Hors ligne']]) {
    state.state.contacts[0].presence=presence;state.state.contacts[0].avatarUrl=fixtures.mediaOrigin+'avatars/91?revision='+presence;await apply(state);
    const views=await page.evaluate(()=>['#thread-avatar','.conversation-row[data-key="d:42:91"]','.message[data-message-id="9007199254740993"] .message-avatar'].map(selector=>{const host=document.querySelector(selector),dot=host?.querySelector('.presence-dot');return{status:dot?.dataset.presence,avatar:host?.querySelector('img')?.src,color:dot&&getComputedStyle(dot).backgroundColor};}));
    check('Current '+presence+' presence and avatar refresh in header, conversation and old message',views.every(view=>view.status===presence&&view.avatar.endsWith('revision='+presence))&&await page.locator('#thread-presence').innerText()===label);
    presenceColors.push(views[0].color);
  }
  check('The four global presence values have distinct visual indicators',new Set(presenceColors).size===4);
  state.state.contacts[0].presence='online';
  state.messages=[fixtures.message('2001',fixtures.self,'',1,{attachments:[{id:'portrait',fileName:'Portrait de test.png',kind:'image',contentType:'image/png',size:'4200',url:fixtures.mediaOrigin+'attachments/portrait'}]}),fixtures.message('2002',fixtures.lyra,'Une petite capture et le document.',3,{attachments:[{id:'tiny',fileName:'Miniature.png',kind:'image',contentType:'image/png',size:'500',url:fixtures.mediaOrigin+'attachments/tiny'},{id:'document',fileName:'Plan du donjon.pdf',kind:'file',contentType:'application/pdf',size:'64000',url:fixtures.mediaOrigin+'attachments/document'}]})];
  state.state.threads[0].lastMessage=state.messages.at(-1);state.state.threads[0].pinnedMessages=[];state.state.threads[0].unreadCount=0;await apply(state);await page.locator('.attachment-image img').evaluateAll(images=>Promise.all(images.map(img=>img.decode())));
  const imageMetrics=await page.locator('.attachment-image').evaluateAll(nodes=>nodes.map(node=>{const box=node.getBoundingClientRect(),img=node.querySelector('img'),imageBox=img.getBoundingClientRect(),css=getComputedStyle(node);return{width:box.width,height:box.height,imgWidth:imageBox.width,imgHeight:imageBox.height,naturalWidth:img.naturalWidth,naturalHeight:img.naturalHeight,border:css.borderTopWidth,background:css.backgroundColor,footer:!!node.querySelector('.attachment-footer'),text:node.innerText};}));
  check('Portrait image occupies exactly its 87.5 by 350 preview without a frame or footer',Math.abs(imageMetrics[0].width-87.5)<1&&imageMetrics[0].height===350&&imageMetrics[0].width===imageMetrics[0].imgWidth&&imageMetrics[0].height===imageMetrics[0].imgHeight&&imageMetrics[0].border==='0px'&&imageMetrics[0].background==='rgba(0, 0, 0, 0)'&&!imageMetrics[0].footer&&!imageMetrics[0].text);
  check('Small images keep their natural dimensions and document download stays available',imageMetrics[1].width===96&&imageMetrics[1].height===64&&await page.locator('[data-attachment-id="document"]').getByRole('button',{name:'Télécharger',exact:true}).count()===1);
  await page.locator('#timeline').evaluate(node=>node.scrollTop=0);await capture('chat-fr-portrait-fixed.png');
  const downloadsBefore=await page.evaluate(()=>__actions.filter(action=>action.action==='downloadAttachment').length);await page.locator('.attachment-image').first().click();
  const imageDialog=page.locator('#image-dialog');await imageDialog.waitFor({state:'visible'});
  check('The image opens in a large lightbox with no buttons, footer or surrounding frame',await imageDialog.locator('button,footer,.dialog-footer,.dialog-heading').count()===0&&await imageDialog.evaluate(node=>{const img=node.querySelector('#image-dialog-image'),box=img.getBoundingClientRect(),css=getComputedStyle(node);return box.height>500&&Math.abs(box.width/box.height-0.25)<0.01&&css.borderTopWidth==='0px';}));
  const lightboxFiles=await page.evaluate(value=>{
    const before=__actions.length,data=new DataTransfer();data.items.add(new File(['fixture'],'lightbox.png',{type:'image/png'}));document.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));document.querySelector('#composer-input').dispatchEvent(new ClipboardEvent('paste',{bubbles:true,cancelable:true,clipboardData:data}));AtlasChat.receive({type:'dropState',active:true,sessionId:value.sessionId,ownerAccountId:value.ownerAccountId});
    return{actions:__actions.slice(before).filter(action=>['dropFiles','pasteImage'].includes(action.action)),acceptsFiles:__actions.filter(action=>action.action==='composerState').at(-1)?.payload.acceptsFiles,overlay:!document.querySelector('#drop-overlay').hidden};
  },state);
  check('The image lightbox closes native file admission and rejects hidden drops or image paste',lightboxFiles.actions.length===0&&lightboxFiles.acceptsFiles===false&&!lightboxFiles.overlay);
  await capture('chat-fr-image-lightbox-fixed.png');
  await page.locator('#image-dialog-image').click();check('Clicking the enlarged image leaves it open and never starts a download',await imageDialog.isVisible()&&await page.evaluate(before=>__actions.filter(action=>action.action==='downloadAttachment').length===before,downloadsBefore));
  await page.locator('#image-dialog-image').dblclick();
  check('Repeated image clicks do not create blue text selection or enable image dragging',await page.locator('#image-dialog-image').evaluate(image=>getComputedStyle(image).userSelect==='none'&&!image.draggable&&getSelection().toString()===''&&!image.dispatchEvent(new Event('selectstart',{bubbles:true,cancelable:true}))&&!image.dispatchEvent(new DragEvent('dragstart',{bubbles:true,cancelable:true})))&&await imageDialog.isVisible());
  await page.keyboard.press('Escape');await imageDialog.waitFor({state:'hidden'});check('Escape closes the lightbox and restores focus to its original image',!await imageDialog.isVisible()&&await page.locator('.attachment-image').first().evaluate(node=>node===document.activeElement));
  check('Closing the lightbox publishes fresh native file admission for the same conversation',await page.evaluate(()=>__actions.filter(action=>action.action==='composerState').at(-1)?.payload.acceptsFiles===true));
  await page.locator('.attachment-image').first().click();await imageDialog.click({position:{x:5,y:5}});await imageDialog.waitFor({state:'hidden'});check('Clicking the lightbox background closes it and restores image focus',!await imageDialog.isVisible()&&await page.locator('.attachment-image').first().evaluate(node=>node===document.activeElement));

  const removed=fixtures.message('3003',fixtures.lyra,'Texte effacé',5,{deletedAt:fixtures.at(6)});
  state.messages=[fixtures.message('3001',fixtures.lyra,'Message conservé',1),fixtures.message('3002',fixtures.self,'La réponse reste lisible.',3,{replyTo:{messageId:'3003',senderUsername:'Lyra',body:'Texte effacé',isDeleted:true}}),removed];state.state.threads[0].lastMessage=removed;state.state.threads[0].pinnedMessages=[removed];state.state.threads[0].unreadCount=2;state.state.threads[0].lastReadMessageId='3001';state.sessionId='polish-deleted';await page.evaluate(()=>window.__actions=[]);await apply(state);
  check('Deleted messages leave no article, reply preview, pin strip or deleted placeholder',await page.locator('.message').count()===2&&await page.locator('.message-reply,.message-deleted').count()===0&&!await page.locator('#pinned-strip').isVisible()&&!await page.locator('.conversation-row').first().innerText().then(text=>text.includes('Texte effacé')));
  check('Reaching visible bottom acknowledges a deleted server tail using its exact ID',await page.evaluate(()=>__actions.some(a=>a.action==='read'&&a.payload.throughMessageId==='3003')));
  state.messages=state.messages.map(message=>({...message,deletedAt:fixtures.at(8)}));state.messages.push(fixtures.message('3004',fixtures.lyra,'',9,{deletedAt:fixtures.at(10)}));state.pending=[{clientMessageId:state.messages[0].clientMessageId,threadId:state.selectedThreadId,body:'Ne doit pas réapparaître',status:'queued',createdAt:fixtures.at(1),attachments:[]}];await apply(state);
  check('An entirely deleted history hides all messages and separators including matching pending sends',await page.locator('.message,.date-divider,.unread-divider').count()===0&&await page.locator('#no-messages').isVisible());
  check('An entirely deleted tail can still advance the receipt cursor',await page.evaluate(()=>__actions.some(a=>a.action==='read'&&a.payload.throughMessageId==='3004')));

  state=clone(fixtures.snapshot);state.sessionId='polish-armory';state.ownCharacters={status:'idle',characters:[],error:null};state.draft.attachments=[{id:'keep-upload',fileName:'Capture.png',contentType:'image/png',size:'500',offset:'500',status:'ready',isComplete:true,previewUrl:fixtures.mediaOrigin+'attachments/tiny'}];await apply(state);
  let prompts=0;page.on('dialog',async dialog=>{prompts++;await dialog.dismiss();});
  await page.locator('#composer-input').fill('Voici mon personnage.');await page.locator('#share-game-button').click();
  const rosterRequest=await page.evaluate(()=>__actions.filter(a=>a.action==='requestOwnCharacters').at(-1));
  check('Sharing opens an inline Armory picker and asks native code for owned characters',!!rosterRequest&&await page.locator('#armory-picker').isVisible()&&await page.locator('dialog[open]').count()===0&&await page.locator('#armory-picker input').count()===0);
  state.ownCharacters={status:'loading',characters:[],error:null};await apply(state);check('Owned-character loading is visible within the composer',await page.locator('#armory-picker-status').innerText().then(text=>text.includes('Chargement')));
  state.ownCharacters={status:'ready',characters:[{guid:'4294967295',name:'Asterion',level:80,classId:6,raceId:1,gender:0,realmName:'Arthas'},{guid:'942',name:'Feuillebrume',level:72,classId:11,raceId:4,gender:1,realmName:'Arthas'}],error:null};await apply(state);await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{status:'ready'}}),rosterRequest);
  check('The Armory chooser contains only the supplied own roster with exact string GUIDs',await page.locator('.armory-character').count()===2&&JSON.stringify(await page.locator('.armory-character').evaluateAll(nodes=>nodes.map(node=>node.dataset.characterGuid)))==='["4294967295","942"]'&&await page.locator('#armory-character-list').innerText().then(text=>text.includes('Niveau 80')&&text.includes('Chevalier de la mort')&&!text.includes('Lyra')));
  await capture('chat-fr-armory-fixed.png');
  check('The inline Armory picker keeps the composer visible at the fixed launcher size',await page.evaluate(()=>document.documentElement.scrollWidth===innerWidth&&document.querySelector('#composer-input').getBoundingClientRect().bottom<=innerHeight&&document.querySelector('#armory-picker').getBoundingClientRect().top>0));
  check('Opening Armory preserves the right-aligned composer action group',await composerOrder());
  await page.locator('.armory-character[data-character-guid="4294967295"]').click();const characterSelection=await page.evaluate(()=>__actions.filter(a=>a.action==='selectOwnCharacter').at(-1));
  check('Character selection sends only the current thread and exact GUID to native validation',characterSelection.payload.threadId==='d:42:91'&&characterSelection.payload.characterGuid==='4294967295'&&Object.keys(characterSelection.payload).length===2);
  await page.locator('#composer-input').fill('Texte complété pendant la sélection.');
  const ownCard=clone(fixtures.characterCard);state.draft.body='Voici mon personnage.';state.draft.card=ownCard;await apply(state);await page.evaluate(({request,card})=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{card}}),{request:characterSelection,card:ownCard});await page.waitForTimeout(310);
  check('Canonical Armory result preserves fresh text and existing upload while closing the picker',await page.locator('#composer-input').inputValue()==='Texte complété pendant la sélection.'&&await page.locator('#composer-context-body').innerText()==='Asterion'&&await page.locator('.queued-file').count()===1&&!await page.locator('#armory-picker').isVisible());
  await page.locator('#send-button').click();const armorySend=await page.evaluate(()=>__actions.filter(a=>a.action==='send'&&a.payload.card?.title==='Asterion').at(-1));
  check('Sending an Armory includes canonical ownership and retains the attachment reference',armorySend?.payload.card.fields.ownerAccountId==='42'&&armorySend.payload.card.fields.characterGuid==='4294967295'&&armorySend.payload.attachmentIds[0]==='keep-upload'&&armorySend.payload.body==='Texte complété pendant la sélection.');
  await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{accepted:true}}),armorySend);state.draft={};state.messages=[fixtures.message('4001',fixtures.self,'',1,{card:ownCard})];state.state.threads[0].lastMessage=state.messages[0];state.pending=[fixtures.pending(4,{body:'Le même personnage attend l’envoi.',card:ownCard})];await apply(state);
  check('The final and pending character cards share a clear name, level and actual class identity',await page.locator('.game-card.character-card').count()===2&&await page.locator('.character-card-identity').evaluateAll(nodes=>nodes.every(node=>node.textContent.includes('Asterion')))&&await page.locator('.character-card-meta').evaluateAll(nodes=>nodes.every(node=>node.textContent.includes('80')&&node.textContent.includes('Chevalier de la mort'))));
  check('Character cards expose one Armory action and keep raw account identifiers out of the visible card',await page.locator('.character-card-action').count()===2&&await page.locator('.character-card').evaluateAll(nodes=>nodes.every(node=>!node.innerText.includes('4294967295')&&!node.innerText.includes('ownerAccountId'))));
  await capture('chat-fr-character-cards-fixed.png');await page.locator('[data-message-id="4001"] .character-card-action').click();
  check('A received Armory opens the exact owner and character without rounding the GUID',await page.evaluate(()=>__actions.some(a=>a.action==='openCharacterArmory'&&a.payload.ownerAccountId===42&&a.payload.characterGuid==='4294967295')));
  await page.locator('#share-game-button').click();const emptyRequest=await page.evaluate(()=>__actions.filter(a=>a.action==='requestOwnCharacters').at(-1));state.ownCharacters={status:'ready',characters:[],error:null};await apply(state);await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{status:'ready'}}),emptyRequest);
  check('An empty account receives inline feedback and no invented character',await page.locator('.armory-character').count()===0&&await page.locator('#armory-picker-status').innerText().then(text=>text.includes('pas encore de personnage')));
  state.ownCharacters={status:'error',characters:[],error:'chat-armory-unavailable'};await apply(state);check('Roster failures show an inline retry action',await page.locator('#armory-picker-status button').innerText()==='Réessayer');
  await page.locator('#armory-picker-status button').click();const oldRosterRequest=await page.evaluate(()=>__actions.filter(a=>a.action==='requestOwnCharacters').at(-1));state.ownCharacters={status:'ready',characters:[{guid:'942',name:'Feuillebrume',level:72,classId:11}],error:null};await apply(state);await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{status:'ready'}}),oldRosterRequest);
  await apply({...state,sessionId:'other-armory-session',ownerAccountId:84,selectedThreadId:null,state:{...state.state,self:{accountId:84,username:'Second'},threads:[],contacts:[]},ownCharacters:{status:'idle',characters:[]},messages:[],draft:{}});await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{status:'ready',characters:[{guid:'942',name:'Feuillebrume'}]}}),oldRosterRequest);
  check('Account changes purge hidden roster DOM and ignore stale Armory results',await page.locator('.armory-character').count()===0&&!await page.locator('#armory-picker').isVisible()&&!await page.locator('body').innerText().then(text=>text.includes('Feuillebrume'))&&prompts===0);

  state=clone(fixtures.snapshot);state.sessionId='polish-native-drop';state.isActive=false;await apply(state);await page.evaluate(value=>AtlasChat.receive({type:'dropState',active:true,sessionId:value.sessionId,ownerAccountId:value.ownerAccountId}),state);
  check('Native Explorer drag can display its overlay while the launcher window is inactive',await page.locator('#drop-overlay').isVisible());await page.evaluate(value=>AtlasChat.receive({type:'dropState',active:false,sessionId:'obsolete-session',ownerAccountId:value.ownerAccountId}),state);
  check('A foreign native drag event cannot change the current account overlay',await page.locator('#drop-overlay').isVisible());await page.evaluate(value=>AtlasChat.receive({type:'dropState',active:false,sessionId:value.sessionId,ownerAccountId:value.ownerAccountId}),state);
  check('Native drag completion hides the overlay without an extra DOM upload request',!await page.locator('#drop-overlay').isVisible());
  await page.evaluate(()=>AtlasChat.receive({type:'result',requestId:'native-drop-11111111111111111111111111111111',error:'chat-file-type-not-supported'}));
  check('A native drop failure without a pending DOM request surfaces the file constraint',await page.locator('#toast').innerText().then(text=>text.includes('500 Mo')));
  state.isActive=true;await apply(state);
  const beforeRepeatedHandshake=await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').length);await apply(state);
  check('Unchanged snapshots do not repeatedly emit composer state',await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').length)===beforeRepeatedHandshake);
  state.state.threads[0].canSend=false;await apply(state);
  check('Revoked write access closes native file intake',await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').at(-1)?.payload.acceptsFiles===false));
  state.state.threads[0].canSend=true;state.sessionId='polish-native-drop-new-session';await apply(state);
  check('A replacement session sends its own fresh native file handshake',await page.evaluate(()=>{const last=__actions.filter(a=>a.action==='composerState').at(-1);return last?.sessionId==='polish-native-drop-new-session'&&last.payload.acceptsFiles===true;}));
  state.supportedAttachmentExtensions=['.png','.mp3','.ogg','.wav','.mp4','.webm','.flac','.mkv','.opus','.m4a','.avi'];await apply(state);
  const extraFormats=await page.evaluate(()=>{
    const data=new DataTransfer();for(const [name,type] of [['son.flac','audio/flac'],['video.mkv','video/x-matroska'],['son.opus','audio/opus'],['son.m4a','audio/mp4'],['video.avi','video/x-msvideo']])data.items.add(new File(['synthetic fixture'],name,{type}));
    const before=__actions.length;document.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));return __actions.slice(before).find(action=>action.action==='dropFiles');
  });
  check('Additional native-declared audio and video formats cross the browser bridge together',extraFormats?.additionalObjectCount===5&&extraFormats.payload.threadId==='d:42:91');
  const formatDrop=async name=>page.evaluate(name=>{
    const data=new DataTransfer();data.items.add(new File(['synthetic fixture'],name,{type:'application/octet-stream'}));const before=__actions.length;document.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));return __actions.slice(before).some(action=>action.action==='dropFiles');
  },name);
  check('Native media-format declarations do not admit executable or archive drops',!await formatDrop('blocked.exe')&&!await formatDrop('blocked.zip'));
  delete state.supportedAttachmentExtensions;await apply(state);
  check('A later snapshot for the same account retains the previously declared media formats',await formatDrop('still-supported.MKV'));
  const newOwner={...clone(fixtures.self),accountId:84,username:'Second'};
  await apply({...state,sessionId:'followup-other-formats-account',ownerAccountId:84,selectedThreadId:'d:84:92',state:{...state.state,self:newOwner,contacts:[fixtures.kael],threads:[{id:'d:84:92',kind:'direct',members:[fixtures.member(newOwner),fixtures.member(fixtures.kael)],canSend:true,lastMessage:null,unreadCount:0,pinnedMessages:[]}]},messages:[],pending:[],draft:{}});
  check('A new account without a formats declaration starts from legacy support instead of inheriting another account list',!await formatDrop('must-not-inherit.mkv')&&await formatDrop('legacy.png'));
  await apply(state);

  await exerciseTimelineMotion(page,apply);
  await apply(state);
  const many=[];for(let i=1;i<=80;i++)many.push(fixtures.message(String(1000+i),i%3?fixtures.lyra:fixtures.self,'Message de test '+i+' — une ligne conservée pendant les mises à jour du fil.\nDétail de la conversation pour vérifier le défilement.',i));
  state.messages=many;state.selectedThreadId='d:42:91';state.state.threads[0].lastMessage=many.at(-1);await apply(state);
  await page.locator('#timeline').evaluate(node=>{node.scrollTop=300;});await page.waitForTimeout(50);const before=await page.locator('#timeline').evaluate(node=>node.scrollTop);
  await page.evaluate(()=>{window.__actions=[];});state.messages.push(fixtures.message('1081',fixtures.lyra,'Un nouveau message en bas.',85));await apply(state);
  check('Incoming messages preserve a reader above the bottom and do not send read',Math.abs(await page.locator('#timeline').evaluate(node=>node.scrollTop)-before)<2&&await page.evaluate(()=>!__actions.some(a=>a.action==='read')));
  const anchorBefore=await page.locator('#message-list').evaluate(node=>{const top=document.querySelector('#timeline').getBoundingClientRect().top;const message=Array.from(node.children).find(n=>n.dataset.messageId&&n.getBoundingClientRect().bottom>top+1);return{id:message.dataset.messageId,offset:message.getBoundingClientRect().top-top};});
  state.messages.unshift(...Array.from({length:15},(_,i)=>fixtures.message(String(985+i),fixtures.lyra,'Ancien message '+i+'\nDeuxième ligne',-20+i)));await apply(state);
  const anchorAfter=await page.locator(`[data-message-id="${anchorBefore.id}"]`).evaluate(node=>node.getBoundingClientRect().top-document.querySelector('#timeline').getBoundingClientRect().top);
  check('Prepending history preserves the visible message anchor',Math.abs(anchorAfter-anchorBefore.offset)<2);
  await page.locator('#jump-latest-button').click();await page.waitForFunction(()=>__actions.some(a=>a.action==='read'&&a.payload.throughMessageId==='1081'));check('Jump to latest confirms only the actual current last ID',await page.evaluate(()=>__actions.some(a=>a.action==='read'&&a.payload.throughMessageId==='1081')));
  const beforeInactive=await page.evaluate(()=>__actions.filter(a=>a.action==='read').length);state.isActive=false;state.messages.push(fixtures.message('1082',fixtures.lyra,'Caché',86));await apply(state);check('Inactive window cannot confirm newly arrived messages',await page.evaluate(()=>__actions.filter(a=>a.action==='read').length)===beforeInactive);
  state.isActive=true;state.locale='en';await apply(state);check('English translation covers composer and thread controls',await page.locator('#composer-input').getAttribute('placeholder')==='Write a message…');await page.locator('#toast').waitFor({state:'hidden'});await capture('chat-en-history-fixed.png');
  check('The English send control remains icon-only with its accessible name and tooltip',await iconOnlySend('Send'));
  const englishOwn=page.locator('.message[data-message-id="1078"]');await englishOwn.scrollIntoViewIfNeeded();await englishOwn.hover();await englishOwn.getByRole('button',{name:'Message actions',exact:true}).click();await page.getByRole('menuitem',{name:'Edit',exact:true}).click();
  check('English editing exposes Save while keeping the send control icon-only',await iconOnlySend('Save'));await page.locator('#cancel-context-button').click();
  await page.locator('#composer-input').fill('A single English send');await page.locator('#send-button').click();
  check('English submission exposes Sending without restoring a visible text label',await iconOnlySend('Sending…'));
  const englishSend=await page.evaluate(()=>__actions.filter(a=>a.action==='send'&&a.payload.body==='A single English send').at(-1));await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{accepted:true}}),englishSend);
  const rejected=await page.evaluate(value=>AtlasChat.applySnapshot({...value,sequence:'0'}),state);check('Older snapshot sequence is rejected',rejected===false);
  await apply({...state,sessionId:'new-session',ownerAccountId:84,selectedThreadId:null,state:{...state.state,self:{accountId:84,username:'Second'},threads:[],contacts:[]},messages:[],draft:{}});
  check('Account change clears private messages, drafts and selection',await page.locator('.message').count()===0&&await page.locator('#composer-input').inputValue()==='');
  const fallbackPage=await browser.newPage({viewport:fixedViewport,deviceScaleFactor:1});
  fallbackPage.on('pageerror',error=>errors.push('Fallback: '+error.message));
  await fallbackPage.emulateMedia({reducedMotion:'reduce'});await fallbackPage.route('**/*',routeFixture);
  await fallbackPage.addInitScript(installFixtureBridge,{withoutMoveBefore:true});
  await fallbackPage.goto(appUrl);await fallbackPage.waitForFunction(()=>!!window.AtlasChat);
  runtime.fallbackMoveBefore=await fallbackPage.evaluate(()=>typeof Element.prototype.moveBefore==='function');
  check('The separate older-runtime fixture really lacks the state-preserving DOM move API',runtime.fallbackMoveBefore===false);
  const fallbackApply=async value=>{value.sequence=String(++sequence);await fallbackPage.evaluate(value=>AtlasChat.applySnapshot(value),value);await fallbackPage.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));};
  await exerciseDraftMediaViewer(fallbackPage,fallbackApply,null,'fallback');await fallbackPage.close();
  check('No uncaught browser script errors',errors.length===0);
  const assetHashes={};for(const name of ['index.html','chat.css','chat.js','chat-render.js','chat-media.js','chat-media.css'])assetHashes[name]=createHash('sha256').update(await fs.readFile(path.join(assets,name))).digest('hex');
  await fs.writeFile(path.join(output,'results.json'),JSON.stringify({passed:checks.length,viewport:fixedViewport,runtime,background:'Transparent DOM capture; native Citadel composition is verified separately.',assetHashes,checks,errors},null,2));
  await browser.close();console.log('Chat DOM: '+checks.length+' checks passed. Headless isolated Edge, synthetic accounts, no user session.');
})().catch(async error=>{console.error(error);if(fixtureBrowser)await fixtureBrowser.close();process.exitCode=1;});

function installFixtureBridge(options) {
  window.__actions=[];window.__motionEvents=[];
  window.chrome=window.chrome||{};
  window.chrome.webview={postMessage:message=>window.__actions.push(message),postMessageWithAdditionalObjects:(message,files)=>window.__actions.push({...message,additionalObjectCount:files.length}),addEventListener:()=>{}};
  if(options?.withoutMoveBefore) Object.defineProperty(Element.prototype,'moveBefore',{configurable:true,value:undefined});
  const animate=Element.prototype.animate;
  Element.prototype.animate=function(frames,options){
    const animation=animate.call(this,frames,options);
    window.__motionEvents.push({target:this,message:this.closest('.message'),queue:this.matches('.queued-file')?this:null,kind:'waapi',duration:typeof options==='number'?options:options?.duration||0,animation});
    return animation;
  };
  document.addEventListener('animationstart',event=>{
    const durations=getComputedStyle(event.target).animationDuration.split(',').map(value=>parseFloat(value)*1000);
    window.__motionEvents.push({target:event.target,message:event.target.closest('.message'),queue:event.target.matches('.queued-file')?event.target:null,kind:'css',name:event.animationName,duration:Math.max(...durations)});
  });
}

async function settleMotion(page) {
  await page.waitForFunction(()=>document.getAnimations().every(animation=>animation.effect?.getTiming().iterations===Infinity||animation.playState!=='running'));
}

async function exerciseTimelineMotion(page,apply) {
  await settleMotion(page);await page.emulateMedia({reducedMotion:'no-preference'});
  let state=clone(fixtures.snapshot);state.sessionId='motion-timeline';state.draft={};state.pending=[];
  state.messages=Array.from({length:60},(_,index)=>fixtures.message(String(7001+index),fixtures.lyra,'Message conservé '+index+'\nUne deuxième ligne pour une lecture stable.',index));
  Object.assign(state.state.threads[0],{lastMessage:state.messages.at(-1),pinnedMessages:[],unreadCount:0,lastReadMessageId:'7060'});
  await page.evaluate(()=>window.__motionEvents=[]);await apply(state);
  check('Initial conversation history appears without replaying message entrance animations',await page.evaluate(()=>!__motionEvents.some(event=>event.message&&event.duration>0)));
  await page.evaluate(()=>{window.__motionEvents=[];window.__actions=[];});
  state.messages.push(fixtures.message('7061',fixtures.lyra,'Une nouvelle arrivée visible.',61));state.state.threads[0].lastMessage=state.messages.at(-1);await apply(state);
  check('Only the new visible live message receives a short entrance animation',await page.evaluate(()=>{const events=__motionEvents.filter(event=>event.message&&event.duration>0);return events.length>0&&events.every(event=>event.message.dataset.messageId==='7061'&&event.duration<=220);}));
  const entries=await page.evaluate(()=>__motionEvents.filter(event=>event.message&&event.duration>0).length);
  await apply(state);state.messages.at(-1).reactions=[{emoji:'❤️',accountIds:[42],count:1}];await apply(state);
  check('Repeated snapshots and reaction changes do not replay a message entrance',await page.evaluate(before=>__motionEvents.filter(event=>event.message&&event.duration>0).length===before,entries));
  await settleMotion(page);
  const pending=fixtures.pending(91,{body:'Un seul article pendant la confirmation.',status:'sending',canCancel:false});state.pending=[pending];await apply(state);
  await page.locator('[data-client-message-id="'+pending.clientMessageId+'"]').evaluate(node=>window.__motionPending=node);
  const pendingEntries=await page.evaluate(()=>__motionEvents.filter(event=>event.message===__motionPending&&event.duration>0).length);
  state.messages.push(fixtures.message('7062',fixtures.self,pending.body,62,{clientMessageId:pending.clientMessageId}));state.pending=[];state.state.threads[0].lastMessage=state.messages.at(-1);await apply(state);
  check('Server confirmation reuses the pending article and does not replay its entrance',await page.evaluate(before=>document.querySelector('[data-message-id="7062"]')===__motionPending&&!__motionPending.dataset.clientMessageId&&__motionEvents.filter(event=>event.message===__motionPending&&event.duration>0).length===before,pendingEntries));
  await settleMotion(page);
  const timeline=page.locator('#timeline');await timeline.evaluate(node=>node.scrollTop=280);await page.waitForTimeout(60);
  const before=await timeline.evaluate(node=>node.scrollTop);await page.evaluate(()=>{window.__actions=[];window.__motionEvents=[];});
  state.messages.push(fixtures.message('7063',fixtures.lyra,'Nouvelle arrivée pendant une ancienne lecture.',63));state.state.threads[0].lastMessage=state.messages.at(-1);await apply(state);
  check('An offscreen arrival neither animates nor steals the current reading position',await page.evaluate(()=>!__motionEvents.some(event=>event.message&&event.duration>0)&&!__actions.some(action=>action.action==='read'))&&Math.abs(await timeline.evaluate(node=>node.scrollTop)-before)<2);
  const anchor=await page.locator('#message-list').evaluate(node=>{const top=document.querySelector('#timeline').getBoundingClientRect().top,message=[...node.children].find(child=>child.dataset.messageId&&child.getBoundingClientRect().bottom>top+1);return{id:message.dataset.messageId,offset:message.getBoundingClientRect().top-top};});
  state.messages.unshift(...Array.from({length:12},(_,index)=>fixtures.message(String(6989+index),fixtures.lyra,'Historique antérieur '+index+'\nDeuxième ligne.',index-15)));await apply(state);
  const retainedOffset=await page.locator('[data-message-id="'+anchor.id+'"]').evaluate(node=>node.getBoundingClientRect().top-document.querySelector('#timeline').getBoundingClientRect().top);
  check('Prepending old history keeps the visible anchor and never plays entrance animations',Math.abs(retainedOffset-anchor.offset)<2&&await page.evaluate(()=>!__motionEvents.some(event=>event.message&&event.duration>0)));
  const jumpStart=await timeline.evaluate(node=>node.scrollTop);await page.locator('#jump-latest-button').click();
  await page.waitForFunction(start=>{const node=document.querySelector('#timeline');return node.scrollTop>start+2&&node.scrollHeight-node.clientHeight-node.scrollTop>5;},jumpStart);
  check('The explicit jump moves through intermediate positions before acknowledging the last message',await page.evaluate(()=>!__actions.some(action=>action.action==='read'&&action.payload.throughMessageId==='7063')));
  await page.waitForFunction(()=>{const node=document.querySelector('#timeline');return node.scrollHeight-node.clientHeight-node.scrollTop<=2&&__actions.some(action=>action.action==='read'&&action.payload.throughMessageId==='7063');});
  check('The smooth user jump ends at the actual bottom with the exact read cursor',await page.evaluate(()=>__actions.filter(action=>action.action==='read').at(-1)?.payload.throughMessageId==='7063'));
  for(const gesture of ['wheel','pointer','keyboard']) {
    await timeline.evaluate(node=>node.scrollTop=280);await page.waitForTimeout(60);await page.evaluate(()=>window.__actions=[]);
    await page.locator('#jump-latest-button').click();
    await page.waitForFunction(()=>{const node=document.querySelector('#timeline');return node.scrollTop>282&&node.scrollHeight-node.clientHeight-node.scrollTop>5;});
    if(gesture==='wheel') {const box=await timeline.boundingBox();await page.mouse.move(box.x+box.width/2,box.y+40);await page.mouse.wheel(0,-200);}
    else if(gesture==='pointer')await timeline.click({position:{x:12,y:40}});
    else {await timeline.focus();await page.keyboard.press('PageUp');}
    await page.waitForTimeout(300);
    check('A '+gesture+' reading gesture cancels the smooth jump without marking unseen messages read',await timeline.evaluate(node=>node.scrollHeight-node.clientHeight-node.scrollTop>10)&&await page.evaluate(()=>!__actions.some(action=>action.action==='read')));
  }
  await page.emulateMedia({reducedMotion:'reduce'});await page.evaluate(()=>{window.__actions=[];window.__motionEvents=[];});
  await page.locator('#jump-latest-button').click();
  await page.waitForFunction(()=>{const node=document.querySelector('#timeline');return node.scrollHeight-node.clientHeight-node.scrollTop<=2;});
  state.messages.push(fixtures.message('7064',fixtures.lyra,'Arrivée avec les animations réduites.',64));state.state.threads[0].lastMessage=state.messages.at(-1);
  state.typing=[{threadId:state.selectedThreadId,accountId:91,username:'Lyra',expiresAt:new Date(Date.now()+60000).toISOString()}];await apply(state);
  check('Reduced motion disables live-message entrance and typing pulse animations',await page.evaluate(()=>matchMedia('(prefers-reduced-motion: reduce)').matches&&!__motionEvents.some(event=>event.message&&event.duration>0)&&[...document.querySelectorAll('.typing-dots i')].length===3&&[...document.querySelectorAll('.typing-dots i')].every(node=>getComputedStyle(node).animationName==='none')));
  check('Reduced motion keeps all composer command transitions disabled',await page.locator('.composer-toolbar button,#composer-box').evaluateAll(nodes=>nodes.every(node=>getComputedStyle(node).transitionDuration.split(',').every(duration=>parseFloat(duration)===0))));
  await page.emulateMedia({reducedMotion:'no-preference'});
}

async function exerciseDraftMediaViewer(page,apply,capture,mode='native') {
  const prefix=mode==='fallback'?'Fallback without moveBefore: ':'';
  const assertViewer=(name,value)=>check(prefix+name,value);
  const viewer=page.locator('#image-dialog');
  await page.evaluate(()=>window.__actions=[]);
  let state=clone(fixtures.snapshot);state.sessionId='motion-viewer-'+mode;
  state.messages=[fixtures.message('6001',fixtures.lyra,'Un brouillon avec des médias locaux.',1)];
  state.state.threads[0].pinnedMessages=[];state.state.threads[0].lastMessage=state.messages[0];state.hasEarlier=false;
  state.draft={body:'Un brouillon qui reste intact.',attachments:clone(fixtures.viewerMediaDraft)};
  await apply(state);
  await page.waitForFunction(()=>[...document.querySelectorAll('.queued-preview audio,.queued-preview video')].length===2&&[...document.querySelectorAll('.queued-preview audio,.queued-preview video')].every(media=>media.readyState>=1&&Math.abs(media.duration-4)<.02));
  assertViewer('The playback fixtures decode a full four-second WAV and VP8 clip',await page.locator('.queued-preview video').evaluate(media=>media.videoWidth===640&&media.videoHeight===360&&!media.error));

  if(mode!=='fallback') {
    const imageOpen=page.locator('.queued-file.is-image .queued-preview-open');
    await imageOpen.evaluate(node=>window.__viewerOpener=node);await imageOpen.click();await viewer.waitFor({state:'visible'});
    assertViewer('A draft image opens its actual local preview without sending or downloading',await viewer.evaluate(node=>node.classList.contains('is-image')&&node.querySelector('#image-dialog-image').src.endsWith('/attachments/tiny'))&&await page.evaluate(()=>!__actions.some(action=>['send','downloadAttachment'].includes(action.action))));
    assertViewer('The draft image viewer preserves the draft and blocks native file admission',await page.locator('#composer-input').inputValue()==='Un brouillon qui reste intact.'&&await page.evaluate(()=>__actions.filter(action=>action.action==='composerState').at(-1)?.payload.acceptsFiles===false));
    Object.assign(state.draft.attachments[0],{status:'uploading',isComplete:false,offset:'125'});await apply(state);
    assertViewer('Image upload progress changes beneath an open viewer without closing or replacing its source',await viewer.isVisible()&&await page.locator('.queued-file.is-image .queued-progress').getAttribute('aria-valuenow')==='25'&&await page.locator('#image-dialog-image').getAttribute('src').then(src=>src.endsWith('/attachments/tiny')));
    await page.locator('#image-dialog-image').click();assertViewer('Clicking an enlarged draft image keeps the viewer open',await viewer.isVisible());
    if(capture)await capture('chat-fr-draft-image-viewer-fixed.png');
    await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});
    assertViewer('Escape returns focus to the exact draft image opener and restores file admission',await page.evaluate(()=>document.activeElement===__viewerOpener&&__actions.filter(action=>action.action==='composerState').at(-1)?.payload.acceptsFiles===true));
    state.draft.attachments=clone(fixtures.viewerMediaDraft);await apply(state);
  }

  for(const kind of ['video','audio']) {
    const index=kind==='video'?1:2;
    const inline=page.locator('.queued-file.is-'+kind+' '+kind);
    const opener=page.locator('.queued-file.is-'+kind+' .media-expand');
    assertViewer('The '+kind+' expand command belongs to its shared player controls',await opener.count()===1&&await opener.evaluate(node=>!!node.closest('.media-controls')&&!node.querySelector('audio,video'))&&await inline.evaluate(media=>!media.controls&&!!AtlasChatMedia.shellFor(media)));
    await inline.evaluate(media=>{window.__viewerPlayer=media;window.__viewerShell=AtlasChatMedia.shellFor(media);media.muted=true;media.volume=.35;media.playbackRate=1.25;media.pause();media.currentTime=.75;});
    try { await page.waitForFunction(()=>!__viewerPlayer.seeking&&__viewerPlayer.readyState>=2&&Math.abs(__viewerPlayer.currentTime-.75)<.08); }
    catch(error){console.error('Media seek diagnostics: '+JSON.stringify(await page.evaluate(()=>{const m=__viewerPlayer;return{src:m.currentSrc,time:m.currentTime,seeking:m.seeking,ready:m.readyState,network:m.networkState,paused:m.paused,duration:m.duration,error:m.error?.message,buffered:Array.from({length:m.buffered.length},(_,i)=>[m.buffered.start(i),m.buffered.end(i)]),seekable:Array.from({length:m.seekable.length},(_,i)=>[m.seekable.start(i),m.seekable.end(i)])};})));throw error;}
    await opener.evaluate(node=>window.__viewerOpener=node);await opener.click();await viewer.waitFor({state:'visible'});
    await page.waitForFunction(()=>document.querySelector('#media-dialog-player-host')?.contains(__viewerPlayer)&&__viewerPlayer.paused&&Math.abs(__viewerPlayer.currentTime-.75)<.08);
    assertViewer('Opening draft '+kind+' enlarges the same paused player and shell with seek, volume and speed intact',await page.evaluate(kind=>{const media=__viewerPlayer,shell=AtlasChatMedia.shellFor(media),box=shell.getBoundingClientRect();return document.querySelector('#image-dialog').classList.contains('is-'+kind)&&shell===__viewerShell&&shell.dataset.mode==='viewer'&&media.volume===.35&&media.playbackRate===1.25&&media.muted&&box.width>(kind==='video'?media.videoWidth:250)&&box.width<=innerWidth*.9+1&&!document.querySelector('.queued-file.is-'+kind+' '+kind);},kind));
    assertViewer('The expanded '+kind+' retains its accessible name without a visible title or footer',await page.locator('#media-dialog-title').count()===0&&await page.evaluate(()=>!!__viewerShell.getAttribute('aria-label')&&!document.querySelector('#media-dialog-player-host .attachment-footer')&&!document.querySelector('#media-dialog-player-host').innerText.includes(__viewerShell.getAttribute('aria-label'))));
    await page.locator('#media-dialog-player-host .media-player').hover();
    const controls=await customPlayerControls(page);
    assertViewer('The expanded '+kind+' exposes usable shared buttons and seek controls while native controls stay disabled',controls.buttons>=2&&controls.sliders>=1&&!!controls.playToggle&&controls.nativeControls===false);
    const seek=page.locator('#media-dialog-player-host .media-seek');await seek.focus();await page.keyboard.press('End');await page.waitForFunction(()=>!__viewerPlayer.seeking&&Math.abs(__viewerPlayer.currentTime-4)<.08);
    await page.keyboard.press('Home');await page.waitForFunction(()=>!__viewerPlayer.seeking&&__viewerPlayer.currentTime<.08);
    assertViewer('The '+kind+' seek bar responds to its Home and End keyboard commands',await seek.getAttribute('aria-valuetext').then(value=>value.startsWith('0:00 / 0:04')));
    await page.locator('#media-dialog-player-host .media-volume-button').click();
    await page.locator('#media-dialog-player-host .media-volume').focus();await page.keyboard.press('End');
    assertViewer('The '+kind+' volume popover changes real volume and unmutes the player',await page.evaluate(()=>__viewerPlayer.volume===1&&!__viewerPlayer.muted&&__viewerShell.querySelector('.media-volume-button').getAttribute('aria-expanded')==='true'));
    await page.keyboard.press('Home');assertViewer('Zero volume mutes '+kind+' without changing playback position',await page.evaluate(()=>__viewerPlayer.volume===0&&__viewerPlayer.muted&&__viewerPlayer.currentTime<.08));
    await page.keyboard.press('Escape');assertViewer('Escape dismisses '+kind+' volume before the viewer and restores the volume control focus',await viewer.isVisible()&&await page.evaluate(()=>document.activeElement===__viewerShell.querySelector('.media-volume-button')&&__viewerShell.querySelector('.media-volume-panel').hidden));
    await page.evaluate(()=>{__viewerPlayer.currentTime=.75;__viewerPlayer.volume=.35;__viewerPlayer.muted=true;});await page.waitForFunction(()=>!__viewerPlayer.seeking&&Math.abs(__viewerPlayer.currentTime-.75)<.08);
    const currentControls=await customPlayerControls(page);await page.mouse.click(currentControls.playToggle.x,currentControls.playToggle.y);
    await page.waitForFunction(()=>!__viewerPlayer.paused&&__viewerPlayer.currentTime>1);
    assertViewer('The shared '+kind+' play button actually starts playback in the viewer',await page.evaluate(()=>!__viewerPlayer.paused&&__viewerPlayer.currentTime>1));
    await page.evaluate(()=>{window.__viewerProgressTime=__viewerPlayer.currentTime;window.__queueMotionCount=__motionEvents.filter(event=>event.queue&&event.duration>0).length;});
    Object.assign(state.draft.attachments[index],{status:'uploading',isComplete:false,offset:String(Math.floor(Number(state.draft.attachments[index].size)/2))});
    state.locale=state.locale==='fr'?'en':'fr';await apply(state);
    assertViewer('Progress and locale updates preserve the open '+kind+' player and ongoing playback',await page.evaluate(()=>document.querySelector('#media-dialog-player-host').querySelector('audio,video')===__viewerPlayer&&!__viewerPlayer.paused&&__viewerPlayer.currentTime>=__viewerProgressTime-.04&&__viewerPlayer.currentTime-__viewerProgressTime<1));
    assertViewer('Progress and locale snapshots do not replay draft preview entrance animations',await page.evaluate(()=>__motionEvents.filter(event=>event.queue&&event.duration>0).length===__queueMotionCount));
    const upload=state.draft.attachments[index];Object.assign(upload,{status:'ready',isComplete:true,offset:upload.size,attachment:{id:'completed-'+kind,fileName:upload.fileName,contentType:upload.contentType,kind,url:upload.previewUrl}});await apply(state);
    assertViewer('Completing the same '+kind+' upload keeps its enlarged player instead of rebuilding it',await page.evaluate(()=>document.querySelector('#image-dialog').open&&document.querySelector('#media-dialog-player-host').querySelector('audio,video')===__viewerPlayer&&!__viewerPlayer.paused));
    if(capture){await page.locator('#media-dialog-player-host .media-player').hover();await page.waitForTimeout(200);await capture('chat-'+state.locale+'-draft-'+kind+'-viewer-fixed.png');}
    await viewer.click({position:{x:5,y:5}});await viewer.waitFor({state:'hidden'});
    await page.waitForFunction(kind=>document.querySelector('.queued-file.is-'+kind+' '+kind)===__viewerPlayer&&!__viewerPlayer.paused,kind);
    assertViewer('Backdrop closure returns the same playing '+kind+' element and restores its opener focus',await page.evaluate(()=>document.activeElement===__viewerOpener&&__viewerPlayer.currentTime>.75&&__viewerPlayer.volume===.35&&__viewerPlayer.playbackRate===1.25));
    await page.locator('.queued-file.is-'+kind+' .media-player').hover();const returnedControls=await customPlayerControls(page);
    if(returnedControls.buttons<2||returnedControls.sliders<1){console.error('Returned shared control diagnostics: '+JSON.stringify({kind,...returnedControls}));if(capture)await capture('chat-returned-'+kind+'-controls-diagnostic.png');}
    assertViewer('The returned inline '+kind+' keeps its shared playback controls accessible',returnedControls.buttons>=2&&returnedControls.sliders>=1&&returnedControls.nativeControls===false);
    const inlineBox=await page.locator('.queued-file.is-'+kind+' .media-player').boundingBox(),pauseButton=returnedControls.playToggle;
    assertViewer('The returned '+kind+' shared control remains positioned inside its preview',!!pauseButton&&pauseButton.x>=inlineBox.x&&pauseButton.x<=inlineBox.x+inlineBox.width&&pauseButton.y>=inlineBox.y&&pauseButton.y<=inlineBox.y+inlineBox.height);
    await page.mouse.click(pauseButton.x,pauseButton.y);await page.waitForFunction(()=>__viewerPlayer.paused);
    assertViewer('The returned shared '+kind+' pause button remains usable after the player moves',await page.evaluate(()=>__viewerPlayer.paused&&AtlasChatMedia.shellFor(__viewerPlayer)===__viewerShell));
    if(capture){await page.waitForTimeout(200);await capture('chat-'+state.locale+'-returned-'+kind+'-controls-fixed.png');}
    await page.evaluate(()=>{__viewerPlayer.pause();__viewerPlayer.currentTime=1.5;});
    await page.waitForFunction(()=>!__viewerPlayer.seeking&&Math.abs(__viewerPlayer.currentTime-1.5)<.08);
    await opener.click();await viewer.waitFor({state:'visible'});await page.locator('#media-dialog-player-host .media-player').hover();const reopenedControls=await customPlayerControls(page);
    assertViewer('Reopening the '+kind+' viewer keeps the actual shared controls accessible',reopenedControls.buttons>=2&&reopenedControls.sliders>=1);
    if(capture)await capture('chat-'+state.locale+'-reopened-'+kind+'-viewer-fixed.png');
    await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});
    assertViewer('A paused '+kind+' stays paused at its position after opening and closing again',await page.evaluate(kind=>document.querySelector('.queued-file.is-'+kind+' '+kind)===__viewerPlayer&&__viewerPlayer.paused&&Math.abs(__viewerPlayer.currentTime-1.5)<.08,kind));
  }
  state.isMediaActive=true;await apply(state);
  await page.locator('.queued-file.is-audio audio').evaluate(media=>{window.__scopePlayer=media;media.currentTime=.5;media.muted=true;});
  await page.locator('.queued-file.is-audio .media-expand').click();await viewer.waitFor({state:'visible'});
  await page.locator('#media-dialog-player-host .media-play').click();await page.waitForFunction(()=>!__scopePlayer.paused);
  const readsBeforeFocusLoss=await page.evaluate(()=>__actions.filter(action=>action.action==='read').length);
  state.isActive=false;state.messages.push(fixtures.message('6002',fixtures.lyra,'Message arrivé pendant la perte de focus.',2));state.state.threads[0].lastMessage=state.messages.at(-1);await apply(state);
  const focusScope=await page.evaluate(value=>{const before=__actions.length,data=new DataTransfer();data.items.add(new File(['fixture'],'inactive.png',{type:'image/png'}));document.dispatchEvent(new DragEvent('drop',{bubbles:true,cancelable:true,dataTransfer:data}));AtlasChat.receive({type:'dropState',active:true,sessionId:value.sessionId,ownerAccountId:value.ownerAccountId});return{viewer:document.querySelector('#image-dialog').open,playing:!__scopePlayer.paused,files:__actions.slice(before).filter(action=>action.action==='dropFiles').length,acceptsFiles:__actions.filter(action=>action.action==='composerState').at(-1)?.payload.acceptsFiles,reads:__actions.filter(action=>action.action==='read').length,overlay:!document.querySelector('#drop-overlay').hidden};},state);
  assertViewer('Losing UI focus preserves allowed media playback while read receipts and file admission remain inactive',focusScope.viewer&&focusScope.playing&&focusScope.files===0&&focusScope.acceptsFiles===false&&focusScope.reads===readsBeforeFocusLoss&&!focusScope.overlay);
  await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});
  assertViewer('Closing a preview while UI focus is inactive returns the same still-playing audio',await page.evaluate(()=>document.querySelector('.queued-file.is-audio audio')===__scopePlayer&&!__scopePlayer.paused&&!!AtlasChatMedia.shellFor(__scopePlayer)));
  state.isMediaActive=false;await apply(state);
  assertViewer('Leaving Messages stops playback but retains a usable shell for the same conversation',await page.evaluate(()=>__scopePlayer.paused&&!!AtlasChatMedia.shellFor(__scopePlayer)&&__scopePlayer.isConnected));
  state.isActive=true;state.isMediaActive=true;await apply(state);
  assertViewer('Returning to Messages keeps the stopped audio paused until another explicit play',await page.evaluate(()=>__scopePlayer.paused));
  if(mode==='fallback')return;

  state.draft.attachments[1]=clone(fixtures.mediaDraft[1]);await apply(state);
  await page.waitForFunction(()=>document.querySelector('.queued-file.is-video video')?.videoWidth===160);
  await page.locator('.queued-file.is-video .media-expand').click();await viewer.waitFor({state:'visible'});await settleMotion(page);
  assertViewer('A low-resolution video opens at a comfortable larger size while retaining its aspect ratio and fitting the viewer',await page.locator('#media-dialog-player-host video').evaluate(media=>{const box=media.getBoundingClientRect();return media.videoWidth===160&&box.width>=320&&Math.abs(box.width/box.height-16/9)<.02&&box.width<=innerWidth*.9+1&&box.height<=innerHeight*.9+1;}));
  if(capture)await capture('chat-fr-draft-low-resolution-video-viewer-fixed.png');
  await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});state.draft.attachments=clone(fixtures.viewerMediaDraft);await apply(state);

  async function openPlayingVideo() {
    await page.locator('.queued-file.is-video video').evaluate(media=>{window.__invalidatedPlayer=media;media.muted=true;media.currentTime=.5;});
    await page.locator('.queued-file.is-video .media-expand').click();await viewer.waitFor({state:'visible'});
    await page.evaluate(async()=>{await __invalidatedPlayer.play();});
    await page.waitForFunction(()=>!__invalidatedPlayer.paused);
  }
  await openPlayingVideo();state.draft.attachments=state.draft.attachments.filter(upload=>upload.id!=='preview-video');await apply(state);
  assertViewer('Removing the viewed upload closes immediately, pauses playback and makes any exiting preview inert',await page.evaluate(()=>{const exiting=__invalidatedPlayer.closest('.queued-file-exit');return !document.querySelector('#image-dialog').open&&__invalidatedPlayer.paused&&(!__invalidatedPlayer.isConnected||exiting?.inert&&exiting.getAttribute('aria-hidden')==='true');}));
  await page.waitForFunction(()=>!__invalidatedPlayer.isConnected);
  assertViewer('The removed upload player detaches after its short visual exit',await page.evaluate(()=>!__invalidatedPlayer.isConnected));
  state.draft.attachments=clone(fixtures.viewerMediaDraft);await apply(state);await openPlayingVideo();state.isActive=false;state.isMediaActive=false;await apply(state);
  assertViewer('Leaving Messages closes the open viewer and pauses its media',await page.evaluate(()=>!document.querySelector('#image-dialog').open&&__invalidatedPlayer.paused));
  state.isActive=true;state.isMediaActive=true;await apply(state);await openPlayingVideo();state.selectedThreadId='d:42:92';state.messages=[];state.draft={};await apply(state);
  assertViewer('Changing conversation closes the viewer, pauses playback and clears the old local source',await page.evaluate(()=>!document.querySelector('#image-dialog').open&&__invalidatedPlayer.paused&&!document.querySelector('#media-dialog-player-host audio,#media-dialog-player-host video')));
  state.selectedThreadId='d:42:91';state.draft={attachments:clone(fixtures.viewerMediaDraft)};await apply(state);await openPlayingVideo();state.sessionId='motion-viewer-replacement-session';state.ownerAccountId=84;state.selectedThreadId=null;state.messages=[];state.draft={};state.state={...state.state,self:{accountId:84,username:'Second'},contacts:[],threads:[]};await apply(state);
  assertViewer('Replacing the account session closes the viewer without restoring a private player into the new account',await page.evaluate(()=>!document.querySelector('#image-dialog').open&&__invalidatedPlayer.paused&&!__invalidatedPlayer.isConnected&&!document.querySelector('#media-dialog-player-host audio,#media-dialog-player-host video')));

  state=clone(fixtures.snapshot);state.sessionId='motion-received-viewer';state.draft={};state.state.threads[0].pinnedMessages=[];state.hasEarlier=false;
  state.messages=[fixtures.message('6101',fixtures.lyra,'Une vidéo reçue.',1,{attachments:[{id:'viewer-video.webm',fileName:'Clip reçu.webm',kind:'video',contentType:'video/webm',size:String(fixtures.viewerVideoBytes.length),url:fixtures.mediaOrigin+'attachments/viewer-video.webm'}]})];await apply(state);
  const received=page.locator('[data-attachment-id="viewer-video.webm"]');
  await received.locator('video').evaluate(media=>{window.__receivedPlayer=media;media.muted=true;media.preload='auto';media.load();});
  await page.waitForFunction(()=>__receivedPlayer.readyState>=2);await received.locator('.media-expand').click();await viewer.waitFor({state:'visible'});
  assertViewer('An already sent video expands through the same viewer using its existing player',await page.evaluate(()=>document.querySelector('#media-dialog-player-host').querySelector('audio,video')===__receivedPlayer));
  const fullscreen=await page.locator('#media-dialog-player-host .media-fullscreen').isVisible();
  if(fullscreen){await page.locator('#media-dialog-player-host .media-fullscreen').click();await page.waitForFunction(()=>document.fullscreenElement===AtlasChatMedia.shellFor(__receivedPlayer));}
  if(fullscreen){
    await page.keyboard.press('Escape');await page.waitForFunction(()=>!document.fullscreenElement);
    assertViewer('Escape exits fullscreen video without leaving its player detached or hidden',await page.evaluate(()=>__receivedPlayer.isConnected&&__receivedPlayer.getBoundingClientRect().width>0&&(document.querySelector('#image-dialog').open?document.querySelector('#media-dialog-player-host').contains(__receivedPlayer):!!__receivedPlayer.closest('.attachment'))));
    if(!await viewer.isVisible()){await received.locator('.media-expand').click();await viewer.waitFor({state:'visible'});}
  }else console.log('SKIP Fullscreen API is unavailable in this headless browser.');
  await page.evaluate(async()=>{await __receivedPlayer.play();});state.messages[0].deletedAt=fixtures.at(2);await apply(state);
  assertViewer('Deleting the viewed sent message closes the viewer and pauses its media',await page.evaluate(()=>!document.querySelector('#image-dialog').open&&__receivedPlayer.paused&&!__receivedPlayer.isConnected));
  state.messages[0].id='6102';state.messages[0].deletedAt=null;await apply(state);
  await received.locator('video').evaluate(media=>{window.__failedReceivedPlayer=media;media.muted=true;media.preload='auto';media.load();});
  await page.waitForFunction(()=>__failedReceivedPlayer.readyState>=2);await received.locator('.media-expand').click();await viewer.waitFor({state:'visible'});
  await page.evaluate(async()=>{await __failedReceivedPlayer.play();__failedReceivedPlayer.src='https://atlas-chat-media.invalid/attachments/fixture-broken.mkv';__failedReceivedPlayer.load();});
  await received.locator('.media-playback-unavailable').waitFor({state:'visible'});
  assertViewer('A decode error closes and pauses sent video, restores local feedback and exposes no download',await page.evaluate(()=>!document.querySelector('#image-dialog').open&&__failedReceivedPlayer.paused&&!document.querySelector('#media-dialog-player-host audio,#media-dialog-player-host video,#media-dialog-player-host .media-playback-unavailable'))&&await received.locator('video,.media-player,button').count()===0);
  assertViewer('Media viewing never triggers a send or download and preserves the transparent Citadel canvas',await page.evaluate(()=>!__actions.some(action=>['send','downloadAttachment'].includes(action.action))&&[document.documentElement,document.body,document.querySelector('#chat-app')].every(node=>getComputedStyle(node).backgroundColor==='rgba(0, 0, 0, 0)')));
}

async function exerciseMediaMenusAndLinks(page,apply,capture) {
  const state=clone(fixtures.snapshot);state.sessionId='player-media-menus';state.hasEarlier=false;
  state.state.threads[0].pinnedMessages=[];state.state.threads[0].unreadCount=0;
  state.draft={attachments:[...clone(fixtures.viewerMediaDraft),{id:'draft-document',fileName:'Brouillon.pdf',contentType:'application/pdf',size:'500',offset:'500',status:'ready',isComplete:true,previewUrl:fixtures.mediaOrigin+'attachments/document'}]};
  const attachments=[
    {id:'tiny',fileName:'Image privée.png',kind:'image',contentType:'image/png',url:fixtures.mediaOrigin+'attachments/tiny'},
    {id:'viewer-audio.wav',fileName:'Audio privé.wav',kind:'audio',contentType:'audio/wav',url:fixtures.mediaOrigin+'attachments/viewer-audio.wav'},
    {id:'viewer-video.webm',fileName:'Vidéo privée.webm',kind:'video',contentType:'video/webm',url:fixtures.mediaOrigin+'attachments/viewer-video.webm'},
    {id:'document',fileName:'Document privé.pdf',kind:'file',contentType:'application/pdf',url:fixtures.mediaOrigin+'attachments/document'}];
  state.messages=attachments.map((attachment,index)=>fixtures.message(String(6201+index),fixtures.lyra,'',index,{attachments:[attachment]}));
  state.state.threads[0].lastMessage=state.messages.at(-1);await apply(state);await page.evaluate(()=>window.__actions=[]);
  const menu=page.locator('#context-menu'),viewer=page.locator('#image-dialog');let saves=0;
  const saveFromMenu=async(target,payload,label,insideViewer=false)=>{
    await target.click({button:'right'});await menu.waitFor({state:'visible'});
    check(label+' exposes only its localized Save as command',await menu.getByRole('menuitem').count()===1&&await menu.getByRole('menuitem',{name:state.locale==='en'?'Save as…':'Enregistrer sous…',exact:true}).count()===1);
    if(insideViewer)check(label+' keeps its context menu inside the modal top layer',await menu.evaluate(node=>node.parentElement.id==='image-dialog'));
    await menu.getByRole('menuitem').click();await menu.waitFor({state:'hidden'});saves++;
    const request=await page.evaluate(()=>__actions.filter(action=>action.action==='downloadAttachment').at(-1));
    check(label+' sends the exact native Save as identifier',JSON.stringify(request.payload)===JSON.stringify(payload));
    await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{saved:true}}),request);
  };
  for(const id of ['tiny','viewer-audio.wav','document'])await saveFromMenu(page.locator('[data-attachment-id="'+id+'"]'),{attachmentId:id},'Received '+id);
  await page.locator('[data-attachment-id="viewer-video.webm"] .media-player').click({button:'right'});
  check('Received video has no context Save as, visible download or file footer',!await menu.isVisible()&&await page.locator('[data-attachment-id="viewer-video.webm"] .media-save:visible,[data-attachment-id="viewer-video.webm"] .attachment-footer').count()===0);
  for(const [kind,id] of [['image','preview-image'],['audio','preview-audio'],['file','draft-document']])await saveFromMenu(page.locator('.queued-file.is-'+kind),{uploadId:id},'Draft '+kind);
  await page.locator('.queued-file.is-video .media-player').click({button:'right'});
  check('Draft video suppresses the context menu and any Save as player command',!await menu.isVisible()&&await page.locator('.queued-file.is-video .media-save:visible').count()===0);
  await page.locator('.queued-file.is-image .queued-preview-open').click();await viewer.waitFor({state:'visible'});
  await saveFromMenu(page.locator('#image-dialog-image'),{uploadId:'preview-image'},'Expanded draft image',true);
  await page.locator('#image-dialog-image').click({button:'right'});await menu.waitFor({state:'visible'});await page.keyboard.press('Escape');await menu.waitFor({state:'hidden'});
  check('The first Escape closes the image Save as menu while preserving its viewer',await viewer.isVisible());
  await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});
  await page.locator('.queued-file.is-audio .media-expand').click();await viewer.waitFor({state:'visible'});
  await saveFromMenu(page.locator('#media-dialog-player-host .media-player'),{uploadId:'preview-audio'},'Expanded draft audio',true);
  await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});
  await page.locator('[data-attachment-id="tiny"]').click();await viewer.waitFor({state:'visible'});
  await saveFromMenu(page.locator('#image-dialog-image'),{attachmentId:'tiny'},'Expanded received image',true);
  await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});
  state.locale='en';await apply(state);
  await page.locator('[data-attachment-id="viewer-audio.wav"] .media-save').click();saves++;
  const audioSave=await page.evaluate(()=>__actions.filter(action=>action.action==='downloadAttachment').at(-1));
  check('The received audio player exposes an explicit localized Save as control',await page.locator('[data-attachment-id="viewer-audio.wav"] .media-save').getAttribute('aria-label')==='Save as…'&&audioSave.payload.attachmentId==='viewer-audio.wav');
  await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{saved:true}}),audioSave);
  await saveFromMenu(page.locator('[data-attachment-id="document"]'),{attachmentId:'document'},'English received document');
  check('Media menus produce only explicit Save as requests and no implicit send or open',await page.evaluate(expected=>__actions.filter(action=>action.action==='downloadAttachment').length===expected&&!__actions.some(action=>['send','openAttachment'].includes(action.action)),saves));
  state.draft={};state.messages=[fixtures.message('6251',fixtures.lyra,'Direct media previews and Vimeo.',1,{linkPreviews:[
    {id:'direct-video',kind:'video',url:'https://example.test/viewer-video.webm',title:'Private direct video title',canRemove:false},
    {id:'direct-audio',kind:'audio',url:'https://example.test/viewer-audio.wav',title:'Private direct audio title',canRemove:false},
    {id:'vimeo',kind:'vimeo',url:'https://vimeo.com/123456789',embedUrl:'https://player.vimeo.com/video/123456789',title:'Duplicate Vimeo title',description:'Duplicate Vimeo description',canRemove:false} ]})];state.state.threads[0].lastMessage=state.messages[0];await apply(state);await page.evaluate(()=>window.__actions=[]);
  const directVideo=page.locator('[data-preview-id="direct-video"]'),directAudio=page.locator('[data-preview-id="direct-audio"]'),vimeo=page.locator('[data-preview-id="vimeo"]');
  check('Direct audio and video links immediately expose integrated custom players with no separate poster click',await directVideo.locator('.media-player video').count()===1&&await directAudio.locator('.media-player audio').count()===1&&await page.locator('[data-preview-id^="direct-"] .media-placeholder').count()===0);
  check('Direct media and Vimeo show no duplicate visible title, file footer or source button',await page.locator('.message-previews .link-preview-main,.message-previews .attachment-footer,.message-previews .preview-source-action').count()===0&&!await page.locator('.message-previews').innerText().then(text=>text.includes('Private direct')||text.includes('Duplicate Vimeo')));
  check('A direct audio preview fits its player exactly without a second frame or empty side panel',await directAudio.evaluate(node=>{const shell=node.querySelector('.media-player'),style=getComputedStyle(node);return Math.abs(node.getBoundingClientRect().width-shell.getBoundingClientRect().width)<1&&style.borderLeftWidth==='0px'&&style.backgroundColor==='rgba(0, 0, 0, 0)'&&style.paddingRight==='0px';}));
  check('A direct video has only its shared player frame',await directVideo.evaluate(node=>getComputedStyle(node).borderTopWidth==='0px'&&getComputedStyle(node).backgroundColor==='rgba(0, 0, 0, 0)'));
  await directVideo.locator('video').evaluate(player=>{window.__linkedPlayer=player;window.__linkedShell=AtlasChatMedia.shellFor(player);player.muted=true;});
  await directVideo.locator('.media-play').click();await page.waitForFunction(()=>!__linkedPlayer.paused&&__linkedPlayer.currentTime>.05);
  check('A direct video starts from one actual custom play click',await page.evaluate(()=>__linkedPlayer.currentSrc.includes('/linked-media?')&&!__linkedPlayer.paused));
  await directVideo.locator('.media-expand').click();await viewer.waitFor({state:'visible'});
  state.locale='fr';await apply(state);
  check('Direct linked video preserves its player, shell and preview identity through expansion and locale refresh',await page.evaluate(()=>document.querySelector('#media-dialog-player-host').contains(__linkedPlayer)&&AtlasChatMedia.shellFor(__linkedPlayer)===__linkedShell&&__linkedShell.dataset.mode==='viewer'&&!__linkedPlayer.paused));
  await page.keyboard.press('Escape');await viewer.waitFor({state:'hidden'});await directVideo.locator('.media-play').click();await page.waitForFunction(()=>__linkedPlayer.paused);
  await directAudio.locator('.media-player').click({button:'right'});
  check('A linked audio preview without a native attachment identity cannot fabricate a Save as request',!await menu.isVisible()&&await directAudio.locator('.media-save:visible').count()===0&&await page.evaluate(()=>!__actions.some(action=>action.action==='downloadAttachment')));
  state.messages[0].linkPreviews[1].canRemove=true;await apply(state);
  check('The removable direct audio preview reserves space for its cross outside the player and expand control',await directAudio.evaluate(node=>{const close=node.querySelector('.preview-dismiss').getBoundingClientRect(),shell=node.querySelector('.media-player').getBoundingClientRect(),expand=node.querySelector('.media-expand').getBoundingClientRect();return close.left>=shell.right&&close.left>=expand.right&&node.getBoundingClientRect().width-shell.width>=30;}));
  if(capture){await directVideo.scrollIntoViewIfNeeded();await capture('chat-fr-integrated-direct-media-fixed.png');}
  await directAudio.locator('.preview-dismiss').click();
  check('The separate audio cross targets only its actual preview',await page.evaluate(()=>__actions.some(action=>action.action==='dismissPreview'&&action.payload.messageId==='6251'&&action.payload.previewId==='direct-audio')));
  await vimeo.locator('.media-placeholder').click();await vimeo.locator('iframe').waitFor();
  check('The first Vimeo click requests autoplay using supported minimal provider chrome parameters',await vimeo.locator('iframe').evaluate(frame=>{const url=new URL(frame.src);return url.hostname==='player.vimeo.com'&&url.searchParams.get('autoplay')==='1'&&url.searchParams.get('playsinline')==='1'&&['title','byline','portrait'].every(key=>url.searchParams.get(key)==='0')&&!url.searchParams.has('background')&&frame.loading==='eager';}));
  await vimeo.frameLocator('iframe').getByText('Lecteur de test isolé',{exact:true}).waitFor();
  if(capture)await capture('chat-fr-integrated-linked-media-fixed.png');
}

async function customPlayerControls(page) {
  return page.evaluate(()=>{
    const player=window.__viewerPlayer,shell=AtlasChatMedia.shellFor(player);
    const visible=node=>{const box=node.getBoundingClientRect(),style=getComputedStyle(node);return !node.hidden&&box.width>0&&box.height>0&&style.visibility!=='hidden'&&style.display!=='none';};
    const buttons=[...shell.querySelectorAll('button')].filter(visible),sliders=[...shell.querySelectorAll('input[type=range]')].filter(visible);
    const toggle=buttons.find(node=>node.classList.contains('media-play')),box=toggle?.getBoundingClientRect();
    return{buttons:buttons.length,sliders:sliders.length,nativeControls:player.controls,playToggle:box?{x:box.left+box.width/2,y:box.top+box.height/2}:null,
      visibleControls:[...buttons,...sliders].map(node=>({role:node.tagName,name:node.getAttribute('aria-label'),disabled:node.disabled}))};
  });
}
