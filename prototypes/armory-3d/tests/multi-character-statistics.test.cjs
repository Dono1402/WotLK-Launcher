const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const {buildCombatStatistics} = require('../armory-data.cjs');
const {normalizeRoster} = require('../launcher-roster.cjs');
const {LauncherArmory} = require('../launcher-armory.cjs');
const {createLauncherServer} = require('../launcher-server.cjs');
const {combatDetails} = require('./fixtures/combat.cjs');

// Synthetic collector fixtures exercise transport and formatting, not live character totals.
const observedAtUtc = '2026-09-06 12:00:00.000000';
const verifiedAfter = '2026-09-06T10:00:00Z';
const capturedAtMs = Date.parse('2026-09-06T11:30:00Z');
const baseFields = ['strength','agility','stamina','intellect','spirit','armor','maxHealth','maxMana'];
function character(guid,name,classId,values={}) {
  const identity = {guid,name,classId,race:1,gender:0,level:80,skin:0,face:0,hairStyle:0,hairColor:0,facialStyle:0};
  const details = combatDetails();
  Object.assign(details.values,values);
  details.talentPoints = [0,0,0];
  return {character:{...identity,online:1,zoneId:12,lastLogout:Date.parse('2026-09-06T11:00:00Z')/1000},
    equipment:[],values:Object.fromEntries(baseFields.map(key => [key,details.values[key]])),
    snapshot:{schemaVersion:1,source:'atlas-armory-engine',reason:'periodic',capturedAtMs,
      character:identity,equipment:[],...details}};
}
const roster = (...characters) => ({observedAtUtc,characters});
const fixtures = () => [
  character(301,'Guerrier',1,{strength:187,attackPower:2345,meleeCritPct:17.25,meleeHitPct:8.125}),
  character(302,'Chasseur',3,{agility:296,rangedAttackPower:4321,rangedCritPct:21.75,rangedHitPct:7.25}),
  character(401,'Magicienne',8,{intellect:315,spellHitPct:6.25,spellHastePct:12.5})
];
function baseline(row) {
  return {character:row.snapshot.character,equipment:row.snapshot.equipment,
    capturedAtUtc:new Date(row.snapshot.capturedAtMs).toISOString().replace('T',' ').replace('Z','')};
}
function displayed(row) {
  return {name:row.name,classId:row.classId,capturedAt:row.snapshot.capturedAtUtc,statistics:row.statistics};
}

test('shared combat import derives each authorized name from its baseline without a prototype default',() => {
  for (const row of fixtures()) {
    const current = {observedAtUtc,snapshot:row.snapshot};
    const result = buildCombatStatistics(baseline(row),current,verifiedAfter);
    assert.equal(result.status,'ready');
    assert.equal(result.record.characterName,row.character.name);
    assert.deepEqual(result.record.values,row.snapshot.values);
    assert.throws(() => buildCombatStatistics(baseline(row),current,verifiedAfter,{characterName:'Foreign'}),/authorized character/);
    const foreign = structuredClone(current);
    foreign.snapshot.character.guid++;
    assert.throws(() => buildCombatStatistics(baseline(row),foreign,verifiedAfter),{code:'ARMORY_REFRESH_REQUIRED'});
  }
});

test('warrior, hunter and mage captures retain exact totals and format their own combat fields in FR and EN',async () => {
  const {characterStatsRows,defaultStatisticsMode} = await import('../character-stats.mjs');
  const input = fixtures();
  input[1].snapshot = JSON.stringify(input[1].snapshot);
  const rows = normalizeRoster(roster(...input),{verifiedAfter});
  const expected = {
    301:{mode:'melee',fields:{attackPower:['2\u202f345','2,345'],meleeCritPct:['17,3\u00a0%','17.3%'],meleeHitPct:['8,1\u00a0%','8.1%']}},
    302:{mode:'ranged',fields:{rangedAttackPower:['4\u202f321','4,321'],rangedCritPct:['21,8\u00a0%','21.8%'],rangedHitPct:['7,3\u00a0%','7.3%']}},
    401:{mode:'spell',fields:{spellPower:['50','50'],spellCritPct:['2,0\u00a0%','2.0%'],spellHitPct:['6,3\u00a0%','6.3%']}}
  };
  for (const row of rows) {
    const source = fixtures().find(value => value.character.guid===Number(row.id));
    assert.equal(row.statistics.characterName,source.character.name);
    assert.deepEqual(row.statistics.values,source.snapshot.values);
    assert.deepEqual(row.statistics.schools,source.snapshot.schools);
    const character = displayed(row), spec = expected[row.id];
    assert.equal(defaultStatisticsMode(character),spec.mode);
    for (const [index,locale] of ['fr','en'].entries()) {
      const stats = Object.fromEntries(characterStatsRows(character,locale).map(value => [value.key,value]));
      for (const [key,values] of Object.entries(spec.fields)) {
        assert.equal(stats[key].value,values[index],`${row.name} ${locale} ${key}`);
        assert.equal(stats[key].known,true);
        assert.ok(stats[key].label);
      }
    }
  }
});

test('missing captures preserve saved base totals without accepting unproven effective powers or hit',async () => {
  const {characterStatsRows} = await import('../character-stats.mjs');
  for (const source of fixtures()) {
    source.character.online = 0;
    source.snapshot = null;
    source.values.attackPower = 99999;
    source.values.spellCritPct = 99;
    source.values.rangedHitPct = 99;
    source.values.rangedHastePct = 99;
    const [row] = normalizeRoster(roster(source),{verifiedAfter});
    assert.equal(row.statistics.source,'arthas-character-stats');
    assert.deepEqual(Object.keys(row.statistics.values),baseFields);
    for (const locale of ['fr','en']) {
      const stats = characterStatsRows(displayed(row),locale);
      assert.ok(stats.filter(value => baseFields.includes(value.key)).every(value => value.known));
      assert.ok(stats.filter(value => !baseFields.includes(value.key)).every(value => !value.known && value.value==='—'));
    }
    source.character.online = 1;
    assert.equal(normalizeRoster(roster(source),{verifiedAfter})[0].statistics,null);
  }
});

test('offline saves show exact base powers and physical critical chance with saved provenance and no fabricated capture',async () => {
  const {characterStatsRows} = await import('../character-stats.mjs');
  const [warrior,hunter,mage] = fixtures();
  for (const source of [warrior,hunter,mage]) {
    source.character.online = 0;
    source.snapshot = null;
  }
  Object.assign(warrior.values,{baseAttackPower:705,meleeCritPct:12.371388,dodgePct:7.96703,parryPct:10.492,resilience:0});
  Object.assign(hunter.values,{baseAttackPower:60,baseRangedAttackPower:47,meleeCritPct:5.608800411224365,rangedCritPct:5.808800220489502});
  Object.assign(mage.values,{baseSpellPower:17,spellCritPct:0});
  const expected = {
    301:{baseAttackPower:705,meleeCritPct:12.371388},
    302:{baseRangedAttackPower:47,rangedCritPct:5.808800220489502},
    401:{baseSpellPower:17}
  };
  for (const row of normalizeRoster(roster(warrior,hunter,mage),{verifiedAfter})) {
    assert.equal(row.statistics.source,'arthas-character-stats');
    assert.equal(row.statistics.schemaVersion,1);
    assert.equal(row.statistics.savedAtSource,'character-last-logout');
    assert.equal(row.statistics.savedAt,'2026-09-06T11:00:00.000Z');
    assert.equal(row.statistics.observedAt,'2026-09-06T12:00:00.000Z');
    assert.equal(row.statistics.schools,undefined);
    assert.equal(row.statistics.includesTemporaryEffects,undefined);
    assert.equal(row.statistics.values.spellCritPct,undefined);
    for (const [key,value] of Object.entries(expected[row.id])) assert.equal(row.statistics.values[key],value);
    for (const locale of ['fr','en']) {
      const rows = characterStatsRows(displayed(row),locale);
      for (const key of Object.keys(expected[row.id])) {
        const stat = rows.find(value => value.key===key);
        assert.equal(stat.known,true,`${row.name} ${key}`);
        if (key.startsWith('base')) {
          assert.match(stat.label,locale==='fr' ? /de base/ : /Base/);
          assert.match(stat.hint,locale==='fr' ? /sauvegardée/ : /Saved/);
        }
      }
      for (const stat of rows.filter(value => /HitPct|HastePct|spellCritPct/.test(value.key))) assert.equal(stat.known,false);
    }
  }
  // No school-specific spell bonus can be inferred from the global base field.
  const mageRow = normalizeRoster(roster(mage),{verifiedAfter})[0];
  for (let school=0;school<=6;school++) {
    const rows = characterStatsRows(displayed(mageRow),'fr','spell',school);
    assert.equal(rows.find(value => value.key==='baseSpellPower').value,'17');
    assert.equal(rows.find(value => value.key==='spellCritPct').known,false);
  }
});

test('invalid or absent saved combat fields are discarded independently and live captures retain priority',() => {
  const source = fixtures()[1];
  source.character.online = 0;
  Object.assign(source.values,{baseAttackPower:-1,baseRangedAttackPower:47.5,baseSpellPower:'17',
    rangedCritPct:NaN,meleeCritPct:Infinity,dodgePct:1e9+1,parryPct:null,blockPct:0,resilience:0});
  const capture = source.snapshot;
  source.snapshot = null;
  const saved = normalizeRoster(roster(source),{verifiedAfter})[0].statistics;
  assert.deepEqual(Object.keys(saved.values),[...baseFields,'blockPct','resilience']);
  assert.equal(saved.values.blockPct,0);
  assert.equal(saved.values.resilience,0);
  source.snapshot = capture;
  const live = normalizeRoster(roster(source),{verifiedAfter})[0].statistics;
  assert.equal(live.source,'arthas-combat-stats');
  assert.deepEqual(live.values,capture.values);
  assert.equal(live.savedAtSource,undefined);
  source.snapshot = null;
  source.values = null;
  assert.equal(normalizeRoster(roster(source),{verifiedAfter})[0].statistics,null);
});

test('foreign, stale and incomplete captures cannot borrow another character totals or suppress valid peers',() => {
  const [warrior,hunter,mage] = fixtures();
  const changes = [
    row => { row.snapshot = structuredClone(mage.snapshot); },
    row => { row.snapshot.character.guid = mage.character.guid; },
    row => { row.snapshot.character.name = mage.character.name; },
    row => { row.snapshot.character.classId = 8; },
    row => { row.snapshot.capturedAtMs = row.character.lastLogout*1000-1; },
    row => { row.snapshot.capturedAtMs = Date.parse(verifiedAfter)-1; },
    row => { row.snapshot.capturedAtMs = Date.parse('2026-09-07T00:00:00Z'); },
    row => { delete row.snapshot.values.meleeHitPct; }
  ];
  for (const change of changes) {
    const invalid = structuredClone(warrior);
    change(invalid);
    const rows = normalizeRoster(roster(invalid,hunter,mage),{verifiedAfter});
    assert.equal(rows.length,3);
    assert.equal(rows.find(row => row.id==='301').statistics,null);
    for (const valid of [hunter,mage]) assert.deepEqual(rows.find(row => Number(row.id)===valid.character.guid).statistics.values,valid.snapshot.values);
  }
});

test('separate account bridges publish all owned combat captures and keep persisted totals isolated',async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(),'atlas-multi-statistics-'));
  const armories = [], servers = [];
  t.after(async () => {
    for (const server of servers) {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    }
    for (const armory of armories) await armory.stop();
    assert.equal(path.dirname(path.resolve(root)),path.resolve(os.tmpdir()));
    assert.ok(path.basename(root).startsWith('atlas-multi-statistics-'));
    await fs.rm(root,{recursive:true,force:true});
  });
  const [warrior,hunter,mage] = fixtures();
  const records = new Map([[41,roster(warrior,hunter)],[42,roster(mage)]]);
  const create = (account,loadRoster=async () => structuredClone(records.get(account))) => {
    const armory = new LauncherArmory(account,{source:'rpc',verifiedAfter},{root,loadRoster,
      loadCatalog:async () => ({items:[]}),reference:async () => null,icons:async () => ({})});
    armories.push(armory);
    return armory;
  };
  for (const [account,input] of records) {
    const armory = create(account);
    await armory.refresh();
    assert.equal(armory.list().status,'ready');
    assert.equal(armory.list().characters.length,input.characters.length);
    const key = String(account===41?'a':'b').repeat(64);
    const server = createLauncherServer({key,armory});
    servers.push(server);
    await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
    const read = id => fetch(`http://127.0.0.1:${server.address().port}/characters/${id}/statistics.json`,{headers:{'x-atlas-armory-key':key}});
    for (const source of input.characters) {
      const response = await read(source.character.guid);
      assert.equal(response.status,200);
      const payload = await response.json();
      assert.equal(payload.status,'ready');
      assert.equal(payload.record.characterName,source.character.name);
      assert.deepEqual(payload.record.values,source.snapshot.values);
    }
    const foreignGuid = account===41 ? 401 : 301;
    assert.equal((await read(foreignGuid)).status,404);
    await armory.stop();
    const cached = create(account,async () => { throw new Error('Synthetic offline test'); });
    await cached.start();
    await cached.pending?.catch(() => {});
    assert.equal(cached.list().status,'cached');
    assert.equal(cached.entry(String(foreignGuid)),undefined);
    for (const source of input.characters) {
      const entry = cached.entry(String(source.character.guid));
      assert.equal(entry.owner,account);
      assert.deepEqual(entry.character.statistics.values,source.snapshot.values);
    }
  }
});
