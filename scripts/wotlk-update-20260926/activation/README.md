# Activation du candidat du 26 septembre

Autorisation explicite : « vasy fait tout », après présentation de la sauvegarde,
des migrations, de la bascule et des vérifications. Ne pas utiliser cette procédure
pour un autre candidat ou relancer aveuglément une phase interrompue.

## Phases

1. `prepare-backup` : vérifier les cinq services et MySQL arrêtés, les empreintes
   du candidat et des fichiers de production ; copier le datadir MySQL à froid et
   comparer contenu et métadonnées par `rsync -aHnci`. Sauvegarder les configurations,
   unités, fichiers d'environnement, certificats, journaux et AccountData en privé.
   Préparer les configurations de production identiques et les trois overrides.
   Les répertoires Hermes `Logs` **et `PacketsLog`** doivent appartenir au compte
   `hermesproxy` en `0750` ; le reste de la livraison demeure en lecture seule.
2. `audit-backup` : revérifier séparément la couverture de toutes les unités,
   drop-ins et lignes `EnvironmentFiles`, même lorsqu'une propriété est répétée.
3. `migrate` : revérifier le backup à froid, démarrer uniquement MySQL, vérifier
   que l'updater Auth n'a aucune migration inconnue, réconcilier les 62 migrations
   World par nom et SHA-1. Les données personnages/guildes/pet_spell ne doivent pas
   changer à cette étape. Le script ne remet jamais une base en arrière tout seul.
4. `activate-core` : remplacer atomiquement le lien de configuration de test par
   celui de production, installer les overrides du 26 septembre, démarrer Auth et
   World. Les anciens overrides et exécutables sont conservés.
5. `open-services` : exiger les ports Auth/World et les deux heartbeats boutique,
   puis démarrer Hermes, l'API et le serveur du launcher. Hermes conserve les données
   de compte partagées hors du répertoire de livraison ; son service existant lance
   aussi la synchronisation des quêtes.
6. `verify` : contrôler les processus et leurs SHA-256, ports, healthchecks HTTP,
   realm et population connectée. Compléter par une observation des logs, de la
   charge, des réservations de guilde et des 1 000 bots après stabilisation.
7. `finalize.py` : conserver un bilan expurgé sans masquer les réserves connues.
   `close-credentials` contrôle une dernière fois l'état et retire uniquement le
   fichier client MySQL temporaire créé par cette procédure. Les phases utilisant
   ce fichier ne peuvent ensuite plus être relancées sans préparation explicite.

Tester les garde-fous avec `python test_deploy.py`. Les résultats, fichiers privés
et backups restent sous le répertoire serveur `deployment-20260926`, jamais dans Git.
Les anciens scripts de préparation imposent une production arrêtée : ils ne doivent
plus être lancés après la mise en service.

## Correctif ciblé des droits Hermes, sans redémarrage

`repair_packet_log_permissions.py` répare uniquement l'absence de `PacketsLog`
dans cette livraison : identité du binaire, répertoire courant et fichiers figés
contrôlés, refus d'écraser un chemin existant, création en `hermesproxy:hermesproxy`
`0750`, test d'écriture/fsync avec ce compte et vérification des PID inchangés.
Il ne modifie aucune configuration, ne désactive pas la capture et ne redémarre
aucun service. Le rapport de réparation est conservé ; ne pas relancer aveuglément.
La reconnexion réelle a ensuite été confirmée par l'utilisateur et recoupée avec
les journaux, voir le bilan du déploiement.

## Retour arrière : intervention supervisée uniquement

- Arrêter les cinq services applicatifs pour couper les écritures ; conserver les
  diagnostics. Ne pas arrêter les services sans rapport avec WoW.
- Avant toute restauration SQL, conserver une nouvelle copie de l'état en échec.
  Après ouverture aux joueurs, un retour à la sauvegarde ferait perdre les écritures
  depuis la bascule : évaluer/réconcilier ces données et demander une décision si
  cela dépasse l'autorisation initiale. Ne pas fusionner des datadirs MySQL.
- Arrêter `arthas-mysql`. Vérifier son identité et son montage `/opt/arthas/mysql`.
  La sauvegarde initiale est `deployment-20260926/private/mysql-cold-backup` : elle
  doit rester intacte. Préparer une nouvelle copie et la vérifier, puis conserver
  l'ancien datadir sous un chemin de quarantaine explicite avant de substituer la
  copie restaurée. Aucune suppression récursive et aucun rollback automatique.
- Retirer uniquement les trois overrides `zzzzzzzzz-atlas-all-update-20260926.conf`
  créés par cette procédure (Auth, World, Hermes), après avoir vérifié leur contenu.
  Les anciens overrides reprennent alors leurs chemins précédents. Recharger systemd.
- Restaurer AccountData seulement si nécessaire, en conservant sa version actuelle.
  Configurations, certificats et fichiers d'environnement originaux n'ont pas été
  modifiés : ne pas les écraser sans diagnostic.
- Redémarrer MySQL, Auth, World puis Hermes/API/launcher avec les mêmes contrôles.
  Une ancienne version binaire sur une base migrée n'est pas un rollback validé.

La copie froide est cohérente entre bases et vérifiée par checksum ; la restauration
de ce backup précis n'est pas répétée en production. Les copies isolées utilisées
pendant la validation avaient déjà démarré avec succès.
