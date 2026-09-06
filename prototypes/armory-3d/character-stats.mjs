import { t } from './i18n.mjs';

export function averageEquippedItemLevel(equipment) {
  if (!Array.isArray(equipment)) return null;
  const slots = new Set();
  const items = [];
  for (const item of equipment) {
    if (!Number.isInteger(item.slot) || item.slot<0 || item.slot>18 || slots.has(item.slot)) return null;
    slots.add(item.slot);
    if (item.slot===3 || item.slot===18) continue;
    if (!Number.isFinite(item.itemLevel) || item.itemLevel<0 || item.quality===7) return null;
    items.push(item);
  }
  // This is the arithmetic mean of worn, non-cosmetic pieces, not the dungeon-entry gear score.
  return items.length ? items.reduce((sum,item) => sum+item.itemLevel,0)/items.length : null;
}

const mageFields = [
  ['intellect','summaryIntellect'], ['stamina','summaryStamina'], ['spirit','summarySpirit'],
  ['spellPower','summarySpellPower'], ['spellCritPct','summarySpellCrit',true],
  ['spellHitPct','summarySpellHit',true], ['spellHastePct','summarySpellHaste',true], ['armor','summaryArmor']
];
const baseFields = [
  ['strength','summaryStrength'], ['agility','summaryAgility'], ['stamina','summaryStamina'],
  ['intellect','summaryIntellect'], ['spirit','summarySpirit'], ['armor','summaryArmor']
];

const classIds = {Warrior:1,Paladin:2,Hunter:3,Rogue:4,Priest:5,'Death Knight':6,Shaman:7,Mage:8,Warlock:9,Druid:11};
const modesByClass = {1:['melee','defense'],2:['melee','healing','defense'],3:['ranged'],4:['melee'],5:['spell','healing'],6:['melee','defense'],7:['spell','melee','healing'],8:['spell'],9:['spell'],11:['spell','melee','healing','defense']};
const modeIcons = {melee:'swords',ranged:'crosshair',spell:'wand-sparkles',healing:'heart-pulse',defense:'shield',base:'activity'};
const modeLabels = {melee:'statsMelee',ranged:'statsRanged',spell:'statsSpell',healing:'statsHealing',defense:'statsDefense',base:'statistics'};
const signedFields = new Set(['spellPower','healingPower','meleeHitPct','rangedHitPct','spellHitPct','meleeHastePct','rangedHastePct','spellHastePct']);
const idOf = character => character?.classId ?? classIds[character?.class];

function matchingRecord(character) {
  const record = character?.statistics;
  const basis = record?.characterCapturedAt ?? record?.capturedAt;
  return typeof basis==='string' && basis.length>0 && basis===character?.capturedAt ? record : null;
}

export function statisticsModes(character,locale) {
  return (modesByClass[idOf(character)] || ['base']).map(key => ({key,label:t(locale,modeLabels[key]),icon:modeIcons[key]}));
}

export function defaultStatisticsMode(character) {
  const id = idOf(character), record = matchingRecord(character);
  const choices = modesByClass[id] || ['base'];
  const points = record?.talentPoints;
  let tree = -1;
  if (Array.isArray(points) && points.length===3 && points.every(n => Number.isInteger(n) && n>=0)) {
    const best = Math.max(...points);
    if (best>0 && points.filter(n => n===best).length===1) tree = points.indexOf(best);
  }
  if (id===11 && [5,8].includes(record?.form)) return 'defense';
  if (id===11 && record?.form===1) return 'melee';
  const talentModes = {1:['melee','melee','defense'],2:['healing','defense','melee'],5:['healing','healing','spell'],7:['spell','melee','healing'],11:['spell','melee','healing']};
  return talentModes[id]?.[tree] || choices[0];
}

export function statisticsSchools(locale) {
  return ['schoolAll','schoolHoly','schoolFire','schoolNature','schoolFrost','schoolShadow','schoolArcane'].map((key,id) => ({id,label:t(locale,key)}));
}

function fieldsFor(character,mode) {
  const attribute = [3,4,7,11].includes(idOf(character)) ? ['agility','summaryAgility'] : ['strength','summaryStrength'];
  if (mode==='spell') return mageFields;
  if (mode==='melee') return [attribute,['stamina','summaryStamina'],['attackPower','summaryAttackPower'],['meleeCritPct','summaryMeleeCrit',true],['meleeHitPct','summaryMeleeHit',true],['expertise','summaryExpertise'],['meleeHastePct','summaryMeleeHaste',true],['armor','summaryArmor']];
  if (mode==='ranged') return [['agility','summaryAgility'],['stamina','summaryStamina'],['rangedAttackPower','summaryRangedAttackPower'],['rangedCritPct','summaryRangedCrit',true],['rangedHitPct','summaryRangedHit',true],['rangedHastePct','summaryRangedHaste',true],['intellect','summaryIntellect'],['armor','summaryArmor']];
  if (mode==='healing') return [['intellect','summaryIntellect'],['spirit','summarySpirit'],['healingPower','summaryHealingPower'],['spellCritPct','summarySpellCrit',true],['spellHastePct','summarySpellHaste',true],['manaRegenCasting','summaryManaRegenCasting'],['maxMana','summaryMaxMana'],['stamina','summaryStamina']];
  if (mode==='defense') return [['maxHealth','summaryMaxHealth'],['stamina','summaryStamina'],['armor','summaryArmor'],['defenseSkill','summaryDefense'],['dodgePct','summaryDodge',true],...(idOf(character)===11 ? [['agility','summaryAgility']] : [['parryPct','summaryParry',true]]),...([1,2].includes(idOf(character)) ? [['blockPct','summaryBlock',true]] : []),['resilience','summaryResilience']];
  return baseFields;
}

export function characterStatsRows(character,locale,requestedMode,school=0) {
  const mode = statisticsModes(character,locale).some(item => item.key===requestedMode) ? requestedMode : defaultStatisticsMode(character);
  const fields = fieldsFor(character,mode);
  const record = matchingRecord(character);
  const values = {...record?.values};
  if (record?.source==='arthas-combat-stats') {
    const selected = school===0 ? record.schools : record.schools?.filter(entry => entry.id===school);
    for (const key of ['spellPower','spellCritPct']) {
      const valid = selected?.length===(school===0?6:1) && selected.every(entry => typeof entry[key]==='number' && Number.isFinite(entry[key]));
      values[key] = valid ? Math.min(...selected.map(entry => entry[key])) : undefined;
    }
  }
  return fields.map(([key,label,percent=false]) => {
    const raw = values?.[key];
    const known = typeof raw==='number' && Number.isFinite(raw) && (raw>=0 || signedFields.has(key));
    const value = known ? new Intl.NumberFormat(locale==='fr'?'fr-FR':'en-US',percent
      ? {style:'percent',minimumFractionDigits:1,maximumFractionDigits:1}
      : {maximumFractionDigits:0}).format(percent ? raw/100 : raw) : '—';
    const hint = key.endsWith('HitPct') ? t(locale,'hitBonusHint') : key.endsWith('HastePct') ? t(locale,'hasteHint') : ['spellPower','spellCritPct'].includes(key) && school===0 ? t(locale,'schoolMinimumHint') : '';
    return {key,label:t(locale,label),value,known,hint,negative:known && raw<0};
  });
}
