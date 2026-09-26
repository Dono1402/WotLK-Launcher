# Atlas Launcher — cohérence globale, candidat local

Travail du 26 septembre 2026 sur `ui/redesign-v2`, après l'audit du candidat `4fb00e7` et l'accord de l'utilisateur pour appliquer les améliorations.

## Comportement livré

- Les destinations du centre d'activité portent le jeu concerné. Une opération WotLK ne renvoie plus à l'accueil Minecraft lorsque cet univers est sélectionné.
- Un profil ami conserve l'univers et le contexte de départ. Retour restitue la page, le portefeuille, l'historique, la conversion, le service de boutique ou le détail d'addon consulté. La suppression d'un ami ferme et purge son ancien profil.
- Échap ferme d'abord le sélecteur d'univers, sans fermer la page située derrière.
- La navigation ordinaire utilise une transition de 200 ms ; le changement d'univers conserve 300 ms. Les transitions peuvent être interrompues par une nouvelle navigation. Les actions entrantes restent désactivées jusqu'à la fin de leur apparition.
- La connexion dispose d'un passage visuel vers l'application. À la déconnexion, le contenu authentifié est immédiatement masqué et désactivé.
- Les panneaux et sous-pages concernés partagent la préférence Windows de réduction des animations via `AtlasMotion`.
- Les pages communes à Atlas utilisent le même fond neutre. Les accueils de jeu gardent leur thème ; la barre reste accessible dans les profils, avec un bouton Retour également disponible en cas d'échec de chargement.
- Les pages secondaires partagent une échelle de titre et une grille de marges. Les textes de l'interface emploient le tutoiement ; les nouvelles formulations sont traduites en anglais. Les anciennes notes publiées sont conservées.
- Les infobulles des deux soldes décrivent la conversion WotLK et la recharge/l'historique. La copie d'une référence et l'ouverture de PayPal affichent un résultat explicite, sans confondre ouverture du navigateur et paiement validé. Le portefeuille expose les états de chargement et de reprise existants.
- « Quitter Atlas » est accessible depuis le menu du profil. La première réduction près de l'horloge explique ce comportement ; le marqueur est sauvegardé sans changer la préférence de fermeture.
- Le brouillon local des notes décrit ces changements sous « À venir », sans réserver un numéro de version.

La fenêtre reste fixe à **1597,6 × 996,8 DIPs**. Les dimensions de la barre, la disposition des deux soldes, les composants et le bouton Jouer doré sont conservés. La connexion au serveur Minecraft et son lancement restent à implémenter : aucun statut ni nombre de joueurs WotLK n'est réutilisé pour les présenter comme des données Minecraft.

## Vérification

Build Release : **0 avertissement, 0 erreur**. Les contrôles graphiques utilisent des données synthétiques et des surfaces hors écran inactives, à la taille fixe. Aucun contrôle de la souris ou du clavier du PC utilisateur.

| Contrôle ciblé | Résultat |
| --- | --- |
| `--service-navigation-wpf` | 196 assertions : changements de jeu, retours, détails d'addon et de boutique, priorité d'Échap, transitions, réduction des animations, barre et rendus FR/EN |
| `--auth-shell-wpf` | 474 assertions : connexion, inscription, récupération, déconnexion et formulaires FR/EN |
| `--friend-profile-wpf` | Profils avec WebView2 et helper locaux de test, cache et isolation des comptes, accès en lecture seule, barre persistante, retour et retrait d'un ami |
| `--settings-runtime-headless` | Sauvegarde des réglages, dont le marqueur de première fermeture, avec stockage temporaire |
| `--tray-menu` | 26 assertions avec NotifyIcon invisible et appels Windows enregistrés ; aucune interaction avec l'Explorateur |
| `--patch-notes` | Brouillon local de trois sections et neuf entrées, traduction anglaise et conservation des notes publiées |

Captures et journaux, exclus de Git : `artifacts/global-ux-preview-20260926/` et `artifacts/launcher-global-ux-audit-20260926/`. Les profils, soldes et personnages des captures sont fictifs. Les scènes de profil utilisent le vrai navigateur intégré, sans compte de production.

## Livraison et limites

Le script existant `scripts/build-local-client.ps1` produit un exécutable local séparé, sans remplacer le launcher installé ni le précédent exécutable local. Aucun lancement de ce candidat sur le bureau, paiement réel, installation de jeu, changement de base, redémarrage de serveur ou publication de mise à jour dans ce travail.

Les vérifications confirment les parcours synthétiques et la géométrie des rendus. Le ressenti sur le bureau et l'intégration avec les services réels ne sont pas certifiés par ces tests. Le candidat et les captures sont fournis pour cette revue visuelle avant une future publication.
