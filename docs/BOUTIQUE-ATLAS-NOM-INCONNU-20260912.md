# Nom « Inconnu » après un renommage

État : **Hermes activé le 12 septembre 2026 à 21 h 59 (Paris), stabilité vérifiée à 22 h 00 ; affichage en jeu confirmé par l'utilisateur**.

Retour utilisateur consigné le 12 septembre à 22 h 06 (Paris) :
« Ca a fonctionné ». Le défaut d'affichage « Inconnu » est donc considéré
résolu pour le parcours signalé.

Après l'activation du premier correctif d'identité à 20 h 37 (Paris), un nouveau
renommage a bien été enregistré, mais le client a affiché « Inconnu » au-dessus
du personnage. La capture réseau contient les notifications d'invalidation et
plusieurs réponses avec le nouveau nom et le bon GUID. Les réponses arrivent
pendant le chargement, avant la création du joueur actif indiquée dans le
journal du client. Aucune nouvelle réponse n'a été observée après cette étape.

La vérification du décodeur natif `3.4.3.54261`, de son cache et de sa fonction
de lecture du nom confirme que les réponses Hermes sont lisibles. Il n'y a pas
de changement de format du paquet dans ce correctif. Le décalage observé dans
le chargement a justifié de déplacer le rafraîchissement. La disparition du
défaut en jeu a ensuite été confirmée par l'utilisateur.

## Comportement corrigé

À l'entrée en jeu, Hermes mémorise le besoin de rafraîchir le nom. Il attend
`CMSG_LOADING_SCREEN_NOTIFY(Showing=false)` avant d'invalider le nom du joueur
actif et de demander son identité actuelle au World. Ce rafraîchissement ne
se produit qu'une fois par entrée ; les notifications répétées de fin de
chargement ne l'émettent pas à nouveau. Aucun délai arbitraire n'est ajouté.

Les notifications consécutives à une transaction de renommage restent en
place, notamment pour les observateurs connectés. La réparation à l'entrée
couvre aussi un service déjà consommé : aucun nouvel achat ni changement de
nom supplémentaire n'est nécessaire.

## Validation et limites

- Les 25 tests .NET ciblés de protocole, services et déconnexion réussissent.
  Les deux méthodes de sérialisation produisent les mêmes réponses de nom.
- Les 11 contrôles du nouveau test natif exécutent, sous émulation CPU, les
  véritables constructeurs et répartiteurs de paquets, caches de royaume et
  de noms, invalidation, rappels de disponibilité et fonction de lecture du
  nom d'une unité du client. Trois changements successifs, dont des noms
  accentués, invalident le nom précédent puis rendent le nouveau nom.
- Les 16 contrôles natifs existants des services de compte réussissent avec
  l'utilitaire d'émulation partagé.
- Le test réseau de chargement échoue sur l'ancien Hermes à cause de
  l'invalidation prématurée. Le candidat réussit les 20 contrôles d'identité :
  entrée, sélection, renommages successifs, observateur, chuchotement réel,
  ancien reçu rejoué et cache périmé conservé à travers une reconnexion SSO.
- Les 20 contrôles réseau de régression des services de compte réussissent.
  Les deux suites utilisent exactement le même exécutable Hermes candidat.

Un premier passage du candidat avait rencontré une course dans le client de
test à la déconnexion : Hermes ferme la connexion d'instance tandis que
`LogoutComplete` arrive sur celle du royaume. Le test accepte désormais un EOF
propre sur la seule instance explicitement en cours de fermeture, tout en
exigeant les réponses réelles d'acceptation et de fin de déconnexion. Le
correctif Hermes n'a pas changé entre ces passages.

Les échanges réseau, SSO, core, API et MySQL sont réels dans un environnement
isolé. Le World de test est reconstruit avec le module actuellement en
production, mais sa configuration reste celle du banc privé. Les contrôles
natifs utilisent les sections privées du client installé, sans lancer le jeu.
Les frontières avec l'allocation, l'horloge, le transport, la localisation et
les notifications graphiques sont simulées. **Aucun de ces tests ne reproduit
l'écran de chargement complet ni ne prouve le rendu visuel du nom.** Les
captures et les identités de joueurs ne sont pas ajoutées au dépôt.

## Livraison Hermes uniquement

La version active se trouve sous
`/opt/hermesproxy-wotlk/releases/hermes-name-response-20260912`. Son exécutable
a pour SHA-256
`b915e5c0c4093f5e5bbdb79e39dfec0ade8907d59b7103b9ad3e295f0bcc248b`.
Le script persistant et les sauvegardes vérifiées se trouvent sous
`/opt/atlas-shop-releases/name-response-20260912`.

Le script de livraison refuse l'activation si les deux suites réseau n'ont pas
réussi sur cet exécutable ou si ses fichiers ont changé. Il vérifie aussi les
services de référence, la configuration et la priorité du nouveau paramètre
systemd. Il ne redémarre que Hermes ; en cas d'échec de démarrage, il restaure
l'exécutable précédent. World, Auth et API doivent conserver leurs processus.

Le code est conservé dans le patch Hermes de ce dépôt. Les tests natifs sont
[`verify_native_identity.py`](../mod-atlas-shop/tests/verify_native_identity.py)
et [`native_client_fixture.py`](../mod-atlas-shop/tests/native_client_fixture.py).
Le rapport de livraison est
[`validation/hermes-name-response-20260912.json`](validation/hermes-name-response-20260912.json).

L'activation s'est terminée à `2026-09-12T19:59:21.011872Z`, avec Hermes
`2897226`. La vérification à `20:00:41.914849Z` confirme le même PID et le même
exécutable, sans redémarrage automatique. World `2878870`, Auth `323656` et
API `2830654` ont conservé leurs processus et exécutables. Les cinq ports
contrôlés répondent, l'API est saine et les signaux de renommage et conversion
d'or ont été renouvelés après l'activation. Les sauvegardes sont vérifiées,
la fixture et son MySQL sont arrêtés, et le manifeste public du launcher 1.7.2
est inchangé.

Le code testé et activé correspond au commit
`6d4a71760e43ad8c1b1ee554c5cacc7e748e2e0d`, poussé sur `ui/redesign-v2`.
Les fichiers du candidat, des tests et du script persistant ont été comparés
par empreinte avant de renseigner cette provenance et d'activer Hermes.

La consigne de vérification visuelle était de se déconnecter complètement du
royaume, puis de se reconnecter et d'entrer en jeu avec le personnage déjà
renommé, sans nouveau service payant ni autre renommage. L'utilisateur a
confirmé le fonctionnement après cette livraison ; cette confirmation est
consignée séparément des tests automatisés dans le rapport.
