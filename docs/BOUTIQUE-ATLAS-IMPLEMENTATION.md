# Boutique Atlas : catalogue, soldes et conversion locale

État au 11 septembre 2026. Ce jalon fournit les écrans WPF, le catalogue
authentifié, deux soldes cliquables, le convertisseur intégré, une page de préparation
des recharges et un historique filtrable. Les quatre services ont des prix distincts
selon la monnaie, confirmés le 11 septembre. Les améliorations ergonomiques
approuvées le même jour relient désormais chaque fiche à la préparation de sa
recharge, avec conservation du bénéficiaire et comparaison directe des deux soldes.
Le mode de prévisualisation peut simuler un débit d'or et un crédit Atlas en mémoire.
Le jalon [recharges manuelles](BOUTIQUE-ATLAS-RECHARGES-MANUELLES.md) ajoute maintenant
un portefeuille et un journal persistants, des demandes PayPal et leur validation
administrateur, désactivés par défaut. Le jalon [achat de renommage](BOUTIQUE-ATLAS-RENOMMAGE.md)
ajoute le débit persistant, le suivi, l'annulation et le module de livraison
du changement de nom, également désactivés par défaut. Les paiements automatiques,
le débit d'or d'un véritable personnage et les trois autres services restent à intégrer.
Aucun service de production ni client installé n'a été modifié.

## Fonctionnement présent

- Onglet **Boutique** reprenant la référence de catalogue avec des cartes
  compactes en grille, illustration panoramique en haut, nom du
  service, description commerciale puis deux prix nommés. Quatre cartes compactes tiennent sur une rangée
  de 1080 à 1672 DIPs, sans défilement ; la grille peut revenir à trois/deux
  colonnes si son espace utile descend sous 1000/720 DIPs.
  Les quatre cartes appartiennent au catalogue partagé entre le serveur et l'aperçu.
- Tarifs approuvés, partagés entre le serveur et l'aperçu : changement de nom
  **5 € portefeuille / 7 € Crédits Atlas**, sésame niveau 70 **60 € / 70 €**,
  changement de faction **35 € / 45 €**, changement de race **20 € / 30 €**.
  Chaque carte affiche les deux lignes, leurs libellés et icônes : bleu pour le
  portefeuille, doré pour les Crédits Atlas. La fiche reprend la monnaie choisie
  et son montant dans le récapitulatif ; le portefeuille est sélectionné par défaut.
- Cliquer une carte ouvre une fiche dédiée : illustration, contenu du service,
  éléments conservés, conditions, personnage bénéficiaire et récapitulatif fixe.
  Deux blocs sélectionnables affichent chacun la monnaie, le prix, le solde
  disponible et la somme manquante éventuelle. Le choix du
  paiement n'apparaît que pour un service tarifé. Retour et Échap restaurent
  le catalogue et sa position de défilement. La navigation supérieure reste accessible.
- Page et données en français/anglais ; conservation de la sélection lors du
  changement de langue et de l'actualisation. Un personnage disparu ne peut
  pas être remplacé silencieusement par un autre bénéficiaire.
- **Crédits Atlas et portefeuille en euros visibles simultanément** dans la
  barre supérieure, immédiatement à gauche de Messages, sur toutes les pages.
  Le libellé supérieur « Euros » est remplacé par **Portefeuille**.
  Cliquer **Crédits Atlas** ouvre la conversion ; cliquer **Portefeuille** ouvre
  la page de recharge, depuis tous les onglets. Les contrôles d'authentification
  et de navigation restent appliqués. Leur groupe passe sur deux lignes en largeur compacte. Les deux sont exprimés
  en centimes entiers. Avec le schéma 0011 disponible, le serveur lit les
  portefeuilles persistants ; sinon les soldes restent `null`, affichés `—`.
- Raccourci **Convertir mon or** placé à droite au-dessus des services,
  dans la zone demandée, avec l'illustration de pièces fournie. Ses dimensions
  restent de 260/280 × 72 DIPs et il est accessible sans défiler. Une pièce
  d’or apparaît immédiatement après le nombre du taux ; une infobulle précise
  « 100 pièces d’or = 1,00 € de Crédits Atlas » pour le taux actuel.
  Le libellé **Crédits Atlas** est également visible sous le taux, sans survol.
  Il remplace le catalogue par une interface
  centrée dans la page du launcher. Présentation horizontale : or disponible à
  gauche, montant numérique au centre, flèche et Crédits Atlas en euros à droite.
  La navigation et les soldes restent accessibles ; **Retour à la boutique**
  et Escape restaurent le catalogue. Choix séparé du
  personnage source, maximum en pièces d'or entières propre à ce personnage,
  saisie entière sans argent/cuivre, bouton Max, curseur et raccourcis
  25/50/75 %, crédit obtenu, or restant et nouveau solde Atlas. Max reste l'unique
  raccourci pour convertir la totalité disponible. Les personnages connectés n'exposent pas
  de solde d'or périmé et ne peuvent pas convertir.
- Page **Créditer mon portefeuille** : montant libre en euros, raccourcis de
  saisie 5/10/20/50 €, choix carte bancaire / PayPal / Bancontact avec leurs logos
  (Visa et Mastercard pour la carte), solde actuel
  et récapitulatif du montant et du solde envisagé. Ces raccourcis ne définissent
  ni packs commerciaux, ni minimum de recharge. La saisie conserve exactement
  les centimes et refuse les montants invalides ou dépassant le plafond.
  Sans activation serveur, le formulaire reste un brouillon. Lorsque les
  recharges manuelles sont configurées, PayPal permet de créer une demande
  persistante sans crédit immédiat ; les limites serveur sont affichées.
  Carte et Bancontact restent fermés. Le joueur dispose de la référence,
  du lien PayPal.Me, de l'annulation et de l'actualisation du statut.
  Retour/Échap retrouve le service d'origine lorsqu'il existe, sinon le catalogue ;
  une déconnexion efface le montant et le moyen choisi.
- Page **Historique des opérations**, accessible depuis le catalogue et le
  portefeuille : filtres par type (recharge, conversion, achat, remboursement)
  et monnaie, date locale, personnage éventuel, montant signé, statut et solde
  après opération lorsqu'il est connu. Retour/Échap retrouve la page d'origine.
  La liste affiche au maximum les 100 opérations les plus récentes. L'aperçu
  fournit trois exemples fictifs et ajoute les conversions réussies en mémoire.
  Avec le schéma 0011, l'API fournit les recharges validées et reprises de
  paiement du compte. Sans ce schéma, l'historique demeure indisponible.
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

## Parcours de financement et simplifications du 11 septembre

Une fiche propose **Ajouter les 50,00 € manquants** si, par exemple, le sésame
à 60 € est choisi avec un portefeuille de 10 €. La recharge s'ouvre à 50 € avec
le nom du service et du bénéficiaire. **Retour au service** restaure exactement
l'offre, le personnage et la monnaie ; consulter l'historique depuis cette
recharge conserve aussi ce retour. Cette préparation reste un brouillon.

**Obtenir les crédits manquants** ouvre le convertisseur avec la quantité d'or
nécessaire, arrondie au nombre entier de pièces suffisant selon le taux, puis
plafonnée à l'or disponible sur le personnage source. Celui-ci peut être différent
du bénéficiaire ; le choix du bénéficiaire n'est jamais remplacé. Un personnage
source déconnecté possédant de l'or est proposé si la source initiale ne peut
pas convertir. Le manque restant est recalculé après chaque conversion simulée.
Exemple : 7 € requis, 2,65 € disponibles et 900 po sur la source donnent une saisie
de 435 po, puis 7 € de Crédits Atlas. Si la source ne possède que 423 po, la saisie
est limitée à 423 et ne prétend pas couvrir l'intégralité du service.

Le retour conserve des identifiants, sans garder d'anciennes lignes de données
ni autoriser un achat. Une offre ou monnaie supprimée ramène au catalogue ; un
personnage supprimé laisse le bénéficiaire à choisir ; un tarif modifié est
signalé. Une sortie par la navigation générale, une déconnexion ou une erreur
d'actualisation efface ce contexte. Un solde inconnu est affiché **indisponible**,
sans être assimilé à zéro et sans proposer un montant de recharge inventé.

Les fiches distinguent **Ce service comprend**, **Ce que vous conservez** et
**Conditions d'utilisation**. Le contenu du sésame en équipement/compétences et
les règles complémentaires de race/faction restent explicitement à définir.
L'état d'éligibilité expose les faits disponibles : personnage à choisir,
personnage connecté, ou niveau 70 déjà atteint pour le sésame. Ce dernier cas
désactive la préparation du financement. Les restrictions non exposées par
l'API ne sont pas présentées comme validées : **Éligibilité à confirmer**.

Le récapitulatif service/personnage/monnaie/total et la confirmation désactivée
restent fixes au bas de la fiche, y compris pendant le défilement à 1080×680.
Le bouton de conversion indique le débit exact, par exemple **Convertir 212 po**,
avec **Vous recevrez 2,12 € de Crédits Atlas**, puis conserve la confirmation reçue.
Le résultat porte **Crédits Atlas à recevoir** avant confirmation et **Crédits
Atlas reçus** après réussite ; une nouvelle saisie retrouve le premier libellé.
Les titres et explications utilisent le pluriel **Crédits Atlas**. La mention
**Recharge bientôt disponible**, avec une icône d'horloge, apparaît immédiatement
sous le titre de recharge, avant le formulaire ; son ancien doublon inférieur est retiré.
Tous les montants du portefeuille sont bleus ; les Crédits Atlas sont dorés.
Les noms et icônes accompagnent ces couleurs. Historique gagne un bouton plus
lisible et les descriptions compactes passent à 13 DIPs.

Le surtitre ATLAS LAUNCHER du catalogue, le logo et WRATH OF THE LICH KING répétés
sur les cartes, l'or sauvegardé dans la fiche d'achat et le bouton 100 % ont été
retirés. L'or reste visible dans le convertisseur. L'aide **Deux soldes indépendants**
se déplie au clavier ou au clic dans les trois formulaires et remplace les longs
paragraphes répétés. Le catalogue conserve ses quatre cartes et son espace libre.

L'actualisation isolée a été remplacée par le chargement automatique à l'ouverture
de Boutique, ou lors de l'entrée depuis une autre page par l'un des soldes de
l'en-tête. Les conversions simulées actualisent immédiatement les soldes et le
journal. **Réessayer** est visible après indisponibilité, erreur réseau ou limitation
de requêtes. Les recharges manuelles disposent maintenant d'une actualisation
du statut ; les notifications automatiques du prestataire restent à intégrer.

## Présentation et ressources

La grille du 11 septembre a été compactée après retour sur la taille des cadres.
Sa largeur est plafonnée à 1440 DIPs, les espacements sont de 18 DIPs et le
bloc de texte de chaque carte mesure 188 DIPs, pour intégrer la description
commerciale et deux lignes de prix distinctes. Les quatre services
sont visibles d’emblée, avec des cadres de 244 à 346 DIPs de large aux tailles
contrôlées, au lieu des grandes cartes de 3/2 colonnes précédentes.
Le titre Boutique partage la taille adaptative 48/42/38, la graisse et le
dégradé TitleIce d’Addons et Notes de version. Le mode vient directement du
Shell, avec les mêmes marges ; le surtitre de marque a été supprimé du catalogue.
Les illustrations occupent une zone 16:9. Les cartes utilisent les surfaces
vitrées bleu nuit, les bordures bleues et les coins arrondis de 14 DIPs du launcher,
avec des titres nacrés et des tarifs bleus/dorés selon la monnaie. Les surfaces grises de la référence
ont été remplacées. Les illustrations restent intactes, avec des angles supérieurs
arrondis par le contrôle WPF.

La fenêtre du launcher conserve son format fixe configuré (1597,6 × 996,8 DIPs,
`ResizeMode="CanMinimize"`). Les tailles supplémentaires exercées par les tests
sont des contrôles de robustesse. Les dernières finitions de libellés et de
disponibilité ne modifient ni ce format, ni les illustrations, ni la composition des fiches.

Le raccourci de conversion est aligné à droite de l'en-tête du catalogue, au-dessus
de la grille. Le défilement reste disponible si le catalogue s’agrandit. Le catalogue,
la fiche de service, le convertisseur, la recharge et l'historique sont des vues exclusives
de la même page. Cliquer l'un des soldes supérieurs active directement sa vue.
Quitter la conversion pour la recharge annule aussi tout transfert animé en
attente avant le débit/crédit simulé.

Le décor et le logo proviennent de l'archive fournie
`Atlas_Boutique_Assets_Pour_Codex.zip`. Les cartes utilisent quatre illustrations
de 1672 × 941 créées avec ImageGen : deux nouvelles compositions pour le sésame
et la race, deux versions redessinées à partir des ressources du nom et de la
faction. Les PNG originaux sont conservés. Provenance, modes, prompts et
empreintes : [ressources](../source/WotLK.Launcher/Assets/Shop/README.md)
et [illustrations générées](../source/WotLK.Launcher/Assets/Shop/GENERATED-ART.md).

Les textes, prix, choix et interactions restent des contrôles WPF, avec Inter
et ClearType/Ideal. La barre supérieure est commune à Jeu, Addons et Boutique.
Le bouton Convertir ouvre une page intégrée, sans comportement modal ; sa
confirmation reste visible même lorsque son contenu central doit défiler.

Le serveur et l'aperçu utilisent le même catalogue `ShopServiceCatalog` et le
même taux partagé de 100 po par euro. La révision du catalogue couvre les
offres, leurs textes commerciaux et le taux. Le schéma 2 conserve la possibilité
d'offres sans prix, mais les quatre offres actuelles ont chacune deux tarifs.
Les champs facultatifs `Tagline`, `Preserved` et `History` complètent ce même schéma :
un ancien catalogue sans accroche utilise sa description en remplacement ; sans
`Preserved`, la fiche indique que les éléments conservés seront précisés avant
l'ouverture. Ce texte bilingue est limité à 1 000 caractères par langue.

## Présence des services dans le serveur

Vérification en lecture seule du code AzerothCore local au commit
`f67b86df8bec0d06b76ad17a9512f08d615f2057`, le 11 septembre :

| Service | Base présente dans le core | Travail avant vente |
| --- | --- | --- |
| Changement de nom | Commande de renommage et `AT_LOGIN_RENAME` | Commande boutique durable, facturation et test du parcours complet |
| Sésame niveau 70 | `HandleCharacterLevelCommand` / `HandleCharacterLevel` permettent une modification administrative du niveau | Définir éligibilité et contenu du sésame, puis livraison durable ; une commande GM n'est pas un service boutique |
| Changement de faction | `AT_LOGIN_CHANGE_FACTION = 0x40` et `HandleCharFactionOrRaceChange` | Relier la boutique, confirmer ou compléter Hermes, vérifier les restrictions et transformations avec le client |
| Changement de race | `AT_LOGIN_CHANGE_RACE = 0x80` et le même gestionnaire | Relier la boutique, confirmer ou compléter Hermes et vérifier les combinaisons race/classe |

Fichiers examinés : `src/server/scripts/Commands/cs_character.cpp`,
`src/server/game/Entities/Player/Player.h` et
`src/server/game/Handlers/CharacterHandler.cpp`. Le gestionnaire du core
contrôle notamment l'appartenance du personnage, sa déconnexion, le flag
accordé et la compatibilité race/classe.

Dans la copie Hermes Atlas `01667dc` disponible localement et dans les fichiers
de personnages de l'upstream `4247d957` référencé par le manifeste de préparation
du 9 septembre, les gestionnaires de renommage sont présents. Aucun
gestionnaire race/faction n'a été identifié dans ces deux fichiers
[Server/CharacterHandler.cs](https://github.com/Xian55/HermesProxy/blob/4247d957b78e6621047783560b3606f8fcf7ff42/HermesProxy/World/Server/PacketHandlers/CharacterHandler.cs)
et [Client/CharacterHandler.cs](https://github.com/Xian55/HermesProxy/blob/4247d957b78e6621047783560b3606f8fcf7ff42/HermesProxy/World/Client/PacketHandlers/CharacterHandler.cs).
Cela motive une vérification du proxy retenu avant activation ; ce contrôle
ne prouve pas l'absence dans tout autre fichier ou dans le binaire actif.
Aucun parcours de changement de race/faction avec le client réel n'a été testé.

Les quatre services restent désactivés par défaut. Le parcours d'achat et la
livraison du renommage sont désormais implémentés et documentés dans
[le jalon renommage](BOUTIQUE-ATLAS-RENOMMAGE.md). Les trois autres services
attendent leurs règles métier et leur livraison durable.

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

Le catalogue est assemblé par `ShopCatalog` à partir de `ShopServiceCatalog`.
Sa révision dépend des offres et du taux de conversion. Les tarifs peuvent être configurés via :

```text
AtlasShop:Rename:EuroCents = 500
AtlasShop:Rename:CreditEuroCents = 700
```

Les tarifs non positifs et les montants hors limites empêchent l'initialisation
du catalogue. Les endpoints d'achat et d'annulation du renommage sont décrits
dans le jalon dédié ; la conversion réelle n'a pas encore d'endpoint.
Le DTO partagé est dans `WotLK.Launcher.Shop.Contracts`, **schéma 2**. Les devises
de prix sont `credits` et `eur` ; `gold` n'est plus accepté. Les deux champs
`CreditBalanceEuroCents` et `EuroBalanceCents` sont indépendants et facultatifs.
Un client de ce jalon refuse le schéma 1 : le déploiement futur doit donc
coordonner les versions serveur/client. Le client limite les
réponses à 256 Kio, y compris sans `Content-Length`, valide le schéma et refuse
les réponses d'une session devenue obsolète pendant le chargement. Une
déconnexion efface le catalogue, le personnage, les montants saisis, les soldes,
l'historique et ses filtres.

`History = null` signifie indisponible ; une liste vide signifie qu'aucune
opération n'est enregistrée. Les entrées ont un identifiant unique, une date UTC,
un type, une monnaie, un montant signé en centimes, un statut et une description
FR/EN. Personnage, solde après opération et or converti sont facultatifs selon
le type. Les doublons, listes de plus de 100 lignes, montants hors limites et
combinaisons incohérentes sont refusés. Les achats et reprises de paiement
(`payment-reversal`) sont négatifs, les autres types positifs ; seul un statut
terminé peut exposer un solde après opération.
Lire ce journal ne modifie jamais les soldes. Les changements de compte,
erreurs d'authentification et réponses tardives ne peuvent conserver les lignes
de la session précédente. La persistance, les routes de recharge et les
contrôles d'administration sont détaillés dans le jalon
[recharges manuelles](BOUTIQUE-ATLAS-RECHARGES-MANUELLES.md).

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
résultat et son entrée d'historique jusqu'à la fin de la session de prévisualisation.
Le journal initial contient une recharge fictive de 15 €, un achat de nom à 5 €
et une conversion fictive de 265 po en 2,65 € de Crédits Atlas, cohérents avec
les soldes affichés. Les achats restent
désactivés. En mode connecté réel, la conversion reste fermée. Les contrôles
hors écran testent aussi l'isolation de cette
route et son incompatibilité avec une autre prévisualisation dédiée.

Le lancement normal utilise l'API configurée du launcher. Tant que le nouveau
backend n'est pas déployé, il peut afficher la boutique indisponible.

## Vérifications réalisées

SDK local : .NET 8.0.424. Compilation du launcher, du serveur et des tests
d'intégration sans erreur ni avertissement. Les trois suites boutique suivantes
ont été exécutées après les améliorations ergonomiques.

| Suite | Résultat et portée |
| --- | --- |
| `--shop` | 339 assertions : deux soldes indépendants, déficits exacts et financement plafonné, retour au bénéficiaire/monnaie d'origine, conversion partielle, tarif/offre/monnaie/personnage modifiés, erreur et réponse tardive après changement de compte, éligibilité connue et solde inconnu ; huit tarifs, journal, filtres, précision et fermeture des opérations réelles conservés |
| `--shop-wpf <dossier>` | 82 captures PNG ; catalogue, fiches, conversion, recharge et historique en FR/EN aux quatre tailles ; blocs de monnaie sélectionnables et accessibles, prix/disponible/manque visibles, récapitulatif fixe, retours de financement natifs et Échap, recharge à 50 €, conversion à 435 po depuis une source distincte, aide dépliable, actualisation à l'entrée et Réessayer ; contrôles existants de saisie, Max, animation, historique et session conservés ; aucune erreur de binding |
| `--shop-mysql` | API HTTP réelle et MySQL 8.4.11 jetable sur loopback ; huit tarifs exacts et détails bilingues des éléments conservés, historique réel indisponible, authentification, appartenance Atlas, personnages autorisés, soldes, taux, absence de mutation, limites et erreurs ; deux bases de test supprimées et instance locale arrêtée |
| `--shell-navigation-wpf` | 898 assertions relancées après ce jalon : navigation et panneaux, raccourcis, focus, changements rapides, gardes modales, listes virtualisées et sélection conservée à 1440×860 et 1080×680 |

Après les deux dernières finitions (libellés de conversion et disponibilité
de la recharge), `--shop` et `--shop-wpf` ont été relancés avec les mêmes résultats.
Les captures de recharge et de conversion avant/après réussite ont été relues
dans le grand format. Les contrôles API/MySQL et de navigation ci-dessus datent
du jalon ergonomique précédent ; ces finitions ne changent ni ces contrats ni la navigation.

Les jalons précédents ont également validé `--armory-session` (session, refus du
refresh, 401, reconnexion au même compte ou à un autre, réponse tardive et
annulation) et `--startup-routing` (prévisualisation boutique isolée et démarrage
réel inchangé). Ces deux suites n'ont pas été relancées pour ce jalon ergonomique.

Les fenêtres graphiques sont synthétiques, inactives et hors écran. Les captures
du catalogue, des fiches et des parcours de financement ont été relues, y compris
des vues compactes FR/EN et le solde final après conversion. Les captures qui
couvrent exactement le manque de 4,35 € utilisent une source fictive à 900 po ;
le lancement d'aperçu standard conserve son exemple à 423,5067 po, avec une
saisie limitée à 423 po. Aucun launcher utilisateur, jeu ou navigateur n'a été ouvert.
Aucun serveur de production n'a été modifié.

## Suite avant activation

1. Valider le module de renommage sur un royaume de test jusqu'au choix effectif
   du nouveau nom dans le client. Relier ensuite les autres achats et la conversion
   au même portefeuille, avec leurs commandes durables et identifiants idempotents.
2. Étendre le module du core pour contrôler l'or et livrer les autres services.
   Tester les connexions concurrentes et les interruptions
   entre débit, sauvegarde et confirmation ; aucune écriture directe de l'or
   d'un personnage connecté depuis l'API.
3. Configurer et autoriser l'activation des recharges manuelles, ou intégrer un
   prestataire automatique compatible avec le compte choisi. La reprise et
   la gestion des remboursements devront aussi couvrir les notifications externes.
4. Boutique et conversion accessibles en jeu, reliées aux mêmes données et aux
   mêmes opérations, avec test sur le client 3.4.3 et Hermes retenus.
5. Validation de bout en bout, puis préparation du déploiement et des migrations
   avec sauvegardes, effets sur les services et accord de publication.

La migration 0011 du portefeuille est ajoutée ; les migrations d'authentification
0009/0010 ne sont pas modifiées. Le plafond de production et la version publique
du launcher n'ont pas changé. Leur application future exige une préparation
explicite de l'ensemble des migrations encore en attente.
