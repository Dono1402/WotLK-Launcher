# Pipeline de maintenance du client - checkpoints 02D.1 et 02C.1

Ce document caractérise le pipeline v1.1.0 extrait de `MainWindow.xaml.cs`.
La fenêtre legacy reste propriétaire de son bail et de sa présentation. La V2
réutilise ce même pipeline pour l'installation, la mise à jour et la réparation
ciblée, sans déplacer le comportement legacy.

## Extension 02C.1 : vérification complète et réparation

- L'analyse automatique conserve `GameClientVerificationService`, le bail
  `Verify`, le cache rapide et ses limites historiques.
- Le clic V2 sur Vérifier acquiert un bail unique `GameRepair`, charge le
  manifeste une seule fois, puis `GameFullFileVerifier` valide chaque chemin,
  taille et SHA-256 sans consulter le cache pour éviter un hash.
- Seuls les résultats `Missing`, `SizeMismatch` et `HashMismatch` sont transmis
  à `GameClientMaintenanceService`. Le téléchargement, le temporaire, les
  validations et le remplacement restent ceux de `GameFileTransferService`.
- Un chemin `InvalidPath` ou un `ReadError` bloque la réparation avant tout
  téléchargement et avant toute écriture du cache.
- Le nettoyage réutilise strictement `GameFileCleanupService` : il supprime les
  fichiers absents du manifeste uniquement lorsqu'ils figuraient dans le cache
  installé, ainsi que les anciens dossiers explicitement gérés. Aucun fichier
  utilisateur non suivi n'est inventorié ou supprimé.
- Pour `GameRepair`, l'enregistrement plateforme réussit avant l'écriture du
  nouveau cache. Une annulation, une fermeture ou une erreur avant cette
  dernière étape conserve donc l'ancien cache.
- Le chemin legacy n'appelle jamais `VerifyAndRepairAsync`; son bouton et son
  analyse continuent d'utiliser le comportement historique.

## Répartition des anciennes méthodes

| Ancienne méthode | Destination | Entrées et sortie | Effets de bord et règles conservées |
| --- | --- | --- | --- |
| `ExecuteGameActionAsync` | Reste dans `MainWindow` | Utilise le `GameAction` legacy et le bail obtenu auprès de `LauncherOperationCoordinator`. | Authentification, contrôle d'accès au dossier, acquisition/libération du bail, erreurs, annulation et rafraîchissement final restent inchangés. |
| `InstallOrUpdateAsync` | `GameClientMaintenanceService.InstallOrUpdateAsync` avec adaptateur legacy dans `MainWindow` | `GameClientMaintenanceRequest`, bail `GameInstall` ou `GameUpdate`, progression brute ; retourne `GameClientMaintenanceResult`. | Ordonne uniquement manifeste, arrêt du jeu, comparaison, nettoyage, transfert, cache et enregistrement. Il n'acquiert et ne libère aucun bail. |
| `LoadManifestAsync` | `GameManifestClient.LoadAsync` | URL et token d'annulation ; retourne `LauncherManifest`. | Même `HttpClient` autorisé, même endpoint, `ResponseHeadersRead`, désérialisation insensible à la casse et erreurs HTTP inchangées. Aucun token brut n'est stocké. |
| `BuildFileUri` | `GameFileTransferService.BuildFileUri` | Manifeste et fichier ; retourne une URI. | URL absolue prioritaire ; sinon `baseUrl` et `url` relative ; sinon `files/` et échappement segment par segment. |
| `FindMissingOrChangedFiles` / `CompareManifestFiles` / `ComputeSha256Async` | `GameFileVerifier` | Racine, manifeste, progression et token ; retourne la comparaison. | Cache rapide, raccourci par version, partage de fichier du hash et défaut connu du cache strictement conservés. Aucune vérification exhaustive 02C.1. |
| `FindRemovedFiles` | `GameFileVerifier`, exposé par `GameFileCleanupService` | Racine et manifeste ; retourne les chemins gérés devenus absents. | Historique installé et anciens dossiers UnBot/MultiBot uniquement ; aucun inventaire agressif des fichiers utilisateur. |
| `DeleteRemovedClientFiles` | `GameFileCleanupService.DeleteRemovedFiles` | Racine, chemins relatifs et token ; retourne le nombre supprimé. | Politique de chemin obligatoire, attribut normal, 12 tentatives espacées de 250 ms, puis `IOException`. Les dossiers parents vides sont supprimés au mieux. |
| `DownloadFileAsync` | `GameFileTransferService.DownloadAsync` | OperationId, URI, cible, taille, SHA-256, progression et token ; retourne `Task`. | Une requête HTTP, fichier temporaire adjacent, validation taille puis hash, remplacement final, nettoyage du temporaire sur toute erreur ou annulation. |
| `MoveDownloadedFileWithRetryAsync` | Méthode privée de `GameFileTransferService` | Temporaire, cible et token. | 60 tentatives espacées de 1 s. Le fichier existant passe en attribut normal avant `File.Move(..., overwrite: true)`. |
| `GetSafeTargetPath` | `GamePathPolicy.GetSafeTargetPath` | Racine et chemin du manifeste ; retourne une cible canonique. | Refuse chemin vide, chemin absolu et toute sortie de la racine, y compris `../`. |
| `RegisterGameApplication` | `GameInstallPlatformAdapter` | Racine, version et langue ; retourne les chemins écrits ou `null`. | Vérifie l'écriture, ajuste `Config.wtf`, copie le désinstalleur, écrit `client-install.json`, puis inscrit l'application Windows via `GameInstallServices`. |
| `SetBusy` | Reste dans `MainWindow` | Booléen ; aucune sortie. | Activation, libellés ANNULER et verrouillage de navigation legacy inchangés. |
| `FormatTransferProgress` / `FormatRemainingTime` | Restent dans `MainWindow` | Données brutes de progression ; retourne du texte legacy. | Aucun service de maintenance ne connaît WPF ou ne formate un libellé d'interface. |

## Bail, annulation et erreurs

- Le handler legacy acquiert un bail `GameInstall` ou `GameUpdate` avant l'appel.
- Le pipeline reçoit ce bail, son `OperationId` et son `CancellationToken`.
- Les services ne créent aucune `CancellationTokenSource`, n'annulent rien et ne
  libèrent aucun bail.
- L'annulation utilisateur et la fermeture suivent uniquement les règles du
  `LauncherOperationCoordinator` de 02D.0.
- Chaque phase et chaque progression porte l'`OperationId`. L'adaptateur legacy
  les applique par `TryInvoke`; un callback obsolète est ignoré.
- `OperationCanceledException` remonte sans être transformée. Les erreurs HTTP,
  disque, taille, hash, permission et verrouillage remontent à
  `ExecuteGameActionAsync`, qui conserve les messages et notifications legacy.
- La v1.1.0 n'effectue aucun retry HTTP. Une réponse HTTP en échec termine donc
  après une seule requête. Les retries historiques concernent uniquement la
  suppression et le remplacement final.

## Ordre des effets de bord

Ordre nominal :

1. création du dossier d'installation ;
2. téléchargement et lecture du manifeste ;
3. arrêt des processus WoW appartenant à cette installation ;
4. lecture du cache et comparaison rapide ou réelle ;
5. calcul des fichiers gérés devenus obsolètes ;
6. suppression des anciens fichiers gérés ;
7. pour chaque fichier requis : création du temporaire adjacent, écriture,
   validation de taille, fermeture du flux, SHA-256, remplacement final ;
8. écriture UTF-8 sans BOM de `client-manifest-cache.json` ;
9. mise à jour du `GameAction` legacy via la phase `CacheSaved` ;
10. écriture de la configuration du client ;
11. copie du désinstalleur ;
12. écriture de `client-install.json` ;
13. écriture de l'entrée de désinstallation Windows ;
14. publication du résultat final dans l'interface legacy.

Exception historique conservée : lorsque le raccourci par `clientVersion`
reconnaît un client jouable sans cache, `GameFileVerifier` écrit immédiatement
le manifeste dans le cache pendant l'étape 4. Ce comportement et sa limite sont
déjà caractérisés par 02C ; 02D.1 ne les corrige pas.

En cas d'annulation ou d'erreur avant l'étape 8, aucun cache complet, marqueur,
désinstalleur ou enregistrement Windows nouveau n'est produit. Les fichiers déjà
remplacés restent appliqués, conformément au comportement v1.1.0. Le temporaire
du fichier en cours est supprimé au mieux. Il n'existe ni reprise HTTP par plage,
ni rollback global.
