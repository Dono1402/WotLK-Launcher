# Livraison Atlas Launcher 1.7.0

La boutique et le changement de nom natif ont été activés sur Atlas le
12 septembre 2026, après accord explicite pour la maintenance World/Hermes/API.
La distribution du launcher sur le serveur a été vérifiée à 09:52 UTC, puis les
services et leurs ports à 09:54 UTC. L'API annonce le changement de nom disponible.
La [release GitHub v1.7.0](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.7.0)
est publique et marquée comme dernière version depuis 10:00:58 UTC (12:00 à Paris).
Ses six fichiers ont été comparés aux tailles et SHA-256 attendus ; le lien de
téléchargement de la dernière version répond 200 avec 518 851 673 octets.

## Fonctionnement livré

L'achat accorde un service au compte. Le personnage bénéficiaire et son nouveau
nom sont choisis depuis la sélection des personnages. Un achat pendant une partie
ne force pas une déconnexion. Le joueur peut revenir ensuite à la sélection des
personnages et utiliser le service, ou l'annuler avant utilisation. Les soldes en
euros et en Crédits Atlas restent séparés.

Le sésame, les changements de faction/race, la conversion réelle d'or et les
paiements automatiques restent indisponibles. La recharge manuelle PayPal n'a
pas été activée pendant cette livraison.

## Production

- World : candidat `atlas-shop-rename-native-20260912`, quatre workers de base
  personnages conservés et heartbeat du protocole natif 2 actif.
- Hermes : release `hermes-shop-native-1.7.0-20260912`, configuration publique et
  répertoires partagés conservés.
- API : release `shop-native-1.7.0-20260912`, schéma 13, services de compte et
  changement de nom activés. Les huit migrations antérieures conservent leurs
  métadonnées exactes.
- Auth : processus préexistant conservé, sans redémarrage.
- Client : 413 487 932 octets ; installateur : 518 851 673 octets. Les empreintes
  figurent dans [release.json](release.json) et [SHA256SUMS.txt](SHA256SUMS.txt).
- Les trois alias de téléchargement, le manifeste signé et les notes publiques
  ont été actualisés. Les notes historiques et les fichiers versionnés 1.6.0
  restent présents.

Le tag `v1.7.0` désigne `ee2ea060ed29db30b6a16a70a807fadc17f00172`, qui contient
les sources de la version et son dossier de préparation. Les preuves de
publication et les scripts d'exploitation sont ajoutés ensuite, sans reconstruire
les exécutables gelés. Le suffixe informatif embarqué de l'API identifie son
commit de base `f0a0354`; cette distinction est consignée dans
[preparation.json](preparation.json).

## Vérifications

Les octets définitifs API/World/Hermes ont passé 19 contrôles dans le royaume
privé : SSO et session chiffrée 3.4.3, service non affecté au compte, validation
Unicode, confirmation et reçu sans second débit, annulation/remboursement,
achat pendant une partie, véritable déconnexion du personnage, utilisation après
retour à la sélection et reconnexion après perte de socket.

En production, trois comptes synthétiques temporaires ont permis de lire la
boutique par HTTPS avant l'ouverture des achats, après ouverture, puis après
publication du client. Les deux soldes, l'historique vide et la disponibilité
des services de compte ont été contrôlés. Les notes 1.7.0 sont lisibles avec
authentification ; les accès anonymes restent refusés. Ces comptes et leurs
sessions ont été supprimés. Aucun achat, débit ou recharge réel n'a été créé par
ces vérifications.

Le client et l'installateur ont été téléchargés entièrement via leurs URL HTTPS
finales et hachés avant l'annonce de la mise à jour. Le manifeste public a été
vérifié depuis Windows avec le code du launcher et son ancre de confiance
embarquée : signature ECDSA P-256, URL, taille et SHA-256 conformes. Le test de
l'installateur a confirmé son payload exact sans installation sur le poste.
Les notes françaises/anglaises, le remplacement atomique et le runtime
d'armurerie ont également passé leurs contrôles ciblés.

Le jeu graphique de l'utilisateur n'a pas été ouvert. La validation du parcours
en jeu est celle du protocole réel dans le royaume privé ; aucune observation
graphique d'un achat en production n'est revendiquée.

## Sauvegarde et retour arrière

Une nouvelle sauvegarde des trois schémas auth/personnages/playerbots a été
prise après l'arrêt des services écrivains et avant la migration. Le dump gzip
fait 109 300 594 octets ; son intégrité et son SHA-256 ont été vérifiés. Les
configurations privées restent sauvegardées sur le serveur. La restauration
complète de ce dump n'a pas été répétée.

Le retour opérationnel ferme les achats, conserve l'API compatible avec le
schéma 13, puis restaure les anciens World/Hermes. Il conserve les soldes,
commandes et reçus. Ne pas remettre l'ancienne API limitée au schéma 8 ni
restaurer une ancienne base par-dessus les écritures intervenues depuis la
reprise du jeu. La procédure complète figure dans le
[dossier d'exploitation](../../scripts/atlas-shop-release/README.md).

Preuves : [production](deployment-result.json), [GitHub](github-publication.json), [préparation](preparation.json),
[manifeste signé](launcher-update.json) et [métadonnées de version](release.json).
