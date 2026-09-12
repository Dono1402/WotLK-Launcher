# Inventaire des modules et mises à jour disponibles — 12 septembre 2026

Inventaire du serveur entre 22 h 12 et 22 h 20 (Paris), en lecture seule.
**Neuf modules sont intégrés au World et activés dans sa configuration. Quatre
composants ont des mises à jour publiques à évaluer : Playerbots, Dungeon Clear,
le core et Hermes. Aucune mise à jour n'a été installée.**

Le périmètre couvre les modules serveur, le core et Hermes. Les addons du client
ne sont pas inclus dans cet inventaire. Les anciennes préparations ont servi de
repères ; les versions ci-dessous sont rattachées aux exécutables effectivement
chargés, à leurs manifestes et aux configurations résolues depuis les processus.

## Modules actifs

| Module | Fonction et configuration vérifiée | Version intégrée | État de l'amont suivi |
| --- | --- | --- | --- |
| Playerbots | Bots joueurs ; population configurée à 1 500 ; connexion des bots de guildes réelles activée | Atlas `cc4579af`, base publique `b949b50b` | **24 commits disponibles** vers `b6696bdb` |
| Dungeon Clear | Stratégies et progression des bots en donjon ; remplissage automatique RDF désactivé | `3dd90f7c` | **7 commits disponibles** vers `98929d6d` |
| AH Bot | Vendeur et acheteur automatiques à l'hôtel des ventes activés | `a680cc1c` + corrections Atlas | Aucun nouveau commit sur `master` |
| Transmog | Transmogrification, accès portable et ensembles activés | Atlas `36b4b72a`, base publique `0d85cbc5`, adaptations SQL | Aucun nouveau commit sur `master` |
| Account Achievements | Hauts faits partagés entre personnages du compte | `bfbe3677` | Aucun nouveau commit sur `master` |
| Atlas Armory | Profils, équipement et statistiques pour le launcher ; collecte en jeu activée, sans restriction à un GUID unique | Source `armory-live-20260905T1310Z/module` | Module interne Atlas |
| Atlas Friends | Synchronisation sociale avec le launcher | Source conservée dans la base du 5 septembre | Module interne Atlas |
| Atlas Chat | Chat et messages privés entre le launcher et le jeu | Source `atlas-chat-whispers-20260907/module` | Module interne Atlas |
| Atlas Shop | Boutique, conversion d'or et services de compte | Source `6a536c1c` du 12 septembre | Module interne Atlas ; dernier correctif d'affichage confirmé par l'utilisateur |

Les quatre modules internes sont entretenus dans le dépôt Atlas ; ils n'ont pas
de version publique indépendante à remplacer. « Activé » décrit ici les
enregistrements du binaire et la configuration, sans revendiquer un nouveau
test en jeu de chaque fonction.

## Core et proxy

| Composant | Version effectivement intégrée | Référence publique suivie | Nouveaux commits |
| --- | --- | --- | ---: |
| Core AzerothCore compatible Playerbots | Atlas `edfd79c4`, base `413bea61`, avec les adaptations Armory et Boutique | `mod-playerbots/azerothcore-wotlk`, branche `Playerbot`, cible `06234df3` | **69**, dont 66 hors fusions |
| HermesProxy | Base Atlas `f859d0c5`, amont `4247d957`, plus correctifs Atlas jusqu'à `6d4a7176` | `Xian55/HermesProxy`, branche `master`, cible `bcacff95` | **32**, dont 16 hors fusions |

Les 24 commits Playerbots comptent 23 commits hors fusions ; les 7 de Dungeon
Clear sont tous hors fusions. Les compteurs représentent des écarts d'historique,
pas autant de fonctionnalités. Ils sont calculés depuis la base publique déjà
intégrée, pour ne pas compter nos adaptations Atlas comme des nouveautés amont.

## Nouveautés utiles

### Dungeon Clear : nouveaux parcours de donjon

La mise à jour ajoute les stratégies des **Salles des Reflets** et de
**l'Épuration de Stratholme**. Elle apporte aussi des corrections pour la fuite
devant le Roi-liche, les portes et obstacles des Salles des Reflets, les dialogues
de PNJ gérés en C++ et le déclenchement de certains événements. L'intérêt pour
le contenu jouable est direct ; les parcours devront être testés sur Atlas.
[Comparaison des 7 commits](https://github.com/jrad7/mod-dungeon-clear/compare/3dd90f7c1122abc291edcbd9d3dc68f6c8fed0bc...98929d6d261e615aa410f8e2d08ca83710fd2cad).

### Core et Playerbots : préparer un ensemble compatible

Le 11 septembre, les évolutions précédemment examinées en `test-staging` ont
été intégrées aux branches suivies `Playerbot` et `master`. Elles font donc
maintenant partie des mises à jour ordinaires disponibles.
[Fusion du core](https://github.com/mod-playerbots/azerothcore-wotlk/commit/06234df3d5ab26c93f4f1f06f3edb828b73ecd3c),
[fusion de Playerbots](https://github.com/mod-playerbots/mod-playerbots/commit/b6696bdbd3740e575598d167d69f39f68cc0b907).

Le core corrige notamment des rencontres et véhicules d'Ulduar, la trajectoire
de Charge contre de très grandes cibles, des quêtes et créatures, ainsi que
l'acheminement de certains courriers. Sa comparaison comprend 28 fichiers SQL
modifiés : ce nombre n'est pas un décompte de migrations à appliquer à notre base.
[Comparaison du core](https://github.com/mod-playerbots/azerothcore-wotlk/compare/413bea61a85e20d9caef7d66fc601a661fdddd9d...06234df3d5ab26c93f4f1f06f3edb828b73ecd3c).

Playerbots améliore les stratégies de Gundrak, de la Terrasse des Magistères
et d'Auchindoun, le butin, les déplacements, les métiers et plusieurs comportements
de classes. Une nouvelle option permet de concentrer les bots dans les zones
adaptées au niveau où se trouvent de vrais joueurs ; elle est désactivée par
défaut. Son éventuelle activation serait un choix de configuration distinct.
[Comparaison Playerbots](https://github.com/mod-playerbots/mod-playerbots/compare/b949b50bfcdd4fab937781bac2d7765e39330e4b...b6696bdbd3740e575598d167d69f39f68cc0b907).

Le core retire une ancienne surcharge de `BuildChatPacket` et Playerbots adapte
ses appels. **Il faut préparer et compiler le couple ensemble.** Nos adaptations
de connexion des bots de guildes touchent des fichiers également modifiés en
amont. Les réglages actuels de suppression des guildes et des équipes d'arène
restent à `0` ; leur préservation fera partie de l'intégration.

### Hermes : corrections de protocole et optimisations

Les changements concernent notamment l'ouverture des serrures, l'arrêt du tir
automatique, les refus d'échange, les permissions de rangs de guilde, la
sérialisation des écritures BNet et plusieurs allocations de paquets et mises
à jour d'objets. Certaines corrections sont spécifiques à d'autres backends,
dont CMaNGOS ; leur présence ne prouve pas un défaut identique sur notre core.
[Comparaison des 32 commits](https://github.com/Xian55/HermesProxy/compare/4247d957b78e6621047783560b3606f8fcf7ff42...bcacff9505a3da2f12c18a896461f5e901f7d367).

La mise à jour change aussi la génération du protobuf et touche des fichiers
partagés avec nos adaptations. Une intégration devra conserver le SSO, le chat,
la boutique et le rafraîchissement du nom après chargement, puis réexécuter leurs
tests. Aucun conflit de fusion n'a encore été recherché par une fusion réelle,
et aucun gain de performance n'a été mesuré sur Atlas pendant cet inventaire.

## Modules sans nouvelle version publique

Les références distantes de [AH Bot](https://github.com/azerothcore/mod-ah-bot),
[Transmog](https://github.com/azerothcore/mod-transmog) et
[Account Achievements](https://github.com/azerothcore/mod-account-achievements)
correspondent aux bases publiques déjà intégrées. Les deux premiers conservent
des adaptations locales à préserver. Le bilan « aucun nouveau commit » ne
constitue pas un audit fonctionnel ou SQL de ces modules.

## Provenance et limites de l'inventaire

- Le SHA-256 du World chargé correspond au manifeste de la livraison du
  renommage du 12 septembre. Ce manifeste contient les neuf enregistrements
  de modules et conserve les objets Dungeon Clear du 9 septembre.
- Les **515 fichiers** de la source Dungeon Clear correspondent à son manifeste
  `3dd90f7c`, malgré la présence d'une copie plus ancienne dans le dossier de base.
- Les sources Atlas Shop correspondent au manifeste de construction actuel.
  Le manifeste Hermes correspond à l'exécutable réellement chargé et référence
  le correctif `6d4a7176` confirmé en jeu.
- Certains dépôts de modules ont des objets de parents historiques manquants.
  Pour Transmog, le parent direct de `36b4b72a` est bien `0d85cbc5` : l'erreur
  d'une recherche générale d'ancêtre ne signifie pas une divergence de version.
- Aucun code n'a été compilé, aucune migration appliquée et aucun candidat
  installé. Les PID World, Hermes, Auth et API, leurs chemins d'exécutables et
  les empreintes des configurations World/modules sont identiques en début et
  en fin d'inventaire. Aucun client graphique n'a été ouvert.

Le [rapport structuré](validation/wotlk-module-inventory-20260912.json) conserve
les références complètes, empreintes, branches suivies et listes de commits.

## Suite proposée

Préparer le **couple core + Playerbots**, puis intégrer Dungeon Clear pour les
nouveaux parcours, en conservant les adaptations Atlas. Préparer Hermes dans
un lot distinct pour pouvoir vérifier spécifiquement connexion, chat, boutique
et nom du personnage. AH Bot, Transmog et les hauts faits de compte ne nécessitent
pas de mise à jour publique à cette date. Cette proposition n'est pas une
autorisation de déploiement : le travail demandé ici s'arrête à l'inventaire.
