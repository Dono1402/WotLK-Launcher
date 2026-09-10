# Boutique Atlas : conception initiale

État au 10 septembre 2026 : étude des points d'intégration. La boutique n'est
pas encore implémentée ou publiée. Le catalogue, la monnaie et le premier
point d'accès restent à choisir avec le propriétaire du serveur.

## Expérience proposée

Une page **Boutique** présente le catalogue, le prix de chaque offre et ses
conditions. Le joueur choisit l'un de ses personnages, vérifie le récapitulatif
puis confirme l'achat. Un historique permet de distinguer commande reçue,
traitement en cours, service disponible et échec remboursé.

Le changement de nom constitue un premier parcours pertinent. La boutique
accorde le droit de renommer le personnage ; le joueur choisit son nouveau nom
depuis l'écran des personnages. Le service doit préciser quand une déconnexion
est nécessaire. L'achat ne recrée pas le personnage.

| Accès | Intégration proposée | Points à valider |
| --- | --- | --- |
| Launcher | Page WPF utilisant la session Atlas, la liste des personnages et l'API existantes | Navigation compacte, choix du personnage, confirmations, états réseau, français et anglais |
| Menu en jeu | Addon Atlas avec bouton dédié, relié à un module du serveur | Interface 30403, transport Hermes, identité de la session, limitations en combat, reconnexion |
| Les deux | Deux interfaces vers le même catalogue, les mêmes commandes et, si retenu, le même portefeuille | Achats simultanés et actualisation commune du solde et de l'historique |

Recommandation initiale : commencer dans le launcher avec le changement de nom,
puis étendre le catalogue et l'accès en jeu après validation du premier achat
complet. Ce choix n'est pas encore une décision de périmètre.

## Constats dans le projet

- `source/WotLK.Launcher/UI/V2/Presentation/LauncherShellPage.cs` ne contient pas
  de page Boutique. La navigation principale est portée par
  `LauncherShellV2.xaml` et son code associé.
- L'API possède déjà l'authentification des requêtes dans
  `source/WotLK.Launcher.Server/AtlasRequestAuthentication.cs` et la lecture des
  personnages du compte dans `ArmoryEndpoints.cs` et `ArmoryDatabase.cs`.
  Ces lectures ne constituent pas une autorisation d'achat : celle-ci devra être
  revalidée à la création de commande et à la livraison.
- Aucun portefeuille, catalogue marchand ou traitement de commande n'a été
  trouvé dans les sources applicatives examinées.
- Le code AzerothCore du candidat de livraison du 9 septembre contient
  `HandleCharacterRenameCommand`, qui peut accorder `AT_LOGIN_RENAME`. Le serveur
  conserve aussi la possibilité d'imposer un nom, distincte du parcours proposé.
- La copie Hermes inspectée dans les artefacts du 7 septembre contient les
  gestionnaires `CMSG_CHARACTER_RENAME_REQUEST` et
  `SMSG_CHARACTER_RENAME_RESULT`, ainsi que le transport des messages d'addons.
  Il s'agit d'une preuve de présence dans les sources, pas d'un test d'achat ou
  de renommage complet sur la version actuellement exécutée.
- Dans cette copie Hermes, les noms d'opcodes `BATTLE_PAY` existent dans les
  énumérations, sans gestionnaires correspondants dans les répertoires de
  traitement des paquets examinés. La boutique native Blizzard ne doit donc pas
  être considérée comme une interface Atlas déjà fonctionnelle.

Les changements d'apparence, de race et de faction doivent être vérifiés
séparément dans le serveur, Hermes et le client 3.4.3 avant ouverture à l'achat.
La présence d'une commande serveur ou d'un bouton client ne suffit pas.

## Fonctionnement commun à construire

1. **Catalogue serveur** : identifiant stable, révision, traductions, catégorie,
   disponibilité, prix, type de monnaie et conditions. Aucun tarif de production
   n'est fixé dans cette étude.
2. **Commande authentifiée** : l'interface envoie l'offre, sa révision, le
   personnage et un identifiant de requête réutilisé en cas de nouvel essai.
   Le serveur déduit le compte de la session et calcule le prix lui-même.
3. **Validation** : propriété actuelle du personnage, royaume, offre active,
   conditions remplies, solde disponible et absence de service identique déjà
   en attente. Un changement de prix demande une nouvelle confirmation.
4. **Réservation et livraison** : la commande et la réservation des crédits
   doivent être enregistrées dans une transaction. Le module de jeu exécute une
   opération métier précise ; aucun texte de commande GM fourni par le client
   n'est exécuté. Pour l'or, la réservation doit être conçue avec le serveur de
   jeu qui possède l'état du personnage connecté.
5. **Résultat durable** : une même commande ne livre et ne débite qu'une fois.
   Une perte de réponse réseau doit permettre de retrouver le résultat. Une
   livraison incertaine est rapprochée de l'état du jeu avant remboursement ou
   nouvel essai, afin d'éviter un service gratuit ou un double débit.
6. **Historique et administration** : états compréhensibles pour le joueur,
   consultation et ajustements tracés pour l'administrateur, désactivation
   d'une offre sans nouvelle publication du launcher.

Le portefeuille éventuel appartient au compte Atlas ; le bénéficiaire d'un
service reste un personnage explicitement choisi. Les deux interfaces doivent
utiliser les mêmes règles de validation et de concurrence.

## Choix de monnaie encore ouverts

- **Crédits Atlas sans paiement réel initial** : décider comment les crédits
  sont attribués, si le solde est partagé entre royaumes et quels ajustements
  administratifs sont permis. Aucun crédit gratuit automatique n'est présumé.
- **Or du jeu** : choisir le personnage débité et faire traiter l'opération par
  le core. Une écriture directe de son or en base pendant sa connexion pourrait
  être écrasée par sa sauvegarde en mémoire.
- **Crédits avec paiement réel** : définir les packs, tarifs et règles avant
  intégration d'un prestataire. Les crédits seront accordés sur confirmation
  serveur vérifiée du paiement, avec traitement des événements répétés et des
  remboursements. Un retour du navigateur ne sera pas une preuve de paiement.

## Validation attendue avant ouverture

- Parcours complet de renommage avec le client 3.4.3 et Hermes retenus, sur un
  compte et un personnage de test autorisés ; vérification de la conservation
  du personnage et de sa progression.
- Compte différent, personnage transféré ou supprimé, solde insuffisant,
  service déjà présent et offre devenue indisponible.
- Deux confirmations simultanées, reprise après expiration du réseau, arrêt
  entre réservation et livraison, puis nouvelle tentative de la même commande.
- Cohérence entre le journal financier, la commande et l'état durable du jeu.
- Interface sans connexion, sans personnage, catalogue vide, erreur serveur,
  historique et écran compact, en français et en anglais.

Les tests de compilation, de règles et de paquets devront être distingués du
test de bout en bout en jeu. Aucun test ne doit acheter ou modifier un personnage
réel par défaut.

## Préparation de la livraison

Les migrations `0009_auth_session_families.sql` et `0010_auth_session_gc.sql`
existent déjà dans le dépôt. La livraison du client 1.6.0 n'a pas déployé ces
migrations d'authentification. Il faudra vérifier le backend et le plafond de
migration réels avant d'ajouter le schéma marchand ; augmenter simplement ce
plafond pourrait aussi activer des changements d'authentification en attente.

Le développement et les essais isolés précèdent une proposition de déploiement
avec les versions, migrations, sauvegardes et effets sur les services. Une
intégration par module du core nécessite de préparer le binaire serveur et son
activation. Cette étude ne modifie aucun service, compte, personnage ou solde.

## Références examinées

- [Commandes GM AzerothCore](https://www.azerothcore.org/wiki/gm-commands) :
  sémantique du renommage à la connexion suivante.
- [Module officiel mod-reward-shop](https://github.com/azerothcore/mod-reward-shop) :
  exemple de remise de récompenses par code auprès d'un PNJ, dont le renommage.
  Ce module ne constitue pas à lui seul la boutique Atlas avec portefeuille et
  commandes proposée ici ; sa compatibilité actuelle n'a pas été testée.
- [Boutique ACore CMS](https://www.azerothcore.org/acore-cms/configure-cms.html) :
  solution distincte fondée sur WordPress/WooCommerce et SOAP. Son installation
  n'est pas retenue implicitement pour étendre le launcher existant.
