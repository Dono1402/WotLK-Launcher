const nativeFields = ['strength','agility','stamina','intellect','spirit','armor','maxHealth','maxMana'];
const combatFields = ['attackPower','rangedAttackPower','meleeCritPct','rangedCritPct','meleeHitPct','rangedHitPct','spellHitPct','meleeHastePct','rangedHastePct','spellHastePct','expertise','healingPower','manaRegenCasting','defenseSkill','dodgePct','parryPct','blockPct','resilience'];
const signedFields = new Set(['meleeHitPct','rangedHitPct','spellHitPct','meleeHastePct','rangedHastePct','spellHastePct','healingPower','spellPower']);

function number(value,key,integer=false) {
  if (typeof value!=='number' || !Number.isFinite(value) || Math.abs(value)>1e9 || (!signedFields.has(key) && value<0) || (integer && !Number.isSafeInteger(value)) || (key.endsWith('HastePct') && value<=-100)) throw new Error(`Invalid combat statistic: ${key}`);
  return value;
}

function sanitizeCombatDetails(raw) {
  const values = {};
  for (const key of nativeFields) values[key] = number(raw.values?.[key],key,true);
  for (const key of combatFields) values[key] = number(raw.values?.[key],key);
  if (!Array.isArray(raw.schools) || raw.schools.length!==6) throw new Error('Six spell schools are required');
  const schools = [];
  for (let id=1;id<=6;id++) {
    const entries = raw.schools.filter(school => school.id===id);
    if (entries.length!==1) throw new Error('Invalid spell school identity');
    schools.push({id,spellPower:number(entries[0].spellPower,'spellPower',true),spellCritPct:number(entries[0].spellCritPct,'spellCritPct')});
  }
  if (!Array.isArray(raw.talentPoints) || raw.talentPoints.length!==3 || !raw.talentPoints.every(n => Number.isInteger(n) && n>=0 && n<=255)) throw new Error('Invalid active talent trees');
  if (!Number.isInteger(raw.form) || raw.form<0 || raw.form>255 || raw.includesTemporaryEffects!==true) throw new Error('Missing combat capture context');
  return {values,schools,talentPoints:[...raw.talentPoints],form:raw.form,includesTemporaryEffects:true};
}

module.exports = {nativeFields,combatFields,sanitizeCombatDetails};
