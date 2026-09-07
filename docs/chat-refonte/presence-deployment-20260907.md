# Activation de la présence en production — 7 septembre 2026

L'API du launcher fonctionne en schéma **8**. L'activation, explicitement autorisée à **11:10:04 UTC**, a entraîné **17,877 secondes d'interruption mesurée de l'API**. Le contrôle final à **11:32:29 UTC** est réussi. Les PID et horodatages de démarrage du serveur de jeu et d'Hermes sont identiques à ceux du préflight ; aucun retour arrière n'a été exécuté.

Les modifications du client et leurs validations locales sont décrites dans [Messages et présence — finitions](polish-20260907.md). Tous les horaires ci-dessous sont en UTC ; Paris est à UTC+2 ce jour-là.

## Déroulement vérifié

| Étape | Heure et résultat |
|---|---|
| Préparation et sauvegarde vérifiée | 11:22:14 ; ancien binaire, configuration et base d'authentification sauvegardés. |
| Validation Linux isolée finale | 11:29:35 ; schéma 8 et 55 contrôles réussis. |
| Arrêt demandé de l'API | 11:30:38 ; sauvegarde complémentaire des préférences et de l'historique des migrations avant bascule. |
| Migration `0008_global_presence.sql` | 11:30:55.852191 ; durée enregistrée de 92 ms. |
| Nouvelle API en santé | 11:30:56 ; interruption mesurée de 17,877 s. |
| Validation en production terminée | 11:31:18 ; 55 contrôles réussis et fixtures nettoyées. |
| Vérification finale | 11:32:29 ; API stable, contrôles publics et services de jeu conformes. |

## Résultats et périmètre

Les deux passages de 55 contrôles vérifient les quatre statuts, leur visibilité depuis un autre compte synthétique, la synchronisation DND dans les deux sens, le maintien en ligne à 1 199 secondes, l'absence automatique à 1 200 secondes et le retour en ligne après reprise d'activité. Ils couvrent aussi le masquage des indicateurs d'activité et de saisie en mode hors ligne, ainsi que les régressions chat v1/v2, groupes, événements, accusés de lecture et pièces jointes.

Chaque passage utilise trois comptes synthétiques sans personnages. Le nettoyage confirme **zéro compte de fixture et zéro ligne de présence associée restants**, avec deux conversations et un upload supprimés. La base et le compte MySQL temporaires du canary, son environnement privé et ses médias ont été supprimés ; le canary est arrêté.

La vérification finale confirme :

- Schéma 8, six colonnes, une clé étrangère et deux contraintes CHECK actives ; historique 1 à 7 et correspondances entre messages v1/v2 conservés.
- API active avec le PID `1842850`, stable pendant les contrôles ; santé locale et publique HTTP 200.
- Routes publiques chat v1, chat v2 et `PUT /api/v1/me/presence` renvoyant HTTP 401 sans authentification, avec `Cache-Control: no-store`.
- Identités du serveur de jeu et d'Hermes inchangées ; ports 1119, 8084, 8086 et 4000 toujours en écoute.
- Aucune modification des configurations préexistantes ; médias privés en mode 0700.
- Zéro erreur et zéro avertissement dans les 15 entrées de journal examinées entre la bascule et 11:32:29 UTC.

## Version déployée et sauvegarde

- Référence sources de cette livraison : `22ec1517ae90ddb432938e63f5665ca21ba9ad3e`.
- Release active : `/opt/wotlk-launcher-api-releases/presence-20260907T111217Z`.
- Sauvegarde : `/opt/wotlk-launcher-api-backups/presence-20260907T111217Z` ; copies et archives relues par empreinte, dont une sauvegarde complémentaire après arrêt de l'API.
- SHA256 du binaire actif : `b61543983fd5150f02a7b96718bc53e2f286bcc9bcde84791d954086930c05da`.
- SHA256 de l'archive Linux : `3bb151987c8ae8b155a4725f54f92af08b01c90c448862284a6110a1dd0ff31c`.

Le manifeste du candidat reste la photographie de sa préparation, antérieure au déploiement. Le client local déjà installé, d'empreinte `bb4119a837086612fbd6b25d1da4de8e974a1592d4da43218ca7566b7b701756`, est inchangé pendant cette activation ; le launcher conserve sa taille fixe.

## Incident isolé et limites

Le premier canary s'est arrêté sur la comparaison des métadonnées CHECK : MySQL exposait les littéraux avec le préfixe `_utf8mb4` et des apostrophes échappées. Seul le helper de vérification a été corrigé pour reconnaître cette représentation exacte, avec 22 tests stricts. Les fixtures de cet essai et du diagnostic ont été nettoyées avant le canary final réussi. Le produit et la production n'ont pas été modifiés à cette étape.

Les comptes de smoke n'avaient aucun personnage : ces passages ne démontrent pas un scénario d'activité avec un personnage réellement connecté en jeu. Les durées d'inactivité ont été injectées uniquement dans les fixtures. La validation locale du dépôt de fichiers utilisait les événements WPF ; aucun geste physique depuis l'Explorateur sur le bureau utilisateur n'a été effectué.

## Preuves conservées

Les 13 fichiers de preuve sont conservés localement, hors du dépôt, sous `artifacts/atlas-chat-polish-20260907/deployment/remote-evidence/`. Les références principales sont `deployment-result.json`, `post-verification.json`, `canary-passed.json`, `canary-smoke.stdout`, `production-smoke.stdout`, `events.jsonl`, `before.json` et `presence-before-cutover.json`. Les autres relevés documentent la préparation, le nettoyage et la correction du helper. La consolidation `artifacts/atlas-chat-polish-20260907/deployment/production-verification.json` répertorie les exports et leurs empreintes.
