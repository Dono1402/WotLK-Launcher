const fs = require('node:fs/promises');
const path = require('node:path');

function parseEnchantments(text) {
  const values = text.trim().split(/\s+/).map(Number);
  if (values.length !== 36 || values.some(n => !Number.isSafeInteger(n) || n < 0)) throw new Error('Invalid enchantment snapshot');
  return Array.from({length:12}, (_,slot) => ({ slot, id:values[slot*3], duration:values[slot*3+1], charges:values[slot*3+2] }));
}

function spellText(id, tables) {
  // Only the fixed spell-power effect present in this snapshot is supported, not a general spell formula evaluator.
  if (id !== 9393) throw new Error(`Unverified item spell ${id}`);
  const effect = tables.spellEffects.find(e => e.SpellID===id && e.EffectIndex===0 && e.DifficultyID===0);
  if (!effect || effect.EffectAura!==13 || effect.EffectDieSides!==1 || effect.EffectRealPointsPerLevel!==0) throw new Error('Unexpected spell-power formula');
  const template = tables.spells[id]?.Description_lang;
  if (!template?.includes('$s1')) throw new Error('Spell-power text missing');
  const text = template.replace('$s1',String(effect.EffectBasePoints + 1));
  if (text.includes('$')) throw new Error('Unresolved spell description');
  return text;
}

function resolveDetails(snapshot, catalog, tables) {
  return snapshot.equipment.map(item => {
    const base = catalog.items.find(row => row.itemId===item.itemId);
    if (!base || !base.name.fr || !base.name.en) throw new Error(`Missing localized catalog item ${item.itemId}`);
    if (base.scalingDistribution || base.scalingValue || item.randomPropertyId<0) throw new Error('Scaling items need an instance scaling resolver');
    const name = { ...base.name };
    const stats = new Map(base.stats.filter(([,value]) => value!==0));
    const enchants = parseEnchantments(item.enchantments);
    const random = enchants.filter(e => e.slot>=7 && e.id>0);
    if (item.randomPropertyId) {
      for (const locale of ['fr','en']) {
        const property = tables[locale].properties[item.randomPropertyId];
        if (!property?.Name_lang) throw new Error('Random property not resolved');
        const expected = property.Enchantment.filter(id => id>0).sort((a,b) => a-b);
        const actual = random.map(e => e.id).sort((a,b) => a-b);
        if (JSON.stringify(expected)!==JSON.stringify(actual)) throw new Error('Random property differs from the equipped instance');
        name[locale] += ' ' + property.Name_lang;
      }
    } else if (random.length) throw new Error('Random stats without a property');
    for (const {id} of random) {
      const enchant = tables.fr.enchantments[id];
      if (!enchant) throw new Error(`Enchantment ${id} missing`);
      enchant.Effect.forEach((effect,index) => {
        if (effect===0) return;
        if (effect!==5 || enchant.EffectScalingPoints[index]!==0) throw new Error('Unsupported random enchantment');
        const type = enchant.EffectArg[index];
        stats.set(type,(stats.get(type) || 0)+enchant.EffectPointsMin[index]);
      });
    }
    const enchantments = enchants.filter(e => e.slot<7 && e.id>0).map(e => ({
      slot:e.slot, name:Object.fromEntries(['fr','en'].map(locale => {
        const description = tables[locale].enchantments[e.id]?.Name_lang;
        if (!description || description.includes('$')) throw new Error('Unresolved enchantment text');
        return [locale, description];
      }))
    }));
    const effects = base.spells.filter(([id]) => id>0).map(([id,trigger]) => ({
      trigger, description:Object.fromEntries(['fr','en'].map(locale => [locale, spellText(id,tables[locale])]))
    }));
    return {
      slot:item.slot, itemId:item.itemId, name, description:base.description,
      classId:base.classId, subclassId:base.subclassId, inventoryType:base.inventoryType,
      requiredLevel:base.requiredLevel, armor:base.armor, block:base.block, bonding:base.bonding,
      stats:Array.from(stats, ([type,value]) => ({type,value})),
      damage:base.damage.filter(d => d.max>0), delay:base.delay,
      resistances:base.resistances, effects, enchantments, sockets:base.sockets.filter(n => n>0)
    };
  });
}

async function main() {
  const {outputRoot:output} = require('./runtime-paths.cjs');
  const read = async filename => JSON.parse(await fs.readFile(path.join(output,filename),'utf8'));
  const snapshot = await read('flowmage.json');
  const catalog = await read('item-catalog.json');
  const tables = { fr:await read('item-tables-fr.json'), en:await read('item-tables-en.json') };
  const result = { characterCapturedAt:snapshot.capturedAtUtc, catalogCapturedAt:catalog.capturedAtUtc, items:resolveDetails(snapshot,catalog,tables) };
  await fs.writeFile(path.join(output,'assets/item-details.json'),JSON.stringify(result,null,2));
  console.log(`Resolved ${result.items.length} equipped items in French and English`);
}
if (require.main===module) main().catch(error => { console.error(error); process.exitCode=1; });
module.exports = { resolveDetails, parseEnchantments, spellText };
