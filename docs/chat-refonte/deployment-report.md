# Atlas Messages — déploiement du 7 septembre 2026

**L’API v2 et la migration 7 sont actives en production.** Le déploiement autorisé s’est terminé à **11 h 17 min 05 s, heure de Paris** (09:17:05 UTC). Seule l’API du launcher a été redémarrée ; l’indisponibilité mesurée entre la demande d’arrêt et le retour de sa santé est de **2,125 secondes**. Le serveur de jeu et Hermes ont conservé leurs processus. Aucun retour arrière n’a été nécessaire.

Le client local avait été livré à 10 h 46, avec sauvegarde et vérification d’empreinte. Son exécutable et sa configuration ont été revérifiés après le déploiement : ils sont inchangés, et les 75 fichiers produit correspondent au manifeste initial. Aucune publication GitHub ou nouvelle version publique du launcher n’a été effectuée.

## Version et données actives

| Élément | État vérifié |
|---|---|
| Autorisation utilisateur | 7 septembre, 08:52:01 UTC — 10 h 52 min 01 s à Paris |
| Bascule de l’API | Arrêt demandé à 09:16:38 UTC ; santé revenue à 09:16:40 UTC |
| Dernier contrôle après déploiement | 09:20:20 UTC — 11 h 20 min 20 s à Paris |
| Version serveur | `/opt/wotlk-launcher-api-releases/chat-workspace-20260907T085830Z` |
| Processus API | PID `1816840` |
| SHA256 du binaire exécuté | `e93409a6d854da7e3bc642e922f04aaff31f3d916a0ca410f3a1317a1fd74557` |
| Migration active | Version 7 ; 8 nouvelles tables, 11 clés étrangères, 6 contraintes CHECK |
| SHA256 de la migration 0007 | `91b95945241f830881bc4e03639e124a972fcc60d1e7d13d9949ae3559b85d97` |
| Historique antérieur | 1 conversation et 1 message conservés et repris dans v2 ; aucun mapping invalide |
| Stockage privé des pièces jointes | `/srv/wotlk/atlas-media/chat`, mode `0700`, propriétaire `wotlklauncher` — UID `984`, GID `979` |

L’activation ajoute `/etc/wotlk/launcher-api-chat.env` et le complément systemd `/etc/systemd/system/wotlk-launcher-api.service.d/zzz-atlas-chat.conf`. Le processus actif utilise `WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=7` et le répertoire média indiqué ci-dessus. Les empreintes des fichiers de configuration qui existaient avant cette activation sont restées inchangées.

## Contrôles obtenus

La candidate Linux a d’abord été démarrée sur le port local 14323 avec une base, un compte MySQL et un stockage média temporaires. Les références aux bases d’authentification, de personnages et de monde pointaient vers cette fixture, et playerbots était désactivé. L’ancienne API de production est restée active pendant cet essai. Le canary a été arrêté et nettoyé avant l’arrêt de l’ancienne API et le démarrage de la migration en production.

| Vérification | Canary Linux isolé | API de production |
|---|---|---|
| Contrôles authentifiés de `run-chat-smoke.py` | 32/32 réussis | 32/32 réussis |
| Schéma 7 et contraintes | Vérifiés | Vérifiés avant et après nettoyage des fixtures |
| Comptes synthétiques, sans personnage | 3 comptes dédiés nettoyés | 3 comptes dédiés nettoyés |
| Données de test | 2 fils et 1 fichier nettoyés | 2 fils et 1 fichier nettoyés |

Les contrôles couvrent les messages et l’idempotence v1/v2, la projection Markdown vers v1, les événements et leur reprise, la confidentialité Lu/DND, les invitations et l’historique des groupes, la révocation d’accès, la reprise d’un transfert et son empreinte, les téléchargements complets ou par plage et les refus d’accès extérieurs. La liste exacte figure dans les rapports de smoke et `artifacts/atlas-chat-refonte/deployment/run-chat-smoke.py`.

Le dernier contrôle public a obtenu HTTP **200** sur `/wotlk/health` et HTTP **401** avec `Cache-Control: no-store` sur les routes privées v1 et v2 sans authentification. Un contrôle complémentaire depuis le poste local a obtenu HTTP 200 en 0,134 seconde. Les journaux API observés entre la bascule et 09:20:20 UTC contiennent **0 erreur et 0 avertissement**. Les quatre ports de jeu contrôlés (`1119`, `8084`, `8086`, `4000`) répondent ; le port du canary est libéré.

| Service de jeu | PID avant et après | Horodatage monotone de démarrage inchangé |
|---|---|---|
| Worldserver | `1461668` | `9017890131594` |
| Hermes | `1732189` | `9134521260266` |

Le premier essai canary a rencontré l’erreur MySQL 1267 dans une requête du script de vérification. L’ajout explicite de la collation binaire dans ce script a corrigé cette comparaison ; aucun code produit n’a été modifié. Le second essai a réussi. Les **deux bases canary temporaires**, leurs utilisateurs MySQL et leurs médias ont été supprimés.

## Sauvegarde et preuves

La sauvegarde serveur est conservée sous `/opt/wotlk-launcher-api-backups/chat-workspace-20260907T085830Z`. Elle couvre les données d’authentification, les configurations et les binaires antérieurs, avec empreintes vérifiées ; une sauvegarde finale des données chat a aussi été réalisée après l’arrêt de l’ancienne API. Aucun secret de connexion n’est inclus dans ce rapport.

Les **14 preuves non secrètes** sont archivées dans `artifacts/atlas-chat-refonte/deployment/remote-evidence/`, notamment `deployment-result.json`, `canary-passed.json`, `post-verification.json` et `events.jsonl`. Le relevé consolidé est `artifacts/atlas-chat-refonte/deployment/production-verification.json` ; le périmètre autorisé et les empreintes attendues figurent dans `artifacts/atlas-chat-refonte/deployment/deployment.json`. La procédure et la contrainte de retour arrière sont documentées dans [deployment-plan.md](deployment-plan.md).

Le client local porte le SHA256 `bb48e6052eac2e27401a0002acb774b378d60b7f6f8a39a493da02c834e7b9ca`. `artifacts/atlas-chat-refonte/local-delivery.json` décrit sa livraison antérieure à l’activation serveur ; le statut de production actuel figure dans le relevé consolidé ci-dessus.

## Limites de la vérification

Les contrôles de production utilisent des comptes synthétiques dédiés sans personnage. Ils valident les API et la projection v1/v2 ; ils ne constituent pas un essai de messagerie dans le jeu avec les comptes réels des utilisateurs. Aucun launcher utilisateur ni jeu n’a été ouvert ou fermé, et aucune session Computer Use n’a été utilisée pour le déploiement.

Aucun transfert de 500 Mo n’a été effectué sur le réseau de production. Les tests locaux du runtime couvrant **500 000 000 octets synthétiques**, ainsi que les vérifications d’interface et de lecture vidéo, restent documentés dans [validation.md](validation.md).
