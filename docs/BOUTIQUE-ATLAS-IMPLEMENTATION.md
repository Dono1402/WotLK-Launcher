# Boutique Atlas : premier jalon local

État au 10 septembre 2026. Ce jalon fournit les écrans WPF, le catalogue
authentifié et le calculateur de conversion. Il ne permet pas encore de payer,
de débiter de l'or, de créditer un portefeuille ou de renommer un personnage.
Aucun service de production ni client installé n'a été modifié.

## Fonctionnement présent

- Onglet **Boutique** reproduisant la composition de la maquette fournie :
  catalogue à gauche, trois cartes illustrées, sélection à droite et bandeau
  de conversion en bas. Les deux services futurs sont uniquement des annonces.
- Fiche **Changement de nom** à **5 € en paiement direct ou 300 po**, conditions,
  choix explicite du personnage et récapitulatif du moyen de paiement.
- Indication carte, Bancontact et PayPal pour l'option directe en euros.
- Page et données en français/anglais ; conservation de la sélection lors du
  changement de langue et de l'actualisation. Un personnage disparu ne peut
  pas être remplacé silencieusement par un autre bénéficiaire.
- Solde Atlas exprimé en euros. Le serveur renvoie actuellement `null`, affiché
  `—`, puisque le portefeuille n'est pas encore implémenté. Il ne renvoie pas
  un faux solde nul.
- Panneau **Convertir mon or en crédit Atlas**, avec montant libre, crédit
  obtenu, or à débiter et reste conservé. Le calcul est immédiat et n'effectue
  aucune opération sur le compte.
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
Les deux boutons de conversion ouvrent le même calculateur existant.
La barre supérieure adopte les proportions et le logo du pack sur cette page.
Les autres pages conservent leur présentation habituelle.

Le pack ne contient pas le décor complet sans interface : son fond reconstitué
est moins détaillé que la référence. Le fragment original de citadelle est
réintégré à sa position native avec des bords fondus. La texture du fond et la
police d'origine non identifiée empêchent une identité stricte pixel par pixel.

Vérifications propres à cette refonte : compilation, `--shop`, `--shop-wpf`
et `--shell-navigation-wpf`. Les contrôles API/MySQL et de session ci-dessous
correspondent au premier jalon ; aucun code serveur n'a changé dans la refonte.
La suite de navigation a terminé ses 898 assertions en 215 secondes sur cette
machine. Sa limite globale est portée à cinq minutes pour couvrir les deux
tailles et les attentes d'animation, avec progression par taille et résultat
retourné après la fermeture du dispatcher WPF.

## Montants et conversion

Les montants d'or sont des entiers en pièces de cuivre ; les montants en euros,
y compris le crédit Atlas, sont des entiers en centimes. Aucun calcul de solde
ne dépend de nombres à virgule flottante.

Le serveur fournit le taux de **8 000 pièces de cuivre par centime**, soit
80 po par euro. Le calcul partagé `ShopGoldConversionRate.Quote` produit :

| Montant proposé | Crédit obtenu | Or débité | Reste conservé |
| --- | --- | --- | --- |
| 400 po | 5,00 € | 400 po | 0 po |
| 212 po | 2,65 € | 212 po | 0 po |
| 1 po | 0,01 € | 80 pa | 20 pa |
| 79 pa 99 pc | 0,00 € | 0 po | 79 pa 99 pc |

Le champ accepte un point ou une virgule et jusqu'à quatre décimales d'or pour
représenter les pièces de cuivre. Les valeurs négatives, la notation
scientifique, les séparateurs de milliers et les montants dépassant la capacité
du champ `money` du jeu sont refusés. Le montant minimal effectivement
convertible correspond à un centime. Le débit réel devra recalculer ce devis
côté serveur ; le montant affiché par le launcher ne fera pas autorité.

Le tarif du renommage à 300 po est distinct du taux de conversion de 400 po
pour 5 €, comme confirmé par le propriétaire.

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
AtlasShop:Rename:GoldCopper = 3000000
```

Les tarifs non positifs et les montants hors limites empêchent l'initialisation
du catalogue. Il n'existe aucun endpoint d'achat ou de conversion dans ce jalon.
Le DTO partagé est dans `WotLK.Launcher.Shop.Contracts`. Le client limite les
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
2,65 € et ses personnages sont fictifs. Les boutons de paiement et conversion
restent désactivés. Les contrôles hors écran testent aussi l'isolation de cette
route et son incompatibilité avec une autre prévisualisation dédiée.

Le lancement normal utilise l'API configurée du launcher. Tant que le nouveau
backend n'est pas déployé, il peut afficher la boutique indisponible.

## Vérifications réalisées

SDK local : .NET 8.0.424. Compilation du launcher, du serveur et des tests
d'intégration sans erreur ni avertissement.

| Suite | Résultat et portée |
| --- | --- |
| `--shop` | 53 assertions : montants approuvés, conversion libre, reste, précision, bornes HTTP, erreurs, sélection et réponses tardives |
| `--shop-wpf <dossier>` | Référence 1586×992 sans barre de défilement, puis français/anglais à 1672×941, 1440×860, 1280×760 et 1080×680 ; navigation, listes, actualisation, conversion et captures PNG ; absence d'erreurs de binding |
| `--shop-mysql` | API HTTP réelle et MySQL 8.4.11 jetable sur loopback : authentification, appartenance Atlas, personnages autorisés, soldes, prix, taux, absence de mutation, limites et erreurs |
| `--armory-session` | Régressions de session et tests boutique : refus du refresh, 401, reconnexion au même compte ou à un autre, réponse tardive et annulation |
| `--shell-navigation-wpf` | 898 assertions incluant le nouvel onglet et les panneaux existants |
| `--startup-routing` | Démarrage réel inchangé et prévisualisation boutique isolée, sans accès au registre réel |

Les contrôles graphiques utilisent des fenêtres synthétiques, inactives et hors
écran. Des captures du catalogue et du convertisseur ont été relues. Aucun
launcher utilisateur, jeu ou navigateur n'a été ouvert. Les bases jetables ont
été supprimées par les tests et le processus MySQL local a été arrêté.

## Suite avant activation

1. Portefeuille persistant en centimes, journal des mouvements, commandes et
   identifiants idempotents pour achats, conversions et notifications répétées.
2. Module du core pour contrôler l'or, exécuter le renommage et enregistrer un
   résultat durable. Tester les connexions concurrentes et les interruptions
   entre débit, sauvegarde et confirmation ; aucune écriture directe de l'or
   d'un personnage connecté depuis l'API.
3. Paiements directs Stripe/PayPal/Bancontact, validation serveur des événements,
   reprise après interruption et traitement des remboursements.
4. Boutique et conversion accessibles en jeu, reliées aux mêmes données et aux
   mêmes opérations, avec test sur le client 3.4.3 et Hermes retenus.
5. Validation de bout en bout, puis préparation du déploiement et des migrations
   avec sauvegardes, effets sur les services et accord de publication.

Les migrations d'authentification 0009/0010 en attente restent hors de ce jalon.
Le plafond de migration et la version publique du launcher n'ont pas changé.
