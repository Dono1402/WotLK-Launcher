# Atlas Launcher 1.8.2 — raccourcis, icônes et désinstallation

Publié le 26 septembre 2026 depuis le commit
`8f2be76949364f5148331631cc7fb49e778862c0`, désigné par le tag `v1.8.2`.

- [Release GitHub](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.8.2)
- [Notes françaises](PATCH-NOTES.md) et [anglaises](PATCH-NOTES.en.md)
- [Publication signée](publication.json), [publication GitHub](github-publication.json)
  et [canaux publics](public-endpoints.json)
- [Vérifications ciblées](verification.json) et [paquets](package-verification.json)

## Causes et corrections

Les dossiers communs du Bureau et du menu Démarrer peuvent légitimement être
modifiables par les utilisateurs. L'installateur leur appliquait la politique
stricte de Program Files : les deux options de raccourcis étaient donc
désactivées sur le PC concerné. Les chemins Windows autorisés restent fixes ;
leurs répertoires sont maintenant verrouillés contre les renommages pendant
l'opération, et les jonctions sont refusées. Les permissions Windows ne sont
pas modifiées. Un lien existant vers une autre application est conservé.

Le désinstalleur ne nettoyait pas les fichiers laissés par le remplacement
automatique du client. Il retire maintenant les noms de fichiers reconnus dans
les dossiers de mise à jour, puis les dossiers devenus vides. Il conserve les
fichiers supplémentaires inconnus et explique leur présence. Le jeu et les
réglages stockés dans le profil utilisateur sont conservés.

L'exécutable installé contenait déjà le nouveau logo Atlas ; Windows affichait
encore l'ancien depuis son cache. Une notification au Shell a corrigé son
affichage, confirmé par l'utilisateur. Le client et l'installateur émettent
désormais cette notification après un remplacement réussi, sans redémarrer
l'Explorateur.

## Vérifications

Le banc isolé couvre la création et la suppression de vrais raccourcis COM,
les retours arrière, les résidus de mise à jour, la suppression du dossier
devenu vide et le helper de suppression différée. Il vérifie aussi la
conservation des fichiers inconnus, le refus des jonctions et le blocage d'un
renommage pendant le verrouillage des répertoires. Les chemins réels du Bureau
et du menu Démarrer passent une vérification en lecture seule.

Le paquet final est contrôlé dans un dossier jetable. Sa charge embarquée
correspond exactement au client publié. Le client accepte la signature et
l'empreinte du candidat, sans téléchargement ni lancement lors de ce contrôle.
Les deux téléchargements HTTPS sont vérifiés intégralement par taille et
SHA-256 ; les six fichiers GitHub et les notes correspondent aux candidats.
Les canaux HTTPS et HTTP historique annoncent la même 1.8.2.

## Installation et limites

La mise à jour automatique du client ne remplace pas l'ancien `Uninstall.exe`.
L'installateur 1.8.2 est donc nécessaire pour bénéficier également du nouveau
désinstalleur. L'assistant ne propose pas encore de réparation sur place :
une installation existante doit être désinstallée avant la réinstallation.

L'utilisateur a autorisé l'ouverture de la désinstallation 1.8.1 puis de
l'installation 1.8.2. Les résultats de cette installation sur son PC sont
consignés séparément lorsqu'ils sont vérifiés. Le nettoyage à la désinstallation
1.8.2 est validé par le banc isolé, pas par une désinstallation réelle de son PC.

Le client local conserve son empreinte
`183044c1cff9fcc26a25b29de680fb2b9d2cb0a2729ce176790fccd929c335c9`.
L'API, World, Auth et Hermes conservent leurs processus ; aucune migration,
modification Caddy ou opération sur le serveur de jeu n'a été effectuée.
Les binaires, secrets et identifiants Windows restent hors Git.
