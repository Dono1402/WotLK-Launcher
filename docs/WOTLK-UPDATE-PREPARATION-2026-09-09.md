# Préparation des mises à jour WotLK — 9 septembre 2026

## Périmètre

Préparation et tests de candidats séparés après l'audit du 9 septembre. Cette
phase n'installe pas les addons dans le jeu, ne publie pas le catalogue et ne
modifie aucun service, configuration privée ou base de production.

Les paquets, sources de travail et sorties de tests restent hors Git. Les
références, correctifs source nécessaires et résultats vérifiés sont consignés
ici pour préparer une validation ultérieure reproductible.

## Bases figées

| Composant | Base réellement utilisée | Cible examinée |
| --- | --- | --- |
| HermesProxy | Atlas `01667dc73262db01b10a5fcb99b505528e52211c` avec le patch whispers actif du 7 septembre | Amont `4247d957b78e6621047783560b3606f8fcf7ff42` en conservant tous les ajouts Atlas |
| Dungeon Clear | `c32a38436567328d4f8f9a6ee0d221f19d58301e` dans le world personnalisé du 7 septembre | `3dd90f7c1122abc291edcbd9d3dc68f6c8fed0bc` |
| WeakAuras | 5.12.8, client 3.4.3.54261 / interface 30403 | 5.12.9 retenue pour un futur test client ; 5.13.1 mise en attente |
| Auctionator | 10.2.0-wrath | 10.2.24-wrath examinée, sans installation |

Le world actuellement actif conserve les personnalisations du core et de
Playerbots, AH Bot, Transmog, Account Achievements, Atlas Friends, Atlas Armory
et Atlas Chat. La source d'Armory utilisée pour le binaire actif est celle du
candidat `armory-live-20260905T1310Z`, plus récente que la copie située dans le
dossier principal du core. Une reconstruction depuis ce dernier seul ne serait
donc pas fidèle.

## Contraintes constatées avant compilation serveur

Contrôle à 10:25 UTC : le volume système Linux est occupé à 98 %, avec environ
12 Gio disponibles (`/dev/md3`, 467 Gio). Le `/tmp` est un tmpfs de 16 Gio, pas
un disque de secours : la mémoire disponible n'était que d'environ 10 Gio et
la swap était déjà partiellement utilisée.

Après autorisation distincte de l'utilisateur, 24 répertoires de sauvegarde
de juillet contenant d'anciens exécutables worldserver ont été supprimés à
10:43:26 UTC. Les sauvegardes d'août/septembre, les sauvegardes SQL, les
configurations seules et tous les anciens candidats/builds/sources ont été
conservés. La suppression est définitive, sans corbeille ni nouvelle copie
des fichiers supprimés ; les snapshots plus récents restent disponibles.

La [liste exacte](update-preparation/2026-09-09/backup-cleanup-result.json)
est versionnée ; les métadonnées détaillées avant/après sont conservées sur le serveur
dans `/opt/arthas-next/maintenance/backup-cleanup-20260909`. Le contrôle final
prouve que seuls ces 24 dossiers ont disparu (47 vers 23 dossiers conservés),
pour **12 530 073 600 octets alloués libérés**. L'espace disponible est passé de
12 246 401 024 à **24 776 450 048 octets**, et l'occupation de 98 % à 95 %.
Les PID worldserver `1910234` et Hermes `1910530` sont restés inchangés,
avec zéro redémarrage automatique.

La préparation native utilise désormais de nouveaux candidats séparés, avec
priorité basse, limites CPU/mémoire et seuil minimal de 8 Gio libres. Elle ne
modifie pas les exécutables actifs, leurs configurations ou les bases.

## Risques révélés pendant la préparation

- Hermes : le conflit dans `BnetRestApiSession.ReadHandler` est résolu en
  conservant les adaptations Atlas, la gestion d'erreurs et la fermeture
  contrôlée de la session fautive. Les diagnostics d'authentification ne
  journalisent plus que des métadonnées à valeurs limitées : aucun contenu,
  header, query, segment libre d'URL ou message d'exception fourni par l'entrée.
- WeakAuras : la déclaration TOC `30403` ne suffit pas à certifier les nouvelles
  références aux API `C_AddOns` et `C_Item` de 5.13.1. La référence API disponible
  concerne 3.4.3.53788, pas exactement 54261 : elle révèle un risque, pas une
  preuve d'incompatibilité. 5.12.9 garde les anciens chemins de compatibilité
  et apporte la correction de chargement lors du changement de double
  spécialisation. Son `Modernize.lua` est identique au 5.12.8 installé
  (version interne 73).
- Auctionator : migration de la base de prix du schéma 6 au schéma 7. Un retour
  à 10.2.0 doit restaurer le SavedVariables sauvegardé avec l'ancien addon ;
  remplacer seulement les fichiers Lua ne suffit pas à préserver la base.

Les correctifs installés d'ElvUI et l'essai séparé de SimpleDungeonMap sont
hors du périmètre de mutation. Aucune vérification automatisée hors du jeu ne
sera présentée comme une validation visuelle ou un parcours de donjon complet.

## Vérifications des addons

Le [manifeste versionné](update-preparation/2026-09-09/addons-manifest.json)
consigne les URLs, tailles et SHA256 exacts des trois archives. WeakAuras 5.12.9
est le seul palier sélectionné pour un futur test client. 5.13.1 et Auctionator
10.2.24-wrath restent en attente pour les raisons ci-dessus ; aucune mise à
jour d'Auctionator n'est présentée comme une correction du protocole AH.

Les [scripts reproductibles](../scripts/addon-update-validation/README.md)
ont été réexécutés après leur mise en forme portable :

- 710 fichiers Lua compilés sans exécution ni erreur, avec les limites du
  parseur Lua 5.1 explicitement consignées et un second compilateur Lua 5.3 ;
- 267 documents XML valides, DTD et accès externes interdits, 121 handlers Lua
  intégrés analysés sans erreur ;
- 25 assertions synthétiques de migration et changement de spécialisation ;
- 8 fichiers de tests LibStub, soit 104 assertions réussies ;
- arbres de chargement Wrath, dépendances et empreintes des fichiers protégés
  contrôlés sans modification de l'installation.

Une dernière comparaison SHA256 des extractions avec les entrées des trois ZIP
épinglés confirme **1 830 fichiers identiques**, sans fichier supplémentaire.

Pour Auctionator, le test utilise des données inventées : il confirme la
conservation des prix lors de la migration 6 vers 7, puis leur remise à zéro
par l'ancien code si le retour à 10.2.0 ne restaure pas aussi SavedVariables.
Les données des groupes personnalisés subsistent, mais la nouvelle interface
ne conserve pas leur édition comme auparavant. Un cas synthétique de liste
de groupes vide reproduit également une absence de garde ; cela ne prouve pas
que le profil réel est concerné, puisqu'il n'a pas été lu.

Les chemins d'entrée des scripts sont maintenant explicites, sans installation
de jeu sélectionnée par défaut. Les sorties vont uniquement dans un dossier
de préparation distinct de la référence en lecture seule. Les paquets et les
résultats bruts demeurent hors Git ; les dépendances sont épinglées avec un
lockfile et installées sans scripts.

## Vérification d'Hermes

La fusion source est figée dans `f859d0c59696b62483a98133b1e064c15dcb5604`.
Le [patch complet et sa provenance](../mod-atlas-chat/hermes/README.md)
reproduisent exactement son arbre depuis l'amont public `4247d957`.

Les tests Windows puis Linux natifs obtiennent **1 150 réussis, 0 échec,
9 ignorés sur 1 159** avec culture invariante, puis **194/194 sans ignoré**
sur le lot configuré pour 3.4.3. Les 9 tests ignorés sont spécifiques à ce
client et sont tous exécutés dans ce second lot. Les nouveaux tests couvrent
les statistiques Int16 des objets, les transports/trams, les destructibles
Atlas, et la non-divulgation des entrées dans les diagnostics d'authentification.

La suite Windows exécutée avec sa culture française par défaut obtient
1 149 réussis, 1 échec et 9 ignorés : le test de métriques préexistant attend
un séparateur décimal anglais. L'application impose déjà la culture invariante
au démarrage. Cet échec est conservé dans les résultats bruts, pas compté
comme une réussite ni corrigé silencieusement.

Le build Linux utilise le SDK 10.0.400 et confirme l'absence de `_WINDOWS`.
La restauration de dépendances est séparée des tests : ceux-ci utilisent un
réseau privé réduit à loopback, un système de fichiers en lecture seule sauf
le nouveau candidat, et des sockets SQL inaccessibles. Le `/tmp` du processus
est un montage du sous-dossier temporaire du candidat. Le premier essai a
échoué sur un mutex NuGet tentant d'écrire dans `/tmp` malgré `TMPDIR` ; cette
adaptation du montage est consignée, sans changement de source après gel.

Ces résultats ne certifient pas une authentification réelle, une session de
jeu, un trajet de transport ou une interaction avec le world mis à jour.

Publication native terminée à 10:54:59 UTC, sans erreur. Les 79 avertissements
du build et les avertissements de trimming sont conservés dans les logs ;
l'exécutable publié n'a pas encore été démarré. Le
[manifeste Hermes](update-preparation/2026-09-09/hermes-manifest.json) donne
les options exactes et les empreintes des preuves.

L'archive `hermes-f859d0c-linux-x64-native.tar.gz` fait 23 784 361 octets,
SHA256 `61785e2db59b0586874fe055c2e94549d2f9de5d3375b4d73fb295eab3e38d61`.
Les 233 fichiers du paquet, dont 114 fichiers de données CSV, ont été
revérifiés après extraction locale. Les CSV correspondent tous à `4247d957`.
Les configurations (même publiques), PDB, logs, données de comptes et
certificats Atlas ne sont pas dans l'archive.

## Préparation du world / Dungeon Clear

La cible `3dd90f7c` représente exactement trois commits et 28 fichiers depuis
`c32a3843`. Elle ne contient aucune migration SQL. La nouvelle fonctionnalité
d'attribution des prérequis peut néanmoins modifier les personnages bots
lorsqu'elle est utilisée : l'absence de fichier SQL ne signifie donc pas
qu'un test de world peut être exécuté sans risque sur les bases actives.

La capture des sources identifiées du world actif a vérifié 12 392 fichiers
par SHA256, dont les 507 blobs Dungeon Clear de la base. Les personnalisations
Atlas, Playerbots, AH Bot et Transmog restent préservées ; les sources Armory
et Chat proviennent bien des overlays réellement intégrés à l'exécutable actif.

Premiers contrôles locaux réussis :

- 18 compilations C++ en objets Windows COFF : 12 fichiers DC nouveaux/modifiés
  et six fichiers Atlas Armory/Friends/Chat ;
- 23 tests upstream du noyau de décision Fosse de Saron, extraits sans changer
  leurs assertions ; ce n'est pas encore la suite DC complète ;
- contrôles upstream de déterminisme, lectures de configuration et portabilité
  (404 fichiers examinés).

Pour la validation native, la méthode commence par reproduire le lien du
world actuel dans un nouveau fichier et comparer ses sections chargées et
en-têtes de programme ELF. Elle reconstruit ensuite les **173 unités C++ DC**
et les **75 unités de sa suite de tests**, sans remplacer l'archive Armory ni
les objets Chat utilisés par la production. Les cartes de lien doivent prouver
que les 170 anciens membres DC n'ont pas été repris depuis cette archive et
que les huit enregistrements de modules Atlas restent présents.

Le constructeur refuse toute sortie hors du nouveau candidat, vérifie les
sources et entrées de compilation, et invalide les étapes dépendantes en cas
d'échec. Il ne démarre jamais worldserver, n'exécute aucun SQL et ne passe pas
par CMake/Make dans les anciens dossiers de build.

Les [recettes natives versionnées](../scripts/wotlk-update-20260909/README.md)
sont identiques aux scripts exécutés ; dix tests de contrat ont été rejoués
avec succès après copie dans ce dépôt. Elles dépendent des snapshots et
archives historiques, et ne constituent pas un build autonome depuis un
clone de ce dépôt seul.

Point de contrôle du **9 septembre à 11:05:23 UTC** : la phase baseline Linux
est réussie. Le fichier reconstruit de 2 468 468 944 octets est identique
intégralement au world actif :
`de9f14523f7b933b714904f05cb0d4bb6d65f6592c9cd4d2aaeb0de125bfe315`.
Cette preuve SHA256 intégrale est plus forte que la seule comparaison des
sections ELF. Un diagnostic non fatal du linker sur la taille de `.debug_info`
a été conservé ; le processus a terminé avec code 0 et aucun relèvement de
limite. Les phases compilation DC, lien candidat et tests complets sont encore
en cours à ce point de contrôle : aucun world mis à jour n'est déclaré validé.
