# Activation des profils amis, de la messagerie et du compteur

Après autorisation explicite « GO », l'API a été déployée le 6 septembre 2026 à
21:02 UTC dans `social-20260906T205658Z`. Elle utilise la migration 0006 et le
compteur excluant les bots. Le retour à une API saine a pris 970 ms ; les PID et
dates de démarrage de WoW et Hermes sont inchangés. Les contrôles en lecture seule
retrouvent les quatre personnages d'un ami accepté, leurs catalogues et les
conversations. Aucun message de test n'a été envoyé aux utilisateurs.

Les preuves sont dans `artifacts/atlas-social-corrections/deployment`.
La sauvegarde serveur est `/opt/wotlk-launcher-api-backups/social-20260906T205658Z`.
Le fichier contenant les secrets est conservé à l'identique. Un fichier distinct
`/etc/wotlk/launcher-api-social.env`, ajouté par `zz-atlas-social.conf`, fournit
uniquement le plafond 6 et `WOTLK_PLAYERBOTS_DB=arthas_playerbots` ; ces deux valeurs
ont été vérifiées dans l'environnement du processus réel.

Le réglage `AtlasArmory.OnlyGuid` est passé du GUID du personnage pilote à 0 dans le fichier actif,
sans autre modification de son contenu. L'utilisateur a confirmé avoir exécuté
`.reload config` avec un compte GM. Une nouvelle capture d'un autre personnage
reste à observer après sa connexion. Aucun redémarrage de WoW ou Hermes n'a été
effectué par cette première intervention. Le complément sur les statistiques
sauvegardées et le chargement 3D est consigné dans `FRIEND-ARMORY-CORRECTIONS.md`.

Avant cette intervention, les lectures publiques sans session donnaient : santé
200, armurerie personnelle 401, armurerie d'un ami 404 et conversations 404.

Le candidat est produit par `scripts/build-social-api-candidate.ps1`. Son archive
et ses empreintes sont conservées dans `artifacts/atlas-social-corrections`.
Cette préparation locale ne déploie aucun fichier et ne modifie aucun service.

## Activation de l'API

L'opération a été autorisée séparément, car le travail avait été limité au local.
Elle concerne uniquement `wotlk-launcher-api.service`, avec une courte interruption
de l'API pendant sa bascule. Elle permet les profils amis, les conversations entre
launchers et le compteur provenant du serveur. Le launcher public 1.4.0 et ses
fichiers de téléchargement restent inchangés.

Avant la bascule, relire le service et son lien actif, vérifier les permissions
de lecture des personnages, puis sauvegarder le binaire, la configuration et la
base concernée. Déployer le candidat dans un nouveau répertoire et comparer ses
empreintes. La migration 0006 ajoute cinq tables de messagerie ; elle nécessite
le passage explicite de `WOTLK_LAUNCHER_MAX_SCHEMA_VERSION` de 5 à 6. Conserver les
autres paramètres et secrets existants sans les afficher.

Le compteur doit recevoir `WOTLK_PLAYERBOTS_DB=arthas_playerbots`, nom confirmé
dans la configuration active du module Playerbots. Vérifier que l'identité SQL de
l'API peut lire `arthas_playerbots.playerbots_account_type` ; si ce droit manque,
prévoir uniquement le droit SELECT sur cette table dans l'opération autorisée.
Les comptes présents dans cette table sont exclus du compteur. Le contrôle du
6 septembre à 20:21 UTC trouve 1 500 personnages en ligne avec les bots, et zéro
personnage joueur après exclusion. Ces chiffres sont une observation ponctuelle.

Valider le candidat sur une adresse loopback distincte avant de remplacer le lien
actif. Après bascule, contrôler la santé, les migrations, l'authentification des
nouvelles routes, la lecture des profils autorisés et le compteur. Vérifier que
les PID et dates de démarrage du worldserver et de Hermes sont inchangés. Ne pas
envoyer de messages à de vrais utilisateurs pour effectuer ces contrôles.

En cas d'échec, le simple retour au binaire 1.4.0 et au plafond 5 ne suffit pas
si la migration 0006 figure dans `atlas_launcher_schema_history` : l'ancien
migrateur refuse cette version inconnue. Le candidat refuse également un plafond
5 lorsque la version 6 est enregistrée. Le démarrage du candidat sur une autre
adresse loopback avec la même base applique déjà la migration et active son
worker de file d'entrée, avant toute bascule du trafic public.

Le repli vérifié avec la DLL conservée de la publication 1.4.0 et une base MySQL
8.4 jetable est le suivant (preuves :
`artifacts/atlas-social-corrections/rollback-proof/result.json` et `run.log`) :

1. Arrêter toutes les instances de l'API utilisant cette base, y compris le
   candidat loopback, afin de suspendre les migrations et le worker de chat.
2. Si l'historique contient 0006, exporter durablement **cette seule ligne avec
   ses six colonnes**, notamment les 32 octets du checksum, la date et la version
   d'application. Vérifier sa correspondance avec la migration du candidat et
   conserver une empreinte de l'export. Dans une transaction, retirer uniquement
   cette ligne en vérifiant version, nom et checksum, puis exiger exactement une
   ligne affectée. Conserver toutes les tables et toutes leurs données.
3. Rétablir le binaire 1.4.0 et ses paramètres, dont le plafond 5, puis vérifier
   démarrage, santé, authentification de session et lecture du profil. Les
   messages restent stockés ; l'ancienne API ne fournit pas les routes de chat.
4. Pour revenir au candidat, arrêter l'ancienne API, vérifier les cinq tables de
   chat, restaurer **exactement la ligne archivée** dans l'historique, puis
   seulement démarrer le candidat au plafond 6. Ne pas relancer sa migration
   alors que les tables existent et que la ligne archivée manque.

Si 0006 échoue avant l'enregistrement de son historique, ses instructions
`CREATE TABLE` peuvent avoir laissé des tables partielles ; une relance aveugle
échoue sur la première table déjà présente. Le retour à l'ancien binaire au
plafond 5 fonctionne sans toucher à ces tables si l'historique reste en 0005.
Avant une nouvelle tentative de migration, inspecter l'état exact. Un nettoyage
est permis uniquement API arrêtées, sans ligne d'historique 0006, pour les tables
dont l'absence avant cette tentative est prouvée, conformes au SQL attendu et
**toutes vides** ; les retirer dans l'ordre inverse des dépendances. Toute table
préexistante, différente ou contenant une ligne impose une reprise examinée
séparément. Ne supprimer aucun message ni compte et ne restaurer aucune base
entière : les écritures intervenues depuis la sauvegarde doivent être conservées.

## Relais avec le jeu

Le module `../mod-atlas-chat` fait l'objet d'une opération distincte. Son intégration
au binaire du core et le redémarrage du worldserver exigent leur propre autorisation.
L'activation de l'API seule ne met pas en service les messages dans WoW.

Après compilation et activation autorisées du module, vérifier un aller-retour
entre deux comptes de test amis, le pseudo Atlas affiché, la reconnexion et le
retrait d'amitié. La réponse en jeu utilise `.atlasmsg PseudoAtlas message`.

## Statistiques de tous les personnages

Lecture confirmée de la configuration active le 6 septembre :

```text
AtlasArmory.Enable = 1
AtlasArmory.OnlyGuid = <GUID_DU_PERSONNAGE_PILOTE>
AtlasArmory.LiveEnable = 1
```

La table de captures contient une seule entrée et aucune capture d'un autre GUID.
Le changement à effectuer après autorisation est uniquement
`AtlasArmory.OnlyGuid = 0`. Le collecteur existant accepte alors tous les personnages
joueurs, en conservant l'exclusion des sessions bots. Aucune valeur de statistique
ne doit être calculée artificiellement à partir de ce manque de captures.

Le processus utilise actuellement
`/opt/arthas-next/candidates/modules-update-20260905T1016Z/server/etc/worldserver.conf`.
Relire ce chemin et les paramètres avant toute modification, sauvegarder ce fichier
et vérifier que la différence ne porte que sur la valeur OnlyGuid.

RA et SOAP sont désactivés et l'entrée standard du worldserver est `/dev/null`.
Le fichier modifié doit donc être relu avec la commande GM `.reload config` depuis
le jeu, ou lors d'un redémarrage du worldserver explicitement autorisé. Ne pas
activer une interface d'administration ni redémarrer le worldserver par défaut.

Après rechargement, vérifier une nouvelle capture d'un personnage autre que le
pilote. La collecte intervient après connexion, changement d'équipement, puis
périodiquement toutes les 60 secondes et à la déconnexion. Un personnage resté
hors ligne depuis l'activation ne possède pas encore de statistiques détaillées.
