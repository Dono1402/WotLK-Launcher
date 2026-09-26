# Ce que les mises à jour apportent à Atlas — 26 septembre 2026

## Périmètre et lecture

La comparaison part des versions amont réellement intégrées lors du déploiement
du **13 septembre 2026**, pas simplement des commits datés après ce jour. Certains
changements plus anciens ont rejoint les branches suivies plus tard. Les versions
retenues sont figées dans la [recette du candidat](../scripts/wotlk-update-20260926/README.md).

Ce document décrit **le contenu du lot source**. Il ne signifie ni que ces versions
sont déjà actives, ni que chaque raid a été terminé sur Atlas. Les résultats des
tests isolés sont distincts. Les valeurs de production, dont les 1 000 bots,
restent inchangées pendant la préparation.

| Dépôt | Son rôle | Nouveaux commits |
| --- | --- | ---: |
| Core Playerbot | Le moteur du serveur et les règles du monde, adapté pour accueillir les bots | 231 |
| Playerbots | Le comportement des personnages contrôlés par le serveur | 26 |
| Dungeon Clear | L'orchestration de parcours autonomes de donjons avec les bots | 9 |
| HermesProxy | La traduction entre le client moderne 3.4.3, le serveur WotLK et l'intégration du launcher | 51 |
| AH Bot | L'alimentation de l'hôtel des ventes | 1 |
| Transmog | La transmogrification | 0 |
| Account Achievements | Les hauts faits partagés au niveau du compte | 0 |

**318 commits ne veulent pas dire 318 fonctionnalités.** Le total comprend les
fusions, les imports SQL, des tests, de la documentation et des modifications de
construction. L'[index exhaustif](WOTLK-UPSTREAM-COMMITS-2026-09-26.md) conserve
chaque commit et son lien, y compris ces opérations techniques.

## 1. Core Playerbot : corrections du jeu lui-même

Il ne s'agit pas d'un deuxième module de bots. Une correction d'Ulduar dans le
core change le combat pour tous les joueurs ; une correction dans Playerbots
change la manière dont un bot réagit à ce combat.

### Ulduar : la plus grosse série de correctifs de contenu

- **Mimiron** : correction du minuteur du mode difficile, de la rotation visuelle
  du barrage laser et d'une désynchronisation après un wipe. Une cible technique
  ne doit plus entrer en combat.
- **Tranchécaille / Razorscale** : corrections du souffle et des phases au sol ;
  elle doit rester au sol pendant sa phase finale et ne lancer le coup d'aile
  qu'une fois par descente.
- **Thorim** : corrections des entités techniques du couloir, des sorts de Sif
  en mode 10 joueurs et d'un retour à l'état initial qui pouvait déclencher à tort
  sa défaite.
- **Hodir** : sa cache rare réapparaît avec le boss.
- **XT-002** : arrêt de l'invocation d'adds après sa mort ; correction d'une zone
  du mode difficile qui pouvait maintenir les joueurs en combat.
- **Kologarn** : son cadavre doit continuer à servir de pont après rechargement
  de l'instance.
- **Freya et ses anciens** : ajustements de récompenses et d'emblèmes, d'un
  indicateur de réinitialisation en 25 joueurs, de la durée et du nombre de rayons
  de soleil, et de l'interruption de Tidal Wave.
- **Léviathan des flammes** : correction de la sauvegarde de raid déclenchée dès
  l'engagement ; IA propre aux flagellants de Freya's Ward et correction de leur
  disparition prématurée.
- **Algalon** : corrections des immunités de ses adds, des constellations fermant
  des trous noirs en hauteur, et ajout d'une période de stase après Big Bang.
- **Vezax** : corrections des flaques de Shadow Crash, de leurs effets liés et
  de leur affichage.
- **Yogg-Saron** : les crânes respectent la ligne de vue ; Psychosis et Malady of
  the Mind évitent les cibles à faible santé mentale ; corrections des gardiens
  immortels, de Corrupted Wisdom pour certains paladins et de l'approche des
  familiers contre une très grande cible comme le cerveau.
- **Butin et hauts faits** : correction de plusieurs tables 10/25 joueurs,
  du mode difficile de Brise-acier, de récompenses de Freya et de l'attribution
  de Stokin' the Furnace sur Ignis.

L'intérêt concret est de réduire les combats qui se bloquent, les phases
incohérentes et les mauvaises récompenses. Ce n'est pas une refonte volontaire
de la difficulté et ce n'est pas une garantie que tout Ulduar fonctionne sans bug.
[Comparaison du core](https://github.com/mod-playerbots/azerothcore-wotlk/compare/06234df3d5ab26c93f4f1f06f3edb828b73ecd3c...7f12e89ee5f467a50e62eba1d525eac7dc953d03).

### ICC et les autres raids/donjons

Pour **ICC**, le lot corrige les animations de vol de Sindragosa et de plusieurs
dragons, l'attente de sept secondes de l'ascenseur de Dame Murmemort, l'artillerie
de la bataille des canonnières et l'événement des capitaines avec Svalna. Il
ajoute la conclusion scénarisée de Saurcroc et corrige le placement d'adds de
Keleseth ainsi qu'un état d'échec incorrect du Conseil des princes à l'apparition.

Dans les autres instances :

- **Naxxramas** : positions des zombies de Gluth et répartition du butin de
  Sapphiron en 25 joueurs.
- **Magtheridon** : le compte à rebours de libération commence sur l'engagement
  des canalistes, ce qui est aussi important pour la stratégie des bots.
- **SSC** : les formes et les rayons d'Hydross suivent l'aura du champ de purification.
- **Zul'Aman** : Zul'jin ne doit plus rester en forme de troll lors de la phase faucon-dragon.
- **Karazhan** : correction de l'état de combat de Terestian Illhoof.
- **AQ40** : les combats des Empereurs jumeaux et de C'Thun respectent les boss prérequis.
- **Caveau d'Archavon** : réapparition des serviteurs d'Emalon près du boss et annonces.
- **Stratholme, Scholomance, profondeurs de Rochenoire** : corrections de séquences
  scénarisées et de butin, dont Kirtonos, Alexei Barov et l'événement du coffre noir.
- **Salles de Foudre, donjon de Drak'Tharon, enclos aux esclaves** : corrections
  de suivi d'un lieutenant, des envahisseurs de Trollgore et des permissions du
  coffre d'Ahune.

### Classes, sorts, familiers et combat

- Le totem de Glèbe ne consomme plus les coups de zone concernés ; les provocations
  ne doivent plus affecter les totems.
- Les auras de paladins venant de lanceurs différents peuvent coexister selon les
  règles corrigées ; une aura sauvegardée conserve son montant même sans son lanceur.
- Correction du glyphe de Puits de lumière, du bonus quatre pièces T8 mage et de
  l'ordre de calcul des modificateurs de coût des sorts.
- La résistance des sorts utilise le niveau du lanceur. Les temps de récupération
  propres aux sorts sont mieux conservés lors des récupérations de catégorie.
- Annuler un rituel ne doit pas lui appliquer indûment un temps de récupération.
- Les familiers interrompent moins leurs canalisations pour suivre leur maître,
  s'arrêtent pour canaliser automatiquement et reprennent mieux la poursuite.
  Le comportement « suivre/rester » et les sorts hors de portée sont corrigés.
- Corrections de déplacements sous le terrain, de drapeaux de mouvement lors
  d'une immobilisation et de plusieurs risques de crash liés aux unités,
  véhicules, groupes et objets.
- Mise à jour d'immunités de créatures et de l'indicateur supprimant l'accélération
  des attaques après une parade sur des boss des paliers T7 à T10.

Ce sont des corrections de règles : conserver les paramètres de dégâts et de
progression n'empêche pas certains combats ou calculs de changer.

### Métiers : un changement de comportement à bien comprendre

La chance de gagner un point en **fabrication** descend maintenant progressivement
entre les seuils jaune et gris, au lieu d'utiliser seulement des probabilités
fixes par couleur. Elle conserve les probabilités configurées aux extrémités.
Avec les valeurs par défaut, le milieu de cette plage correspond à 50 % ; ce
chiffre n'est pas une mesure de la configuration Atlas.

Concrètement, deux recettes affichées dans la même couleur peuvent ne pas avoir
exactement la même chance de donner un point selon ta compétence. Une recette
perd graduellement son intérêt à mesure qu'elle approche du gris. Ce changement
vise la fabrication ; il ne faut pas le présenter comme une modification
générale identique de toutes les compétences de récolte.
[Modification concernée](https://github.com/mod-playerbots/azerothcore-wotlk/commit/a960ecb88c).

### Courrier, objets, groupes, monde et quêtes

- Les courriers différés sont annoncés à chaque arrivée, pas seulement au premier.
  Une taille incorrecte d'entrée dans la liste des courriers est corrigée.
- Une option peut rafraîchir immédiatement la boîte lors d'une livraison :
  `Mail.PushInboxOnDelivery`. Elle est **désactivée par défaut** et n'est pas
  activée dans notre préparation.
- Corrections des votes de butin expirés et de l'identification du jet gagnant.
- Contrôles de classe sur les reliques, ouverture d'objets en étant mort et
  sécurisation de l'expiration d'échange des objets liés.
- Vol à la tire rétabli sur certaines variantes héroïques ; suppression de
  butins indus et corrections de pools de récompenses.
- Météo d'une ancienne zone qui persistait après changement de zone ; fantômes
  à ressusciter autour d'un atelier contesté au Joug-d'hiver ; restrictions de
  quartiers à Dalaran appliquées au bon endroit.
- Plusieurs conditions de quêtes, marqueurs, escortes, invocations, textes et
  patrouilles sont corrigés : Zul'Drak, Shattrath, Orneval, Féralas, Silverpine,
  Dragonblight et Couronne de glace notamment.
- Amélioration des événements de construction du Tournoi d'Argent, du calendrier
  des fêtes et de la musique de la foire de Sombrelune ; corrections de créatures
  et de placements au port de Hurlevent.

Les noms exacts des petites corrections de données sont tous dans l'index des
commits. **62 fichiers SQL World** accompagnent ce lot : il faut les appliquer
aux données, pas uniquement remplacer l'exécutable.

### Technique : les bases de données des modules

Le core fournit maintenant une infrastructure permettant à chaque module de
gérer sa propre base. Le fork retire donc une partie de son ancien code spécifique
à la base Playerbots ; le module Playerbots prend le relais.

Cela ne crée pas une deuxième population de bots et ne demande pas de supprimer
leurs comptes. C'est un changement d'organisation du programme. En revanche,
**core et Playerbots doivent être intégrés ensemble**, avec vérification des
connexions SQL et de nos réservations de bots de guildes. Les options de
suppression de comptes restent désactivées sur Atlas.

Le lot ajoute aussi des points d'extension pour les modules — réception d'objets,
courrier, maîtres de classe, apprentissage de sorts, arènes, exclusivité d'auras —,
des optimisations de calcul de terrain et de recherche de scripts, ainsi que des
tests et des règles de développement. Leur intérêt est principalement technique ;
aucun gain de performances global n'a encore été mesuré sur Atlas avec 1 000 bots.

## 2. Playerbots : ce que les bots feront différemment

### Stabilité et vie quotidienne

- Corrections de crashs liés à certaines commandes et aux objets chuchotés ;
  protection contre une boucle lors de l'analyse d'un montant d'argent.
- Un bot qui se promène peut prendre la quête située près de lui au lieu de la
  manquer à cause de la logique de déplacement.
- Correction d'un état de chute qui empêchait une téléportation en donjon par LFG.
- Une chaîne de fabrication circulaire ne doit plus finir en dépassement de pile.
- Vigilance du guerrier est retravaillée pour sélectionner une cible DPS.
- Les noms de stratégies du voleur sont alignés sur ses spécialisations.
- Les stratégies de donjons WotLK sont correctement retirées/remplacées en
  changeant d'instance ; correction d'un conflit entre des actions de la Cime
  d'Utgarde et du Fort pourpre.
- La suppression de comptes de bots est corrigée pour les installations avec
  base d'authentification distante. **Cette capacité n'est pas activée chez nous.**

### Équipement : moins de choix fondés sur la couleur ou le niveau de l'objet

Le score privilégie les statistiques utiles : le niveau d'objet et la qualité
ne multiplient plus artificiellement la note une seconde fois. En PvE, la
résilience devient neutre dans ce calcul au lieu de recevoir une très forte
pénalité négative. Les armes des druides farouches sont mieux évaluées.

Un bot peut donc préférer un objet moins haut en niveau mais plus adapté à sa
spécialisation. Ce n'est pas un changement des caractéristiques des objets :
c'est son **choix d'équipement** qui évolue. Certaines corrections d'auras
réservées aux formes druidiques visent surtout les serveurs utilisant des objets
TBC restaurés par un autre module ; leur impact n'est pas à supposer sur Atlas.

Lors de la maintenance, une gemme ne dépasse plus la qualité de la pièce qui la
reçoit : par exemple, pas de gemme épique posée automatiquement dans une pièce
bleue. Une pièce épique peut toujours recevoir une méta-gemme de qualité bleue.

### Stratégies de raids Burning Crusade

Ces détails viennent des changements amont, pas d'une certification que nous
avons terminé tous ces raids sur Atlas :

| Raid | Apports concrets |
| --- | --- |
| Gruul / Maulgar | Meilleur écartement avant Fracasser, correction de l'immobilité après cette séquence, attribution de certains tanks et bannissements, répartition des distances. |
| Zul'Aman | Évitement des bombes de Jan'alai revu selon la forme réelle de l'arène ; Héroïsme/Furie sanguinaire mieux réservé aux vagues de jeunes faucons-dragons ; placement et portée des soigneurs revus pendant la phase aigle de Zul'jin ; dissipation et transitions améliorées. |
| Magtheridon | Attributions des bannissements/peurs revues ; gestion des cubes plus souple ; leurs utilisateurs peuvent continuer à attaquer et éviter les dangers en attendant ; ciblage moins dépendant des icônes de raid. |
| Hyjal | Évitement des zones de dégâts, gestion des infernaux, placement des tanks, gestion du mana sur Kaz'rogal et des marques ; feu et projection d'Archimonde ; moins d'interférences avec les stratégies de totems du chaman. |
| Donjon de la Tempête — l'Œil | Placement et phases d'Al'ar, esquive des orbes du Saccageur du Vide, ciblage et mouvements de Solarian ; lévitation, armes légendaires, phénix et œufs de Kael'thas. Les chevaliers de la mort choisissent des armes qu'ils peuvent réellement porter. |

Une partie du travail réduit aussi les calculs de stratégies de boss lorsque le
combat n'est pas en cours, et met en cache des recherches de dangers ou de cibles.
Cela ne suffit pas à annoncer un pourcentage de CPU gagné sur notre serveur.

### Nouvelle stratégie : Remparts des Flammes infernales

- Priorité aux soigneurs qui accompagnent Gargolmar.
- Sur Omor, écartement et réaction à l'aura de traîtrise, priorité aux chiens qui
  le soignent, avec une logique distincte si le tank est porteur du débuff.
- Sur Vazruden/Nazan, priorité à Vazruden avant Nazan pour éviter de s'éparpiller
  sur le dragon encore en vol ; placement du tank revu.

Enfin, le moniteur de performances des bots produit du JSON et mesure davantage
de valeurs. Le reste inclut la migration vers la base modulaire du core, des
adaptations de compilation et de la documentation.
[Comparaison Playerbots](https://github.com/mod-playerbots/mod-playerbots/compare/b6696bdbd3740e575598d167d69f39f68cc0b907...7bae1b5c58c76a0aa20381155edc08096d1485b2).

## 3. Dungeon Clear : deux nouveaux parcours gérés

### Épreuve du champion

Le module apprend à orchestrer les étapes particulières de ce donjon : monter
les chevaux, interagir avec l'annonceur, suivre l'avancement de la joute puis
enchaîner les combats. Il tient compte des portes, de certains dangers et du
bouclier de Paletress. Le suivi des objectifs ne dépend plus seulement de la
mort d'un boss, ce qui ne convenait pas à cette instance.

### Oculus

Le module ajoute la coordination des essences et des drakes, des vols entre les
plateformes, des atterrissages, des groupes à éliminer, des déplacements d'Urom
et du combat d'Eregos. Il évite notamment que le suivi ordinaire des bots entre
en conflit avec le déplacement organisé des drakes.

**La différence avec Playerbots** : Playerbots sait effectuer des actions et
réagir aux combats ; Dungeon Clear coordonne la progression du groupe dans tout
le parcours. Ajouter ce support ne garantit pas encore une réussite systématique
sur nos données de navigation, avec toutes les compositions.

### Déplacements et robustesse

Sur les cartes où le core désactive volontairement le pathfinding, Dungeon Clear
cesse d'attendre un maillage de navigation inexistant et utilise le trajet direct
prévu par le core. Cela corrige notamment des tentatives de récupération qui
pouvaient faire sortir le groupe de l'arène de l'Épreuve du champion. Ce n'est
pas une suppression générale des collisions ni une marche directe sur toutes les cartes.

L'initialisation des index de boss et des graphes de groupes de créatures est
sécurisée entre threads. D'autres changements adaptent le module et ses tests
aux nouvelles interfaces du core/Playerbots. Pas de nouveau SQL dans ce diff.
[Comparaison Dungeon Clear](https://github.com/jrad7/mod-dungeon-clear/compare/98929d6d261e615aa410f8e2d08ca83710fd2cad...805b909c7286348e75d0561f8cc259750e6ae62b).

## 4. HermesProxy : compatibilité client et fonctionnement des connexions

### Changements visibles avec le client 3.4.3

- Inspection des statistiques d'honneur au bon format.
- Déplacement d'un membre entre sous-groupes de raid transmis au serveur.
- Affichage du familier après passage à l'écurie, charme ou véhicule ; correction
  de l'identification des familiers dans les échanges.
- Arrêt d'attaque mieux synchronisé et trajets aériens/taxis mieux interprétés.
- Meilleure prise en charge de paquets envoyés par le client, de jets de butin
  et de pings de minicarte.
- Dans l'Œil du cyclone, déclenchements des tours par proximité corrigés.
- Le chargement du personnage attend que le client connaisse le joueur avant
  d'envoyer certaines de ses données : cela vise les incohérences d'ordre de paquets.
- Les emplacements du porte-clés et des jetons de monnaie sont correctement
  reconnus dans l'inventaire, notamment pour les objets liés aux quêtes.
- Un bloc de mise à jour incorrect ne fait plus perdre automatiquement tout le
  paquet qui le contient.

Le lot comprend aussi la prise en charge des déclinaisons de noms en russe et
la lecture de lettres de versions antérieures à 3.3.0. Ces changements existent,
mais leur intérêt direct est moindre pour notre usage français du serveur WotLK.

### Connexion du launcher

L'amont accepte mieux les identifiants web mis en cache lors de la connexion
launcher et retire le ticket d'une session détruite. Un faux launcher de test
est ajouté. **Cela ne remplace pas automatiquement notre SSO Atlas**, qui utilise
ses propres échanges et doit être conservé/testé pendant le port.

### Refonte technique et performances

- Les tables de dispatch de centaines de messages sont générées à la compilation,
  au lieu d'être découvertes par réflexion à l'exécution.
- Une file d'envoi commune organise les paquets immédiats et ceux qui attendent
  une information ou un événement.
- Le travail d'une session s'exécute de manière sérialisée : les opérations
  sensibles d'une même connexion ne se chevauchent plus librement entre threads.
- Les attentes face à une connexion bloquée ou morte sont bornées ; certains
  détails réseau et la fermeture propre du proxy sont corrigés.
- Compression des gros paquets 3.4.3, réutilisation de buffers et création des
  données rares uniquement quand nécessaire : moins d'allocations inutiles est
  l'objectif, **pas un gain de fluidité ou de RAM déjà mesuré sur Atlas**.
- Mesures de latence plus détaillées, respect de la désactivation des captures
  de paquets et ajustements de publication automatique.

C'est le dépôt qui demande le plus gros travail de reprise de nos personnalisations :
des fichiers où notre ancien correctif se branchait ont disparu ou ont changé de
responsabilité. Il faut préserver la boutique, les services de compte, le chat,
les identités et les connexions, pas simplement réussir une compilation.
[Comparaison Hermes](https://github.com/Xian55/HermesProxy/compare/bcacff9505a3da2f12c18a896461f5e901f7d367...5ea8767f0edbd1496511053d7a30136f7963b0a5).

## 5. AH Bot : correction du choix des objets proposés

Un objet peut avoir plusieurs origines, par exemple fabrication et butin.
La nouvelle logique l'autorise si **au moins une** de ses sources est activée.
Auparavant, une autre source désactivée pouvait l'exclure malgré sa source autorisée.

Cela peut modifier le catalogue alimenté à l'hôtel des ventes en fonction de nos
filtres actuels. Ce commit ne prétend pas ajouter une nouvelle IA économique ni
recalculer toute la politique de prix. Notre personnalisation Atlas des propriétaires
d'enchères est dans un autre fichier et doit être conservée.
[Commit AH Bot](https://github.com/azerothcore/mod-ah-bot/commit/c11d8318cbd8714a9980f9464f78e07d3d48a70a).

## 6. Ce qui n'a pas de nouvelle version identifiée dans cet audit

- **Transmog** : même version amont ; conserver le SQL/personnalisations locales.
- **Account Achievements** : même version amont.
- **Atlas Armory, Friends, Chat et Shop** : fichiers du lot installé contrôlés,
  pas de nouveauté amont identifiée à intégrer dans ce périmètre.
- **Launcher, branche `ui/redesign-v2`** : aucun commit distant supplémentaire
  à récupérer lors de l'audit. Les scripts et rapports de cette préparation
  constituent un nouveau travail, distinct d'une mise à jour fonctionnelle du launcher.
- Le lot ne comprend pas de mise à jour du client, de ses addons, de MySQL,
  du système d'exploitation ou des autres applications hébergées.

## Ce qu'il faut retenir pour jouer

Le gain attendu est surtout **moins de bugs de contenu**, **des bots mieux adaptés
à plusieurs rencontres BC**, **deux parcours Dungeon Clear supplémentaires** et
**une meilleure traduction du client 3.4.3 par Hermes**. Les changements de métiers,
de choix d'équipement des bots et de sélection d'objets d'AH peuvent être visibles
même sans changer les nombres dans la configuration.

Les principaux risques d'intégration sont le couple core/Playerbots autour des
bases de données, le port Atlas dans Hermes et la navigation des bots. C'est
pour cela que compilation, copies SQL, tests de protocole et tests de parcours
sont traités séparément avant toute éventuelle remise en service.
