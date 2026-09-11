# Recharges PayPal et administration Atlas

État vérifié le 11 septembre 2026. Ce jalon ajoute un portefeuille persistant,
des demandes de recharge, une validation administrateur et un journal des
opérations. Il est désactivé par défaut et n'a pas été déployé en production.
Le compte PayPal particulier souhaité par l'utilisateur est conservé comme
contrainte : aucune intégration Checkout ou notification PayPal n'est activée.

## Parcours joueur et administrateur

1. Le joueur choisit PayPal et un montant, puis crée une demande. Le serveur
   l'associe au compte authentifié et génère une référence `ATLAS-…`.
2. Le portefeuille reste inchangé. Le joueur peut copier la référence et
   ouvrir son lien PayPal.Me, limité au destinataire configuré et au montant
   de la demande. Les instructions demandent un paiement **biens et services**.
   Le joueur doit joindre la référence ; le lien ne garantit pas sa transmission.
3. Un administrateur vérifie le paiement reçu directement dans PayPal :
   référence Atlas, transaction, montant en euros et type de paiement.
4. Dans **Administration des recharges**, il saisit l'identifiant PayPal,
   le montant reçu, une note et confirme sa vérification. **Valider et créditer**
   inscrit le crédit, la décision et le journal dans une transaction SQL unique.
5. Le joueur actualise le statut ; seuls les soldes relus du serveur sont affichés.

Une capture d'écran, un retour de navigateur ou la simple création de la demande
ne prouvent pas le paiement. Le serveur ne contacte pas PayPal dans cette version :
l'administrateur reste responsable du rapprochement. Les achats et la livraison
des services de personnage, la conversion réelle de l'or, la carte bancaire et
Bancontact restent fermés.

Il ne peut exister qu'une demande en attente par compte. Son annulation est
possible avant paiement. Une demande annulée reste consultable et peut être
créditée par un administrateur si un paiement retardé est effectivement reçu.
Les demandes annulées comptent dans les plafonds de création sur 24 heures.

## Litiges et remboursements

- **Signaler un litige** exige une référence de dossier et une note. Le montant
  encore disponible, plafonné à la recharge contestée, est gelé. Les autres
  sommes déjà gelées restent protégées. Il n'y a pas de bannissement automatique.
- **Clore le litige** libère le montant réservé lorsque l'administrateur constate
  une issue permettant de conserver le paiement.
- **Enregistrer un remboursement confirmé** exige que PayPal ait déjà remboursé
  ou repris la totalité du paiement. Cette action ajuste le portefeuille Atlas ;
  elle ne déclenche aucun remboursement chez PayPal.
- Si les fonds disponibles ne couvrent pas la reprise, le solde manquant est
  conservé comme montant à régulariser. Une recharge ultérieure le couvre en
  premier. Aucun solde négatif n'est présenté comme un crédit utilisable.

Les remboursements partiels et les changements d'issue complexes doivent faire
l'objet d'une évolution du modèle avant utilisation ; ce jalon couvre les
reprises intégrales. Un service consommé n'est pas récupéré par cette comptabilité.

## Configuration serveur

Configuration illustrative uniquement, laissée désactivée. Remplacer le nom
PayPal.Me et les identifiants administrateurs après vérification. Aucun secret
PayPal n'est utilisé par ce parcours manuel.

```json
{
  "AtlasShop": {
    "ManualPayPal": {
      "Enabled": false,
      "PayPalMeName": "",
      "AdministratorAccountIds": [],
      "MinimumCents": 100,
      "MaximumCents": 5000,
      "DailyMaximumCents": 10000
    }
  }
}
```

Les limites par défaut sont **1 à 50 € par demande**, **100 € et 10 demandes
sur 24 heures**, pour tous les comptes. Les administrateurs sont des identifiants
de comptes Atlas existants, déclarés explicitement côté serveur ; un statut GM,
un champ fourni par le launcher ou un bouton affiché ne donne aucun droit.

La migration **0011_manual_shop_funding.sql** crée `atlas_shop_wallet`,
`atlas_shop_top_up` et `atlas_shop_ledger`. Elle ne s'applique pas si le plafond
de migration configuré est inférieur à 11. Ce jalon ne change aucun plafond de
production. Une activation future doit préparer la sauvegarde et examiner aussi
toutes les migrations intermédiaires encore en attente, avant exécution autorisée.
Le serveur valide la structure, les clés et les contraintes attendues.

Une fois le schéma disponible, mettre `Enabled` à `false` arrête les nouvelles
demandes et les liens de paiement tout en conservant les soldes, l'historique,
l'annulation des demandes et l'administration des paiements existants.
Ne pas supprimer les tables ni retirer les administrateurs nécessaires au suivi
des paiements pour suspendre les encaissements.

## Garanties techniques

Tous les montants sont des entiers en centimes. Le verrou du portefeuille
sérialise quotas et décisions pour un compte. Les identifiants de requête sont
idempotents ; un identifiant de transaction PayPal ne peut être utilisé qu'une
fois dans la base. La version de la demande évite d'appliquer une décision sur
un état périmé. Un rejeu strictement identique ne recrédite pas le compte.

Chaque transition conserve auteur, heure UTC, note, référence de litige,
mouvements et soldes avant/après déductibles. Le journal est ajouté sans
modification des entrées précédentes par l'application, dans la même transaction
que le portefeuille. Une erreur d'écriture annule l'ensemble. Les clés étrangères
empêchent la suppression d'un profil référencé par ces données financières.

Les lectures joueur portent sur son compte uniquement : 50 demandes et
100 opérations récentes. L'administration est paginée à 50 demandes et affiche
les 100 dernières entrées du journal de chaque demande ; les anciennes entrées
restent en base. L'historique distingue la reprise d'un paiement, négative, du
remboursement d'un achat, positif. Les données administratives ne sont jamais
incluses dans le catalogue joueur.

Le déploiement futur doit coordonner serveur et launcher : un ancien client
peut refuser le nouveau type d'historique `payment-reversal`. Le schéma 2 reste
lisible par le nouveau client lorsque les champs de recharge sont absents.

| Route | Accès et effet |
| --- | --- |
| `GET /api/v1/shop` | Compte authentifié : catalogue, soldes, ses demandes et historique |
| `POST /api/v1/shop/top-ups` | Compte authentifié : créer ou retrouver sa demande idempotente |
| `POST /api/v1/shop/top-ups/{id}/cancel` | Propriétaire : annuler uniquement sa demande en attente |
| `GET /api/v1/shop/admin/top-ups` | Administrateur : liste filtrée par `status`, curseur `before` |
| `GET /api/v1/shop/admin/top-ups/{id}` | Administrateur : détail et journal |
| `POST /api/v1/shop/admin/top-ups/{id}/decision` | Administrateur : décision versionnée et documentée |

Les mutations refusent les propriétés JSON inconnues, les corps dépassant
8 Kio, les paramètres imprévus et les identifiants invalides. L'authentification
Atlas et une limitation par compte s'appliquent. Le launcher borne et valide
les réponses, efface les données à la déconnexion et ignore les résultats tardifs.

## Vérifications locales

- Compilation .NET 8.0.424 : aucun avertissement ni erreur.
- `--shop` : 355 assertions, dont rejouabilité après erreur réseau, autorisations,
  précision des montants et réponses tardives après fermeture ou déconnexion.
- `--shop-funding-mysql` : 186 contrôles avec HTTP réel et MySQL 8.4.11 local,
  migrations et reprise d'une migration incomplète, profils/sessions,
  permissions, doublons concurrents, quotas, crédits, gels, libérations, reprises,
  régularisation, pagination et persistance. Le client du launcher et sa
  présentation ont aussi exécuté une recharge complète contre cette API.
  Une erreur SQL provoquée pendant l'écriture du journal a confirmé le rollback
  du crédit, de la demande et de la réservation de l'identifiant PayPal.
- `--shop-funding-wpf <dossier>` : contrôles natifs de création, validation,
  gel, reprise, historique, français/anglais, navigation et déconnexion dans
  une fenêtre synthétique inactive hors écran, sans erreur de binding.
- `--shop-wpf <dossier>` : parcours existants de catalogue, service, conversion,
  financement et historique revalidés en français/anglais aux quatre largeurs.
  Les captures de recharge, validation et remboursement ont été relues ; les
  champs administrateurs vérifient aussi que la ligne de texte n'est pas coupée.
- `--startup-routing` et `--migration-ceiling` : aperçus isolés, démarrage normal
  préservé et nouveau schéma soumis au plafond configuré.

La suite MySQL exige `ATLAS_SHOP_TEST_DB` vers **127.0.0.1:13307** et un
nom de base jetable commençant par `atlas_shop_test_`. Elle crée et supprime ses
bases dédiées ; ne jamais utiliser une base existante ou de production. La
fixture exécutée dans ce jalon a supprimé ses deux bases et fermé son instance.
Ces tests n'ont effectué aucun paiement PayPal ni livré de service dans le jeu.

## Aperçu autonome

```powershell
.\AtlasLauncherLocal.exe --ui-v2 --preview-shop-funding
```

Ce mode fournit un portefeuille fictif vide et une administration de
démonstration. Il ne charge pas de session réelle, ne contacte aucun prestataire,
n'ouvre pas de lien PayPal et ne sauvegarde rien sur le serveur. Créer une recharge,
la valider avec un identifiant fictif de 8 à 64 lettres/chiffres, puis examiner
son historique permet de revoir le parcours. L'aperçu boutique classique
`--preview-shop` garde ses exemples et sa conversion simulée habituels.

## Automatisation et prérequis PayPal

Le choix confirmé est de conserver un compte particulier. La vérification des
documents PayPal n'a pas établi de parcours automatique officiellement compatible
avec ce compte et PayPal.Me : la [mise en service REST](https://developer.paypal.com/api/get-started/)
exige un compte Business et le [paramétrage IPN documenté](https://developer.paypal.com/api/nvp-soap/ipn/IPNSetup)
part d'un compte Business. Aucun robot de connexion ni lecture automatique
d'emails n'est ajouté comme preuve de paiement.

Une intégration automatique ultérieure nécessitera un compte adapté, la création
et la capture des commandes côté serveur, des notifications authentifiées,
le rapprochement du montant/devise/destinataire et une reprise idempotente.
Elle devra aussi traiter les [remboursements et litiges PayPal](https://developer.paypal.com/api/rest/webhooks/event-names/).
Les règles du [type de compte et des transactions commerciales](https://www.paypal.com/fr/legalhub/paypal/useragreement-full)
ainsi que l'[éligibilité des crédits à la protection marchands](https://www.paypal.com/fr/legalhub/paypal/seller-protection)
restent à confirmer pour l'activité avant activation réelle. La validation
manuelle n'écarte pas une contestation ultérieure.
