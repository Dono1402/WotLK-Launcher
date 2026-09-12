# Bascule coordonnée proposée

Ce plan et les deux drop-ins sont préparatoires. Ils ne valent pas autorisation
d'interrompre la production. Le compte rendu de validation doit être revu avant
leur utilisation.

État de la préparation : validation partielle. Le démarrage de tous les
modules n'a pas atteint l'état prêt dans le banc plafonné à 5 Gio ; cinq
sondes de navigation Dungeon Clear échouent. Ce plan n'est donc pas un feu
vert de déploiement. Voir le [compte rendu](../../../docs/WOTLK-UPDATE-PREPARATION-2026-09-12.md).

1. Recontrôler les exécutables et configurations actifs par rapport aux
   empreintes de début de préparation, ainsi que les empreintes des candidats
   réellement testés. Tout changement entre-temps impose une nouvelle revue.
2. Préparer la nouvelle release Hermes depuis le paquet vérifié, sous
   `/opt/hermesproxy-wotlk/releases/hermes-all-update-20260912`, avec les droits
   nécessaires à son utilisateur de service. Conserver le fichier JSON Atlas,
   les variables d'environnement et le certificat déjà en usage. Le paramètre
   `--config` du drop-in désigne explicitement ce fichier JSON.
3. Préparer dans le candidat World une copie des configurations actives, avec
   les mêmes valeurs et des permissions adaptées à `acore`. Vérifier les neuf
   fichiers de configuration inventoriés. Les données de cartes restent celles
   du serveur actuel. La configuration des 1 500 bots, la priorité des bots de
   guildes réelles et les deux options de suppression à `0` sont conservées.
4. Après accord sur l'interruption, arrêter Hermes puis World. Conserver Auth
   et l'API. Sauvegarder les bases avec des empreintes et un journal de
   sauvegarde, au moment de cet arrêt. La sauvegarde de la base du banc ne
   remplace jamais celle de la production.
5. Appliquer uniquement les 28 migrations World figées et contrôlées, en
   enregistrant chaque résultat. Il n'y a pas de migration entrante retenue
   pour Auth, Characters ou Playerbots dans ces quatre mises à jour. En cas
   d'échec SQL, interrompre la bascule et examiner l'état partiellement migré.
6. Remplacer le lien `server/etc` du candidat, encore dirigé vers le banc,
   par la configuration de production préparée. Vérifier les droits d'accès
   des exécutables et de tous leurs répertoires parents pour `acore` et
   `hermesproxy`.
7. Installer les fichiers `world.override.conf` et `hermes.override.conf` sous
   le nom `zzzzzzzzz-atlas-all-update-20260912.conf` dans les répertoires de
   drop-ins de `arthas-worldserver.dungeon-clear-8224099.service` et
   `hermesproxy-wotlk.service`. Ce nom doit être revérifié comme dernier dans
   l'ordre lexical effectif. Le répertoire de travail actuel de World est
   conservé pour ses fichiers relatifs ; seul son exécutable et son chemin de
   configuration changent.
8. Recharger systemd, contrôler les commandes effectives, puis démarrer World
   et attendre son état prêt avant de démarrer Hermes. Contrôler les PID,
   empreintes des exécutables, modules chargés, connexion des bots réservés,
   santé de la boutique et absence d'erreur de démarrage. Vérifier qu'Auth et
   l'API conservent leurs PID.

L'arrêt World/Hermes déconnecte les joueurs et interrompt temporairement leur
reconnexion au royaume. Le temps de sauvegarde et de chargement des bots fait
partie de l'interruption.

## Retour arrière

Les exécutables, configurations et drop-ins précédents sont conservés.
Retirer uniquement les deux nouveaux drop-ins, recharger systemd et reprendre
le couple précédent permet un retour binaire, après vérification de sa
compatibilité avec l'état SQL atteint. Le lien de configuration de l'ancien
World n'est pas modifié par ce plan.

La migration `2026_08_30_05.sql` élargit le type de
`creature_template_model.VerifiedBuild`. Les autres migrations changent des
données World. Un retour binaire ne restaure donc pas automatiquement l'état
antérieur de la base. Toute restauration doit être décidée sur l'état réel,
avant la reprise des joueurs ; aucune restauration automatique de Characters,
Auth ou des commandes de boutique ne doit écraser des écritures intervenues
après la remise en service.
