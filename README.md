# WotLK Launcher

## Version 1.4.0

[Installer et télécharger la version 1.4.0](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.4.0)

Le profil et l’armurerie 3D sont intégrés au client public. Cette version comprend aussi la refonte de l’interface, les améliorations du compte, de la présence des amis et des addons.

[Notes en français](releases/v1.4.0/PATCH-NOTES.md) · [English release notes](releases/v1.4.0/PATCH-NOTES.en.md) · [Instructions de compilation](releases/v1.4.0/BUILD.md)


Remote cible: `git@github.com:Dono1402/WotLK-Launcher.git`

Repo serveur pour historiser les versions publiees du launcher WotLK.

## Auth GitHub serveur

Une fois par serveur:

```bash
gh auth login --web --git-protocol ssh
gh auth setup-git
```

Si le repo `Dono1402/WotLK-Launcher` n'existe pas encore, le script de release peut le creer automatiquement quand `gh` est connecte.

Workflow:

1. Produire les octets finaux du launcher et de l'installer.
2. Depuis Atlas, lancer la publication administrative signee avec le `keyId`
   approuve. Le package versionne est publie avant le manifeste.
3. Verifier le canal HTTPS et l'endpoint de transition legacy.
4. Historiser ensuite la release avec:

```bash
sudo env ATLAS_LAUNCHER_SIGNING_KEY_ID=atlas-prod-p256-2026-01 \
  /opt/wotlk-launcher-release/source/Publish-Launcher-Atlas.sh \
  /chemin/WotLK-Launcher.exe \
  /chemin/WotLK-Launcher-Installer.exe \
  X.Y.Z
```

Puis:

```bash
ATLAS_LAUNCHER_SIGNING_KEY_ID=atlas-prod-p256-2026-01 \
  /opt/wotlk-launcher-release/scripts/release-launcher.sh
```

Le second script:

- valide le hash et la taille de `WotLK-Launcher.exe`;
- copie les assets dans `/srv/wotlk/launcher-releases/vX.Y.Z`;
- commit les metadonnees publiees;
- cree le tag git annote `vX.Y.Z`;
- pousse le tag si un remote `origin` existe;
- cree une GitHub Release si `gh` est installe et authentifie.

La cle privee n'existe que sur Atlas sous
`/etc/atlas-release-signing/launcher-update-private.pem`, avec un repertoire
`root:root` `0700` et un fichier `root:root` `0600`. Elle n'est jamais copiee
dans ce depot ni dans les racines publiques ou d'artefacts.

Les binaires ne sont pas stockes dans git. Git garde les metadonnees, les hashes et le tag de release; les assets restent dans le store serveur et peuvent etre uploades en release GitHub.
