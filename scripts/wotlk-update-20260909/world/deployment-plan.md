# Bascule Dungeon Clear du 9 septembre 2026

État du document : recette préparée, en attente de la revue et du GO final du responsable. Aucun arrêt, dump, changement d'unité ou démarrage candidat n'a été réalisé lors de sa préparation. L'utilisateur a autorisé le déploiement et la déconnexion entraînée par le redémarrage ; Hermes doit d'abord avoir été basculé et validé.

## Périmètre exact et preuves

- Unité effective : `arthas-worldserver.dungeon-clear-8224099.service`, alias `arthas-worldserver.service`.
- Ancien processus observé : PID `1910234`, actif depuis le 7 septembre, `NRestarts=0`.
- Ancien binaire conservé : `/opt/arthas-next/candidates/atlas-chat-whispers-20260907/server/bin/worldserver`, SHA256 `de9f14523f7b933b714904f05cb0d4bb6d65f6592c9cd4d2aaeb0de125bfe315`.
- Nouveau binaire lié : `/opt/arthas-next/candidates/modules-update-20260909T1035Z/build-isolated/worldserver-candidate`, SHA256 `428e92b4498312593ea44f6c0d2870519a43af65acf68be3567890d178cd87d6`, 2 472 074 992 octets. Le binaire de référence a été reproduit bit-à-bit avant le remplacement des 173 objets DC ; les inscriptions des modules Atlas sont conservées.
- Limite assumée : 75 unités de test compilées mais exécutable GTest non lié, donc **aucun GTest Linux exécuté**. Le correctif de lien de test n'est pas déployé. Ni cette compilation ni un démarrage sain ne prouvent un parcours en jeu complet.
- Configuration principale active copiée strictement à l'identique : `/opt/arthas-next/candidates/atlas-chat-whispers-20260907/server/etc/worldserver.conf`, SHA256 `770dcd97377a2f421d302e7240830bb8c39705f09fddcf57d9f2b12aa9e85016`.
- Les 14 fichiers actifs `.conf`/`.conf.dist` concernés sont inventoriés par nom, taille, droits et hash dans `deployment-preflight.json`, sans contenu de configuration.

## Dépendances runtime maintenues

Le nouveau binaire contient toujours `_CONF_DIR=/opt/arthas-next/candidates/modules-update-20260905T1016Z/server/etc`. La méthode `ConfigMgr::LoadModulesConfigs` utilise `GetConfigPath() + "modules/"` et `GetConfigPath` renvoie `_CONF_DIR` sous Linux (`candidate/core/src/common/Configuration/Config.cpp`). Le déplacement du fichier principal passé par `--config` ne déplace donc pas les modules.

Les modules restent exactement ceux chargés dans `/opt/arthas-next/candidates/modules-update-20260905T1016Z/server/etc/modules`. Aucun fichier de module ni valeur par défaut n'est remplacé. Leur sauvegarde et leur hash font partie du scellement préalable.

Restent inchangés :

- `WorkingDirectory=/opt/arthas-next/candidates/modules-update-20260905T1016Z/server/bin` ;
- les drop-ins 10, 30, 40, 50 et 60, dont les ajouts Armory et Chat ;
- `AC_UPDATES_ENABLE_DATABASES=0` et `AC_PLAYERBOTS_UPDATES_ENABLE_DATABASES=0`, ainsi que `Updates.EnableDatabases=0` dans la configuration principale : pas de migration SQL ;
- `SourceDirectory` actuel, `LogsDir=/opt/arthas-next/server/logs`, port World `4000`, utilisateur/groupe `acore` ;
- `DataDir=/opt/arthas-next/candidates/dungeon-clear-8224099-20260903T062903Z/server/data` : cinq liens résolus et lisibles par `acore`, dont les mmaps vers `clean-official-20260829T132012Z/navigation-v20/mmaps` et les autres ressources vers `/opt/arthas-next/server/data`.

Ces anciens candidats restent donc des dépendances de production : **ne pas les supprimer lors d'un nettoyage ultérieur**.

## Étapes séparées, script `deploy_world_dc.py`

Le script tourne uniquement sur Atlas sous `sudo -n /usr/bin/python3 -I -B`, sans optimisation Python, avec `--confirm-root-reviewed` et une phase explicite ; le mode isolé est obligatoire et vérifié. Il ne crée pas d'archive globale du candidat. Les configurations et sauvegardes privées restent exclusivement sur le serveur, dans le nouveau candidat. Version soumise à revue : SHA256 `c514e9cdebdda8dfb424a2ce52be2ebf76be9f4e26de4d69f4baf6241c9d89ae`.

1. **`--phase prepare`**, avant toute interruption : revalider ancienne unité/configurations, SHA anciens et nouveaux binaires, chemin et SHA du processus réellement chargé via `/proc/PID/exe`, trois phases natives baseline/compile/link réussies, et au moins 3 Gio disponibles en plus de la réserve de 8 Gio. Sauvegarder les fichiers de configuration et les six fichiers d'unité/drop-ins dans `deployment/backup` privé. Copier, sans lien dur, le binaire dans `server/bin/worldserver` (`root:acore`, `0550`) et la configuration dans `server/etc/worldserver.conf` (`root:acore`, `0640`). Répertoires runtime `0750 root:acore`. Seule la racine du candidat passe de `0700` à `0711` pour permettre à `acore` de traverser ; tous ses anciens sous-répertoires privés restent inchangés. Aucun binaire n'est lancé.
2. **`--phase stop-backup --verified-hermes-executable CHEMIN_VERIFIE`**, seulement après validation de la nouvelle version Hermes et GO final : revalider le scellement, vérifier Hermes actif avec l'exécutable exact, contrôler les droits sur les deux bases et tester un dump de schéma `--no-data --skip-lock-tables` (sorties privées, timeout 120 s) **avant** toute interruption. Puis `systemctl stop arthas-worldserver.dungeon-clear-8224099.service`. L'arrêt propre garde le `TimeoutStopSec=300` existant. Les joueurs sont déconnectés ; auth/MySQL/Hermes ne sont pas arrêtés. Vérifier World sans PID et son cgroup disparu ou réellement vide avant la sauvegarde.
3. Toujours dans `stop-backup`, exécuter `mysqldump --defaults-extra-file=/dev/stdin --lock-tables --no-tablespaces --set-gtid-purged=OFF --routines --events --triggers --hex-blob --databases arthas_chars arthas_playerbots`, puis gzip niveau 1. Les identifiants sont lus en mémoire depuis la configuration serveur et transmis par stdin, jamais en arguments, sortie ou fichiers locaux. Verrous limités aux tables des deux bases, **aucun verrou global** ; le writer World est arrêté. La sauvegarde inclut les tables MyISAM et InnoDB : personnages environ 1,176 Go et Playerbots 108 Mo selon les tailles SQL observées. Pas de routines, vues, triggers ou événements recensés dans ces deux bases lors du contrôle. Le fichier final doit être non vide, décompressé intégralement sans erreur CRC, contenir le footer de fin et avoir son SHA256 consigné. Réserve de 8 Gio et plafond de 3 Gio pour l'ensemble runtime+déploiement surveillés pendant le flux. Timeout du processus dump : 900 s ; un `finally` tue uniquement ce processus et l'attend si gzip/IO échoue, libérant ses verrous. Échec : aucun candidat n'est démarré, World reste arrêté en attente de décision/retour code.
4. **`--phase start`**, après revue du résultat du dump : vérifier encore les hashes des fichiers actifs, sauvegarde et binaire. Installer uniquement `/etc/systemd/system/arthas-worldserver.dungeon-clear-8224099.service.d/70-atlas-dungeon-clear-20260909.conf`, puis `systemctl daemon-reload`. Contrôler que l'ExecStart est nouveau mais WD et environnement intégralement identiques à ceux scellés. `systemctl start` de la même unité.
5. Vérification runtime : exécutable réel de `/proc/PID/exe`, SHA256, nouvelle configuration, port 4000, fin du chargement World dans les nouveaux logs, modules et population de bots, `NRestarts`, erreurs fatales/SQL/config/mmap. Comparer sans exposer de données joueurs ou de secrets. Continuer à distinguer santé du démarrage et validation des donjons en jeu.

Contenu intégral du seul nouveau drop-in :

```ini
[Service]
ExecStart=
ExecStart=/opt/arthas-next/candidates/modules-update-20260909T1035Z/server/bin/worldserver --config /opt/arthas-next/candidates/modules-update-20260909T1035Z/server/etc/worldserver.conf
```

## Retour arrière

`--phase rollback-binary --confirm-root-reviewed` : vérifier les hashes de l'ancien runtime et des configurations, arrêter le World courant, déplacer uniquement le nouveau drop-in 70 dans le répertoire privé de déploiement (réversible), recharger systemd, vérifier le retour à l'ExecStart précédent avec WD/environnement identiques, puis démarrer l'ancien World. Aucune suppression de candidat, aucune réécriture des anciens fichiers, aucune restauration de DB.

Le nouveau DC peut accorder des prérequis persistants aux bots : quêtes récompensées, objets, hauts faits. Revenir au binaire précédent n'annule pas ces données. Une restauration SQL complète effacerait aussi les changements des joueurs depuis la sauvegarde : elle nécessite un besoin démontré et une autorisation distincte, World à nouveau arrêté. Elle n'est pas automatisée par ce script.

## Espace, interruption et confidentialité

Espace libre observé avant préparation : environ 15,1 Go. La copie runtime consomme 2,47 Go et le dump compressé est supplémentaire ; la garde de 8 Gio empêche de pousser le volume à saturation. Les anciens binaires restent disponibles sans seconde copie de 2,47 Go. L'interruption comprend l'arrêt propre, la sauvegarde et le chargement complet des 1 500 bots ; la durée réelle sera consignée après exécution, sans promettre une durée fixe.

Après la copie du fichier `server/etc/worldserver.conf`, **interdiction de rapatrier ou archiver globalement ce candidat** : uniquement les manifestes assainis, listes, hashes et journaux filtrés. L'archive source existante reste figée et distincte. Aucun dump ni configuration privée ne doit être ajouté au dépôt Git.
