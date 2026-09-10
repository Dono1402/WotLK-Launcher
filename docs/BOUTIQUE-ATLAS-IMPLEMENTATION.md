# Boutique Atlas : catalogue, soldes et conversion locale

État au 11 septembre 2026. Ce jalon fournit les écrans WPF, le catalogue
authentifié, deux soldes visibles simultanément et le convertisseur intégré.
Le mode de prévisualisation peut simuler un débit d'or et un crédit Atlas en mémoire.
Il ne permet pas encore de payer, de débiter l'or d'un véritable personnage,
de créditer un portefeuille persistant ou de renommer un personnage.
Aucun service de production ni client installé n'a été modifié.

## Fonctionnement présent

- Onglet **Boutique** reproduisant la composition de la maquette fournie :
  catalogue à gauche, trois cartes illustrées, sélection à droite et bandeau
  de conversion en bas. Les deux services futurs sont uniquement des annonces.
- Fiche **Changement de nom** à **5 € depuis les Crédits Atlas ou le
  portefeuille en euros**, conditions, personnage bénéficiaire et récapitulatif.
  L'ancien tarif de 300 po est retiré.
- Page et données en français/anglais ; conservation de la sélection lors du
  changement de langue et de l'actualisation. Un personnage disparu ne peut
  pas être remplacé silencieusement par un autre bénéficiaire.
- **Crédits Atlas et portefeuille en euros visibles simultanément** dans la
  barre supérieure, immédiatement à gauche de Messages, sur toutes les pages.
  Leur groupe passe sur deux lignes en largeur compacte. Les deux sont exprimés
  en centimes entiers. Le serveur renvoie actuellement `null` pour chacun,
  affiché `—`, puisque leur persistance n'est pas encore implémentée.
- Bandeau inférieur **Convertir** remplaçant le catalogue par une interface
  centrée dans la page du launcher. Présentation horizontale : or disponible à
  gauche, montant numérique au centre, flèche et Crédits Atlas en euros à droite.
  La navigation et les soldes restent accessibles ; **Retour à la boutique**
  et Escape restaurent le catalogue. Choix séparé du
  personnage source, maximum en pièces d'or entières propre à ce personnage,
  saisie entière sans argent/cuivre, bouton Max, curseur et raccourcis
  25/50/75/100 %, crédit obtenu, or restant et nouveau solde Atlas. Les personnages connectés n'exposent pas
  de solde d'or périmé et ne peuvent pas convertir.
- Pendant la conversion de prévisualisation, des pièces se déplacent à
  l'intérieur du convertisseur. Le nombre de Crédits Atlas dans l'en-tête évolue
  ensuite discrètement sur place, sans pastille traversant l'écran.
  Le réglage Windows de réduction des animations est respecté.
- **La conversion reste affichée après réussite**, avec le crédit reçu, le solde
  précédent, le nouveau solde et un message de confirmation. Saisir un nouveau
  montant prépare une autre conversion. Les doubles clics pendant l'animation
  sont ignorés ; quitter la page ou changer de session annule l'opération en
  attente avant tout débit/crédit simulé.
- États de chargement, catalogue vide, absence de personnages, session expirée,
  débit de requêtes excessif et boutique indisponible. Un ancien backend sans
  cette route n'entraîne pas l'affichage d'un catalogue d'exemple dans la session.

## Maquette et ressources du 10 septembre

Les illustrations, le logo et le décor proviennent de l'archive fournie
`Atlas_Boutique_Assets_Pour_Codex.zip`. Leur provenance et leurs dimensions sont
documentées dans `source/WotLK.Launcher/Assets/Shop/README.md`.
Les composants restent natifs : textes localisés, liste du service disponible,
choix du personnage, choix du paiement, récapitulatif et calculateur.
Les conditions du service sont consultables dans l'infobulle du récapitulatif.

À 1586 × 992, l'ensemble du catalogue et du bandeau tient sans défilement.
Sous 1480 DIPs de largeur, le récapitulatif passe sous les cartes pour conserver
leur lisibilité ; le défilement vertical permet d'accéder au reste de la page.
Le bouton Convertir du bandeau ouvre la conversion dans la zone de contenu,
sans fenêtre, voile sombre ni comportement modal. Le contenu central défile si
nécessaire sur un petit écran ; le bouton de confirmation reste visible.
La barre supérieure est commune à Jeu, Addons et Boutique : même logo, hauteur,
marges, boutons et proportions à largeur identique. Seul l'onglet actif change.
Les adaptations dépendent uniquement de la largeur disponible.
Les panneaux réutilisent les surfaces vitrées, les bordures et les rayons de
14 DIPs du launcher, avec Inter et ClearType/Ideal. Les titres utilisent le
dégradé nacré commun. L'icône d'or est un dessin vectoriel WPF, sans texte agrandi
dans une Viewbox et sans halo lumineux autour des cartes sélectionnées.

Le pack ne contient pas le décor complet sans interface : son fond reconstitué
est moins détaillé que la référence. Le fragment original de citadelle est
réintégré à sa position native avec des bords fondus. La texture du fond et la
police d'origine non identifiée empêchent une identité stricte pixel par pixel.

Vérifications propres à cette refonte : compilation, `--shop`, `--shop-wpf`
et `--shell-navigation-wpf`. Le serveur et l'aperçu utilisent le même taux partagé de 100 po par euro.
La révision du catalogue inclut désormais ce taux, en plus des offres.
Le contrôle API/MySQL a été rejoué après ce changement : 1, 10, 100 et 400 po
donnent exactement autant de centimes depuis le catalogue HTTP authentifié.
La limite globale de la suite de navigation est de huit minutes pour couvrir les deux
tailles et les attentes d'animation, avec progression par taille et résultat
retourné après la fermeture du dispatcher WPF.

## Montants et conversion

Les montants d'or sont des entiers en pièces de cuivre ; les montants en euros,
y compris le crédit Atlas, sont des entiers en centimes. Aucun calcul de solde
ne dépend de nombres à virgule flottante.

Le serveur fournit le taux de **10 000 pièces de cuivre par centime**, soit
100 po par euro, selon la décision du 11 septembre. Le calcul partagé `ShopGoldConversionRate.Quote` produit :

| Montant proposé | Crédit obtenu | Or débité | Reste conservé |
| --- | --- | --- | --- |
| 400 po | 4,00 € | 400 po | 0 po |
| 212 po | 2,12 € | 212 po | 0 po |
| 10 po | 0,10 € | 10 po | 0 po |
| 1 po | 0,01 € | 1 po | 0 po |
| 99 pa 99 pc | 0,00 € | 0 po | 99 pa 99 pc |

Le champ n'accepte que des pièces d'or entières, sans point ni virgule.
Max et les pourcentages ignorent les pièces d'argent/cuivre du solde disponible.
Exemple : 423,5067 po donnent un maximum saisissable de 423 po ; 25 % de 120,8 po
proposent 30 po. Les valeurs négatives, la notation scientifique, les séparateurs
de milliers et les montants dépassant la capacité du champ `money` sont refusés.
Le minimum saisissable est 1 po. Le calcul partagé conserve toutefois toute sa
précision en cuivre : 423 po proposés donnent 4,23 € et débitent exactement
423 po. Le solde initial de 423,5067 po conserve donc 50 pa et 67 pc. La dernière ligne du tableau
teste le calcul interne, pas une saisie permise par l'interface. Le débit réel
devra recalculer ce devis côté serveur ; le montant affiché par le launcher ne fera pas autorité.

Le montant à convertir doit être inférieur ou égal à l'or connu du personnage.
Passer à un personnage moins riche réduit la saisie à son maximum. Le champ
refuse les lettres et les décimales à la frappe et au collage ; les montants
d'or sont présentés en nombres entiers, sans arrondi vers le haut.
Le devis ne peut pas dépasser le plafond des Crédits Atlas. La conversion
n'affecte jamais le portefeuille en euros.

## API et session

`GET /api/v1/shop` utilise l'authentification Atlas existante et déduit le compte
de la session. Aucun paramètre de compte, personnage, tarif ou requête SQL n'est
accepté. La réponse porte `Cache-Control: no-store` et le budget de lecture est
limité par compte.

Le serveur lit uniquement les personnages possédés par ce compte. Il fournit
l'or sauvegardé d'un personnage déconnecté. Pour un personnage connecté, le
solde est `null` : l'or en mémoire du core peut différer de la dernière
sauvegarde SQL. Même l'or sauvegardé devra être revalidé lors d'un futur débit.

Le catalogue initial est défini dans `ShopCatalog`. Sa révision dépend de son
contenu. Les tarifs peuvent être configurés via :

```text
AtlasShop:Rename:EuroCents = 500
AtlasShop:Rename:CreditEuroCents = 500
```

Les tarifs non positifs et les montants hors limites empêchent l'initialisation
du catalogue. Il n'existe aucun endpoint d'achat ou de conversion dans ce jalon.
Le DTO partagé est dans `WotLK.Launcher.Shop.Contracts`, **schéma 2**. Les devises
de prix sont `credits` et `eur` ; `gold` n'est plus accepté. Les deux champs
`CreditBalanceEuroCents` et `EuroBalanceCents` sont indépendants et facultatifs.
Un client de ce jalon refuse le schéma 1 : le déploiement futur doit donc
coordonner les versions serveur/client. Le client limite les
réponses à 256 Kio, y compris sans `Content-Length`, valide le schéma et refuse
les réponses d'une session devenue obsolète pendant le chargement. Une
déconnexion efface le catalogue, le personnage, le montant saisi et le solde.

## Essai local isolé

Après compilation, le mode suivant ouvre la boutique avec des exemples :

```powershell
.\AtlasLauncherLocal.exe --ui-v2 --preview-shop
```

Ce mode passe par la route de prévisualisation existante. Il ne crée pas le
runtime réel, ne charge pas le compte et ne contacte pas l'API. Son solde de
2,65 € de Crédits Atlas, ses 10,00 € de portefeuille et ses personnages sont
fictifs. Le bouton Convertir simule une opération en mémoire : seuls l'or du
personnage choisi et les Crédits Atlas changent. L'actualisation conserve ce
résultat jusqu'à la fin de la session de prévisualisation. Les achats restent
désactivés. En mode connecté réel, la conversion reste fermée. Les contrôles
hors écran testent aussi l'isolation de cette
route et son incompatibilité avec une autre prévisualisation dédiée.

Le lancement normal utilise l'API configurée du launcher. Tant que le nouveau
backend n'est pas déployé, il peut afficher la boutique indisponible.

## Vérifications réalisées

SDK local : .NET 8.0.424. Compilation du launcher, du serveur et des tests
d'intégration sans erreur ni avertissement.

| Suite | Résultat et portée |
| --- | --- |
| `--shop` | 113 assertions : taux de 100 po par euro, deux soldes, limites par personnage, précision, débit/crédit simulé conservatif, refus du dépassement, saisie, session, erreurs HTTP et séparation du mode réel |
| `--shop-wpf <dossier>` | Catalogue à 1586×992 puis quatre tailles FR/EN ; deux soldes supérieurs, conversion horizontale intégrée de 1080×680 à 1586×992, frappe/collage entiers, Max par personnage, en-tête identique à Addons, navigation libre/retour/Escape, animation interne annulable, maintien dans la conversion après réussite, sélection conservée et actualisation des soldes à la connexion ; captures PNG et aucune erreur de binding |
| `--shop-mysql` | API HTTP réelle et MySQL 8.4.11 jetable sur loopback : authentification, appartenance Atlas, personnages autorisés, soldes, prix, taux, absence de mutation, limites et erreurs |
| `--armory-session` | Régressions de session et tests boutique : refus du refresh, 401, reconnexion au même compte ou à un autre, réponse tardive et annulation |
| `--shell-navigation-wpf` | 898 assertions incluant le nouvel onglet et les panneaux existants |
| `--startup-routing` | Démarrage réel inchangé et prévisualisation boutique isolée, sans accès au registre réel |

Les contrôles graphiques utilisent des fenêtres synthétiques, inactives et hors
écran. Des captures du catalogue et du convertisseur ont été relues. Aucun
launcher utilisateur, jeu ou navigateur n'a été ouvert. Les bases jetables ont
été supprimées par les tests et le processus MySQL local a été arrêté.

## Suite avant activation

1. Deux portefeuilles persistants en centimes, journal des mouvements, commandes et
   identifiants idempotents pour achats, conversions et notifications répétées.
2. Module du core pour contrôler l'or, exécuter le renommage et enregistrer un
   résultat durable. Tester les connexions concurrentes et les interruptions
   entre débit, sauvegarde et confirmation ; aucune écriture directe de l'or
   d'un personnage connecté depuis l'API.
3. Recharge du portefeuille en euros via Stripe/PayPal/Bancontact, validation serveur des événements,
   reprise après interruption et traitement des remboursements.
4. Boutique et conversion accessibles en jeu, reliées aux mêmes données et aux
   mêmes opérations, avec test sur le client 3.4.3 et Hermes retenus.
5. Validation de bout en bout, puis préparation du déploiement et des migrations
   avec sauvegardes, effets sur les services et accord de publication.

Les migrations d'authentification 0009/0010 en attente restent hors de ce jalon.
Le plafond de migration et la version publique du launcher n'ont pas changé.
