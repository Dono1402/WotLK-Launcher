# Atlas Launcher 1.7.2 — conversion d’or

**Version 1.7.2 publiée et activée le 12 septembre 2026.**
La maintenance World et API a été explicitement autorisée. La conversion d’or
et l’achat du renommage sont ouverts sur le royaume. Le site et GitHub servent
les artefacts vérifiés de cette version.

- [Release GitHub](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.7.2)
- [`deployment.json`](deployment.json) : services, sauvegarde, migration et contrôles HTTPS.
- [`publication.json`](publication.json) : manifeste et téléchargements publics.
- [`github-publication.json`](github-publication.json) : six fichiers GitHub et empreintes.
- [`validation.json`](validation.json) : résultats du banc avant maintenance.
- [`SHA256SUMS.txt`](SHA256SUMS.txt) : empreintes des fichiers de la release GitHub.

La conversion réelle passe par une demande durable de l’API puis une
transaction du royaume : retrait d’or, ajout des Crédits Atlas et reçu sont
validés ensemble. Les réponses perdues se reprennent avec la même clé. Le taux
est de 100 po pour 1 € de Crédits Atlas ; les autres pièces et le portefeuille
en euros sont conservés. Voir le
[fonctionnement détaillé](../../docs/BOUTIQUE-ATLAS-CONVERSION-OR.md).

## Artefacts livrés

- Launcher public Windows x64 1.7.2 :
  `artifacts/atlas-release-172/client/WotLK-Launcher.exe`.
- Installateur embarquant exactement ce launcher :
  `artifacts/atlas-release-172/installer/AtlasLauncherSetup.exe`.
- API 1.7.2 contenant la migration 0014 :
  `/opt/wotlk-launcher-api-releases/shop-gold-1.7.2-20260912`.
- World contenant le consommateur de conversion et tous les modules existants :
  `/opt/arthas-next/candidates/atlas-shop-rename-gold-20260912`.
- Plan privé et configurations de bascule :
  `/opt/atlas-shop-releases/gold-1.7.2-20260912`.

Le paquet d’armurerie conserve l’empreinte de la livraison précédente :
`84a57db71c985be18c47f62e5761e21effe7d032a7316e0f69e9edc650a7e645`.
Les binaires, caches et configurations contenant des secrets restent hors Git.

## Résultats vérifiés

Le scénario a été exécuté deux fois avec succès : d’abord sur le core du banc,
puis sur **les mêmes octets World et API que ceux du candidat de livraison**.
Les 30 contrôles couvrent notamment la conversion de 2 000 po en 20 € depuis
un portefeuille vide, l’achat du renommage pour 7 €, le choix natif du
personnage via Hermes 3.4.3, la validation/confirmation Unicode et une nouvelle
connexion SSO retrouvant le nom et 13 € de crédits.

Les essais supplémentaires vérifient la répétition concurrente, un or devenu
insuffisant, l’expiration, le rollback complet sur échec SQL du reçu, la reprise,
la connexion bloquée pendant le débit, la sauvegarde après jeu, les plafonds et
la réserve de remboursement, puis le redémarrage du World de test.

Le client passe 18 nouveaux contrôles et les 369 assertions existantes de la
boutique. Le schéma et l’API passent 20 nouveaux contrôles MySQL, en complément
des 186 contrôles de financement et 35 des services. Les suites WPF ciblée et
générale passent avec des fenêtres inactives hors écran et aucune erreur de
binding. L’installateur final a été installé dans un dossier jetable et ses
octets vérifiés ; l’installation habituelle du joueur n’a pas été utilisée.

Les empreintes des sources C++ locales correspondent au manifeste compilé.
Les fichiers API locaux correspondent à ceux testés. Le précontrôle a vérifié
les anciens processus, unités, configurations et migrations avant leur
remplacement autorisé.

Le jeu graphique n’a pas été ouvert : le parcours est validé sur les services,
la base et le protocole réel, sans observation manuelle du rendu du jeu.
Il ne s’agit pas d’un test de charge à 1 500 joueurs ou bots.

## Mise en production vérifiée

La sauvegarde des bases Auth, Characters et Playerbots a été prise après
l’arrêt du World et de l’API. Son gzip est intègre : 109 579 120 octets
compressés, 496 325 468 octets décompressés. Les configurations ont été copiées
et leurs empreintes vérifiées. Cette sauvegarde n’a pas fait l’objet d’un
exercice de restauration.

La migration 0014 est appliquée ; les treize migrations précédentes sont
inchangées. World utilise le binaire testé et les quatre workers Characters.
Les heartbeats du renommage (protocole 2) et de la conversion (protocole 1,
10 000 cuivres par centime) sont valides. Auth et Hermes ont conservé leurs
processus. Aucun des quatre services n’est en boucle de redémarrage.

Trois contrôles HTTPS ont été exécutés avec des comptes temporaires : boutique
fermée, boutique activée et version publiée. Les soldes sont indépendants,
les lectures anonymes sont refusées, les indicateurs et le flux de nouveautés
correspondent à la phase. Ces comptes ont été supprimés, sans achat ni conversion.

Le manifeste signé correspond exactement au document public. Les deux EXE
versionnés ont été téléchargés intégralement par HTTPS et hachés. Les six
assets GitHub, leurs tailles, empreintes et notes correspondent aux fichiers
préparés ; la release est publique et marquée comme dernière version.
Le vérificateur du launcher a accepté le manifeste et l’EXE 1.7.2, puis renvoyé
`NoUpdate` pour ce même candidat, sans téléchargement ni élévation.

Le précontrôle a corrigé le droit de traversée du nouveau dossier API avant
l’arrêt des services. Le contrôle final normalise le suffixe de révision Git
de `AssemblyInformationalVersion` avant de vérifier `1.7.2` ; aucun nouveau
binaire n’a été construit pendant la maintenance.

La [procédure exécutée](../../scripts/atlas-shop-gold-release/README.md)
conserve les versions précédentes. Après migration, tout retour arrière
conserve l’API compatible 0014 avec les conversions fermées et préserve les
reçus et les soldes. PayPal et les trois autres services ne sont pas activés
par cette livraison.
