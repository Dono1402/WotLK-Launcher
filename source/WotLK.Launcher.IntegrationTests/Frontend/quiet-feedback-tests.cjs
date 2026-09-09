const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const { existsSync } = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const repo = path.resolve(__dirname, '../../..');
const output = path.join(repo, 'artifacts/quiet-feedback/profile');
const playwright = process.env.ATLAS_CHAT_PLAYWRIGHT ||
  path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const { chromium } = require(playwright);
const edge = process.env.ATLAS_CHAT_EDGE_PATH || [process.env['ProgramFiles(x86)'], process.env.ProgramFiles]
  .filter(Boolean).map(root => path.join(root, 'Microsoft/Edge/Application/msedge.exe')).find(existsSync);
let passed = 0;
const check = (name, result) => { assert.ok(result, name); passed++; console.log('PASS ' + name); };
let browser;
(async () => {
  await fs.mkdir(output, { recursive: true });
  browser = await chromium.launch({ executablePath: edge, headless: true, args: ['--disable-gpu', '--disable-background-networking'] });
  for (const locale of ['fr', 'en']) {
    const page = await browser.newPage({ viewport: { width: locale === 'fr' ? 1598 : 1080, height: 872 } });
    let roster = { status: 'loading', characters: [] };
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.__actions = []; window.__receivers = [];
      window.chrome = { webview: {
        postMessage: value => window.__actions.push(value),
        addEventListener: (_, handler) => window.__receivers.push(handler)
      } };
      window.__receive = value => window.__receivers.forEach(handler => handler({ data: value }));
    });
    await page.route('**/*', async route => {
      const url = new URL(route.request().url());
      const files = {
        '/': ['launcher.html', 'text/html'], '/launcher.js': ['launcher.js', 'text/javascript'],
        '/launcher.css': ['launcher.css', 'text/css'], '/character-labels.mjs': ['character-labels.mjs', 'text/javascript']
      };
      if (url.pathname === '/characters.json') return route.fulfill({ json: roster });
      if (url.pathname === '/lucide.js') return route.fulfill({ contentType: 'text/javascript', body: 'window.lucide={createIcons(){}}' });
      if (files[url.pathname]) {
        const [file, contentType] = files[url.pathname];
        return route.fulfill({ contentType, body: await fs.readFile(path.join(repo, 'prototypes/armory-3d', file)) });
      }
      return route.fulfill({ status: 200, contentType: 'text/plain', body: '' });
    });
    await page.goto('https://atlas.test/?lang=' + locale);
    const receive = value => page.evaluate(message => window.__receive(message), value);
    let profile = { type: 'profile', locale, username: 'Asterion', presence: 'online', presenceLabel: locale === 'fr' ? 'En ligne' : 'Online',
      statusMessage: 'Statut initial', bio: 'Description initiale', canUpdateSocialProfile: true, canModifyAvatar: true, canModifyBanner: true };
    await receive(profile);
    await page.waitForTimeout(500);
    check(locale + ': initial roster has one accessible indicator and no loading sentence',
      await page.locator('#roster-busy').isVisible() && await page.locator('#roster-status').innerText() === ''
      && !await page.locator('#empty-state').isVisible()
      && Number(await page.locator('#roster-busy').evaluate(node => getComputedStyle(node).opacity)) > .95);

    const saveText = locale === 'fr' ? 'Enregistrer' : 'Save';
    await page.locator('#edit-profile').click();
    await page.locator('#edit-status').fill('Nouveau statut');
    const before = await page.locator('#save-profile').boundingBox();
    await page.locator('#save-profile').click();
    const after = await page.locator('#save-profile').boundingBox();
    check(locale + ': saving keeps label, bounds and disabled duplicate protection',
      await page.locator('#save-profile').innerText() === saveText && await page.locator('#save-profile').isDisabled()
      && await page.locator('#save-profile').getAttribute('aria-busy') === 'true'
      && before.width === after.width && before.height === after.height);
    await receive({ ...profile, profileBusy: true });
    await page.waitForTimeout(480);
    check(locale + ': delayed save ring becomes visible',
      Number(await page.locator('#save-profile').evaluate(node => getComputedStyle(node, '::after').opacity)) > .95);
    await page.screenshot({ path: path.join(output, 'profile-' + locale + '-saving.png') });
    profile = { ...profile, statusMessage: 'Nouveau statut', profileBusy: false, profileNotice: 'confirmed' };
    await receive(profile);
    check(locale + ': confirmed save updates content and closes the editor without a success banner',
      !await page.locator('#profile-editor').isVisible() && await page.locator('#profile-status').innerText() === 'Nouveau statut'
      && !await page.locator('#profile-notice').isVisible());

    await page.locator('#edit-profile').click();
    await page.locator('#edit-bio').fill('Texte à conserver après erreur');
    await page.locator('#save-profile').click();
    await receive({ type: 'profile-save-result', accepted: false, message: 'Échec témoin' });
    check(locale + ': rejected save retains the draft, shows the error and restores retry',
      await page.locator('#notification-text').innerText() === 'Échec témoin' && await page.locator('#profile-notice').isVisible()
      && await page.locator('#edit-bio').inputValue() === 'Texte à conserver après erreur' && await page.locator('#save-profile').isEnabled());
    await page.locator('#dismiss-notification').click();
    await page.locator('#cancel-profile').click();

    const image = await page.evaluate(() => {
      const canvas = document.createElement('canvas'); canvas.width = 800; canvas.height = 300;
      const context = canvas.getContext('2d'); context.fillStyle = '#263f56'; context.fillRect(0, 0, 800, 300);
      return canvas.toDataURL('image/png');
    });
    await receive({ type: 'banner-selected', image });
    await page.waitForFunction(() => document.getElementById('banner-crop-image').naturalWidth > 0);
    const bannerBefore = await page.locator('#save-banner').boundingBox();
    await page.locator('#save-banner').click();
    await page.emulateMedia({ reducedMotion: 'reduce' });
    const bannerAfter = await page.locator('#save-banner').boundingBox();
    check(locale + ': banner save keeps geometry and a visible nonrotating reduced-motion indicator',
      bannerBefore.width === bannerAfter.width && bannerBefore.height === bannerAfter.height
      && await page.locator('#save-banner').innerText() === (locale === 'fr' ? 'Appliquer' : 'Apply')
      && await page.locator('#save-banner').evaluate(node => getComputedStyle(node, '::after').opacity === '1'
        && getComputedStyle(node, '::after').animationName === 'none'));
    await receive({ type: 'banner-save-result', accepted: false, completed: true, error: 'Bannière indisponible' });
    check(locale + ': failed banner save preserves editor and its local error',
      await page.locator('#banner-editor').isVisible() && await page.locator('#notification-text').innerText() === 'Bannière indisponible');
    await page.locator('#save-banner').click();
    await receive({ type: 'banner-save-result', accepted: true, succeeded: true, completed: true });
    check(locale + ': successful banner save closes quietly and commits the image',
      !await page.locator('#banner-editor').isVisible() && !await page.locator('#profile-notice').isVisible()
      && await page.locator('.banner-image').getAttribute('src') === image);

    roster = { status: 'cached', refreshing: false, characters: [{ id: '42', name: 'Asterion', classId: 6, level: 80, available: false }] };
    await page.waitForFunction(() => document.querySelectorAll('.character').length === 1);
    check(locale + ': pending character keeps its row and uses a quiet indicator',
      await page.locator('#character-busy').isVisible() && !await page.locator('#empty-state').isVisible()
      && await page.locator('#roster-status').isVisible());
    roster = { ...roster, refreshing: true };
    await page.locator('#retry').click();
    await page.waitForFunction(() => document.getElementById('characters').getAttribute('aria-busy') === 'true');
    check(locale + ': refresh keeps cached characters and the stable retry label',
      await page.locator('.character').count() === 1
      && await page.locator('#retry').innerText() === (locale === 'fr' ? 'Réessayer' : 'Retry'));
    check(locale + ': no horizontal overflow or browser errors',
      await page.evaluate(() => document.documentElement.scrollWidth === innerWidth) && errors.length === 0);
    await page.close();
  }
  console.log(JSON.stringify({ passed, output }));
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => { await browser?.close(); });
