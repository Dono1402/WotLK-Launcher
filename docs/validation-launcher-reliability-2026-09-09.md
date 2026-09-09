# Fiabilité du launcher — 9 septembre 2026

Périmètre : les cinq améliorations retenues après la revue, en conservant le nettoyage des confirmations et textes d’attente du commit `1474eb0`.

## Changements

1. **Brouillons.** La temporisation de 250 ms après la dernière saisie est complétée par un point de sauvegarde après une seconde de frappe continue. Le résultat natif est vérifié ; un échec conserve le texte, affiche uniquement une erreur localisée et déclenche une nouvelle tentative après trois secondes. Les réponses anciennes ne remplacent pas une saisie plus récente et ne passent pas d’une session à une autre. Les changements de conversation transmettent également le brouillon sortant. Aucune ligne « Enregistrement… » ou « Brouillon conservé » n’est réintroduite.
2. **Lecture locale de Messages.** Le rafraîchissement et la boucle de reconnexion retentent l’initialisation après un échec, avec la temporisation progressive de reconnexion existante. Les lectures concurrentes sont sérialisées ; un workspace initialisé n’est pas rechargé par-dessus des brouillons récents. Les données existantes et l’état des messages en attente sont conservés.
3. **Relance du moteur Messages.** La correction livrée dans `1474eb0` est revérifiée avec un crash réel du renderer de test : la bascule vers le mode de secours puis le retour conserve le bouton Réessayer. Sa relance retrouve la conversation et le brouillon natif sans rejouer d’action métier.
4. **Avatars.** Une image décodée est publiée en mémoire avant l’entretien du disque. Les écritures sont regroupées par fenêtres de 250 ms et le nettoyage s’effectue après un lot, hors du chemin d’affichage. Les erreurs de disque restent sans effet sur l’image disponible. Chaque lot en attente est limité à 32 éléments et 4 Mio de données PNG ; un lot actif et le suivant peuvent coexister. Les plafonds précédents de 16 Mio/256 images décodées et 64 Mio sur disque sont conservés ; la limite disque est rétablie lors de la maintenance lorsque les fichiers sont accessibles.
5. **Réglages.** Les huit opérations du coordinateur exposent une API asynchrone. Les écritures de préférences et de texte de quête s’exécutent hors du thread WPF. Les contrôles concernés deviennent indisponibles pendant l’écriture, sans texte de confirmation supplémentaire. Un échec restaure les valeurs et les contrôles après application du snapshot d’erreur, en préservant les bindings. La fermeture attend la sauvegarde acceptée dans son délai de grâce habituel. Le remplacement atomique et la sauvegarde de secours des réglages sont conservés.

## Validation

Compilation de l’application et des tests : zéro avertissement et zéro erreur.

| Vérification | Résultat |
| --- | --- |
| `chat-dom-tests.cjs` | 309 contrôles : frappe continue, texte complet après pause, erreur puis nouvelle tentative sans frappe, réponses tardives, isolation de session, FR/EN et régressions de messagerie/médias. |
| `--chat-workspace` | 217 assertions : lecture défaillante puis récupération manuelle/concurrente ; JSON invalide puis récupération automatique après réparation ; conservation du brouillon et de l’état de la file d’envoi ; protections de session, envois et transferts existants. |
| `--chat-rich-host-wpf` | 206 assertions, incluant crash du renderer, aller-retour entre modes et relance sans rejeu d’action. |
| `--account-avatar-client` | Cache disque rendu inutilisable par un fichier obstacle : image décodée toujours disponible, conservée en mémoire, sans nouveau téléchargement ni modification de l’obstacle ; déduplication, variantes et plafond disque. |
| `--settings-runtime` | Disque synthétique bloqué alors que le dispatcher reste réactif ; réglages désactivés ; échec puis retour de la valeur et de l’interface ; concurrence et attente de fermeture. |
| `--settings-preview` | Isolation, navigation et disposition aux tailles prises en charge. Les anciens chiffres fixes du panneau sont remplacés par des vérifications de lisibilité, d’encombrement et d’absence de débordement adaptées à sa disposition actuelle. |
| `--optimization-storage` | Écriture atomique, copie de secours, fichier absent ou corrompu, écriture verrouillée et signalement de récupération. |
| `--optimization-memory` | Plafonds et éviction du cache décodé, publication après destruction, collecte des contrôles traduits détachés. |
| `--friends-runtime` | Préférences de notification et fonctionnement du runtime amis après migration de l’API de réglages. |
| `--runtime-composition`, `--runtime-hardening` | Composition et fermeture des composants du launcher. |

Les comptes, fichiers et services de ces tests sont synthétiques. Les tests graphiques utilisent Edge sans interface ou des fenêtres WPF inactives hors écran. Aucun launcher utilisateur, client de jeu ou service de production n’est lancé.

## Limites

Une sauvegarde de brouillon n’est durable qu’après son écriture native : un crash peut encore faire perdre la saisie qui n’a pas atteint ce point. La reprise du stockage local retente une lecture ; elle ne remplace pas silencieusement un document Messages définitivement corrompu par un document vide. Si le défaut persiste, l’erreur reste explicite et les données originales restent intactes. La copie de secours mentionnée ci-dessus concerne les réglages.

Ces tests vérifient le comportement et la réactivité sous panne simulée. Aucun gain de démarrage, de CPU ou de mémoire globale n’est chiffré dans cette livraison.
