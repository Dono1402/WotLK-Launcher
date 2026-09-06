const {nativeFields,combatFields} = require('../../combat-statistics.cjs');

function combatDetails() {
  const values = Object.fromEntries([...nativeFields,...combatFields].map(key => [key,0]));
  Object.assign(values,{intellect:150,stamina:80,spirit:42,armor:300,maxHealth:600,maxMana:1800,attackPower:320,rangedAttackPower:280,meleeCritPct:4.5,rangedCritPct:6.5,spellHitPct:2,meleeHitPct:3,rangedHitPct:4,expertise:6,spellHastePct:5,healingPower:65,manaRegenCasting:12.4});
  return {values,schools:Array.from({length:6},(_,index) => ({id:index+1,spellPower:50+index,spellCritPct:2+index})),talentPoints:[0,0,10],form:0,includesTemporaryEffects:true};
}
module.exports = {combatDetails};
