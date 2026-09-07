const classes = {
  fr:{1:'Guerrier',2:'Paladin',3:'Chasseur',4:'Voleur',5:'Prêtre',6:'Chevalier de la mort',7:'Chaman',8:'Mage',9:'Démoniste',11:'Druide'},
  en:{1:'Warrior',2:'Paladin',3:'Hunter',4:'Rogue',5:'Priest',6:'Death Knight',7:'Shaman',8:'Mage',9:'Warlock',11:'Druid'}
};
const races = {
  fr:{1:'Humain',2:'Orc',3:'Nain',4:'Elfe de la nuit',5:'Mort-vivant',6:'Tauren',7:'Gnome',8:'Troll',10:'Elfe de sang',11:'Draeneï'},
  en:{1:'Human',2:'Orc',3:'Dwarf',4:'Night Elf',5:'Undead',6:'Tauren',7:'Gnome',8:'Troll',10:'Blood Elf',11:'Draenei'}
};
const colors = {1:'#c79c6e',2:'#f58cba',3:'#abd473',4:'#fff569',5:'#ffffff',6:'#ec435d',7:'#459aff',8:'#69ccf0',9:'#b9a3df',11:'#ff9a54'};
export const className = (id,locale) => classes[locale]?.[id] || (locale==='fr'?'Personnage':'Character');
export const raceName = (id,locale) => races[locale]?.[id] || '';
export const classColor = id => colors[id] || '#c6cdd2';
