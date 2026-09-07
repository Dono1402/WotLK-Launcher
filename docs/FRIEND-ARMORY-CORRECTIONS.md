# Modèles, équipement et statistiques des amis

## Causes constatées le 6 septembre 2026

La liste des quatre personnages et les catalogues d'objets arrivaient bien de
l'API. La préparation locale attendait cependant l'export complet du premier
modèle avant de charger les icônes du suivant. Les personnages encore en attente
affichaient le message d'indisponibilité. Dans le cache observé, le personnage C n'était
prêt qu'environ 140 secondes après la liste initiale. Rouvrir le profil détruisait
en plus son cache et pouvait recommencer cette préparation.

Les statistiques détaillées avaient une seconde cause : `character_stats`
possède des valeurs sauvegardées que l'API et le lecteur local ne projetaient pas.
Les quatre personnages de l'ami étaient hors ligne et sans capture de combat ;
trois avaient une ligne `character_stats`, le quatrième n'en avait aucune.
L'utilisateur a confirmé avoir exécuté `.reload config` après le passage de
`AtlasArmory.OnlyGuid` à zéro. Les données absentes demandent encore une nouvelle
connexion du personnage pour que le collecteur réalise son premier relevé.

## Correctifs

- Charger les détails et icônes de tous les personnages avant les exports 3D.
- Identifier explicitement les modèles en préparation et donner la priorité au
  personnage sélectionné dès que l'export courant est terminé.
- Conserver les modèles entre visites et relances du même compte. Chaque
  ouverture exige un nouveau roster autorisé ; aucun roster du cache ne peut être
  affiché avant cette réponse. Déconnexion, changement de compte et retrait d'un
  ami purgent les caches concernés. La conservation après fermeture normale a
  été ajoutée par l'optimisation décrite dans `FRIEND-ARMORY-PERFORMANCE.md`.
- Exposer neuf champs sauvegardés supplémentaires dans l'API et le lecteur local.
  Les puissances de base restent distinctes des totaux de la capture de combat.
  Critiques physiques, esquive, parade, blocage et résilience sont conservés.
  Le champ de critique magique sauvegardé correspond à l'école physique dans le
  core actif : il n'est pas réinterprété comme une critique magique générale.
- Conserver les valeurs manquantes comme inconnues. Toucher, hâte, détail par école
  et autres valeurs du collecteur deviennent disponibles après une capture valide.

## Vérification

`artifacts/atlas-friend-assets-fix` contient les preuves suivantes :

- `real-assets-verification.json` : quatre exports réels depuis le client CASC,
  38 icônes accessibles en 4,7 secondes, 16 fichiers glTF et 109 ressources HTTP
  vérifiés. Le premier export reste coûteux : 61 secondes sur cette exécution,
  puis le personnage C traité en deuxième grâce à la priorité de sélection.
- `warm-cache-verification.json` : quatre modèles réutilisés, aucun export,
  25 ms de traitement local après réception d'un nouveau roster autorisé.
- `observed-assets-verification.json` et captures des quatre personnages :
  icônes décodées, meshes visibles, animations actives, aucune erreur JS/HTTP,
  viewport fixe 1598 × 997. Ces contrôles utilisent une copie des vrais assets.
- `test-final-node.log` : 34 tests ciblés réussis.
- `test-friend-cache.log` et `test-friend-profile-wpf.log` : réutilisation,
  isolation des comptes, nouveau helper RPC, refus de modification du profil ami,
  traductions, retrait d'ami et déconnexion/reconnexion.
- `../friend-stats-fix/armory-mysql.log` : vrais endpoints HTTP personnels et amis,
  MySQL 8.4.11 avec colonnes `INT UNSIGNED` et `FLOAT`, compatibilité avec les
  colonnes manquantes, absence de ligne et priorité des captures.

Le client local de cette première correction faisait 100 885 989 octets ; SHA256
`b2c731407cf2b4af7bf2b754f9491c9e7c00caacca437340ae3b9d108b8084b6`.
Il a été copié dans le dossier habituel après fermeture du launcher par
l'utilisateur, avec sauvegarde et comparaison des empreintes.

L'API corrigée est déployée dans `social-20260906T213516Z`. La preuve est dans
`artifacts/atlas-friend-assets-fix/deployment/deployment-result.json` : nouveau
processus 1732868, retour à une API saine en 957 ms, schéma 6 et configurations
conservés. La vraie lecture du compte ami confirme la remontée des valeurs
sauvegardées de puissance à distance de base et de critique à distance.

Un redémarrage indépendant d'Hermes a interrompu le premier contrôle, avant toute
modification d'API. Son nouvel état a été vérifié et consigné dans
`deployment/hermes-state-drift.json` et `deployment/cutover-baseline.json` avant
de reprendre. Cette intervention a uniquement redémarré l'API ; le processus WoW
est resté identique. Le contrôle final à 21:45:54 UTC retrouve les quatre ports de
jeu disponibles, le canary arrêté et aucune erreur API. Aucun envoi de message de
test aux utilisateurs.
