# Activation d’Atlas Messages

L’activation a été autorisée par l’utilisateur le 7 septembre 2026 à 08:52:01 UTC (10 h 52 min 01 s à Paris), puis terminée à 09:17:05 UTC (11 h 17 min 05 s à Paris). Ce document décrit l’ordre de bascule appliqué et les contraintes de retour arrière. Les résultats vérifiés figurent dans le [rapport de déploiement](deployment-report.md). Le client local avait été livré séparément à 10 h 46 ; aucune nouvelle version publique du launcher n’a été publiée.

## Changement serveur appliqué

- API `/api/v2/chat`, tout en conservant `/api/v1/chat` et le pont de messages privés avec le jeu.
- Migration additive `0007_chat_workspace.sql` : huit tables nouvelles et copie de l’historique privé existant avec conservation des identifiants. La migration 0006 reste inchangée.
- Plafond explicite `WOTLK_LAUNCHER_MAX_SCHEMA_VERSION=7`.
- Répertoire privé persistant, détenu par le compte du service, désigné par `WOTLK_CHAT_MEDIA_ROOT`. Les fichiers ne sont pas exposés comme un répertoire web public.
- Candidate Linux autonome : archive contenant uniquement `WotLK.Launcher.Server` et `libSkiaSharp.so`. Les paramètres et secrets existants restent externes à cette archive.

Seul `wotlk-launcher-api.service` a été arrêté puis démarré sur la nouvelle version. Son indisponibilité mesurée est de 2,125 secondes. Les processus du serveur de jeu et de Hermes sont restés identiques avant et après l’opération.

## Ordre de la bascule exécutée

1. Relire les identités des services, la version active, les empreintes des binaires/configurations et l’historique réel des migrations. Chaque étape reste conditionnée à l’absence de changement inattendu.
2. Sauvegarder la base d’authentification, les configurations et les binaires actifs sur le serveur, puis vérifier les empreintes. Conserver les identités du serveur de jeu et de Hermes pour les comparer ensuite ; les secrets restent sur le serveur.
3. Extraire la candidate dans un nouveau répertoire de version et vérifier les empreintes de ses deux fichiers. Préserver les paramètres de production externes au paquet.
4. **Valider d’abord la candidate Linux sur une base MySQL temporaire isolée, pendant que l’ancienne API reste active.** Le compte temporaire n’a de droits que sur cette base, contenant uniquement les structures `account` et `characters` copiées depuis les bases réelles. Authentification, personnages et monde pointent vers cette fixture ; playerbots est désactivé et les médias sont isolés. Démarrer le canary sur `127.0.0.1:14323` avec le plafond 7, vérifier sa santé, son schéma et les 32 contrôles API, puis l’arrêter et supprimer ses fixtures. Le migrateur et `ChatGameInboxWorker` de la candidate ne doivent pas accéder à `arthas_auth` pendant cette étape.
5. Après réussite et nettoyage du canary, arrêter uniquement l’ancienne API et confirmer la libération du port 4323. Relever l’historique privé courant et effectuer une sauvegarde finale des données concernées. Créer le stockage privé des médias, ajouter les paramètres de plafond 7 et de stockage, puis basculer le lien de version et démarrer la nouvelle API. La migration 0007 s’exécute au démarrage, après l’arrêt de l’ancien processus ; attendre le retour de la santé.
6. Vérifier le schéma 7, son empreinte, les huit nouvelles tables et la reprise de l’historique. Exécuter les 32 contrôles authentifiés sur l’API de production avec des comptes synthétiques : messages v1/v2 et leur projection, groupes et invitations, événements, lecture privée, transferts et contrôle d’accès. Nettoyer les comptes, fils et fichiers de test, puis revalider les données antérieures.
7. Vérifier la santé publique, les refus d’accès non authentifiés, les journaux et les quatre ports de jeu. Comparer les identités du serveur de jeu et de Hermes et les empreintes des fichiers de configuration d’origine, puis consigner le résultat.

## Retour arrière

Aucun retour arrière n’a été nécessaire pour ce déploiement. La sauvegarde vérifiée est conservée sous `/opt/wotlk-launcher-api-backups/chat-workspace-20260907T085830Z`.

L’ancien binaire refuse un historique contenant une migration qu’il ne connaît pas. **Remettre seulement l’ancien exécutable après la migration 7 ne suffit donc pas.** La procédure de secours implémentée arrête les écrivains API et nettoie les éventuelles fixtures, puis archive les tables v2 et l’historique avec vérification de leur contenu et de leur empreinte. Les fichiers médias sont conservés. Elle retire ensuite uniquement les huit tables v2 dans l’ordre des dépendances, puis la ligne de migration 7 exactement identifiée. Les tables v1 et les messages arrivés depuis la bascule sont conservés ; toute la base `arthas_auth` n’est jamais restaurée automatiquement.

La procédure retire les deux fichiers de configuration ajoutés pour le chat et rétablit le lien vers l’ancien binaire. Après son démarrage, elle vérifie l’empreinte du processus réellement exécuté, le plafond actif à 6, la santé de l’API et les identités inchangées des services de jeu. Les fonctions propres à v2 deviennent alors indisponibles, mais leurs données restent archivées et leurs médias conservés. Ce retour arrière est conditionné aux vérifications de sauvegarde et de propriété ; il n’a pas été exécuté pendant ce déploiement.

L’API v1 et son miroir de texte restent disponibles dans le nouveau serveur. Les messages déjà affichés dans le jeu ne peuvent pas être retirés de son écran par une édition ou une suppression dans le launcher.

## Portée des validations et exploitation

Le démarrage Linux, le schéma et les contrôles API authentifiés ont été validés sur le canary isolé, puis sur l’API de production avec des comptes synthétiques dédiés sans personnage. Aucun essai n’a été effectué dans le jeu ou avec les comptes réels des utilisateurs. Les tests locaux du runtime couvrent 500 000 000 octets synthétiques ; aucun transfert de cette taille n’a été réalisé sur le réseau de production.

Les fichiers terminés sont conservés côté serveur. Aucun nettoyage automatique des pièces jointes terminées n’est activé dans cette refonte ; une suppression future devra vérifier leurs références aux messages et aux groupes. La sauvegarde et la capacité du répertoire des médias font partie de l’exploitation du service.
