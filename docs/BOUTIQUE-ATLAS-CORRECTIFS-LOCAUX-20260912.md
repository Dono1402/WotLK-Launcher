# Boutique : correctifs locaux du 12 septembre 2026

Cette campagne prépare un launcher `1.7.2-local`. Elle ne publie aucune mise à
jour et ne modifie ni les processus ni les bases du royaume public.

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

## Sélecteur natif de renommage : correctif de proxy préparé

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

Ce patch n'est pas installé sur le proxy public. Le launcher local se connecte aux
mêmes services réels ; il ne peut pas corriger lui-même une réponse du proxy.
Aucune validation de clic en jeu ni nouvelle campagne réseau de la fixture n'est
revendiquée ici. Le personnage de niveau 1 de la capture reste inéligible au
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
