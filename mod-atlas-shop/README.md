# mod-atlas-shop

Module AzerothCore pour la livraison des achats Atlas de changement de nom.
Il traite les commandes persistantes de l'API du launcher et accorde le service
natif `AT_LOGIN_RENAME` une seule fois. Désactivé par défaut.

Voir [le parcours, les prérequis et les tests](../docs/BOUTIQUE-ATLAS-RENOMMAGE.md).

Le module exige les bases auth/characters sur la même instance MySQL, des tables
InnoDB, le schéma Atlas 0012 et **un seul worker CharacterDatabase dès le démarrage**.
Il attend la déconnexion complète du compte au royaume. Copier ce dossier dans
les modules du cœur et reconstruire le worldserver relève d'une étape distincte
de mise en service sur un royaume de test.
