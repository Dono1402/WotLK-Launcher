# Atlas Launcher 1.5.0 — livraison

Atlas Launcher est passé à **1.5.0 le 7 septembre 2026 à 21 h 35 min 33 s (Paris)**. Le manifeste signé porte la date UTC `2026-09-07T19:34:09Z`. La release GitHub a été publiée à **21 h 41 min 50 s (Paris)**, sous le tag `v1.5.0`, sur le commit `9f06acc96a163634e0b54cdf38b40581339f0522`. Elle est publique, non préliminaire et désignée comme dernière version. Lors de cette publication initiale, les sept fichiers, leurs tailles et leurs empreintes SHA-256 ont été comparés aux fichiers préparés, puis leur disponibilité a été vérifiée sans authentification. Le relevé historique est [github-publication.json](github-publication.json). La présentation des téléchargements a ensuite été corrigée : l’état actuel est décrit dans [github-presentation.json](github-presentation.json).

- [Installation Windows x64](https://github.com/Dono1402/WotLK-Launcher/releases/download/v1.5.0/AtlasLauncherSetup.exe)
- [Manifeste signé](https://animeclub.fr/wotlk/launcher/launcher-update.json)
- [Release GitHub](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.5.0)
- Patchnotes : [français](PATCH-NOTES.md), [anglais](PATCH-NOTES.en.md), **37 points dans huit rubriques**.

## Fichiers et installation

| Fichier | Octets | Version |
|---|---:|---|
| WotLK-Launcher.exe | 401 195 333 | 1.5.0 / 1.5.0.0 |
| WotLK-Launcher-Installer.exe | 506 566 745 | 1.5.0 / 1.5.0.0 |
| armory-runtime.zip | 298 277 802 | 318 fichiers vérifiés |

Les empreintes des fichiers GitHub sont dans [SHA256SUMS.txt](SHA256SUMS.txt), et celles des paquets du serveur dans [release.json](release.json). L’installateur GitHub `AtlasLauncherSetup.exe` contient les mêmes octets que `WotLK-Launcher-Installer.exe` dans le stockage serveur. Il embarque le client public final, avec les ressources de messagerie et le paquet actuel de profil/armurerie. Son désinstalleur autonome est une copie du setup : le test mesure 907 763 295 octets installés et 974 870 942 octets d’espace requis. Les exécutables restent hors Git.

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

## Retours d’installation

Après publication, l’utilisateur a confirmé avoir téléchargé la mise à jour, puis a confirmé qu’une autre personne avait également réussi à l’installer. Cette personne avait initialement vu « Mise à jour disponible · 1.5.0 » dans son launcher 1.4.0. Le délai signalé s’est résolu ; sa cause n’a pas été établie et aucun correctif supplémentaire n’a été publié. Ces retours utilisateur sont distincts des tests automatisés décrits ci-dessus.

## Présentation publique corrigée

La release GitHub présente désormais **AtlasLauncherSetup.exe** comme seul exécutable pour les joueurs. Le client seul a été retiré de ses fichiers ; le paquet utilisé par les mises à jour automatiques reste disponible sur le serveur à l’URL du manifeste signé. Les octets de l’installateur et du client n’ont pas changé.

Les six fichiers GitHub actuels comprennent l’installateur, le paquet d’armurerie, le manifeste, les deux patchnotes et la liste SHA-256 mise à jour. Le README décrit l’installation et les fonctionnalités pour les joueurs ; les informations d’administration ont été déplacées dans [la documentation technique](../../docs/RELEASE-MAINTENANCE.md). La présentation emploie le nom Atlas Launcher.

L’identité Git du dépôt local et de son dépôt de publication serveur utilise `Dono1402` avec son adresse GitHub `noreply`. Le commit `fd9e716aabef7c15c43f1335848d77ac4e1ca627` est rattaché à ce compte comme auteur et committer dans l’API GitHub. Les anciens commits conservent leur identité historique.
