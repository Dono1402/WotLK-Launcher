# mod-atlas-shop

Module AzerothCore pour les services de changement de nom achetés dans le
launcher Atlas. Il est désactivé par défaut.

Le [parcours de services pour le compte](../docs/BOUTIQUE-ATLAS-SERVICES-NATIFS.md)
permet d'acheter pendant la partie puis de choisir le personnage à la sélection,
via le sélecteur natif 3.4.3 et Hermes. La validation et la consommation sont
testées avec les vrais services sur un royaume isolé ; le rendu graphique et
la mise en production restent distincts.

Ce mode requiert le schéma 0013, les trois fichiers du cœur adaptés par
`tests/patch_native_core.py`, le correctif Hermes et
`AtlasShop.AccountServices=1`. Il a été vérifié avec quatre workers des
personnages. Auth et characters doivent être sur la même instance MySQL, avec
les tables concernées en InnoDB. Les hooks sont obligatoires à la liaison.

L'ancien mode, `AtlasShop.AccountServices=0`, conserve les commandes attribuées
à un personnage et le bit `AT_LOGIN_RENAME` ; il exige un seul worker et la
fermeture du compte. Ses campagnes historiques sont décrites dans le
[banc World](../docs/BOUTIQUE-ATLAS-TEST-ROYAUME.md) et le
[banc Hermes](../docs/BOUTIQUE-ATLAS-HERMES.md). Elles ne décrivent pas le
nouveau parcours de services du compte.
