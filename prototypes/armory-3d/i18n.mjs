const messages = {
  fr: {
    statsMelee:'Mêlée', statsRanged:'Distance', statsSpell:'Magie', statsHealing:'Soins', statsDefense:'Défense', statsMode:'Type de statistiques', statsSchool:'École de magie',
    schoolAll:'Toutes les écoles', schoolHoly:'Sacré', schoolFire:'Feu', schoolNature:'Nature', schoolFrost:'Givre', schoolShadow:'Ombre', schoolArcane:'Arcanes',
    summaryAttackPower:"Puissance d'attaque", summaryRangedAttackPower:'Puissance à distance', summaryMeleeCrit:'Critique en mêlée', summaryMeleeHit:'Toucher en mêlée', summaryMeleeHaste:'Hâte en mêlée', summaryExpertise:'Expertise', summaryRangedCrit:'Critique à distance', summaryRangedHit:'Toucher à distance', summaryRangedHaste:'Hâte à distance', summaryHealingPower:'Bonus aux soins', summaryManaRegenCasting:'Mana / 5 s en incantation', summaryMaxMana:'Mana maximum', summaryMaxHealth:'Vie maximum', summaryDefense:'Défense', summaryDodge:'Esquive', summaryParry:'Parade', summaryBlock:'Blocage', summaryResilience:'Résilience',
    hitBonusHint:'Bonus général de toucher. Les effets propres à un sort ou à une cible ne sont pas inclus.', hasteHint:"Vitesse globale au moment du relevé, effets temporaires inclus. Les modifications propres à un sort ne sont pas incluses.", schoolMinimumHint:'Valeur minimale des six écoles de magie, comme dans la fiche du jeu.', combatSnapshotHint:'Relevé du {date}, avec les effets temporaires et la forme présents à cet instant.',
    characterSummary:'Résumé du personnage', statistics:'Statistiques', averageEquipped:'Moyenne équipée', averageLevelHint:"Moyenne des niveaux des objets portés, hors chemise et tabard. Emplacements vides ignorés ; ce n'est pas le score d'accès aux donjons.", statsMissing:'Données serveur indisponibles', statsPartial:'Données serveur incomplètes', statUnavailable:'Non disponible',
    summaryStrength:'Force', summaryAgility:'Agilité', summaryIntellect:'Intelligence', summaryStamina:'Endurance', summarySpirit:'Esprit', summarySpellPower:'Puissance des sorts', summarySpellCrit:'Critique des sorts', summarySpellHit:'Toucher des sorts', summarySpellHaste:'Hâte des sorts', summaryArmor:'Armure',
    armory:'Armurerie', level:'Niveau', mage:'Mage', bloodElf:'Elfe de sang', equipment:'Personnage et équipement', model:'Modèle 3D du personnage', controls:'Vue 3D', weapons:'Armes équipées',
    loading:'Chargement du personnage…', loadingData:'Chargement des données', modelBuilding:'Préparation du modèle 3D…', modelClientMissing:'Sélectionne une installation de WotLK dans les paramètres pour préparer l’aperçu 3D.', modelGraphicsUnavailable:'Aperçu 3D indisponible sur cet appareil.', modelUnavailable:'Modèle 3D indisponible pour cet équipement', weaponMelee:'Mêlée', weaponRanged:'À distance', showMelee:'Afficher les armes de mêlée', showRanged:"Afficher l’arme à distance", snapshot:'Relevé du {date}', snapshotMode:'Dernier relevé enregistré',
    pause:"Mettre l'animation en pause", play:"Reprendre l'animation", rotate:'Rotation automatique', zoomIn:'Zoom avant', zoomOut:'Zoom arrière', reset:'Recentrer', fullscreen:'Plein écran', exitFullscreen:'Quitter le plein écran',
    close:'Fermer les détails', equipped:'Équipé', itemLevel:"Niveau d'objet", empty:'{slot} : emplacement vide', dataUnavailable:'Données locales indisponibles', loadFailed:'Impossible de charger le personnage', detailsUnavailable:"Caractéristiques de l'objet indisponibles", detailsPartial:'Certains effets de cet objet ne sont pas encore disponibles.',
    armor:'{value} Armure', block:'{value} Blocage', requiredLevel:'Niveau {value} requis', speed:'Vitesse {value}', dps:'{value} dégâts par seconde', damage:'{min} - {max} dégâts', damageSchool:'{min} - {max} dégâts ({school})',
    equipEffect:'Équipé : {text}', critEffect:'Augmente le score de coup critique de {value}.', useEffect:'Utiliser : {text}', hitEffect:'Toucher : {text}', enchant:'Enchantement : {text}', socket:'Châsse', twoHand:'Deux mains', stat:'Caractéristique {id}', resistance:'Résistance {school} +{value}'
  },
  en: {
    statsMelee:'Melee', statsRanged:'Ranged', statsSpell:'Spell', statsHealing:'Healing', statsDefense:'Defense', statsMode:'Statistics category', statsSchool:'Spell school',
    schoolAll:'All schools', schoolHoly:'Holy', schoolFire:'Fire', schoolNature:'Nature', schoolFrost:'Frost', schoolShadow:'Shadow', schoolArcane:'Arcane',
    summaryAttackPower:'Attack power', summaryRangedAttackPower:'Ranged attack power', summaryMeleeCrit:'Melee critical chance', summaryMeleeHit:'Melee hit bonus', summaryMeleeHaste:'Melee haste', summaryExpertise:'Expertise', summaryRangedCrit:'Ranged critical chance', summaryRangedHit:'Ranged hit bonus', summaryRangedHaste:'Ranged haste', summaryHealingPower:'Bonus healing', summaryManaRegenCasting:'Mana / 5 sec casting', summaryMaxMana:'Maximum mana', summaryMaxHealth:'Maximum health', summaryDefense:'Defense', summaryDodge:'Dodge', summaryParry:'Parry', summaryBlock:'Block', summaryResilience:'Resilience',
    hitBonusHint:'General hit bonus. Spell-specific and target-specific effects are not included.', hasteHint:'Global speed at capture time, including temporary effects. Spell-specific modifiers are not included.', schoolMinimumHint:'Minimum across the six spell schools, as in the in-game character sheet.', combatSnapshotHint:'Snapshot: {date}, including temporary effects and the form active at that time.',
    characterSummary:'Character summary', statistics:'Statistics', averageEquipped:'Equipped average', averageLevelHint:'Mean item level of worn pieces, excluding shirt and tabard. Empty slots are ignored; this is not the dungeon-entry gear score.', statsMissing:'Server data unavailable', statsPartial:'Server data incomplete', statUnavailable:'Unavailable',
    summaryStrength:'Strength', summaryAgility:'Agility', summaryIntellect:'Intellect', summaryStamina:'Stamina', summarySpirit:'Spirit', summarySpellPower:'Spell power', summarySpellCrit:'Spell critical chance', summarySpellHit:'Spell hit chance', summarySpellHaste:'Spell haste', summaryArmor:'Armor',
    armory:'Armory', level:'Level', mage:'Mage', bloodElf:'Blood Elf', equipment:'Character and equipment', model:'Character 3D model', controls:'3D view', weapons:'Equipped weapons',
    loading:'Loading character…', loadingData:'Loading character data', modelBuilding:'Preparing 3D model…', modelClientMissing:'Select a WotLK installation in settings to prepare the 3D preview.', modelGraphicsUnavailable:'3D preview unavailable on this device.', modelUnavailable:'3D model unavailable for this equipment', weaponMelee:'Melee', weaponRanged:'Ranged', showMelee:'Show melee weapons', showRanged:'Show ranged weapon', snapshot:'Snapshot: {date}', snapshotMode:'Latest saved snapshot',
    pause:'Pause animation', play:'Resume animation', rotate:'Auto-rotate', zoomIn:'Zoom in', zoomOut:'Zoom out', reset:'Reset view', fullscreen:'Fullscreen', exitFullscreen:'Exit fullscreen',
    close:'Close item details', equipped:'Equipped', itemLevel:'Item level', empty:'{slot}: empty slot', dataUnavailable:'Local data unavailable', loadFailed:'Unable to load character', detailsUnavailable:'Item details unavailable', detailsPartial:'Some effects of this item are not available yet.',
    armor:'{value} Armor', block:'{value} Block', requiredLevel:'Requires level {value}', speed:'Speed {value}', dps:'{value} damage per second', damage:'{min} - {max} damage', damageSchool:'{min} - {max} damage ({school})',
    equipEffect:'Equip: {text}', critEffect:'Improves critical strike rating by {value}.', useEffect:'Use: {text}', hitEffect:'Chance on hit: {text}', enchant:'Enchantment: {text}', socket:'Socket', twoHand:'Two-hand', stat:'Stat {id}', resistance:'+{value} {school} Resistance'
  }
};
export const slotNames = {
  fr:['Tête','Cou','Épaules','Chemise','Torse','Taille','Jambes','Pieds','Poignets','Mains','Doigt','Doigt','Bijou','Bijou','Dos','Main droite','Main gauche','À distance','Tabard'],
  en:['Head','Neck','Shoulders','Shirt','Chest','Waist','Legs','Feet','Wrists','Hands','Finger','Finger','Trinket','Trinket','Back','Main hand','Off hand','Ranged','Tabard']
};
const statNames = {
  fr:{0:'Mana',1:'Vie',3:'Agilité',4:'Force',5:'Intelligence',6:'Esprit',7:'Endurance',12:'Défense',13:'Esquive',14:'Parade',15:'Blocage',31:'Score de toucher',32:'Score de coup critique',35:'Résilience',36:'Hâte',37:'Expertise',38:"Puissance d’attaque",39:"Puissance d’attaque à distance",43:'Mana toutes les 5 s',45:'Puissance des sorts',46:'Vie toutes les 5 s',47:'Pénétration des sorts',48:'Valeur de blocage'},
  en:{0:'Mana',1:'Health',3:'Agility',4:'Strength',5:'Intellect',6:'Spirit',7:'Stamina',12:'Defense',13:'Dodge',14:'Parry',15:'Block',31:'Hit rating',32:'Critical strike rating',35:'Resilience',36:'Haste',37:'Expertise',38:'Attack power',39:'Ranged attack power',43:'Mana per 5 sec',45:'Spell power',46:'Health per 5 sec',47:'Spell penetration',48:'Block value'}
};
const schools = { fr:['Physique','Sacré','Feu','Nature','Givre','Ombre','Arcanes'], en:['Physical','Holy','Fire','Nature','Frost','Shadow','Arcane'] };
const armorTypes = { fr:['','Tissu','Cuir','Mailles','Plaques','','Bouclier'], en:['','Cloth','Leather','Mail','Plate','','Shield'] };
const weaponTypes = { fr:{0:'Hache',1:'Hache',2:'Arc',3:'Arme à feu',4:'Masse',5:'Masse',6:"Arme d’hast",7:'Épée',8:'Épée',10:'Bâton',13:'Arme de pugilat',15:'Dague',16:'Arme de jet',18:'Arbalète',19:'Baguette',20:'Canne à pêche'}, en:{0:'Axe',1:'Axe',2:'Bow',3:'Gun',4:'Mace',5:'Mace',6:'Polearm',7:'Sword',8:'Sword',10:'Staff',13:'Fist weapon',15:'Dagger',16:'Thrown',18:'Crossbow',19:'Wand',20:'Fishing pole'} };
const bindings = { fr:['','Lié quand ramassé','Lié quand équipé','Lié quand utilisé','Objet de quête'], en:['','Binds when picked up','Binds when equipped','Binds when used','Quest item'] };

export function normalizeLocale(value) {
  if (typeof value!=='string') return null;
  if (/^fr(?:[-_]|$)/i.test(value)) return 'fr';
  if (/^en(?:[-_]|$)/i.test(value)) return 'en';
  return null;
}
export function chooseLocale(override, launcher, browserLanguages=[]) {
  return normalizeLocale(override) || normalizeLocale(launcher) || browserLanguages.map(normalizeLocale).find(Boolean) || 'en';
}
export function t(locale,key,values={}) {
  const message = messages[locale]?.[key];
  if (message===undefined) throw new Error(`Missing translation ${locale}.${key}`);
  return message.replace(/\{(\w+)\}/g, (_,name) => String(values[name] ?? ''));
}
export function itemName(item,locale) { return item.details?.name[locale] || item.name; }
export function itemType(details,locale) {
  if (details.classId===4) return armorTypes[locale][details.subclassId] || '';
  if (details.classId===2) return [weaponTypes[locale][details.subclassId],details.inventoryType===17 ? t(locale,'twoHand') : ''].filter(Boolean).join(' · ');
  return '';
}
export function itemLines(details,locale) {
  if (!details) return [{kind:'muted',text:t(locale,'detailsUnavailable')}];
  const number = (n,digits=0) => new Intl.NumberFormat(locale==='fr'?'fr-FR':'en-US',{minimumFractionDigits:digits,maximumFractionDigits:digits}).format(n);
  const lines = [];
  const add = (key,values,kind='normal') => lines.push({kind,text:t(locale,key,values)});
  if (bindings[locale][details.bonding]) lines.push({kind:'muted',text:bindings[locale][details.bonding]});
  if (details.armor) add('armor',{value:number(details.armor)});
  if (details.block) add('block',{value:number(details.block)});
  for (const damage of details.damage) add(damage.school?'damageSchool':'damage',{min:number(damage.min),max:number(damage.max),school:schools[locale][damage.school]});
  if (details.damage.length && details.delay>0) {
    add('speed',{value:number(details.delay/1000,2)});
    add('dps',{value:number(details.damage.reduce((sum,d) => sum+(d.min+d.max)/2,0)/(details.delay/1000),1)},'muted');
  }
  const statText = stat => `${stat.value>0?'+':''}${number(stat.value)} ${statNames[locale][stat.type] || t(locale,'stat',{id:stat.type})}`;
  for (const stat of details.stats.filter(stat => stat.type<12)) lines.push({kind:stat.value<0?'penalty':'stat',text:statText(stat)});
  details.resistances.forEach((value,i) => { if (value) add('resistance',{school:schools[locale][i+1],value}); });
  if (details.requiredLevel) add('requiredLevel',{value:details.requiredLevel},'requirement');
  for (const stat of details.stats.filter(stat => stat.type>=12)) {
    const text = stat.type===32 ? t(locale,'critEffect',{value:number(stat.value)}) : statText(stat);
    add('equipEffect',{text},stat.value<0?'penalty':'effect');
  }
  for (const effect of details.effects) add(effect.trigger===1?'equipEffect':effect.trigger===2?'hitEffect':'useEffect',{text:effect.description[locale]},'effect');
  for (const enchantment of details.enchantments) add('enchant',{text:enchantment.name[locale]},'effect');
  for (const socket of details.sockets) lines.push({kind:'muted',text:`${t(locale,'socket')} ${socket}`});
  if (details.description?.[locale]) lines.push({kind:'description',text:details.description[locale]});
  if (details.incomplete) lines.push({kind:'muted',text:t(locale,'detailsPartial')});
  return lines;
}
