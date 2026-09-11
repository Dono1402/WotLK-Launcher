# mod-atlas-shop

Module AzerothCore pour la livraison des achats Atlas de changement de nom.
Il traite les commandes persistantes de l'API du launcher et accorde le service
natif `AT_LOGIN_RENAME` une seule fois. Désactivé par défaut.

Le nouveau [parcours de services achetés pour le compte](../docs/BOUTIQUE-ATLAS-SERVICES-NATIFS.md)
est préparé côté API et launcher. Ce module ne fournit pas encore le bouton
BattlePay/VAS ni la consommation native de ces services ; il ne faut pas
l'activer pour ce parcours.

Voir [le parcours, les prérequis et les tests](../docs/BOUTIQUE-ATLAS-RENOMMAGE.md).
Le [banc Linux isolé](../docs/BOUTIQUE-ATLAS-TEST-ROYAUME.md) lie un vrai World
et teste l'API avec les commandes natives de personnage, sans client graphique.
La [campagne Hermes](../docs/BOUTIQUE-ATLAS-HERMES.md) vérifie aussi le SSO du
launcher, les paquets 3.4.3 et les coupures réseau. Elle fournit un correctif
Hermes séparé, validé uniquement dans cette fixture.

Le module exige les bases auth/characters sur la même instance MySQL, des tables
InnoDB, le schéma Atlas 0012 et **un seul worker CharacterDatabase dès le démarrage**.
Il attend la déconnexion complète du compte au royaume. Copier ce dossier dans
les modules du cœur et reconstruire le worldserver relève d'une étape distincte
de mise en service sur un royaume de test.
