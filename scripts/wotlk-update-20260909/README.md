# Recettes natives figées du 9 septembre 2026

Ces recettes de préparation sont spécifiques aux sources, candidats et
archives Atlas identifiés dans le [rapport](../../docs/WOTLK-UPDATE-PREPARATION-2026-09-09.md).
Elles ne sont pas des installateurs génériques et n'autorisent aucune bascule
ou interruption de service. Elles ne démarrent pas Hermes ou worldserver,
ne lisent aucune configuration privée et n'importent aucun SQL.

Les sources/exécutables de production, données de navigation, dépendances
et sorties de compilation ne sont pas dans ce dépôt. Un clone de ce dépôt
seul ne suffit donc pas à reconstruire ces candidats historiques.

## Hermes

`hermes/build-native.sh` sépare `restore` et `build-test-publish` et exige le
commit source figé dans le nouveau candidat. `hermes/package-native.sh`
contrôle les résultats avant de créer une archive sans configuration,
logs, données de comptes ou certificats Atlas. Leurs SHA256 sont dans le
[manifeste](../../docs/update-preparation/2026-09-09/hermes-manifest.json),
conservé sous sa forme de preuve : les noms de fichiers qu'il contient
désignent le paquet/dossier de préparation d'origine.

Le patch complet versionné se trouve dans
[`mod-atlas-chat/hermes`](../../mod-atlas-chat/hermes/README.md).
Il reproduit l'arbre source depuis `4247d957`, pas automatiquement le commit
de fusion et son historique Git nécessaires à l'identité de build canonique.

La compilation a été lancée sous une unité systemd distincte : `Nice=19`,
`CPUQuota=100%`, `MemoryMax=3G`, `MemorySwapMax=0`, `ProtectSystem=strict`,
seul le candidat en écriture. Après restauration des dépendances :
`PrivateNetwork=yes` et sockets MariaDB/MySQL inaccessibles. Le répertoire
temporaire du candidat est monté sur `/tmp` dans l'espace de noms du processus.
Ne pas exécuter directement ces scripts sans revoir ce confinement.

## World / Dungeon Clear

`world/build_world_linux.py` exige le paquet de sources, les manifestes et
les recettes capturées dans `evidence/`. Son mode par défaut est un plan
en lecture seule :

```text
python -B build_world_linux.py --root <prepared-world-root> --phase plan
```

Les phases explicites `baseline`, `compile`, `link`, `tests` écrivent
uniquement sous `build-isolated/` de la nouvelle racine Linux figée. Elles
doivent être lancées sous le confinement externe revu : utilisateur `debian`,
`Nice=19`, `CPUQuota=100%`, `MemoryMax=4G`, `MemorySwapMax=0`,
`ProtectSystem=strict`, réseau privé, sockets SQL/configurations runtime
inaccessibles, seule la nouvelle racine en écriture. Une réserve d'au moins
8 Gio doit subsister pendant les phases ; elle ne constitue pas le budget
total des fichiers à produire.

La reprise est scellée par hashes de source, manifestes, constructeur,
compilateurs, recettes et en-têtes générés. Un échec invalide les étapes
dépendantes. Les objets et bibliothèques historiques sont seulement lus,
jamais remplacés. Les données de navigation, si fournies, doivent rester
dans un montage en lecture seule ; aucune base de production n'est permise.

Les dix tests de contrat se rejouent sur un dossier de préparation complet,
sans compilation ni connexion :

```powershell
$env:ATLAS_WORLD_PREPARATION_ROOT = '<prepared-world-root>'
python -B world/test_build_world_linux.py
```

Seul le chemin de fixture de ce test a été rendu paramétrable lors de la
copie dans ce dépôt. Le constructeur et les deux scripts Hermes sont
identiques octet par octet aux recettes exécutées pendant cette préparation.
