# Activation des formats médias — 7 septembre 2026

Le serveur du launcher accepte désormais le registre étendu de 52 extensions, dont 38 audio/vidéo, décrit dans [le suivi Messages](media-followup-20260907.md). La mise en production autorisée à **13:22:07 UTC** est terminée à **13:38:35 UTC**, après **9,88 secondes mesurées entre la demande d'arrêt de l'API et sa nouvelle réponse de santé**. Le serveur de jeu et Hermes n'ont pas redémarré. Aucun retour arrière n'a été nécessaire.

Tous les horaires ci-dessous sont en UTC ; Paris est à UTC+2 ce jour-là.

## Vérifications effectuées

| Étape | Résultat |
|---|---|
| Préflight à 13:24:07 | Ancienne API active, schéma 8, versions et configurations relevées. |
| Préparation à 13:35:26 | Ancien serveur, configurations et base d'authentification sauvegardés ; copies et archive relues par empreinte. |
| Instance Linux isolée à 13:35:50 | 83/83 contrôles réussis, puis arrêt et nettoyage de la base, du compte MySQL, de l'environnement et des médias temporaires. |
| Bascule à 13:38:01 | Arrêt de la seule API, remplacement atomique du lien de release, redémarrage avec les configurations existantes. |
| Nouvelle API en santé à 13:38:11 | HTTP 200 local ; intervalle mesuré de 9,88 s. |
| Validation de production à 13:38:35 | 83/83 contrôles réussis et données synthétiques nettoyées. |
| Vérification finale à 13:40:05 | API stable, santé publique HTTP 200, accès privés conformes, services de jeu inchangés. |

Les deux passages de 83 contrôles conservent les 55 contrôles de présence et de chat déjà utilisés : statuts, absence automatique, confidentialité hors ligne, chat v1/v2, groupes, événements, accusés de lecture et pièce jointe texte. Les 28 contrôles supplémentaires utilisent un vrai M4A AAC et un vrai MKV issus du corpus FFmpeg local. Ils vérifient :

- Transfert en deux parties avec reprise à l'offset confirmé.
- Empreinte exacte, type audio/vidéo et MIME déterminés par le serveur malgré un MIME déclaré incorrect.
- Finalisation idempotente et accès impossible avant association à un message.
- Pièce jointe visible dans l'historique du destinataire ; refus d'un accès anonyme ou depuis un compte extérieur à la conversation.
- Téléchargement complet et deux plages d'octets, avec contenu exact, `Content-Type`, `no-store` et `nosniff`.
- Suppression du message et révocation immédiate de l'accès pour les deux participants.
- Refus d'une page HTML renommée MKV, impossibilité de l'envoyer, puis annulation du brouillon.

Chaque passage utilise trois comptes synthétiques sans personnages. Le nettoyage confirme zéro compte synthétique et zéro ligne de présence associée restants, avec deux conversations et les trois uploads encore présents supprimés. Le brouillon invalide avait déjà été annulé par le test. Les contrôles du nettoyage refusent toute référence à un compte, une conversation ou un fichier extérieur à ces données synthétiques.

## État final confirmé

- API active avec le PID `1866560`, stable pendant les vérifications.
- Serveur de jeu : PID `1461668`, démarrage monotone `9017890131594`, inchangés.
- Hermes : PID `1732189`, démarrage monotone `9134521260266`, inchangés.
- Ports de jeu 1119, 8084, 8086 et 4000 en écoute ; port du canary 14323 libéré.
- Schéma **8 inchangé**, y compris les huit lignes d'historique des migrations et leurs dates. Les métadonnées des 23 tables du launcher correspondent à la sauvegarde avant bascule : 171 colonnes, 93 entrées d'index, 34 clés étrangères et 18 contraintes CHECK. Les correspondances des messages v1/v2 restent valides.
- Configurations, ensemble des fichiers de surcharge du service et paramètres d'environnement contrôlés identiques au préflight. Aucune nouvelle migration, modification de configuration ou recharge de systemd.
- Santé publique HTTP 200. Les routes chat v1, chat v2 et présence renvoient HTTP 401 sans authentification, avec `Cache-Control: no-store`.
- Répertoire de médias privé conservé en mode 0700.
- **Zéro erreur et zéro avertissement** dans les 15 entrées de journal examinées depuis la bascule jusqu'à 13:40:05 UTC.

## Version et sauvegarde

| Élément | Référence |
|---|---|
| Sources vérifiées de cette livraison | `cf8ebcfc320e04cac6e3f5aa096479fb492ec8ef` |
| Release active | `/opt/wotlk-launcher-api-releases/media-20260907T132544Z` |
| Ancienne release conservée | `/opt/wotlk-launcher-api-releases/presence-20260907T111217Z` |
| Sauvegarde privée | `/opt/wotlk-launcher-api-backups/media-20260907T132544Z` |
| SHA-256 du binaire actif | `61fe586b68efd0eec363313cbdb98b74d2b21fded32d6eef3d1916ca590ac567` |
| SHA-256 de l'archive candidate | `2fbbfc9b4362c805d42007631001c102422b95806ecb7c3cd2a0980732b8ae86` |
| SHA-256 de la sauvegarde auth compressée | `fc430acab8c00f885321d4cf9e2cd608c3a20dfbdd6ce274bc07550732ee2676` |

La sauvegarde auth contient 212 486 octets compressés, relus intégralement en 664 524 octets. Elle reste sur le serveur avec les configurations privées. Le mécanisme de retour arrière concerne le binaire ; il ne restaure pas toute la base. Ses chemins de succès et d'échec du nettoyage ou des diagnostics ont été exercés localement par mocks, avec 11 tests réussis. Aucun rollback réel n'a été exécuté.

Le client habituel déjà installé conserve le SHA-256 `ee7107430740a8693c49db179ff5a8a5429a03245ebec08e77f72fec9421b8c8` et sa configuration conserve `b174a090de95a39c164f1d7ccb11e4d0c395b75b0aa8a814d97450222b5c2445`. Cette activation n'a lancé, fermé ni piloté le launcher utilisateur.

Les preuves sans secrets sont regroupées hors Git dans `artifacts/atlas-chat-followup-20260907/deployment/server-evidence.json` et `artifacts/atlas-chat-followup-20260907/production-verification.json`. Les rapports de préparation du client et du candidat restent des photographies antérieures au déploiement ; leurs indicateurs historiques `productionDeployed: false` ne décrivent pas cet état final.

L'acceptation et le transport d'un conteneur ne garantissent pas sa lecture avec chaque codec par WebView2. Le comportement de repli et les limites de validation sont décrits dans le suivi Messages. Les essais de production vérifient l'API avec des comptes synthétiques ; ils ne constituent pas une session manuelle dans le launcher utilisateur.
