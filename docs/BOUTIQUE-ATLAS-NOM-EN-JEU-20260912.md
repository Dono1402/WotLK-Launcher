# Renommage : identité en jeu et cache des autres joueurs

État : **correctif implémenté et vérifié en environnement isolé ; activation publique en attente de l'accord explicite pour redémarrer World**.

Le signalement du 12 septembre était confirmé : après un renommage réussi, la
sélection affichait le nouveau nom mais le client pouvait conserver l'ancien
en jeu. La base et le cache du serveur contenaient déjà le nouveau nom. La
capture réelle ne montrait ni notification d'invalidation après la confirmation,
ni nouvelle demande de nom lors de l'entrée en jeu suivante.

La méthode `CharacterCache::UpdateCharacterData` du core utilisé actualise ses
tables internes ; son émission d'invalidation est commentée. Actualiser les
listes de personnages dans Hermes ne suffit donc pas à actualiser le cache de
noms du client, qui peut survivre à une sélection ou à une reconnexion SSO.

## Chaîne complétée

1. Après vérification du reçu SQL durable, le module actualise le cache du core
   puis envoie `SMSG_INVALIDATE_PLAYER` à toutes ses sessions connectées,
   propriétaire encore à la sélection compris. Une simple validation, un refus
   ou une transaction annulée n'émettent pas cette notification.
2. Hermes traduit cette notification pour le client `3.4.3.54261`. Il conserve
   les métadonnées nécessaires à la connexion du personnage et à son niveau.
   Pour un joueur déjà connu d'un observateur en jeu, il redemande également
   l'identité au core et transmet sa réponse actuelle.
3. À chaque entrée en jeu de ce client, Hermes invalide puis redemande
   l'identité du personnage actif. Cela couvre les changements déjà consommés
   avant le correctif : aucun nouvel achat ni renommage supplémentaire n'est
   nécessaire pour réparer le cache à la reconnexion.
4. Le rejeu d'un ancien reçu reste idempotent, mais sa réponse transporte le
   **nom actuellement enregistré** si un autre renommage a eu lieu depuis.
   Le nom historique du reçu ne remplace plus l'identité actuelle dans Hermes.

Le GUID du personnage, les reçus, les soldes et les données de jeu restent ceux
du personnage existant. Les fichiers corrigés sont le module natif et le patch
Hermes maintenu dans ce dépôt ; le lanceur distribué ne change pas.

## Vérifications

| Suite | Contrôles réussis |
| --- | ---: |
| Identité : jeu → sélection → renommage → jeu, avec observateur | 8 |
| Consommation native, barrières de sauvegarde, erreurs SQL et rejeux | 36 |
| SSO, sélection native, validation, consommation, annulation et déconnexions Hermes | 20 |
| Conversion d'or, achat, renommage, transactions et redémarrage du serveur isolé | 30 |
| Tests .NET de protocole et de déconnexion Hermes | 24 |

Le nouveau test a d'abord échoué sur les anciens exécutables à l'attente des
notifications de renommage. Sur les candidats corrigés, il vérifie notamment :

- le nom Unicode reçu en jeu, sans nouvelle requête du client lors de la rentrée ;
- le rafraîchissement chez un observateur resté connecté ;
- un vrai chuchotement entre les deux personnages temporaires, adressé au nouveau
  nom, et la réponse « joueur introuvable » pour l'ancien nom ;
- la conservation du nom actuel après le rejeu d'un reçu antérieur ;
- la réparation d'un cache volontairement périmé après une nouvelle connexion
  SSO, sans racheter ou réappliquer un service consommé.

Les comptes et monnaies sont temporaires. Les échanges chiffrés Hermes, le core,
l'API et MySQL sont réels et confinés au réseau privé de la fixture. Le cache
client est un modèle de test alimenté par les paquets natifs décodés. **Le rendu
du client graphique installé n'a pas été testé.**

Le garde-fou des tests de conversion accepte le nouveau candidat uniquement
avec la configuration de la fixture et dans le même espace réseau privé que
le test. Il refuse de suspendre un processus public.

Les rapports détaillés et empreintes sont dans
[`validation/hermes-rename-identity-20260912.json`](validation/hermes-rename-identity-20260912.json).
Après les tests, la fixture et son MySQL sont arrêtés. Les processus publics
World, Hermes, Auth et API sont restés inchangés.

## Activation préparée

Les deux exécutables sont préparés sous des chemins distincts et inactifs :

- World : `/opt/arthas-next/candidates/atlas-shop-rename-identity-20260912` ;
- Hermes : `/opt/hermesproxy-wotlk/releases/hermes-rename-identity-20260912`.

Le script [`deploy.py`](../scripts/atlas-shop-identity-release/deploy.py) sépare
la préparation, l'activation et la vérification. La préparation vérifie les
empreintes des exécutables testés, ne permet qu'une modification de source du
module natif, copie les configurations actuelles et sauvegarde les exécutables,
configurations et définitions de services précédents avec contrôle d'empreintes.
Les nouveaux paramètres de service restent inactifs jusqu'à l'activation.

L'activation nécessite **un arrêt propre et démarrage de World, puis un
redémarrage de Hermes**, avec déconnexion des joueurs. Elle conserve Auth et
l'API en activité, ainsi que la configuration, le schéma SQL et le manifeste
du lanceur. La vérification contrôle les nouveaux exécutables, les ports et les
heartbeats natifs du renommage et de la conversion d'or. Un échec déclenche le
rétablissement des anciens exécutables ; aucune restauration SQL automatique
n'est effectuée.

La consigne de ne pas redémarrer World sans demande explicite demeure applicable.
La phase `activate --authorized-maintenance` ne doit être exécutée qu'après cet
accord, puis suivie d'une vérification de stabilité et d'un retour en jeu du
client déjà renommé.
