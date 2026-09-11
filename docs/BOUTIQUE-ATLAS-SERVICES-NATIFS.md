# Services disponibles à la sélection des personnages

## État au 12 septembre 2026

L'achat de services pour le compte est implémenté et testé dans l'API et le
launcher, sur des données jetables. Les structures de paquets BattlePay/VAS de
Hermes sont maintenant vérifiées avec les fonctions de décodage du client
3.4.3.54261. Leur acheminement vers le cœur et la consommation en jeu restent
à implémenter et à vérifier. **Ce parcours
n'est pas prêt à être activé sur le royaume public.**

## Parcours demandé

Le 11 septembre 2026, pendant la préparation du déploiement, le parcours a été
réexaminé : un achat ne doit pas obliger à interrompre la partie. Le launcher
confirme l'achat ; le joueur retourne volontairement à la sélection des
personnages pour utiliser le service.

Le parcours confirmé par l'utilisateur est le suivant : le compte reçoit un
service disponible, puis son personnage bénéficiaire est choisi dans le
parcours natif du client. Aucun personnage n'est imposé au moment de l'achat.

Ce choix correspond au bouton de services demandé. Le schéma 0012 imposait un
personnage bénéficiaire dès l'achat. La migration 0013 permet maintenant de
conserver un service disponible sur le compte, sans bénéficiaire prédéfini.

## Achat et suivi implémentés

- Une commande `available` appartient au compte et au royaume. Son personnage
  vaut zéro et son nom est vide jusqu'à son utilisation. Le portefeuille,
  l'écriture du débit et le reçu restent dans une seule transaction.
- Une requête répétée retrouve la même commande, y compris après annulation ou
  après qu'un consommateur a renseigné un personnage et un nouveau nom.
- Un service disponible peut être annulé et recrédité une seule fois. Une
  commande `consumed` ne peut pas être annulée par l'API. La réservation de
  capacité du portefeuille inclut les services disponibles remboursables.
- Le launcher masque le choix du personnage pour ce mode, permet l'achat avec
  des personnages en ligne ou sans personnage, affiche le stock et explique
  le retour volontaire à la sélection. Le suivi conserve le mode du compte
  même si la lecture suivante échoue. Le passage vers la recharge n'impose pas
  de bénéficiaire ; la conversion d'or garde ses conditions sur le personnage
  source.
- L'historique conserve les anciens reçus attribués à un personnage. Pour les
  nouveaux services, un nom absent est exposé comme tel dans l'historique ; un
  reçu consommé affiche l'ancien et le nouveau nom.

Le nouveau mode requiert `AtlasShop:Purchases:AccountServicesEnabled=true` et,
pour ouvrir les achats, `RenameEnabled=true`, un plafond de schéma d'au moins
13 et une preuve de disponibilité récente du consommateur en protocole 2.
Le module actuel n'annonce que le protocole 1. Il ne peut donc pas ouvrir les
achats du nouveau mode. Les paramètres restent désactivés par défaut.

Le protocole 2 est une exigence de compatibilité réservée au futur consommateur,
pas une déclaration selon laquelle ce consommateur serait déjà implémenté.
Un nouveau launcher compatible devra aussi être distribué avant l'activation :
les anciennes versions ne connaissent pas les états `available` et `consumed`.

## Compatibilité du client vérifiée dans les sources

Le tag `3.4.3` du miroir des sources d'interface Blizzard pointe sur
`564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde`. Il contient :

- `CharacterSelect.lua` : lecture des services disponibles, création des boutons
  et affichage de leur quantité ;
- `Interface_TBC/GlueXML/CharacterSelect.xml` : le conteneur de ces boutons est
  placé au bord supérieur de la liste des personnages ;
- `CharacterServicesPaidNameChange.lua` : sélection du personnage, saisie du
  nouveau nom, validation puis confirmation de l'utilisation du service.

Sources : [boutons et services disponibles](https://github.com/Gethe/wow-ui-source/blob/564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde/Interface/GlueXML/CharacterSelect.lua),
[position du conteneur](https://github.com/Gethe/wow-ui-source/blob/564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde/Interface_TBC/GlueXML/CharacterSelect.xml),
[parcours du renommage](https://github.com/Gethe/wow-ui-source/blob/564ca565fd2de4d1bd4ca787d75d9f8c6d1ffcde/Interface/SharedXML/CharacterServicesPaidNameChange.lua).

Cela établit la présence du parcours dans les sources 3.4.3. Cela ne constitue
pas une observation graphique de l'exécutable 3.4.3.54261 installé ni une
validation de ses échanges avec Atlas. Le parcours natif consulté impose aussi
des règles côté client, notamment un niveau minimal de 10 pour le renommage
payant ; il faudra aligner les conditions affichées et celles du serveur.

## Écart avec Hermes et le module actuels

L'inspection de Hermes `f859d0c`, incluant le correctif séparé de fermeture des
connexions, ne trouve pas de gestionnaires ni de structures de paquets pour
alimenter et utiliser les services BattlePay/VAS. Leurs noms existent dans les
énumérations générales ; cela ne fournit pas leur implémentation. La table
3.4.3.54261 utilisée par ce fork est également incomplète pour ces échanges.

Les tests déjà réussis couvrent le renommage classique attribué à un personnage
par `AT_LOGIN_RENAME`, annoncé dans l'énumération des personnages puis consommé
par la commande native de renommage. Ils ne couvrent pas le bouton de services
disponibles du compte.

Pour ce bouton, il reste à implémenter et vérifier la liste des services, le
choix d'un personnage appartenant au compte, la validation du nom et la
consommation unique. Une annulation, un nom refusé, une requête répétée ou une
coupure réseau ne doivent pas perdre le service ni créer un second débit.

Les structures anciennes trouvées dans d'autres cœurs ne constituent pas une
preuve du format de cette version du client. L'analyse initiale de l'exécutable
sur disque était insuffisante : ses sections de code nécessitent une
initialisation. Une copie du seul exécutable a ensuite été initialisée sous
Wine, dans un conteneur privé sur Atlas, sans réseau, limité à un CPU et 1 Gio
de mémoire. Aucun compte, répertoire de données du jeu ou fichier personnel
n'était monté. Ce processus a été arrêté après la lecture de ses sections.
Le jeu installé sur le PC n'a pas été lancé. Aucun outil Computer Use n'a
été utilisé.

La lecture des sections initialisées a permis d'identifier les formats exacts.
Le correctif `mod-atlas-shop/patches/hermes-f859d0c-account-services.patch`
contient leurs structures et leurs tests, sans activer le service. Les messages
synthétiques produits par Hermes ont passé **12 vérifications** avec les
véritables fonctions de décodage du client émulées par Unicorn : listes de
0, 1, 2 et 100 services, service consommé/révoqué, listes vides du magasin,
liste des personnages et réponses de validation. La fonction de classement
du client reconnaît le produit comme `PaidNameChange` et masque les services
consommés ou révoqués. Son rappel de validation reçoit bien le jeton de la
demande et le résultat attendu.

Les **11 tests Hermes** supplémentaires couvrent aussi les demandes produites
par le véritable sérialiseur du client, dont le nom UTF-8, la distinction
validation/confirmation, chaque troncature et les octets superflus. Une
vérification explicite des longueurs évite d'accepter une chaîne tronquée.
Les exécutables et sections du client restent privés et hors Git. Le script
`mod-atlas-shop/tests/verify_native_protocol.py` ne contient que l'oracle de
vérification et exige la copie locale correspondante.

Cela valide le format des paquets, **pas l'affichage dans un client connecté
ni une consommation réelle**. L'acheminement, la notification dans le jeu et
le consommateur doivent encore être reliés et testés ; le message du launcher
est déjà présent.

## Sauvegardes et déconnexions

Une déconnexion forcée n'est pas une condition fonctionnelle de l'achat. Le
problème technique est d'éviter qu'une sauvegarde du personnage encore en cours
écrase une modification apportée par le service.

Le module actuellement validé attend la fermeture de toute la session du compte
et exige un worker CharacterDatabase unique. Le royaume public utilise quatre
workers. Ce paramètre n'a pas été modifié.

Une piste pour conserver les quatre workers consiste à suivre la durée de vie
des transactions produites par `Player::SaveToDB`, jusqu'à leur libération par
les workers SQL. L'inspection du cœur établit que le worker détruit son opération
après l'exécution de la transaction. Une référence faible par personnage
pourrait donc attendre ses sauvegardes sans bloquer celles des autres joueurs.
Cette piste n'est pas encore implémentée ni validée. Elle doit aussi couvrir
plusieurs sauvegardes simultanées, les transactions partagées, la reconnexion
et le retour à la sélection avant la fin d'une sauvegarde.

Le redémarrage du World et de Hermes évoqué pour l'installation initiale est
distinct de l'utilisation de chaque achat.

## Préparation indépendante déjà vérifiée

La répétition de migration exécute le véritable ancien binaire de l'API avec une
copie de la structure du schéma public et de son historique 0001–0008. Aucune
ligne de compte ou de personnage public n'est copiée. Un compte synthétique
est créé par cette API, puis le candidat applique les migrations jusqu'à 0013.

Les dix contrôles réussis vérifient notamment la conservation des échéances de
session, l'utilisation de l'ancien jeton d'accès, la rotation du jeton de
renouvellement, un redémarrage idempotent et le maintien des achats fermés en
l'absence de consommateur natif. Aucun solde ou achat n'est créé par la
migration. Une première répétition jusqu'à 0012 avait déjà réussi neuf
contrôles. Le script est `mod-atlas-shop/tests/test_api_upgrade.py` ; les
preuves et configurations jetables restent hors Git.

Les tests locaux MySQL 8.4 de sécurité des sessions passent avec **181 assertions**,
ainsi que les contrôles de plafonnement des migrations. Les corrections du banc
adaptent le nombre de migrations disponibles, l'attente sur un signal de
révocation déjà émis et l'ordre déterministe des sessions à expirer. Le code
d'authentification n'a pas été modifié.

Les vérifications spécifiques au nouveau mode passent également :

- **186 contrôles** de financement et **35 contrôles supplémentaires** sur les
  services du compte, avec l'API HTTP et MySQL 8.4 réels : migration et reprise,
  achats concurrents, absence de personnage, personnage en ligne, annulation,
  isolation des comptes, panne du journal, reprise après réponse perdue et
  protection de la capacité de remboursement ;
- **369 assertions** de comportement du launcher et des contrats ;
- **67 contrôles supplémentaires** du parcours ancien, avec le SQL du module
  réel et MySQL, pour vérifier sa compatibilité après les changements de l'API ;
- parcours WPF ancien et nouveau, exécutés hors écran dans une fenêtre
  inactive avec données fictives, sans lancer le jeu ; les vues françaises à
  1280 × 760 et anglaise à 1586 × 992 ont été inspectées.

Le test MySQL renseigne explicitement un reçu `consumed` pour vérifier le
comportement de l'API face à cet état. **Il ne simule pas une validation du
protocole natif, de l'application du renommage ou de la course entre
consommation en jeu et remboursement.** Ces vérifications attendent le vrai
consommateur.

Les paquets construits pendant cette préparation ne sont pas une livraison du
nouveau parcours de services natifs. Aucun remplacement de service public,
aucune migration publique et aucune publication du client n'ont été effectués.
Les changements provisoires de version 1.7 ont été retirés tant que le parcours
en jeu n'est pas prêt. Le candidat World construit précédemment correspond
encore au mécanisme ancien, avec un seul worker ; il n'a pas été démarré et ne
doit pas servir à activer les services du compte.

L'audit final confirme les mêmes processus et les mêmes exécutables publics
pour World, Auth, Hermes et l'API, avec quatre workers CharacterDatabase.
Les bases locales de test ont été supprimées et leurs processus arrêtés. Le
conteneur MySQL distant jetable est arrêté ; ses données et preuves sont
conservées pour la suite.

Un retour à l'ancien binaire API seul ne suffit pas après une migration publique
jusqu'à 0013 : cet ancien binaire ne connaît que le schéma 0008. Le retour arrière
devra préserver les sessions et les écritures effectuées après la bascule, en
particulier les soldes et commandes. La procédure finale reste à préparer pour
le parcours retenu.
