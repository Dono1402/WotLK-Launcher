# Conversion d’or en Crédits Atlas

Implémentée et vérifiée le 12 septembre 2026. La mise en service publique
nécessite encore une maintenance approuvée ; les tests décrits ici utilisent
des comptes et des personnages jetables dans un royaume isolé.

## Parcours

Le joueur choisit un personnage déconnecté, saisit un nombre entier de pièces
d’or, puis confirme la conversion. Le taux reste celui décidé pour la boutique :
**100 po = 1 € de Crédits Atlas**, soit 10 000 cuivres par centime de crédit.
Les pièces d’argent et de cuivre restantes restent sur le personnage.
Le portefeuille en euros est indépendant et ne change pas.

La conversion est définitive. Tous les personnages du compte doivent rester
déconnectés pendant le traitement ; une session à la sélection des personnages
est acceptée. Le joueur peut fermer la page pendant l’attente. Le launcher
retrouve le reçu après actualisation ou reconnexion et anime le crédit après
confirmation du serveur. Une réponse perdue conserve la même clé de demande.

Le changement de nom s’achète ensuite en Crédits Atlas, sans choisir de
personnage dans le launcher. Le joueur choisit son personnage et son nouveau
nom dans le service natif du jeu. Le prix actuel est de 7 € de Crédits Atlas,
soit 700 po converties.

## Autorité du royaume et conservation des soldes

L’API authentifie le compte, vérifie le devis et crée une demande durable
`pending` ; cette insertion ne retire aucun or et ne crédite aucun portefeuille.
Une seule demande peut être en attente par compte et le quota est de 100
demandes par période de 24 heures. Une demande non traitée expire après deux
minutes ; le royaume enregistre alors un refus sans débit.

Le module du royaume vérifie les sessions, le chargement et la déconnexion
des personnages. Il partage la barrière déjà utilisée par les services natifs :
les sauvegardes antérieures doivent être terminées avant le traitement, puis
la connexion et les modifications concurrentes sont exclues jusqu’au résultat
SQL. Les quatre workers de la base personnages sont conservés.

Une transaction MySQL unique, sur la même instance InnoDB, verrouille le
portefeuille, la demande et le personnage. Elle revérifie propriétaire,
suppression, état connecté, expiration, taux, or, dette et plafond. Elle retire
l’or, ajoute les crédits et écrit le reçu avec les quatre soldes avant/après.
Si l’une de ces écritures échoue, elles sont toutes annulées. Le reçu durable
sert également de journal de conversion et empêche un deuxième débit lors
d’une répétition. Les variables SQL de travail sont réinitialisées à chaque
tentative et à chaque reprise après interblocage.

Le plafond tient compte des crédits remboursables pour les services encore
inutilisés. Une conversion ne peut pas consommer cette réserve. L’API et le
royaume revérifient chacun cette condition sous le verrou du portefeuille.

## Interfaces et configuration

- Migration `0014_shop_gold_conversion.sql` : tables de demandes/reçus et de
  disponibilité du worker. Contraintes sur le devis, les deltas du reçu et
  l’unicité de la demande en attente. L’application valide aussi le schéma et
  l’expression générée au démarrage.
- `POST /api/v1/shop/conversions` : `idempotencyKey`, `characterGuid`,
  `offeredCopper`, `expectedCreditCents`, `catalogRevision`.
- `GET /api/v1/shop/conversions/{id}` : reçu limité au compte authentifié et au
  royaume configuré. Le snapshot boutique inclut les 100 dernières demandes
  et le journal des conversions terminées.
- API : `AtlasShop:GoldConversion:Enabled=true`, `RealmId=1` et plafond de
  migration 14. Le module publie un heartbeat de protocole 1, avec base de
  personnages et taux ; il doit dater de moins de 30 secondes.
- Royaume : `AtlasShop.Enable=1`, `AtlasShop.GoldConversion=1`, avec les hooks
  vérifiés de sauvegarde et de session. Les services natifs restent activés
  par `AtlasShop.AccountServices=1`.

Le rôle SQL du royaume doit lire les tables boutique, modifier les crédits du
portefeuille et les champs de résultat des demandes, et écrire le heartbeat.
Ses droits de modification de l’or dans la base personnages restent nécessaires.
Les nouveaux indicateurs sont désactivés par défaut. Les lectures et la reprise
idempotente restent possibles après suspension des nouvelles conversions.

## Vérifications effectuées

- 369 assertions existantes sur le runtime boutique.
- 18 contrôles du client : réponse perdue, répétition, conservation de la clé,
  retour tardif d’un ancien compte, attente retrouvée et reçus incohérents.
- 20 contrôles supplémentaires MySQL/API sur le schéma 0014 : reprise de DDL
  interrompu, dérive du schéma, authentification, propriété, devis, concurrence,
  absence de débit à l’insertion et maintien des lectures lorsque désactivé.
  Les 186 contrôles de financement et 35 contrôles de services natifs de cette
  même suite passent également.
- WPF synthétique, fenêtre inactive hors écran : boutons et saisies bloqués
  pendant l’envoi, fermeture sans annuler une opération déjà envoyée, reçu
  après navigation, saisie suivante, déconnexion et langues FR/EN. Aucune erreur
  de binding relevée.
- 30 contrôles sur le véritable core, MySQL, API et Hermes dans un réseau privé :
  six répétitions concurrentes d’une conversion, or devenu insuffisant après
  insertion, expiration, rollback sur échec du reçu, nouvelle tentative,
  connexion concurrente bloquée, sauvegarde après jeu, plafond et remboursement,
  redémarrage réel du processus World et reprise du reçu.

Le scénario complet démarre avec **0 crédit**, convertit **2 000 po en 20 €**,
achète le changement de nom pour **7 €**, sélectionne le personnage via les
paquets natifs 3.4.3, valide et confirme un nom Unicode, puis ouvre une nouvelle
connexion SSO/Hermes. Le nom persiste, les crédits restent à **13 €**, le
portefeuille à **12,34 €**, et le personnage conserve ses **500 po, 67 cuivres**.

Le client de jeu graphique n’a pas été ouvert : la chaîne de protocole et les
écritures réelles sont vérifiées, sans observation manuelle de son écran.
Ce test ne constitue pas un essai de charge à 1 500 joueurs/bots.

## Mise en service et retour arrière

Préparer un nouveau binaire World avec son propre répertoire de configuration,
l’API contenant la migration 0014 et le launcher correspondant. Sauvegarder
les configurations et les bases concernées avant activation. La maintenance
remplace puis redémarre **World et API** ; elle déconnecte les joueurs du royaume
et interrompt brièvement l’API. Le binaire Hermes existant est compatible.

En cas de retour arrière après migration, conserver l’API compatible avec le
schéma 0014 et désactiver les nouvelles conversions. Revenir à l’ancien World
reste possible après drainage des transactions. Ne pas supprimer les reçus,
restaurer un ancien portefeuille ou remettre l’or par une deuxième écriture.
Les demandes encore en attente sont conservées et expirent sans débit quand
un worker compatible reprend leur traitement.
