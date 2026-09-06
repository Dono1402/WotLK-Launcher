const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const {combatDetails} = require('./fixtures/combat.cjs');
const {sanitizeCombatDetails} = require('../combat-statistics.cjs');
const {buildCombatStatistics} = require('../capture-statistics.cjs');
const {readStatistics} = require('../statistics-cache.cjs');
const {createServer} = require('../server.cjs');

test('all WotLK classes receive appropriate categories, not mage rows by default',async () => {
  const {statisticsModes,defaultStatisticsMode,characterStatsRows} = await import('../character-stats.mjs');
  const expected = {1:'melee',2:'melee',3:'ranged',4:'melee',5:'spell',6:'melee',7:'spell',8:'spell',9:'spell',11:'spell'};
  for (const [id,mode] of Object.entries(expected)) {
    const character = {classId:Number(id)};
    assert.equal(defaultStatisticsMode(character),mode);
    for (const locale of ['fr','en']) {
      for (const choice of statisticsModes(character,locale)) {
        const rows = characterStatsRows(character,locale,choice.key);
        assert.ok(rows.length>=6 && rows.length<=8);
        assert.ok(rows.every(row => row.label && !row.known));
        if (['melee','ranged','defense'].includes(choice.key)) assert.ok(rows.every(row => !row.key.startsWith('spell')));
      }
    }
  }
  assert.equal(characterStatsRows({classId:3},'en','spell')[0].key,'agility');
  assert.equal(defaultStatisticsMode({classId:99}),'base');
});

test('active talents and druid form select a category without guessing a tank DK',async () => {
  const {defaultStatisticsMode:mode,characterStatsRows} = await import('../character-stats.mjs');
  const character = {classId:2,capturedAt:'same',statistics:{characterCapturedAt:'same',talentPoints:[20,0,0],form:0}};
  assert.equal(mode(character),'healing');
  character.statistics.talentPoints = [0,20,0];
  assert.equal(mode(character),'defense');
  character.classId = 7;
  assert.equal(mode(character),'melee');
  character.classId = 11;
  character.statistics.form = 8;
  assert.equal(mode(character),'defense');
  assert.ok(!characterStatsRows(character,'fr').some(row => ['parryPct','blockPct'].includes(row.key)));
  character.statistics.form = 1;
  assert.equal(mode(character),'melee');
  character.classId = 6;
  assert.equal(mode(character),'melee');
  assert.ok(!characterStatsRows(character,'fr','defense').some(row => row.key==='blockPct'));
  character.classId = 2;
  character.statistics.characterCapturedAt = 'stale';
  assert.equal(mode(character),'melee');
  character.statistics.characterCapturedAt = 'same';
  character.statistics.talentPoints = [10,10,0];
  assert.equal(mode(character),'melee');
});

test('schools stay distinct, all-school minimum is explicit, negative haste is preserved',async () => {
  const {characterStatsRows:rows} = await import('../character-stats.mjs');
  const character = {classId:8,capturedAt:'same',statistics:{source:'arthas-combat-stats',characterCapturedAt:'same',...combatDetails()}};
  const get = (school,locale='en') => Object.fromEntries(rows(character,locale,'spell',school).map(row => [row.key,row]));
  assert.equal(get(0).spellPower.value,'50');
  assert.equal(get(0).spellCritPct.value,'2.0%');
  assert.match(get(0).spellPower.hint,/Minimum/);
  assert.equal(get(4).spellPower.value,'53');
  assert.equal(get(4,'fr').spellCritPct.value,'5,0\u00a0%');
  assert.equal(get(4).spellPower.hint,'');
  character.statistics.values.spellHastePct = -20;
  assert.equal(get(4).spellHastePct.value,'-20.0%');
  assert.equal(get(4).spellHastePct.negative,true);
  assert.ok(!get(8).spellPower.known);
  character.statistics.characterCapturedAt = 'old';
  assert.ok(rows(character,'en').every(row => !row.known));
});

test('combat payload is complete, typed and stripped of unknown fields',() => {
  const details = combatDetails();
  details.values.password = 'secret';
  details.schools[0].token = 'secret';
  const clean = sanitizeCombatDetails(details);
  assert.ok(!JSON.stringify(clean).includes('secret'));
  details.schools[1].id = 1;
  assert.throws(() => sanitizeCombatDetails(details),/school/);
  const missing = combatDetails();
  delete missing.values.spellHitPct;
  assert.throws(() => sanitizeCombatDetails(missing),/spellHitPct/);
  missing.values.spellHitPct = '0';
  assert.throws(() => sanitizeCombatDetails(missing),/spellHitPct/);
  missing.values.spellHitPct = -2;
  assert.equal(sanitizeCombatDetails(missing).values.spellHitPct,-2);
  missing.values.spellHastePct = -100;
  assert.throws(() => sanitizeCombatDetails(missing),/spellHastePct/);
  const context = combatDetails();
  context.includesTemporaryEffects = false;
  assert.throws(() => sanitizeCombatDetails(context),/context/);
});

test('combat import binds full equipment and identity to a confirmed post-activation snapshot',async () => {
  const baseline = JSON.parse(await fs.readFile(path.resolve(__dirname,'../../../artifacts/armory-prototype/flowmage.json'),'utf8'));
  const snapshot = {schemaVersion:1,source:'atlas-armory-engine',reason:'logout',capturedAtMs:Date.parse('2026-09-05T12:00:00Z'),character:structuredClone(baseline.character),equipment:structuredClone(baseline.equipment),...combatDetails()};
  const current = {observedAtUtc:'2026-09-05 12:01:00.000000',snapshot};
  const after = '2026-09-05T11:00:00Z';
  const result = buildCombatStatistics(baseline,current,after);
  assert.equal(result.record.schemaVersion,2);
  assert.equal(result.record.savedAt,'2026-09-05T12:00:00.000Z');
  assert.ok(!/"(guid|enchantments|account)"/.test(JSON.stringify(result)));
  assert.equal(buildCombatStatistics(baseline,{...current,snapshot:null},after).reason,'missing-combat-snapshot');
  assert.equal(buildCombatStatistics(baseline,current,undefined).status,'unavailable');
  assert.equal(buildCombatStatistics(baseline,current,'2026-09-05T12:01:00Z').status,'unavailable');
  snapshot.capturedAtMs += 120000;
  assert.throws(() => buildCombatStatistics(baseline,current,after),/date/);
  snapshot.capturedAtMs -= 120000;
  snapshot.equipment[0].randomPropertyId++;
  assert.throws(() => buildCombatStatistics(baseline,current,after),/Equipment changed/);
  snapshot.equipment = structuredClone(baseline.equipment);
  snapshot.character.classId = 1;
  assert.throws(() => buildCombatStatistics(baseline,current,after),/Character changed/);
});

test('live capture reasons retain date, equipment and identity validation while online',async () => {
  const baseline = JSON.parse(await fs.readFile(path.resolve(__dirname,'../../../artifacts/armory-prototype/flowmage.json'),'utf8'));
  const snapshot = {schemaVersion:1,source:'atlas-armory-engine',capturedAtMs:Date.parse('2026-09-05T12:00:00Z'),
    character:{...structuredClone(baseline.character),online:1},equipment:structuredClone(baseline.equipment),...combatDetails()};
  const current = {observedAtUtc:'2026-09-05 12:01:00.000000',snapshot};
  const after = '2026-09-05T11:00:00Z';
  for (const reason of ['logout','login','equipment','periodic']) {
    snapshot.reason = reason;
    const result = buildCombatStatistics(baseline,current,after);
    assert.equal(result.status,'ready');
    assert.equal(result.record.savedAt,'2026-09-05T12:00:00.000Z');
    assert.ok(!/"(online|reason|guid)"/.test(JSON.stringify(result.record)));
    assert.equal(buildCombatStatistics(baseline,current,'2026-09-05T12:01:00Z').reason,'new-capture-required-after-enabling-collection');
    snapshot.character.guid++;
    assert.throws(() => buildCombatStatistics(baseline,current,after),{code:'ARMORY_REFRESH_REQUIRED'});
    snapshot.character.guid--;
    snapshot.equipment[0].randomPropertyId++;
    assert.throws(() => buildCombatStatistics(baseline,current,after),{code:'ARMORY_REFRESH_REQUIRED'});
    snapshot.equipment[0].randomPropertyId--;
    snapshot.capturedAtMs += 120000;
    assert.throws(() => buildCombatStatistics(baseline,current,after),/date/);
    snapshot.capturedAtMs -= 120000;
  }
  for (const reason of [undefined,'','manual','login-other',{},1]) {
    snapshot.reason = reason;
    assert.throws(() => buildCombatStatistics(baseline,current,after),/Unknown combat collector/);
  }
});

test('combat cache uses the existing read-only endpoint without leaking private metadata',async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-combat-test-'));
  const file = path.join(root,'statistics.json');
  const raw = {schemaVersion:2,source:'arthas-combat-stats',characterName:'Flowmage',characterCapturedAt:'base',savedAt:'2026-09-05T12:00:00Z',observedAt:'2026-09-05T12:01:00Z',...combatDetails()};
  const server = createServer({getStatistics:() => readStatistics(file)});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    await fs.writeFile(file,JSON.stringify({...raw,account:123,token:'secret'}));
    assert.deepEqual((await (await fetch(base+'/statistics.json')).json()).record,raw);
    raw.schools.pop();
    await fs.writeFile(file,JSON.stringify(raw));
    assert.equal((await fetch(base+'/statistics.json')).status,503);
  } finally {
    server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
    await fs.unlink(file); await fs.rmdir(root);
  }
});
