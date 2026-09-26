# Candidat isolé Atlas du 26 septembre 2026

> Le candidat a depuis été activé. Ce document décrit la préparation historique :
> ne pas relancer ces phases en production active. Voir la [procédure d'activation](activation/README.md)
> et le [bilan réel](../../docs/WOTLK-UPDATE-DEPLOYMENT-2026-09-26.md).

Cette recette prépare et teste les mises à jour sans activer la production.
Les services WoW et `arthas-mysql` doivent rester arrêtés. Les valeurs de
production restent inchangées, notamment les **1 000 bots** ; les petits pools
des tests appartiennent uniquement au banc synthétique.

Voir l'[explication détaillée des changements](../../docs/WOTLK-UPSTREAM-CHANGES-2026-09-26.md)
et l'[index intégral des commits](../../docs/WOTLK-UPSTREAM-COMMITS-2026-09-26.md).

| Composant | Dépôt amont | Commit figé |
| --- | --- | --- |
| Core / Playerbot | mod-playerbots/azerothcore-wotlk | `7f12e89ee5f467a50e62eba1d525eac7dc953d03` |
| Playerbots | mod-playerbots/mod-playerbots | `7bae1b5c58c76a0aa20381155edc08096d1485b2` |
| Dungeon Clear | jrad7/mod-dungeon-clear | `805b909c7286348e75d0561f8cc259750e6ae62b` |
| AH Bot | azerothcore/mod-ah-bot | `c11d8318cbd8714a9980f9464f78e07d3d48a70a` |
| Hermes | Xian55/HermesProxy | `5ea8767f0edbd1496511053d7a30136f7963b0a5` |

Les modules Atlas, Transmog et Account Achievements sont repris depuis le lot
installé, empreintes contrôlées. Le correctif propriétaire AH Bot est conservé.
Les correctifs de ce dossier s'appliquent aux SHA ci-dessus. Le correctif core
reste celui de `../wotlk-update-20260912/patches/core-06234df-atlas.patch`.
Il comprend aussi le test `CorpseReclaimDelayTest.cpp`, à ajouter à l'index.

## Recette et dépendances privées

Ces scripts sont liés à cette machine et à cette préparation, pas un installateur
générique. Ils refusent plusieurs réexécutions pour préserver les résultats.
Ne pas supprimer leurs gardes ou réutiliser les anciens scripts d'activation.

- Candidat : `/opt/arthas-next/candidates/atlas-all-update-20260926`.
- Banc synthétique : `/opt/atlas-shop-tests/rename-20260926`.
- Copie de migration : `private/migration-copy` sous le candidat.
- Anciennes sources, configurations privées, données de jeu, SDK .NET/Go et
  image MySQL sont des dépendances locales contrôlées, non fournies par Git.
- Les comptes et secrets restent dans les dossiers privés du serveur. Aucun
  export SQL, journal contenant des données de compte ou exécutable n'est versionné.

Les entrées de `inputs/` sont copiées depuis cette recette et celle du
12 septembre : `core-atlas.patch`, les deux nouveaux correctifs,
`prior-tests/`, `run_cpp_suites_20260912.py` et `world-migrations.json` (62 migrations World).
Le script `export_patches.py` régénère les deux nouveaux correctifs depuis les
clones de travail locaux ; il n'est pas requis pour appliquer ceux déjà versionnés.

## Étapes

1. `prepare_candidate.py prepare` fige les sources, conserve les modules et
   relève les empreintes actuelles de 129 fichiers de production. `build`
   compile en RelWithDebInfo, six tâches, tests inclus, dans le candidat.
   `verify` contrôle les empreintes et l'arrêt des services et de MySQL.
2. `prepare_hermes.py prepare` applique le port Atlas à l'amont figé, avec
   l'historique Git nécessaire à GitVersion. `build` compile les tests en Release.
3. `prepare_databases.py` fait deux copies à froid, vérifie tous les fichiers
   par checksum, utilise des conteneurs sans réseau et applique les 62 migrations
   uniquement aux copies. Les migrations déjà enregistrées doivent avoir le
   même SHA-1. La base de production n'est jamais démarrée.
4. `prepare_validation.py` adapte les chemins des bancs précédents, utilise le
   nouvel Auth et recompile les huit exécutables Go contre le nouveau core.
   Les critères fonctionnels des tests sont conservés. `build` seul reprend cette compilation.
   `patch_protocol_clock.py` adapte l'horodatage Dungeon Clear à la précision
   réelle du serveur et exclut explicitement tous les identifiants de parcours
   présents avant le lancement. `clock-test` recompile le test Atlas et vérifie
   cinq cas purs de fraîcheur, dont un ancien parcours de la même seconde.
5. `validate_candidate.py after-build` attend la compilation, installe dans le
   candidat, lie sa configuration au banc, puis exécute les suites C++.
   `hermes-tests` exécute la suite par défaut puis les tests Atlas/compatibilité
   3.4.3. `hermes-publish` produit le paquet Linux autonome, réduit et monofichier
   avec le runtime .NET, comme le format installé — pas NativeAOT.
6. `seed_auth_reference.py` complète les quatre tables de référence Auth vides
   de l'ancien banc synthétique depuis le SQL public du core figé : versions
   de client et permissions RBAC. Il refuse des tables déjà remplies et ne
   modifie ni comptes, ni bases de production. Sans `build_info`, Auth rejette
   les clients de protocole avant leur entrée en jeu.
   `verify_production_copy_auth.py` contrôle séparément en lecture seule que
   ces références et le client 12340 existent dans la copie de production,
   puis arrête son conteneur. Aucun remplissage n'est appliqué à cette copie.
7. `run_realm_validation.py basic` lance les phases or, identité, services et
   protocole natif. `modules` lance ensuite les suites de protocole, le social
   Atlas, un parcours Dungeon Clear avec cinq bots et la réservation de guilde.
   `after-basic` attend la réussite et le nettoyage des quatre phases de base.
   `dungeon-retry` archive l'essai dont le résultat a été rejeté par l'ancien
   contrôle d'horloge, puis rejoue uniquement ce parcours ; il exige la réussite
   des autres contrôles de modules et du test de non-régression d'horodatage.
8. `verify_nav_slices.py` prépare les extraits privés de navigation nécessaires
   au test de géométrie supplémentaire. Les cartes dérivées du client restent
   ignorées par Git. `rebuild_hermes_validation.py` permet de rejouer compilation,
   tests et publication après archivage explicite des preuves d'un essai précédent.
9. `collect_results.py` contrôle à nouveau les empreintes de production et
   rassemble les seuls résultats agrégés dans `evidence/public-summary.json`.
   Une phase absente reste explicitement absente, jamais déclarée réussie.
   Il refuse des tests ou copies SQL encore actifs. L'option
   `--finalize-manifest` vérifie les prérequis et marque le candidat comme
   contrôlé avec les cinq réserves de navigation connues, sans l'activer.

Les compilations et les tests sont lancés dans des unités systemd transitoires
bornées. Les mondes de test ont `PrivateNetwork=yes`, un système de fichiers
protégé et uniquement le banc/les preuves accessibles en écriture. Les conteneurs
MySQL utilisent `--network=none`, des sockets Unix, 2 Gio maximum et aucun port
publié. Les tests complets de modules ont un plafond de 12 Gio et exigent au
moins 14 Gio disponibles avant démarrage. Chaque phase arrête son propre monde
et son propre conteneur. Ce ne sont pas les unités de production.

## Particularités du port Hermes

Les anciens gestionnaires CMSG ont été intégrés aux nouveaux systèmes statiques,
codecs et tables de dispatch générées. Le port conserve notamment les services
de compte, le SSO, les messages `#Launcher`, l'identité 3.4.3, les achats et les
talents. Les octets des messages d'addon sont conservés sans transcodage UTF-8.
La fermeture d'un ancien socket ne doit ni fermer la connexion de remplacement,
ni vider ses files de paquets ; les tests couvrent ce cycle de vie.

Les cinq tests propres au format natif 3.4.3 sont exclus de la suite par défaut
1.14, puis réellement exécutés dans la seconde suite. Les six nouveaux
gestionnaires Atlas apparaissent dans le snapshot de dispatch, sans suppression
de gestionnaire amont. Le ticket du launcher utilise des DTO et métadonnées JSON
générés, avec tests du format existant. Un essai NativeAOT a montré des
incompatibilités de sérialisation puis du dispatch Battle.net ; ce format avait
été choisi sur une mauvaise interprétation du binaire installé. Les marqueurs
du bundle .NET et les sections ELF ont ensuite établi la différence. L'essai est
archivé et abandonné au profit du profil de publication amont `UsePublishBuildSettings`.

Une compilation, un test unitaire ou une migration réussis ne valent pas
validation d'un parcours de jeu. Les résultats finaux et les limites doivent être
consignés dans le rapport de préparation avant toute demande de bascule.
L'activation et la remise en service de production nécessitent une autorisation
distincte et un plan de sauvegarde/migration/retour arrière adapté.
