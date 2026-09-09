# Retours d’état discrets — 9 septembre 2026

Les actions courtes ne créent plus de ligne « Enregistrement… » ni de bandeau de succès lorsque leur résultat est déjà visible. Les boutons gardent leur libellé et un emplacement réservé à l’indicateur. Le composant WPF commun et les indicateurs web apparaissent après 350 ms ; la rotation est désactivée lorsque les animations sont réduites.

| Zone | Comportement |
| --- | --- |
| Statut de présence | Indicateur dans le sélecteur ; suppression de la ligne sous le bouton. Le statut confirmé et l’erreur éventuelle sont conservés. |
| Amis | Suppression des phrases d’opération et bandeaux de succès. Indicateur dans le panneau et le bouton d’envoi de demande. |
| Profil et bannière | Libellés Enregistrer/Appliquer stables. Les données confirmées ferment l’éditeur sans notification verte. Un échec conserve le brouillon et le message d’erreur. |
| Avatar | Suppression du bandeau de préparation, traitement et actualisation. Pourcentage et progression réels regroupés dans le pied du recadrage ; indicateur dans le bouton. |
| Personnages et modèle 3D | Indicateurs sans phrase de chargement, personnages existants conservés pendant une actualisation. Les états indisponibles restent explicites. |
| Brouillons du chat | Suppression de la mention permanente « Brouillon conservé ». L’aperçu de conversation utilise « Brouillon » ; le mécanisme de sauvegarde est inchangé. |
| Chargement du chat | Indicateurs dans le démarrage natif, la liste, le fil vide et le choix de personnage. Le bouton d’historique conserve son libellé. |
| Recherche du chat | Compteur de résultats et indicateur compact. L’historique partiel, les erreurs et les possibilités de reprise restent signalés. |
| Copie et transferts | La copie réussie ne produit plus de toast. Les erreurs, états d’envoi, pourcentages et commandes de reprise des transferts restent disponibles. |
| Sécurité du compte | Indicateur dans les boutons d’enregistrement. Les résultats de sécurité importants et les erreurs restent affichés. |
| Appareils connectés | Suppression de la carte de chargement et de la confirmation de révocation ; indicateurs pour la liste et l’appareil concerné. |
| Catalogue et opérations addons | Suppression des phrases d’attente et succès génériques au-dessus de la liste. Progression et annulation restent sur les éléments et dans Activité. Erreurs, vérification incomplète et consigne /reload sont conservées. |
| Mise à jour et serveur de jeu | Indicateurs près de la version et du statut ; absence de phrase temporaire d’actualisation. |
| Vérification et installation du client | Étapes lisibles : Préparation, Vérification, Téléchargement, Installation, Nettoyage. Les volumes, pourcentages et autres mesures réelles sont conservés. |

Les formulaires de connexion et de déconnexion conservent également leurs libellés. Le formulaire historique de profil reçoit le même nettoyage. La barre de sauvegarde des seuls scénarios de démonstration des paramètres reste distincte : elle est déjà masquée dans le fonctionnement réel.

Le retour vers un chat dont le processus de rendu a échoué conserve maintenant le bouton Réessayer. Recréer ce rendu restaure la conversation et le brouillon sans rejouer d’action.

## Vérifications

Compilation de l’application et du projet d’intégration : zéro erreur et zéro avertissement, avec `AtlasLocalClientBuild=true` et `NuGetAudit=false`.

| Vérification | Résultat |
| --- | --- |
| Présence WPF | 992 assertions ; français/anglais, deux tailles, indicateur réellement visible après le délai, état confirmé, échec et limites de texte. |
| Connexion et déconnexion | 174 assertions dans un shell WPF sans fenêtre native. |
| Compte et avatar | Scénarios de recadrage, envoi, erreur, e-mail, mot de passe et révocation ; captures hors écran. |
| Paramètres | Persistance, échec et restauration, état connecté, statut de mise à jour et indicateur. |
| Addons | Coordinateur, opérations, annulation, lots, erreurs et interface ; 29 assertions de dépendances et 43 d’intégrité. Le cas de lien symbolique est ignoré car l’hôte ne permet pas d’en créer un dans la fixture. |
| Jeu et Activité | Coordination installation/mise à jour, progression, annulation et interface du centre d’activité. |
| Chat web | 301 contrôles DOM sur les vrais fichiers embarqués : brouillons, envoi, erreurs, pièces jointes, lecteurs et changements de conversation. |
| Recherche web | 42 contrôles : résultats, pagination, couverture partielle, erreurs, reprise et isolation des sessions. |
| Profil web | 22 contrôles dédiés : géométrie stable des boutons, succès silencieux, erreurs, brouillons conservés, bannière et réduction des animations en français/anglais. |
| Chat WebView2 | 206 assertions, dont panne réelle du rendu de test, retour vers l’écran en échec, reprise et restauration du brouillon. |
| Armurerie WebView2 | Suite complète : profil, bannière, personnages, modèle Three.js, pont natif, profils d’amis, navigation, déconnexion et reprise. |
| Notes de version | Brouillon 1.5.0 et traduction anglaise validés ; notes publiées préservées. |

Les scénarios graphiques utilisent des comptes synthétiques et des fenêtres hors écran, inactives, ou Edge sans interface. Ils n’ouvrent pas le launcher utilisateur et n’effectuent aucune validation sur un serveur de production. Les captures sont dans `artifacts/quiet-feedback/` ; la recherche conserve son répertoire habituel `artifacts/atlas-chat-search-20260907/`.

Les attentes devenues obsolètes des tests ont été actualisées : cadrage normalisé de l’avatar, largeur actuelle de l’icône Activité, et image de bannière fournie explicitement au scénario de mise en page.

Le nouveau test web se lance avec `node source/WotLK.Launcher.IntegrationTests/Frontend/quiet-feedback-tests.cjs`. Les autres scénarios utilisent les options existantes du projet d’intégration, notamment `--presence-profile-wpf`, `--account-preview`, `--settings-runtime`, `--addons-runtime`, `--game-runtime`, `--activity-runtime-wpf`, `--auth-shell-wpf`, `--chat-rich-host-wpf`, `--armory-launcher` et `--patch-notes`.
