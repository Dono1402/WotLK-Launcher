# Portage HD : prototype humain homme, Classic 3.4.3.54261

## Résultat

**Un prototype de conversion hors client a été produit et vérifié. Ce n'est pas
un pack installable ni un portage jouable validé.** Le pilote concerne seulement
l'humain homme Leeviathan ; les autres races, l'équipement rapporté, les PNJ et
les environnements ne sont pas convertis dans ce jalon.

Cette étape fait suite à la [préparation initiale](WOTLK-HD-MODELS-PREPARATION-2026-09-09.md)
et à l'accord de l'utilisateur pour un prototype isolé. Aucun jeu, launcher ou
interface graphique n'a été ouvert. Aucun fichier du client installé, addon,
feed, HermesProxy ou worldserver n'a été modifié. Le hash de `WowClassic.exe`
est identique à celui relevé au début de l'étude.

Les assets sont conservés uniquement dans le dossier de préparation local
`.codex-stage/hd-models-20260909/human-male-classic-pilot-v2`, à côté du dépôt,
et ne sont pas redistribués sur GitHub. Ce dossier contient le manifeste complet
`pilot-manifest.json` et l'inventaire `legacy-customization-inventory.json`.
Les sources tierces et DLL sont également hors dépôt. Aucun fichier existant
n'est remplacé ; les scripts imposent une sortie distincte et refusent l'écrasement.

## Preuves et transformation réalisée

Le ZIP de 2 934 500 949 octets a cette fois été téléchargé entièrement et le test
7-Zip des huit entrées a réussi. Seul le MPQ de base a été extrait ; les variantes
optionnelles ne sont pas utilisées. Le MPQ contient 40 711 entrées, dont 6 188 M2,
6 256 SKIN, 1 135 animations, 27 125 BLP et sept DBC.

| Élément | Humain homme HD source | Référence réelle du client | Prototype |
| --- | --- | --- | --- |
| Conteneur/version M2 | MD20 / 264 | MD21, MD20 interne / 274 | MD21, MD20 interne / 274 |
| Sommets | 16 271 | 5 264 | 16 271, données inchangées |
| Os | 228 | 138 | 228 |
| Séquences d'animation | 241 | 156 | 241 |
| Caméras | 2, structures de 100 octets | 2, structures de 116 octets | 2, structures de 116 octets |
| Géométries SKIN | 4, en-tête 48 octets | 1, en-tête 64 octets | 4, en-tête 64 octets |

Le [convertisseur](../scripts/wotlk-hd-models/build_human_pilot.py) :

- conserve les sommets, poids, UV, os et données d'animation ;
- ajoute une nouvelle table de caméras et des pistes de champ de vision constant,
  sans déplacer les données déjà référencées par les offsets M2 ;
- agrandit les en-têtes SKIN et reloge leurs cinq tableaux sans changer leurs
  indices, triangles, sous-maillages ou lots de rendu ;
- ajoute les correspondances SFID, TXID et AFID issues de la liste vérifiée,
  sans inventer d'identifiants ;
- enveloppe les 53 animations externes brutes dans des chunks AFM2 et conserve
  leurs payloads à l'identique ; le comportement de ce choix dans le moteur
  reste expérimental ;
- inventorie 615 lignes de personnalisation de l'ancien CharSections et extrait
  leurs ressources, sans fabriquer de faux DB2 Classic.

Résultat : **847 assets, 158 090 638 octets** hors manifestes : un M2, quatre SKIN,
53 animations et 789 textures BLP. Les géométries contiennent chacune 20 435
triangles ; leurs nombres différents d'indices et de sous-maillages sont
conservés. Les textures sont présentes, mais leur raccordement à la
personnalisation Classic n'est pas achevé.

## Vérifications exécutées

- 25 nouveaux tests synthétiques réussis : conversion de caméra, offsets,
  animations externes, SKIN, DBC, chemins dangereux, absence d'écrasement,
  correspondances d'identifiants et diagnostic PE.
- 20 tests existants de lecture ZIP distante toujours réussis.
- Validation des tableaux M2/SKIN et de **94 039 tableaux de pistes non vides**
  avant conversion, 94 043 après ajout des deux pistes de champ de vision.
  Ce sont des contrôles de bornes, pas une simulation du moteur d'animation.
- Géométrie SKIN et bloc sommets/poids/UV comparés octet par octet avant/après.
- Deux générations séparées donnent les mêmes empreintes pour les 847 assets.
- Le [vérificateur indépendant](../scripts/wotlk-hd-models/PilotFormatVerifier/Program.cs),
  utilisant WoWFormatLib, relit le M2 et ses quatre SKIN, retrouve les comptes
  attendus et revérifie les 847 empreintes. Il lit aussi le M2 natif extrait du
  client : version 274, 5 264 sommets, 138 os, deux caméras.
- Les deux petits outils .NET compilent sans erreur ni avertissement. La
  bibliothèque CASC amont a un avertissement d'API WebRequest obsolète ; ses
  chemins de repli réseau ont été désactivés pour cette lecture locale.
- Sept références CASC sont relues avec les mêmes empreintes : HumanMale M2 et
  SKIN, CharSections, CharHairGeosets, CharacterFacialHairStyles, ChrModel et
  ChrModelMaterial. Les tables du client sont des WDC4, pas des DBC 3.3.5.

## Ce qui empêche encore une installation

1. **Chargeur exact non validé.** L'ancienne branche open source avec
   `CUSTOM_FILES` a été compilée en dossier isolé, jamais exécutée. Les trois
   signatures de chargement recherchées sont absentes du PE sur disque. L'amont
   attend cependant le décompactage du programme en mémoire : cette absence ne
   démontre pas une incompatibilité, mais ne valide aucun hook pour 54261.
2. **Personnalisation DB2 à adapter.** Classic utilise des ressources de matériau,
   pas les trois chemins de texture de l'ancien CharSections. Quinze textures
   `HumanMaleSkin00_*_Extra.blp` n'ont aucun FileDataID dans la liste utilisée.
   Cela ne prouve pas leur absence dans tout autre jeu de données. Aucun ID
   arbitraire et aucun remplacement de DB2 installé n'a été effectué.
3. **Rendu et animations à tester.** Le bit `0x200000` observé sur le M2 Classic
   est ajouté au candidat avec les chunks d'animation. Son interprétation
   effective et les alias/transitions d'animation ne sont pas validés en jeu.
   Les nouveaux tableaux de shadow batches sont vides ; ombres, sélection de
   géométrie et comportement LOD restent à compléter/valider.
4. **Couverture limitée.** Les géosets de personnalisation, casques, épaulières,
   armures, montures, portraits, création/sélection du personnage et PNJ doivent
   être testés. La réussite d'un parseur ne vérifie pas ces comportements.

Il n'existe donc ni installateur, ni configuration de remplacement du client,
ni commande lançant un chargeur expérimental. La prochaine étape hors jeu est
le raccordement des tables de personnalisation et des matériaux. Une future
modification du client utilisé ou un lancement du jeu nécessitera un accord
explicite. Aucun retour arrière côté client n'est nécessaire à ce stade.

## Reproduction hors client

Les tests synthétiques ne demandent aucune dépendance tierce :

```powershell
python -I -B scripts/wotlk-hd-models/test_human_pilot.py -v
python -I -B scripts/wotlk-hd-models/test_inspect_remote_zip.py -v
```

La conversion nécessite Python 3.11+, le MPQ source, la DLL StormLib officielle
x64 v9.40 et le listfile du 8 septembre 2026. Le modèle, CharSections, le listfile
et la DLL attendus sont verrouillés par SHA-256. Préparer soi-même un dossier
de sortie **vide et distinct du client** ; ne pas utiliser son dossier `Data`.

```powershell
python -I -B scripts/wotlk-hd-models/build_human_pilot.py `
  --stormlib 'C:\staging\stormlib-v9.40\x64\StormLib.dll' `
  --archive 'C:\staging\leeviathan-base\patch-H.MPQ' `
  --listfile 'C:\staging\community-listfile-202609081819.csv' `
  --output-dir 'C:\staging\nouveau-pilote-vide'
```

Le script ne télécharge rien. Il lit et valide les ressources avant la première
écriture, n'exécute aucun asset et ne copie aucun DBC dans le pilote. Un échec
d'écriture peut laisser une sortie partielle : ne jamais la considérer comme
complète sans manifeste et relecture réussie.

### Dépendances des diagnostics facultatifs

- [StormLib v9.40](https://github.com/ladislav-zezula/StormLib/releases/tag/v9.40) :
  DLL x64 officielle fournie explicitement à l'outil MPQ, chargée seulement après
  contrôle de son empreinte. Elle n'est pas redistribuée ici.
- [CascLib](https://github.com/WoW-Tools/CascLib/tree/3f8be478177802de4ae7ebae24fb25ab860ef104) :
  appliquer [le patch local-only](../scripts/wotlk-hd-models/casclib-local-only.patch)
  à une copie séparée de cette révision, puis compiler pour net8.0 avec
  `-p:TargetFrameworks=net8.0 -p:GeneratePackageOnBuild=false`.
  Le patch corrige deux replis CDN et cinq ouvertures de configuration pour
  qu'elles soient strictement en lecture. `CascPilotReader` exige l'empreinte
  de la DLL auditée, pas seulement le booléen de configuration amont.
  Une recompilation donnant un autre hash exige une nouvelle revue ; ne pas
  supprimer ce contrôle pour faire accepter une DLL inconnue.
- [WoWFormatLib](https://github.com/Marlamin/WoWFormatLib/tree/3c601f15d9737dde99d38827fc90ff6e800eb097),
  avec [TACTSharp](https://github.com/wowdev/TACTSharp/tree/cc5bf85170cf87e0e8ea5d44edfac986ac647877)
  en dossier frère : compilation .NET 10. La dépendance transitive
  `Microsoft.Build.Tasks.Git` 8.0.0 signalée par NuGet a été remplacée localement
  par une référence explicite privée à 10.0.401 ; compilation sans avertissement.
- [Arctium open source](https://github.com/NumboWoW/WoW-Launcher/tree/3deaa3f50b95ae918ba49ca2a4d9a895247e67f7) :
  configuration `ReleaseCustomFiles`, plateforme x64, SDK 8.0.424,
  `PublishAot=false`. La référence flottante System.CommandLine a été fixée à
  `2.0.0-beta4.22272.1`. Cette compilation constitue une preuve de disponibilité
  du code, pas une procédure d'exécution ni une preuve de compatibilité.
- [Community listfile 202609081819](https://github.com/wowdev/wow-listfile/releases/tag/202609081819)
  et [WoWDBDefs](https://github.com/wowdev/WoWDBDefs/tree/d0cc6370847f0c6ae3bfb970ad9520be253276f5)
  servent aux correspondances et à la comparaison des schémas.

Les projets de diagnostic acceptent respectivement `-p:CascLibAssembly=...` et
`-p:WoWFormatLibAssembly=...`. Utiliser `BaseOutputPath` et
`BaseIntermediateOutputPath` vers le staging pour garder les binaires hors Git.
Le lecteur CASC reçoit un stockage local 3.4.3.54261, une sortie séparée existante
et des FileDataID séparés par virgules. Le vérificateur reçoit le chemin du
manifeste pilote puis, facultativement, celui du M2 stock déjà extrait.

Ces outils ne sont pas des composants livrés dans le launcher WPF et ne changent
pas son processus de démarrage. Les droits de redistribution des dépendances
et assets doivent être revus séparément avant toute livraison à d'autres joueurs.

## Empreintes principales SHA-256

```text
ZIP Leeviathan      a67ec7c8daa64ec8caad4f60b84f4ed51daaf5aa5219ed8cb3bab0855b4965a5
patch-H.MPQ         1c678ba18545041d4a8599b3baa1c2af3500cc0f17e1e278df111bb455450d98
StormLib x64 DLL    93321f6f030be5d7d79eb4cbf433d6ef7e83ce20d47d8963717f207d54afbe16
HumanMale source    b059ed5972a90664a751d0fb6e4375117b2f8ff5959de218742f6b0303e01682
HumanMale prototype e1429a34c96885eafb34817aabe2fa256eb8ca52037b76166d517565b78defae
HumanMale stock     2c1790308f015f26ef5c2745e66e86d8469a1d4755adbfa41ee0f1f53338ab5e
CascLib local-only  abb8b4496195fe8629ea9c86e6e3572f3dfec231d37d8521b53a52732f299b29
WowClassic.exe      e240f9d88445643d2545ac54b3c3874b2f86336bc8947256584ef07bb5fd66a7
Community listfile  fdb3ddea306322f33b65416abf2e889e1b7bddc2d62738b84c66fe9f16398225
```
