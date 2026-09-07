const fs = require('node:fs/promises');
const { existsSync } = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');
const fixtures = require('./chat-fixtures.cjs');
const repo = path.resolve(process.env.ATLAS_CHAT_REPO_ROOT || path.join(__dirname, '../../..'));
const assets = path.join(repo, 'source/WotLK.Launcher/Assets/Chat');
const output = path.resolve(repo, process.env.ATLAS_CHAT_SEARCH_TEST_OUTPUT || 'artifacts/atlas-chat-search-20260907');
const playwright = process.env.ATLAS_CHAT_PLAYWRIGHT || path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const { chromium } = require(playwright);
const edge = process.env.ATLAS_CHAT_EDGE_PATH || [process.env['ProgramFiles(x86)'], process.env.ProgramFiles].filter(Boolean)
  .map(root => path.join(root, 'Microsoft/Edge/Application/msedge.exe')).find(existsSync);
const clone = value => JSON.parse(JSON.stringify(value));
const checks = [];
const check = (name, condition) => { assert.ok(condition, name); checks.push(name); console.log('PASS ' + name); };
let browser;
(async () => {
  await fs.mkdir(output, { recursive: true });
  browser = await chromium.launch({ executablePath: edge, headless: true, args: ['--disable-gpu', '--no-first-run', '--disable-background-networking'] });
  const page = await browser.newPage({ viewport: { width: 1597, height: 872 } });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  // Every request is intercepted: this test never accesses an account, the
  // public service, or the user's running browser/launcher.
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'https://animeclub.fr' || !url.pathname.startsWith('/atlas-messages/')) return route.abort();
    const relative = url.pathname.slice('/atlas-messages/'.length) || 'index.html';
    const target = path.resolve(assets, relative);
    if (!target.startsWith(assets + path.sep)) return route.abort();
    const contentType = target.endsWith('.js') ? 'application/javascript' : target.endsWith('.css') ? 'text/css' : target.endsWith('.ttf') ? 'font/ttf' : 'text/html';
    return route.fulfill({ status: 200, contentType, body: await fs.readFile(target) });
  });
  await page.addInitScript(() => {
    window.__actions = [];
    window.chrome = { webview: { postMessage: value => window.__actions.push(value), addEventListener: () => {} } };
  });
  await page.goto('https://animeclub.fr/atlas-messages/');
  await page.waitForFunction(() => !!window.AtlasChat);
  let sequence = 1;
  const apply = async value => {
    value.sequence = String(++sequence);
    await page.evaluate(value => AtlasChat.applySnapshot(value), value);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  const loads = () => page.evaluate(() => __actions.filter(action => action.action === 'loadEarlier'));
  const result = action => page.evaluate(action => AtlasChat.receive({ type: 'result', requestId: action.requestId, payload: null, error: action.error || null }), action);
  const find = async query => { await page.locator('#conversation-search-input').fill(query); await page.waitForTimeout(240); };
  const closed = () => page.locator('#conversation-search').evaluate(node => node.hidden);
  const marks = () => page.locator('mark.search-match').count();
  const state = clone(fixtures.snapshot);
  state.hasEarlier = false;
  state.messages = [
    fixtures.message('101', fixtures.lyra, '**École** et [éCOLE](https://example.test/ecole) · ||École secrète||', 0),
    fixtures.message('102', fixtures.self, 'Littéral a+b [test] et cœ ur. Café **au** lait.', 1),
    fixtures.message('103', fixtures.lyra, 'École supprimée', 2, { deletedAt: fixtures.at(3) }),
    fixtures.message('104', fixtures.lyra, 'École finale', 4, { attachments: [{ id: 'test-audio', kind: 'audio', fileName: 'École.wav', url: fixtures.mediaOrigin + 'attachments/fixture-audio.wav', contentType: 'audio/wav', size: '1' }] }),
    fixtures.message('105', fixtures.lyra, 'ΟΣ · cœur · 🙂 · cafe\u0301', 5)
  ];
  await apply(state);
  check('Search has no permanent opener or loupe and starts hidden', await closed() && await page.locator('.thread-header [href="#i-search"]').count() === 0);
  await page.locator('#composer-input').fill('Brouillon à garder');
  await page.locator('#composer-input').evaluate(node => node.setSelectionRange(3, 9));
  await page.keyboard.press('Control+f');
  check('Ctrl+F from the composer opens and focuses the conversation bar', !await closed() && await page.locator('#conversation-search-input').evaluate(node => node === document.activeElement));
  await page.evaluate(() => {
    window.__savedNodes = { link: document.querySelector('[data-message-id="101"] a'), spoiler: document.querySelector('.spoiler'), player: document.querySelector('audio') };
  });
  await find('ecole');
  check('Literal case/accent insensitive find excludes deleted messages and concealed spoilers', await marks() === 3 && await page.locator('#conversation-search-count').innerText() === '1 / 3');
  check('Coverage explicitly confirms only a fully loaded history', await page.locator('#conversation-search-status').innerText().then(text => text.includes('Tout l’historique parcouru')));
  await page.locator('#conversation-search-input').press('Enter');
  check('Enter moves forward without sending the composer draft', await page.locator('#conversation-search-count').innerText() === '2 / 3' && await page.evaluate(() => !__actions.some(action => action.action === 'send')));
  await page.locator('#conversation-search-input').press('Shift+Enter');
  await page.locator('#conversation-search-input').press('Shift+Enter');
  check('Shift+Enter moves backward and wraps within known results', await page.locator('#conversation-search-count').innerText() === '3 / 3');
  await page.locator('#conversation-search-input').dispatchEvent('keydown', { key: 'Enter', isComposing: true, keyCode: 229 });
  await page.locator('#conversation-search-input').dispatchEvent('keydown', { key: 'Escape', isComposing: true, keyCode: 229 });
  check('IME composition Enter/Escape neither navigates nor closes the search', !await closed() && await page.locator('#conversation-search-count').innerText() === '3 / 3');
  await page.locator('.spoiler').click();
  check('A spoiler is included only after its existing explicit reveal action', await marks() === 4 && await page.locator('.spoiler').getAttribute('aria-expanded') === 'true');
  await find('cafe au lait');
  check('A phrase spans Markdown nodes while preserving formatting', await marks() === 3 && await page.locator('#conversation-search-count').innerText() === '1 / 1' && await page.locator('[data-message-id="102"] strong').innerText() === 'au');
  await find('a+b [test]');
  check('Regex punctuation is searched literally', await marks() === 1);
  await find('ος'); check('Greek final sigma folds consistently between the query and indexed text', await marks() === 1 && await page.locator('mark.search-match').innerText() === 'ΟΣ');
  await find('coeur'); check('Ligatures preserve the original text in a single highlight', await marks() === 1 && await page.locator('mark.search-match').innerText() === 'cœur');
  await find('🙂'); check('Supplementary Unicode characters preserve their surrogate pairs', await marks() === 1 && await page.locator('mark.search-match').innerText() === '🙂');
  await find('café'); check('Decomposed and precomposed accents share the same search results', await marks() === 2);
  await find('zz absent');
  check('No-match status and disabled navigation remain explicit', await page.locator('#conversation-search-count').innerText() === 'Aucun résultat' && await page.locator('#conversation-search-next').isDisabled());
  check('Find does not recreate links, spoiler listeners or audio nodes', await page.evaluate(() => __savedNodes.link === document.querySelector('[data-message-id="101"] a') && __savedNodes.spoiler === document.querySelector('.spoiler') && __savedNodes.player === document.querySelector('audio')));
  await page.keyboard.press('Escape');
  check('Escape removes marks, restores composer focus and preserves the draft', await closed() && await marks() === 0 && await page.locator('#composer-input').inputValue() === 'Brouillon à garder' && await page.locator('#composer-input').evaluate(node => node === document.activeElement));
  check('Returning to the composer preserves its text selection', await page.locator('#composer-input').evaluate(node => node.selectionStart === 3 && node.selectionEnd === 9));
  await page.locator('#new-conversation-button').click();
  await page.keyboard.press('Control+f');
  check('Ctrl+F does not steal focus from a modal dialog', await closed() && await page.locator('#app-dialog').evaluate(node => node.open && node.contains(document.activeElement)));
  await page.keyboard.press('Escape');

  // History results and bridge snapshots can arrive in either order. Exercise
  // both orders and confirm one exact Int64 cursor at a time.
  state.messages = [fixtures.message('9007199254741099', fixtures.lyra, 'Récent sans correspondance', 10)];
  state.hasEarlier = true;
  await apply(state); await page.locator('#composer-input').focus(); await page.keyboard.press('Control+f'); await find('archive');
  await page.waitForFunction(() => __actions.some(action => action.action === 'loadEarlier'));
  let dispatched = await loads(); let first = dispatched.at(-1);
  check('Find requests older history with its exact current thread and Int64 cursor', first.payload.threadId === state.selectedThreadId && first.payload.beforeId === '9007199254741099');
  check('Pending pagination displays partial coverage instead of false full results', await page.locator('#conversation-search-status').innerText().then(text => text.includes('Historique partiel')));
  await find('autre'); await find('archive'); await result(first); await page.waitForTimeout(180);
  check('Query changes and early bridge results do not overlap history requests', (await loads()).length === dispatched.length);
  state.isLoadingEarlier = true; await apply(state);
  state.messages.unshift(fixtures.message('9007199254741050', fixtures.lyra, 'Archive ancienne', 0)); state.isLoadingEarlier = false;
  await apply(state); await page.waitForFunction(count => __actions.filter(action => action.action === 'loadEarlier').length > count, dispatched.length);
  dispatched = await loads(); const second = dispatched.at(-1);
  check('Find includes a newly loaded older match and advances the cursor sequentially', await marks() === 1 && second.payload.beforeId === '9007199254741050' && dispatched.length === 2);
  state.messages.unshift(fixtures.message('9007199254741000', fixtures.lyra, 'Archive très ancienne', -10)); state.hasEarlier = false;
  await apply(state); await result(second);
  check('Final snapshot before its result completes accurate history coverage', await marks() === 2 && await page.locator('#conversation-search').getAttribute('data-coverage') === 'complete');
  await page.screenshot({ path: path.join(output, 'chat-search-history.png'), omitBackground: true });
  check('The search bar keeps the fixed launcher free of horizontal overflow', await page.evaluate(() => document.documentElement.scrollWidth === innerWidth && document.querySelector('#conversation-search-close').getBoundingClientRect().right <= innerWidth));
  state.locale = 'en'; await apply(state);
  check('Live locale updates translate search status and accessible controls', await page.locator('#conversation-search-status').innerText().then(text => text.includes('Entire history searched')) && await page.locator('#conversation-search-next').getAttribute('aria-label') === 'Next result (Enter)');
  state.locale = 'fr'; await apply(state);

  // Failed pages keep the partial results and offer a user-controlled retry.
  state.hasEarlier = true; await apply(state); await find('archive');
  await page.waitForFunction(count => __actions.filter(action => action.action === 'loadEarlier').length > count, dispatched.length);
  let failed = (await loads()).at(-1); await result({ ...failed, error: 'http-503' });
  check('History failure retains partial results and exposes Retry', await marks() === 2 && await page.locator('#conversation-search-continue').isVisible() && await page.locator('#conversation-search-status').innerText().then(text => text.includes('interrompu') && text.includes('partiel')));
  await page.keyboard.press('Escape');
  check('Closing a settled search restores the manual older-history button immediately', !await page.locator('#load-earlier-button').isDisabled());
  const retryStart = (await loads()).length;
  await page.keyboard.press('Control+f'); await find('archive');
  await page.waitForFunction(total => __actions.filter(action => action.action === 'loadEarlier').length > total, retryStart);
  await result({ ...(await loads()).at(-1), error: 'http-503' });
  const beforeRetry = (await loads()).length;
  await page.locator('#conversation-search-continue').click();
  check('Retry issues one new request after the failed request ended', (await loads()).length === beforeRetry + 1);
  const retry = (await loads()).at(-1);
  await page.keyboard.press('Escape'); await result(retry); await page.waitForTimeout(200);
  check('Closing stops further page scheduling while an existing request settles', await closed() && (await loads()).length === beforeRetry + 1);
  check('Closing before a page settles keeps manual loading locked against overlap', await page.locator('#load-earlier-button').isDisabled());
  await page.keyboard.press('Control+f'); await find('archive');
  check('Reopening the same thread cannot overlap an unresolved request', (await loads()).length === beforeRetry + 1);
  state.hasEarlier = false; await apply(state);
  check('A late snapshot can safely settle the pending page after reopen', await page.locator('#conversation-search').getAttribute('data-coverage') === 'complete');

  // Search positioning must never mark a newly received, unseen tail as read,
  // including queued resize/scroll frames after Escape.
  const unreadId = '9007199254741199';
  state.messages.push(fixtures.message(unreadId, fixtures.lyra, 'Dernier inédit', 30));
  await apply(state); await find('Dernier inédit'); await page.keyboard.press('Escape'); await page.waitForTimeout(180);
  check('Search jumps and closing do not emit a read cursor for an unseen tail', !await page.evaluate(id => __actions.some(action => action.action === 'read' && action.payload.throughMessageId === id), unreadId));
  await page.locator('#timeline').dispatchEvent('wheel', { deltaY: 40 });
  await page.locator('#timeline').evaluate(node => { node.scrollTop = node.scrollHeight; node.dispatchEvent(new Event('scroll')); });
  await page.waitForTimeout(80);
  check('Explicit user scrolling restores normal read behavior after search', await page.evaluate(id => __actions.some(action => action.action === 'read' && action.payload.throughMessageId === id), unreadId));

  await page.keyboard.press('Control+f'); await find('archive');
  const other = clone(state); other.selectedThreadId = 'd:42:92'; other.messages = [fixtures.message('8', fixtures.kael, 'Autre fil', 0, { threadId: 'd:42:92' })];
  await apply(other);
  check('Changing conversation clears the query, marks and search surface', await closed() && await marks() === 0 && await page.locator('#conversation-search-input').inputValue() === '');
  await page.keyboard.press('Control+f'); await find('Autre fil');
  other.sessionId = '098f6829-54ea-45e2-b7a9-af823d03e97d'; other.ownerAccountId = 99; other.selectedThreadId = null; other.messages = []; other.state.threads = []; other.state.contacts = []; other.state.self = { accountId: 99, username: 'New account' };
  await apply(other); await result(first);
  check('Account switch discards all search state and ignores stale results', await closed() && await marks() === 0 && await page.locator('#conversation-search-input').inputValue() === '');

  // Long histories are intentionally bounded per batch, with honest partial
  // coverage and an explicit continuation instead of unbounded API requests.
  state.messages = [fixtures.message('100000', fixtures.lyra, 'Batch récent', 0)]; state.hasEarlier = true;
  const startBatch = (await loads()).length;
  await apply(state); await page.locator('#composer-input').focus(); await page.keyboard.press('Control+f'); await find('batch');
  for (let index = 0; index < 20; index++) {
    await page.waitForFunction(total => __actions.filter(action => action.action === 'loadEarlier').length >= total, startBatch + index + 1);
    const load = (await loads()).at(-1);
    state.messages.unshift(fixtures.message(String(99999 - index), fixtures.lyra, 'Batch ' + index, -index - 1));
    await apply(state); await result(load);
  }
  await page.waitForTimeout(180);
  check('A long history pauses after 20 sequential pages with an honest continuation', (await loads()).length === startBatch + 20 && await page.locator('#conversation-search-continue').isVisible() && await page.locator('#conversation-search').getAttribute('data-coverage') === 'partial');
  await page.locator('#conversation-search-continue').click();
  check('Continue resumes from the oldest exact cursor after the bounded batch', (await loads()).length === startBatch + 21 && (await loads()).at(-1).payload.beforeId === '99980');
  const lastBatch = (await loads()).at(-1); await result({ ...lastBatch, error: 'http-503' }); await page.keyboard.press('Escape');

  await page.clock.install();
  const beforeTimed = (await loads()).length;
  await page.keyboard.press('Control+f'); await find('batch');
  await page.waitForFunction(total => __actions.filter(action => action.action === 'loadEarlier').length > total, beforeTimed);
  const timedLoad = (await loads()).at(-1), timedCount = (await loads()).length;
  await page.clock.fastForward(15100);
  check('A missing bridge result reports waiting without permitting overlapping retry', (await loads()).length === timedCount && await page.locator('#conversation-search-status').innerText().then(text => text.includes('Réponse en attente') && text.includes('partiel')) && !await page.locator('#conversation-search-continue').isVisible());
  await result(timedLoad);
  check('A timed-out result without history progress permits a safe explicit retry', await page.locator('#conversation-search-continue').isVisible() && await page.locator('#conversation-search-status').innerText().then(text => text.includes('interrompu')));
  await page.keyboard.press('Escape');
  check('Real assets completed all search flows without JavaScript errors', errors.length === 0);
  await fs.writeFile(path.join(output, 'report.json'), JSON.stringify({ checks, errors, browser: browser.version() }, null, 2));
  console.log(JSON.stringify({ passed: checks.length, output }));
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => { if (browser) await browser.close(); });
