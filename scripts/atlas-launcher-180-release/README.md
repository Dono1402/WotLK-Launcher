# Publication Atlas Launcher 1.8.0

Cette procédure concerne uniquement les candidats du 26 septembre 2026. Ne pas
réutiliser les scripts pour une autre version ni relancer une phase interrompue
sans examiner son état.

1. Compiler le client public et son installateur avec le runtime d’armurerie
   vérifié. La version locale reste distincte, sans mise à jour automatique.
2. `release-api.py prepare` vérifie l’API 1.7.2 et les migrations 1 à 14, copie le
   candidat Linux 1.8.0 dans un répertoire séparé, conserve la configuration et
   prépare le passage au schéma 15 sans démarrer le candidat.
3. `publish-client.py prepare` sauvegarde les pointeurs actuels et prépare les
   fichiers versionnés, les notes FR/EN et le manifeste signé en privé. La clé
   privée de signature ne quitte pas le serveur.
4. Après accord explicite pour le bref redémarrage de l’API et la migration 15,
   `release-api.py activate --approved-api-restart-and-schema15` sauvegarde
   `arthas_auth` en transaction cohérente, active le candidat et contrôle les
   routes publiques sans envoyer d’e-mail ni modifier de compte. La migration
   ajoute uniquement la table des liens de réinitialisation. Les migrations
   antérieures restent inchangées.
5. `publish-client.py publish` exige l’activation de l’API, vérifie les octets
   préparés et les téléchargements HTTPS, puis remplace les pointeurs publics.
   Le manifeste de mise à jour est remplacé en dernier. Les anciennes notes sont
   conservées. Aucun service n’est redémarré pendant cette phase.
6. Publier sur GitHub les six fichiers vérifiés : `AtlasLauncherSetup.exe`,
   `armory-runtime.zip`, `launcher-update.json`, les deux notes Markdown et
   `SHA256SUMS.txt`. Le client brut reste réservé au canal automatique signé.

L’activation de l’API interrompt brièvement ses connexions, le chat et la boutique.
Les processus World, Auth et Hermes restent en place. Les sauvegardes SQL, les
configurations privées et les binaires restent hors Git. Ne jamais restaurer
automatiquement une base active : le schéma 15 n’est pas reconnu par l’ancienne
API. En cas d’échec, conserver les diagnostics et corriger le candidat avant de
publier le client ; tout retour arrière de schéma doit être évalué séparément.

Vérifications locales : formulaires WPF hors écran sans fenêtre native,
authentification sans interface, stabilité de l’actualisation boutique,
installation/désinstallation dans un dossier de test et récupération sur MySQL
jetable avec e-mail et Hermes simulés. Cela ne prouve pas la réception d’un vrai
e-mail de réinitialisation.
