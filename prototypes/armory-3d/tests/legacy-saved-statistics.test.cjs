const {test} = require('node:test');
const assert = require('node:assert/strict');
const {rosterQuery,readRoster} = require('../launcher-roster.cjs');

test('local SQL maps persisted power and physical percentages to the same explicit saved-field contract as the API',() => {
  const sql = rosterQuery(42);
  const mappings = {
    baseAttackPower:'attackPower',baseRangedAttackPower:'rangedAttackPower',baseSpellPower:'spellPower',
    meleeCritPct:'critPct',rangedCritPct:'rangedCritPct',dodgePct:'dodgePct',parryPct:'parryPct',blockPct:'blockPct',resilience:'resilience'
  };
  for (const [field,column] of Object.entries(mappings)) assert.match(sql,new RegExp(`'${field}',s\\.${column}\\b`));
  assert.match(sql,/'values',IF\(s\.guid IS NULL,NULL,JSON_OBJECT\(/);
  assert.match(sql,/LEFT JOIN arthas_chars\.character_stats s ON s\.guid=c\.guid/);
  assert.match(sql,/WHERE c\.account=42;\s*ROLLBACK;$/);
  assert.match(sql,/^SET SESSION TRANSACTION READ ONLY;\s*START TRANSACTION WITH CONSISTENT SNAPSHOT;/);
  assert.doesNotMatch(sql,/'(?:attackPower|rangedAttackPower|spellPower|spellCritPct|\w+HitPct|\w+HastePct)'|s\.spellCritPct|\b(?:INSERT|UPDATE|DELETE|REPLACE)\b/i);
});

test('local saved-statistics transport reaches the shared normalization and rendering without a combat capture',async () => {
  const {characterStatsRows} = await import('../character-stats.mjs');
  const values = {strength:30,agility:37,stamina:29,intellect:36,spirit:25,armor:143,maxHealth:270,maxMana:280,
    baseAttackPower:60,baseRangedAttackPower:47,baseSpellPower:0,meleeCritPct:5.608800411224365,
    rangedCritPct:5.808800220489502,dodgePct:20.958168,parryPct:4.96,blockPct:0,resilience:0};
  const identity = {guid:302,name:'SavedHunter',race:1,classId:3,gender:0,level:10,
    skin:0,face:0,hairStyle:0,hairColor:0,facialStyle:0,online:0,zoneId:12,lastLogout:Date.parse('2026-09-06T11:00:00Z')/1000};
  const config = {verifiedAfter:'2026-09-06T10:00:00Z'};
  let calls = 0;
  // Inject only the SQL transport. The real local importer and renderer run below.
  const rows = await readRoster(42,config,async (sql,options) => {
    calls++;
    assert.equal(sql,rosterQuery(42));
    assert.equal(options,config);
    return {observedAtUtc:'2026-09-06 12:00:00.000000',characters:[
      {character:identity,values,equipment:[],snapshot:null},
      {character:{...identity,guid:303,name:'NoSavedRow'},values:null,equipment:[],snapshot:null}
    ]};
  });
  assert.equal(calls,1);
  const row = rows.find(value => value.id==='302');
  assert.deepEqual(row.statistics.values,values);
  assert.equal(row.statistics.source,'arthas-character-stats');
  assert.equal(row.statistics.savedAtSource,'character-last-logout');
  assert.equal(row.statistics.schools,undefined);
  assert.equal(rows.find(value => value.id==='303').statistics,null);
  const displayed = {classId:row.classId,capturedAt:row.snapshot.capturedAtUtc,statistics:row.statistics};
  for (const locale of ['fr','en']) {
    const statistics = Object.fromEntries(characterStatsRows(displayed,locale).map(value => [value.key,value]));
    assert.equal(statistics.baseRangedAttackPower.value,'47');
    assert.equal(statistics.rangedCritPct.value,locale==='fr' ? '5,8\u00a0%' : '5.8%');
    assert.equal(statistics.rangedHitPct.known,false);
    assert.equal(statistics.rangedHastePct.known,false);
  }
});
