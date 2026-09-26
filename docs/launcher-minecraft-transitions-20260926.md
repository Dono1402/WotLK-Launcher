# Navigation WotLK / Minecraft — candidat local du 26 septembre 2026

## Périmètre

Intégration du décor fourni et correction de la transition entre services. La fenêtre reste fixe à 1597,6 × 996,8 DIPs, le sélecteur entre Atlas Launcher et Jeu, la barre à 80 DIPs. Les composants, la typographie, les commandes et les données existants sont réutilisés. Le bouton principal reste doré.

Les assets d’origine et leur résolution sont documentés dans `source/WotLK.Launcher/Assets/Minecraft/README.md`. Aucun changement de serveur, installateur, version publique ou publication. Le candidat reste `1.8.2-local`, sans mise à jour automatique.

## Comportement

- Fond WPF couvrant toute la fenêtre : fondu de 300 ms, sans capture logicielle de la page sur le thread d’interface.
- Sortie du contenu précédent pendant 75 ms, puis entrée du nouveau entre 150 et 300 ms. Les titres et les contours des cartes ne se superposent pas ; les cartes ne glissent plus.
- Actions des jeux et onglets contextuels non interactifs durant leur transition. Le sélecteur et les commandes de fenêtre restent accessibles. Aucun clic n’est mémorisé pour être rejoué.
- Nouvelle sélection ou navigation pendant le fondu : reprise des valeurs animées courantes, seule la dernière destination termine la transition.
- Recomposition progressive des onglets Addons/Boutique, avec effacement des libellés avant leur rétraction. Interpolation des couleurs de la barre.
- Menu du sélecteur : ouverture 180 ms, fermeture 120 ms, fermeture non interactive. Les préférences Windows d’animation s’appliquent aux transitions et au menu.
- Dernière page propre à chaque jeu conservée pendant la session. Les pages Atlas ouvertes restent ouvertes au changement de service. Le service initial reste WotLK ; aucune nouvelle préférence persistante n’est introduite.
- Portefeuille et Crédits Atlas visibles sur les deux jeux. Leur écran existant peut s’ouvrir et revenir à la page précédente sans imposer WotLK. Le catalogue WotLK reste réservé à WotLK ; la conversion d’or mentionne explicitement son personnage source WotLK.
- Sous Minecraft, les pages communes retrouvent les surfaces Atlas sur un fond neutre, sans citadelle WotLK. La page Jeu seule porte l’ambiance émeraude.
- Paramètres du client et diagnostics WotLK masqués sous Minecraft. L’accès au profil ouvre l’éditeur Atlas existant, avec son onglet Profil ; l’entrée WotLK identifie les personnages et conserve l’armurerie.
- Minecraft explique visiblement l’absence actuelle de raccordement du lancement et du suivi. Aucun compteur illustratif de la maquette n’est utilisé comme donnée réelle.

L’armurerie native WebView2 ne prend pas en charge les fondus WPF : elle est masquée à la sortie et révélée une fois le nouveau contexte prêt, sans essayer de la transformer en image.

## Vérifications

Compilation Release de la fixture : zéro erreur, zéro avertissement. Exécution ciblée `--service-navigation-wpf` : **160 assertions validées**, à la seule taille fixe du launcher, en français et en anglais.

La fixture utilise une fenêtre WPF réelle, inactive et hors écran. Elle refuse la prise de focus, ne lance ni jeu ni launcher utilisateur et ne contacte aucun backend. Le menu est rendu sans ouvrir son popup sur le bureau. Les soldes et profils visibles sur les captures sont ceux de la fixture, pas une lecture du compte utilisateur.

Scénarios : aller-retour, inversion en cours, navigation pendant le fondu, pages communes, mémoire de page, portefeuille/retour/conversion, profil Atlas, filtrage des paramètres, modes sans animation, garde des modales et zones de clic de la barre.

Captures et mesures locales : `artifacts/minecraft-transition-preview-20260926/`. `transition-samples.json` consigne des valeurs WPF et le temps de traitement synchrone de la sélection. Les captures ralentissent volontairement les échantillons suivants : ces relevés ne constituent pas un benchmark de FPS.

La capture WotLK finale est identique à celle du début du même test. Par rapport au candidat émeraude précédent, les positions et le décor sont conservés ; la séparation des couches entraîne de minuscules différences de composition sur les contours (au maximum 2 niveaux sur 255 par canal).

Le focus clavier réel, le lecteur d’écran et le ressenti à l’écran ne sont pas certifiés par cette fixture inactive. Les achats, le lancement de jeux et les services distants ne sont pas exécutés pour cette vérification de navigation.

## Validation avant diffusion

Le pack demande une vraie capture pour validation visuelle avant publication ou push. Les captures et l’exécutable candidat séparé sont fournis pour cette étape. Le client local habituel et la version installée restent inchangés.
