# Atlas Launcher 1.5.0 — livraison

La distribution AnimeClub est passée à **1.5.0 le 7 septembre 2026 à 21 h 35 min 33 s (Paris)**. Le manifeste signé porte la date UTC `2026-09-07T19:34:09Z`. La release GitHub utilise le tag `v1.5.0` et les mêmes octets ; son contrôle final est consigné séparément après publication.

- [Installation Windows x64](https://animeclub.fr/wotlk/launcher/AtlasLauncherSetup.exe)
- [Client 1.5.0](https://animeclub.fr/wotlk/launcher/releases/1.5.0/WotLK-Launcher.exe)
- [Installateur 1.5.0](https://animeclub.fr/wotlk/launcher/releases/1.5.0/WotLK-Launcher-Installer.exe)
- [Manifeste signé](https://animeclub.fr/wotlk/launcher/launcher-update.json)
- [Release GitHub](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.5.0)
- Patchnotes : [français](PATCH-NOTES.md), [anglais](PATCH-NOTES.en.md), **37 points dans huit rubriques**.

## Fichiers et installation

| Fichier | Octets | Version |
|---|---:|---|
| WotLK-Launcher.exe | 401 195 333 | 1.5.0 / 1.5.0.0 |
| WotLK-Launcher-Installer.exe | 506 566 745 | 1.5.0 / 1.5.0.0 |
| armory-runtime.zip | 298 277 802 | 318 fichiers vérifiés |

Les empreintes complètes sont dans [SHA256SUMS.txt](SHA256SUMS.txt). L’installateur embarque le client public final, avec les ressources de messagerie et le paquet actuel de profil/armurerie. Son désinstalleur autonome est une copie du setup : le test mesure 907 763 295 octets installés et 974 870 942 octets d’espace requis. Les exécutables restent hors Git.

La préparation utilise `scripts/build-armory-runtime.ps1`, `scripts/build-public-client.ps1` et `scripts/build-atlas-installer.ps1`, avec des chemins de sortie explicites sous `artifacts/atlas-release-150`. Le client est compilé avec `AtlasLocalClientBuild=false`. Les versions du projet, les métadonnées PE et les trois libellés de l’assistant indiquent 1.5.0.

## Vérifications de cette release

| Contrôle | Résultat |
|---|---|
| Compilation Release et harnais public | Réussite ; harnais : zéro avertissement, zéro erreur |
| Patchnote | Version, 37 changements, traduction anglaise et conservation de l’historique validés |
| Paquet armurerie | Empreintes du manifeste et des sources, dépendances, ressources et absence de données privées/de développement vérifiées |
| Contrat du paquet public | Routes authentifiées, validation, réparation, archives malformées et prérequis WebView2 simulés validés |
| Messagerie | 210 assertions sur brouillons, transferts, reprises, suppression durable et isolation des comptes |
| Médias | 786 assertions, dont 685 sur 52 extensions et 38 conteneurs audio/vidéo |
| Installation | Suites isolées sans élévation et artefact final réussies ; payload relu par SHA-256 |
| Publication atomique | 29 assertions locales sur copies, échecs, immutabilité et téléchargements simulés |
| Téléchargements publics | Client et installateur entièrement retéléchargés en HTTPS et comparés aux empreintes finales avant annonce |
| Signature | ECDSA P-256/SHA-256 vérifiée côté serveur puis avec le code de vérification du client et son ancre de confiance publique |
| Distribution | Manifeste HTTP 200 avec `no-store`, trois alias de téléchargement mis à jour, fichiers 1.4.0 conservés |

Les tests emploient des fichiers et comptes synthétiques. Ils n’ouvrent pas le launcher habituel ni le jeu de l’utilisateur. Le client local de développement conserve son empreinte `e1370dd2994a5d40dfc469e80335b2855e58260d33473688b4568fb740febc7c`. Aucun cycle manuel de mise à jour depuis un launcher ouvert, aucune installation UAC réelle et aucun aller-retour de chuchotement avec `/r` dans le jeu n’ont été rejoués pour cette publication. Les validations fonctionnelles précédentes sont décrites dans `docs/chat-refonte/` ; elles ne sont pas présentées comme de nouveaux tests de cette release.

## Publication et serveur

Le candidat `client-1.5.0-20260907T1931Z` a été signé dans un dossier isolé ; la clé privée est restée sur le serveur. Le script vérifie les empreintes avant toute copie, sauvegarde les fichiers modifiables et annonce la mise à jour par remplacement atomique du manifeste en dernier. La sauvegarde vérifiée est `/srv/wotlk/launcher-release-backups/client-1.5.0-20260907T1931Z-ipv4`.

Le flux `patch-notes.json` contient la 1.5.0 puis les quatre entrées antérieures inchangées. Son contenu correspond au patchnote français, dont chaque texte possède une traduction dans le client. La route API exige toujours une authentification (401 sans session) et relit le fichier à chaque demande. Le rendu du flux sous un compte réel n’a pas été observé pendant cette publication.

L’API, le serveur de jeu, Hermes et le serveur d’authentification conservent leurs PID et horodatages de démarrage ; Caddy et les configurations restent inchangés. Les fonctionnalités serveur nécessaires étaient déjà déployées. Aucune migration ni aucun redémarrage n’ont été nécessaires pour publier ce client.

Les preuves détaillées se trouvent sous `artifacts/atlas-release-150/` : logs de compilation/tests, `package-content-verification.json`, puis `deployment/stage-proof.json`, `publication-result.json` et `public-manifest-client-verification.json`.
