# Optimisations du launcher — 8 septembre 2026

Périmètre : les sept améliorations retenues. La recherche dans les paramètres est exclue.

## Comportement livré

1. Les retours entre pages conservent le défilement. La sélection d’un addon est conservée après navigation et actualisation du catalogue ; les panneaux temporaires continuent de se fermer.
2. La lecture, l’entretien et l’invalidation du cache des profils amis passent dans une file de travail en arrière-plan. Les invalidations attendent l’arrêt du helper concerné, puis précèdent les lectures suivantes. L’attente d’arrêt du processus ne bloque plus le thread WPF ; la fermeture de l’application attend son nettoyage.
3. Le cache des avatars conserve au maximum 256 images et 16 Mio de pixels décodés, avec éviction des images les moins récemment utilisées. Cette limite concerne les références du cache, pas celles des images encore affichées. La traduction libère ses abonnements aux anciens éléments visuels et conserve leurs textes sources dans une table à clés faibles.
4. L’actualisation automatique des amis passe de 15 à 60 secondes lorsque la fenêtre est inactive, réduite ou masquée. Le retour au premier plan déclenche une actualisation. La présence et la réception des notifications de messages gardent leur fonctionnement existant.
5. Les amis, leurs demandes et leurs en-têtes partagent une liste virtualisée ; le filtre et les groupes repliables restent utilisables. Les notes de version recyclent également leurs lignes, sous un titre de page fixe. Les données restent disponibles pendant que seules les lignes voisines du viewport sont créées.
6. Les réglages sont écrits dans un fichier temporaire vidé sur disque, puis remplacés atomiquement. Une sauvegarde valide permet de récupérer un fichier absent ou illisible, avec un message dans les paramètres. Lors de la prochaine écriture, les octets corrompus sont conservés séparément. Deux fichiers invalides provoquent une erreur explicite, sans réinitialisation silencieuse. Messages et Armory proposent une recréation de leur navigateur après incident ; les snapshots de session et l’état métier restent détenus par les composants natifs.
7. Le menu de profil affiche d’abord le statut courant. Les quatre choix se déplient au clic, puis se replient après choix, Échap ou fermeture du menu. Les actions utilisent « Désinstaller », « Retirer cet ami » et « Tout désélectionner », avec traductions anglaises.

## Vérifications

Compilation de l’application et des tests : zéro avertissement, zéro erreur.

| Suite | Vérification |
| --- | --- |
| `--optimization-memory` | Plafonds du cache, LRU, publication tardive après destruction, collecte des anciens éléments traduits, aller-retour FR/EN. |
| `--optimization-storage` | Sauvegarde, corruption, fichier primaire absent, verrouillage empêchant le remplacement, conservation de l’original et ordre des invalidations. |
| `--optimization-ui` | 600 notes, 500 addons et 1 000 amis ; moins de 30 lignes de notes et 100 lignes d’amis créées ; accès au dernier élément, défilement et sélection conservés. |
| `--shell-navigation-wpf` | 778 assertions : matrice des pages et panneaux, souris/clavier synthétiques, focus, modales et conservation de la navigation sur les grandes listes. |
| `--friends-runtime` | Cadence 15/60 secondes, retour au premier plan, absence de timers supplémentaires et arrêt définitif. |
| `--friends-right-click` | 41 assertions : actions, filtre, groupes et traductions, sans fenêtre native. |
| `--presence-profile-wpf` | 988 assertions : menu compact, quatre statuts, enregistrement, erreurs et dimensions FR/EN. |
| `--auth-shell-wpf` | 174 assertions : authentification, déconnexion et limites des panneaux modaux. |
| `--settings-runtime` | Commandes des paramètres et conservation des valeurs lors d’un échec. |
| `--friend-cache`, `--armory-session` | Isolation des comptes, révocation, annulation, réponses tardives et règles d’invalidation. |
| `--friend-profile-wpf` | Entretien asynchrone du cache, véritable helper RPC de test, profils en lecture seule, avatars et changement de session. |
| `--chat-rich-host-wpf` | 204 assertions : véritable WebView2, pont natif et isolation ; crash du renderer, nouvelle vue, conversation et brouillon enregistré restaurés sans rejeu d’action. |
| `--armory-shared-character-wpf` | Échec initial et crash du renderer ; arrêt de l’ancien helper et restauration du personnage partagé exact après relance. |

Les tests graphiques utilisent des fixtures sans fenêtre native ou des fenêtres inactives hors écran avec interdiction d’activation. Les données et comptes sont synthétiques ; aucun service de production ni client de jeu n’est utilisé. Le cas de lien symbolique de `--friend-cache` est ignoré lorsque Windows refuse sa création.

La récupération de Messages restaure le brouillon déjà transmis au workspace. Le texte qui n’avait pas encore quitté le renderer au moment de son crash n’est pas garanti ; la saisie utilise déjà une temporisation de sauvegarde de 250 ms.

## Reproduire

Sur Windows avec le SDK .NET 8, depuis la racine du dépôt :

```powershell
dotnet build source/WotLK.Launcher.IntegrationTests/WotLK.Launcher.IntegrationTests.csproj -p:AtlasLocalClientBuild=true -p:NuGetAudit=false
$testDll = 'source/WotLK.Launcher.IntegrationTests/bin/Debug/net8.0-windows10.0.17763.0/WotLK.Launcher.IntegrationTests.dll'
dotnet $testDll --optimization-storage
dotnet $testDll --optimization-memory
dotnet $testDll --optimization-ui
dotnet $testDll --shell-navigation-wpf
```
