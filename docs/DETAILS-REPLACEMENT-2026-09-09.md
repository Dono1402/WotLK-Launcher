# Details : version choisie par l'utilisateur, remplacement publié

## État

**Details `20240115.12220.155` est publié depuis le 9 septembre 2026 à 18:50:45 UTC
(20:50:45 à Paris), après confirmation de l'utilisateur.** Le catalogue serveur
remplace l'ancienne version `20250228.13407.162` par le paquet Atlas préparé
ci-dessous. Les treize autres addons sont inchangés. Aucun addon installé ou
réglage de joueur n'a été modifié ; aucun jeu ou launcher n'a été ouvert.
Ce travail est indépendant du prototype HD, laissé de côté à la demande de
l'utilisateur.

Le ZIP fourni est `Details-Details.20240115.12220.155.zip`, 5 009 518 octets,
SHA-256 `5a4419ff922916f1e7cfb6c2360a0675b0319a5daa810a980542113c79e74656`.
Il contient 551 entrées, dont 490 fichiers répartis entre huit dossiers. Le
contrôle CRC intégral réussit. Le TOC principal Wrath annonce bien `30403` et
`#Details.20240115.12220.155`.

Avant publication, le catalogue de production a été consulté en lecture seule :
14 addons, Details `20250228.13407.162`, hash du catalogue
`cc4d48f80ac5dae76ec58fbb80f0ffe254196361d30ba26d42cf3f494c032b79`.
Le paquet proposé jusqu'ici est hébergé sur ForgeCDN. Le catalogue local
`current/addons/catalog.json` n'est pas un miroir actuel de la production :
**ne pas le republier intégralement** pour ce changement.

## Adaptation nécessaire au paquet Atlas

Treize TOC Wrath des modules annexes déclarent encore `30401` ou `30402`.
L'installateur existant exige un TOC `30403` dans chacun des huit dossiers ;
le ZIP brut serait donc refusé, malgré le TOC principal compatible.

Le [préparateur reproductible](../scripts/addon-update-validation/prepare_details_package.py)
ne change que ces treize déclarations d'interface, vers `30403` :

- aucun des **254 fichiers Lua** ne change ;
- les versions, dépendances et déclarations SavedVariables restent inchangées ;
- tous les autres fichiers ont les mêmes octets que dans le ZIP fourni ;
- les 14 TOC Wrath et leurs inclusions sont contrôlés : 44 documents TOC/XML,
  269 références existantes ;
- l'archive finale est relue et comparée aux fichiers attendus ;
- le ZIP source est conservé intact, aucun fichier tiers n'est exécuté.

Les 13 tests synthétiques du préparateur réussissent, notamment le refus du
paquet brut selon la règle de l'installateur, la conservation des déclarations
de version/SavedVariables, les références manquantes et les chemins dangereux.

Il s'agit d'une adaptation de métadonnées, **pas d'une preuve supplémentaire
de compatibilité en jeu**. L'utilisateur indique utiliser cette version dans
son client ; aucun test en jeu n'a été réalisé par l'agent.

Paquet préparé : `Details-Details.20240115.12220.155-atlas-30403.zip`,
4 959 885 octets, SHA-256
`ca2ffad679c0647a0f4345f57675ef9e1198514d22022e5cfb380a34186c688b`.
Les fichiers binaires restent hors Git, dans le staging local. Le manifeste
`details-package.json` et le rapport `details-preparation.json` y sont associés.

## Publication exécutée

Le [script de publication ciblée](../scripts/addon-update-validation/publish_details_package.py)
a appliqué les étapes suivantes après confirmation :

1. Relecture du catalogue et contrôle de son empreinte avant remplacement.
   Seule l'entrée `id=details` change ; les métadonnées générales sont conservées.
2. Sauvegarde privée vérifiée du catalogue précédent, puis publication du ZIP
   sous un nouveau nom. Aucun ancien ZIP n'est supprimé.
3. Remplacement atomique du catalogue, sous verrou et avec une nouvelle
   vérification de non-concurrence juste avant bascule.
4. Relecture du catalogue et du ZIP après publication, comparaison des treize
   autres entrées à la sauvegarde, contrôle de lecture par `wotlklauncher`.
5. Mise à jour ciblée de la définition de fabrication et de l'entrée Details du
   catalogue local. Les anciennes releases restent intactes.

Résultat détaillé : [details-publication-20260909.json](update-preparation/details-publication-20260909.json).

- Catalogue après publication :
  `33c5e012f0fff8d12f6f427b7189c828a11844a6c0cabb55820050554f147224`.
- Paquet : empreinte et taille conformes à la préparation, permissions `0644`.
- Sauvegarde privée sur Atlas :
  `/var/backups/atlas-launcher-addons/details-20260909T185045Z-cf01213a/catalog.before.json`.
- API toujours active, même PID `1866560`, compteur de redémarrages `0` avant/après.
  Aucun redémarrage d'API, de HermesProxy ou de worldserver n'a été effectué.
- Les deux endpoints HTTPS répondent `401` aux requêtes GET sans compte,
  conformément à l'authentification existante. HEAD est refusé avec `405`
  (`Allow: GET`) : ce contrôle a été repris avec la méthode GET du launcher.
  Aucun téléchargement HTTP authentifié ni test dans le launcher réel n'a été
  effectué ; les octets et permissions serveur sont vérifiés directement.
- 13 tests du préparateur et sept tests de publication ciblée réussissent.
  La syntaxe PowerShell du générateur est validée. La fabrication globale de
  tous les addons n'a pas été relancée, afin de ne pas remplacer le catalogue
  distant par des définitions locales plus anciennes.

Le script de publication est volontairement lié à l'empreinte précédente :
**ne pas le relancer après succès**. Pour un changement ultérieur ou un retour
arrière, relire d'abord l'état courant et préserver les mises à jour intermédiaires.

Le remplacement est un retour à une version plus ancienne pour les joueurs
ayant celle du catalogue actuel. Le launcher compare version **et** empreinte,
sans exiger une version numériquement supérieure : il proposera le changement.
Il ne l'installera pas sans action du joueur. L'installateur ne touche pas aux
SavedVariables, mais leur interprétation par une ancienne version de Details
reste à contrôler : une sauvegarde des réglages est recommandée avant retour.

## Reproduction

```powershell
python -I -B scripts/addon-update-validation/test_prepare_details_package.py -v
python -I -B scripts/addon-update-validation/prepare_details_package.py `
  --archive 'C:\chemin\Details-Details.20240115.12220.155.zip' `
  --output-dir 'C:\staging\details-vide'
```

La sortie doit être un dossier existant, vide et distinct du ZIP. Le script
ne contacte aucun serveur et n'écrase aucun fichier. Le contrôle de hash refuse
toute autre archive. Le catalogue et les paquets ne sont pas inclus dans
l'exécutable Atlas Launcher ; cette publication ne demande donc pas de nouvelle
version du launcher.
