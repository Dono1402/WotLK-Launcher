# Correctif Windows Atlas 1.8.2

Procédure limitée aux candidats du 26 septembre 2026. Les versions précédentes
restent immuables. Aucun changement de base, d’API, de Caddy ou de service.

1. Compiler le client public et l’installateur depuis le commit du correctif.
2. Valider `--installer-shell-lifecycle`, `--installer-reparse-security` et le
   paquet final. Le banc utilise des fichiers jetables et lit seulement les
   chemins de raccourcis réels ; il ne désinstalle pas le launcher de l’utilisateur.
3. Préparer `/tmp/atlas-release-182-20260926/client` avec les deux EXE, le
   publisher du dépôt, l’outil de manifeste, la clé publique et les notes FR/EN.
   `inputs.json` fige le commit, les tailles et les SHA-256 de ces sept fichiers.
4. `publish-client.py prepare` exige le canal 1.8.1, sauvegarde les pointeurs et
   prépare la 1.8.2 signée en privé. La clé privée ne quitte pas le serveur.
5. Préparer le tag du commit compilé, puis le brouillon GitHub avec
   `prepare-github.py` ; vérifier ses six fichiers et leurs empreintes.
6. `publish-client.py publish` contrôle les téléchargements HTTPS complets,
   conserve les anciennes notes et change le manifeste public en dernier.
   Les PID des services et la configuration Caddy doivent rester identiques.
7. Publier le brouillon avec `publish-github.py --publish` comme debian et
   enregistrer les preuves publiques, sans les binaires ni les secrets.

La création des liens utilise les chemins communs Windows fixes, une chaîne de
répertoires verrouillée contre les renommages et un remplacement atomique du
raccourci préparé dans le répertoire d’installation. Les permissions globales
du Bureau et du menu Démarrer ne sont pas modifiées. Les fichiers inconnus et les
cibles de jonctions ne sont pas supprimés par le nettoyage de désinstallation.

Le client seul ne remplace pas un ancien `Uninstall.exe` : le setup est nécessaire
pour appliquer aussi le correctif de désinstallation à une installation existante.
