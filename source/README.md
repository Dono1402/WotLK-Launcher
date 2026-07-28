# WotLK Launcher

Launcher Windows pour installer, mettre a jour et lancer le client WotLK.

Le launcher lit le manifeste du feed, telecharge les fichiers manquants ou modifies, puis verifie chaque fichier en SHA256.

Le client distribue est WotLK Classic 3.4.3.54261. Le bouton Jouer lance `Arctium Game Launcher Atlas.exe` avec le portail `animeclub.fr`; le feed ne contient ni `WTF`, ni caches, ni journaux utilisateur.

Le launcher s'execute sans elevation permanente. L'installer accorde au compte Windows courant un acces en modification limite au dossier du client; une ancienne installation est migree lors de sa derniere mise a jour elevee. Seules l'auto-mise a jour du launcher et la desinstallation peuvent encore demander une validation administrateur.

L'onglet Addons propose des installations optionnelles et independantes du feed client. Le launcher ne supprime que les dossiers qu'il a lui-meme enregistres dans `_classic_\Interface\AddOns\.atlas-addons.json`; les `SavedVariables` et les addons utilisateur restent intacts.

## Catalogue addons 3.4.3

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-AddonPackages.ps1 `
  -OutputDirectory .\artifacts\addons

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test-AddonPackages.ps1 `
  -PackageDirectory .\artifacts\addons
```

Le catalogue fige des versions testees declarant l'interface `30403`:

- WeakAuras 5.13.1
- ElvUI 13.61
- Questie 10.19.2
- Deadly Boss Mods 11.0.34 avec les modules Vanilla, Burning Crusade et WotLK
- Details! 20250119.13388.161
- AtlasLootClassic 3.2.0
- Auctionator 10.2.0-wrath
- Leatrix Plus 3.0.191
- Nova Instance Tracker 1.55-Wrath
- Attune WOTLK-314
- Baganator 158-wrath

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
