# Details : version choisie par l'utilisateur, préparation du remplacement

## État

**Paquet préparé localement, publication en attente de confirmation.** Aucun
catalogue distribué, addon installé ou réglage de joueur n'a été modifié.
Aucun jeu ou launcher n'a été ouvert. Cette préparation est indépendante du
prototype HD, laissé de côté à la demande de l'utilisateur.

Le ZIP fourni est `Details-Details.20240115.12220.155.zip`, 5 009 518 octets,
SHA-256 `5a4419ff922916f1e7cfb6c2360a0675b0319a5daa810a980542113c79e74656`.
Il contient 551 entrées, dont 490 fichiers répartis entre huit dossiers. Le
contrôle CRC intégral réussit. Le TOC principal Wrath annonce bien `30403` et
`#Details.20240115.12220.155`.

Le catalogue de production a été consulté **en lecture seule** le 9 septembre :
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

## Publication prévue, non exécutée

Après confirmation :

1. Recharger le catalogue distant pour préserver toute évolution intervenue
   depuis cette préparation. Ne modifier que l'entrée `id=details`.
2. Sauvegarder le catalogue précédent dans un répertoire privé distinct, puis
   publier le ZIP sous son nouveau nom et vérifier taille/empreinte.
3. Remplacer atomiquement le catalogue avec une vérification de non-concurrence.
   Version, URL HTTPS Atlas, taille, SHA-256 et `installHash` doivent correspondre
   au nouveau paquet. Préserver les treize autres addons.
4. Vérifier les fichiers servis, les permissions et l'état de l'API ; aucun
   redémarrage de l'API, de HermesProxy ou de worldserver n'est nécessaire.
5. Mettre à jour la définition de fabrication et l'entrée Details du catalogue
   local pour éviter qu'une fabrication ultérieure ne réintroduise l'ancien
   paquet. Ne pas réécrire les archives historiques de releases.

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
