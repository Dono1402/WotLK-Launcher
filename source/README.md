# WotLK Launcher

Launcher Windows pour installer, mettre a jour et lancer le client WotLK.

Le launcher lit le manifeste du feed, telecharge les fichiers manquants ou modifies, puis verifie chaque fichier en SHA256.

Le client distribue est WotLK Classic 3.4.3.54261. Le bouton Jouer lance `Arctium Game Launcher Atlas.exe` avec le portail `animeclub.fr`; le feed ne contient ni `WTF`, ni caches, ni journaux utilisateur.

## Feed Classic frFR

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-WotLKClassicFeed.ps1 `
  -ClientRoot "C:\chemin\World of Warcraft 3.4.3.54261-frFR" `
  -OutputRoot "C:\chemin\WotLK-Classic-LauncherFeed" `
  -AtlasLauncherPath "C:\chemin\Arctium Game Launcher Atlas.exe"
```

## Build serveur

```bash
cd /opt/wotlk-launcher-release/source
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
dotnet publish WotLK.Launcher/WotLK.Launcher.csproj -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

## Publication

Les binaires publics sont deployes dans `/var/www/wotlk-launcher/launcher` puis historises par `/opt/wotlk-launcher-release/scripts/release-launcher.sh`.
Le launcher ne stocke plus d'identifiant secret local.
