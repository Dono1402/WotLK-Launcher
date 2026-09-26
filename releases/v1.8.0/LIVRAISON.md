# Atlas Launcher 1.8.0 — identité Atlas et connexion

**Version publiée et API activée le 26 septembre 2026**, après l’accord explicite
« Oui, active et publie la 1.8.0 ». Le numéro 1.8.0 a été choisi par l’utilisateur.

- [Release GitHub](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.8.0)
- [Notes de version françaises](PATCH-NOTES.md) et [anglaises](PATCH-NOTES.en.md)
- [Distribution et services](deployment.json)
- [Publication du canal signé](publication.json) et [fichiers GitHub](github-publication.json)
- [Métadonnées du client et de l’installateur](package-verification.json)
- [Empreintes des fichiers GitHub](SHA256SUMS.txt)

## Contenu livré

Le nouveau décor de connexion/inscription et l’icône Atlas, la phrase « Tes jeux,
tes services, un seul compte Atlas. », les champs entièrement cliquables, les
boutons d’affichage des mots de passe et les retours de validation sont repris
de la version locale validée. Les champs vides restent sans erreur rouge. La
confirmation de création de compte et les corrections d’actualisation de la
boutique sont incluses. Aucun indicateur Verr. Maj. n’a été ajouté.

Le client public conserve son dossier de réglages `WotLK Launcher` et la mise à
jour automatique. Le build local existant conserve son exécutable, ses réglages
et son isolation ; il n’a pas été remplacé par le client public. L’installation
habituelle sur le PC n’a pas été lancée ni modifiée pendant cette publication.

L’installateur indique Atlas comme éditeur. Pour les installations mises à jour
depuis 1.7.2, le démarrage public suivant peut demander l’élévation Windows pour
actualiser l’ancienne inscription AnimeClub. La version locale ne touche pas
à cette inscription.

Le tag `v1.8.0` désigne le commit des sources compilées
`d35fd9b1b47820f1583a13c8e3ac7d28929ac557`. Les corrections de la procédure et les
preuves de publication sont conservées dans les commits suivants, sans déplacer
le tag. Le runtime d’armurerie garde son empreinte précédente :
`84a57db71c985be18c47f62e5761e21effe7d032a7316e0f69e9edc650a7e645`.

## Activation de la récupération du mot de passe

L’API 1.8.0 est active avec le plafond de migration 15. Une sauvegarde cohérente
de `arthas_auth` a précédé la migration ; son empreinte est enregistrée, mais
aucun exercice de restauration n’a été effectué. Les quatorze migrations
précédentes sont inchangées. La nouvelle table conserve les empreintes des liens
de réinitialisation, utilisables une fois pendant 30 minutes.

Seule l’API a été redémarrée. World, Auth et Hermes ont conservé leurs processus.
Les réglages existants de l’API et de la boutique sont conservés. Aucune base du
jeu n’a été restaurée et aucun compte de production n’a été réinitialisé.

Le contrôle HTTPS a détecté que Caddy remplaçait la CSP à nonce de la page par
celle du site, empêchant son script de fonctionner. Une exception limitée au
chemin `/wotlk/api/v1/auth/password-reset` conserve désormais la CSP et la
politique `no-referrer` fournies par l’API. La configuration a été validée et
rechargée sans redémarrage de Caddy. La politique des autres pages et tous les
PID sont inchangés, voir la [preuve du correctif](caddy-recovery-policy.json).
La [vérification finale de l’API](api-activation.json) n’a relancé ni migration
ni service.

## Vérifications et limites

Les contrôles ciblés des changements passent : 474 assertions WPF FR/EN sans
fenêtre native, authentification sans interface, 15 contrôles d’actualisation
boutique et 36 contrôles de récupération sur MySQL jetable. L’e-mail et Hermes
sont simulés dans ce dernier banc. L’installateur final 1.8.0 a été installé et
désinstallé dans un dossier de test avec vérification du payload exact ; la
migration de l’éditeur Windows utilise un registre isolé.

La signature est acceptée par l’ancre embarquée du launcher. Les deux EXE
versionnés ont été téléchargés intégralement par HTTPS et leurs SHA-256 comparés.
La release GitHub publique est la dernière version ; ses six fichiers, tailles,
empreintes et notes correspondent aux candidats. Le contrôle du canal public
par le client 1.8.0 renvoie `NoUpdate`, sans téléchargement, remplacement ou UAC.

Le patch note 1.8.0 est le premier élément du flux utilisé par l’API ; les
anciennes notes sont conservées. Les routes publiques de réinitialisation, les
réponses aux entrées invalides et la correspondance nonce HTML/CSP sont vérifiées.
La réception d’un véritable e-mail et une réinitialisation sur un compte réel
n’ont pas été testées. Aucun launcher ni jeu graphique n’a été ouvert sur le PC.

Les binaires, sauvegardes SQL et configurations privées restent hors Git. La
[procédure](../../scripts/atlas-launcher-180-release/README.md) conserve les anciens
artefacts et les preuves de bascule ; elle ne restaure jamais automatiquement
une base active.
