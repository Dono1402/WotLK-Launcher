# Profils amis et messagerie Atlas — développement local

Cette évolution est distincte de la version publique GitHub 1.4.0. Le client
corrigé est livré dans `artifacts/AtlasLauncherLocal/AtlasLauncherLocal.exe`.
L'API correspondante est déployée depuis le 6 septembre 2026 à 21:02 UTC, après
autorisation explicite. La release GitHub et les téléchargements publics restent
inchangés. Voir `SOCIAL-SERVER-ACTIVATION.md` pour les preuves et le réglage des
statistiques. L'utilisateur a depuis confirmé le `.reload config` en jeu ; les
personnages sans capture doivent encore se connecter pour leur premier relevé.
Les corrections de chargement 3D et des statistiques sauvegardées sont détaillées
dans `FRIEND-ARMORY-CORRECTIONS.md`.

## Parcours

- Les trois points et le clic droit d'une ligne d'ami proposent **Voir le profil**,
  **Envoyer un message** et **Supprimer des amis**. La suppression conserve sa confirmation.
- Le profil d'un ami affiche son identité Atlas, son avatar, son statut, sa bio
  et ses personnages. Les commandes de modification sont masquées et bloquées
  dans le pont natif. La bannière personnalisée de la version 1.4.0 est stockée
  sur l'appareil de son propriétaire ; sans bannière disponible, le profil utilise
  un fond bleu, sans illustration de remplacement.
- La page **Messages** contient les conversations, les non lus, l'historique
  paginé et un brouillon distinct par ami. Entrée envoie ; Maj+Entrée ajoute une ligne.
  Un échec conserve le brouillon et réutilise le même identifiant de message.
- Le fond de la connexion obligatoire recouvre toute la fenêtre fixe, en conservant
  ses proportions. Les onglets et les autres pages sont inaccessibles avant connexion.
- La page Jeu affiche le nombre de personnages joueurs connectés, avec exclusion
  des comptes bots lorsque leur base est configurée. La latence mesure la connexion
  TCP à la passerelle du jeu ; une mesure indisponible reste affichée par un tiret.
- Les trois encadrés promotionnels du jeu et la flèche de retour de Messages sont retirés.

## Données et relais vers le jeu

Les lectures de profils amis passent par l'API authentifiée. Chaque requête
vérifie l'amitié acceptée et l'appartenance du personnage. Le cache du profil
exige un nouveau roster autorisé à chaque ouverture. Les modèles compatibles
sont réutilisés entre les visites et les lancements du logiciel, puis purgés à
la déconnexion, au changement de compte ou au retrait de l'ami. La génération
initiale et la conservation du cache sont décrites dans
`FRIEND-ARMORY-PERFORMANCE.md`.

La migration `0006_private_chat.sql` crée les conversations, messages, quotas
et files du relais. Le plafond de production est maintenant **0006**. La messagerie
entre launchers est activée ; le module du core servant de relais vers le jeu
reste local et n'a pas été déployé.

Le module local `../mod-atlas-chat` reçoit et envoie les messages via la base auth
du core. Une copie en jeu n'est créée que si un personnage du destinataire est
connecté. Le texte apparaît uniquement chez celui-ci, sous la forme :

```text
[Atlas] PseudoAtlas : Bonjour !
```

La réponse en jeu utilise :

```text
.atlasmsg PseudoAtlas Bonjour depuis le jeu !
```

Le pseudo affiché est celui du compte Atlas. Le module n'utilise pas le nom d'un
personnage comme expéditeur. Il est désactivé par défaut ; son installation et
un essai réel dans le jeu nécessitent une opération serveur séparée.

## Validation locale

Les tests utilisent des comptes factices, un serveur MySQL jetable lié à
127.0.0.1 et des transports HTTP locaux ou factices. Les vérifications natives
du profil utilisent une fenêtre de test hors écran, sans activation.

Commandes du projet `WotLK.Launcher.IntegrationTests` :

```text
--armory-session
--armory-public-package
--armory-api-mysql
--armory-launcher --capture-directory <dossier>
--chat-api-mysql
--chat-runtime
--chat-view-wpf --capture-directory <dossier>
--migration-ceiling
```

Le test web `prototypes/armory-3d/verify-friend-profile.cjs` vérifie aussi les
profils français et anglais à la dimension fixe du launcher, la navigation et l'absence de
commandes de modification. Les tests C++ et les commandes de compilation du
module sont décrits dans `../mod-atlas-chat/README.md`.

Les preuves de cette préparation sont conservées dans
`artifacts/atlas-private-chat`. Une compilation du module et les tests de sa
file SQL ne prouvent pas encore l'affichage du message dans un vrai client WoW.

La préparation de l'activation serveur, y compris la collecte des statistiques
pour tous les personnages, est détaillée dans `SOCIAL-SERVER-ACTIVATION.md`.
