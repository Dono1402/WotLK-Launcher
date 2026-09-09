# Modèles HD Leeviathan : préparation et limite de compatibilité

## Résultat au 9 septembre 2026

Le pack correspondant au choix de l'utilisateur a été identifié et son inventaire
ZIP contrôlé. **Il n'est pas installé et aucun portage compatible n'est prêt.**
Le pack WotLK fourni vise **3.3.5a** ; le client installé est **Classic
3.4.3.54261**, avec stockage CASC. Copier `patch-H.MPQ` dans son dossier `Data`
ne constitue donc pas une procédure d'installation validée.

Le périmètre reste les personnages, leurs textures associées et les PNJ couverts
par ces modèles. Il exclut les environnements, le terrain, ReShade, les addons,
le changement de client et les variantes qui remplacent les races.

## Preuves obtenues

L'[inventaire vérifié](update-preparation/hd-models-leeviathan-20260909.json)
conserve les tailles, les CRC annoncés par le ZIP, le hash du texte effectivement
lu et les limites de la vérification. Aucun asset ni texte intégral tiers n'est
redistribué dans ce dépôt.

- Client local : `WowClassic.exe`, version fichier `3.4.3.54261`, 50 589 832 octets.
- Arctium local : 6 771 712 octets ; son champ ProductVersion référence le commit
  `9bf58f92b5879c4707e9869027ab0f5c92f914ec`. Son SHA-256 figure dans l'inventaire.
- Le [README de cette révision publique](https://github.com/Burralis/Game-Launcher/blob/9bf58f92b5879c4707e9869027ab0f5c92f914ec/README.md)
  exclut le chargement de mods. La métadonnée du binaire établit sa provenance
  déclarée, pas une preuve de compilation reproductible ni l'absence certaine
  de modifications locales non documentées.
- La [fiche du pack Leeviathan](https://www.wowmodding.net/files/file/372-leeviathans-hd-character-models/)
  mène à une archive publique WotLK. Son `info.txt` confirme la cible 3.3.5a.
  La mention « Classic » de la fiche ne prouve pas une compatibilité avec le
  client moderne 3.4.3.
- Archive : 2 934 500 949 octets, huit entrées. Pack de base : `patch-H.MPQ`,
  2 504 852 362 octets décompressés. Les variantes optionnelles de morts-vivants
  et de Draeneï sont exclues de la sélection.
- Le texte `info.txt` a été lu et son CRC vérifié ; SHA-256 :
  `431d9106553a6b42cd4aba4a2b4d27d82147d91ad1db0b17296d1543488acaf6`.

Seulement 1 336 octets de l'archive ont été transférés en six requêtes HTTP
partielles, en plus de la page de téléchargement et des en-têtes HTTP. Le serveur
ne fournit pas d'ETag. **Le hash global de l'archive, ses contenus MPQ, l'intégrité
des modèles et leur rendu en jeu ne sont pas vérifiés.** Les CRC du reste des
entrées sont des valeurs de l'annuaire ZIP, pas des contrôles des assets.

## Adaptation à étudier séparément

Une [ancienne révision open source d'Arctium](https://github.com/NumboWoW/WoW-Launcher/blob/3deaa3f50b95ae918ba49ca2a4d9a895247e67f7/README_OLD.md)
documente le chargement de fichiers séparés et des correspondances FileDataID
pour la famille 3.4.x. Son code conditionnel `CUSTOM_FILES` existe, mais cela
**ne valide pas le build 54261**. Aucun ancien launcher n'a été compilé, exécuté
ou substitué à celui du client.

Un prototype de portage, s'il est demandé, doit franchir les étapes suivantes :

1. Sélectionner et vérifier une méthode de chargement compatible exactement avec
   3.4.3.54261, tout en conservant les paramètres de connexion actuels.
2. Télécharger le pack dans un dossier distinct, calculer son SHA-256, tester
   l'archive puis inventorier le MPQ de base uniquement. Ne pas exécuter de
   fichiers fournis avec un pack.
3. Comparer un modèle pilote et ses dépendances au client Classic : formats M2,
   SKIN, animations, textures et éventuelles tables, chemins et FileDataID. Ne pas
   supposer que changer le conteneur suffit, ni que la conversion est impossible.
4. Préparer une version isolée, avec manifeste et retour arrière. Obtenir
   l'autorisation avant de lancer le jeu ou de modifier le client utilisé.
5. Vérifier réellement sélection/création du personnage, personnalisation,
   équipement, animations et PNJ avant d'étendre à toutes les races. Une réussite
   de compilation ou d'extraction ne remplace pas cette validation graphique.

L'[application Arctium actuelle](https://arctium.io/about/) annonce des fonctions de modding, mais sa
compatibilité exacte avec ce build n'a pas été établie. Aucune installation,
acceptation de conditions ou création de compte n'a été effectuée. Ne pas
transformer cette piste en dépendance obligatoire sans décision de l'utilisateur.

## Diagnostic reproductible

Depuis la racine du dépôt, avec Python 3 et, pour ce ZIP Deflate64, le 7-Zip
installé localement :

```powershell
python -I -B scripts/wotlk-hd-models/inspect_remote_zip.py `
  --page-url 'https://www.mediafire.com/file/nkdszrg8nsh9t76/Leeviathan-s_WoD_Character_Models_%25283.3.5a%2529.zip/file' `
  --seven-zip 'C:\Program Files\7-Zip\7z.exe' `
  --scratch-dir 'C:\chemin\dossier-de-preparation-existant'

python -I -B scripts/wotlk-hd-models/test_inspect_remote_zip.py -v
```

Le diagnostic n'extrait aucun modèle. Il lit le répertoire ZIP et les petits
textes ; pour Deflate64, il crée un ZIP temporaire à une seule entrée dans le
dossier explicitement fourni, décode ce texte vers une sortie bornée puis supprime
uniquement son fichier temporaire. Il ne lance ni jeu ni launcher, ne modifie
aucun fichier client et n'exécute aucun contenu téléchargé.

Garde-fous : HTTPS MediaFire exclusivement, redirections contrôlées, bouton
public non ambigu, refus des réponses ignorant les plages HTTP, budgets de
transfert, refus des chemins dangereux et des entrées chiffrées, taille et durée
du décodeur bornées. Un refus d'accès arrête le diagnostic, sans contournement.

Validation effectuée : **20 tests hors réseau réussis**, puis nouvelle inspection
réelle réussie avec 7-Zip et contrôle du CRC du texte. Aucun test en jeu.

## Installations existantes

Cette préparation HD n'a changé ni HermesProxy, ni worldserver, ni le feed
client, ni les addons, ni les fichiers du jeu. L'état du déploiement des services
est documenté séparément dans [le compte rendu du 9 septembre](WOTLK-DEPLOYMENT-2026-09-09.md).
