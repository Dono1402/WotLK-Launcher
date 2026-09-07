const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { resolveDetails, parseEnchantments, spellText } = require('../item-details.cjs');
const { readViewerConfig } = require('../viewer-config.cjs');
const { createServer } = require('../server.cjs');
const output = path.resolve(__dirname, '../../../artifacts/armory-prototype');
const read = async name => JSON.parse(await fs.readFile(path.join(output,name),'utf8'));
async function fixture() {
  return {
    snapshot:await read('flowmage.json'), catalog:await read('item-catalog.json'),
    tables:{fr:await read('item-tables-fr.json'), en:await read('item-tables-en.json')}
  };
}

test('localized details preserve actual instance stats without double counting', async () => {
  const {snapshot,catalog,tables} = await fixture();
  const items = resolveDetails(snapshot,catalog,tables);
  const publicData = await read('assets/item-details.json');
  assert.deepEqual(publicData.items.map(({slot,...item}) => item),items.map(({slot,...item}) => item));
  assert.deepEqual(items.map(item => item.slot),snapshot.equipment.map(item => item.slot));
  assert.equal(publicData.characterCapturedAt,snapshot.capturedAtUtc);
  assert.equal(items.length,13);
  assert.ok(items.every(i => i.name.fr && i.name.en));
  assert.ok(!/"(guid|email|account|password|token|owner)"/i.test(JSON.stringify(publicData)));
  const robe = items.find(i => i.itemId===6569);
  assert.equal(robe.name.en,'Shimmering Robe of the Eagle');
  assert.equal(robe.name.fr,"Robe chatoyante de l'aigle");
  assert.deepEqual(robe.stats,[{type:5,value:6},{type:7,value:6}]);
  assert.equal(robe.armor,39);
  const legs = items.find(i => i.itemId===14125);
  assert.deepEqual(legs.stats,[{type:5,value:4},{type:6,value:4}]);
  assert.equal(legs.name.fr,'Jambières rituelles de la chouette');
  assert.deepEqual(items.find(i => i.itemId===28303).stats,[{type:5,value:3},{type:32,value:2}]);
});

test('spell power comes from the verified effect, not unresolved template text', async () => {
  const {snapshot,catalog,tables} = await fixture();
  const shoulder = resolveDetails(snapshot,catalog,tables).find(i => i.itemId===3748);
  assert.equal(shoulder.effects.length,1);
  for (const locale of ['fr','en']) {
    assert.match(shoulder.effects[0].description[locale],/\b2\b/);
    assert.ok(!shoulder.effects[0].description[locale].includes('$'));
  }
  assert.throws(() => spellText(123,tables.fr),/Unverified/);
  const changed = structuredClone(tables.fr);
  changed.spellEffects.find(e => e.SpellID===9393 && e.EffectIndex===0 && e.DifficultyID===0).EffectRealPointsPerLevel = 1;
  assert.throws(() => spellText(9393,changed),/formula/);
});

test('unsupported or inconsistent instances fail explicitly', async () => {
  assert.throws(() => parseEnchantments('0 0'),/Invalid/);
  assert.throws(() => parseEnchantments(Array(36).fill(-1).join(' ')),/Invalid/);
  assert.equal(parseEnchantments(Array(36).fill(0).join(' ')).length,12);
  const {snapshot,catalog,tables} = await fixture();
  const wrong = structuredClone(snapshot);
  wrong.equipment.find(i => i.itemId===6569).enchantments = Array(36).fill(0).join(' ');
  assert.throws(() => resolveDetails(wrong,catalog,tables),/differs/);
  const scaled = structuredClone(catalog);
  scaled.items[0].scalingDistribution = 1;
  assert.throws(() => resolveDetails(snapshot,scaled,tables),/Scaling/);
  const suffix = structuredClone(snapshot);
  suffix.equipment[0].randomPropertyId = -1;
  assert.throws(() => resolveDetails(suffix,catalog,tables),/Scaling/);
});

test('language selection honors preview, launcher, browser and fallback in order', async () => {
  const {chooseLocale,normalizeLocale,t,slotNames} = await import('../i18n.mjs');
  assert.equal(chooseLocale('en-GB','fr-FR',['fr-FR']),'en');
  assert.equal(chooseLocale(null,'fr-FR',['en-US']),'fr');
  assert.equal(chooseLocale(null,null,['de-DE','fr-CA']),'fr');
  assert.equal(chooseLocale(null,null,['de-DE']),'en');
  assert.equal(normalizeLocale('EN_us'),'en');
  assert.equal(normalizeLocale('french'),null);
  for (const locale of ['fr','en']) {
    assert.equal(slotNames[locale].length,19);
    assert.ok(t(locale,'close'));
    assert.ok(!t(locale,'snapshot',{date:'test'}).includes('{'));
  }
});

test('tooltip lines show localized armor, stats, weapon speed and damage', async () => {
  const {itemLines,itemType} = await import('../i18n.mjs');
  const {items} = await read('assets/item-details.json');
  const staff = items.find(i => i.itemId===22980);
  const en = itemLines(staff,'en').map(l => l.text);
  const fr = itemLines(staff,'fr').map(l => l.text);
  for (const text of ['44 - 66 damage','Speed 3.00','18.3 damage per second','+10 Intellect','+4 Spirit']) assert.ok(en.includes(text),text);
  for (const text of ['44 - 66 dégâts','Vitesse 3,00','18,3 dégâts par seconde','+10 Intelligence','+4 Esprit']) assert.ok(fr.includes(text),text);
  assert.equal(itemType(staff,'fr'),'Bâton · Deux mains');
  assert.ok(itemLines(items.find(i => i.itemId===5252),'en').some(l => l.text==='16 - 31 damage (Shadow)'));
  assert.ok(itemLines(items.find(i => i.itemId===6569),'fr').some(l => l.text==='39 Armure'));
  assert.equal(itemLines(null,'en')[0].text,'Item details unavailable');
});

test('primary attributes, secondary bonuses and passives have distinct color roles', async () => {
  const {itemLines} = await import('../i18n.mjs');
  const {items} = await read('assets/item-details.json');
  for (const locale of ['fr','en']) {
    const ring = itemLines(items.find(i => i.itemId===28303),locale);
    assert.equal(ring.find(l => l.text.startsWith('+3')).kind,'stat');
    const critical = ring.find(l => /crit/i.test(l.text));
    assert.equal(critical.kind,'effect');
    assert.equal(critical.text,locale==='fr'?'Équipé : Augmente le score de coup critique de 2.':'Equip: Improves critical strike rating by 2.');
    const shoulders = itemLines(items.find(i => i.itemId===3748),locale);
    assert.equal(shoulders.filter(l => l.kind==='effect').length,1);
    assert.match(shoulders.find(l => l.kind==='effect').text,/\b2\b/);
  }
  const penalty = structuredClone(items.find(i => i.itemId===28303));
  penalty.stats = [{type:5,value:-3}];
  assert.equal(itemLines(penalty,'en').find(l => l.text==='-3 Intellect').kind,'penalty');
});

test('launcher settings are read-only, prefer local, and expose only locale', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-locale-test-'));
  const local = path.join(root,'Atlas Launcher Local','settings.json');
  const installed = path.join(root,'WotLK Launcher','settings.json');
  try {
    assert.deepEqual(await readViewerConfig(root),{locale:null,source:'browser'});
    await fs.mkdir(path.dirname(installed));
    await fs.writeFile(installed,JSON.stringify({InterfaceLocale:'en-US',token:'test-only'}));
    assert.deepEqual(await readViewerConfig(root),{locale:'en',source:'launcher'});
    await fs.mkdir(path.dirname(local));
    const original = '\uFEFF'+JSON.stringify({InterfaceLocale:'fr-FR',secret:'test-only'});
    await fs.writeFile(local,original);
    assert.deepEqual(await readViewerConfig(root),{locale:'fr',source:'launcher-local'});
    assert.equal(await fs.readFile(local,'utf8'),original);
    await fs.writeFile(local,'{}');
    assert.equal((await readViewerConfig(root)).locale,'fr');
    for (const invalid of ['broken','null','[]']) {
      await fs.writeFile(local,invalid);
      assert.equal((await readViewerConfig(root)).source,'launcher');
    }
  } finally {
    // Only remove the explicitly created test files and their now-empty directories.
    await fs.unlink(local).catch(() => {});
    await fs.unlink(installed).catch(() => {});
    await fs.rmdir(path.dirname(local)).catch(() => {});
    await fs.rmdir(path.dirname(installed)).catch(() => {});
    await fs.rmdir(root);
  }
});

test('viewer config HTTP response never includes other settings', async () => {
  const server = createServer({getViewerConfig:async () => ({locale:'en',source:'launcher-local',token:'not-public'})});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const url = `http://127.0.0.1:${server.address().port}/viewer-config.json`;
  try {
    const response = await fetch(url);
    assert.deepEqual(await response.json(),{locale:'en',source:'launcher-local'});
    assert.equal(response.headers.get('cache-control'),'no-store');
    assert.equal(await (await fetch(url,{method:'HEAD'})).text(),'');
    assert.equal((await fetch(url,{method:'POST'})).status,405);
  } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});
