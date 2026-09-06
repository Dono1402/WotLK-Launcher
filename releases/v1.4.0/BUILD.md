# Construire Atlas Launcher 1.4.0

Les sources du tag `v1.4.0` correspondent aux exécutables publics du 6 septembre 2026. Les travaux ultérieurs sur la messagerie et les profils d’amis ne font pas partie de cette version.

## Client Windows

Prérequis : Windows et le SDK .NET 8 (build de référence : 8.0.424).
Télécharger `armory-runtime.zip` depuis la release GitHub 1.4.0 et vérifier son SHA-256 :

`d86320bfc6960ae67454d870ca930a7ed6cb54ee4feacf3bea9f96bf533c324c`

Ce paquet contient les dépendances exactes de l’armurerie, leurs licences, Node et le programme Microsoft WebView2. Il ne contient pas de fichiers du jeu ni de données de compte. Les modèles du personnage sont exportés depuis le jeu installé par le joueur.

Depuis la racine du dépôt, dans PowerShell :

```powershell
./scripts/build-public-client.ps1 -DotnetPath (Get-Command dotnet).Source -ArmoryPayloadPath C:/Downloads/armory-runtime.zip -OutputDir ./artifacts/AtlasLauncherPublic
```

Pour construire uniquement les projets et les tests :

```powershell
dotnet build source/WotLK.Launcher.IntegrationTests/WotLK.Launcher.IntegrationTests.csproj -c Release -p:NuGetAudit=false
```

L’API ASP.NET Core est dans `source/WotLK.Launcher.Server`. Sa configuration exemple ne contient aucun secret. Sa mise en service et celle du module serveur nécessitent une configuration propre à l’environnement ; une compilation locale ne les déploie pas.

## Fichiers de la release

- `WotLK-Launcher.exe` : client public 1.4.0.
- `WotLK-Launcher-Installer.exe` : installateur 1.4.0.
- `launcher-update.json` : manifeste de mise à jour signé pour le canal Atlas.
- `armory-runtime.zip` : dépendances exactes pour reconstruire le client.
- `SHA256SUMS.txt` : empreintes des fichiers distribués.

Les notes détaillées sont disponibles en [français](PATCH-NOTES.md) et en [anglais](PATCH-NOTES.en.md).
