# Tests DOM de Messages

La suite charge les assets réels de Messages avec la politique CSP extraite de l’hôte natif. Elle utilise Microsoft Edge en mode headless, un profil temporaire et un pont natif simulé. Les comptes, images et réponses réseau sont fictifs ; aucune session utilisateur ou instance du launcher n’est contrôlée.

Depuis la racine du dépôt :

```powershell
node source/WotLK.Launcher.IntegrationTests/Frontend/chat-dom-tests.cjs
node source/WotLK.Launcher.IntegrationTests/Frontend/chat-media-tests.cjs
node source/WotLK.Launcher.IntegrationTests/Frontend/chat-search-tests.cjs
```

Le script résout la racine du dépôt depuis son propre emplacement ; il peut donc aussi être lancé depuis un autre répertoire. Node.js, Playwright et Microsoft Edge doivent être disponibles. Aucune installation ou modification du poste n’est effectuée par la suite.

La suite `chat-media-tests.cjs` vérifie le décodage de la première image avant lecture, le préchargement limité aux vidéos proches de l’écran, les commandes et le temps en surimpression, leur disparition après inactivité et leur accès au clavier. Elle utilise de vrais médias synthétiques, vérifie lecture/pause, recherche, volume, plein écran et libération des lecteurs retirés, puis enregistre ses captures et résultats dans `ATLAS_CHAT_TEST_OUTPUT`.

La suite `chat-search-tests.cjs` vérifie Ctrl+F, Entrée/Maj+Entrée et Échap, le retour du focus et du brouillon, la recherche littérale sans distinction de casse ou d’accent, ainsi que la conservation des liens et des médias. Elle couvre les spoilers masqués, les messages supprimés, les changements de compte et de conversation, les pages d’historique chargées progressivement, les erreurs et réponses tardives, et l’absence d’accusé de lecture causé par un déplacement automatique. Une seule page peut être en cours de chargement ; après vingt pages, la recherche propose explicitement de continuer. Ses captures et `report.json` sont écrits dans `ATLAS_CHAT_SEARCH_TEST_OUTPUT` (par défaut `artifacts/atlas-chat-search-20260907`).

## Configuration facultative

| Variable d’environnement | Valeur par défaut |
| --- | --- |
| `ATLAS_CHAT_REPO_ROOT` | Racine du dépôt déduite de l’emplacement du script. |
| `ATLAS_CHAT_TEST_OUTPUT` | `artifacts/atlas-chat-followup-20260907/dom`, relatif à la racine du dépôt. Accepte également un chemin absolu. |
| `ATLAS_CHAT_PLAYWRIGHT` | Module `playwright` résolu par Node.js, puis runtime Codex du compte Windows courant sous `.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright`. Pour une autre installation, utiliser son chemin absolu. |
| `ATLAS_CHAT_EDGE_PATH` | `Microsoft/Edge/Application/msedge.exe` sous `ProgramFiles(x86)`, puis `ProgramFiles`. Pour une autre installation, utiliser le chemin absolu de l’exécutable. |

Exemple avec un runtime fourni par l’environnement de test :

```powershell
$env:ATLAS_CHAT_PLAYWRIGHT = 'C:/test-runtime/node_modules/playwright'
$env:ATLAS_CHAT_EDGE_PATH = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'
$env:ATLAS_CHAT_TEST_OUTPUT = 'artifacts/chat-dom-ci'
node source/WotLK.Launcher.IntegrationTests/Frontend/chat-dom-tests.cjs
```

La suite DOM écrit les captures PNG et `results.json` dans le répertoire de sortie. Toutes les captures utilisent le viewport Messages mesuré dans la shell V2 fixe : **1597 × 872 pixels CSS**, sous un en-tête de 124 DIP ; aucun redimensionnement n’est effectué. Le relevé conserve le viewport et les empreintes des assets testés, y compris `chat-search.js`. Ces fichiers générés, profils et dépendances ne font pas partie des sources de tests.

Les assertions couvrent notamment les statuts et avatars, les images sans cadre, les suppressions, le sélecteur Armory, les brouillons, les pièces jointes, les groupes, les médias, les accusés de lecture et le défilement. Elles vérifient également le signal `composerState` transmis au natif lors de l’entrée et de la sortie d’édition, des changements de fil ou de session et de la révocation du droit d’écrire. Le nombre exact d’assertions du dernier passage figure dans `results.json`.

La finition premium vérifie l’absence des filtres et de la recherche dans la colonne Conversations, le maintien des badges non lus et de la recherche dans Nouvelle conversation, l’envoi par icône seule et ses libellés accessibles FR/EN, l’alignement des commandes à droite, leur ordre clavier et la saisie multiligne.

La suite de finition vérifie également l’absence du bandeau Messages, les aperçus image/vidéo/audio seuls avant l’envoi, les erreurs sur la pièce jointe ou le message concernés et la suppression ciblée des envois refusés. Une tentative dont la réponse serveur a été perdue ne peut pas utiliser l’annulation ordinaire. Quand le natif expose `canDelete`, sa suppression passe par `deleteFailedSend` avec son seul identifiant ; l’état de suppression masque toute action de renvoi ou d’annulation. Une confirmation serveur remplace l’entrée locale sans doublon. Les erreurs tardives ne passent pas dans une autre conversation. Les erreurs antérieures à la file native conservent le brouillon et le même identifiant lors d’un nouvel essai.

Les médias de test sont synthétiques et intégrés aux fixtures : image SVG, une seconde de silence WAV et une courte vidéo VP8/WebM enregistrée localement. Le décodage des métadonnées est vérifié dans le navigateur ; cela ne certifie pas la prise en charge de tous les codecs pouvant se trouver dans chaque extension acceptée. La lightbox est contrôlée sans boutons ni cadre, avec fermeture par Échap ou fond et restauration du focus. Les cartes de personnage utilisent le niveau et la classe fournis, avec le GUID exact réservé à l’action Armory.

Les captures préservent la transparence du document. La vérification CSS ne démontre pas à elle seule la continuité du fond Citadelle : sa composition avec l’en-tête est vérifiée séparément dans la shell WPF complète. Le pont étant simulé, cette suite ne remplace pas un dépôt Explorer dans le launcher réel ou une connexion à deux comptes réels.

## Vérification WPF isolée

`--chat-rich-host-wpf` vérifie le pont natif, les changements de présence, les restrictions de session et le routage FileDrop. `--chat-full-shell-wpf` utilise la véritable shell fixe et WebView2, des données fictives et un résolveur de médias local. La fenêtre reste inactive, hors écran, sans runtime du launcher ni accès au backend. Ses captures directes `RenderTargetBitmap` incluent la WebView, l’en-tête natif, le fond Citadelle unique et le panneau de profil. Les pixels transparents de la marge supérieure sont comparés au fond natif après suppression du bandeau.

La suite complète vérifie aussi le rendu de Ctrl+F en français et en anglais, ses résultats et la restauration du brouillon. Pour exercer la branche clavier, elle injecte un état de conversation active dans le DOM de test ; la fenêtre native reste inactive. `--friends-right-click` vérifie les actions et la liste compacte, le filtre, les groupes et les propriétés d’accessibilité sur des contrôles WPF sans créer de fenêtre native. Ces scénarios isolés ne constituent pas une validation manuelle dans une session utilisateur.

```powershell
dotnet build source/WotLK.Launcher.IntegrationTests/WotLK.Launcher.IntegrationTests.csproj -c Release -p:AtlasLocalClientBuild=true --artifacts-path artifacts/atlas-chat-followup-20260907/build
$fixture = 'artifacts/atlas-chat-followup-20260907/build/bin/WotLK.Launcher.IntegrationTests/release/WotLK.Launcher.IntegrationTests.exe'
& $fixture --chat-rich-host-wpf --capture-directory artifacts/atlas-chat-followup-20260907/rich-host
& $fixture --chat-full-shell-wpf --capture-directory artifacts/atlas-chat-followup-20260907/full-shell
```
