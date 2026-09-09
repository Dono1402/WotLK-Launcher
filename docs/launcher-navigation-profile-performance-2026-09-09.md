# Navigation et mémoire des profils — 9 septembre 2026

Cette série complète la [référence initiale](launcher-performance-2026-09-09.md)
par une comparaison avant/après. Elle vise le coût des premières ouvertures de
pages et les allocations lors des changements successifs de profils.

## Modifications

Le pont de traduction regroupait auparavant des parcours complets de
sous-arbres pour chaque événement `Loaded` et chaque génération de conteneurs.
Il regroupe maintenant ces découvertes dans un passage au dispatcher et visite
chaque élément une seule fois par lot. Le texte d'un élément recyclé est
restauré immédiatement ; les descendants sont découverts à la priorité de
liaison des données du dispatcher.
Le retrait des abonnements parcourt chaque liste une fois, et les éléments
logiques sans représentation visuelle ne provoquent plus d'exception utilisée
comme contrôle de flux.

L'armurerie conserve sa surface WebView2 lors d'un changement de profil. Chaque
profil garde une nouvelle autorisation, un nouveau helper et un nouveau
document. Le document précédent est masqué immédiatement et sa session annulée
refuse les requêtes pendant la transition. Les événements du navigateur sont
désabonnés puis rattachés à la nouvelle session ; seule sa navigation réussie
peut rendre le document visible. Déconnexion, changement de compte,
reconfiguration, retrait d'ami, fermeture et reprise après incident conservent
une destruction complète du navigateur.

## Protocole

- Trois passages de référence et trois de contrôle final. Chaque passage
  démarre un processus neuf avec des dossiers de cache applicatif neufs.
- Même parcours de mesure pour les deux versions. Le launcher avant changement
  provient de la compilation publiée localement à 18:22 UTC pour `66d9d46` ;
  son DLL a été conservé avant les modifications de ce travail.
- Même parcours et mêmes fixtures que dans la référence initiale : Messages,
  profil personnel avec modèle 3D local préparé, vingt cycles de navigation,
  puis vingt changements entre deux profils amis avec roster sans modèle 3D.
- Chaque phase attend 1,5 seconde puis mesure seize échantillons espacés de
  500 ms. Aucun GC forcé, trimming ou vidage du cache de fichiers Windows.
- WPF hors écran et inactif, services et données synthétiques locaux. Aucun
  compte réel, jeu, service distant ou entrée sur le bureau. L'arrêt des
  processus enfants est vérifié après chaque passage.

La mémoire rapportée est la somme des octets privés engagés du shell, de ses
environnements WebView2 et de son helper Node. Elle ne mesure pas la RAM
physique unique. Les temps de navigation s'arrêtent au dispatcher disponible,
avec un critère DOM prêt pour Messages et les profils ; ils ne mesurent pas
la présentation d'une frame sur l'écran. Les résultats ne couvrent pas le GPU,
la fenêtre minimisée, l'authentification ou la préparation d'un modèle inédit.

## Pages natives isolées

Une série distincte de trois comparaisons ouvre Addons, Notes de version,
Paramètres puis Jeu, sans initialiser les navigateurs de Messages et de
l'armurerie. Elle isole le travail des pages natives. Les colonnes donnent
la médiane de la première ouverture, puis la plage min–max, sur trois processus
neufs par version.

| Page | Avant, ms | Après, ms | Baisse de la médiane |
| --- | ---: | ---: | ---: |
| Addons | 17,61 (17,59–18,13) | 15,83 (15,74–15,95) | 10 % |
| Notes de version | 90,35 (64,61–292,51) | 43,64 (43,62–44,82) | 52 % |
| Paramètres | 16,31 (16,26–22,14) | 11,21 (11,19–11,42) | 31 % |

Les allocations du thread d'interface lors de ces premières ouvertures passent
de 1,35 à 1,09 Mio pour Addons, de 8,53 à 5,49 Mio pour les notes et de 2,74 à
2,04 Mio pour les paramètres. Les baisses sont respectivement de 20 %, 36 % et
26 %. Ce sont des octets alloués pendant l'action, pas de la mémoire retenue.
Trois essais par version restent une observation locale, sans intervalle de
confiance statistique.

Ces gains isolés ne décrivent pas toute la navigation après ouverture de la
3D : le dispatcher, le rendu WPF et les vues web partagent alors le parcours.
Le tableau du parcours complet ci-dessous conserve ces coûts, y compris les
cas où la latence ne s'améliore pas.

## Parcours complet et profils

Les six passages retenus ont chargé le modèle personnel 3D et leurs 42 phases
de mesure ont conservé des ensembles de processus stables. Tous les processus
enfants ont quitté après chaque fermeture. Les chiffres de mémoire sont les
médianes des trois moyennes de phase.

| Mesure | Avant | Après | Évolution |
| --- | ---: | ---: | ---: |
| Changement d'ami jusqu'au roster, médiane de 60 actions | 450,13 ms | 160,67 ms | −64,3 % |
| Même action, p95 | 480,77 ms | 187,18 ms | −61,1 % |
| Mémoire privée au premier profil ami | 814,8 Mio | 802,5 Mio | −1,5 % |
| Après 10 changements d'ami | 1 020,9 Mio | 876,0 Mio | −14,2 % |
| Après 20 changements d'ami | 1 028,2 Mio | 924,8 Mio | −10,1 % |

Les soixante changements d'ami prennent 419,52–484,07 ms avant et
151,72–219,11 ms après. Au vingtième changement, les trois moyennes mémoire
s'étendent de 1 025,8 à 1 041,5 Mio avant et de 924,0 à 929,5 Mio après.
La mémoire continue de croître entre le dixième et le vingtième changement :
ce parcours démontre une réduction d'allocations, pas un plateau garanti sur
une longue session.

La première ouverture de chaque page dans ce même parcours, après avoir
ouvert Messages et l'armurerie, donne les médianes suivantes. Les plages sont
conservées pour éviter de masquer les pics.

| Page | Avant, ms | Après, ms |
| --- | ---: | ---: |
| Retour vers Jeu | 13,75 (12,81–16,32) | 14,87 (14,05–15,14) |
| Addons | 20,45 (15,17–595,40) | 21,59 (17,03–386,51) |
| Notes de version | 454,71 (126,70–746,93) | 67,33 (66,08–70,68) |
| Paramètres | 14,53 (12,61–15,46) | 55,03 (50,75–62,07) |
| Retour vers Messages | 6,87 (5,62–7,05) | 6,82 (6,34–12,25) |

La navigation complète a une médiane de 4,63 → 4,16 ms sur 300 actions par
version, mais son p95 passe de 15,46 à 21,93 ms. La réouverture du profil
personnel déjà chargé passe de 9,02 à 11,27 ms en médiane. Il n'y a donc pas
de gain uniforme de latence : les paramètres après la 3D régressent d'environ
40 ms à la première ouverture, malgré leur amélioration dans le test natif
isolé. Un pic de 387 ms reste aussi visible sur Addons. Les pics antérieurs
proches de deux secondes ne sont pas reproduits dans cette référence ; leur
disparition ne peut pas être attribuée à cette modification.

Le CPU machine n'a pas baissé dans ces contrôles : la médiane du profil 3D
ouvert passe de 1,98 à 2,31 %, celle après vingt cycles de navigation de 1,96 à
2,19 %, et celle du vingtième changement d'ami de 1,54 à 1,75 %. Ces valeurs
sont normalisées sur 32 processeurs logiques. Leur cause n'est pas attribuée
par ce harnais ; aucun gain CPU, GPU ou de consommation en mode minimisé n'est
revendiqué. Le profilage du rendu et des attentes du dispatcher reste un
travail distinct des gains confirmés sur les allocations et les profils.

## Profilage ciblé

Une instrumentation temporaire du pont de traduction a relevé, à la première
ouverture des notes de version, 10 494 visites sur 348 parcours avant changement,
contre 870 visites sur trois parcours après changement. Ce relevé explique le
travail évité, mais ne représente pas à lui seul le temps complet de la page.
Les compteurs ont été retirés du produit avant la série comparative.

Dans les essais diagnostiques, les vingt anciens contrôles WebView2 étaient
encore accessibles avant une collecte forcée et ne l'étaient plus après.
Cela identifie des allocations temporaires collectables ; ce constat ne prouve
pas une fuite mémoire. La réutilisation évite ces créations successives.

## Vérifications

Compilation Release sans avertissement ni erreur. Suites `--optimization-ui`,
`--optimization-memory`, `--shell-navigation-wpf`, `--friend-profile-wpf` et
`--armory-shared-character-wpf` réussies. La navigation complète couvre
778 assertions, notamment les pages, panneaux, changements rapides, contrôles
virtualisés et positions conservées.

Le test de traduction vérifie aussi les descendants créés après la découverte
de leur page, sans rafraîchissement manuel, puis leur retour au français.
Le test des profils vérifie la réutilisation du même navigateur, le masquage
immédiat de l'ancien document, le changement d'origine, la disparition des
variables de l'ancien document et l'arrêt de l'ancien helper. Il conserve les
scénarios de lecture seule, de traduction FR/EN, de réponse tardive d'avatar,
de retrait d'ami et de déconnexion/reconnexion. Le test du personnage partagé
couvre aussi un échec d'ouverture, un crash provoqué du moteur de test et une
reprise sur le personnage demandé.

## Données et diagnostic

Les résultats et empreintes des assemblies restent hors Git, sous
`artifacts/launcher-perf-followup-20260909/`. La référence utilise
`comparison-1-before` à `comparison-3-before`. Les contrôles finaux utilisent
`initial-visibility-check`, `comparison-final-2-after` et
`comparison-final-3-after`. La première série de diagnostic comparative reste
dans `comparison-1-after` à `comparison-3-after` ; elle n'entre pas dans les
valeurs finales.

`comparison-final-summary.json` rassemble les statistiques finales.
`comparison-assemblies.json` conserve les empreintes de la référence et de
la première comparaison ; les empreintes de la compilation finale sont dans
`final-verification.json`.

Les sorties `native-1-before` à `native-3-after` contiennent les mesures
isolées. Le harnais enregistre maintenant le nom de la page, le cycle, les durées du
clic et du dispatcher, et les allocations du thread d'interface par navigation.

`ATLAS_LAUNCHER_PERF_DIAGNOSTICS=1` active un mode séparé : deux échantillons par
phase et une phase supplémentaire après collecte forcée, avec comptage faible
des anciens navigateurs. Ce mode sert exclusivement au diagnostic et ses
résultats ne doivent pas être mélangés aux mesures normales. Le script
`scripts/measure-launcher-performance.ps1` permet de reproduire les mesures
normales sur la compilation courante en l'absence de cette variable.

Avec le mode diagnostic, `ATLAS_LAUNCHER_PERF_NATIVE_ONLY=1` sélectionne le
parcours natif isolé, sans démarrage de WebView, ni phase CPU/mémoire au repos.
