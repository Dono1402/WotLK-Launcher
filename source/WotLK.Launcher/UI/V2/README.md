# Atlas Launcher UI V2 - Checkpoint Jeu

Lancer la V2 réelle en lecture seule avec :

```powershell
WotLK.Launcher.exe --ui-v2
```

Ce mode lit le dossier, la langue et la version installée, puis tente une seule restauration de la session existante. Il ne démarre aucun téléchargement, timer, service d'amis, analyse de manifeste ou auto-update. Toutes les commandes mutantes restent désactivées au checkpoint 02A.

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
WotLK.Launcher.exe --ui-v2 --preview-auth=atlas-enrollment
WotLK.Launcher.exe --ui-v2 --preview-auth=atlas-enrollment-error
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

`--preview-auth` sans valeur ouvre la connexion. Les scénarios `atlas-enrollment` et `atlas-enrollment-error` présentent le parcours volontaire d'activation Atlas pour un compte WoW existant, avec des données entièrement fictives. Cet argument sans `--ui-v2` est refusé avant toute composition de service. Le preview d'authentification ne crée ni authentification réelle, ni client HTTP, ni session, ni accès au client WotLK, ni timer, ni processus enfant.

Le manifeste v1.1.0 ne déclare pas explicitement de mode DPI. Le processus observé sous Windows est `PROCESS_SYSTEM_DPI_AWARE`. Ce checkpoint conserve volontairement ce comportement ; le passage à PerMonitorV2 est hors périmètre.
