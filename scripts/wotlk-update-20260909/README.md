# Recettes natives figées du 9 septembre 2026

Ces recettes de préparation sont spécifiques aux sources, candidats et
archives Atlas identifiés dans le [rapport](../../docs/WOTLK-UPDATE-PREPARATION-2026-09-09.md).
Elles ne sont pas des installateurs génériques et n'autorisent aucune bascule
ou interruption de service. Les recettes de build ne démarrent pas Hermes ou
worldserver, ne lisent aucune configuration privée et n'importent aucun SQL.
Le smoke Hermes séparé démarre uniquement une copie isolée, avec configuration
synthétique ; il ne constitue pas une commande de déploiement.

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
avec sorties et caches dirigés vers le candidat. Après restauration des dépendances :
`PrivateNetwork=yes` et sockets MariaDB/MySQL inaccessibles. Le répertoire
temporaire du candidat est monté sur `/tmp` dans l'espace de noms du processus.
Ne pas exécuter directement ces scripts sans revoir ce confinement.
La contre-revue a relevé que `ProtectSystem=strict` seul conserve des exceptions
standard d'écriture (`/home`, `/root`, `/run/user`, `/dev/shm`). Une nouvelle
exécution doit aussi protéger ces chemins, notamment avec `ProtectHome=yes`
et des protections explicites des répertoires runtime ; ne pas assimiler
l'ancienne commande à une interdiction exhaustive de toute autre écriture.

`hermes/smoke-published.py` et son plan décrivent le contrôle du paquet Linux
publié. Le [manifeste runtime séparé](../../docs/update-preparation/2026-09-09/hermes-runtime-manifest.json)
consigne sa réussite unique et le confinement renforcé effectivement vérifié.
Le plan reste une recette historique, pas une preuve de résultat. Le script
refuse un dossier de smoke déjà utilisé et n'autorise aucune relance automatique.

## World / Dungeon Clear

`world/build_world_linux.py` exige le paquet de sources, les manifestes et
les recettes capturées dans `evidence/`. Son mode par défaut est un plan
en lecture seule :

```text
python -B build_world_linux.py --root <prepared-world-root> --phase plan
```

Les phases explicites `baseline`, `compile`, `link`, `tests` écrivent
uniquement sous `build-isolated/` de la nouvelle racine Linux figée. Elles
doivent être lancées sous un confinement externe revu. Le premier passage
utilisait un seul CPU et `MemoryMax=4G`. Après la demande d'accélération,
la reprise a utilisé `--jobs 6`, sans quota CPU ni affinité monocœur,
`Nice=19`, `MemoryHigh=6G`, `MemoryMax=7G` et `MemorySwapMax=0`.
Le plafond d'espace d'adressage de chaque processus reste de 4 Gio.

Le confinement renforcé combine `ProtectSystem=strict`, `ProtectHome=yes`,
`PrivateDevices=yes`, `PrivateIPC=yes`, `NoNewPrivileges=yes`, réseau privé,
et chemins SQL/configurations runtime, `/run/user` et `/dev/shm` inaccessibles.
Seule la nouvelle racine est explicitement autorisée en écriture ; les
propriétés effectives et les montages doivent être vérifiés dans l'unité.
`KillMode=control-group` et `TimeoutStopSec=15` complètent l'arrêt des groupes
de processus enfants par le constructeur. Une réserve d'au moins
8 Gio doit subsister pendant les phases ; elle ne constitue pas le budget
total des fichiers à produire.

La reprise est scellée par hashes de source, manifestes, constructeur,
compilateurs, recettes et en-têtes générés. Un échec invalide les étapes
dépendantes. Les objets et bibliothèques historiques sont seulement lus,
jamais remplacés. Les données de navigation, si fournies, doivent rester
dans un montage en lecture seule ; aucune base de production n'est permise.

`world/migrate_world_linux_scheduler.py` ne lance aucune compilation. Son
préflight vérifie la paire de constructeurs revue, l'état arrêté, les 248
recettes et les empreintes de chaque objet déjà terminé. Son mode d'application
explicite sauvegarde et vérifie l'ancien constructeur et l'ancien état avant
de remplacer leur identité ensemble, avec journal de migration. Une
interruption entre les deux remplacements provoque un refus de reprise,
pas l'acceptation d'un état incohérent.

**Lancer impérativement cette migration avec `python3 -B`**, comme lors de
l'exécution vérifiée : elle importe les deux constructeurs pour comparaison.
Sans `-B` (ou `PYTHONDONTWRITEBYTECODE=1`), Python pourrait écrire un cache
`__pycache__` avant un échec du préflight ; la promesse de préflight sans
écriture dépend donc aussi de cette option de lancement.

Les dix tests de contrat et sept tests d'ordonnancement se rejouent sur un
dossier de préparation complet, sans compilation C++ ni connexion :

```powershell
$env:ATLAS_WORLD_PREPARATION_ROOT = '<prepared-world-root>'
python -B world/test_build_world_linux.py
python -B world/test_world_scheduler.py
python -B world/test_world_link_group.py
```

Les tests d'ordonnancement utilisent de faux compilateurs, vérifient la borne
de concurrence, l'annulation, les états JSON et la conservation des journaux.
Ils comparent aussi les commandes réellement émises par `compile_one` avec
le constructeur séquentiel figé dans `world/fixtures/`, et la reprise sans
nouvel appel de compilation. Ces tests ne remplacent pas les contrôles Linux
des unités et processus réels.

Seuls les chemins d'entrée et de fixture des tests sont adaptés au dépôt.
Les scripts et migrations sont conservés octet par octet par rapport aux
versions correspondantes de la préparation, en distinguant celles exécutées
des propositions restées locales.

### Correction de lien préparée, non exécutée sur Linux

Le [manifeste World natif](../../docs/update-preparation/2026-09-09/world-native-manifest.json)
identifie le constructeur réellement exécuté : `2b2e6e5b`, conservé dans
`world/fixtures/build_world_linux.parallel-sealed.py`. Il a compilé le world
candidat, mais le lien des tests a échoué avant toute exécution GoogleTest.

Le constructeur principal versionné `7230d028` ajoute uniquement deux bornes
de groupe d'archives au lien des tests. Les cinq tests supplémentaires de
`test_world_link_group.py` portent le total local à 22 : ils vérifient l'ajout
de ces deux tokens et l'identité des commandes de compilation et de lien World.
`migrate_world_linux_test_link.py` est la migration distincte préparée pour
une éventuelle reprise. **Cette correction et cette migration n'ont pas été
appliquées sur Linux** ; la suite exhaustive a été suspendue. Elles ne sont
pas nécessaires pour utiliser le world candidat déjà assemblé.
