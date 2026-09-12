# Boutique : correctifs locaux du 12 septembre 2026

Cette campagne a préparé un launcher `1.7.2-local`, sans publication de mise à
jour. Après la demande explicite d'activer le correctif du sélecteur, Hermes seul
a été remplacé et redémarré le 12 septembre à 18 h 09 puis à 18 h 51 (Paris).
La seconde activation corrige le type réseau qui bloquait encore la sélection.
World, Auth et l'API
conservent leurs processus. Le déploiement n'applique aucune migration et ne crée
aucun achat, conversion ou renommage sur un compte joueur.

## Rafraîchissement de la boutique

Le suivi automatique, toutes les cinq secondes, utilisait le même état bloquant
qu'une actualisation demandée par l'utilisateur. Les listes de personnages,
prix, commandes, recharges et historique pouvaient également être réaffectées
alors que leur contenu n'avait pas changé. Cela reconstruisait des contrôles WPF
et désactivait brièvement les champs.

Le suivi en arrière-plan conserve maintenant les listes et lignes inchangées.
Les contrôles restent utilisables pendant la lecture ; la réponse conserve les
sélections les plus récentes. Une mutation financière annule toute lecture de
suivi antérieure, dont une réponse tardive ne peut plus remplacer les soldes et
reçus confirmés. La déconnexion et l'expiration de session vident les données.
Une actualisation manuelle conserve son indicateur de chargement.

## Présentation locale

- Le convertisseur utilise deux panneaux : or disponible, saisie, curseur,
  raccourcis et or restant à gauche ; crédits reçus et soldes avant/après à droite.
- Le PNG fourni est conservé à l'identique dans `Assets/Shop/Gold_coins.png`
  (SHA-256 `b1e85400a36ee2ce5cd89fa0ca95a8ab86d57e0b32a6dc30b72d9edee20b4dfc`).
  Il apparaît aussi devant le montant d'or restant, dans le taux et l'animation.
- Lorsque l'API annonce les services de compte, les quatre fiches omettent le
  choix du bénéficiaire dans le launcher et expliquent sa sélection en jeu.
  Le sésame, la race et la faction restent indisponibles tant que leurs parcours
  serveur ne sont pas implémentés et activés. Ce correctif de présentation
  n'ajoute pas leurs distributions natives au protocole du renommage.

## Sélecteur natif de renommage : correctif de proxy activé

Le handler VAS envoyait `RealmName=""`. Or le code du client 3.4.3 construit le
sélecteur avec `C_StoreSecure.GetRealmList()` puis
`C_StoreSecure.GetCharactersForRealm(selectedRealm)` ; il n'affiche la couche de
sélection que si cette liste contient des personnages. Le royaume vide est une
anomalie compatible avec le symptôme de la capture, mais son rôle dans le blocage
reste à confirmer sur le parcours graphique.

Le patch Hermes renseigne désormais le nom du royaume de la session authentifiée.
Une configuration sans nom de royaume renvoie une erreur au lieu d'une liste
ambiguë. Le test de paquet utilise le même constructeur de réponse que le handler,
car l'ancienne entrée fabriquée directement dans le test contenait déjà un nom
de royaume et masquait cette différence. Le test réseau de la fixture est renforcé
pour vérifier aussi le nom et l'adresse du royaume.

Le patch est désormais installé sur le proxy public. Le launcher local se connecte
aux mêmes services réels ; cette correction s'applique donc aussi aux utilisateurs
du launcher public sans distribuer un nouvel exécutable client. Les 19 contrôles
réseau de la fixture passent avec le paquet Linux exact, dont le royaume non vide,
la validation du nom, la consommation unique, l'annulation, la sortie du monde et
la reconnexion. Le clic dans l'interface graphique reste à confirmer après une
reconnexion complète au royaume. Le personnage de niveau 1 reste inéligible au
renommage natif, dont le minimum est le niveau 10 ; le niveau 35 satisfait ce seul
critère et nécessite encore la validation complète du parcours.

## Vérifications effectuées

- Compilation locale .NET 8 : aucune erreur ni avertissement.
- `--shop` : 369 assertions ; `--shop-refresh` : 15 contrôles de conservation des
  listes, saisie pendant le suivi, réponse obsolète après conversion et session.
- `--shop-wpf`, `--shop-gold-wpf`, `--shop-rename-wpf` et
  `--shop-local-fixes-wpf` : réussis, sans erreurs de liaison.
- Les nouvelles vérifications WPF inspectent les véritables conteneurs de cartes,
  le choix de monnaie, le texte et le curseur pendant une lecture retardée ;
  elles vérifient les deux panneaux et les deux icônes d'or en français/anglais
  à 1080 et 1586 pixels. Les rendus ont été produits avec une fenêtre synthétique
  hors écran, inactive, sans compte réel ni lancement du jeu.
- Hermes : 22 tests ciblés réussis ; les patchs disconnect puis account-services
  s'appliquent sur la base `f859d0c`. Les 14 vérifications des parseurs du client
  3.4.3 sous émulation CPU acceptent les paquets émis. Ce contrôle valide le format,
  sans remplacer un test de sélection graphique ou de session réseau.

## Utilisation du launcher local

`scripts/build-local-client.ps1` produit un exécutable autonome
`AtlasLauncherLocal.exe`, avec `AtlasLocalClientBuild=true`. Les fonctionnalités
applicatives et points d'accès réels sont conservés. Le stockage est séparé dans
`Atlas Launcher Local`, et l'auto-mise à jour publique est désactivée pour garder
les correctifs locaux pendant les essais. L'armurerie utilise le runtime Node et
le serveur local du dépôt via `armory-local.json` ; ce paquet est destiné à ce PC.

Les rendus, journaux et exécutables sont conservés sous `artifacts/` et exclus de
Git. Le push des sources sur la branche de travail ne constitue pas une release
ni une installation du patch proxy sur le royaume.

## Activation du proxy

Le paquet provient d'une nouvelle copie de la base Hermes `f859d0c`, avec les
patchs disconnect-owner et account-services du commit launcher `a3d572c`.
L'exécutable Linux testé puis activé a l'empreinte
`7520e133e7d20e0f2d5ded6bb36a61e3b792d1acc1427597ad81120eb7deeb7d`.

La nouvelle installation est
`/opt/hermesproxy-wotlk/releases/hermes-vas-selector-20260912`. La configuration
existante et les dossiers partagés AccountData/Logs/PacketsLog sont conservés.
Le binaire précédent, la configuration et les unités systemd sont sauvegardés
sous `/opt/atlas-shop-releases/vas-selector-20260912/backup`, avec hashes comparés.
L'ancienne installation reste également disponible à son chemin d'origine.

`scripts/atlas-shop-vas-selector-release/deploy.py` sépare préparation, test isolé
et activation ; la dernière phase redémarre uniquement `hermesproxy-wotlk` et
prévoit le retour au binaire précédent si le démarrage échoue. Le test réseau
utilise une base jetable sans réseau public, arrêtée ensuite. `verify.py` contrôle
l'identité active, la stabilité du processus, les autres services, le signal du
module et le manifeste public du launcher resté strictement identique en 1.7.2.

La vérification finale a confirmé Hermes actif sans redémarrage automatique,
les quatre ports attendus ouverts et l'API saine. Les processus World `2828323`,
Auth `323656` et API `2830654` sont restés identiques. Voir la
[preuve d'activation](validation/hermes-vas-selector-20260912.json).

## Sélection toujours bloquée après cette activation

L'essai du client à 18 h 34 a mis en évidence un second défaut : la requête
`CMSG_GET_VAS_ACCOUNT_CHARACTER_LIST` contient le type réseau **7**, tandis que
le proxy attendait **4**. La réponse observée avait `Result=1` et zéro personnage.
Le correctif du nom de royaume ne pouvait donc pas être utilisé par l'interface.

Le client 3.4.3.54261 convertit lui-même la valeur Lua `PaidNameChange=4` en
type réseau `7`. Cette conversion a été exécutée sous émulation CPU et reproduit
exactement les huit octets observés : `0100000007000000`. Le proxy utilise
désormais une constante `PaidNameChangeWireType=7` ; les autres types restent
refusés. Le client du test réseau utilise également la valeur réellement émise
par le jeu. Son ancienne valeur 4 expliquait pourquoi le test passait alors que
l'interface restait bloquée.

Vérifications locales du correctif :

- Le nouveau test rejette la requête avant correction, puis passe après.
- Les 24 tests ciblés passent avec `HERMES_TEST_MODERN_BUILD=3.4.3`.
- Les 16 contrôles natifs passent, y compris la conversion de la requête, le
  remplissage du cache de la boutique et les fonctions `GetRealmList` et
  `GetCharactersForRealm` utilisées par l'étape 1. Les événements et l'envoi réseau
  sont capturés par le test ; le jeu et son interface ne sont pas lancés.
- La compilation Linux autonome est réussie. Les avertissements de compilation
  et de trimming préexistants restent présents.

Voir la [preuve du défaut et des contrôles client](validation/hermes-vas-wire-type-client-20260912.json).
Le déploiement de ce second correctif utilise les phases existantes avec
`--release vas-wire-type-20260912`, dans un nouveau répertoire avec sa propre
sauvegarde.

Le correctif est activé depuis le 12 septembre à **18 h 51 (Paris)**, avec
Hermes PID `2860655`, sans redémarrage automatique. Le binaire actif est celui
du test isolé : SHA-256
`4bd47690d3df31cb97b2eb6beacf2e37515b25373b3dc2718d527478ed3e7c47`.
Les **20 contrôles réseau passent**, dont la requête réelle de type 7,
la validation, le renommage Unicode, l'absence de double débit et la reconnexion
après jeu. Tous les processus et le conteneur MySQL de test sont arrêtés.

World `2828323`, Auth `323656` et API `2830654` conservent leurs PID,
exécutables et unités systemd. La configuration Hermes et le manifeste public
du launcher 1.7.2 sont inchangés. Les sauvegardes vérifiées sont conservées sous
`/opt/atlas-shop-releases/vas-wire-type-20260912/backup` ; le paquet actif est
`/opt/hermesproxy-wotlk/releases/hermes-vas-wire-type-20260912`.
Voir la [preuve d'activation et de test réseau](validation/hermes-vas-wire-type-20260912.json).

Le passage graphique à l'étape 2 reste à confirmer dans le jeu après une
reconnexion complète au royaume ; aucune interaction avec le jeu de l'utilisateur
n'a été effectuée pendant ces contrôles.

## Signalement suivant : nouveau nom à la sélection, ancien nom en jeu

Le retour utilisateur suivant et le reçu durable confirment qu'un renommage a
abouti après le correctif du sélecteur. Une liaison supplémentaire manquait :
l'invalidation du cache de noms du client et des autres joueurs connectés.
Le [correctif de l'identité en jeu](BOUTIQUE-ATLAS-NOM-EN-JEU-20260912.md) complète
cette notification, le rafraîchissement à l'entrée en jeu et le traitement des
anciens reçus. Ses candidats passent 94 contrôles d'intégration et 24 tests
Hermes. Ils sont préparés et sauvegardés à 20 h 30, mais leur activation attend
l'accord explicite pour redémarrer World. Le lanceur public reste en 1.7.2.
