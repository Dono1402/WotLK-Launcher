# Atlas Launcher 1.8.1 — correction de la relance après mise à jour

Publié le 26 septembre 2026 après le signalement « La mise à jour ne se fait pas ».
La 1.8.0 reste immuable. Le client et l’installateur 1.8.1 sont compilés depuis
`93e1beeb9c710c461ee9d26f2b2b8a9752093dc0`, désigné par le tag `v1.8.1`.

- [Release GitHub](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.8.1)
- [Notes françaises](PATCH-NOTES.md) et [anglaises](PATCH-NOTES.en.md)
- [Publication signée](publication.json) et [publication GitHub](github-publication.json)
- [Vérification du correctif](verification.json) et [contenu des paquets](package-verification.json)
- [Empreintes GitHub](SHA256SUMS.txt)

## Cause et correction

Le client 1.7.2 téléchargeait et remplaçait correctement le binaire, mais son
helper recevait une erreur Windows à la relance. Il restaurait alors la 1.7.2,
puis échouait également à relancer l’ancienne version. L’erreur était déjà
présente dans les journaux du 12 septembre ; le journal ne conservait que le
type d’exception, sans son code natif.

Un programme de diagnostic sans fenêtre reproduit l’erreur 5 avec les mêmes
droits sur le jeton. La duplication d’un jeton primaire avec ADJUST_DEFAULT et
ADJUST_SESSIONID, en plus des droits existants, permet la relance. Le jeton reste
celui du demandeur, sans élévation ni changement de compte. Le journal conserve
désormais le code natif Windows, sans ajouter les messages d’exception sensibles.

## Vérifications

Le probe natif appelle la méthode de production depuis un vrai helper élevé,
après la sortie d’un demandeur jetable. Il confirme le même utilisateur, la même
session et un enfant non élevé. Le scénario avec un autre compte administrateur
à l’invite UAC n’a pas été exercé. Le remplacement atomique, les retours arrière
et les barrières d’identité passent dans leur banc isolé.

Le paquet public conserve ses réglages `WotLK Launcher`, son canal automatique,
son identité Atlas et le runtime d’armurerie inchangé. L’installateur final est
contrôlé dans un répertoire jetable. Le client accepte le manifeste signé et
l’empreinte du candidat, sans lancer de mise à jour réelle pendant ce contrôle.

Les deux téléchargements HTTPS versionnés sont entièrement comparés par taille
et SHA-256. Les six fichiers GitHub et les notes sont vérifiés ; la 1.8.1 est la
dernière release. Les canaux HTTPS et HTTP historique annoncent le même candidat.
Le flux de patch notes conserve les entrées précédentes.

## Installation existante

L’ancien helper doit être remplacé une fois par l’installateur : un nouveau
candidat téléchargé ne peut pas réparer le code de mise à jour déjà en cours.
Après « C’est fermé, lance l’installateur », l’assistant 1.8.1 a été ouvert.
La vérification après installation confirme le binaire 1.8.1 et son SHA-256
exact, le désinstalleur identique au setup livré, ainsi que DisplayVersion 1.8.1
et Publisher Atlas dans Windows. Le processus du launcher installé est actif.
Cela valide la réparation par l’installateur, pas une mise à jour automatique
complète depuis la 1.8.1 vers une version ultérieure.

Le build local existant conserve son empreinte
`183044c1cff9fcc26a25b29de680fb2b9d2cb0a2729ce176790fccd929c335c9`.
L’API, World, Auth et Hermes n’ont pas été redémarrés ; aucune migration ni
modification Caddy n’a été effectuée pour ce correctif. Les fichiers privés,
les binaires et les résultats contenant des identifiants Windows restent hors Git.
