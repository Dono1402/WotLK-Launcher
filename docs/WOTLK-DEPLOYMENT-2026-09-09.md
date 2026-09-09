# Déploiement WotLK — 9 septembre 2026

## Périmètre autorisé

Après la [préparation et ses tests](WOTLK-UPDATE-PREPARATION-2026-09-09.md),
l'utilisateur a autorisé l'installation d'HermesProxy, de Dungeon Clear et
de WeakAuras 5.12.9, avec redémarrage d'Hermes puis du serveur de jeu.
La suite exhaustive de tests natifs Dungeon Clear reste suspendue ; aucun
test de connexion avec un compte réel, lancement du jeu ou ouverture du
launcher n'est effectué par cette procédure.

Auctionator, WeakAuras 5.13.1, les autres addons et le catalogue distribué
par le launcher ne font pas partie de cette installation. Les modifications
Atlas du core, Playerbots, Armory, Friends, Chat, AH Bot, Transmog et
Account Achievements sont conservées.

## État d'exécution

Les trois installations prévues sont effectuées. Le contrôle final serveur
du 9 septembre à **12:32:30 UTC** confirme Hermes et World actifs, sans
redémarrage automatique, et les **1 500 bots reconnectés**. Le journal
`Errors.log` du nouveau démarrage est vide. Aucun retour arrière n'a été
nécessaire.

| Composant | Version réellement installée | Validation effectuée |
| --- | --- | --- |
| HermesProxy | Commit Atlas `f859d0c`, amont `4247d957` | Paquet, tests natifs et readiness réseau |
| Dungeon Clear | `3dd90f7c` dans le nouveau World Atlas | Binaire, démarrage, modules, port et 1 500 bots |
| WeakAuras | 5.12.9, cinq racines locales | 675/675 fichiers conformes, sauvegardes et préservation |

Cette disponibilité contrôlée ne remplace pas une connexion de joueur,
l'exercice des auras et un parcours de donjon ; ces validations restent
manuelles et ne sont pas revendiquées dans ce compte rendu.

## HermesProxy : nouvelle version active

Bascule exécutée de 12:22:44 à 12:22:50 UTC, sans retour arrière nécessaire.
Le processus `2286032`, démarré à 12:22:48 UTC, charge la release
`/opt/hermesproxy-wotlk/releases/hermes-f859d0c-20260909`, issue du commit
`f859d0c59696b62483a98133b1e064c15dcb5604` : amont `4247d957` avec les
adaptations Atlas préservées. Le monde est resté sur son PID `1910234`
pendant cette première bascule.

Les 233 fichiers correspondent au manifeste du paquet Linux. Les trois
liens AccountData, Logs et PacketsLog pointent toujours vers les mêmes
données partagées. Configuration, environnement, certificat, anciens
drop-ins et service de synchronisation des quêtes sont conservés. Une
sauvegarde privée des fichiers statiques a été vérifiée, puis AccountData
a été copié et comparé après l'arrêt d'Hermes, avant son redémarrage.

La seule modification d'unité est le nouveau drop-in
`90-hermes-update-20260909.conf`, qui remplace ExecStart et WorkingDirectory.
La synchronisation des quêtes existante reste exécutée comme dépendance
au démarrage ; elle peut actualiser les données partagées AccountData,
mais ne modifie pas les 233 fichiers du paquet.

Readiness réussie à 12:26:02 UTC :

- cinq ports attendus détenus par le nouveau PID, service actif et zéro
  redémarrage automatique ;
- chaîne de certificat et nom `animeclub.fr` vérifiés en TLS pour REST et BNet ;
- formulaire REST HTTP 200 et poignée de main BNet non authentifiée ;
- bridge refusant une requête sans secret avec HTTP 401 ;
- bannières des connexions realm et instance conformes.

Le test loopback reçoit `https://localhost:8081/bnetserver/login/srp/`,
conformément à `LoginServiceManager` qui choisit localhost pour une adresse
source loopback. Une première assertion attendait une autre forme ; le
contrôle corrigé a vérifié ce comportement dans le code. Il ne s'agissait
pas d'un échec de connexion d'un joueur. Aucun compte, ticket de launcher,
secret de session ou mot de passe n'a été utilisé ou modifié pour ces tests.

Sauvegarde privée serveur :
`/opt/hermesproxy-wotlk/rollback-hermes-f859d0c-20260909`.
Le retour binaire retire uniquement le drop-in 90 après contrôle de son
contenu, puis relance l'ancienne release ; il ne restaure pas automatiquement
AccountData depuis sa sauvegarde.

Le [résultat de déploiement assaini](update-preparation/2026-09-09/hermes-deployment-result.json)
consigne les identités et contrôles. Les recettes exactes conservées sont
[la préparation de release](../scripts/wotlk-update-20260909/hermes/prepare-production-release.sh),
[la bascule et son retour binaire](../scripts/wotlk-update-20260909/hermes/switch-production-hermes.sh)
et [le drop-in 90](../scripts/wotlk-update-20260909/hermes/90-hermes-update-20260909.conf).

## Dungeon Clear : nouvelle version active

Le nouveau World reprend la source Dungeon Clear
`3dd90f7c1122abc291edcbd9d3dc68f6c8fed0bc` et conserve les autres modules
Atlas. Son binaire de 2 472 074 992 octets a pour SHA256
`428e92b4498312593ea44f6c0d2870519a43af65acf68be3567890d178cd87d6`.
La préparation runtime a sauvegardé 14 fichiers de configuration et six
fichiers d'unité/drop-ins ; les configurations actives n'ont pas été modifiées.
Le [résultat de préparation runtime](update-preparation/2026-09-09/world-deployment-prepared-result.json)
documente aussi les droits d'accès et l'absence de bascule à cette étape.

La phase d'arrêt/sauvegarde a commencé à 12:27:32 UTC. Le World s'est arrêté
proprement à 12:27:57 UTC (code 0, PID nul, service inactif et cgroup disparu).
Hermes est resté actif avec le même PID `2286032`.

La sauvegarde des bases `arthas_chars` et `arthas_playerbots` s'est terminée
à 12:28:07 UTC. Le dump a verrouillé uniquement les tables des deux bases,
sans verrou global MySQL, World déjà arrêté. La décompression intégrale,
le CRC gzip, le footer de fin et le SHA256 ont été vérifiés avant d'autoriser
la phase de démarrage séparée.

| Propriété | Résultat |
| --- | --- |
| Taille gzip | 106 295 150 octets |
| Taille SQL décompressée | 481 495 812 octets |
| SHA256 gzip | `139881af316f21bef3c6eee3b2d29e7e3f5eefb4f4ca71a9a4b4b74ec0d6d04f` |
| Permissions | `0600 root:root`, répertoire privé |
| Espace libre après sauvegarde | 12 361 568 256 octets, au-dessus de la réserve de 8 Gio |
| Runtime et déploiement cumulés | 2 579 307 676 octets, sous le plafond de 3 Gio |

Sauvegarde privée serveur :
`/opt/arthas-next/candidates/modules-update-20260909T1035Z/deployment/backup/characters-playerbots.sql.gz`.
Son contenu n'a pas été rapatrié. Seul le
[résultat assaini](update-preparation/2026-09-09/world-database-backup-result.json)
est versionné.

La recette [deploy_world_dc.py](../scripts/wotlk-update-20260909/world/deploy_world_dc.py)
sépare explicitement préparation, arrêt/sauvegarde, démarrage et retour
binaire ; elle doit être lancée avec Python `-I -B` sans optimisation.
Le [plan préparatoire](../scripts/wotlk-update-20260909/world/deployment-plan.md)
reste une recette historique antérieure au GO, pas un état courant.

Le répertoire de travail et les configurations de modules restent dans le
candidat `modules-update-20260905T1016Z`. Les liens de données et de navigation
conservent également leurs cibles historiques. **Ces anciens candidats sont
des dépendances actives et ne doivent pas être supprimés.** La configuration
SQL garde les mises à jour automatiques désactivées ; aucune migration SQL
ne fait partie de cette bascule.

### Démarrage et readiness

Après revue séparée de la sauvegarde, la phase de démarrage a activé
uniquement le drop-in `70-atlas-dungeon-clear-20260909.conf`. Le nouveau
processus World `2287415` a démarré à **12:29:27 UTC**, sur
`/opt/arthas-next/candidates/modules-update-20260909T1035Z/server/bin/worldserver`,
avec la configuration principale copiée à l'identique. L'initialisation du
monde a duré 53 secondes ; le port de jeu 4000 est ensuite devenu disponible.
Le retour des bots est progressif, distinct de la disponibilité initiale du port.

Contrôles achevés à **12:32:30 UTC** :

- exécutable réellement chargé via `/proc/2287415/exe` et SHA256 conformes ;
- World actif, PID stable et `NRestarts=0`, Hermes toujours `2286032` et zéro
  redémarrage automatique ;
- nouveaux marqueurs World initialisé, Dungeon Clear enregistré et bridge
  Atlas Chat prêt ; port 4000 en écoute ;
- **1 500/1 500 bots en ligne**, aucun compte non-bot connecté au point de contrôle ;
- `Errors.log` vide, aucun signal fatal, SQL ou mmap détecté par les contrôles ciblés ;
- configuration principale copiée, 14 configurations d'origine et six fichiers
  d'unité préexistants toujours conformes à leurs empreintes ;
- environnement systemd intégralement préservé, deux variables de mises à jour
  SQL à zéro dans le processus réel, répertoire de travail inchangé ;
- 12 314 234 880 octets libres, soit environ 11,47 Gio.

Le [résultat runtime assaini](update-preparation/2026-09-09/world-deployment-result.json)
consigne ces contrôles. Aucun compte réel n'a été connecté pour la validation,
aucune restauration SQL ni exécution GoogleTest n'a été effectuée.

## WeakAuras local : 5.12.9 installé

Installation vérifiée le 9 septembre à 12:14:23 UTC pour le client
3.4.3.54261 / interface 30403. Les cinq répertoires WeakAuras,
WeakAurasArchive, WeakAurasModelPaths, WeakAurasOptions et WeakAurasTemplates
contiennent **675 fichiers sur 675 conformes au paquet figé**. Le changement
porte sur 59 fichiers, sans ajout ni suppression.

Deux copies vérifiées du code 5.12.8 sont conservées dans la sauvegarde
privée locale : une copie préalable et les répertoires originaux déplacés
de manière récupérable. Les six fichiers SavedVariables concernés ont été
sauvegardés et comparés par hash, sans modifier les fichiers actifs ni
exposer leur contenu. Les droits des fichiers privés sont limités à
l'utilisateur, SYSTEM et Administrators.

Les hashes de cinq fichiers protégés (correctif ElvUI Inspect, fichiers
SimpleDungeonMap et registre des addons) sont inchangés. Aucun autre addon,
fichier WTF actif ou catalogue n'a été modifié. Le jeu et le launcher étaient
fermés et n'ont pas été ouverts ou arrêtés par l'installateur.

- [Résultat assaini de l'installation](update-preparation/2026-09-09/weakauras-installation-result.json).
- [Installateur historique](../scripts/wotlk-update-20260909/addons/install-weakauras-5.12.9.ps1).
- Sauvegarde locale : `%LOCALAPPDATA%\Atlas\Backups\WeakAuras\5.12.8-to-5.12.9-20260909T121312935Z`.

Le registre du launcher est volontairement inchangé : son affichage peut
encore indiquer 5.12.8. Ouvrir ou actualiser le launcher ne réinstalle pas
l'addon automatiquement, mais une action explicite de réinstallation ou
réparation peut réappliquer la version du catalogue. Le catalogue versionné
reste en 5.12.8 ; son endpoint protégé répond HTTP 401 sans authentification
et n'a pas été interrogé avec un compte pendant le déploiement. La
publication d'un nouveau catalogue n'a pas été réalisée.

La lecture du code confirme ce comportement : chargement/inspection seuls
dans `LauncherAddonsCoordinator.cs:541-548`, `forceReinstall=true` pour
réinstaller/réparer à `:646-650`, puis téléchargement du paquet catalogue
dans `AddonInstallServices.cs:161-171`. La version 5.12.8 reste déclarée dans
`source/Build-AddonPackages.ps1:400-403`.

## Limites et vérifications en jeu

Le paquet Hermes a passé les tests natifs documentés : 1 150 réussis,
9 ignorés, puis 194/194 sur la sélection client 3.4.3, ainsi qu'un smoke
isolé du paquet publié. Ces résultats ne prouvent pas une connexion de
joueur à travers toute la chaîne de production.

Pour Dungeon Clear, 23 tests ciblés du noyau de décision Fosse de Saron
ont réussi pendant la préparation. Le binaire World a été assemblé après
reproduction bit-à-bit de la référence, mais le lien de l'exécutable de
tests GoogleTest Linux a échoué avant son exécution. Son correctif de lien
est seulement préparé localement : **aucun GoogleTest Linux exécuté, aucune
validation de parcours complet Fosse de Saron revendiquée**.

La vérification manuelle restante est une connexion au jeu, puis `/wa`,
l'affichage des auras existantes, les options et un changement de groupe
de talents sans erreur Lua. Un parcours de donjon reste nécessaire pour
valider les nouveaux comportements Dungeon Clear en situation réelle.

## Restauration et confidentialité

Les anciennes versions serveur restent conservées. Revenir à un ancien
binaire ne restaure pas automatiquement les données : Dungeon Clear peut
accorder aux bots des prérequis persistants (quêtes, objets, hauts faits).
Une restauration SQL annulerait aussi la progression acquise depuis la
sauvegarde et demande donc une autorisation distincte.

Les recettes sont spécifiques à ce déploiement historique. Leurs arguments
de confirmation ne constituent pas une autorisation de réexécution.
Les dumps, configurations, secrets, certificats, SavedVariables et journaux
bruts ne sont jamais inclus dans le dépôt. Les candidats serveur contenant
désormais une configuration privée ne doivent pas être archivés ou rapatriés
globalement ; seules les preuves assainies sont versionnées.
