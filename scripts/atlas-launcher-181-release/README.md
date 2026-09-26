# Correctif client Atlas 1.8.1

Procédure limitée aux candidats du 26 septembre 2026. La 1.8.0 publiée reste
immuable. Aucun déploiement d’API, migration, changement Caddy ou redémarrage de
service n’est effectué. L’API 1.8.0 déjà approuvée est vérifiée avant publication.

1. Compiler le client public et l’installateur 1.8.1 depuis le commit du correctif.
2. Valider la relance Windows native avec `scripts/launcher-update-token-probe`,
   le remplacement/rollback isolé et le contenu de l’installateur final.
3. Préparer `/tmp/atlas-release-181-20260926/client` avec les binaires, le
   publisher du dépôt, l’outil de manifeste, la clé publique et les deux notes.
   `inputs.json` fige le commit, les tailles et les SHA-256 de ces sept fichiers.
4. `publish-client.py prepare` exige le canal public 1.8.0, sauvegarde ses
   pointeurs, signe la 1.8.1 en privé et conserve les anciennes notes.
5. Préparer le tag du commit compilé et le brouillon GitHub avec
   `prepare-github.py`. Les six fichiers GitHub sont vérifiés par taille/hash.
6. `publish-client.py publish` vérifie les téléchargements HTTPS complets et
   remplace le manifeste public en dernier. Les services et Caddy doivent être
   inchangés ; les pointeurs sont restaurés si une vérification échoue.
7. `publish-github.py --publish`, exécuté comme debian dans son dossier de
   préparation, publie le brouillon et vérifie la dernière release et ses fichiers.

Les installations affectées doivent utiliser l’installateur une fois : le
nouveau candidat ne peut pas réparer le helper déjà exécuté depuis la 1.7.2.
Le client local reste séparé. Aucun launcher installé n’est ouvert ou fermé par
cette procédure. Les preuves et notes peuvent être versionnées, mais pas les
binaires, configurations privées ou clés de signature.
