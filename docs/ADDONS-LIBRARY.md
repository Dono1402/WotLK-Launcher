# Bibliothèque Addons

L’onglet conserve le cadre fixe du launcher et le décor de la Citadelle. Le titre compact, les noms à 15 px et les informations secondaires à 12 px libèrent de la place pour les addons. La fiche détaillée existante est conservée.

## Catalogue et sélections

- Filtres Tous, Installés, Mises à jour, Favoris et Manuels, catégorie et tri.
- Recherche avec Ctrl+F, effacement explicite, parcours clavier et actions séparées pour les détails et la maintenance.
- Favoris et profils enregistrés localement pour chaque dossier de jeu.
- Packs et profils préparent une sélection à vérifier avant installation. Un profil représente des identifiants d’addons : il ne contient ni fichiers d’addons, ni réglages WTF/SavedVariables, ni activation par personnage.
- L’import JSON prépare la sélection sans lancer de téléchargement ni modifier le jeu. L’export passe par un emplacement choisi par l’utilisateur.

## Installations et dépendances

Le plan ordonne les dépendances avant les addons qui les utilisent. Les composants déjà à jour sont conservés ; les dépendances manquantes, cycles et conflits de dossiers empêchent le démarrage. La sélection de plusieurs addons, l’ajout de dépendances ou le remplacement d’une installation manuelle présente une confirmation avec les composants concernés.

Le plan et le dossier de jeu sont contrôlés à nouveau après cette confirmation. Le moteur effectue également un précontrôle de tous les remplacements avant la première mutation. La suppression d’un addon est refusée lorsqu’un addon installé en dépend, y compris lorsque cette dépendance provient d’un TOC local.

La progression de groupe conserve son bouton d’annulation pendant toute l’opération. Les échecs et les composants non traités sont conservés pour permettre une reprise ciblée. Les opérations restent accessibles lorsque le jeu tourne, conformément au comportement du launcher depuis la version 1.5.0.

## Inventaire et intégrité

L’inventaire parcourt aussi les dossiers absents du catalogue et lit les métadonnées TOC disponibles. Ces addons apparaissent comme installations manuelles. Un dossier connu du catalogue ne devient géré par Atlas qu’après confirmation de son remplacement et installation réussie du paquet Atlas.

Chaque nouvelle installation gérée enregistre une référence des tailles et empreintes SHA-256 de ses fichiers. « Vérifier » relit les fichiers sans les modifier et détecte les fichiers manquants, modifiés ou supplémentaires. Une ancienne installation dépourvue de référence reste « non vérifiée » ; elle ne reçoit pas un résultat d’intégrité inventé. « Réinstaller » récupère le paquet validé et remplace ses dossiers gérés.

La compatibilité déclarée par la version d’interface TOC est distincte d’une validation sur Atlas. Un indicateur de validation Atlas demande une preuve explicite dans le catalogue. Les éventuelles limitations sont affichées sans déduire la compatibilité en jeu de la seule valeur `30403`.

Il n’y a pas de sauvegarde conservée ni de commande de retour à la version précédente. Les dossiers temporaires utilisés pour restaurer une transaction interrompue gardent leur fonction technique existante.

## Menu de la zone de notification

À l’ouverture du menu natif, le launcher réaffirme le premier plan et la position supérieure du menu lui-même. Cela évite que les commandes Ouvrir et Quitter se retrouvent derrière le panneau Windows des icônes masquées. La fermeture du menu termine la séquence native sans ouvrir la fenêtre principale.

## Vérification locale

Les suites Addons couvrent le moteur avec faux services et archives locales, les dépendances, l’intégrité, l’inventaire, les commandes et la persistance des profils. Le rendu WPF utilise un catalogue synthétique, des fenêtres hors écran et `WS_EX_NOACTIVATE` ; il ne pilote ni le launcher de l’utilisateur ni un dossier de jeu réel. Les captures françaises et anglaises vérifient les dimensions fixes, les listes, les recherches, les commandes et la fiche existante.

Le test du menu de notification vérifie l’ordre des appels natifs à travers une interface simulée. Il ne remplace pas une reproduction interactive du panneau Windows sur la machine de l’utilisateur.

Validation du 8 septembre 2026 : compilation Release sans avertissement ni erreur ; 72 assertions WPF FR/EN, 78 assertions de bibliothèque et persistance, 23 assertions de commandes, 29 assertions de dépendances et 43 assertions d’intégrité, ainsi que la suite runtime existante et ses nouveaux scénarios de reprise. Le menu de notification passe 26 assertions. Un scénario de lien symbolique est signalé `SKIP`, cet hôte ne permettant pas de créer le lien synthétique. Aucun test ne modifie une installation de jeu réelle.
