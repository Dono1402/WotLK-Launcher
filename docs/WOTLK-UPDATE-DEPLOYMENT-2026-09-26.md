# Déploiement WotLK du 26 septembre 2026

## Résultat

Le lot préparé et testé a été activé après l'autorisation explicite de l'utilisateur
(« vasy fait tout »). MySQL et les cinq services applicatifs sont redémarrés.
Le bilan du **26 septembre à 05:59 UTC / 07:59 à Paris** est **opérationnel avec
réserves connues**, et non « tous les défauts corrigés ».

**Complément après essai réel du client :** à 06:35 UTC, l'authentification réussit,
mais Hermes interrompt la connexion faute d'accès au dossier `PacketsLog`. Le défaut
de déploiement a été corrigé à 06:38 UTC sans redémarrage, puis l'utilisateur a
confirmé que la connexion fonctionne. Voir le détail ci-dessous : le bilan initial
de disponibilité ne constituait pas une validation de connexion en jeu.

- **1 000 bots aléatoires connectés**, cible inchangée à 1 000.
- **3/3 bots des guildes de joueurs connectés et réservés** sans expiration.
- Auth, World, Hermes, API launcher et serveur launcher actifs ; aucun redémarrage
  automatique depuis la bascule, exécutables réellement chargés vérifiés par SHA-256.
- **62 migrations World appliquées**, aucune migration Auth en attente ou différente.
- Ports Auth/World/Hermes, deux heartbeats boutique et deux healthchecks locaux OK.
- API publique `https://animeclub.fr/wotlk/health` : HTTP 200 et statut `ok`.
- Connexions TLS Hermes sur 8081 et 1119 : certificat de confiance, expiration
  au 20 octobre 2026, négociation TLS 1.2 vérifiée.
- Aucun crash, défaut de permission ou deadlock SQL 1213 détecté dans l'observation.
- **Réserve importante : trois transactions de sauvegarde de familiers de bots ont
  échoué avec une erreur SQL 1062**, voir ci-dessous.

Les [preuves structurées](update-deployment/2026-09-26/deployment-summary.json)
couvrent 383 secondes depuis le lancement du nouveau World, dont la reconnexion
progressive des bots. Ce n'est ni un test d'endurance ni une validation manuelle
depuis le client graphique. Aucun launcher ou jeu n'a été ouvert sur le PC.

Le [contrôle de clôture à 06:00:55 UTC / 08:00:55 à Paris](update-deployment/2026-09-26/closure-summary.json)
confirme encore 1 000 bots, les mêmes processus sans redémarrage et toujours trois
erreurs SQL 1062. Le fichier client MySQL temporaire de déploiement a ensuite été
retiré ; les sauvegardes privées restent protégées sur le serveur.

## Versions réellement déployées

| Élément | Version / contrôle |
| --- | --- |
| Core | Amont `7f12e89ee5f4`, intégration Atlas `99ece3bc2b53` |
| Playerbots | Amont `7bae1b5c58c7`, intégration Atlas `8f35348ff120` |
| Dungeon Clear | `805b909c7286` |
| AH Bot | Amont `c11d8318cbd8`, intégration Atlas `44dd82e78e5d` |
| Hermes | Amont `5ea8767f0edb`, intégration Atlas `089e747beac8` |
| Auth | Nouvel exécutable du même candidat que World |
| API launcher | Exécutable installé conservé, SHA-256 `203484f27efc…` |
| Serveur launcher | Service Node existant redémarré, application non mise à jour |

Les modules Atlas, Transmog et Account Achievements, ainsi que les adaptations
Atlas d'AH Bot, sont conservés comme décrit dans le rapport de préparation.
Hermes utilise la livraison .NET autonome validée, **pas NativeAOT**.

Pour comprendre les apports : [changements expliqués par dépôt](WOTLK-UPSTREAM-CHANGES-2026-09-26.md)
et [index des 318 commits](WOTLK-UPSTREAM-COMMITS-2026-09-26.md).

## Sauvegarde et conservation

La sauvegarde a été effectuée avec MySQL et tous les services applicatifs arrêtés :
elle est cohérente entre les bases, contrairement à un ensemble de dumps successifs
réalisés avec des écrivains actifs.

- Copie froide complète de `/opt/arthas/mysql`, vérifiée par `rsync -aHnci`
  (contenu et métadonnées), puis revérifiée avant le démarrage de MySQL.
- 242 fichiers sauvegardés : configurations, unités/drop-ins, fichiers
  d'environnement, certificats, données de compte Hermes et journaux précédents.
- Les anciens exécutables et overrides restent disponibles.
- 169 fichiers originaux suivis sont toujours identiques ; les 10 fichiers de
  configuration du nouveau répertoire de production sont copiés sans changement
  de valeurs. Le lien de configuration du candidat ne pointe plus vers le banc de test.
- Réglages conservés : minimum/maximum de bots 1 000, réservation de guildes activée,
  suppression des comptes/guildes/équipes de bots désactivée, threads de carte inchangés.
- Après migrations et avant lancement de World, checksums inchangés des tables
  `characters`, `guild`, `guild_member` et `pet_spell`.
- Données de compte Hermes partagées hors du répertoire de livraison ; nouveau
  répertoire de logs accessible au compte de service. La synchronisation habituelle
  des quêtes est exécutée par la dépendance du service Hermes.
- Deux bases de test de nouveau arrêtées ; seule la base de production reste active.
- Environ 61 Gio libres après la bascule. Pas de suppression des anciennes versions.

Sauvegarde serveur :
`/opt/arthas-next/candidates/atlas-all-update-20260926/deployment-20260926/private/mysql-cold-backup`.
Le backup précis de retour arrière n'a pas fait l'objet d'une restauration répétée ;
les copies isolées de la production utilisées pendant la préparation avaient, elles,
démarré et reçu les migrations avec succès.

La [procédure d'activation et de retour arrière](../scripts/wotlk-update-20260926/activation/README.md)
prévoit de conserver l'état post-bascule avant toute restauration. Aucun rollback
SQL automatique : après réouverture, restaurer le backup ferait perdre les nouvelles
écritures sans réconciliation préalable.

## Connexion client : dossier de captures Hermes corrigé

Les tentatives du 26 septembre à 06:35:43 et 06:35:50 UTC atteignent bien
`authenticated successfully`, puis échouent avec `UnauthorizedAccessException` :
`Access to the path '.../hermes-all-update-20260926/PacketsLog' is denied`.
Le refus survient dans `SniffFile` lors de la création du journal de paquets,
et remonte dans les lectures WorldClient/WorldSocket. Il ne s'agit pas d'un refus
des identifiants ni du problème indépendant de sauvegarde des familiers.

La livraison était volontairement en lecture seule pour le compte Hermes. `Logs`
était correctement préparé, mais pas `PacketsLog`, alors que la capture de paquets
était déjà activée dans la configuration conservée. La création du dossier n'était
exercée qu'à la connexion d'un vrai client, pas par les healthchecks.

Le [correctif vérifié à 06:38:04 UTC / 08:38:04 à Paris](update-deployment/2026-09-26/packet-log-permissions-repair.json)
crée uniquement ce dossier avec propriétaire/groupe `hermesproxy`, droits `0750`.
Un fichier temporaire anonyme a été créé, écrit et synchronisé avec le vrai compte
de service ; le test réussit sans produire de fausse capture réseau.

- Aucun redémarrage : tous les PID et compteurs de redémarrage sont inchangés.
- Binaires, paramètres, mots de passe et fichiers de configuration inchangés.
- Répertoire de livraison toujours non inscriptible pour le service, seuls les
  répertoires de données nécessaires le sont.
- Procédure corrigée pour préparer **Logs et PacketsLog** ; 12 tests unitaires
  des garde-fous passent, dont les cas d'ajout et de refus de chemin existant.
- L'utilisateur confirme ensuite : **« La connexion fonctionne »**.

Le [contrôle de reconnexion à 06:39:32 UTC](update-deployment/2026-09-26/packet-log-reconnect-confirmation.json)
constate une authentification client réussie après correction, deux captures réseau
non vides créées et aucune nouvelle erreur de permission. Hermes conserve son PID
initial, sans redémarrage. Les captures privées ne sont ni lues ni ajoutées au dépôt.

## Familiers : incident confirmé, correction non livrée

Trois erreurs `Duplicate entry ... for key pet_spell.PRIMARY` ont été observées lors
de la montée en charge. Les trois familiers identifiés appartiennent à des comptes
de bots de type 1. Trois transactions sont annoncées annulées, avec six requêtes non
exécutées chacune. Le chemin de sauvegarde concerné englobe auras, sorts et cooldowns ;
ce n'est donc pas un simple message à ignorer.

L'ancien lot présentait déjà un incident de cette famille. La comparaison de
`Pet::_SaveSpells` confirme que la réécriture complète du grimoire existait déjà dans
la version installée : ce déploiement **n'apporte pas de correction de ce mécanisme**.
Aucune ligne de production n'a été supprimée, aucune clé primaire contournée, aucun
`INSERT IGNORE` ou nettoyage arbitraire appliqué.

Une reproduction SQL synthétique, dans le conteneur de test sans réseau, a montré :

1. Avec InnoDB et `READ-COMMITTED`, deux transactions peuvent supprimer en parallèle
   un grimoire initialement absent.
2. La première insère et valide ; la seconde reçoit alors une erreur 1062 en insérant
   la même clé.
3. La même réécriture exécutée séquentiellement réussit.

Le test confirme **un mécanisme de concurrence compatible**, pas la trace complète
des appels C++ des incidents réels. Le nombre d'erreurs est resté à trois entre
05:55 et le bilan de 05:59 UTC. Cela ne prouve ni leur disparition durable ni
l'impossibilité d'affecter un familier de joueur humain.

Suite recommandée : reproduire les appels concurrents du chemin C++ et valider
une sérialisation/atomicité correcte de la sauvegarde avant une nouvelle livraison.
Changer globalement l'isolation MySQL ou masquer les doublons ne constitue pas une
correction démontrée. Ce travail n'est pas inclus dans les corrections livrées ici.

## Autres limites et vérifications de la procédure

Les cinq échecs de navigation Dungeon Clear sont identiques à ceux de l'ancien lot :
un trajet de Cime d'Utgarde, une distance de staging de Fosse de Saron et trois
assertions d'Épuration de Stratholme. Les tests ne sont ni désactivés ni présentés
comme réussis ; voir le [rapport isolé](WOTLK-UPDATE-PREPARATION-2026-09-26.md).

La procédure a été ajustée sur des détails de contrôle, sans correction de données :
propriété systemd absente ou répétée, healthcheck Node en texte brut `ok`, événement
de déconnexion nul représenté par l'absence de ligne. Neuf tests unitaires des
garde-fous passent. La première tentative de préparation s'est arrêtée avant toute
copie/migration et ses traces ont été conservées. La couverture de tous les fichiers
d'environnement a ensuite été auditée séparément avant les migrations.

Les captures préliminaires d'erreurs comptaient deux motifs sur une même ligne ;
le compteur final compte une seule ligne par erreur et confirme trois erreurs,
pas six. Les résultats finaux ne remplacent pas les traces brutes privées.
