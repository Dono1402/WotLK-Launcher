# Atlas Launcher 1.4.0

Interface repensée, lancement du jeu mieux encadré, présence des amis enrichie, compte et addons améliorés. Le profil et l’armurerie 3D rejoignent le client public préparé pour la version 1.4.0.

Publication : 2026-09-06T18:31:16Z

## Launcher

- Refonte de la page Jeu et des pages Addons, Notes de version et Paramètres autour d’un décor de la Citadelle, d’un visuel panoramique du Roi-liche et de nouvelles surfaces bleues.
- Nouvelle typographie commune, textes secondaires plus contrastés et rendu du texte amélioré dans les pages, menus et fenêtres du launcher.
- Barre de navigation allégée, accès aux amis par une icône et version du launcher regroupée dans les paramètres.
- La fenêtre adopte un format fixe et peut être déplacée depuis la barre de navigation ou la marge supérieure vide.
- Les fenêtres de connexion, le menu du profil, les amis, le recadrage et le centre d’activité adoptent la même présentation. Les panneaux superposés masquent correctement le contenu placé derrière.
- Suppression des sous-titres, cadres de focus et infobulles d’action redondants. Les détails des textes tronqués et les raisons d’indisponibilité restent accessibles.

## Jeu

- L’état du serveur est affiché au-dessus du bouton Jouer et actualisé automatiquement.
- Le bouton Jouer indique le lancement puis l’utilisation du jeu, et redevient disponible à sa fermeture.
- Lorsque le serveur est confirmé hors ligne, le bouton affiche Serveur indisponible et empêche le lancement. Le retour en ligne permet de jouer à nouveau ; les outils de maintenance restent accessibles.
- Les textes de progression du téléchargement et de la vérification disposent d’espaces séparés pour éviter leur superposition.
- Amélioration de la prise en charge des autorisations Windows lorsque le dossier du jeu n’est pas accessible en écriture.

## Profil et compte

- Les accès Gérer mon profil et Gérer mon compte sont séparés pour distinguer le profil public des réglages du compte.
- Ajout d’une bio et d’un statut personnel visibles sur le profil consulté par les amis.
- Les photos de profil jusqu’à 25 Mo sont acceptées. Un clic sur l’avatar permet de choisir une photo, puis de la recadrer avec déplacement et zoom à la molette.
- Correction de la cohérence entre le cadrage et l’aperçu, de la mise à jour de l’avatar après validation et de sa synchronisation après reconnexion.
- Les pages Sécurité et Sessions, les champs de saisie et les états d’enregistrement ont été réorganisés pour rendre les actions plus lisibles.
- L’état de vérification est indiqué à côté de l’adresse e-mail. L’avertissement apparaît uniquement lorsqu’une confirmation est nécessaire, et le champ de nouvelle adresse reste vide à l’ouverture.
- Une reconnexion depuis le même appareil ne crée plus de doublons dans les sessions. La déconnexion ramène à la connexion sans laisser le compte ni le jeu visibles derrière.

## Amis et présence

- Liste d’amis réorganisée avec recherche, demandes d’amitié et informations de présence plus lisibles.
- La présence distingue désormais Connecté au launcher et En jeu. Un ami peut apparaître en ligne même si aucun de ses personnages n’est connecté au jeu.
- Le compteur, l’ordre des amis en ligne et la dernière présence prennent en compte la connexion au launcher.
- Un clic sur un ami ouvre sa photo, son pseudo, son statut, sa bio et ses personnages, avec leur classe, niveau, zone et dernière présence.
- Le personnage mis en avant n’est plus répété dans les autres personnages. Hors ligne, il est présenté comme Dernier personnage joué ; les icônes et couleurs de classe facilitent la lecture.
- Les nouvelles demandes d’ami sont notifiées. Une notification sonore, désactivable dans les paramètres, signale les connexions d’amis ; le passage du launcher au jeu ne déclenche pas une seconde notification.
- Ajout d’une confirmation avant de retirer un ami, avec menus, sélection, retour à la liste et fermeture par Échap améliorés.

## Addons

- Catalogue plus compact, descriptions sur une ligne et versions abrégées. Les informations complètes restent accessibles dans les détails et les infobulles.
- La recherche conserve les espaces pendant la saisie, notamment pour les noms composés comme Deadly Boss.
- L’actualisation d’une fiche ne déplace plus le focus vers sa croix de fermeture.
- La confirmation de suppression conserve le clavier dans sa fenêtre. Les libellés de filtre et de mise à jour du catalogue ont également été complétés en anglais.

## Paramètres et Windows

- Interface disponible en français et en anglais, avec changement de langue sans redémarrage.
- Paramètres réorganisés, descriptions répétitives retirées et interrupteurs arrondis avec animation et curseur adaptés.
- L’option Démarrer avec Windows lance désormais le launcher réduit dans la barre des tâches, sans prendre le focus. L’ancien réglage de démarrage est repris automatiquement.
- Une seule instance du launcher est ouverte à la fois. Un second démarrage automatique ne remet pas la fenêtre existante au premier plan.
- Le launcher reste accessible dans la zone de notification Windows. Selon le réglage choisi, fermer la fenêtre peut l’y ranger et retirer son bouton de la barre des tâches.

## Notes de version et mises à jour

- L’onglet Notes de version dispose d’une page dédiée, avec les nouveautés regroupées par catégorie et les versions précédentes conservées.
- Lecture des notes améliorée : texte plus grand, lignes moins longues et interligne plus aéré.
- Un bouton vert apparaît dans la barre du haut lorsqu’une mise à jour du launcher est disponible. Il lance son installation et ouvre le suivi du téléchargement.
- La version et l’état de mise à jour sont réunis sur une ligne dans les paramètres. Les états non vérifié, recherche en cours, à jour, mise à jour disponible et erreur sont distingués.

## Corrections d’interaction

- Sur l’écran d’authentification, Entrée respecte le champ ou le bouton utilisé et ne soumet plus la connexion depuis l’onglet Inscription.
- Un clic en dehors du menu du profil conserve le focus sur le contrôle choisi. Dans l’interface principale, Tab parcourt les champs de saisie sans passer par les boutons, interrupteurs et lignes d’addons.
- Les formulaires du compte et le recadrage protègent les modifications en cours. Les erreurs, la progression et les boutons de validation restent visibles pendant l’enregistrement.

## Profil et armurerie 3D

- Le profil immersif et l’armurerie 3D sont intégrés au client public, accessibles depuis Gérer mon profil.
- Consultation des personnages du compte avec recherche et sélection du personnage à afficher.
- Aperçu 3D animé reprenant l’apparence et l’équipement du personnage, avec rotation, zoom, recentrage, pause de l’animation et affichage des armes.
- Équipement présenté par emplacement, avec infobulles d’objets en français et en anglais. Les statistiques utilisent le dernier relevé serveur disponible et signalent les valeurs manquantes.
- Édition de l’avatar, de la bio et du statut regroupée dans le profil. La photo, le nom et les textes de présentation sont agrandis.
- Bannière personnalisée enregistrée localement pour chaque compte, avec import, remplacement, réinitialisation et aperçu avant validation.
- Recadrage de la bannière par déplacement de l’image, curseur et zoom à la molette de 100 à 300 %. Annuler conserve la bannière enregistrée.
- La barre de navigation apparaît au survol de toute la bannière, avatar compris, et commence à se masquer dès que le pointeur en sort. Elle reste accessible pendant l’utilisation de ses menus.
- L’ouverture et la fermeture de l’édition du profil conservent le zoom et l’orientation de la caméra 3D. Un refus du sélecteur d’image ne laisse plus les boutons bloqués.

## Installation et distribution

- Le launcher inclut les composants nécessaires au profil et à l’armurerie, sans installation manuelle d’outils supplémentaires.
- Les personnages et leur équipement sont chargés à partir du compte connecté. L’armurerie donne accès uniquement aux personnages de ce compte.
- Si le composant Microsoft WebView2 est absent ou trop ancien, le launcher installe automatiquement la version nécessaire à l’ouverture du profil.
