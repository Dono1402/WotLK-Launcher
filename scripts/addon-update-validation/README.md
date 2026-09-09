# Vérifications hors jeu des addons préparés

Ces scripts reproduisent les contrôles du 9 septembre 2026. Ils n'installent
aucun addon, n'ouvrent pas le jeu et n'accèdent pas aux SavedVariables. Le
répertoire de référence AddOns est lu uniquement ; toutes les sorties JSON
restent dans le répertoire de préparation explicitement passé en argument.
Les deux répertoires doivent être distincts et ne pas être imbriqués.
Utiliser un stage de confiance sans symlinks, jonctions ou hardlinks ajoutés.
Les rapports JSON Node sont écrits par remplacement atomique d'un fichier
temporaire neuf ; les fichiers de rapport liés préexistants sont refusés.

## Entrées figées

Voir le [manifeste des paquets](../../docs/update-preparation/2026-09-09/addons-manifest.json)
pour les URLs officielles, versions, empreintes SHA256 et résultats.
Les archives/extractions et données de test générées ne sont pas versionnées.

Le répertoire de préparation doit contenir les archives préalablement
contrôlées dans `downloads/`, sous les noms du manifeste versionné, et les
extractions :

- `extracted/weakauras-legacy-5.12.9/` ;
- `extracted/weakauras-5.13.1/` ;
- `extracted/auctionator-10.2.24-wrath/`.

La référence de comparaison est l'installation auditée : WeakAuras 5.12.8,
Auctionator 10.2.0-wrath, ElvUI avec le correctif d'inspection, SimpleDungeonMap
et `.atlas-addons.json`. Une autre référence peut légitimement faire échouer
les assertions de comparaison. Aucun profil de joueur n'est nécessaire.

## Exécution

Installer les dépendances épinglées dans ce dossier avec
`pnpm install --frozen-lockfile --ignore-scripts`, puis exécuter :

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File verify-extractions.ps1 -StageRoot <prepared-stage>
node validate-addons.cjs <prepared-stage> <baseline-AddOns>
powershell -NoProfile -ExecutionPolicy Bypass -File validate-xml.ps1 -StageRoot <prepared-stage>
node run-offline-tests.cjs <prepared-stage> <baseline-AddOns>
```

L'option `ExecutionPolicy` de ces deux commandes s'applique seulement au
processus lancé pour le script local relu ; aucune stratégie Windows globale
n'est modifiée. Les scripts restent compatibles avec Windows PowerShell 5.1.

`verify-extractions.ps1` vérifie l'empreinte de chaque ZIP contre le manifeste
versionné, puis compare tous les fichiers extraits aux entrées de l'archive
par taille et SHA256. Il refuse les fichiers supplémentaires, chemins sortants
et points de jonction/symlinks. Cette étape ne crée ni ne modifie aucun fichier.

Le dernier script utilise les snippets XML de `xml-validation.json`. Les
tests Lua s'exécutent dans une VM Fengari avec mocks synthétiques ; `io`,
`os`, `package` et `debug` ne sont jamais ouverts, `require` est absent,
et les fonctions de base `dofile` et `loadfile` sont retirées. Le seul
`loadfile` réintroduit pour LibStub accepte le nom littéral de sa source
déjà fournie en mémoire. Cela ne remplace pas un bac à sable système et les
scripts visent uniquement les paquets vérifiés ci-dessus.
Les tests LibStub reçoivent aussi un mock inerte de `debug.traceback`, seulement
pour leur affectation `debugstack` inutilisée ; aucun accès au registre Lua
n'est exposé.

## Limites

`luaparse` 0.3.1 refuse certains `break;` en mode Lua 5.1. Le contrôle masque
uniquement ces points-virgules, délimités par le lexer, en mémoire. Les sources
originales restent intactes et sont compilées séparément par Fengari (Lua
5.3). Les avertissements bruts et les écarts sont conservés dans le rapport.

Les tests couvrent 25 assertions synthétiques de migration/changement de
spécialisation et 8 fichiers de tests LibStub (104 assertions), pas le client
WoW, les requêtes de l'hôtel des ventes ou un parcours de jeu. L'extraction
XML interdit les DTD et les résolveurs externes.
