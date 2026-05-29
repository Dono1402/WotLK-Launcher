# WotLK Launcher

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

1. Publier les nouveaux fichiers dans `/var/www/wotlk-launcher/launcher`.
2. Mettre a jour `/var/www/wotlk-launcher/launcher/launcher-update.json`.
3. Synchroniser l'endpoint de transition legacy si necessaire pour les anciens launchers.
4. Lancer:

```bash
/opt/wotlk-launcher-release/scripts/release-launcher.sh
```

Le script:

- valide le hash et la taille de `WotLK-Launcher.exe`;
- copie les assets dans `/srv/wotlk/launcher-releases/vX.Y.Z`;
- commit les metadonnees publiees;
- cree le tag git annote `vX.Y.Z`;
- pousse le tag si un remote `origin` existe;
- cree une GitHub Release si `gh` est installe et authentifie.

Les binaires ne sont pas stockes dans git. Git garde les metadonnees, les hashes et le tag de release; les assets restent dans le store serveur et peuvent etre uploades en release GitHub.
