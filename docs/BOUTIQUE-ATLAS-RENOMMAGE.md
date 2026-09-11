# Achat et livraison du changement de nom

Implémentation locale du 11 septembre 2026, désactivée par défaut. Le launcher peut
acheter un changement de nom à **5 € du portefeuille** ou **7 € de Crédits Atlas**.
L'API débite le solde choisi et crée une commande persistante. Le nouveau module
`mod-atlas-shop` accorde ensuite le droit natif de renommage à la sélection du
personnage. Le joueur choisit le nom dans le client du jeu ; les règles de nommage
restent celles du cœur.

Les autres services, la conversion d'or réelle et les paiements automatiques
restent fermés. Aucun serveur de production, compte réel, installation du joueur
ou dépôt du cœur n'a été modifié pour ce jalon.

## Parcours du joueur

1. Choisir explicitement son personnage et une monnaie, puis confirmer le prix
   affiché. Les deux soldes ne sont jamais additionnés.
2. Revenir à l'écran de connexion du jeu. La simple sélection des personnages
   conserve une session sur le royaume et ne suffit pas : le module attend que
   le compte ait quitté le royaume, y compris les sessions conservées hors ligne.
3. Le launcher actualise les commandes en attente toutes les cinq secondes
   lorsqu'il affiche la boutique. L'actualisation manuelle reste possible.
4. Après **Activation livrée**, revenir à la sélection du personnage et choisir
   le nouveau nom. Cette confirmation porte sur le droit accordé, pas sur la
   consommation effective du service.

Le joueur peut annuler une commande encore en attente. Son solde est recrédité
dans la monnaie débitée, une seule fois. Une activation livrée n'est plus annulable.
Si le personnage disparaît, change de propriétaire ou reçoit déjà un renommage
avant livraison, le module rejette la commande et l'API la rembourse automatiquement.
Cette reprise fonctionne même si le launcher est fermé.
L'historique propose aussi un suivi des services et leur annulation, y compris
si le bénéficiaire n'apparaît plus dans la liste des personnages. Les commandes
non terminées sont prioritaires dans la liste bornée renvoyée par l'API.

Le suivi conserve le nom d'origine et la référence de la commande. Le journal
financier enregistre séparément le débit et l'éventuel remboursement. Un débit
comptabilisé ne signifie pas que la livraison est déjà terminée.

## Garanties et fonctionnement

- `0012_shop_orders.sql` crée les commandes, leur journal et les signaux de
  disponibilité du module. La migration 0011 reste inchangée. Les identifiants
  de requête sont uniques par compte et un personnage ne peut avoir qu'une
  commande en attente par royaume.
- Le compte vient de la session authentifiée. Le serveur contrôle l'offre,
  le prix et la révision du catalogue, la propriété du personnage, son état
  hors ligne, l'absence de renommage natif et le solde réellement disponible.
  Les sommes gelées sont exclues et une dette bloque tout nouvel achat.
- Le verrou du portefeuille sérialise les achats, recharges et remboursements.
  Débit, commande et journal sont validés dans la même transaction. Une réponse
  perdue se reprend avec la même clé, même si les ventes ont depuis été suspendues.
- La livraison utilise une seule mise à jour InnoDB touchant les deux bases :
  `characters.at_login |= 1` et `order.status = 'delivered'`. Elle conserve les
  autres bits. Un reçu déjà livré empêche toute réactivation après consommation.
- Le module attend l'absence de session du compte, puis bloque brièvement les
  paquets de connexion, suppression et services de personnage pendant la
  transaction. Il libère cette protection après son résultat, y compris si sa
  configuration est désactivée entre-temps.
- La transaction passe par la **file d'écriture CharacterDatabase à un worker**,
  après les sauvegardes déjà en attente. Cela évite qu'une sauvegarde de
  déconnexion réécrive les anciens indicateurs après livraison. Le nombre de
  workers est mémorisé au premier chargement : un simple rechargement de config
  ne peut pas rendre compatible un pool démarré avec plusieurs workers.
- Les remboursements en euros couvrent d'abord une dette apparue depuis l'achat.
  Les recharges préservent la place nécessaire aux remboursements des commandes
  encore annulables, même près du plafond du portefeuille. Les Crédits Atlas
  restent indépendants ; toute future source de crédits devra respecter ce
  même principe de capacité réservée.
- Lecture bornée à 100 commandes et 100 événements ; création limitée à
  100 commandes par compte sur 24 heures, plus la limite HTTP existante.

## Préparer un royaume de test

Cette procédure décrit une activation future et n'a pas été exécutée sur le
royaume de production. Le module doit être installé dans `modules/mod-atlas-shop`
de la version du cœur concernée, puis le `worldserver` doit être reconstruit.
Les sources locales du cœur ont uniquement servi à la compilation de contrôle.

Prérequis :

- un seul worldserver propriétaire du royaume et de sa base de personnages ;
- MySQL 8.0+ pris en charge par le migrateur Atlas, tables InnoDB, bases auth et
  characters sur la même instance ;
- `CharacterDatabase.WorkerThreads = 1` dès le démarrage du worldserver ;
- schéma Atlas 0012 migré et validé, base de personnages complète du cœur ;
- compte SQL de `CharacterDatabase` autorisé à lire/modifier les commandes auth
  et à écrire le signal de disponibilité, en plus de ses droits habituels ;
- compte SQL de `LoginDatabase` autorisé à lire la file des commandes ;
- compte SQL de l'API autorisé sur les tables boutique et en lecture sur les
  personnages. L'API ne modifie jamais les indicateurs du personnage.

Exemple de configuration API, volontairement désactivée :

```json
{
  "AtlasShop": {
    "Purchases": { "RenameEnabled": false, "RealmId": 1 },
    "Rename": { "EuroCents": 500, "CreditEuroCents": 700 }
  }
}
```

Le fichier `mod_atlas_shop.conf.dist` conserve `AtlasShop.Enable = 0`. Les deux
activations sont nécessaires. L'API exige aussi un signal récent du module,
avec le même royaume, la même base de personnages et le protocole 1. Ce signal
expire après 30 secondes. Le module vérifie InnoDB et les droits d'écriture
avant de l'émettre. Les commandes antérieures et leur annulation restent
accessibles lorsque les nouveaux achats sont suspendus.

Avant une ouverture réelle, valider sur le royaume de test la reconstruction et
le chargement du module, l'achat avec chaque monnaie, la déconnexion complète,
une reconnexion pendant la livraison, le choix du nouveau nom dans le client,
la conservation des autres services et la reprise après redémarrage. Une
compilation d'objets et les tests SQL ne remplacent pas ce passage en jeu.

## Vérifications reproductibles

- Construire `source/WotLK.Launcher.IntegrationTests` avec
  `-p:AtlasLocalClientBuild=true -p:NuGetAudit=false`.
- Exécuter `--shop`, `--migration-ceiling`, `--shop-wpf <dossier>` et
  `--shop-rename-wpf <dossier>`. Les tests WPF utilisent une fenêtre synthétique
  hors écran, inactive, sans session ni installation de jeu.
- `mod-atlas-shop/tests/Test-CoreSyntax.ps1` compile le module et son chargeur
  contre les vrais en-têtes du cœur, avec `-CoreRoot`, `-ZigPath`, `-BoostRoot`
  et éventuellement `-Playerbots`. Il ne lie ni ne lance le worldserver.
- `mod-atlas-shop/tests/Run-MySqlTests.ps1` reçoit `-ZigPath`, `-MySqlRoot`,
  `-MySqlConfig` et `-DotNet`. Il exige une installation MySQL 8.4 de test déjà
  présente dans les artefacts du dépôt, configurée sur `127.0.0.1:13307`, sans
  mot de passe root et sans exposition réseau. Il refuse tout port occupé.
  Il compile l'émetteur SQL depuis le même en-tête C++ que le module, lance
  `--shop-rename-mysql`, vérifie le retrait des deux bases jetables et arrête
  uniquement le processus qu'il a lui-même démarré. Il n'installe rien.

La suite MySQL réutilise d'abord les 186 contrôles du financement manuel, puis
teste l'achat réel via HTTP et le client du launcher, le SQL exact de livraison,
les requêtes simultanées, les erreurs injectées, les fonds gelés, les dettes,
l'annulation, les reprises et la non-réactivation après consommation simulée.
Le signal du module et la consommation native sont simulés dans cette base
isolée. Aucun nom n'a été changé dans un client de jeu réel pendant ces tests.

Résultats vérifiés pour ce jalon : compilation .NET sans avertissement ni erreur,
364 assertions runtime, 186 contrôles MySQL du financement puis 67 contrôles du
renommage, suites WPF catalogue/renommage/financement sans erreur de liaison,
routage de démarrage et plafond des migrations validés. Le module et son
chargeur compilent contre le cœur local `f67b86df8bec0d06b76ad17a9512f08d615f2057`,
avec et sans les en-têtes Playerbots. Les deux avertissements C++ observés
proviennent de fmt/G3D dans les dépendances du cœur. Les bases de test ont été
retirées et le MySQL temporaire arrêté après chaque exécution.
