# Tests DOM de Messages

La suite charge les assets réels de Messages avec la politique CSP extraite de l’hôte natif. Elle utilise Microsoft Edge en mode headless, un profil temporaire et un pont natif simulé. Les comptes, images et réponses réseau sont fictifs ; aucune session utilisateur ou instance du launcher n’est contrôlée.

Depuis la racine du dépôt :

```powershell
node source/WotLK.Launcher.IntegrationTests/Frontend/chat-dom-tests.cjs
```

Le script résout la racine du dépôt depuis son propre emplacement ; il peut donc aussi être lancé depuis un autre répertoire. Node.js, Playwright et Microsoft Edge doivent être disponibles. Aucune installation ou modification du poste n’est effectuée par la suite.

## Configuration facultative

| Variable d’environnement | Valeur par défaut |
| --- | --- |
| `ATLAS_CHAT_REPO_ROOT` | Racine du dépôt déduite de l’emplacement du script. |
| `ATLAS_CHAT_TEST_OUTPUT` | `artifacts/atlas-chat-premium-20260907/dom`, relatif à la racine du dépôt. Accepte également un chemin absolu. |
| `ATLAS_CHAT_PLAYWRIGHT` | Module `playwright` résolu par Node.js, puis runtime Codex du compte Windows courant sous `.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright`. Pour une autre installation, utiliser son chemin absolu. |
| `ATLAS_CHAT_EDGE_PATH` | `Microsoft/Edge/Application/msedge.exe` sous `ProgramFiles(x86)`, puis `ProgramFiles`. Pour une autre installation, utiliser le chemin absolu de l’exécutable. |

Exemple avec un runtime fourni par l’environnement de test :

```powershell
$env:ATLAS_CHAT_PLAYWRIGHT = 'C:/test-runtime/node_modules/playwright'
$env:ATLAS_CHAT_EDGE_PATH = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'
$env:ATLAS_CHAT_TEST_OUTPUT = 'artifacts/chat-dom-ci'
node source/WotLK.Launcher.IntegrationTests/Frontend/chat-dom-tests.cjs
```

La suite écrit les captures PNG et `results.json` dans le répertoire de sortie. Toutes les captures utilisent le viewport Messages mesuré dans la shell V2 fixe : **1597 × 872 pixels CSS**, sous un en-tête de 124 DIP ; aucun redimensionnement n’est effectué. Le relevé conserve le viewport et les empreintes des quatre assets testés. Ces fichiers générés, profils et dépendances ne font pas partie des sources de tests.

Les 103 assertions couvrent notamment les statuts et avatars, les images sans cadre, les suppressions, le sélecteur Armory, les brouillons, les pièces jointes, les groupes, les médias, les accusés de lecture et le défilement. Elles vérifient également le signal `composerState` transmis au natif lors de l’entrée et de la sortie d’édition, des changements de fil ou de session et de la révocation du droit d’écrire.

La finition premium vérifie l’absence des filtres et de la recherche dans la colonne Conversations, le maintien des badges non lus et de la recherche dans Nouvelle conversation, l’envoi par icône seule et ses libellés accessibles FR/EN, l’alignement des commandes à droite, leur ordre clavier et la saisie multiligne.

Les captures préservent la transparence du document. La vérification CSS ne démontre pas à elle seule la continuité du fond Citadelle : sa composition avec l’en-tête est vérifiée séparément dans la shell WPF complète. Le pont étant simulé, cette suite ne remplace pas un dépôt Explorer dans le launcher réel ou une connexion à deux comptes réels.
