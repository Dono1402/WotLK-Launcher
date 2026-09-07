# Armurerie locale du launcher

La page Profil du launcher WPF héberge maintenant le viewer Three.js dans WebView2.
Les personnages du compte connecté apparaissent à gauche, l'équipement et le modèle
sélectionnés au centre et les statistiques à droite. Le bouton « Personnaliser »
ouvre les réglages de profil existants. La langue française ou anglaise suit celle
du launcher, y compris lors d'un changement pendant la consultation.

## Intégration locale

`LauncherArmoryLocalHost` lance `launcher-server.cjs` sur un port éphémère de
`127.0.0.1`. L'identifiant de compte provient de la session native authentifiée.
Le serveur vérifie un secret aléatoire transmis uniquement dans les requêtes de
la WebView, et `launcher-roster.cjs` filtre les personnages par ce compte dans une
transaction SQL en lecture seule. Les caches sont séparés par compte. Cette page
ne dépend pas d'une liste prédéfinie de noms de personnages.

Le navigateur relit le résultat local toutes les cinq secondes lorsqu'il est
visible ; le processus local actualise les données serveur en arrière-plan.
Une liste déjà reçue reste consultable en cas de panne, avec un état de cache et
un bouton pour réessayer. Ce bouton déclenche une nouvelle lecture serveur et
affiche « Actualisation… » pendant son exécution ; la lecture périodique de la page
consulte seulement le cache local. Un personnage supprimé de la liste disparaît également
de l'affichage. Une navigation vers une autre page conserve la session WebView du
profil. À la fermeture du launcher ou à la déconnexion, le launcher ferme la WebView
et son processus local, afin de libérer les données du compte précédent.

Tous les personnages sont listés, indépendamment de leur disponibilité 3D.
`launcher-models.cjs` réutilise un export compatible ou génère le modèle depuis le
client installé lorsque `clientRoot` est configuré. Pendant cette préparation,
les personnages conservent leur équipement et leurs statistiques avec un message
explicite. Les données absentes sont signalées,
et les descriptions d'objets incomplètes restent marquées comme telles.
Les icônes d'équipement sont récupérées indépendamment du modèle 3D ; une icône
absente conserve un pictogramme d'emplacement et une infobulle consultable.
Le cache distingue l'identité, l'apparence, l'équipement et la version du moteur
d'export. Une simple mise à jour des statistiques réutilise le modèle. Un cache
ancien ou auquel il manque une ressource est invalidé, y compris hors ligne.
Les variantes dont les ressources locales ou les correspondances de squelette ne
sont pas disponibles restent explicitement indisponibles.

Depuis la racine du dépôt :

```powershell
./scripts/build-local-client.ps1
```

Ce script produit `artifacts/AtlasLauncherLocal/AtlasLauncherLocal.exe` et
`armory-local.json`, qui référence les chemins locaux du dépôt et du runtime Node.
Il faut aussi les dépendances du prototype, le runtime WebView2 et la configuration
privée `artifacts/armory-prototype/statistics-sync.json` décrite plus bas.
Cette version utilise une lecture SSH locale de prototype : elle n'est pas un
mécanisme à distribuer aux joueurs. Aucune publication ni installation en production
ne fait partie de cette intégration.

`node verify-launcher.cjs` démarre un serveur temporaire avec des fixtures, sans SSH,
puis le ferme après les tests. Les huit affichages FR/EN à 1228 × 794, 1068 × 654,
868 × 614 et 808 × 554 pixels vérifient le rendu WebGL, la disposition sans débordement de
document, la sélection de trois personnages, les états sans modèle ou en attente,
les infobulles, la recherche, Personnaliser, le changement de langue, le cache,
la disparition d'un personnage et le retrait explicite de statistiques.
La colonne de statistiques peut défiler seule quand la hauteur disponible est faible.
Les captures `launcher-*.png` et `launcher-verification.json`, dans les artefacts,
contiennent des données de test et ne représentent pas plusieurs personnages réels.

La validation native WPF a également réussi avec les scénarios `--armory-launcher`,
`--profile-logout` et `--account-preview`. La compilation des tests utilise :

```powershell
dotnet build source/WotLK.Launcher.IntegrationTests -p:AtlasLocalClientBuild=true
```

La cible locale est `net8.0-windows10.0.17763.0` ; la dépendance au SDK Windows est
nécessaire pour les API Composition utilisées par WebView2.

Le contrôle réel du 5 septembre 2026 avec Flowmage niveau 23 valide ses 14 objets
et son modèle (`modelReady: true`, `renderSchemaVersion: 3`), dont le casque
Hooded Cowl 3732. Les groupes de cheveux masqués suivent les données du client.
Un bouton « Mêlée / À distance » choisit entre le bâton et la baguette, avec la
fermeture des doigts de la main occupée. Le zoom, l'orientation et l'infobulle
épinglée sont conservés lors du changement de mode.

Les attaches de tête, épaules, main droite et main gauche sont issues du modèle
M2. Le bouclier utilise le point 0 ; l'arme ou l'objet tenu à gauche et l'arc
utilisent le point 2. Les pièces d'armure composées sur le corps gardent leurs
textures et leurs géosets. Les objets sans représentation portée, comme les
bagues, restent visibles dans leur emplacement et leur infobulle.
Les matériaux conservent leur mode alpha natif : les pièces opaques ne sont plus
découpées à tort lorsque leur canal alpha décrit des reflets. Les cheveux et
autres parties découpées gardent leur seuil de transparence propre.

`verify-equipment.cjs` construit des configurations locales à partir des objets
du client installé : casque présent ou absent, deux dagues, bouclier, lanterne,
arc, fusil et arbalète. Il vérifie les attaches natives, les modes d'armes, le
cadrage, les masques du casque et les vues de face et de dos. Ces configurations
ne modifient pas l'équipement du compte. Les résultats et captures sont conservés
sous `artifacts/armory-prototype/equipment-verification/`.
Une tenue en plaques teste aussi les anciennes références de modèles présentes
dans certains objets de torse, bottes et gants : lorsque le masque natif ne
sélectionne aucune géométrie, elles restent invisibles et ne bloquent plus le
modèle. Leur texture portée sur le corps est conservée. Les références écartées
sont distinguées des collections visibles dans `hiddenModelReferences`.

Les options d'apparence héritées sont normalisées selon les identifiants des races
du client. Deux sondes supplémentaires, Humain femme et Tauren homme, valident
la sélection de cinq paramètres et le port des 14 objets, casque inclus.
La correction des matériaux restaure aussi la queue du Tauren. De fines bandes
sombres restent visibles dans sa crinière au-dessus de la cape : cette limite
de fidélité visuelle n'est pas considérée comme corrigée.
Ces sondes locales ne représentent pas d'autres personnages du compte.

Le contrôle `launcher-current-model-verification.json` concerne le véritable
relevé : 32 ressources locales accessibles par HTTP, aucune erreur navigateur,
et aucun nouvel export lors de la seconde actualisation à équipement identique.
`equipment-probes/native-alpha-verification.json` complète le contrôle avec
46 matériaux sur trois modèles : plate v3, Tauren v3 et ancien export v2.
Les modèles opaques restent complets, les découpes de cheveux sont conservées
et les anciens exports gardent leur comportement précédent.

## Historique de la preuve de faisabilité Flowmage

Les sections suivantes conservent les relevés, validations et limites historiques
du viewer autonome et de son collecteur. Leurs dates et nombres d'objets décrivent
ces relevés, pas l'état actuel du personnage ni les validations WPF de l'intégration.

### Premier résultat

- Flowmage : elfe de sang, personnage féminin, mage niveau 22 sur Arthas.
- Apparence issue du relevé serveur : peau 0, visage 3, coiffure 0, couleur 1, bijoux 4.
- 13 objets équipés, leurs identifiants, raretés, niveaux d'objet et icônes du client.
- Noms et caractéristiques en français et en anglais : armure, attributs, bonus
  aléatoires de l'instance équipée, effets vérifiés, dégâts, vitesse et DPS des armes.
- Modèle réel `BloodElfFemale`, FileDataID 116921, avec robe, cape, épaulières et bâton.
- Animation Stand avec pose HandsClosed de la main droite, rotation, zoom, pause,
  recentrage, plein écran et sélection d'un objet.
- Captures et tests dans `artifacts/armory-prototype/` (ignoré par Git).

## Interface

Fond uniforme avec scène 3D sans cadre, colonnes d'armure rapprochées et armes alignées
en dessous. Les emplacements vides ne conservent qu'une icône avec un libellé accessible.
La vue PC s'adapte à la hauteur de la fenêtre, sans bandeau technique ni défilement
aux dimensions vérifiées. Le périmètre produit est le launcher PC, adapté aux tailles
de fenêtre Windows. Aucune version mobile n'est prévue.

Une colonne fixe à droite présente le niveau d'objet moyen et les statistiques du
personnage. Elle ne change pas à la sélection d'un objet : les caractéristiques de
celui-ci restent dans son infobulle. Aucun bloc « Dernier changement » n'est affiché.
Le niveau moyen est la moyenne arithmétique des pièces actuellement portées, hors
chemise et tabard, emplacements vides ignorés : 272 / 12 = 22,7 pour ce relevé.
Cette définition est précisée au survol ; ce n'est pas le score serveur d'accès aux donjons.

Les totaux du personnage ne sont pas déduits des seules statistiques des objets.
Le premier contrôle du 5 septembre 2026 n'avait trouvé aucune ligne pour Flowmage
dans `arthas_chars.character_stats`. Après activation autorisée et déconnexion du
personnage à 06:30:37 UTC, un relevé réel est disponible : Intelligence 107,
Endurance 50, Esprit 63, Armure 468. Il correspond au niveau, à l'apparence et à
l'équipement de l'export 3D. Ce relevé natif ne contient pas les totaux de combat
complets ; ceux-ci ont ensuite été importés depuis le module complémentaire à
07:28:32.094 UTC. Une valeur absente reste indiquée par un tiret, avec un état
« Données serveur incomplètes », jamais par un zéro ou un pourcentage inventé.
La table serveur documentée est
[character_stats](https://www.azerothcore.org/wiki/character_stats) ; sa collecte
dépend de `PlayerSave.Stats.*`. La première écriture réelle confirme que le serveur
a pris en compte le réglage, sans redémarrage de son processus.

Le viewer accepte facultativement `character.json.statistics` avec `capturedAt`
identique au relevé et `values` contenant les totaux numériques. Le cache natif utilise
`characterCapturedAt` pour désigner le relevé compatible, et conserve séparément
`savedAt` (dernière déconnexion) et `observedAt` (lecture SQL). Pour le mage :
`intellect`, `stamina`, `spirit`, `spellPower`, `spellCritPct`, `spellHitPct`,
`spellHastePct` et `armor`. Les champs `Pct` sont déjà des pourcentages serveur,
pas des scores convertis côté navigateur. Une date différente est refusée afin de
ne pas associer des totaux périmés à un autre équipement. Aucun total de test n'est
écrit dans les assets. Les dix classes de WotLK disposent maintenant de vues adaptées :
mêlée, distance, magie, soins ou défense. Les classes hybrides proposent plusieurs
catégories avec sélection manuelle ; les points dans les arbres de talents actifs
et la forme du druide fournissent le choix initial lorsqu'un relevé complet est présent.
Un chevalier de la mort n'est pas supposé tank selon son arbre seul. Le blocage
n'est proposé qu'au guerrier et au paladin ; le druide ne présente pas de parade.
Les données manquantes restent indisponibles quelle que soit la classe.

## Collecte Des Statistiques

`statistics-readonly.sql` lit Flowmage, son apparence, ses objets équipés et les valeurs
natives sauvegardées en une transaction READ ONLY avec ROLLBACK. Le script
`capture-statistics.cjs` nécessite une destination SSH explicite, une clé locale
existante et la date vérifiée d'activation de la collecte. Il ne modifie ni Arthas,
ni les fichiers du jeu, ni le relevé d'équipement précédent.

Avant de publier le cache local, il exige un personnage déconnecté après cette date,
le même niveau et la même apparence, et les mêmes pièces, propriétés aléatoires et
enchantements que dans le relevé 3D. Si l'équipement a changé, il faut d'abord
rafraîchir l'export complet ; la boucle locale le fait maintenant automatiquement
pour les objets pris en charge lorsque `clientRoot` est configuré.
L'absence de données n'écrase pas un ancien cache.
Les fichiers sont publiés par remplacement atomique ; aucun GUID ou secret ne figure
dans le résultat public. Le point d'accès `/statistics.json` filtre les champs du
cache, refuse les écritures HTTP et les hôtes externes. Le fichier brut n'est pas servi.

Le viewer lit ce cache local en arrière-plan, sans attendre sa réponse pour charger
la scène. Il le relit toutes les cinq secondes pendant que la page est visible,
ainsi qu'au retour sur la fenêtre, sans recharger la 3D ni fermer une infobulle.
Une boucle locale distincte récupère maintenant le dernier relevé serveur toutes
les 60 secondes lorsqu'elle est explicitement configurée. Le relevé du moteur
reste produit à la déconnexion, pas pendant la session de jeu.

Seuls les cinq attributs, l'armure, les points de vie maximum et le mana maximum natifs
sont acceptés. Pour la colonne du mage, cela remplit quatre lignes. Le toucher et la
hâte ne figurent pas dans `character_stats` ; `spellPower` y provient de
`GetBaseSpellPowerBonus()` et `spellCritPct` d'un seul champ de la série de critiques
par école, pas d'un total universel. Ils ne sont donc pas présentés comme des totaux
de combat. Référence : fonction `_SaveStats` dans
[PlayerStorage.cpp](https://github.com/azerothcore/azerothcore-wotlk/blob/master/src/server/game/Entities/Player/PlayerStorage.cpp).
Le module complémentaire décrit ci-dessous récupère ces autres valeurs. Après une
première phase limitée aux tests, son installation a été explicitement autorisée
et effectuée le 5 septembre 2026. Le premier relevé de combat réel a été produit
à la déconnexion de Flowmage à 07:28:32.094 UTC, puis importé dans le prototype.

Le 5 septembre 2026 à 06:23:15 UTC, après accord explicite, le réglage
`PlayerSave.Stats.MinLevel` est passé de `0` à `1` dans le fichier du serveur actif.
Seule cette ligne a changé, avec copie préalable du fichier et permissions conservées.
`PlayerSave.Stats.SaveOnlyOnLogout = 1` et `PlayerSaveInterval = 900000` sont inchangés.
Le processus n'a pas été redémarré. L'utilisateur a confirmé le rechargement en jeu ;
la première ligne réelle pour Flowmage, sauvegardée à sa déconnexion à 06:30:37 UTC,
a ensuite été vérifiée et importée en local. Cette première sauvegarde confirmée
sert de borne conservatrice `--verified-after 2026-09-05T06:30:37Z` : l'heure exacte
du rechargement n'est pas déduite de la date de modification du fichier.
Le collecteur lui-même n'active aucun réglage et ne redémarre aucun service.

Après activation approuvée et une nouvelle déconnexion de Flowmage :

```powershell
node capture-statistics.cjs --host USER@HOST --key PATH_TO_EXISTING_KEY --verified-after COLLECTION_ACTIVATION_ISO_DATE
```

Les paramètres sont explicites, aucune clé n'est incluse au dépôt. Les erreurs et
les états sans données ne doivent pas être interprétés comme une capture réussie.

## Collecteur De Combat Déployé

`server-module/mod-atlas-armory` contient un module AzerothCore désactivé par défaut.
Après accord explicite, il a été intégré à un nouveau candidat Arthas et activé
uniquement pour Flowmage (`AtlasArmory.Enable = 1`, `AtlasArmory.OnlyGuid = 19092`).
La migration a créé `arthas_chars.atlas_armory_combat_snapshot`, sans modifier les
tables existantes. Le nouveau processus a démarré le 5 septembre 2026 à 07:17:06 UTC ;
son port de jeu 4000 a été confirmé ouvert à 07:18:06 UTC. Le launcher et le viewer
restent locaux : aucune version du launcher n'a été publiée pour cette opération.

La version initiale du module capture les valeurs du moteur dans `OnPlayerBeforeLogout`, avant les
opérations de nettoyage de la session. Il ignore les bots et effectue une seule
écriture asynchrone, sans requête SQL synchrone ni modification du personnage.
Un relevé contient identité, apparence, équipement porté et ses enchantements,
statistiques, six écoles de magie, arbres de talents actifs, forme et horodatage.
Il est indépendant des transactions de sauvegarde natives ; tous ses champs sont
lus ensemble sur le thread du jeu. Il ne collecte ni sacs ni banque et ne constitue
pas une synchronisation périodique. La collecte pendant le jeu a été ajoutée ensuite,
dans la section « Collecte Pendant Le Jeu » ci-dessous.

Les bonus aux dégâts et aux soins utilisent `SpellBaseDamageBonusDone` et
`SpellBaseHealingBonusDone`, pas la seule puissance de base. Les critiques sont
lus dans le champ correspondant à chaque école ; le champ physique n'est pas utilisé
comme critique magique. La puissance d'attaque utilise `GetTotalAttackPowerValue`.
Le toucher représente le bonus général du moteur, pas la probabilité de toucher
une cible donnée et pas tous les talents limités à un sort. La hâte est dérivée des
multiplicateurs de temps déjà calculés par le moteur, avec ralentissements conservés.
L'expertise présentée est celle de la main droite. Ces valeurs incluent les effets
temporaires et la forme présents à la capture, pas une simulation sans buffs.

Le choix d'école permet de consulter puissance et critique propres au Feu, au Givre,
etc. « Toutes les écoles » utilise leur minimum, suivant la fiche WotLK ; il ne s'agit
pas d'une moyenne. Les effets limités à un sort précis ou à une cible ne sont pas
inventés. Sources : `Unit/StatSystem.cpp`, `Unit/Unit.cpp` et `Player/Player.cpp` de
la révision active `ee60100e422b65bedbfab649e24f2c95794c8014`, ainsi que
`Interface_Wrath/FrameXML/PaperDollFrame.lua` du client audité.

Après connexion puis déconnexion de Flowmage, importer un relevé réel :

```powershell
node capture-statistics.cjs --host USER@HOST --key PATH_TO_EXISTING_KEY --verified-after 2026-09-05T07:17:06Z --combat
```

La nouvelle borne doit correspondre à l'activation du module de combat, pas à celle
de la collecte native. `combat-statistics-readonly.sql` ne lit que Flowmage ; le même
contrôle d'identité et d'équipement s'applique. Le cache version 2 est filtré avant
exposition par `/statistics.json`. Il ne contient ni GUID, ni compte, ni clé ; les
captures brutes serveur ne sont pas publiées. Les captures incomplètes ou incompatibles
sont refusées et n'écrasent pas le cache natif réel.

Validation préalable au déploiement : 27 tests Node, huit affichages PC FR/EN,
toutes les catégories des dix classes en FR/EN à 1280 x 720, compilation syntaxique
des deux fichiers C++ contre les en-têtes du candidat Arthas actif, tests C++ de hâte
et vérification du JSON typé par un SELECT MySQL en transaction READ ONLY.
Les 27 tests Node ont également été relancés avec succès pendant le déploiement.
Le binaire a été construit par compilation des deux fichiers du module et du chargeur
généré par CMake, puis édition de liens isolée avec les archives du candidat précédent.
Ce n'est pas une reconstruction complète : les six modules existants, leurs
enregistrements, les correctifs locaux et tous les autres objets ont été conservés.
Les identifiants ELF de la base, les dates des entrées et l'absence de modification
de ces entrées ont été contrôlés. Les dépendances dynamiques sont inchangées et
`worldserver --version` a réussi avant activation.

Le premier relevé réel du module a été produit le 5 septembre 2026 à 07:28:32.094 UTC,
puis importé à 07:28:47 UTC. L'identité et l'équipement complet correspondent à
l'export 3D. Le cache version 2 remplace le cache natif partiel : puissance des sorts
8, critique des sorts 6,3601127 %, bonus général de toucher 3 %, hâte 0 %.
Les six écoles ont ici les mêmes puissance et critique. Les talents actifs sont
0/0/13 et la forme vaut 0. L'écriture réelle confirme l'exécution du hook serveur ;
une comparaison côte à côte avec la fiche du jeu n'a pas été effectuée.
Après cet import, les 27 tests Node et les huit affichages PC FR/EN ont réussi
avec les valeurs réelles : aucune statistique manquante ni débordement, modèle
non vide et animé, rotation et infobulles fonctionnelles. Les tests des dix classes,
des écoles et du chargement asynchrone restent verts. Résultats et captures dans
`artifacts/armory-prototype/verification.json` et `view-fr-1440.png`.
Le démarrage n'est pas exempt de messages des modules existants : des erreurs de
sauvegarde de bots et de trajets ont été observées et ne sont pas corrigées ici.

### Traçabilité Du Déploiement

- Candidat : `/opt/arthas-next/candidates/armory-combat-20260905T070444Z`.
- Base préservée : `/opt/arthas-next/candidates/dungeon-clear-8224099-20260903T062903Z`.
- Révision source de base : `ee60100e422b65bedbfab649e24f2c95794c8014`.
- ELF Build ID : `f0e27089346ad13266ad4a8c5d2de76e34445811`.
- SHA-256 : `0dd3fd0cb34ea1fbe49961230e67147b1b3de9fabc75947f16a4f699e39fac63`.
- Service : `arthas-worldserver.dungeon-clear-8224099.service`, PID initial `1388262`.
- Surcharge : `/etc/systemd/system/arthas-worldserver.dungeon-clear-8224099.service.d/30-atlas-armory.conf`.
- Sauvegarde préalable : `/opt/arthas-next/backups/armory-combat-20260905T070444Z`.
- Scripts et manifeste locaux : `artifacts/armory-prototype/deploy-20260905` depuis la racine du dépôt.

La surcharge remplace uniquement `ExecStart` et porte `TimeoutStopSec` à 300 secondes.
Configuration, données, répertoire de travail et RUNPATH conservent leurs chemins
dans le candidat précédent : celui-ci ne doit pas être supprimé. Le service
d'authentification n'a pas été redémarré. Le retour arrière prévu n'a pas été nécessaire.

Une infobulle présente le nom coloré, l'icône, l'emplacement, le niveau et les caractéristiques de l'objet.
Le survol ou le focus donne un aperçu ; un clic ou Entrée conserve les détails.
Un clic à l'extérieur, Échap ou le bouton de fermeture les masque. Les caractéristiques
indisponibles sont signalées ; aucune valeur manquante n'est inventée.
Les attributs de base sont blancs ; les bonus secondaires et passifs d'équipement
sont verts. Les éventuels malus sont rouges, et le nom conserve la couleur de rareté.
La Chevalière d'invocateur affiche ainsi son Intelligence en blanc et son bonus
de critique en vert, avec une ligne distincte « Équipé ».

La langue suit `InterfaceLocale` dans les paramètres du launcher local
(`%LOCALAPPDATA%\Atlas Launcher Local\settings.json`), puis du launcher installé
(`%LOCALAPPDATA%\WotLK Launcher\settings.json`). Sans ces fichiers, la langue du
navigateur est utilisée, avec repli anglais. `?lang=fr` ou `?lang=en` force une langue
pour la prévisualisation. Les paramètres restent en lecture seule ; le serveur ne
renvoie que la langue et sa provenance, jamais les autres préférences ou secrets.
Revenir sur la page recharge la préférence de langue, pas les données du personnage.

## Provenance

Le fichier `flowmage-readonly.sql` a été exécuté après autorisation explicite, dans
une transaction MySQL READ ONLY, terminée par ROLLBACK. Il ne sélectionne que
Flowmage et ses emplacements équipés. Le relevé date du 4 septembre 2026 à 20:58:35 UTC.
Le JSON brut n'est pas servi au navigateur ; ni identifiant de compte, ni courriel,
ni secret n'est inclus dans le JSON public.

`catalog-readonly.sql` lit uniquement les 13 modèles d'objets de ce relevé dans
`arthas_world.item_template` et leurs traductions françaises dans `item_template_locale`,
également en transaction READ ONLY avec ROLLBACK. La date du catalogue est distincte
de celle du personnage : lire les descriptions ne met pas son équipement à jour.
`resolve-item-tables.cjs` extrait en français et en anglais les propriétés aléatoires,
enchantements et sorts pertinents des DB2 locales ; `item-details.cjs` les rapproche
des enchantements réellement enregistrés sur chaque instance.

La robe ajoute ainsi +6 Intelligence et +6 Endurance, les jambières +4 Intelligence
et +4 Esprit. Le sort d'équipement 9393 est vérifié : sa description utilise la valeur
2 de son effet fixe, une seule fois. Aucun remplacement dans `spell_dbc` n'a été trouvé
pour ce sort lors du contrôle en lecture seule. Les formules non prises en charge
sont refusées à l'export, pas évaluées approximativement. Références de structure :
[item_template](https://www.azerothcore.org/wiki/item_template) et
[item_instance_enchantments](https://www.azerothcore.org/wiki/item_instance_enchantments).

Les modèles, textures, animations et DB2 viennent exclusivement du client installé
`C:\Program Files (x86)\WotLK`, build `3.4.3.54261`. Les replis de téléchargement
d'assets du jeu sont désactivés. Les seules ressources réseau du préparateur sont
les définitions publiques WoWDBDefs et la liste publique TACTKeys de wowdev.
Les sections DB2 indisponibles sont ignorées comme dans le chargeur amont ; chaque
enregistrement utilisé pour ce personnage est exigé explicitement.

Parseurs et export glTF : [wow.export](https://github.com/Kruithne/wow.export),
licence MIT, révision `c2fd7bde36a712be78a5da896c995b84fbfa2545`.
La source clonée et sa licence restent dans `artifacts/armory-prototype/tools/wow-export`.
Three.js 0.180.0 (MIT), Lucide 0.468.0 (ISC), webp-wasm 1.0.6 (MIT), versions verrouillées.
Le code adapte uniquement l'initialisation desktop/cache de wow.export, sans modifier
les parseurs M2, BLP, DB2 ou le format glTF. La composition de texture utilise Canvas2D
et refuse les modes de fusion non pris en charge par ce prototype.

Ne pas publier les assets extraits du client : ils ne sont pas inclus au dépôt.

## Lancement

Depuis ce dossier, avec Node et pnpm disponibles :

```powershell
pnpm install --frozen-lockfile --ignore-scripts
node start.cjs
```

Le lanceur choisit un port libre entre 4387 et 4399. Il affiche l'URL, et enregistre
le PID et le port dans `artifacts/armory-prototype/server-instance.json`.
Pour un serveur au premier plan : `node server.cjs` (port 4387, configurable par `PORT`).

Node déjà présent sur le poste de Dono :

```powershell
& "$env:USERPROFILE\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe" start.cjs
```

## Synchronisation Locale Automatique

`statistics-sync.cjs` démarre après l'ouverture du serveur HTTP : aucune connexion
SSH n'est attendue pour afficher le cache. Une lecture READ ONLY du dernier relevé
de Flowmage a lieu immédiatement puis toutes les 60 secondes, indépendamment des
requêtes HTTP et du nombre d'onglets. Il n'y a qu'une capture en vol par processus.
La récupération fonctionne tant que ce serveur local tourne ; aucun service Windows,
tâche planifiée ou réglage supplémentaire d'Arthas n'est installé.

L'activation se fait uniquement dans le fichier privé, ignoré par Git et non servi
par HTTP, `artifacts/armory-prototype/statistics-sync.json`. Exemple à adapter avant
de lancer `node start.cjs` :

```json
{
  "schemaVersion": 1,
  "enabled": true,
  "host": "USER@HOST",
  "key": "C:\\path\\to\\existing-key",
  "verifiedAfter": "2026-09-05T07:17:06Z",
  "intervalMs": 60000,
  "clientRoot": "C:\\Program Files (x86)\\WotLK"
}
```

Le poste de Dono est configuré avec la destination et la clé existantes déjà
autorisées. La clé elle-même n'est jamais copiée. Aucun accès distant n'est activé
si le fichier manque ou si `enabled` vaut `false`. Une configuration invalide
désactive la récupération sans empêcher l'ouverture du viewer. Relancer le serveur
local après modification de cette configuration. Ne lancer qu'une instance pour
le même export ; `start.cjs` peut ouvrir un autre port si le précédent est occupé.

Chaque import valide toujours l'identité, le niveau, l'apparence et toutes les
instances équipées contre l'export 3D. Avec `clientRoot`, `sync-armory.cjs` détecte
une différence et prépare un export complet en arrière-plan. Sans ce champ,
seules les statistiques compatibles sont actualisées, comme précédemment.
Le cache précédent reste intact en cas d'absence de relevé, d'erreur ou d'objet
non pris en charge. Les relevés identiques ne réécrivent pas le cache ; une simple
variation des statistiques ne reconstruit pas la 3D.

Les requêtes SSH expirent après 30 secondes. En cas d'erreur, le délai entre lectures
passe à deux, quatre puis cinq minutes ; une réussite rétablit la minute habituelle.
`statistics-sync-status.json`, dans le même dossier privé, conserve l'heure du
dernier contrôle, le prochain contrôle et un code d'état sans secret ni réponse SSH.
Ce contrôle ne change pas `savedAt`, l'heure réelle de la capture du personnage.

Le navigateur lit uniquement `/statistics.json`, au plus une requête à la fois,
avec un délai maximal de dix secondes. La lecture périodique est suspendue quand
la page est cachée et reprend à son retour. Le cache déjà affiché reste visible
en cas de panne et les données inchangées ne reconstruisent pas les contrôles.
Une nouvelle capture compatible apparaît normalement dans la minute suivante,
plus le temps de lecture réseau et au plus cinq secondes de lecture locale.
Un changement visuel ajoute le temps de préparation décrit ci-dessous. Cela reste
une récupération des relevés du serveur ; la collecte pendant le jeu est décrite plus bas.

Validation : 35 tests Node passent, ainsi que les huit affichages PC FR/EN et les
vues des dix classes. Les tests Playwright à horloge contrôlée couvrent mise à jour
automatique, cache inchangé, refus des anciens relevés, panne, timeout, reprise,
onglet caché, retour sur la page, conservation du modèle, de l'école sélectionnée
et de l'infobulle épinglée. Deux lectures réelles ont été constatées les
5 septembre 2026 à 10:21:07 et 10:22:07 UTC, avec le même relevé conservé et sans
nouvelle écriture du cache. Aucun nouveau redémarrage d'Arthas n'a été effectué.

## Actualisation De L'Équipement Et De La 3D

Le collecteur serveur fournit déjà un relevé cohérent du personnage, de ses objets
équipés et de ses statistiques. La nouvelle boucle réutilise ce relevé pour Flowmage
uniquement. En cas de changement, elle lit les modèles d'objets correspondants en
transaction READ ONLY, avec une liste limitée aux 19 emplacements et des identifiants
entiers validés. Sacs et banque ne sont ni interrogés ni exportés.

L'export se déroule dans `builds/<identifiant>` : préparation, composition des textures,
glTF, icônes et descriptions FR/EN. Les processus d'export sont séparés du serveur HTTP
et lancés à priorité réduite lorsque Windows l'autorise. Les parseurs wow.export et
le cache de métadonnées existants sont réutilisés ; le client WoW reste en lecture seule.
Les descriptions sont maintenant associées à leur emplacement, pas seulement à
l'identifiant d'objet, pour ne pas confondre deux instances d'un même objet.

L'identité, l'équipement, les descriptions, les références aux ressources, les tailles
des buffers, les images, l'animation et les points d'attache sont vérifiés avant
publication. Le dossier final va dans `snapshots/<identifiant>`, puis le pointeur
`armory-current.json` est remplacé atomiquement. `/armory.json` n'expose que la révision
et son préfixe public. Les anciens assets sont conservés pour les onglets encore ouverts.
Leurs instantanés bruts et caches restent privés ; `/statistics.json` sert uniquement
les statistiques filtrées de la révision active.

Le navigateur charge le nouveau modèle, ses textures, icônes et shaders pendant que
l'ancien reste animé et utilisable. Il remplace ensuite ensemble équipement, descriptions,
statistiques et modèle. Orientation, zoom relatif, animation, école et infobulle d'un
objet toujours présent sont conservés. L'infobulle d'un objet retiré se ferme. La géométrie
et les textures du modèle remplacé sont libérées. Un échec ou un chargement de plus
de 30 secondes conserve la version visible ; une même révision en échec n'est retentée
qu'après 30 secondes, mais une nouvelle révision est prise en compte immédiatement.

Premier export complet réel validé le 5 septembre 2026 : relevé de Flowmage à
10:37:36.067 UTC, révision `761bd134cfc74a35b5d58dc3c1766470`. La préparation et la
publication ont pris environ 48 secondes sur ce poste, sans bloquer le cache affiché.
Ce temps n'est pas une garantie pour tous les équipements. L'utilisateur a confirmé
le succès du retrait réel du bâton. Son relevé du 5 septembre à 12:49:53.678 UTC
contient 12 objets équipés, sans arme en main droite.

42 tests Node passent. `verify.cjs` vérifie huit affichages PC FR/EN et les catégories
des dix classes. `verify-refresh.cjs` teste en FR/EN un retrait puis retour d'arme,
avec réponses modifiées uniquement dans le navigateur : ancien modèle visible pendant
le chargement, remplacement cohérent, cadrage, reprise après ressource manquante,
libération des ressources et infobulles. Ses captures `test-hot-swap-*.png` contiennent
des données de test, pas un nouveau relevé réel de Flowmage.

À cette étape historique, l'export était limité à Flowmage, les modèles de tête et
de main gauche n'étaient pas validés et la baguette restait dans son emplacement.
Ces restrictions de rendu ont été traitées dans l'intégration décrite en début de
document. La commande autonome de synchronisation reste liée à son relevé de
référence ; le launcher utilise les personnages du compte connecté. Les formules
d'effets non résolues restent signalées dans les descriptions d'objets.

Pour reconstruire volontairement un relevé réel même sans changement :

```powershell
node sync-armory.cjs --force
node verify-refresh.cjs
```

## Collecte Pendant Le Jeu

Activée après autorisation explicite de l'installation et du redémarrage d'Arthas.
Le serveur utilise le candidat `armory-live-20260905T1310Z` depuis le 5 septembre
2026 à 13:15:01 UTC ; son port 4000 a été confirmé prêt à 13:15:59 UTC.
Le candidat intermédiaire `modules-update-20260905T1016Z` a été préservé : seul
l'objet compilé du collecteur est remplacé. Les autres modules et leurs correctifs
restent identiques. Détails, empreintes et sauvegarde dans `server-module/README.md`.
Le launcher reste local et le service d'authentification n'a pas été redémarré.

`AtlasArmory.LiveEnable = 1` complète les deux réglages existants. La collecte est
toujours limitée à Flowmage, hors bots, et aux 19 emplacements portés, sans sacs
ni banque. Le module capture environ cinq secondes après l'entrée dans le monde,
puis toutes les 60 secondes. Un changement d'équipement déclenche une capture
après deux secondes de stabilisation, avec au moins cinq secondes entre captures.
Le signal d'équipement ne fait aucune écriture : les valeurs sont lues après la
mise à jour du joueur, une fois ses bonus appliqués. La déconnexion conserve un
dernier relevé, mais n'est plus nécessaire pour produire une capture.

Chaque écriture reste asynchrone. Une capture ancienne exécutée en retard ne peut
plus remplacer une capture récente. L'import accepte explicitement les raisons
`login`, `equipment`, `periodic` et `logout`, avec les mêmes contrôles d'identité,
d'équipement complet et de date. Une hausse des stats seule ne relance pas la 3D.

Ce n'est pas une synchronisation instantanée : après la capture, le contrôle local
peut ajouter jusqu'à 60 secondes, puis jusqu'à cinq secondes côté navigateur. Un
changement de modèle ajoute la préparation 3D (48 secondes lors de l'export mesuré).
Le cache reste visible et utilisable pendant ce travail. La charge à grande échelle
n'a pas été mesurée ; l'absence de requête SQL synchrone ne constitue pas une garantie
d'absence de lag. Les effets temporaires actifs sont inclus dans les statistiques.

Validation : 44 tests Node, tests C++ de cadence et de hâte, deux compilations
syntaxiques contre les en-têtes actifs et cinq contrôles SQL READ ONLY réussis.
Le retrait/retour simulé d'arme passe en FR/EN avec pixels WebGL non vides, cadrage,
infobulles et reprise après échec. À 13:17:05 UTC, Flowmage était encore hors ligne :
la première capture réellement effectuée en jeu reste à vérifier après sa connexion.

Pour désactiver cette collecte tout en gardant celle à la déconnexion, remettre
`AtlasArmory.LiveEnable = 0` et recharger la configuration. Ne pas supprimer la table
ni retirer les autres modules. Aucune généralisation des objets ou des personnages
non validés n'est impliquée par cette activation.

## Reproduire L'Export

Nécessite le relevé autorisé `artifacts/armory-prototype/flowmage.json`, le client exact,
Edge pour la composition Canvas2D, Playwright et la source wow.export épinglée ci-dessus.
Le prototype refuse un autre personnage ou modèle non vérifié.

```powershell
node prepare.cjs 'C:\Program Files (x86)\WotLK'
node export.cjs 'C:\Program Files (x86)\WotLK'
node resolve-item-tables.cjs 'C:\Program Files (x86)\WotLK' fr
node resolve-item-tables.cjs 'C:\Program Files (x86)\WotLK' en
node item-details.cjs
node --test tests/*.test.cjs
node verify.cjs
```

`prepare.cjs` peut prendre plusieurs minutes à initialiser les tables de personnalisation.
Son résultat est conservé dans `prepared.json`. `export.cjs` utilise ensuite ce résultat
et s'exécute en quelques secondes. `PLAYWRIGHT_MODULE` permet de définir le chemin du
module Playwright ; par défaut celui du runtime local Codex est utilisé.
L'enrichissement nécessite aussi `item-catalog.json`, issu de la requête autorisée
ci-dessus. Les deux extractions de langue utilisent des processus séparés pour isoler
le cache DB2 amont. Le viewer n'utilise les détails que si leur date de relevé correspond
exactement à celle de `character.json`.

Vérification : tests Node (routes, accès HTTP, provenance, glTF, textures, caractéristiques,
traductions et isolation des préférences), puis Playwright en français et en anglais
à 1440, 1920, 1280 et 1024 pixels. Lecture des pixels WebGL pour vérifier
un rendu non vide, un cadrage initial complet et du mouvement effectif pendant
l'animation et la rotation. Tests supplémentaires de pause, zoom, sélection, débordement,
absence de défilement vertical sur PC et infobulles au survol, au clic et au clavier.
Le changement de langue au retour sur la page préserve aussi l'infobulle épinglée.
La colonne de statistiques est vérifiée à droite sans chevauchement ni débordement,
avec données absentes, totaux simulés uniquement dans les tests et relevé incompatible.
Une réponse de statistiques bloquée ou en erreur ne doit pas bloquer le modèle ;
son arrivée met à jour uniquement la colonne et conserve l'infobulle ouverte.
`verify.cjs` utilise le port enregistré par `start.cjs`, ou l'URL fournie par `ARMORY_URL`.

## Limites relevées avant l'intégration WPF

- Récupération et export automatiques pour les objets pris en charge de Flowmage.
  Collecte pendant le jeu activée ; sa première capture réelle en ligne reste à vérifier.
- Première capture native réelle à 06:30:37 UTC, puis premier relevé de combat complet
  à 07:28:32.094 UTC le 5 septembre 2026, vérifiés et importés pour Flowmage.
- Collecteur de combat déployé sur Arthas pour Flowmage uniquement ; comparaison
  côte à côte avec le jeu encore à effectuer. Les vues des dix classes sont testées
  sur des données de test, pas sur dix personnages réels.
- Un seul personnage validé. Pas de configuration de profil, bio ou droits de visibilité.
- Caractéristiques validées uniquement pour les 13 objets de ce relevé. Pas de résolution
  universelle des formules de sorts, objets évolutifs ou suffixes à facteur aléatoire.
  Aucun de ces objets n'a de gemme ou d'enchantement non aléatoire à valider ; leur gestion
  générale reste à développer. La durabilité actuelle n'a pas été relevée et n'est pas affichée.
- Éclairage Three.js, découpe alpha et composition simplifiée : ce n'est pas encore
  une reproduction exacte des shaders, particules ou effets du client WoW.
- Le bâton utilise le point d'attache natif de la main droite. Son orientation et la
  prise en main restent à comparer visuellement avec Flowmage dans le jeu.
- La baguette apparaît dans son emplacement, sans être rendue simultanément dans la main.
- Aucun binaire du launcher installé ni fichier du client de jeu modifié. Le binaire
  du serveur Arthas, deux réglages de collecte, une surcharge systemd et une table
  dédiée ont été déployés après autorisation distincte.
- Aucun test WPF relancé : aucun fichier WPF n'a été touché pour cette preuve de concept.

L'hébergement dans WebView2 et le filtrage local par compte sont maintenant réalisés
dans la version décrite au début de ce document. Les étapes restantes concernent
la généralisation des objets et modèles et une API d'armurerie de production
filtrée par les droits du joueur.
La boucle SSH locale est un outil de prototype,
pas un mécanisme à distribuer aux joueurs.
Le périmètre convenu couvre uniquement l'équipement porté des personnages : ni sacs,
ni banque. La collecte pendant une session de jeu pour Flowmage est décrite plus haut ;
elle ne couvre pas encore tous les personnages du compte.
