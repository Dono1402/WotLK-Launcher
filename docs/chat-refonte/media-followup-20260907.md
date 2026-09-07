# Messages — aperçus, erreurs et formats du 7 septembre 2026

Le bandeau « Messages » et son sous-titre sont supprimés. La conversation et sa liste latérale utilisent la hauteur libérée, dans la fenêtre habituelle de 1597,6 × 996,8. Le fond Citadelle et la composition transparente de la finition précédente sont conservés.

## Parcours corrigés

- Avant l'envoi, les images, vidéos et sons affichent leur aperçu ou lecteur, avec une croix de retrait. Le nom, la taille et la mention « Prêt » ne s'affichent plus à côté de ces aperçus. Les libellés accessibles conservent le nom du fichier.
- Les lecteurs existants restent montés pendant les mises à jour de progression. Une lecture commencée ne repart donc pas de zéro à chaque événement de transfert.
- Un clic sur une image envoyée ouvre un agrandissement conservant ses proportions, sans cadre ni boutons Télécharger/Fermer. Un clic sur le fond ou Échap ferme l'image et rend le focus à son déclencheur. Le dépôt et le collage de fichiers sont suspendus pendant cet agrandissement.
- Les erreurs d'envoi et d'action apparaissent sur le message concerné. Les erreurs de transfert ou de retrait apparaissent sur l'aperçu concerné. Le pied du composeur n'affiche plus « Action échouée ». Une réponse tardive ne reporte pas l'erreur dans une autre conversation.
- Les avatars utilisent les statuts confirmés du profil et de la liste d'amis, y compris dans les envois en attente. Les données de personnage sont retirées lorsque le statut est hors ligne.
- Les cartes de personnage affichent le nom, le niveau et la classe localisée avec sa couleur, et une action pour ouvrir l'Armory. L'action conserve le GUID exact du personnage.

## Suppression d'un envoi échoué

Un refus définitif et une réponse réseau perdue sont distingués. Pour un ancien échec dont le serveur a pu accepter l'envoi, « Supprimer » enregistre d'abord une intention durable, puis recherche ce seul UUID par `GET /messages/by-client/{uuid}` et supprime le message serveur correspondant s'il existe. Le compte, le fil, l'UUID et l'identifiant serveur sont contrôlés. L'entrée ne repasse jamais par un POST d'envoi après cette intention.

Une réponse 404 masque l'échec local mais conserve son UUID : un commit tardif est recherché à nouveau, y compris après redémarrage. L'état de suppression retire les actions de renvoi et affiche un éventuel refus sur l'entrée. Une erreur avant l'inscription dans la file native conserve le brouillon ; « Réessayer » réutilise le même contenu et le même UUID.

## Formats

Le registre partagé par le sélecteur natif, la WebView, la préparation locale et le serveur contient 52 extensions, dont 38 audio/vidéo. La limite reste de 500 000 000 octets par fichier et dix pièces jointes par message.

| Catégorie | Extensions |
|---|---|
| Audio | MP3, MP2, AAC, M4A, M4B, FLAC, OGG, OGA, OPUS, SPX, WAV, AIF, AIFF, AIFC, WMA, MKA, WEBA |
| Vidéo | MP4, M4V, MOV, QT, 3GP, 3G2, WEBM, MKV, AVI, MPG, MPEG, MPE, M1V, M2V, TS, MTS, M2TS, OGV, WMV, ASF, FLV |
| Images et documents existants | PNG, JPG, JPEG, GIF, WEBP, PDF, TXT, MD, DOCX, XLSX, PPTX, ODT, ODS, ODP |

La validation audio/vidéo inspecte des en-têtes et structures de conteneur avec des lectures bornées, avant la mise en cache locale et à la fin du transfert serveur. Changer l'extension d'un exécutable, d'une page HTML ou d'un autre type de fichier ne suffit pas à le faire accepter. Cette reconnaissance ne décode pas intégralement le média et ne garantit pas tous les codecs contenus dans chaque extension. Si WebView2 ne sait pas lire un codec, le fichier reste une pièce jointe avec une indication de lecture indisponible et une possibilité de récupération.

## Vérifications et livraison

Les preuves de ce suivi sont conservées hors Git sous `artifacts/atlas-chat-followup-20260907/`. Les contrôles utilisent des données synthétiques, des fenêtres WPF inactives et hors écran, et une base MySQL isolée sur la boucle locale. Aucun compte réel ni instance du launcher utilisateur n'est piloté.

| Vérification | Résultat |
|---|---|
| Compilation Release avec `AtlasLocalClientBuild=true` | 0 avertissement, 0 erreur |
| Interactions DOM et rendu FR/EN | 149 assertions, 11 captures et quatre empreintes d'assets vérifiées |
| Pont natif WebView2 | 161 assertions |
| Fenêtre WPF complète FR/EN | 112 assertions et 10 captures directes vérifiées visuellement |
| File d'envoi, suppression durable et présence | 210 assertions |
| Médias, stockage et métadonnées de liens | 786 assertions, dont 685 sur le registre et les conteneurs |
| Corpus réellement encodé | 39/39 fichiers acceptés : les 38 extensions audio/vidéo et une variante WAV RF64 |
| API HTTP et MySQL 8.4.11 | 101 assertions ; transferts médias rejoués sur le schéma 8 |

Le corpus est généré avec FFmpeg 9.0.1 à partir de silence et d'une mire. Son archive d'outillage provient du fournisseur Windows référencé sur la [page officielle de téléchargement FFmpeg](https://ffmpeg.org/download.html), avec comparaison du SHA-256 publié. L'outillage et les médias restent dans les artefacts locaux. Les fixtures de conteneurs testent aussi les longueurs incohérentes, les familles incompatibles, l'annulation et les lectures bornées ; elles ne constituent pas un décodeur complet.

Le test HTTP embarque un véritable M4A AAC de 1 021 octets afin de se rejouer sans encodeur externe. Il vérifie la dérivation du MIME serveur, le stockage dans le message, la lecture privée par plages d'octets, `no-store`, `nosniff` et la révocation après suppression. Il refuse aussi une page HTML renommée MKV à la fin du transfert et lors d'une tentative d'envoi. La base temporaire et le processus MySQL de test sont supprimés/arrêtés ; aucun port de fixture ne reste en écoute sur 13307/13308.

Les résultats se trouvent dans `dom/results.json`, `native-release.log`, `full-shell-release/verification.json`, `chat-workspace-final.log`, `chat-media.log`, `encoded-media/validation.json` et `chat-v2-api-mysql.log`. Les captures WPF incluent la barre native, la Citadelle et la WebView ; elles sont réalisées directement, sans montage ni redimensionnement du launcher. Le dépôt de fichiers est exercé par événements synthétiques ; aucun geste physique depuis l'Explorateur n'est simulé sur le bureau utilisateur.

Le client habituel `artifacts/AtlasLauncherLocal/AtlasLauncherLocal.exe` a été remplacé le 7 septembre 2026 à **15 h 14 min 36 s (Paris)**, après constat de sa fermeture. L'exécutable de 102 812 995 octets a le SHA-256 `ee7107430740a8693c49db179ff5a8a5429a03245ebec08e77f72fec9421b8c8`. L'ancien exécutable et `armory-local.json` ont été sauvegardés puis relus par empreinte ; la configuration conserve le SHA-256 `b174a090de95a39c164f1d7ccb11e4d0c395b75b0aa8a814d97450222b5c2445`. Aucun processus utilisateur n'a été lancé ou fermé. Le relevé exact est `local-delivery.json`.

Le script `scripts/build-chat-media-api-candidate.ps1` prépare le candidat Linux à partir de preuves explicites des tests médias et API v2. Il compare les huit migrations avec le manifeste de schéma 8 déjà vérifié. Le paquet ne contient que l'exécutable Linux et sa bibliothèque native ; aucune configuration ni aucun secret. Cette extension de formats ne crée aucune migration de base de données.

Le candidat `server-candidate/atlas-chat-media-api-linux-x64-schema8.tar.gz` est construit et relu : deux fichiers, SHA-256 `2fbbfc9b4362c805d42007631001c102422b95806ecb7c3cd2a0980732b8ae86`. Le manifeste conserve les empreintes des sources, des migrations, des logs et des binaires ; les modes Unix sont 0755 pour l'exécutable et 0644 pour la bibliothèque native.

**Activation serveur terminée le 7 septembre 2026 à 15 h 38 min 35 s (Paris)**, après accord explicite. Le candidat Linux réussit 83 contrôles sur une instance isolée puis les mêmes 83 contrôles en production, dont de vrais transferts M4A et MKV. La bascule de l'API dure 9,88 secondes. Le schéma 8 et les configurations restent identiques ; le serveur de jeu et Hermes conservent leurs PID et horodatages de démarrage. Le contrôle final à 15 h 40 min 05 s confirme la santé publique, les accès privés, le nettoyage des données synthétiques et l'absence d'erreur dans les journaux examinés. Le [compte rendu de déploiement](media-deployment-20260907.md) précise les preuves, la version et la sauvegarde.
