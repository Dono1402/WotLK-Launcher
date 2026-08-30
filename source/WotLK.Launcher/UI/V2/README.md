# Atlas Launcher UI V2 - Checkpoint Jeu

Lancer la prévisualisation avec :

```powershell
WotLK.Launcher.exe --ui-v2
```

Sans cet argument, le launcher v1.1.0 et ses services existants restent le chemin de démarrage par défaut. Le mode de prévisualisation n'instancie ni authentification, ni réseau, ni minuterie, ni téléchargement.

Les dictionnaires `AtlasV2.Tokens.xaml`, `AtlasV2.Icons.xaml` et `AtlasV2.Controls.xaml` sont chargés dans les ressources de l'application uniquement après détection de `--ui-v2`. Ce chargement ciblé est nécessaire pour que les `UserControl` WPF compilés puissent résoudre leurs ressources avant leur rattachement à la fenêtre. Toutes les clés sont préfixées `AtlasV2.` et aucun style global implicite n'est déclaré ; le démarrage v1.1.0 ne charge donc aucune ressource V2.

Deux arguments réservés à la validation visuelle permettent de fixer le viewport logique et l'état initial du drawer :

```powershell
WotLK.Launcher.exe --ui-v2 --ui-v2-size=1080x680 --ui-v2-friends-open
```

Les statuts, l'actualité, l'installation et la liste d'amis affichés dans ce checkpoint sont fictifs. Seules les commandes de fenêtre et l'ouverture/fermeture du panneau Amis sont interactives.

Le manifeste v1.1.0 ne déclare pas explicitement de mode DPI. Le processus observé sous Windows est `PROCESS_SYSTEM_DPI_AWARE`. Ce checkpoint conserve volontairement ce comportement ; le passage à PerMonitorV2 est hors périmètre.
