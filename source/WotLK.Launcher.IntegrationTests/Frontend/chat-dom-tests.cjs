const fs = require('node:fs/promises');
const { existsSync } = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');
const fixtures = require('./chat-fixtures.cjs');
const repo = path.resolve(process.env.ATLAS_CHAT_REPO_ROOT || path.join(__dirname, '../../..'));
const assets = path.join(repo, 'source/WotLK.Launcher/Assets/Chat');
const output = path.resolve(repo, process.env.ATLAS_CHAT_TEST_OUTPUT || 'artifacts/atlas-chat-polish-20260907/dom');
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
  const page = await browser.newPage({viewport:{width:1470,height:900},deviceScaleFactor:1});
  const errors = [];
  page.on('pageerror',error=>errors.push(error.message));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin === 'https://animeclub.fr' && url.pathname.startsWith('/atlas-messages/')) {
      const relative = decodeURIComponent(url.pathname.slice('/atlas-messages/'.length)) || 'index.html';
      const target = path.resolve(assets, relative);
      if (!target.startsWith(assets + path.sep)) return route.abort();
      const type = target.endsWith('.css')?'text/css':target.endsWith('.js')?'application/javascript':target.endsWith('.ttf')?'font/ttf':'text/html';
      return route.fulfill({status:200,contentType:type,headers:{'Content-Security-Policy':policy},body:await fs.readFile(target)});
    }
    if (url.origin === 'https://atlas-chat-media.invalid') {
      if (url.pathname.includes('avatar')) return route.fulfill({status:200,contentType:'image/svg+xml',body:svgAvatar(url.pathname.split('/').at(-1))});
      if (url.pathname.includes('landscape')) return route.fulfill({status:200,contentType:'image/svg+xml',body:landscape});
      if (url.pathname.includes('portrait')) return route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="240" height="960"><rect width="240" height="960" fill="#476978"/><circle cx="120" cy="140" r="62" fill="#bec9c3"/><path d="M0 960V600L120 270l120 330v360Z" fill="#273f4b"/></svg>'});
      if (url.pathname.includes('tiny')) return route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="96" height="64"><rect width="96" height="64" fill="#466e68"/><circle cx="48" cy="32" r="18" fill="#bdd5cd"/></svg>'});
      return route.fulfill({status:200,contentType:'application/octet-stream',body:''});
    }
    if (url.origin === 'https://www.youtube-nocookie.com' || url.origin === 'https://player.vimeo.com') return route.fulfill({status:200,contentType:'text/html',body:'<!doctype html><title>Fixture video</title><body style="margin:0;background:#081826;color:#ccddeb;display:grid;place-items:center;height:100vh;font:16px sans-serif">Lecteur de test isolé</body>'});
    return route.abort();
  });
  await page.addInitScript(() => {
    window.__actions=[];
    window.chrome=window.chrome||{};
    window.chrome.webview={postMessage:message=>window.__actions.push(message),postMessageWithAdditionalObjects:(message,files)=>window.__actions.push({...message,additionalObjectCount:files.length}),addEventListener:()=>{}};
  });
  await page.goto(appUrl); await page.waitForFunction(()=>!!window.AtlasChat);
  check('Boot emits ready without synthetic account content',await page.evaluate(()=>__actions[0].action==='ready'&&document.querySelectorAll('.message').length===0));
  const apply = async value => { value.sequence=String(++sequence); await page.evaluate(value=>AtlasChat.applySnapshot(value),value); await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))); };
  let state=clone(fixtures.snapshot); await apply(state); await page.waitForTimeout(80);
  check('The native composer handshake enables files only for its current session and thread',await page.evaluate(()=>__actions.some(a=>a.action==='composerState'&&a.payload.threadId==='d:42:91'&&a.payload.acceptsFiles===true&&a.ownerAccountId===42&&a.sessionId==='43e20968-4137-4f23-9174-4a097cc6a873')));
  check('Two columns at large width with native Atlas typography',await page.evaluate(()=>getComputedStyle(document.querySelector('.chat-layout')).gridTemplateColumns.split(' ').length===2&&getComputedStyle(document.querySelector('h1')).fontFamily.includes('Inter')));
  check('Int64 identifiers above 2^53 remain exact and ordered',JSON.stringify(await page.locator('.message[data-message-id]').evaluateAll(nodes=>nodes.map(n=>n.dataset.messageId)))===JSON.stringify(state.messages.map(m=>m.id)));
  check('Same author and origin group consecutive messages',await page.locator('[data-message-id="9007199254740994"]').evaluate(node=>node.classList.contains('is-continuation')));
  check('Unread and date separators are visible',await page.locator('.unread-divider').count()===1&&await page.locator('.date-divider').count()===1);
  check('Actual bottom after rendering emits last rendered cursor as string',await page.evaluate(()=>__actions.some(a=>a.action==='read'&&a.payload.throughMessageId==='9007199254740999')));
  await page.screenshot({path:path.join(output,'chat-fr-large.png')});
  await page.setViewportSize({width:1032,height:720}); await page.waitForTimeout(80); await page.screenshot({path:path.join(output,'chat-fr-compact.png')});
  check('Compact layout has no horizontal overflow and retains both columns',await page.evaluate(()=>document.documentElement.scrollWidth===innerWidth&&document.querySelector('.conversation-sidebar').getBoundingClientRect().width>=248));
  await page.setViewportSize({width:1470,height:900});

  const safety=await page.evaluate(()=>{
    const R=AtlasChatRender,node=R.renderMarkdown('**gras** *italique* ~~barré~~ ||secret||\n\n<img src=x onerror="window.__xss=1">\n\n![photo](https://outside.test/a.png) [piège](javascript:alert(1)) [ok](https://example.test)',key=>key);
    document.body.append(node);const before={strong:node.querySelectorAll('strong').length,em:node.querySelectorAll('em').length,strike:node.querySelectorAll('s').length,images:node.querySelectorAll('img').length,scripts:node.querySelectorAll('script').length,badLinks:Array.from(node.querySelectorAll('a')).some(a=>a.href.startsWith('javascript:')),spoilerHidden:node.querySelector('.spoiler').getAttribute('aria-expanded')==='false'};
    node.querySelector('.spoiler').click();before.spoilerRevealed=node.querySelector('.spoiler').getAttribute('aria-expanded')==='true';node.remove();return before;
  });
  check('Markdown formats text while raw HTML and remote image loading stay disabled',safety.strong===1&&safety.em===1&&safety.strike===1&&safety.images===0&&safety.scripts===0&&!safety.badLinks);
  check('Spoiler requires an explicit reveal action',safety.spoilerHidden&&safety.spoilerRevealed);
  check('URL policy blocks credentials, script schemes and untrusted media hosts',await page.evaluate(()=>!AtlasChatRender.safeUrl('javascript:alert(1)')&&!AtlasChatRender.safeUrl('https://user:pass@example.test')&&!AtlasChatRender.mediaUrl('https://example.test/a.png')&&!AtlasChatRender.embedUrl('https://evil.test/embed/123')));

  await page.locator('#contact-search').fill('lyra'); check('Contact search filters conversations and participant names',await page.locator('.conversation-row').count()===2); await page.locator('#contact-search').fill('');
  await page.locator('[data-filter="unread"]').click();check('Unread filter uses genuine server unread counts',await page.locator('.conversation-row').count()===2);await page.locator('[data-filter="all"]').click();
  await page.locator('#new-conversation-button').click(); await page.getByRole('button',{name:'Groupe',exact:true}).click(); await page.getByRole('textbox',{name:'Nom du groupe',exact:true}).fill('La compagnie'); await page.getByRole('button',{name:'Kael',exact:true}).click(); await page.getByRole('button',{name:'Mira',exact:true}).click(); await page.getByRole('button',{name:'Créer le groupe',exact:true}).click();
  check('Group creation sends selected account IDs and an idempotency UUID',await page.evaluate(()=>__actions.some(a=>a.action==='createThread'&&a.payload.isGroup&&a.payload.title==='La compagnie'&&a.payload.participantAccountIds.join(',')==='92,93'&&/^[a-f0-9-]{36}$/.test(a.payload.requestId))));
  check('Messages has no local status, settings, Markdown-help or archive controls',await page.locator('#dnd-button,#settings-button,#format-button,[data-filter=archived]').count()===0);
  check('New conversation uses a plus icon and all existing conversations remain accessible',await page.locator('#new-conversation-button use').getAttribute('href')==='#i-plus'&&await page.locator('.conversation-row').count()===state.state.threads.length);
  state.state.preferences.doNotDisturb=true;await apply(state);
  check('Global DND snapshots do not recreate a local control or write another preference',await page.locator('#dnd-button,#settings-button,[role=switch]').count()===0&&await page.evaluate(()=>!__actions.some(a=>a.action==='preferences')));


  await page.locator('#composer-input').fill('Brouillon conservé'); await page.waitForTimeout(310);
  check('Draft text persists through native bridge with thread/session identity',await page.evaluate(()=>__actions.some(a=>a.action==='draft'&&a.payload.body==='Brouillon conservé'&&a.payload.threadId==='d:42:91'&&a.sessionId&&a.ownerAccountId===42)));
  await apply(state);check('Stale model draft does not overwrite fresh local input',await page.locator('#composer-input').inputValue()==='Brouillon conservé');
  await page.locator('#composer-input').press('Shift+Enter');check('Shift+Enter remains text input',await page.locator('#composer-input').inputValue().then(value=>value.includes('\n')));
  await page.locator('#composer-input').fill('Un seul envoi');await page.locator('#composer-input').press('Enter');await page.locator('#composer-input').press('Enter');
  check('Double Enter does not create duplicate in-flight logical sends',await page.evaluate(()=>__actions.filter(a=>a.action==='send'&&a.payload.body==='Un seul envoi').length===1));
  const send=await page.evaluate(()=>__actions.find(a=>a.action==='send'&&a.payload.body==='Un seul envoi'));
  await page.locator('#composer-input').fill('Un texte plus récent');await page.evaluate(send=>AtlasChat.receive({type:'result',requestId:send.requestId,payload:{accepted:true}}),send);
  check('Send acknowledgement preserves text typed after submitting',await page.locator('#composer-input').inputValue()==='Un texte plus récent');

  state.messages.push(fixtures.message('9007199254741000',fixtures.lyra,'Deux aperçus dans le même message :\nhttps://example.test/guide\nhttps://www.youtube.com/watch?v=dQw4w9WgXcQ',15,{linkPreviews:[{id:'ordinary',url:'https://example.test/guide',kind:'link',title:'Un guide utile',canRemove:true,isRemoved:false},{id:'youtube',url:'https://www.youtube.com/watch?v=dQw4w9WgXcQ',kind:'video',title:'Vidéo de test',embedUrl:'https://www.youtube.com/embed/dQw4w9WgXcQ',provider:'YouTube',canRemove:true,isRemoved:false}],attachments:[{id:'audio',fileName:'Note vocale.ogg',kind:'audio',contentType:'audio/ogg',size:'15000',url:fixtures.mediaOrigin+'attachments/audio'}]}));
  await apply(state);const rich=page.locator('[data-message-id="9007199254741000"]');await rich.scrollIntoViewIfNeeded();
  check('Multiple previews stay in source order and video never has a dismiss cross',JSON.stringify(await rich.locator('.link-preview').evaluateAll(nodes=>nodes.map(n=>[n.dataset.previewId,!!n.querySelector('.preview-dismiss')])))===JSON.stringify([['ordinary',true],['youtube',false]]));
  await rich.getByRole('button',{name:'Lire la vidéo',exact:true}).click();await page.waitForTimeout(100);
  check('YouTube uses an allowlisted frame with a real page origin and referrer policy',await rich.locator('iframe').evaluate(frame=>frame.src.startsWith('https://www.youtube-nocookie.com/embed/')&&frame.referrerPolicy==='strict-origin-when-cross-origin'&&location.origin==='https://animeclub.fr'));
  await rich.evaluate(node=>{window.__frame=node.querySelector('iframe');window.__audio=node.querySelector('audio');window.__body=node.querySelector('.message-content');});
  state.messages.at(-1).reactions=[{emoji:'🔥',accountIds:[42],count:1}]; await apply(state);
  check('Reaction update preserves media instances and the selectable body DOM',await rich.evaluate(node=>node.querySelector('iframe')===window.__frame&&node.querySelector('audio')===window.__audio&&node.querySelector('.message-content')===window.__body));
  await rich.locator('.preview-dismiss').click();check('Non-video cross requests shared deletion without deleting text',await page.evaluate(()=>__actions.some(a=>a.action==='dismissPreview'&&a.payload.messageId==='9007199254741000'&&a.payload.previewId==='ordinary')&&!__actions.some(a=>a.action==='deleteMessage')));
  state.messages.at(-1).linkPreviews[0].isRemoved=true;await apply(state);
  check('Persisted removal affects only the matching preview',await rich.locator('.link-preview').count()===1&&await rich.locator('iframe').count()===1&&await rich.locator('.message-content').innerText().then(text=>text.includes('https://example.test/guide')));
  state.isActive=false;await apply(state);check('Inactive page removes active embedded players',await rich.locator('iframe').count()===0);state.isActive=true;

  state=clone(fixtures.snapshot);state.sessionId='43e20968-4137-4f23-9174-4a097cc6a874';await apply(state);
  const incoming=page.locator('[data-message-id="9007199254740999"]');await incoming.hover();await incoming.getByRole('button',{name:'Répondre',exact:true}).click();
  check('Reply context holds the exact source message ID',await page.locator('#composer-context').isVisible()&&await page.locator('#composer-context-title').innerText().then(text=>text.includes('Aster')));
  await page.locator('#composer-input').fill('Une réponse liée');await page.locator('#send-button').click();
  check('Reply action sends its exact decimal parent ID',await page.evaluate(()=>__actions.some(a=>a.action==='send'&&a.payload.replyToMessageId==='9007199254740999'&&a.payload.body==='Une réponse liée')));
  const replySend=await page.evaluate(()=>__actions.find(a=>a.action==='send'&&a.payload.body==='Une réponse liée'));await page.evaluate(send=>AtlasChat.receive({type:'result',requestId:send.requestId,payload:{accepted:true}}),replySend);
  const own=page.locator('[data-message-id="9007199254740995"]');await own.scrollIntoViewIfNeeded();await own.hover();await own.getByRole('button',{name:'Actions du message',exact:true}).click();await page.getByRole('menuitem',{name:'Modifier',exact:true}).click();
  check('Entering message editing immediately disables native file intake',await page.evaluate(()=>__actions.filter(a=>a.action==='composerState').at(-1)?.payload.acceptsFiles===false)&&await page.locator('#attach-button').isDisabled());
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
  check('Local image preview and upload progress render before upload completion',await page.locator('.queued-file img').count()===1&&await page.locator('.queued-file-status').innerText().then(text=>text.includes('30 %')));
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
  await page.locator('#timeline').evaluate(node=>node.scrollTop=0);await page.screenshot({path:path.join(output,'chat-fr-portrait.png')});
  await page.locator('.attachment-image').first().click();await page.locator('dialog').getByRole('button',{name:'Télécharger',exact:true}).click();
  check('Image viewer retains the native attachment download action',await page.evaluate(()=>__actions.some(a=>a.action==='downloadAttachment'&&a.payload.attachmentId==='portrait')));await page.getByRole('button',{name:'Fermer',exact:true}).last().click();

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
  await page.screenshot({path:path.join(output,'chat-fr-armory-inline.png')});await page.setViewportSize({width:1032,height:720});await page.screenshot({path:path.join(output,'chat-fr-armory-compact.png')});
  check('The compact inline Armory picker keeps the composer visible without horizontal overflow',await page.evaluate(()=>document.documentElement.scrollWidth===innerWidth&&document.querySelector('#composer-input').getBoundingClientRect().bottom<=innerHeight&&document.querySelector('#armory-picker').getBoundingClientRect().top>0));await page.setViewportSize({width:1470,height:900});
  await page.locator('.armory-character[data-character-guid="4294967295"]').click();const characterSelection=await page.evaluate(()=>__actions.filter(a=>a.action==='selectOwnCharacter').at(-1));
  check('Character selection sends only the current thread and exact GUID to native validation',characterSelection.payload.threadId==='d:42:91'&&characterSelection.payload.characterGuid==='4294967295'&&Object.keys(characterSelection.payload).length===2);
  await page.locator('#composer-input').fill('Texte complété pendant la sélection.');
  const ownCard={kind:'character',title:'Asterion',referenceId:'4294967295',fields:{ownerAccountId:'42',characterGuid:'4294967295',level:'80',classId:'6',raceId:'1'}};state.draft.body='Voici mon personnage.';state.draft.card=ownCard;await apply(state);await page.evaluate(({request,card})=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{card}}),{request:characterSelection,card:ownCard});await page.waitForTimeout(310);
  check('Canonical Armory result preserves fresh text and existing upload while closing the picker',await page.locator('#composer-input').inputValue()==='Texte complété pendant la sélection.'&&await page.locator('#composer-context-body').innerText()==='Asterion'&&await page.locator('.queued-file').count()===1&&!await page.locator('#armory-picker').isVisible());
  await page.locator('#send-button').click();const armorySend=await page.evaluate(()=>__actions.filter(a=>a.action==='send'&&a.payload.card?.title==='Asterion').at(-1));
  check('Sending an Armory includes canonical ownership and retains the attachment reference',armorySend?.payload.card.fields.ownerAccountId==='42'&&armorySend.payload.card.fields.characterGuid==='4294967295'&&armorySend.payload.attachmentIds[0]==='keep-upload'&&armorySend.payload.body==='Texte complété pendant la sélection.');
  await page.evaluate(request=>AtlasChat.receive({type:'result',requestId:request.requestId,payload:{accepted:true}}),armorySend);state.draft={};state.messages=[fixtures.message('4001',fixtures.self,'',1,{card:ownCard})];state.state.threads[0].lastMessage=state.messages[0];await apply(state);await page.locator('.game-card button').click();
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

  const many=[];for(let i=1;i<=80;i++)many.push(fixtures.message(String(1000+i),i%3?fixtures.lyra:fixtures.self,'Message de test '+i+' — une ligne conservée pendant les mises à jour du fil.\nDétail de la conversation pour vérifier le défilement.',i));
  state.messages=many;state.selectedThreadId='d:42:91';state.state.threads[0].lastMessage=many.at(-1);await apply(state);
  await page.locator('#timeline').evaluate(node=>{node.scrollTop=300;});await page.waitForTimeout(50);const before=await page.locator('#timeline').evaluate(node=>node.scrollTop);
  await page.evaluate(()=>{window.__actions=[];});state.messages.push(fixtures.message('1081',fixtures.lyra,'Un nouveau message en bas.',85));await apply(state);
  check('Incoming messages preserve a reader above the bottom and do not send read',Math.abs(await page.locator('#timeline').evaluate(node=>node.scrollTop)-before)<2&&await page.evaluate(()=>!__actions.some(a=>a.action==='read')));
  const anchorBefore=await page.locator('#message-list').evaluate(node=>{const top=document.querySelector('#timeline').getBoundingClientRect().top;const message=Array.from(node.children).find(n=>n.dataset.messageId&&n.getBoundingClientRect().bottom>top+1);return{id:message.dataset.messageId,offset:message.getBoundingClientRect().top-top};});
  state.messages.unshift(...Array.from({length:15},(_,i)=>fixtures.message(String(985+i),fixtures.lyra,'Ancien message '+i+'\nDeuxième ligne',-20+i)));await apply(state);
  const anchorAfter=await page.locator(`[data-message-id="${anchorBefore.id}"]`).evaluate(node=>node.getBoundingClientRect().top-document.querySelector('#timeline').getBoundingClientRect().top);
  check('Prepending history preserves the visible message anchor',Math.abs(anchorAfter-anchorBefore.offset)<2);
  await page.locator('#jump-latest-button').click();await page.waitForTimeout(60);check('Jump to latest confirms only the actual current last ID',await page.evaluate(()=>__actions.some(a=>a.action==='read'&&a.payload.throughMessageId==='1081')));
  const beforeInactive=await page.evaluate(()=>__actions.filter(a=>a.action==='read').length);state.isActive=false;state.messages.push(fixtures.message('1082',fixtures.lyra,'Caché',86));await apply(state);check('Inactive window cannot confirm newly arrived messages',await page.evaluate(()=>__actions.filter(a=>a.action==='read').length)===beforeInactive);
  state.isActive=true;state.locale='en';await apply(state);check('English translation covers composer and thread controls',await page.locator('#composer-input').getAttribute('placeholder')==='Write a message…');await page.screenshot({path:path.join(output,'chat-en-history.png')});
  const rejected=await page.evaluate(value=>AtlasChat.applySnapshot({...value,sequence:'0'}),state);check('Older snapshot sequence is rejected',rejected===false);
  await apply({...state,sessionId:'new-session',ownerAccountId:84,selectedThreadId:null,state:{...state.state,self:{accountId:84,username:'Second'},threads:[],contacts:[]},messages:[],draft:{}});
  check('Account change clears private messages, drafts and selection',await page.locator('.message').count()===0&&await page.locator('#composer-input').inputValue()==='');
  check('No uncaught browser script errors',errors.length===0);
  await fs.writeFile(path.join(output,'results.json'),JSON.stringify({passed:checks.length,checks,errors},null,2));
  await browser.close();console.log('Chat DOM: '+checks.length+' checks passed. Headless isolated Edge, synthetic accounts, no user session.');
})().catch(async error=>{console.error(error);if(fixtureBrowser)await fixtureBrowser.close();process.exitCode=1;});
