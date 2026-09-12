# Préparation de la conversion d’or — 1.7.2

Les trois scripts de ce dossier préparent et testent des candidats. Aucun
n’installe un override systemd, ne migre la base publique ou ne redémarre
un service de production. Leur périmètre est fixe et ils refusent d’écraser
un candidat existant.

1. `prepare-world.py` compile le module et ses hooks dans un nouveau candidat
   avec son propre chemin de configuration, lié au banc privé pendant les tests.
2. `prepare-backend.py` reçoit l’archive API et le précontrôle, copie les
   configurations actuelles vers les candidats, prépare les indicateurs
   désactivés/activés et vérifie les deux unités fusionnées par
   `systemd-analyze verify`. Les overrides restent dans le répertoire privé.
3. `test-candidate.py` teste ces mêmes binaires avec le véritable core, MySQL,
   API et Hermes dans le réseau privé. Il vérifie les empreintes et arrête les
   processus et le conteneur MySQL de test à la fin.

Le plan et la preuve complète sont conservés sous
`/opt/atlas-shop-releases/gold-1.7.2-20260912` ; les configurations contiennent
des secrets et ne doivent jamais être copiées dans Git.

## Maintenance restant à autoriser

- Sauvegarder les bases et les configurations après arrêt des écritures World
  et API. Vérifier le dump et conserver le rollback précédent.
- Installer uniquement les deux overrides préparés, basculer le lien de
  configuration du nouveau World vers `server/etc-production`, puis recharger
  systemd. Le WorkingDirectory World, les quatre workers et les autres modules
  restent ceux du précontrôle.
- Démarrer l’API compatible avec le schéma 0014 et les achats/conversions fermés,
  vérifier la migration, puis démarrer World et attendre les deux heartbeats
  des services natifs et de la conversion. Les droits SQL actuels du rôle
  `arthas` couvrent les nouvelles tables au niveau du schéma ; la préparation
  ne crée aucun droit supplémentaire.
- Vérifier les lectures de boutique, activer les indicateurs préparés, puis
  publier le launcher, son manifeste signé et l’installateur 1.7.2. Conserver
  les fichiers des versions précédentes et ne pas remplacer un artefact versionné.
- Vérifier les services, le schéma, les flags, les téléchargements HTTPS et
  leurs empreintes. Les vérifications publiques ne doivent pas dépenser l’or
  d’un vrai joueur.

World déconnectera les joueurs et l’API sera brièvement indisponible. Cette
mise à niveau conserve les processus Hermes et Auth existants. Après migration,
un retour arrière doit conserver l’API 0014 avec les conversions fermées et
restaurer uniquement l’ancien World. Ne pas supprimer les reçus ni rétablir
les anciens soldes par une restauration de base après de nouvelles opérations.

Le détail fonctionnel et les limites des tests figurent dans
[la documentation de conversion](../../docs/BOUTIQUE-ATLAS-CONVERSION-OR.md).
