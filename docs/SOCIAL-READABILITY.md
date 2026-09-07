# Lisibilité de Messages et de la liste d'amis

Cette évolution du client conserve la fenêtre fixe et le décor de la Citadelle. Le texte principal de Messages utilise 15 px et les informations secondaires 12 px. Le fond des conversations est légèrement plus dense ; les messages envoyés par le compte connecté portent un filet bleu discret et une teinte de fond douce.

## Liste d'amis

- En-tête compact, espacement réduit et lignes à deux niveaux typographiques.
- Le pseudo Atlas reste l'identité principale. L'infobulle distingue le compte du personnage actif ou du dernier personnage joué.
- Les informations hors ligne utilisent une couleur secondaire et la pastille reste attachée à l'avatar.
- Un bouton Message apparaît au survol ou au focus clavier. Le mini-profil et le menu du clic droit restent disponibles.
- Une recherche locale filtre les amis par pseudo ou personnage ; elle est distincte du champ d'ajout d'un ami.
- Les groupes En ligne et Hors ligne affichent leur nombre et peuvent être repliés. Une recherche ouvre les groupes pour rendre les résultats visibles.

## Recherche dans une conversation

`Ctrl+F` ouvre la recherche du dialogue sélectionné. Aucune loupe permanente n'est ajoutée. `Entrée` et `Maj+Entrée` parcourent les résultats ; `Échap` ferme la recherche et rend le focus au contrôle précédent.

La recherche porte sur le texte visible des messages. Elle ignore la casse et les accents, accepte la ponctuation comme texte littéral et conserve les liens, médias et commandes interactives. Les messages supprimés et les spoilers encore masqués sont exclus.

Les pages plus anciennes sont chargées progressivement par le parcours existant de l'historique. L'interface indique si l'historique est complet ou partiel. Pour une conversation longue, le bouton Continuer dans l'historique permet de poursuivre après une série de pages ; les erreurs sont signalées avec une possibilité de reprise. Fermer la recherche ou changer de conversation arrête la planification des pages suivantes.

Les déplacements automatiques de recherche ne marquent pas la conversation comme lue. Les résultats et la recherche sont réinitialisés au changement de compte ou de conversation ; le brouillon reste conservé.

## Validation

Les scénarios utilisent des comptes et médias fictifs, un navigateur headless isolé et des contrôles WPF rendus hors écran ou sans fenêtre native. Ils n'ouvrent pas le launcher de l'utilisateur et n'accèdent pas au serveur de production.

Les suites ciblées couvrent la recherche, sa pagination et les réponses tardives, les changements de compte, les actions de la liste d'amis, le filtrage et les groupes, puis les parcours existants de messagerie et de médias. Les captures et journaux générés restent hors du dépôt Git.

Ces modifications du client ne nécessitent ni migration de base de données ni nouvelle route serveur. Elles ne modifient pas les fichiers de la release publique 1.5.0.
