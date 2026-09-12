# Préparation Atlas 1.7.0 — services de compte natifs

Ces scripts sont liés à la livraison du 12 septembre 2026 et aux chemins exacts
audités sur Atlas. Ils préparent et vérifient des candidats privés. Ils ne
redémarrent aucun service public, ne publient pas le launcher et n'appliquent pas
de migration à la base publique. Les chemins existants provoquent volontairement
un arrêt dans les scripts de préparation : ne pas les relancer pour « réparer »
un candidat sans examiner son état.

## État préparé

- Racine : `/opt/atlas-shop-releases/native-1.7.0-20260912`, accessible à root.
- World : `/opt/arthas-next/candidates/atlas-shop-rename-native-20260912`.
- API : `/opt/wotlk-launcher-api-releases/shop-native-1.7.0-20260912`.
- Hermes : `/opt/hermesproxy-wotlk/releases/hermes-shop-native-1.7.0-20260912`.
- Client, installateur et manifeste signé : `launcher/prepared` sous la racine privée.
- Configurations exactes et destinations : `operations/operations.json`.
- Sauvegarde préparatoire : `latest-backup.json` et la preuve qu'il référence.

`prepare-world.py` compile un World inactif à partir des modules vérifiés.
`prepare_backend.py` vérifie les empreintes de production, extrait les paquets
API/Hermes contrôlés et copie les configurations publiques dans les candidats.
`test-candidate.py` exécute les octets définitifs dans le royaume jetable isolé,
puis arrête ses processus et son conteneur MySQL. `prepare-client.py` signe le
manifeste du client avec la clé Atlas déjà installée, dont la partie publique est
comparée à l'ancre embarquée. La clé privée n'est ni exportée ni copiée.
`prepare-operations.py` rend les trois unités complètes et les vérifie avec
`systemd-analyze verify`; leurs surcharges restent privées.

La vérification finale est une lecture seule :

```bash
sudo python3 /opt/atlas-shop-releases/native-1.7.0-20260912/scripts/verify-prepared.py
```

Elle vérifie aussi que chaque compte de service peut lire ses fichiers et
exécuter son binaire, que les quatre processus publics n'ont pas changé, que les
configurations actives correspondent aux sauvegardes, et que le manifeste public
est encore celui de la 1.6.0.

## Bascule proposée, après accord de maintenance

L'activation nécessite un redémarrage de World et Hermes, donc une déconnexion
des joueurs, ainsi qu'une brève interruption de l'API du launcher. Le service
d'authentification reste actif. Le nombre de workers CharacterDatabase reste 4.
Les étapes ci-dessous sont une procédure d'exploitation à suivre et vérifier
séquentiellement, pas un script à lancer en aveugle.

1. Refaire `verify-prepared.py`. Si un processus, un fichier ou une configuration
   a changé, actualiser l'audit et la sauvegarde concernés avant de continuer.
2. Arrêter `hermesproxy-wotlk`, `wotlk-launcher-api` puis
   `arthas-worldserver.dungeon-clear-8224099`. Vérifier leur `MainPID=0` et leur
   état inactif. Conserver le PID initial d'`arthas-authserver` pour le contrôle.
3. Exécuter `scripts/backup.py --after-stop` sous la racine privée. Les trois
   schémas `arthas_auth`, `arthas_chars`, `arthas_playerbots` sont sauvegardés.
   Cette option refuse de continuer si un service écrivain est encore actif.
   La copie préparatoire en ligne ne remplace pas ce point de restauration :
   certaines tables ne sont pas transactionnelles. Vérifier le succès du dump,
   son SHA-256 et l'intégrité gzip avant toute migration.
4. Installer uniquement les trois fichiers répertoriés dans
   `operations/operations.json`, aux destinations exactes qui y figurent, avec
   propriétaire root et mode 0644. Ils sont tous nommés
   `zzzzzz-atlas-shop-native-170.conf`; ils ne remplacent aucune ancienne
   surcharge. Le fichier privé `operations/api.env` configure le plafond 13,
   les services de compte activés et les achats encore désactivés.
5. Remplacer atomiquement le lien du candidat World `server/etc` par un lien
   vers `server/etc-production`. Vérifier d'abord sa cible actuelle, le répertoire
   de test. Ne jamais copier la configuration publique à travers ce lien.
6. Faire `systemctl daemon-reload`, vérifier les trois `ExecStart`, les comptes
   de service, le répertoire de travail conservé de World et la dernière
   `EnvironmentFile` de l'API. Démarrer l'API : attendre `/health` sur
   `127.0.0.1:4323`, contrôler les migrations 1 à 13 et l'absence d'erreur de
   checksum, de migration ou d'accès disque. Les achats restent fermés.
7. Démarrer World, attendre son écoute sur le port 4000 et son initialisation
   complète, puis démarrer Hermes avec sa configuration publique existante.
   Vérifier les empreintes des trois exécutables réellement lancés, les journaux,
   l'absence de redémarrage en boucle, l'état du royaume et le PID Auth préservé.
8. Vérifier dans `arthas_auth.atlas_shop_delivery_health` une ligne pour
   `realm_id=1`, `protocol=2`, `character_database='arthas_chars'`, dont
   `last_seen_at` est renouvelé depuis moins de 30 secondes. Vérifier les
   lectures authentifiées du catalogue et de l'historique sans créer de débit
   réel de test. Les services autres que le changement de nom restent fermés.
9. Copier atomiquement `operations/api-enabled.env` sur `operations/api.env`,
   puis redémarrer uniquement l'API. Vérifier à nouveau sa santé, son plafond 13,
   le heartbeat World et la disponibilité du changement de nom. Cette étape
   ouvre les achats ; elle doit venir après les contrôles précédents.
10. Publier d'abord les répertoires immuables 1.7.0 du client et de l'installateur
    depuis `launcher/prepared/public/releases/1.7.0` vers
    `/var/www/wotlk-launcher/launcher/releases/1.7.0`, puis ceux du stockage vers
    `/srv/wotlk/launcher-releases/v1.7.0`. Refuser un fichier existant dont les
    octets diffèrent. Télécharger les deux EXE par leur URL HTTPS finale et
    vérifier leurs tailles et SHA-256 avant de rendre la version visible.
11. Vérifier de nouveau les empreintes `launcher/proof.json:publicBefore`.
    Actualiser les métadonnées de publication pour leurs chemins publics réels
    (celles du préparateur portent les chemins privés de staging), copier les
    métadonnées sous `/opt/wotlk-launcher-release/releases/v1.7.0`, puis remplacer
    atomiquement le manifeste `current`, les trois alias EXE publics et le flux
    de notes par `launcher/prepared/patch-notes.next.json`. Remplacer en dernier
    le manifeste public `launcher-update.json` par l'exemplaire signé préparé.
    Préserver toutes les anciennes entrées et tous les fichiers versionnés 1.6.0.
12. Relire le manifeste public par HTTPS, vérifier sa signature et ses octets,
    les tailles des téléchargements, les notes via l'API et la santé des services.
    Enregistrer la preuve de publication. Ne pas annoncer une validation
    graphique du jeu : le parcours natif a été éprouvé au niveau protocole dans
    le royaume privé, sans ouvrir le jeu de l'utilisateur.

## Retour arrière

Avant toute migration, retirer seulement les nouvelles surcharges installées
(après comparaison de leur SHA-256), recharger systemd et relancer les versions
précédentes. Leurs binaires et configurations sont conservés.

Après migration 13, le retour opérationnel conserve l'API 1.7.0 et son plafond 13.
Remettre `RenameEnabled=false` dans son fichier d'environnement et redémarrer
l'API. Retirer uniquement les nouvelles surcharges World/Hermes dont les
empreintes correspondent au dossier, recharger systemd, puis relancer les anciens
World/Hermes. Les commandes de boutique, soldes, reçus et remboursements sont
conservés. L'ancienne API limitée au schéma 8 refuserait le schéma 13.

Si les pointeurs du client ont déjà changé, restaurer d'abord l'ancien manifeste
public, puis les notes et alias sauvegardés dans `launcher/public-before`, avec
leurs permissions et propriétaires d'origine. Conserver les répertoires 1.7.0
immuables pour le diagnostic. Une réversion du manifeste ne désinstalle pas les
clients qui ont déjà téléchargé la 1.7.0 ; ils doivent pouvoir utiliser l'API
conservée, achats désactivés.

Ne pas restaurer une ancienne base après reprise du jeu ou création de commandes :
cela effacerait les écritures intervenues depuis. Une restauration complète est
une opération distincte, avec tous les écrivains arrêtés et revue explicite du
point de restauration. L'intégrité gzip de la copie préparatoire a été vérifiée;
la restauration complète de ce dump n'a pas été répétée.
