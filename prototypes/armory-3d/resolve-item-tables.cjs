const fs = require('node:fs/promises');
const path = require('node:path');
const { openClient } = require('./local-client.cjs');

async function main() {
  const locale = process.argv[3] || 'fr';
  const { vendor, output } = await openClient(process.argv[2], { locale });
  const snapshot = JSON.parse(await fs.readFile(path.join(output, 'flowmage.json'), 'utf8'));
  const catalog = JSON.parse(await fs.readFile(path.join(output, 'item-catalog.json'), 'utf8'));
  const db2 = require(path.join(vendor, 'casc/db2.js'));
  const enchantIds = new Set(snapshot.equipment.flatMap(item => item.enchantments.trim().split(/\s+/).map(Number).filter((v,i) => i%3===0 && v>0)));
  const propertyIds = new Set(snapshot.equipment.map(item => item.randomPropertyId).filter(id => id>0));
  const spellIds = new Set(catalog.items.flatMap(item => item.spells.filter(([id]) => id>0).map(([id]) => id)));
  const result = { locale, enchantments:{}, properties:{}, spells:{}, spellEffects:[] };
  for (const id of enchantIds) result.enchantments[id] = await db2.SpellItemEnchantment.getRow(id);
  for (const id of propertyIds) result.properties[id] = await db2.ItemRandomProperties.getRow(id);
  for (const id of spellIds) result.spells[id] = await db2.Spell.getRow(id);
  const effects = await db2.SpellEffect.getAllRows();
  result.spellEffects = Array.from(effects.values()).filter(row => spellIds.has(row.SpellID));
  await fs.writeFile(path.join(output, `item-tables-${locale}.json`), JSON.stringify(result,null,2));
  console.log(JSON.stringify(result,null,2));
}
main().catch(error => { console.error(error); process.exitCode=1; });
