# Préparation groupée Atlas — 12 septembre 2026

État au 13 septembre 2026 à 01 h 22, heure de Paris : sources intégrées et
binaires compilés ; validation partielle. La campagne de tests est close et
le banc arrêté. Aucune bascule de production n'a été effectuée. Le démarrage
avec tous les modules et les bots reste non validé.

La campagne réunit le core Playerbot, Playerbots, Dungeon Clear et Hermes dans
un nouveau candidat, en conservant les modifications Atlas actives. Les
[versions figées et recettes](../scripts/wotlk-update-20260912/README.md)
identifient les sources exactes. La préparation est distincte de la
[bascule coordonnée](../scripts/wotlk-update-20260912/activation/PLAN.md).

## Conservation des personnalisations

Les correctifs du core et de Playerbots reproduisent exactement les arbres Git
préparés depuis leurs versions amont figées. Le correctif Hermes reproduit lui
aussi l'arbre source testé. Ces correctifs conservent notamment :

- La synchronisation sociale Atlas, les adaptations de récupération du corps
  en PvP et les hooks natifs de boutique du core.
- La réservation et la connexion prioritaire des bots de guildes dirigées
  par de vrais joueurs.
- L'authentification Atlas, le SSO du launcher, les messages privés, les
  adaptations du client 3.4.3 et les services natifs de boutique de Hermes.
- Le correctif d'identité après renommage et la fermeture de la connexion
  legacy appartenant à chaque socket.

Les sept modules conservés ont été comparés fichier par fichier aux sources
actives, soit 109 fichiers : AHBot, Transmogrification, Account Achievements,
Atlas Armory, Atlas Friends, Atlas Chat et Atlas Shop. La seule addition de
compilation à Atlas Shop expose son header de hooks au core ; les fichiers
inventoriés restent identiques.

Les neuf fichiers de configuration World et 23 fichiers liés aux services ont
été sauvegardés puis comparés à leurs empreintes initiales. Les sauvegardes
comprennent les paramètres privés de Hermes, ses variables d'environnement
et son certificat. Leur contenu reste sur le serveur, hors Git. La future
bascule conservera notamment le réglage des 1 500 bots, la priorité des bots
de guildes réelles et les options de suppression de guildes et d'équipes
d'arène désactivées.

## Résultats établis

| Vérification | Résultat |
| --- | --- |
| Hermes, suite complète ciblant le client 3.4.3 | 1 954 réussites, 4 cas ignorés, aucun échec |
| Paquets natifs de boutique et d'identité | 27 contrôles réussis |
| Reproduction des sources core, Playerbots et Hermes par les correctifs | Exacte |
| Conservation des fichiers, configurations et sauvegardes inventoriés | Aucun écart |
| Migrations World sur la base isolée | 28 appliquées avec succès |
| Contrôles SQL sur les 28 migrations figées | Réussis |
| Dungeon Clear : lectures de configuration, portabilité MSVC et déterminisme | Réussis |
| Compilation World, Auth et des deux suites C++ | Réussie |
| Core C++ | 5 916 réussites, 5 487 cas ignorés, aucun échec |
| Dungeon Clear C++ avec données de navigation | 1 595 réussites, 5 échecs, aucun cas ignoré |
| Conversion d'or, achat et renommage par SSO/Hermes | 30 contrôles réussis |
| Identité après renommage, observateur et reconnexion | 20 contrôles réussis |
| Services de compte via Hermes 3.4.3 | 20 contrôles réussis |
| Services natifs avec transactions concurrentes et redémarrage World | 36 contrôles réussis |
| Démarrage des neuf modules avec bots synthétiques | Arrêt par limite mémoire du banc à 5 Gio, avant l'état prêt |
| Social Atlas complet, priorité de guilde et parcours Dungeon Clear en fonctionnement | Non exécutés |
| Suites Go supplémentaires : groupe, échange, session, mort et instances | Compilées, non exécutées |

Les quatre tests Hermes ignorés concernent deux types de paquets dépourvus
d'opcode dans ce client. Les vérifications natives émulent les fonctions
ciblées du client à partir de paquets produits par Hermes ; elles ne lancent
ni le jeu ni son interface et ne constituent pas une validation visuelle.

Le contrôle de style C++ sur l'ensemble du core signale 42 diagnostics.
L'analyse des lignes modifiées les situe tous hors des ajouts Atlas. Le
contrôle global de style n'est donc pas présenté comme réussi. Le contrôle
SQL utilise les fonctions du vérificateur amont sur les 28 fichiers retenus,
sans son mécanisme de récupération d'une branche `origin/master` différente
de la branche Playerbot utilisée ici.

Les 5 487 cas C++ ignorés correspondent aux conditions internes des tests
paramétrés et à un test réservé à ASAN, désactivé par l'amont. Un premier
passage avait également cinq erreurs de configuration temporaire : la recette
a été corrigée pour utiliser son répertoire temporaire autorisé, puis la
suite core complète a réussi. Aucun code du core n'a été changé pour cela.

Les cinq échecs Dungeon Clear concernent les sondes de navigation d'Utgarde
Pinnacle (un), de Pit of Saron (un) et de Culling of Stratholme (trois). Les
deux premières sondes et leurs routes n'ont pas changé dans cette mise à jour ;
les trois dernières sont nouvelles. Des chemins sont incomplets et certaines
distances ne satisfont pas les attentes des sondes. Leur incidence en jeu
n'a pas été établie. Les assertions n'ont pas été affaiblies et les échecs
ne sont pas présentés comme des réussites.

Les quatre phases boutique utilisent réellement World, MySQL et l'API ; trois
passent aussi par le nouveau paquet Hermes. Les autres modules y sont
désactivés. Elles prouvent ces fonctions dans ce périmètre, sans valider
l'ensemble des interactions entre les neuf modules.

Le banc initial ne contenait que les schémas Characters/Playerbots : ses
16 tables de référence ont été alimentées depuis les fichiers SQL publics
de la version Playerbots retenue. Son statut de royaume, resté à `flag=3`
après un arrêt, a aussi été réinitialisé avec contrôle de l'adresse locale.
Le dernier essai a ensuite atteint la limite de 5 Gio de son unité systemd
et s'est terminé avec `oom-kill`. Ce constat ne permet pas d'attribuer une
régression mémoire au candidat : aucun essai comparatif de l'ancienne version
dans les mêmes conditions n'a été exécuté. Le conteneur et tous les processus
du banc sont arrêtés. Les scénarios longs supplémentaires n'ont pas été lancés.

Les [résultats et versions exactes](update-preparation/2026-09-12/validation-summary.json)
conservent ces distinctions. Les recettes restantes sont préparées pour une
éventuelle reprise ciblée ; leur présence dans Git ne signifie pas qu'elles
ont été exécutées avec succès.

## Base de données et limites

Les 28 migrations ont été exécutées uniquement dans `shop_test_world`, après
sauvegarde du banc. Les tables de textes et d'objets personnalisés contrôlées
sont restées inchangées. La migration `2026_08_30_05.sql` élargit la colonne
`creature_template_model.VerifiedBuild` ; les autres changent des données
World. Aucune migration entrante Auth, Characters ou Playerbots n'est retenue
dans ce lot.

Les essais de modules utilisent des comptes et un petit pool de bots
synthétiques. Ils ne mesurent pas la charge de 1 500 bots et ne garantissent
pas le fonctionnement exhaustif de tous les donjons. Les résultats du banc
ne remplacent pas les contrôles après une éventuelle bascule autorisée.

World, Hermes, Auth et l'API conservent pour l'instant leurs processus et
exécutables de départ. La bascule proposée concerne World et Hermes ; leur
arrêt déconnectera les joueurs et interrompra temporairement la reconnexion
au royaume. Elle nécessite l'accord distinct sur cette interruption, après
revue des résultats. Les anciennes versions et configurations sont conservées.

Un retour aux anciens exécutables ne restaure pas les données SQL. Toute
restauration éventuelle devra être décidée à partir de l'état réel des bases ;
elle ne doit jamais écraser automatiquement des écritures de joueurs.
