# WotLK Launcher

Launcher Windows pour installer, mettre a jour et lancer le client WotLK.

Le launcher lit le manifeste du feed, telecharge les fichiers manquants ou modifies, puis verifie chaque fichier en SHA256.

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
