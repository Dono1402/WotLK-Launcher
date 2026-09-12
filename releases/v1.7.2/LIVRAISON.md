# Atlas Launcher 1.7.2 — conversion d’or

**Candidats préparés et testés, publication en attente d’autorisation.**
Le launcher public et les services existants ne sont pas remplacés par cette
préparation. Les empreintes et les résultats figurent dans
[`validation.json`](validation.json) et [`SHA256SUMS.txt`](SHA256SUMS.txt).

La conversion réelle passe par une demande durable de l’API puis une
transaction du royaume : retrait d’or, ajout des Crédits Atlas et reçu sont
validés ensemble. Les réponses perdues se reprennent avec la même clé. Le taux
est de 100 po pour 1 € de Crédits Atlas ; les autres pièces et le portefeuille
en euros sont conservés. Voir le
[fonctionnement détaillé](../../docs/BOUTIQUE-ATLAS-CONVERSION-OR.md).

## Artefacts préparés

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
Les fichiers API locaux correspondent à ceux testés. Les processus, unités,
configurations et migrations de production correspondent au précontrôle.

Le jeu graphique n’a pas été ouvert : le parcours est validé sur les services,
la base et le protocole réel, sans observation manuelle du rendu du jeu.
Il ne s’agit pas d’un test de charge à 1 500 joueurs ou bots.

## Bascule restante

La mise en service nécessite une maintenance de **World et API** :
déconnexion des joueurs du royaume et interruption brève de l’API. Hermes et
Auth conservent leurs processus. Les overrides ont été préparés et vérifiés
sans être installés ; les conversions restent désactivées publiquement.

La [procédure de préparation et de bascule](../../scripts/atlas-shop-gold-release/README.md)
prévoit la sauvegarde après arrêt des écritures, la migration 0014, la
vérification des heartbeats, l’activation, puis la publication du manifeste
signé et de l’installateur. La signature, les téléchargements publics et la
release GitHub 1.7.2 doivent être vérifiés au moment de cette publication.

Après migration, tout retour arrière conserve l’API compatible 0014 avec les
conversions fermées et préserve les reçus et les soldes. PayPal et les trois
autres services ne sont pas activés par cette livraison.
