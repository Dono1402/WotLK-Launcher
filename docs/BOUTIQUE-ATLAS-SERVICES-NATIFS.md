# Services disponibles à la sélection des personnages

## État au 12 septembre 2026

Le parcours **acheter un service pour le compte dans le launcher, puis choisir
le personnage en jeu** est implémenté et validé sur le royaume isolé. L'API,
Hermes 3.4.3, le cœur World et MySQL ont effectué de vrais achats, validations,
renommages, consommations et remboursements sur des comptes jetables.

**La production n'a pas été modifiée. Le rendu graphique du bouton et de son
parcours n'a pas été observé dans le client installé.** Aucun outil Computer Use
ni lancement du jeu sur le PC n'a été utilisé. Les exécutables de test restent
dans la fixture ; ils ne constituent pas une version publique du launcher.

## Parcours livré

- L'achat crée une commande `available` du compte et du royaume, sans personnage
  imposé (`character_guid=0`). Il est permis sans personnage ou pendant la partie.
- Le joueur peut continuer à jouer. Une notification indique la disponibilité
  du service ; son utilisation nécessite un retour volontaire à la sélection.
- Hermes fournit au client natif les services disponibles et les personnages
  du compte. Le parcours natif valide le nom puis confirme le renommage.
- Le cœur vérifie le propriétaire, le royaume, le niveau minimal de 10,
  l'absence de personnage en cours de chargement/jeu, les règles de nom et les
  services déjà en attente. Le nom et le reçu passent ensemble à l'état final.
- Aucun débit supplémentaire n'a lieu à la consommation. L'historique du
  launcher expose le personnage, l'ancien nom et le nouveau nom.
- Un service disponible peut être annulé et remboursé une fois. L'annulation
  envoie aussi une révocation native pour retirer le service du cache du client.
- Une confirmation répétée retrouve son reçu. Un service consommé ne peut
  ni être remboursé ni être utilisé avec un autre personnage ou nom.

Ce parcours conserve les **quatre workers CharacterDatabase**. Il ne nécessite
pas de fermer la session du compte, BNet ou le launcher après chaque achat.

## Vérifications réalisées

| Vérification | Résultat |
| --- | --- |
| Cœur natif et vraie API/MySQL, avec 4 workers asynchrones et 1 synchrone | 36 contrôles réussis |
| SSO launcher, BNet, transport AES-GCM 3.4.3, Hermes, cœur et MySQL | 19 contrôles réussis |
| Nouveaux tests unitaires Hermes : paquets et passerelle | 18 tests réussis |
| Paquets Hermes décodés par les fonctions réelles du client émulées par Unicorn | 14 vérifications réussies |
| Barrière C++ : plusieurs sauvegardes, transaction partagée, callback antérieur, durée de vie côté worker | Réussi |

Les 36 contrôles du cœur couvrent notamment les noms invalides et occupés,
les comptes étrangers, le niveau minimal, les services conflictuels, la
validation sans consommation, le reçu, la reprise et un redémarrage réel.
Une panne SQL provoquée annule à la fois le renommage et le reçu. Les deux
ordres de la course remboursement/consommation sont contrôlés avec un vrai
verrou MySQL : une seule opération gagne, l'autre préserve le résultat.

Le scénario de sauvegarde fait réellement entrer puis déconnecter un Player.
Une ligne verrouillée retarde sa transaction de sauvegarde alors que le client
est déjà revenu à la sélection. Le service attend cette transaction ; un autre
compte peut encore créer un personnage. Après libération, le renommage Unicode
réussit et résiste à une nouvelle entrée, déconnexion et sauvegarde.

Les 19 contrôles Hermes vérifient les paquets du sélecteur natif, l'activation
du drapeau de services après le signal du cœur, les noms Unicode, la révocation,
la consommation, l'achat pendant le jeu, le retour à la sélection avec la
session du compte ouverte, puis une coupure de socket et une reconnexion SSO.
Un rafraîchissement redondant après une confirmation déjà traitée a été retiré
pour conserver la limite ordinaire des demandes de liste de personnages.

La campagne Hermes complète forcée en 3.4.3 compte 1 180 tests : 1 177 passent,
deux échecs LFG existent déjà sur la base inchangée et un test de métriques
attend un séparateur décimal anglais alors que le processus utilise le français.
Ces trois tests ne sont pas présentés comme réussis et n'ont pas été modifiés.

Les validations antérieures de l'API/launcher restent distinctes : 186 contrôles
de financement, 35 propres aux services de compte, 369 assertions de contrats
et comportement du launcher, 67 contrôles du SQL ancien, 181 assertions de
sécurité des sessions et 10 contrôles de migration depuis l'ancienne API.
Les vues WPF ont été inspectées hors écran avec des données fictives.

## Construction et fichiers

- `mod-atlas-shop/src/atlas_shop_native.cpp` : protocole interne, authentification
  par WorldSession, liste des services, validation, consommation et reçus.
- `atlas_shop_native_sql.h` : verrou du portefeuille avant la commande,
  renommage et reçu dans une transaction, littéraux UTF-8 encodés en hexadécimal.
- `atlas_shop_barrier.h` : références faibles aux sauvegardes/transactions et
  demandes de noms antérieures. Leur expiration suit leur fin effective.
- `tests/patch_native_core.py` : copie de travail de trois fichiers du cœur
  Atlas, avec contrôle SHA-256 des sources. Les sources actives restent intactes.
- `patches/hermes-f859d0c-account-services.patch` : passerelle et paquets Hermes,
  à appliquer **après** `hermes-f859d0c-disconnect-owner.patch` sur `f859d0c`.
  L'application successive des deux correctifs a été vérifiée.

Les hooks couvrent `Player::SaveToDB`, les opérations de création/nom, la durée
de vie de WorldSession et une limite propre de 20 requêtes de service par
session et par 10 secondes. Une seule consommation est en cours globalement.
Pendant son court commit/contrôle de reçu, les autres créations ou changements
de nom peuvent recevoir un refus temporaire ; les autres comptes peuvent
continuer à jouer et entrer dans le monde. Si la lecture du reçu échoue après
la transaction, la protection du nom reste en place jusqu'au retour de MySQL.

Le module requiert les hooks versionnés et refuse une liaison sans ceux-ci.
Copier uniquement le module dans un cœur non adapté ne suffit pas. La recette
de construction est propre au cœur Atlas épinglé ; ce n'est pas un installateur
universel d'AzerothCore. Les écritures manuelles SQL et commandes GM qui changent
les noms en dehors des chemins de joueur couverts restent hors du périmètre.

## Reproduction dans la fixture

Racine privée : `/opt/atlas-shop-tests/rename-20260911`. Réutiliser son MySQL
jetable existant, sans réinitialiser ses données ni exposer de port. Les comptes
et fonds sont synthétiques. Aucun compte/personnage public n'y est importé.

1. Copier les sources du module et ses scripts dans la fixture. Exécuter
   `build_realm_linux.py --root <racine> --output-name build-native` dans une
   unité `PrivateNetwork=yes`, `ProtectSystem=strict`, `ReadWritePaths=<racine>`,
   `MemoryMax=4G`, `MemorySwapMax=0`, `CPUQuota=100%`, avec priorité basse.
   `--reuse-verified` ne réutilise que des objets dont les sources, en-têtes,
   options, signatures des entrées et hashes correspondent au manifeste.
2. Publier l'API dans `api-account-services`, puis exécuter
   `run_realm_fixture.py --root <racine> --account-services --api-package api-account-services`
   sous la même isolation réseau/fichiers, avec `MemoryMax=3G`, sans swap et
   `CPUQuota=150%`. Le script lance puis arrête ses propres API/World.
3. Publier Hermes corrigé dans `hermes-native` ; ajouter
   `--with-hermes --hermes-package hermes-native` pour le scénario 3.4.3 complet.
   La fixture ajoute alors son Auth et son Hermes, et les arrête aussi à la fin.
4. Vérifier `native-account-services-result.json`,
   `hermes-account-services-result.json` et `build-native/manifest.json`.
   Les rapports ne passent à `passed=true` qu'après tous leurs contrôles.
5. Arrêter le seul conteneur enregistré avec
   `prepare_realm_fixture.py --root <racine> --stop` ; conserver les preuves.

Les 14 contrôles de formats utilisent `tests/verify_native_protocol.py`, les
paquets produits par les tests Hermes et les sections privées du client
3.4.3.54261. Ils exécutent ses parseurs, son classement `PaidNameChange` et son
rappel de résultat sous émulation CPU. Les seuls remplacements sont les
allocateurs et copies mémoire. Aucun code binaire du client n'est versionné.

Le parcours existe dans les [sources de CharacterSelect 3.4.3](https://github.com/Gethe/wow-ui-source/blob/564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde/Interface/GlueXML/CharacterSelect.lua)
et du [renommage payant](https://github.com/Gethe/wow-ui-source/blob/564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde/Interface/SharedXML/CharacterServicesPaidNameChange.lua).
La vérification de protocole ne remplace pas l'observation de ce parcours à l'écran.

## Mise en service restante

Les paquets de test sont prêts et vérifiés. La bascule publique doit utiliser
une version propre du launcher/API/Hermes/World, avec les configurations et
droits de production contrôlés, une sauvegarde et une procédure de retour.
Le binaire World de la fixture embarque son chemin de configuration de test :
**ne pas le copier directement à la place du World public**. Le candidat
préparé auparavant pour l'ancien parcours à un seul worker est lui aussi obsolète.

L'installation initiale nécessite un redémarrage de World et Hermes, donc une
déconnexion des joueurs, ainsi qu'un redémarrage bref de l'API. L'authserver
n'a pas besoin d'être remplacé. Les quatre workers CharacterDatabase restent
à quatre. Cette coupure d'installation est indépendante de l'usage des achats.

Avant ouverture, le module doit avoir `AtlasShop.Enable=1` et
`AtlasShop.AccountServices=1`. L'API requiert un plafond de schéma 13,
`AtlasShop:Purchases:AccountServicesEnabled=true` et `RenameEnabled=true`.
Les achats restent fermés tant que le cœur n'annonce pas un signal protocole 2
récent après contrôle des tables InnoDB et droits SQL. Les valeurs distribuées
restent désactivées par défaut. Le magasin Blizzard reste désactivé.

La migration publique passe de 0008 à 0013 ; sa répétition a vérifié les sessions
existantes et la rotation des jetons. Après migration, l'ancienne API 0008 ne
peut pas être simplement remise en place : elle refuse le schéma plus récent.
Le retour opérationnel doit garder l'API compatible, fermer les achats et
préserver soldes/reçus ; il peut rétablir les anciens World/Hermes. Ne pas
restaurer une ancienne base par-dessus des écritures financières ou sessions
créées après la bascule. Le détail de la bascule publique reste à finaliser.

Après cette campagne, les processus de fixture et son MySQL sont arrêtés.
L'audit public retrouve les mêmes PID, exécutables, configurations et quatre
workers pour World, Auth, Hermes et l'API. Aucune migration publique, publication
du launcher ou modification du jeu installé n'a été effectuée.
