# Validation du renommage sur un vrai royaume isolé

Le 11 septembre 2026, un `worldserver` Linux incluant `mod-atlas-shop` a été
lié et démarré sur Atlas, avec une API du launcher et des bases de test séparées.
Le client de test utilise le protocole natif 3.3.5 : création de personnages,
sélection, renommage et suppression passent par les vrais gestionnaires du cœur.
Les reçus de livraison et la consommation du renommage ne sont plus simulés.

Cette validation complète les tests SQL et WPF décrits dans
[le dossier du renommage](BOUTIQUE-ATLAS-RENOMMAGE.md). Elle ne constitue pas une
mise en production ni une validation graphique du client 3.4.3 via Hermes.

## Résultat vérifié

**28 contrôles API/World réussis**, avec deux véritables redémarrages du World
de test. Le dernier passage complet s'est terminé le 11 septembre à 19:52:31 UTC.
L'API publiée pour Linux a été compilée sans avertissement ni erreur.
Le binaire de test contient les neuf inscriptions de modules attendues ; son
SHA-256 est `cdb7215c202a47bbce6219c0bcb12f875a1da9e192f9afef06b34df89c42ed8d`.

Le dernier scénario bloque réellement une ligne de personnage dans MySQL,
observe la transaction de livraison en attente, puis reconnecte un client qui
envoie son renommage avant la fin de l'initialisation de session. Sur ce cœur,
la commande reste différée : reçu, nom et indicateurs restent inchangés avant
déblocage ; la livraison est ensuite validée et le renommage natif réussit.
Ce scénario vérifie l'ordre effectif des opérations. Il ne prétend pas avoir
observé un refus explicite de chacun des opcodes protégés par le module.

Les processus API/World de test et le conteneur MySQL jetable ont été arrêtés.
Les PID du World public, de l'authserver et de Hermes sont restés identiques,
avec leurs compteurs de redémarrage à zéro. Les preuves
reproductibles sont `build/manifest.json`, `native-test-result.json` et les
journaux de la fixture, conservés hors Git. Les fichiers de distribution
restent désactivés par défaut ; aucun code de livraison n'a dû être corrigé
pendant cette validation.

## Environnement et isolation

- Répertoire dédié : `/opt/atlas-shop-tests/rename-20260911`.
- Cœur Linux : `edfd79c4839c7c5a9fc6fc067ee0b627a8ee9a64`, recette de lien du
  candidat Atlas du 9 septembre. Les objets Dungeon Clear, Chat, Armory et les
  inscriptions des modules existants restent dans l'exécutable de test.
- Seuls le module boutique, son chargeur, le chargeur global, `Main.cpp` et
  `Config.cpp` sont recompilés. Le dernier fixe le chemin des configurations
  de modules dans le dossier de test : `--config` seul ne suffit pas à le déplacer.
- Les sources et objets existants sont lus, sans modification ni installation.
  Le manifeste enregistre la recette, les hashes des nouvelles sources/objets,
  le hash du binaire et les signatures des entrées avant/après la construction.
- Compilation limitée à un CPU, 3 Gio, sans swap ni réseau. Le couple API/World
  utilise un réseau privé systemd, au plus 1,5 CPU et 4 Gio, sans swap.
- MySQL 8.4 provient de l'image déjà présente, dans un nouveau conteneur
  `--network=none`, limité à un CPU et 1 Gio. Aucun port n'est publié.
- Une passerelle TCP **dans le réseau privé** relie API/World au socket Unix
  du MySQL de test. Elle évite un comportement du pool de ce cœur qui remplace
  `.` par `localhost` après sa première connexion et perd ensuite le socket
  personnalisé. Le pilote du cœur n'a pas été modifié pour ce test.
- Les quatre bases sont `shop_test_auth`, `shop_test_chars`, `shop_test_world`
  et `shop_test_playerbots`. Auth/personnages/bots ne reçoivent aucune ligne
  de compte ou de joueur provenant du royaume public. Les données statiques
  World viennent de l'ancien staging ; la saison d'arène reçoit la valeur
  initiale du cœur, nécessaire au démarrage d'une base de personnages vide.
- Les tables Atlas de l'auth ne sont pas importées sans leur historique :
  l'API applique réellement ses migrations 1 à 12 sur une base neuve.
- Les mots de passe et jetons sont propres au test, dans des fichiers privés
  hors Git. Les identifiants SQL de lecture du conteneur source restent en
  mémoire, sans apparaître dans les arguments ou les journaux.
- Les modules sans rapport avec la boutique sont désactivés par configuration,
  de même que les bots, mises à jour automatiques, console distante et SOAP.
  Les paramètres habituels du royaume public ne sont pas repris.

## Scénarios

Le banc utilise l'inscription et les endpoints boutique de la vraie API.
Seuls les soldes fictifs, un autre indicateur de service et une clé de session
du compte synthétique sont préparés directement dans les bases de test.

Les vérifications portent sur :

- le signal de disponibilité émis par le module réellement chargé ;
- la création de deux personnages par `CMSG_CHAR_CREATE`, puis leur lecture
  par le launcher ;
- quatre achats HTTP simultanés avec la même clé : un seul débit ;
- l'attente tant que la session reste à la sélection des personnages ;
- l'indépendance des soldes euros/crédits et l'annulation remboursée une fois ;
- la reprise d'une commande après un véritable arrêt/démarrage du World ;
- la livraison atomique du bit de renommage et la conservation des autres bits ;
- la présence de l'indicateur dans `SMSG_CHAR_ENUM` ;
- le refus d'un nom invalide sans consommation du droit ;
- l'acceptation d'un nom valide par `CMSG_CHAR_RENAME`, sa persistance et
  la consommation native du droit ;
- les reprises HTTP et natives, puis un second redémarrage, sans nouveau
  débit ni réactivation d'un service consommé ;
- un second achat en crédits livré et consommé par le cœur ;
- la suppression native d'un bénéficiaire en attente, puis le rejet de
  livraison et le remboursement par le worker de l'API ;
- la reconnexion pendant une transaction volontairement bloquée dans MySQL.
  La finalisation normale de session attend le worker des personnages ; le
  banc envoie aussi un renommage anticipé et vérifie qu'il ne peut réussir
  avant la validation de la livraison, qu'il soit différé par le cœur ou refusé
  par la protection du module.

## Reproduction

Ce banc est lié aux entrées Linux Atlas citées ci-dessus. Ce n'est pas un
installateur générique d'AzerothCore. Les scripts refusent les racines de travail
extérieures à `/opt/atlas-shop-tests/rename-*` et ne remplacent pas une fixture
MySQL déjà existante.

1. Créer un nouveau dossier privé sous cette racine ; y copier uniquement
   `mod-atlas-shop/src`, `conf` et les quatre scripts de `tests` ci-dessous.
2. Exécuter `build_realm_linux.py --root <dossier>` dans une unité temporaire
   avec `PrivateNetwork=yes`, `ProtectSystem=strict`, `ReadWritePaths=<dossier>`,
   `NoNewPrivileges=yes`, `CPUQuota=100%`, `MemoryMax=3G`, `MemorySwapMax=0`
   et une priorité basse. Il n'exécute ni World ni SQL.
3. Exécuter `prepare_realm_fixture.py --root <dossier>` avec accès au Docker
   local. Les exports de source n'utilisent aucun verrou de table ; seules
   les bases du nouveau conteneur reçoivent des écritures. Le script conserve
   son identifiant exact pour l'arrêt ultérieur.
4. Publier `source/WotLK.Launcher.Server/WotLK.Launcher.Server.csproj` avec
   `dotnet publish -c Release -r linux-x64 --self-contained true -p:NuGetAudit=false`
   dans `api-linux` sous le dossier de test, puis rendre l'exécutable Linux exécutable.
5. Exécuter `run_realm_fixture.py --root <dossier>` dans une nouvelle unité
   temporaire avec `PrivateNetwork=yes`, `PrivateTmp=yes`, `ProtectSystem=strict`,
   `ReadWritePaths=<dossier>`, `NoNewPrivileges=yes`, `CPUQuota=150%`,
   `MemoryMax=4G`, `MemorySwapMax=0`, `LimitCORE=0`, `RuntimeMaxSec=1200`.
   Le script génère les configurations à partir des `.dist`, lance l'API,
   attend sa santé, lance World et appelle `test_native_realm.py`. Les processus
   qu'il possède sont arrêtés dans son bloc de sortie, y compris après erreur.
6. Lire `native-test-result.json` et les journaux privés. `passed` n'est vrai
   qu'après la fin de tous les contrôles ; un nouveau lancement le remet à faux.
7. Exécuter `prepare_realm_fixture.py --root <dossier> --stop`. L'identifiant,
   le nom, l'isolation réseau et le montage de données du conteneur sont
   recontrôlés avant l'arrêt. Les données et preuves restent sur place.

Le nom du conteneur est volontairement réservé à ce banc : une fixture déjà
présente interdit une nouvelle initialisation. Le mode `--hold` conserve
temporairement API/World pour un diagnostic, avec une limite de 45 minutes ;
il ne produit pas un résultat de validation réussi.

## Limites avant mise en service

La clé de session native est provisionnée dans le compte jetable : le parcours
SRP de l'authserver, la traduction Hermes et le client graphique 3.4.3 ne sont
pas couverts. Aucun launcher installé ni client du joueur n'a été ouvert.
L'entrée effective dans le monde, une sauvegarde de déconnexion d'un joueur
en jeu et les interactions de jeu avec les autres modules restent à vérifier.
Les avertissements liés au staging World et aux modules désactivés ne valent
pas une validation générale du contenu du royaume.

Avant activation publique, il reste un passage avec le client 3.4.3 via Hermes
sur un environnement de test accessible de façon contrôlée, puis la préparation
et l'autorisation de la mise en service. Les deux activations boutique restent
désactivées par défaut dans les fichiers distribués.
