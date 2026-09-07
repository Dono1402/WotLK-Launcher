# Optimisation des modèles des amis

La préparation locale relisait les enregistrements binaires de la table
`ChrCustomizationMaterial` pour chaque matériau, malgré son préchargement.
Sur le personnage C, l'initialisation des personnalisations prenait 46,008 secondes sur
les 48,307 secondes de `prepare.cjs`.

`withIndexedTableRows` utilise temporairement la Map déjà décodée pendant cette
initialisation. Chaque lecture rend une copie indépendante et le lecteur initial
est restauré même en cas d'échec. Le parseur fournisseur, les fichiers du jeu et
le format des modèles restent identiques. Aucun index persistant supplémentaire
ni téléchargement n'est nécessaire.

## Mesures

Exports complets dans un nouveau dossier, à partir des mêmes personnages,
équipements et fichiers client que la mesure précédente :

| Personnage | Avant | Après |
|---|---:|---:|
| Personnage A | 56,190 s | 6,505 s |
| Personnage B | 51,124 s | 6,395 s |
| Personnage C | 49,363 s | 6,403 s |
| Personnage D | 48,224 s | 6,556 s |

Le lot de quatre exports est terminé en 25,914 secondes. Ces durées mesurent la
génération locale ; elles n'incluent pas l'authentification, le démarrage de la
WebView et la préparation initiale des icônes du roster.

Les quatre `prepared.json`, les métadonnées de personnage et d'équipement,
ainsi que les **109 fichiers GLTF, PNG et BIN** sont identiques aux références.

## Réutilisation après redémarrage

Le cache des amis utilise un répertoire stable par compte connecté et compte ami.
Une fermeture normale conserve les modèles. La déconnexion explicite, le
changement de compte et le retrait d'un ami purgent les caches concernés.

Un nouveau helper exige toujours une réponse de roster autorisée avant de lire
ou servir un modèle conservé. La comparaison de l'apparence et de l'équipement,
la version du rendu et la présence des ressources déterminent la réutilisation.
Les statistiques proviennent de la nouvelle réponse et ne sont pas figées avec
la géométrie.

Deux processus RPC distincts ont récupéré les quatre modèles en **73 ms puis
43 ms** après réception du roster de test. Aucun export ni requête de catalogue,
et les 109 ressources sont restées identiques. Avant autorisation, aucun
personnage n'était exposé et la route du modèle répondait HTTP 404.

## Preuves

- `artifacts/atlas-friend-assets-fix/optimization/benchmark-models.json` :
  mesures des quatre exports et empreintes de chaque ressource.
- `artifacts/atlas-friend-assets-fix/optimization/material-index-audit.json` :
  table réelle de 12 597 lignes, sans copies ni identifiants dupliqués.
- `artifacts/atlas-friend-assets-fix/optimization/node-tests.log` : suite Node
  complète, 120 tests réussis et deux oracles Canvas facultatifs ignorés.
- `artifacts/atlas-friend-performance/process-reuse/process-reuse-verification.json` :
  deux redémarrages du vrai helper RPC sur une copie des modèles réels.
- `artifacts/atlas-friend-performance/rendering` : contrôle des quatre vues,
  38 icônes, 109 ressources, animations actives et aucune erreur JS/HTTP,
  viewport fixe 1598 × 997. Les données de cette fixture sont les relevés copiés
  lors des contrôles précédents, sans nouvelle lecture serveur.

L'optimisation est locale et n'exige aucune modification de l'API ni redémarrage
des services du jeu. La livraison du client et les tests WPF sont consignés dans
`artifacts/atlas-friend-performance/final-verification.json` après validation.

## Client livré

Le 7 septembre 2026 à 00:22 heure de Paris, après fermeture du logiciel par
l'utilisateur, l'exécutable habituel `artifacts/AtlasLauncherLocal/AtlasLauncherLocal.exe`
a été remplacé atomiquement avec sauvegarde de la version précédente. Le fichier
installé fait 100 890 597 octets et son SHA256 est
`9794343ca80531f97bddc07e3c8f6a3debd1782b18f0058ee6ca6571607d760f`.
La configuration locale existante a été conservée à l'identique.

Compilation sans avertissement ni erreur ; tests du cache, lifecycle WPF et
profils amis réussis. Le cas de création de lien symbolique est explicitement
ignoré faute de privilège sur cet hôte ; les contrôles des dossiers voisins et
de l'invalidation des fichiers verrouillés ont été exécutés.

## Réactivité visible et préparation parallèle

Le contrôle du 7 septembre a identifié deux délais supplémentaires : la
génération des personnages restait séquentielle, et le frontend pouvait attendre
presque cinq secondes avant de détecter un modèle pourtant déjà prêt.

Le helper prépare maintenant **deux personnages simultanément**. Le personnage
sélectionné prend le prochain créneau disponible ; les exports déjà commencés
se terminent normalement. Les icônes et informations d'équipement restent
consultables pendant la préparation. L'arrêt attend la fin des deux workers
et leur nettoyage, et les écritures du cache CASC partagé sont atomiques.

Mesures sur les mêmes quatre personnages, dans un dossier de modèles vide :

| Concurrence | Premier modèle prêt | Quatre modèles prêts | Pic mémoire des enfants |
|---|---:|---:|---:|
| 1, avant | 10,83 s | 30,29 s | non mesuré |
| **2, retenu** | **11,20 s** | **17,89 s** | **1,68 Go** |
| 4, comparaison | 12,01 s | 12,06 s | 2,95 Go |

Ces durées commencent à la lecture du roster et comprennent environ 4,5 secondes
de préparation initiale des icônes ; elles se terminent à la publication du
modèle local, avant le rendu. Deux workers réduisent l'attente des personnages
suivants en limitant la mémoire. Les 109 ressources GLTF, PNG et BIN sont
identiques à la référence avec deux comme avec quatre workers.

Pendant la préparation, la vue consulte le helper local toutes les 250 ms,
puis revient à cinq secondes. Les statistiques gardent leur cadence de cinq
secondes et aucun rafraîchissement distant supplémentaire n'est déclenché.
Sur les quatre modèles, le délai **modèle local prêt → première frame rendue**
passe de **4 981–5 124 ms à 254–370 ms**. Le délai roster autorisé → liste visible
passe de 4 862 à 118 ms dans la fixture à transition contrôlée.

Le chargement interne d'un personnage déjà généré n'affiche aucun texte,
spinner ou faux message d'indisponibilité. Une trace DOM continue le vérifie
pour les quatre personnages. Les vrais états de préparation, d'absence du
client et d'indisponibilité restent signalés. Le maintien de l'ancienne vue
pendant un changement est décrit ci-dessous.

Les preuves et limites de mesure sont dans
`artifacts/atlas-friend-visible-latency/README.md`, `final.json`,
`lifecycle.json` et `pipeline`. La suite Node complète contient 124 tests
réussis et deux oracles Canvas facultatifs ignorés. Les tests frontend vérifient
également la pause/reprise, les réponses reçues après fermeture, l'absence de
requêtes concurrentes dupliquées et la libération des ressources GPU.

Cette correction modifie les sources JavaScript/HTML utilisées par le
`ServerPath` du client local installé. Elle ne nécessite pas de remplacer à
nouveau l'exécutable ; une relance du lanceur recharge le helper et la vue.

### Parcours intégré avec exports réels

Le test ouvre `launcher.html` avant la réponse autorisée du roster, sélectionne
le personnage C dès l'apparition de la liste, puis attend une frame réellement rendue
et une progression de son animation. Le premier processus utilise un dossier
de données vide ; le second utilise le même cache après l'arrêt du premier.

| Depuis la navigation dans la vue | Cache vide | Nouveau helper, cache conservé |
|---|---:|---:|
| Personnage C visible | 10,994 s | 475 ms |
| Personnage C animé | 11,055 s | 495 ms |
| Quatre modèles prêts localement | 17,678 s | 90 ms |
| Exports effectués | 4 | 0 |

Le personnage C est le premier export lancé : la sélection précède bien la préparation
des modèles. La réouverture et les quatre changements de personnage avec cache
conservé ne produisent aucun texte dans la zone du modèle. Les 38 icônes et
109 ressources sont accessibles par les routes HTTP du produit et identiques
entre les deux processus. Les helpers de test ont été arrêtés proprement.

La première génération prend donc encore environ onze secondes dans ce
scénario. Les valeurs excluent le délai réseau réel de l'API et le démarrage
natif WPF/WebView ; elles ne sont pas une promesse de délai total après le clic
dans le lanceur utilisateur. Sources et traces :
`artifacts/atlas-friend-visible-latency/end-to-end/report.json`.

Un contrôle distinct annule deux vrais sous-processus de fixture pendant leur
préparation. Les deux événements `close` précèdent le retour de `stop`, en
10,56 ms ; aucun processus enfant, worker ou dossier temporaire ne reste.
Preuve : `pipeline/real-cancellation/report.json` dans le même dossier de
validation.

## Transitions entre personnages

Changer immédiatement l'URL de l'iframe effaçait l'ensemble du personnage,
de l'équipement et des statistiques pendant le chargement suivant. La vue
actuelle est maintenant conservée pendant la préparation d'une seconde vue,
de même taille et transparente. La nouvelle vue se signale après sa première
frame réellement rendue ; elle apparaît alors par un fondu de 180 ms au-dessus
de l'ancienne, qui reste opaque et est retirée à la fin. Le panneau ne passe
donc plus par un fond vide. Le premier affichage du profil continue à rendre
l'équipement consultable pendant une éventuelle génération.

La dernière sélection prend le dessus sur les demandes précédentes. Deux
iframes au maximum existent pendant une transition, une seule après. Le
masquage et la fermeture de la page annulent la préparation en attente ; le
retour reprend la sélection. Lorsque la réduction des animations est activée,
la vue prête remplace directement l'ancienne, sans fondu.

Une erreur HTTP du document ou du module restaure la sélection précédente
et son interaction, avec une notification permettant de réessayer. Un échec
réel du modèle présente son état d'erreur sans bloquer le changement. La
notification de disponibilité vérifie l'origine, la fenêtre source et
l'identifiant du personnage demandé.

Validation headless finale sur les quatre modèles réels : 803 échantillons
pendant les changements ordinaires, les clics rapides et l'interruption d'un
fondu ; aucun panneau vide ni texte de chargement. La limite de deux vues,
le nettoyage, la reprise, la réduction des animations et les erreurs HTTP
du document, du module et d'une ressource GLTF sont vérifiés. Les captures
ont été inspectées et les processus de test arrêtés. Preuves :
`artifacts/atlas-character-transitions/report.json`.

Ces modifications JavaScript/HTML/CSS sont chargées depuis le `ServerPath`
local configuré. Une relance du lanceur recharge la vue ; l'exécutable et
le cercle natif d'ouverture du profil restent ceux déjà livrés.
