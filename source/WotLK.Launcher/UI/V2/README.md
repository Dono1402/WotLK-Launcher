# Atlas Launcher UI V2 - Checkpoint Jeu

Lancer la V2 réelle avec :

```powershell
WotLK.Launcher.exe --ui-v2
```

Ce mode utilise la composition réelle unique du launcher. Au checkpoint 03B.1, le panneau Amis charge les relations Atlas existantes, résout leurs photos 64 px via l'`AvatarImageCache` partagé et utilise un unique rafraîchissement social de 15 secondes pendant la session authentifiée.

Lancer une prévisualisation fictive déterministe avec :

```powershell
WotLK.Launcher.exe --ui-v2 --preview-state=Ready
```

Sans `--ui-v2`, le launcher v1.1.0 et ses services existants restent le chemin de démarrage par défaut. Le mode de prévisualisation n'instancie ni authentification, ni réseau, ni minuterie, ni téléchargement.

Les dictionnaires `AtlasV2.Tokens.xaml`, `AtlasV2.Icons.xaml` et `AtlasV2.Controls.xaml` sont chargés dans les ressources de l'application uniquement après détection de `--ui-v2`. Ce chargement ciblé est nécessaire pour que les `UserControl` WPF compilés puissent résoudre leurs ressources avant leur rattachement à la fenêtre. Toutes les clés sont préfixées `AtlasV2.` et aucun style global implicite n'est déclaré ; le démarrage v1.1.0 ne charge donc aucune ressource V2.

Deux arguments réservés à la validation visuelle permettent de fixer le viewport logique et l'état initial du drawer :

```powershell
WotLK.Launcher.exe --ui-v2 --preview-state=Ready --ui-v2-size=1080x680 --ui-v2-friends-open
```

Dans le mode preview uniquement, les statuts, l'actualité, l'installation et la liste d'amis sont fictifs. Seules les commandes de fenêtre et l'ouverture/fermeture du panneau Amis sont interactives.

Prévisualiser le catalogue Addons V2 :

```powershell
WotLK.Launcher.exe --ui-v2 --preview-addons=default
WotLK.Launcher.exe --ui-v2 --preview-addons=updates
WotLK.Launcher.exe --ui-v2 --preview-addons=detail
WotLK.Launcher.exe --ui-v2 --preview-addons=installing
WotLK.Launcher.exe --ui-v2 --preview-addons=empty
WotLK.Launcher.exe --ui-v2 --preview-addons=error
WotLK.Launcher.exe --ui-v2 --preview-addons=many
WotLK.Launcher.exe --ui-v2 --preview-addons=game-running
```

Ces scénarios utilisent uniquement des états de présentation locaux de 0, 6, 20 ou 50 entrées. La recherche, les filtres, le panneau de détail et les transitions visuelles sont fictifs. Aucun catalogue distant, fichier `.atlas-addons.json`, téléchargement, processus ou timer n'est créé. Les logos proviennent exclusivement des ressources déjà documentées dans `Assets/Launcher/addon-icons/sources.json`; les entrées étendues sans ressource utilisent l'icône générique Atlas.

Prévisualiser le Centre d’activité Atlas :

```powershell
WotLK.Launcher.exe --ui-v2 --preview-activity=idle
WotLK.Launcher.exe --ui-v2 --preview-activity=game-download
WotLK.Launcher.exe --ui-v2 --preview-activity=game-install
WotLK.Launcher.exe --ui-v2 --preview-activity=game-verify
WotLK.Launcher.exe --ui-v2 --preview-activity=game-repair
WotLK.Launcher.exe --ui-v2 --preview-activity=addon
WotLK.Launcher.exe --ui-v2 --preview-activity=addon-batch
WotLK.Launcher.exe --ui-v2 --preview-activity=addon-remove
WotLK.Launcher.exe --ui-v2 --preview-activity=self-update
WotLK.Launcher.exe --ui-v2 --preview-activity=error
WotLK.Launcher.exe --ui-v2 --preview-activity=history
WotLK.Launcher.exe --ui-v2 --preview-activity=many-history
```

Ces états restent exclusivement visuels et déterministes. Ils ne composent aucun `LauncherRuntime`, coordinateur métier, téléchargement, client HTTP, timer, token d’annulation, accès disque métier ou processus enfant.

## Centre d’activité réel - checkpoint 04B.2

En mode `--ui-v2`, `LauncherActivityCoordinator` agrège en lecture seule les snapshots déjà coalescés de `LauncherOperationCoordinator`, `GameRuntimeCoordinator`, `LauncherAddonsCoordinator` et `LauncherSelfUpdateCoordinator`. Il n’acquiert aucun bail, ne crée aucune source d’annulation et ne recalcule ni débit ni estimation de durée.

Le centre suit installation, mise à jour, vérification et réparation du client, les installations, mises à jour, réparations, suppressions et batchs Addons, ainsi que le téléchargement réel d’une mise à jour d’Atlas Launcher. `Play`, les rafraîchissements de lecture et les simples recherches de version restent exclus. Un batch conserve un seul `OperationId`, expose l’addon courant et les identifiants encore en attente, sans fabriquer de pourcentage global.

L’historique est limité aux dix derniers contrats terminaux explicites de la session, dédupliqués par `OperationId`. Il n’est pas persisté. Le bouton Annuler délègue directement à `LauncherOperationCoordinator.CancelFromUser`, et les liens de l’historique ouvrent Jeu ou Addons sans lancer d’opération.

## Auto-update réel - checkpoint 04B.3b

`LauncherRuntime` compose une seule instance de `LauncherSelfUpdateCoordinator` et un seul timer de 30 secondes. Les vues Paramètres et Activité observent cette instance et ne possèdent ni timer, ni client HTTP, ni pipeline de téléchargement. Paramètres affiche le réglage automatique existant en lecture seule, les versions installée/disponible, la dernière comparaison exploitable et les commandes de recherche puis de mise à jour.

Une recherche est coalescée et ne crée aucune Activity. Seul un téléchargement ayant obtenu le bail global `LauncherAutoUpdate` apparaît sous le nom `Atlas Launcher`; ses octets, pourcentage, débit et ETA viennent directement du downloader extrait du legacy. Après validation, l’annulation est fermée et le coordinateur délègue exclusivement au mécanisme atomique de 04B.3a. En cas de récupération d’une transaction interrompue, les retries automatiques sont neutralisés pour le lancement courant afin d’éviter une boucle de rollback.

Les harnais déterministes associés se lancent avec :

```powershell
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --activity-runtime
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --activity-runtime-wpf
```

## Addons V2 réels - checkpoint 04A.2

En mode `--ui-v2`, la page Addons utilise le catalogue authentifié historique et délègue toutes les mutations à `AddonInstallServices` via `LegacyAddonManagementService`. Elle ne possède aucun téléchargeur, extracteur, calcul de hash, format d'état ou mécanisme de suppression propre.

Caractérisation du comportement conservé :

- `AddonInstallServices.LoadCatalogAsync` charge et valide le schéma 1 pour l'interface 30403 ; la recherche et les filtres restent ensuite entièrement locaux.
- `AddonInstallServices.Inspect` est la seule source de l'état local. Un dossier seul est `DetectedUnmanaged`, jamais installé. Une entrée valide de `.atlas-addons.json` fournit version, hash, dossiers et date ; un dossier géré absent produit `MissingFiles`.
- Installer, mettre à jour et réparer passent tous par `ApplySelectionAsync`. Chaque action V2 lui fournit un catalogue limité au package ciblé afin de ne pas modifier les autres addons.
- Une mise à jour ne publie sa nouvelle version qu'après téléchargement, validation et application réussis. La transaction historique restaure les dossiers précédents si l'application échoue.
- Supprimer passe par la même sélection avec `false` et ne retire que les dossiers enregistrés comme gérés. L'opération n'est pas présentée comme annulable, car la phase de déplacement/suppression legacy ne consulte pas de token interne.
- Les composants supplémentaires sont téléchargés et extraits dans la même transaction que l'archive principale. `dependencies` est actuellement une métadonnée de catalogue : le pipeline historique ne résout pas automatiquement de graphe de dépendances.
- La progression disponible est constituée des octets reçus et de la taille attendue par archive. La V2 en dérive pour l'affichage pourcentage, débit et estimation ; les phases sans mesure sont indéterminées. Les publications sont coalescées à 80 ms, sauf changement de phase et valeur terminale.
- WoW ouvert n'interdit pas les mutations addon et n'est jamais fermé par ce pipeline. Après un succès, la V2 conseille `/reload` sans tenter de l'injecter dans le jeu.
- Un `401` invalide la session par `LauncherSessionCoordinator`. Les erreurs réseau, disque, accès et fichier verrouillé restent attachées à la ligne concernée et les logs ne contiennent que l'identifiant, l'opération, la version, la phase, le résultat et la catégorie d'erreur.
- Un rafraîchissement distant en échec conserve le catalogue déjà chargé en mémoire. Aucun nouveau cache persistant de catalogue n'est introduit.

`Tout mettre à jour` est réel lorsque plusieurs mises à jour sont disponibles. Il conserve un seul bail global `Addons`, traite les packages un par un dans l'ordre alphabétique, enregistre chaque succès, s'arrête au premier échec et partage une annulation globale. Aucun téléchargement parallèle n'est lancé.

Pendant ce batch, les compteurs suivent immédiatement les états réellement enregistrés, mais les lignes initialement visibles sous le filtre `Mises à jour` restent épinglées jusqu'à la fin de l'opération. Le filtre est alors réappliqué en une fois afin d'éviter des disparitions successives pendant le téléchargement.

Matrice de concurrence effective :

| Combinaison | Résultat |
|---|---|
| Install + Install | refus immédiat `Busy` |
| Update + Update | refus immédiat `Busy` |
| Install + Update | refus immédiat `Busy` |
| Remove + Download | refus immédiat `Busy` |
| Repair + Update | refus immédiat `Busy` |
| Addons + GameInstall/GameUpdate/GameRepair | refus immédiat `Busy` |
| Addons + Verify | refus immédiat `Busy` |
| Addons + LauncherAutoUpdate | refus immédiat `Busy` |
| Addons + Play | refus immédiat `RejectedByCompatibility` |
| Play + Verify non mutante, client jouable | autorisé par le coordinateur global |

`TryBegin` ne met aucune action en file d'attente. La présence d'un processus WoW déjà ouvert ne constitue pas un bail `Play` et reste donc compatible avec la gestion des addons.

Le harnais déterministe associé se lance avec :

```powershell
& 'C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe' run --project 'source\WotLK.Launcher.IntegrationTests\WotLK.Launcher.IntegrationTests.csproj' -c Release --no-build -- --addons-runtime
```

Prévisualiser les états isolés du panneau Amis :

```powershell
WotLK.Launcher.exe --ui-v2 --preview-friends=empty
WotLK.Launcher.exe --ui-v2 --preview-friends=populated
WotLK.Launcher.exe --ui-v2 --preview-friends=incoming-requests
WotLK.Launcher.exe --ui-v2 --preview-friends=outgoing-requests
WotLK.Launcher.exe --ui-v2 --preview-friends=add-friend
WotLK.Launcher.exe --ui-v2 --preview-friends=add-friend-error
WotLK.Launcher.exe --ui-v2 --preview-friends=avatar-fallback
WotLK.Launcher.exe --ui-v2 --preview-friends=network-error
WotLK.Launcher.exe --ui-v2 --preview-friends=avatars
WotLK.Launcher.exe --ui-v2 --preview-friends=mixed-avatars
WotLK.Launcher.exe --ui-v2 --preview-friends=avatar-changed
WotLK.Launcher.exe --ui-v2 --preview-friends=network-stale
WotLK.Launcher.exe --ui-v2 --preview-friends=100
```

Ces scénarios ouvrent directement le drawer avec des données locales déterministes. Ils ne créent ni `LauncherRuntime`, ni service d'authentification, ni client HTTP, ni stockage, ni processus enfant.

Prévisualiser l'overlay d'authentification fictif :

```powershell
WotLK.Launcher.exe --ui-v2 --preview-auth=login
WotLK.Launcher.exe --ui-v2 --preview-auth=register
WotLK.Launcher.exe --ui-v2 --preview-auth=loading
WotLK.Launcher.exe --ui-v2 --preview-auth=login-error
WotLK.Launcher.exe --ui-v2 --preview-auth=register-error
WotLK.Launcher.exe --ui-v2 --preview-auth=register-validation
WotLK.Launcher.exe --ui-v2 --preview-auth=email-warning
WotLK.Launcher.exe --ui-v2 --preview-auth=service-unavailable
```

Prévisualiser la page Compte et le recadrage fictifs :

```powershell
WotLK.Launcher.exe --ui-v2 --preview-account=profile
WotLK.Launcher.exe --ui-v2 --preview-account=fallback
WotLK.Launcher.exe --ui-v2 --preview-account=crop
WotLK.Launcher.exe --ui-v2 --preview-account=uploading
WotLK.Launcher.exe --ui-v2 --preview-account=upload-error
WotLK.Launcher.exe --ui-v2 --preview-account=removing
WotLK.Launcher.exe --ui-v2 --preview-account=security
WotLK.Launcher.exe --ui-v2 --preview-account=sessions
```

Les scénarios Compte utilisent exclusivement des états de présentation locaux. Ils ne composent aucun service de compte, client HTTP, sélecteur de fichier ou stockage.

`--preview-auth` sans valeur ouvre la connexion. Cet argument sans `--ui-v2` est refusé avant toute composition de service. Le preview d'authentification ne crée ni authentification réelle, ni client HTTP, ni session, ni accès au client WotLK, ni timer, ni processus enfant.

Le manifeste v1.1.0 ne déclare pas explicitement de mode DPI. Le processus observé sous Windows est `PROCESS_SYSTEM_DPI_AWARE`. Ce checkpoint conserve volontairement ce comportement ; le passage à PerMonitorV2 est hors périmètre.
