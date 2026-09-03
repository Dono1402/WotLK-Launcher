# WotLK Launcher

Launcher Windows pour installer, mettre a jour et lancer le client WotLK.

Le launcher lit le manifeste du feed, telecharge les fichiers manquants ou modifies, puis verifie chaque fichier en SHA256.

Le client distribue est WotLK Classic 3.4.3.54261. Le bouton Jouer lance `Arctium Game Launcher Atlas.exe` avec le portail `animeclub.fr`; le feed ne contient ni `WTF`, ni caches, ni journaux utilisateur.

Le launcher s'execute sans elevation permanente. L'installer accorde au compte Windows courant un acces en modification limite au dossier du client; une ancienne installation est migree lors de sa derniere mise a jour elevee. Seules l'auto-mise a jour du launcher et la desinstallation peuvent encore demander une validation administrateur.

Le launcher comprend les pages principales Jeu, Addons, Amis et Patch notes. Le statut du serveur, le profil et les parametres restent accessibles directement depuis l'en-tete. La page Addons propose des onglets Installe/Catalogue/Mises a jour, des categories, une recherche, un tri et des cartes illustrees. Les addons peuvent etre installes ou mis a jour pendant que WoW est ouvert, puis appliques avec `/reload`. Le launcher ne supprime que les dossiers qu'il a lui-meme enregistres dans `_classic_\Interface\AddOns\.atlas-addons.json`; les `SavedVariables` et les addons utilisateur restent intacts.

Les cartes utilisent les logos de projet distribues par CurseForge. Les URL d'origine sont conservees dans `WotLK.Launcher/Assets/Launcher/addon-icons/sources.json`. ElvUI n'etant pas distribue officiellement sur CurseForge, son icone provient du fichier `LogoAddon.tga` inclus dans le paquet officiel ElvUI.

Le compte Atlas permet de modifier et valider l'e-mail via Brevo, de choisir l'avatar, de changer le mot de passe, de consulter les sessions actives et d'en revoquer une a distance. Le tableau de bord affiche aussi l'etat direct des passerelles et du worldserver. La double authentification et les codes de recuperation restent des ameliorations futures.

`account` reste la table technique d'identite AzerothCore et conserve notamment
les comptes `rndbot` de Playerbots. Seule l'existence d'une ligne dans
`atlas_launcher_profile` donne acces aux fonctions Atlas (session launcher,
profil, avatar et social). Aucun profil Atlas n'est cree automatiquement lors
de la connexion d'un compte AzerothCore existant.

## Catalogue addons 3.4.3

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-AddonPackages.ps1 `
  -OutputDirectory .\artifacts\addons

powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Test-AddonPackages.ps1 `
  -PackageDirectory .\artifacts\addons
```

Le catalogue fige des versions testees declarant l'interface `30403`:

- WeakAuras 5.12.8 (derniere version compatible WotLK Classic 3.4.3)
- ElvUI 13.61
- Questie 10.19.2
- What's Training? 5.0.3
- Deadly Boss Mods 11.0.34 avec les modules Vanilla, Burning Crusade et WotLK
- Details! 20250119.13388.161
- AtlasLootClassic 3.2.0
- Auctionator 10.2.0-wrath
- Leatrix Maps 3.0.191
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

Les binaires publics sont signes et deployes par
`Publish-Launcher-Atlas.sh`. La publication doit etre executee en root sur
Atlas avec `ATLAS_LAUNCHER_SIGNING_KEY_ID=atlas-prod-p256-2026-01`; elle utilise
uniquement `/etc/atlas-release-signing/launcher-update-private.pem`, publie le
package versionne avant le manifeste et verifie l'ancre publique embarquee.
Les metadonnees sont ensuite historisees par
`/opt/wotlk-launcher-release/scripts/release-launcher.sh`.
Le launcher ne stocke plus d'identifiant secret local.
