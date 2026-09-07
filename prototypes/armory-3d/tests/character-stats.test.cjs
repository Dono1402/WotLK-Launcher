const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');

test('equipped mean excludes cosmetics and empty slots without hiding invalid levels', async () => {
  const {averageEquippedItemLevel:average} = await import('../character-stats.mjs');
  assert.equal(average([{slot:0,itemLevel:20},{slot:4,itemLevel:40},{slot:3,itemLevel:1},{slot:18,itemLevel:1}]),30);
  assert.equal(average([]),null);
  assert.equal(average(null),null);
  assert.equal(average([{slot:0,itemLevel:0}]),0);
  assert.equal(average([{slot:0,itemLevel:20},{slot:0,itemLevel:20}]),null);
  assert.equal(average([{slot:0,itemLevel:NaN}]),null);
  assert.equal(average([{slot:0,itemLevel:10,quality:7}]),null);
  const character = JSON.parse(await fs.readFile(path.resolve(__dirname,'../../../artifacts/armory-prototype/assets/character.json'),'utf8'));
  assert.equal(average(character.equipment),272/12);
});

test('missing totals remain unavailable and never become item-bonus sums or zero', async () => {
  const {characterStatsRows} = await import('../character-stats.mjs');
  const character = {class:'Mage',capturedAt:'snapshot',equipment:[{details:{stats:[{type:5,value:50}]}}]};
  for (const locale of ['fr','en']) {
    const rows = characterStatsRows(character,locale);
    assert.equal(rows.length,8);
    assert.ok(rows.every(row => !row.known && row.value==='—'));
    assert.equal(rows[0].key,'intellect');
    assert.ok(rows.some(row => row.key==='spellCritPct'));
  }
});

test('total statistics must match the character snapshot and contain numeric values', async () => {
  const {characterStatsRows} = await import('../character-stats.mjs');
  const character = {classId:8,capturedAt:'snapshot',statistics:{capturedAt:'old',values:{intellect:150}}};
  assert.ok(characterStatsRows(character,'fr').every(row => !row.known));
  character.statistics.capturedAt = 'snapshot';
  character.statistics.values = {intellect:150,stamina:0,spirit:null,spellPower:'50',spellCritPct:3.5,spellHitPct:NaN,spellHastePct:120,armor:-1};
  const fr = Object.fromEntries(characterStatsRows(character,'fr').map(row => [row.key,row]));
  const en = Object.fromEntries(characterStatsRows(character,'en').map(row => [row.key,row]));
  assert.equal(fr.intellect.value,'150');
  assert.equal(fr.stamina.value,'0');
  assert.equal(fr.stamina.known,true);
  assert.equal(fr.spellCritPct.value,'3,5\u00a0%');
  assert.equal(en.spellCritPct.value,'3.5%');
  assert.equal(en.spellHastePct.value,'120.0%');
  for (const key of ['spirit','spellPower','spellHitPct','armor']) assert.equal(fr[key].known,false);
});
