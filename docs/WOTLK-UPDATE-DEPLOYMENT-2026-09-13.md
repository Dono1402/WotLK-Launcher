# Mise en service de la mise à jour Atlas — 13 septembre 2026

La mise à jour préparée le 12 septembre est active en production après
l'autorisation du 13 septembre. World et Hermes utilisent les nouveaux
binaires ; Auth et l'API ont conservé leurs processus. Les 1 500 bots sont
connectés, dont les trois bots de guildes réelles réservés en priorité.

L'arrêt de World était terminé à 23 h 33 min 18 s, heure de Paris. Le nouveau
World a été lancé à 23 h 34 min 28 s ; Hermes a été relancé à 23 h 35 min 46 s,
après vérification de World et des deux indicateurs de santé de la boutique.
Le dernier contrôle de cette intervention date de 23 h 39 min 18 s.

## Versions actives

| Composant | Source avec personnalisations Atlas |
| --- | --- |
| Core Playerbot | `e9d127f1a8bf285ce2b2e3df7e923225eb334d72` |
| Playerbots | `fe355fa88e3295729d4886dbb4078c4dd7547aa8` |
| Dungeon Clear | `98929d6d261e615aa410f8e2d08ca83710fd2cad` |
| Hermes | `d4fe01a390fbda937b2b6643fbe29446ca2d5ffe` |

Les versions amont et correctifs reproductibles sont dans le
[dossier de préparation](../scripts/wotlk-update-20260912/README.md).
Les empreintes des exécutables réellement chargés correspondent aux binaires
testés. World porte le PID `3100212` et Hermes le PID `3100655` ; aucun
redémarrage automatique n'a été observé au contrôle final.

## Sauvegardes et données

Les quatre bases ont été sauvegardées après l'arrêt de World et Hermes :
`arthas_world`, `arthas_chars`, `arthas_playerbots` et `arthas_auth`.
Chaque archive a été entièrement décompressée pour contrôler son intégrité,
son marqueur de fin et son empreinte SHA-256. Les sauvegardes privées restent
sur le serveur dans le dossier de cette intervention.

World, Characters et Playerbots utilisent des verrous de tables pendant
leur sauvegarde, car certaines tables ne sont pas transactionnelles. Auth
utilise un instantané transactionnel et reste en service. L'ensemble n'est
pas un instantané atomique commun aux quatre bases ; aucune restauration
complète n'a été répétée dans le cadre de cette bascule.

Les 28 migrations figées ont ensuite été appliquées et enregistrées dans
`arthas_world.updates`. Les empreintes des tables de textes et d'objets
personnalisés contrôlées, ainsi que celles des guildes et appartenances,
étaient identiques avant et après cette application. Aucun de ces scripts
ne ciblait Auth, Characters ou Playerbots.

## Personnalisations et fonctionnement contrôlés

- Les neuf fichiers de configuration World/modules sont identiques aux
  fichiers actifs avant la bascule. Les réglages des 1 500 bots et de la
  priorité des bots de guildes sont conservés ; les options de suppression
  de guildes et d'équipes d'arène restent à zéro.
- Les sept modules conservés et les correctifs du core, de Playerbots et de
  Hermes proviennent des sources vérifiées pendant la préparation.
- Les trois bots de guildes réelles ont leurs événements de réservation
  persistants et sont connectés ; les 1 500 personnages en ligne contrôlés
  appartiennent au pool de bots aléatoires.
- Le journal confirme l'état prêt de World, l'initialisation du chat Atlas
  et l'enregistrement de Dungeon Clear. Les options des neuf modules,
  d'Armory Live et des fonctions de boutique restent activées.
- Les deux indicateurs de santé natifs de la boutique et de la conversion
  d'or sont frais. L'API répond normalement ; les PID Auth `323656` et API
  `2830654` sont inchangés.
- Hermes charge le client `3.4.3.54261`, ouvre ses ports de connexion et
  expose le service de tickets du launcher uniquement sur `127.0.0.1:8099`.
  Ses paramètres Atlas, variables d'environnement et certificat sont conservés.

## Limites et incident observé

Un conflit SQL de clé dupliquée a été observé lors de l'enregistrement d'un
sort de familier appartenant à un bot. Une transaction de huit requêtes a
été abandonnée. Le sort existe déjà en base avec un état d'activation différent
de celui demandé par l'écriture en échec. Une seule occurrence était présente
au contrôle final ; aucun correctif manuel de ces données n'a été appliqué.
L'incidence plus large sur cette sauvegarde de familier n'a pas été validée.
Le démarrage, les 1 500 bots et les indicateurs de santé restaient opérationnels.

Les cinq échecs de sondes de navigation Dungeon Clear décrits dans le
[compte rendu de préparation](WOTLK-UPDATE-PREPARATION-2026-09-12.md)
restent ouverts. La réussite du démarrage en production résout l'incertitude
du banc limité à 5 Gio pour ce démarrage ; elle ne valide pas tous les donjons.
Aucun parcours complet de donjon, achat ou message de test n'a été créé en
production. Aucun client graphique ni launcher sur le PC n'a été ouvert.

Les anciennes versions, leurs configurations et leurs drop-ins sont
conservés. Aucune restauration SQL automatique n'a été effectuée ; un retour
binaire éventuel doit tenir compte des migrations désormais appliquées et
préserver les écritures intervenues après la reprise.

Les [preuves de déploiement](update-preparation/2026-09-13/deployment-result.json)
contiennent les empreintes, sauvegardes, résultats SQL et contrôles effectués.
