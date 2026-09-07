# Atlas Messages — résultat de la refonte

Atlas Messages est déployé depuis le 7 septembre 2026. Le client local habituel a été remplacé à 10 h 46 (Paris), après vérification de sa fermeture et sauvegarde de l’ancien fichier. Après l’autorisation de l’utilisateur à 10 h 52 min 01 s, l’API v2 et la migration 7 ont été activées ; le déploiement s’est terminé à 11 h 17 min 05 s. Seule l’API du launcher a été redémarrée, avec une indisponibilité mesurée de 2,125 secondes. Le serveur de jeu et Hermes ont conservé leurs processus. Les résultats de production et leurs limites sont détaillés dans le [rapport de déploiement](deployment-report.md).

## Fonctionnalités livrées dans le code

La page complète réunit les conversations à gauche et la discussion à droite. Elle comprend recherche de contacts et conversations, filtre Non lus, avatars et présence, conversations épinglées et archivées, messages épinglés partagés, groupes avec invitations explicites, image, nom et gestion des membres.

Les messages prennent en charge Markdown et spoilers, réponses, réactions, édition et suppression par leur auteur, sélection et copie du texte. Les cartes Atlas permettent de partager un objet, un personnage, une quête, un lieu ou une sortie avec date et rôles. Une carte personnage ouvre le profil du compte ; elle ne force pas la sélection d’un personnage dans l’armurerie.

Images et GIF personnels, PDF/textes/documents bureautiques, audio et vidéo sont acceptés dans la limite de **500 000 000 octets par fichier**. Le sélecteur, le dépôt et le collage natif alimentent une prévisualisation avant envoi. Les GIF copiés comme fichiers gardent leur animation. Les transferts ont une progression, une reprise et une annulation ; les originaux de l’utilisateur restent intacts. Limites techniques de cette version : dix fichiers par message et cinquante membres par groupe.

YouTube et Vimeo disposent d’un lecteur intégré ; les médias directs compatibles et les autres pages disposent de lecteurs ou de cartes de métadonnées. Plusieurs aperçus restent dans l’ordre de leurs liens. La croix d’un aperçu non vidéo retire cet aperçu pour tous les participants, de façon persistante, sans retirer le texte. Les aperçus vidéo n’ont pas cette action.

Les brouillons et la boîte d’envoi sont persistés par compte et environnement. Le même identifiant d’envoi est conservé après une réponse perdue ou un redémarrage. DND est global et manuel ; le partage Lu/Saisie est réglable globalement. Le curseur de lecture privé reste distinct du partage Lu. **Les sons Messages restent désactivés.**

Le pont existant avec le jeu conserve les messages privés textuels dans les deux sens. Le serveur transforme le Markdown en texte lisible pour ce pont tout en conservant l’original riche pour le launcher. Les groupes et les binaires ne sont pas injectés dans le chat du jeu. Un message déjà affiché en jeu ne peut pas être retiré de cet écran.

## Vérifications

| Vérification | Résultat |
|---|---|
| Compilation commune Release | 0 erreur, 0 avertissement |
| API v2, migration depuis v6 et HTTP authentifié sur MySQL 8.4.11 | 86 assertions |
| Stockage média, types, limites, URLs et projection Markdown | 105 assertions |
| Régression API v1 et contrat SQL du pont jeu | 238 assertions |
| Runtime : DPAPI, brouillons, envois, reprises, révocation, DND | 167 assertions |
| Interface DOM, actions, FR/EN, deux largeurs, CSP native | 52 assertions |
| WebView2 natif : ressources embarquées, rendu, pont, médias, isolation | 39 assertions |
| Notifications : silencieux, DND, visibilité, changement de compte | 13 assertions |
| Ancienne vue WPF conservée en compatibilité | 84 assertions |
| Ancien runtime chat, composition du launcher et métriques de passerelle | Suites réussies |
| YouTube réel dans Edge headless isolé | Lecteur chargé, vidéo muette effectivement en lecture, aucune erreur 153 |
| Candidate Linux avec schéma 7 et base canary isolée | 32 contrôles API authentifiés réussis ; fixtures supprimées |
| API de production après bascule, avec comptes synthétiques dédiés | 32 contrôles API authentifiés réussis ; fixtures supprimées |
| Schéma réel après migration | 8 nouvelles tables, 11 clés étrangères, 6 contraintes CHECK ; 1 message et 1 conversation antérieurs conservés, aucun mapping invalide |
| Contrôle après déploiement à 11 h 20 min 20 s (Paris) | Santé publique 200 ; API v1/v2 sans authentification 401 avec no-store ; 0 erreur et 0 avertissement dans les journaux API depuis la bascule |

Les tests du runtime parcourent intégralement **500 000 000 octets synthétiques**, avec des lectures bornées à 64 KiB et des blocs de 4 MiB maximum. Ils vérifient une reprise à l’offset confirmé après un bloc accepté dont la réponse a été perdue. Ils ne constituent pas un transfert de 500 Mo sur le réseau de production.

La relecture a conduit à deux protections de concurrence supplémentaires : une réponse tardive ne peut pas réintroduire un fil dont l’accès a été révoqué, ni rétablir une ancienne préférence DND. Ces scénarios ont leurs tests de régression.

Le test YouTube utilise une vidéo publique Google for Developers, sans compte ni cookie utilisateur. L’origine et la politique de sécurité sont celles de la page Messages ; le Referer a été observé et le temps de lecture a avancé. Cette vérification Chromium complète le test séparé du host WebView2. Aucun launcher utilisateur ni jeu n’a été lancé pour les tests.

## Artefacts et preuves

- Client candidat : `artifacts/atlas-chat-refonte/client-candidate/AtlasLauncherLocal.exe`.
- Client local habituel : `artifacts/AtlasLauncherLocal/AtlasLauncherLocal.exe` ; 102 715 141 octets, SHA256 `bb48e6052eac2e27401a0002acb774b378d60b7f6f8a39a493da02c834e7b9ca`. La copie et la sauvegarde ont été vérifiées par empreinte ; la configuration d’armurerie locale est conservée. Preuve : `artifacts/atlas-chat-refonte/local-delivery.json`.
- Candidate API finale : `artifacts/atlas-chat-refonte/server-candidate-singlefile/atlas-chat-workspace-api-linux-x64-schema7.tar.gz` ; 48 541 151 octets, SHA256 `2636ec79ae2bc13a0d6cc9d84cfbc872b5915d329b35b62714357d688d0881b0`.
- L’archive serveur contient exactement `WotLK.Launcher.Server` et `libSkiaSharp.so`, relus et comparés par empreinte. Ses paramètres de production restent externes.
- Captures du rendu : `artifacts/atlas-chat-refonte/dom/chat-fr-large.png`, `chat-fr-compact.png` et `chat-en-history.png` ; host natif dans `artifacts/atlas-chat-refonte/native/`.
- Rapports détaillés : `artifacts/chat-v2-validation/RESULTAT.md`, `artifacts/atlas-chat-refonte/FRONTEND-VALIDATION.md`, `workspace-runtime-report.md`.
- Preuve vidéo réelle : `artifacts/atlas-chat-refonte/youtube-live/result.json`.
- Preuves du déploiement et de ses contrôles : `artifacts/atlas-chat-refonte/deployment/production-verification.json` et les 14 fichiers de `artifacts/atlas-chat-refonte/deployment/remote-evidence/`, avec le [rapport de déploiement](deployment-report.md) pour leur portée. L’exécutable local, sa configuration et les 75 fichiers produit du manifeste initial ont été revérifiés sans écart.
- Journaux de compilation et de tests : `artifacts/atlas-chat-refonte/` et `artifacts/chat-v2-validation/`.

La première candidate serveur multi-fichiers dans `server-candidate/` est un artefact intermédiaire. L’archive effectivement déployée est celle de `server-candidate-singlefile/`.

## Activation réalisée et limites restantes

L’activation serveur autorisée est terminée. Le [plan d’activation](deployment-plan.md) consigne l’ordre appliqué : canary sur base isolée pendant que l’ancienne API reste active, puis arrêt de cette API, sauvegarde finale, migration 0007 et bascule. Le stockage privé est actif sous `/srv/wotlk/atlas-media/chat`. Les fixtures de validation, les deux bases canary temporaires et leurs utilisateurs ont été supprimés ; aucun retour arrière n’a été nécessaire.

Les contrôles de production emploient des comptes synthétiques dédiés, sans personnage. Ils valident les API authentifiées et la projection entre v1 et v2, mais ne constituent pas un essai de messagerie dans le jeu ni un transfert de 500 Mo sur le réseau de production. Aucun launcher utilisateur ni jeu n’a été ouvert ou fermé pour ce déploiement, et aucune publication GitHub ou nouvelle version publique du launcher n’a été effectuée. Le client local livré à 10 h 46 est resté inchangé pendant la bascule serveur.
