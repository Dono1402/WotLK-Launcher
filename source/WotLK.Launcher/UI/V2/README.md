# Atlas Launcher UI V2 - Checkpoint Jeu

Lancer la V2 réelle avec :

```powershell
WotLK.Launcher.exe --ui-v2
```

Ce mode utilise la composition réelle unique du launcher. Au checkpoint 03B, le panneau Amis charge les relations Atlas existantes à son ouverture et permet les actions déjà exposées par l'API.

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
