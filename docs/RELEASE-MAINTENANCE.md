# Compilation et publication d’Atlas Launcher

Cette page s’adresse aux personnes qui développent ou publient le launcher. Pour l’installer et jouer, utilise le [téléchargement Windows](../README.md).

## Compiler le client public

Prérequis : Windows, SDK .NET 8 et paquet `armory-runtime.zip` de la version concernée, disponible dans les fichiers de la release GitHub. Vérifier son empreinte dans `SHA256SUMS.txt` avant de l’utiliser. Le paquet comprend les dépendances de l’armurerie et leurs licences ; les fichiers du jeu et les données de compte en sont exclus.

Depuis la racine du dépôt, avec le paquet enregistré sous `artifacts/atlas-release-150/armory-runtime.zip` :

```powershell
./scripts/build-public-client.ps1 `
  -DotnetPath (Get-Command dotnet).Source `
  -ArmoryPayloadPath "$PWD/artifacts/atlas-release-150/armory-runtime.zip" `
  -OutputDirectory "$PWD/artifacts/atlas-release-150/public-client"

./scripts/build-atlas-installer.ps1 `
  -DotnetPath (Get-Command dotnet).Source `
  -LauncherPayloadPath "$PWD/artifacts/atlas-release-150/public-client/WotLK-Launcher.exe" `
  -OutputDirectory "$PWD/artifacts/atlas-release-150/setup"
```

L’installateur contient le client public final. Les exécutables, caches et fichiers générés restent hors Git. Le [compte rendu 1.5.0](../releases/v1.5.0/LIVRAISON.md) précise les versions, empreintes et vérifications de la livraison. Les [instructions 1.4.0](../releases/v1.4.0/BUILD.md) restent disponibles pour cette version historique.

## Identité Git et GitHub

Les releases GitHub utilisent le compte connecté à `gh` :

```bash
gh auth login --web --git-protocol ssh
gh auth setup-git
```

Les commits et tags utilisent l’identité Git configurée sur le dépôt (`user.name` et `user.email`). Le script de publication ne la remplace pas. Une adresse GitHub `noreply` permet de rattacher les commits au compte sans publier son adresse personnelle.

## Fichiers destinés aux joueurs

Sur GitHub, **AtlasLauncherSetup.exe** est le seul exécutable proposé au téléchargement. Les notes de version et les empreintes accompagnent l’installateur ; le paquet d’armurerie et le manifeste restent disponibles pour la compilation et la vérification.

Le fichier `WotLK-Launcher.exe` est conservé sur le serveur de distribution à l’URL versionnée du manifeste signé. Il sert à la mise à jour automatique et ne doit pas être ajouté aux fichiers de la release GitHub. Le nom interne `WotLK-Launcher-Installer.exe` dans le stockage serveur ne change pas le nom public de l’installateur.

## Publication serveur

`source/Publish-Launcher-Atlas.sh` vérifie et signe le manifeste, installe les fichiers versionnés, puis remplace le manifeste public. La clé privée reste sur le serveur, hors du dépôt et des répertoires publics. Préparer et vérifier les candidats avant toute publication ; le manifeste doit désigner exactement les octets du client final.

`scripts/release-launcher.sh` est un outil d’administration du serveur : il copie les fichiers de livraison, prépare les métadonnées, crée un commit et un tag, pousse `main` et publie sur GitHub. Il peut aussi récupérer des fichiers depuis la configuration du serveur. Son exécution modifie donc davantage que la présentation d’une release ; relire les fichiers préparés et leur périmètre avant de l’utiliser.

Pour la version 1.5.0, la préparation isolée, la bascule atomique et les vérifications HTTPS sont décrites dans le [compte rendu de livraison](../releases/v1.5.0/LIVRAISON.md).
