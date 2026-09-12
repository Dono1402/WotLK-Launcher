# Candidat groupé Atlas du 12 septembre 2026

Cette préparation réunit le core, Playerbots, Dungeon Clear et Hermes dans un
nouveau candidat. Les versions amont sont figées :

État final de cette campagne : compilation réussie, validation partielle,
aucun déploiement. Voir le [compte rendu](../../docs/WOTLK-UPDATE-PREPARATION-2026-09-12.md)
pour les tests réussis, les cinq échecs de navigation et la limite mémoire
du banc. Les recettes Go, sociales et de guilde ci-dessous ne sont pas
présentées comme exécutées.

| Composant | Dépôt amont | Commit |
| --- | --- | --- |
| Core, branche Playerbot | mod-playerbots/azerothcore-wotlk | `06234df3d5ab26c93f4f1f06f3edb828b73ecd3c` |
| Playerbots | mod-playerbots/mod-playerbots | `b6696bdbd3740e575598d167d69f39f68cc0b907` |
| Dungeon Clear | jrad7/mod-dungeon-clear | `98929d6d261e615aa410f8e2d08ca83710fd2cad` |
| Hermes | Xian55/HermesProxy | `bcacff9505a3da2f12c18a896461f5e901f7d367` |

Les correctifs complets depuis ces versions sont conservés dans
`patches/core-06234df-atlas.patch`, `patches/playerbots-b6696bd-atlas.patch` et
[`hermes-bcacff9-atlas.patch`](../../mod-atlas-shop/patches/hermes-bcacff9-atlas.patch).
Ils complètent les copies des sept modules conservés, les configurations
privées et les données de jeu. Ces éléments privés et les exécutables restent
hors Git. Un clone de ce dépôt seul ne reconstitue donc pas le serveur.

Le core conserve la synchronisation sociale Atlas, le réglage de récupération
du corps en PvP et les trois hooks natifs de boutique. Playerbots conserve la
réservation et la connexion prioritaire des bots de guildes dirigées par des
joueurs. La compilation complète utilise aussi
[`mod-atlas-shop.cmake`](../../mod-atlas-shop/mod-atlas-shop.cmake) pour rendre
le header des hooks accessible au core.

Hermes conserve l'authentification Atlas, le SSO du launcher, les messages
privés, les adaptations 3.4.3, les services natifs de boutique et la fermeture
de la connexion legacy appartenant à chaque socket. Les conflits avec les
nouveaux messages de journal et l'envoi différé des permissions de guilde ont
été résolus en gardant les deux comportements. Le test de réponse à une
proposition de donjon a été corrigé pour inclure l'octet propre au client 3.4.3.

## Vérifications reproductibles

Pour Hermes, compiler `HermesProxy.Tests` en Release avec .NET 10, puis exécuter
son runner avec `HERMES_TEST_MODERN_BUILD=3.4.3` et `--culture en-US`. Les quatre
cas ignorés portent sur deux types de paquets sans opcode dans ce client.
`HERMES_NATIVE_PACKET_OUTPUT` et `HERMES_IDENTITY_PACKET_OUTPUT` permettent
d'exporter les paquets synthétiques des tests vers des fichiers privés.
Les vérificateurs `mod-atlas-shop/tests/verify_native_protocol.py` et
`verify_native_identity.py` contrôlent ces paquets à l'aide des sections
privées du client. Ils n'ouvrent ni le jeu ni son interface.

Le banc `mod-atlas-shop/tests/run_realm_fixture.py` accepte un candidat
`atlas-all-update-*` uniquement si son manifeste lie le binaire à un répertoire
de configuration pointant sur le banc isolé. Il utilise alors les distributions
de configuration du nouveau core. `--all-modules` exige `--hold` et ce candidat ;
il active les neuf modules avec un petit pool de comptes synthétiques.
`--reserve-guild-bots` réduit le plafond à deux bots pour vérifier que les deux
membres de la guilde synthétique occupent ces places réservées après le
redémarrage du banc. Les comptes, monnaies, guildes et modifications de bases de ces tests
appartiennent exclusivement au banc.

Les recettes de contrôle se trouvent dans `tests/` :

- `run_cpp_suites.py` exécute CTest puis la suite complète Dungeon Clear,
  sans filtrer les scénarios de navigation. Il exige un réseau privé,
  utilise les cartes existantes en lecture seule et conserve les rapports XML,
  y compris les cas ignorés ou en échec.
- `stage_completed_build.py` exige la réussite de la compilation complète,
  installe les exécutables dans le candidat, conserve les configurations de
  distribution et crée le lien de configuration vers le banc.
- `seed_bot_reference_data.py` alimente les 16 tables de référence initialement
  vides du banc depuis les fichiers publics Playerbots figés, avec sauvegarde,
  liste de tables autorisées et contrôle du conteneur MySQL sans réseau.
- `start_candidate_fixture.py` lance une phase bornée sous systemd, avec réseau
  privé et limites de ressources. Il vérifie l'identité du conteneur MySQL
  jetable et l'arrête à la fin de la phase. Les phases `modules` et `guild`
  gardent le banc disponible pendant 45 minutes au maximum ; son fichier
  `stop-fixture` permet de terminer plus tôt.
- `run_go_suites.py` exige le réseau du banc déjà démarré et vérifie le World
  réellement chargé avant d'exécuter les suites compilées. Les accès SQL sont
  limités par sa configuration aux bases `shop_test_*` du réseau privé.
- `atlas_custom_e2e_test.go` s'installe dans le répertoire ignoré
  `core/e2e/local/atlas_custom` du candidat et se compile avec le tag `e2e`.
  Il prépare la guilde synthétique par commandes GM et démarre un parcours
  Dungeon Clear. Le compte qui lance ce parcours reste connecté jusqu'au
  résultat final ; le test contrôle le groupe de cinq bots et tous les boss.
- `test_custom_social_realm.py` vérifie les amis et la collecte Armory puis
  fait traverser au chat la vraie API, le module World et Hermes dans les deux
  sens, avec deux comptes synthétiques et des messages `#Launcher`.
- `verify_guild_reservation.py` contrôle les appartenances à la guilde,
  les réservations persistantes et les deux connexions prioritaires après le
  redémarrage du banc.
- `verify_preservation.py` compare à nouveau les fichiers des sept modules
  conservés, les configurations, les sauvegardes privées et les services actifs
  à leurs empreintes de départ, sans afficher le contenu des secrets.

Ces recettes restent liées aux chemins et versions de cette préparation. Elles
ne constituent pas un installateur autonome ; les binaires de test Go, le
conteneur, les données privées et le paquet API du banc doivent déjà exister.

## Bascule distincte

Ces fichiers ne redémarrent aucun service de production. La bascule groupée
nécessite une autorisation d'interruption après revue des résultats. Elle doit
recontrôler les empreintes des configurations actives, sauvegarder les bases
au moment de l'arrêt, appliquer les migrations World retenues, puis démarrer
le couple World/Hermes préparé. Le retour aux anciens exécutables doit tenir
compte des migrations SQL ; aucune restauration automatique de base ne doit
écraser des écritures de joueurs.
