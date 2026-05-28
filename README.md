# WotLK Launcher Releases

Repo serveur pour historiser les versions publiees du launcher WotLK.

Workflow:

1. Publier les nouveaux fichiers dans `/var/www/wotlk-launcher/launcher`.
2. Mettre a jour `/var/www/wotlk-launcher/launcher/launcher-update.json`.
3. Garder l'ancien endpoint de transition `/var/www/animeclub/launcher` synchronise tant que des launchers anciens pointent dessus.
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

