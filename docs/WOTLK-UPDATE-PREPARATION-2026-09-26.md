# Préparation isolée WotLK du 26 septembre 2026

## Périmètre

Préparation et tests uniquement, sans bascule de production. Le lot de référence
est celui installé le 13 septembre. Les valeurs de production restent figées,
notamment **1 000 bots**. Aucun client graphique, launcher ou jeu n'a été ouvert.

L'[explication fonctionnelle](WOTLK-UPSTREAM-CHANGES-2026-09-26.md), l'[index des
318 commits](WOTLK-UPSTREAM-COMMITS-2026-09-26.md) et la [recette reproductible](../scripts/wotlk-update-20260926/README.md)
séparent les nouveautés amont de leur validation sur Atlas.

La campagne isolée est terminée : le candidat est construit et testé, avec
**cinq réserves de navigation connues**, mais **n'est pas déployé**. Le dernier
contrôle de conservation date du 26 septembre à 04:31 UTC. Les [preuves
agrégées](update-preparation/2026-09-26/validation-summary.json) contiennent les
empreintes, résultats, limites et contrôles d'arrêt, sans données de comptes.

## Versions et conservation

- Core amont `7f12e89ee5f467a50e62eba1d525eac7dc953d03` ; intégration Atlas
  `99ece3bc2b53` dans le candidat.
- Playerbots amont `7bae1b5c58c76a0aa20381155edc08096d1485b2`, avec la réservation
  de bots de guildes portée vers la nouvelle base modulaire.
- Dungeon Clear `805b909c7286348e75d0561f8cc259750e6ae62b`.
- AH Bot `c11d8318cbd8714a9980f9464f78e07d3d48a70a`, propriétaire Atlas conservé.
- Hermes amont `5ea8767f0edbd1496511053d7a30136f7963b0a5`, fonctions Atlas adaptées
  aux nouveaux systèmes, codecs et mécanismes d'envoi.
- Transmog, Account Achievements, Armory, Friends, Chat et Shop conservés depuis
  les sources installées, avec leurs personnalisations.
- API du banc identique au paquet installé : exécutable contrôlé, puis **330
  fichiers de runtime** comparés sans différence.

Le candidat utilise `/opt/arthas-next/candidates/atlas-all-update-20260926`.
Sa configuration pointe uniquement vers `/opt/atlas-shop-tests/rename-20260926/etc`.
Cette configuration de test n'est pas une future configuration de production.

## Vérifications de construction et de données

| Vérification | Résultat établi |
| --- | --- |
| Compilation complète core + modules + Auth + tests | Réussie en RelWithDebInfo, six tâches de compilation |
| Copie à froid des bases de production | Tous les fichiers vérifiés par checksum avant ouverture de la copie |
| Migrations sur copie de production | 62/62 nouveaux scripts World appliqués, sans échec |
| Migrations sur copie du banc synthétique | 62/62 appliqués, sans échec |
| Références Auth sur copie de production, contrôlées sans modification SQL | 11 versions de client dont 12340 ; 654 permissions, 653 liens RBAC, 4 permissions par défaut présentes |
| Tests C++ core | 5 935 réussis, 0 échec, 5 487 ignorés/désactivés sur 11 422 cas |
| Tests C++ Dungeon Clear, premier passage | 1 685 réussis, 5 échecs, 1 ignoré sur 1 691 cas |
| Test de géométrie initialement ignoré, après ajout des extraits privés | Réussi : 29 scénarios sur les cartes 229, 545, 560 et 658 |
| Suite Dungeon Clear complète rejouée avec ces extraits | 1 686 réussis, les mêmes 5 échecs, 0 ignoré |
| Hermes, suite par défaut après correction | 2 918 réussis, 16 ignorés, 0 échec sur 2 934 cas |
| Hermes, sélection Atlas/compatibilité 3.4.3 | 618 réussis, 0 ignoré, 0 échec ; recoupe partiellement la suite précédente |
| Or → portefeuille → renommage, avec redémarrage World | 30 contrôles réussis |
| Identité et caches du nom à travers Hermes | 20 contrôles réussis |
| Services de compte à travers Hermes | 20 contrôles réussis |
| Services de compte directement sur le core, accès concurrents et redémarrage | 36 contrôles réussis |
| Protocole amont : connexion, groupes, échanges, session, mort/résurrection | 25 scénarios réussis, 0 échec, 0 ignoré |
| Protocole amont : Ulduar | 12 scénarios réussis en 519 s, 0 échec, 0 ignoré |
| Protocole amont : Stratholme | 1 scénario réussi en 15 s |
| Social Atlas avec tous les modules | 5 contrôles réussis : amis, Armory et messages launcher/jeu dans les deux sens |
| Réservation des bots de guilde après redémarrage | 4 contrôles réussis : appartenances, événements permanents, 2 places occupées par les 2 bots réservés, trace de priorité |
| Non-régression du récupérateur de résultats Dungeon Clear | 5 cas réussis : précision à la seconde et exclusion des parcours préexistants |
| Mortemines autonomes, première exécution | Résultat serveur réussi : 7/7 boss, cinq bots niveau 80, 602 s, aucune mort ; collecteur corrigé ensuite |
| Mortemines autonomes, seconde exécution complète | Test réussi avec un nouvel identifiant : 7/7 boss, cinq bots niveau 80, 694 s, aucune mort |

Le test supplémentaire complète donc le cas ignoré de Dungeon Clear ; il ne
supprime pas les cinq échecs. Les cas ignorés du core comprennent notamment des
paramétrisations qui ne s'appliquent pas à certains sorts, et un test réservé
à une construction avec AddressSanitizer. Ils ne sont pas comptés comme réussis.

Les conteneurs de copies MySQL ont un réseau `none`, aucun port publié et une
limite de 2 Gio. Le conteneur de production `arthas-mysql` n'a pas été démarré.
La réussite des migrations sur copie ne remplace pas une sauvegarde et une
procédure de bascule si une installation est autorisée ultérieurement.

Les 38 scénarios de protocole comptent les tests Go de premier niveau, sans
additionner une seconde fois leurs sous-tests. Les 12 cas Ulduar couvrent
notamment la réinitialisation de Thorim, les vagues et récompenses de Freya,
la ligne de vue et la santé mentale de Yogg-Saron, les rayons de Brillefeuille,
l'interruption de Tidal Wave et la stase d'Algalon après Big Bang. Ce ne sont
pas douze raids complets ; le cas Charge/Kologarn commenté dans les sources
amont n'a pas été exécuté et n'apparaît pas dans le compteur des tests ignorés.

## Réserves de navigation

Les cinq noms en échec sont identiques à ceux du lot précédent :

1. `UtgardePinnacleRouteProbe.EveryLegRoutesEndToEndWithNoLedge` : un segment vers
   Ymiron n'atteint pas sa destination et prend un détour trop long.
2. `PitOfSaronRouteProbe.TheArmStagingPointIsClearOfGateOne` : un point d'attente
   est trop proche du déclencheur de l'embuscade selon l'oracle du test.
3. `CullingOfStratholmeRouteProbe.TheApproachAndTheCrateRoadRoute` : trajet
   d'approche incomplet vers Chromie.
4. `CullingOfStratholmeRouteProbe.TheTownHallAndMarketLegsRoute` : trajet incomplet
   vers la porte de l'hôtel de ville.
5. `CullingOfStratholmeRouteProbe.TheCorruptorIsOnlyNearArthasPreMalganisStop` :
   les distances de navigation vers le Corrupteur ne correspondent pas aux
   hypothèses du scénario, notamment sans prise en compte des portes par le maillage.

Aucune assertion n'a été assouplie pour rendre ces tests verts. Ces résultats
signalent des écarts à examiner ; ils ne constituent pas cinq nouveaux bugs
introduits par ce lot, ni cinq parcours de donjon rejoués graphiquement.

## Essais intermédiaires et corrections du banc/Hermes

- L'ancien correctif Hermes ne s'appliquait pas à la nouvelle architecture.
  Il a été porté en conservant les fonctions Atlas et leurs assertions de tests.
- Les tests spécifiques au client 3.4.3 sont exécutés avec cette version ; ils
  ne sont pas faussement évalués contre le format 1.14 de la suite par défaut.
- Les gardes de chemins de trois anciens tests pointaient encore sur le banc
  du 11 septembre. Elles ont été adaptées au chemin exact du nouveau banc,
  sans retirer le contrôle de réseau privé.
- Une interprétation initiale incorrecte du format du binaire installé a conduit
  à essayer NativeAOT. Ces paquets compilaient mais échouaient sur la sérialisation
  du ticket puis sur les délégués dynamiques des services Battle.net. Les essais
  sont archivés. L'examen des marqueurs de bundle .NET et des sections ELF a
  confirmé que la production utilise un paquet autonome monofichier avec runtime.
  La recette finale reprend ce format et ne prétend pas rendre Hermes compatible NativeAOT.
- La sérialisation du ticket utilise désormais des métadonnées générées et des
  réponses typées ; quatre cas supplémentaires vérifient la compatibilité de son JSON.
- Le test de redémarrage attendait un message de disponibilité resté dans le
  tampon de stdout jusqu'à l'arrêt, provoquant une expiration du test malgré
  l'absence de crash. Les deux lancements du World dans le banc utilisent
  désormais une sortie par ligne (`stdbuf`) ; délais et assertions sont inchangés.
- Le premier essai des clients Go était rejeté par Auth avant l'entrée en jeu :
  la copie de l'ancien banc synthétique avait une table `build_info` vide. Les
  références publiques des versions de client et les trois tables RBAC vides
  ont été complétées depuis le SQL du core figé, uniquement dans ce banc.
  Ce premier rejet ne constitue pas un échec des mécaniques de jeu concernées.
  Un contrôle séparé confirme que la copie de production possède déjà ces
  références, avec ses permissions supplémentaires ; elle n'a pas été réensemencée.
- Le premier parcours autonome des Mortemines s'est terminé avec **7/7 boss et
  cinq bots en 602 secondes, sans mort**, mais le client de test ignorait son résultat : `startedAtMs` est
  arrondi à la seconde par le serveur, tandis que le client comparait à la
  milliseconde. Le collecteur Go de cet essai a été arrêté après conservation
  du résultat serveur ; son code d'échec reste archivé. Le test corrigé utilise
  la précision réelle et rejette tous les identifiants de parcours présents
  avant la commande. Cinq cas purs contrôlent cette règle. Les exigences de
  réussite, de donjon, de niveau, de graine, de cinq bots et de tous les boss
  restent intactes. Une seconde exécution complète sert à vérifier la correction.
  Cette seconde exécution est réussie ; son identifiant est distinct de celui
  du premier parcours. Le premier résultat n'a donc pas servi à valider le rejeu.

Les journaux intermédiaires restent sur le serveur pour diagnostic. Ils ne sont
pas remplacés par les journaux d'une relance réussie.

Les journaux ne sont pas silencieux : le passage de protocole avec tous les
modules contient 15 erreurs MySQL `1213` (interblocages de transactions), sans
message d'abandon fatal. Le core possède une reprise explicite des transactions
interbloquées ; l'ancien banc présentait aussi ce type de message. Des
`send NULL_OPCODE` sont également présents, y compris dans les anciens journaux.
Cela reste consigné comme signal de diagnostic, sans en déduire un crash ni
garantir l'absence de tout problème SQL. Les quatre phases boutique/identité/
services n'ont pas produit de code MySQL `[ERROR]` dans leurs consoles inspectées.

## Limites et frontière de remise en service

Les essais de protocole pilotent des comptes synthétiques sur le banc privé.
Ils ne valident pas l'affichage du client, toutes les combinaisons de classes,
tous les raids, ni les performances avec 1 000 bots. Le test autonome des Mortemines
emploie cinq bots de niveau 80 : il sert à vérifier l'intégration et la progression
du parcours, pas son équilibrage au niveau normal.

Cette campagne sur comptes synthétiques ne permet pas non plus de déclarer
résolue l'ancienne erreur de doublon `pet_spell` mentionnée dans le [bilan de
production du 13 septembre](WOTLK-UPDATE-DEPLOYMENT-2026-09-13.md). Il faudrait un contrôle spécifique des données
et des circonstances concernées avant de conclure sur ce point.

Les données extraites du client, les secrets, les bases copiées, les paquets
compilés et les journaux contenant des comptes restent hors Git. Avant toute
mise en service : autorisation distincte, nouvelles sauvegardes vérifiées,
traitement des réserves, migrations contrôlées et vérification des services.
Un ancien script de déploiement ne doit pas être lancé tel quel sur ce candidat.

La compilation Hermes en réseau privé n'a pas pu consulter l'index de
vulnérabilités NuGet (`NU1900`). Les avertissements de compilation conservés
dans les journaux ne sont pas des erreurs de build, mais cette exécution ne
constitue pas un audit de sécurité des dépendances.

## État laissé après les essais

- Les **129 fichiers de production** suivis par empreinte sont inchangés.
- Les cinq services WoW de production et le conteneur `arthas-mysql` restent arrêtés.
- Les valeurs actives sur disque `MinRandomBots` et `MaxRandomBots` restent
  à **1 000** ; les suppressions de comptes, guildes et équipes d'arène restent à zéro.
- Aucun exécutable du candidat ou du banc n'est encore en cours d'exécution.
  Les deux conteneurs MySQL de copies sont arrêtés ; les preuves et copies sont conservées.
- Le manifeste du candidat indique la fin des contrôles isolés avec les cinq
  réserves connues, et explicitement l'absence d'activation de production.
  L'état d'échec de la première unité de test reste une trace historique du
  défaut de collecte corrigé, pas un service encore en fonctionnement.
- Les correctifs Hermes/Playerbots exportés correspondent exactement aux diffs
  des sources finales testées ; les 119 fichiers du paquet Hermes sont contrôlés.

La prochaine décision est une éventuelle procédure de remise en service,
distincte de cette préparation : ce rapport n'autorise ni un redémarrage
automatique, ni l'utilisation de la configuration synthétique en production.
