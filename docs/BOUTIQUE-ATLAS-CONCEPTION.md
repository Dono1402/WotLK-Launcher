# Boutique Atlas : conception initiale

État au 11 septembre 2026 : premier jalon implémenté et testé localement : page
WPF, catalogue authentifié, choix du personnage et calculateur de conversion.
Les achats, les débits d'or, le portefeuille persistant et l'accès en jeu ne sont
pas encore actifs. Aucun élément de ce jalon n'est publié en production.
Le propriétaire a demandé une présentation
comme celle de Blizzard dans WoW et confirmé **les deux accès, launcher et jeu,
avec les mêmes produits et le même solde**. Les crédits pourront être gagnés en
jeu par conversion d'or. Un second portefeuille contient les euros déposés
sur le compte. Les prestataires retenus pour sa recharge sont **Stripe et PayPal**,
avec **Bancontact** parmi les moyens de paiement. Le premier produit et son
tarif sont maintenant définis ci-dessous. Les autres
produits et les règles d'attribution complémentaires restent à définir.

### Décisions commerciales confirmées

- Premier produit : **changement de nom**, puis élargissement progressif.
- Tarif confirmé le 10 septembre au soir : **5 € depuis les Crédits Atlas ou
  5 € depuis le portefeuille en euros**, au choix. Cette décision remplace
  l'ancien tarif de 300 po et le parcours de paiement direct de cette offre.
- Le **crédit Atlas est un solde affiché en euros**, conservé en centimes entiers.
  Il pourra servir aux achats de la boutique, dont le changement de nom.
- Le **portefeuille en euros est un solde distinct** sur le compte, comparable
  au portefeuille Blizzard. Les deux soldes se consultent en haut à droite,
  immédiatement à gauche de Messages ; ils sont visibles simultanément sur toutes les pages.
- Taux confirmé le 11 septembre : **100 po = 1 € de crédit Atlas**, soit
  **1 po = 0,01 €** et 10 000 pièces de cuivre par centime.
  Ce taux remplace les 80 po par euro initialement retenues.
- Le montant saisi est libre : **10 po donnent 0,10 €**, **212 po donnent 2,12 €**
  et **400 po donnent 4 €**. Le changement de nom reste à 5 € ; convertir
  500 po permet donc d'obtenir 5 € de Crédits Atlas.
- Chaque pièce d'or entière saisie est entièrement convertie en un centime.
  Les pièces d'argent/cuivre déjà présentes restent sur le personnage.
- Le bandeau inférieur ouvre un convertisseur centré via **Convertir**.
  Il remplace le catalogue directement dans la page du launcher, sans fenêtre.
  L'or est à gauche, la saisie au centre et le résultat en euros à droite ;
  la navigation supérieure et les soldes restent accessibles. Il comprend :
  personnage source, maximum propre à ce personnage, saisie en pièces d'or
  entières (sans argent/cuivre), raccourcis de pourcentage et aperçu du solde final.
  Des pièces s'animent dans le convertisseur ; seul le nombre de Crédits Atlas
  évolue ensuite dans l'en-tête. La page reste sur la conversion après réussite
  et permet de saisir un autre montant. Le portefeuille en euros n'est pas
  crédité par une conversion d'or.
- Présentation harmonisée avec les autres onglets : barre supérieure commune,
  Inter, ClearType/Ideal, surfaces vitrées et bordures discrètes du launcher.

Le [suivi d'implémentation](BOUTIQUE-ATLAS-IMPLEMENTATION.md) précise les fonctions
présentes, les tests et les travaux restant avant activation.

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
| Menu en jeu | Fenêtre native WoW ou interface Atlas par addon : réalisation à valider | Client 3.4.3, transport Hermes, identité de la session, limitations en combat, reconnexion |
| Les deux | Deux interfaces vers le même catalogue, les mêmes commandes et le même portefeuille de crédits | Achats simultanés et actualisation commune du solde et de l'historique |

Les deux interfaces font partie du périmètre demandé. Le premier parcours
vertical proposé reste le changement de nom, pour valider un achat complet
avec un seul service avant d'élargir le catalogue. La livraison finale ne devra
pas être déclarée terminée si l'un des deux accès manque.

### Présentation demandée

- Fenêtre de boutique dédiée, catégories à gauche et fiches produits illustrées.
- Détail d'une offre, aperçu quand il est pris en charge, prix et conditions.
- Choix du personnage bénéficiaire et récapitulatif avant achat.
- Présentation cohérente dans le launcher et le jeu, en français et en anglais.
- Solde et historique issus du même compte Atlas. Les crédits et commandes
  ne doivent pas être stockés séparément dans le launcher ou l'addon.

Les catégories Services, Montures et Mascottes servent à explorer la
présentation ; elles ne constituent pas encore un catalogue commercial validé.

### Intégration au style actuel du launcher

Le propriétaire a apprécié la maquette et précisé que la boutique doit
s'intégrer à l'esthétique actuelle du launcher. Cette exigence est retenue :
la référence Blizzard guide l'organisation du catalogue, les aperçus et le
parcours d'achat, tandis que la page WPF réutilise la charte Atlas existante.

La page doit reprendre les ressources de `UI/V2/Resources` : typographie
effective du launcher, fonds sombres, panneaux translucides, accents dorés et
cyan, arrondis, boutons, états de focus et animations. Elle doit aussi conserver
les espacements et l'adaptation aux petites fenêtres des autres pages.
Les couleurs et styles communs seront référencés depuis ces ressources.

Les ornements et la police classique de l'aperçu WoW ne définissent pas
l'habillage final de la page du launcher. L'interface en jeu pourra conserver
une présentation adaptée à WoW, avec les mêmes produits, libellés et règles
d'achat que le launcher. Les deux accès partagent leurs données sans imposer
un habillage identique à deux environnements graphiques différents.

## Constats dans le projet

- La navigation `LauncherShellV2` comprend désormais la page `ShopViewV2`, avec
  les mêmes ressources visuelles que les autres pages. L'API authentifiée
  `GET /api/v1/shop` fournit le catalogue et les personnages du compte.
- L'API possède déjà l'authentification des requêtes dans
  `source/WotLK.Launcher.Server/AtlasRequestAuthentication.cs` et la lecture des
  personnages du compte dans `ArmoryEndpoints.cs` et `ArmoryDatabase.cs`.
  Ces lectures ne constituent pas une autorisation d'achat : celle-ci devra être
  revalidée à la création de commande et à la livraison.
- Le catalogue initial est défini côté serveur. Aucun portefeuille persistant,
  traitement de commande ou débit d'or n'est encore implémenté dans ce jalon.
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

### Fenêtre Blizzard native : vérification complémentaire

Le checkout d'interface WoW examiné est
[`Gethe/wow-ui-source` au commit `564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde`](https://github.com/Gethe/wow-ui-source/tree/564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde).
Son fichier `Interface/AddOns/Blizzard_StoreUI/Blizzard_StoreUI_Wrath.toc`
charge l'interface commune/TBC de la boutique. Les sources comprennent les
catégories, fiches et services, avec un environnement protégé utilisant
`C_StoreSecure`. Les boutons ordinaires ne remplacent pas ce service interne.

Dans la copie Hermes du 7 septembre, `World/Server/WorldSocket.cs`, méthode
`SendFeatureSystemStatusGlueScreen`, fixe `BpayStoreAvailable` et
`BpayStoreEnabled` à `false`. Ces valeurs expliquent l'indisponibilité annoncée
par cette implémentation ; les passer à `true` ne fournirait ni catalogue ni
achat. Une relecture de la source exacte du binaire actif reste nécessaire.

La référence
[`TrinityCore/WowPacketParser` au commit `9806ff01740b799df3543122fb5b6ae96c493c27`](https://github.com/TrinityCore/WowPacketParser/tree/9806ff01740b799df3543122fb5b6ae96c493c27)
ne donne pas un décodeur utilisable du catalogue dans les fichiers examinés :
les gestionnaires BattlePay des modules 7.0.3 et 3.4.0 se limitent à consommer
le contenu du paquet sans le décoder. Celui du module 3.4.0 cible en outre
la version 3.4.4. Il ne faut pas déduire de leur présence un format de paquet
validé pour le client 3.4.3.54261.

Deux réalisations restent techniquement distinctes : réutiliser la fenêtre
native en implémentant les échanges exacts attendus par le client, ou produire
une interface Atlas fidèle à sa présentation avec un addon dédié. La première
piste exige de prouver le chargement d'un catalogue de test avant d'activer un
achat ; la seconde exige de valider son transport addon et son intégration au
menu. Le rendu de la maquette ne prouve pas la compatibilité de l'une ou l'autre.

Les changements d'apparence, de race et de faction doivent être vérifiés
séparément dans le serveur, Hermes et le client 3.4.3 avant ouverture à l'achat.
La présence d'une commande serveur ou d'un bouton client ne suffit pas.

## Fonctionnement commun à construire

1. **Catalogue serveur** : identifiant stable, révision, traductions, catégorie,
   disponibilité, prix, type de monnaie et conditions. Le premier catalogue
   implémente le tarif de renommage confirmé, sans activer les achats.
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

Le portefeuille de crédits appartient au compte Atlas ; le bénéficiaire d'un
service reste un personnage explicitement choisi. Les deux interfaces doivent
utiliser les mêmes règles de validation et de concurrence.

## Économie retenue

Deux portefeuilles et l'or du personnage doivent être représentés séparément :

| Moyen | Propriétaire du solde | Origine | Règle d'achat |
| --- | --- | --- | --- |
| Crédits Atlas affichés en euros | Compte Atlas, commun aux deux interfaces | Conversion volontaire d'or et éventuelles récompenses définies ultérieurement | Renommage à 5 €, débit et attribution centralisés et traçables |
| Portefeuille en euros | Compte Atlas, commun aux deux interfaces | Recharge du compte ; prestataires retenus Stripe et PayPal | Renommage à 5 € prélevés sur ce solde distinct |
| Or du jeu | Personnage explicitement sélectionné | Économie du jeu existante | Source de la conversion en Crédits Atlas ; validation et débit par le serveur de jeu |

Le joueur choisit le portefeuille utilisé pour l'achat avant de confirmer.
La conversion de l'or en crédit Atlas est une opération distincte, volontaire,
avec prévisualisation du montant libre et confirmation. Le paiement
partiel d'un même achat depuis les deux portefeuilles n'a pas été demandé.

Le journal du crédit conserve son origine : conversion d'or, récompense,
paiement, dépense, annulation et correction administrative. Les événements de
conversion, récompense et paiement possèdent des identifiants durables pour
empêcher un crédit répété.
Les règles d'attribution en jeu doivent être définies avant activation ; aucun
bonus de connexion, gain par minute ou tarif de récompense n'est présumé.

Le launcher et l'interface en jeu affichent le même or **pour le même
personnage**. Aucun portefeuille d'or partagé entre personnages n'a été demandé.
Une écriture directe de l'or en base pendant la connexion du personnage pourrait
être écrasée par sa sauvegarde en mémoire ; l'opération relève du core.

La recharge du portefeuille en euros proposera Stripe et PayPal. Bancontact sera
intégré via Stripe, qui le prend en charge dans Checkout pour les paiements en
euros. Son activation sur le compte marchand et le parcours réel doivent être
vérifiés avant ouverture. Il faut définir les packs, tarifs et règles de
remboursement avant activation. Les
euros seront accordés sur confirmation serveur vérifiée du paiement, avec
traitement des événements répétés et des remboursements. Un retour du navigateur
ne sera pas une preuve de paiement. Une annulation de paiement dont les euros
ont déjà été dépensés doit déclencher un traitement explicite, pas un nouveau
crédit ni un effacement silencieux de l'historique.

La carte bancaire et Bancontact suivent la confirmation serveur Stripe.
PayPal possède son propre parcours de capture et ses notifications vérifiées.
Les recharges aboutissent au journal du portefeuille en euros, séparé des
Crédits Atlas issus de l'or. Les références
des prestataires sont dédupliquées séparément ; aucun événement de test ne doit
alimenter un portefeuille de production.

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
- [Bancontact avec Stripe](https://docs.stripe.com/payments/bancontact) :
  disponibilité dans Checkout, paiements en euros et activation du moyen de
  paiement sur le compte Stripe.
- [Vérification des notifications Stripe](https://docs.stripe.com/webhooks/signature)
  et [notifications PayPal](https://developer.paypal.com/api/rest/webhooks/) :
  authenticité des événements à vérifier avant tout crédit.
- [Commandes PayPal v2](https://developer.paypal.com/api/orders/v2) : création,
  consultation et capture du paiement côté serveur.
