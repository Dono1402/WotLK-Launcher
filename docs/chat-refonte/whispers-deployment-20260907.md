# Activation des whispers Atlas — 7 septembre 2026

Après l'autorisation explicite « GO », les versions préparées de HermesProxy et
du worldserver ont été activées ensemble. Les deux services répondent depuis
**18:07:54 UTC** (20:07:54 à Paris). La durée entre la demande d'arrêt de Hermes
et le retour des deux services prêts est de **69,079 secondes**.

Le module annonce dans le journal du monde :

```text
Atlas chat bridge ready for realm 1 (named #Launcher whispers).
```

L'activation serveur est vérifiée. L'affichage dans un vrai client WoW, un
aller-retour entre amis et la réponse par `/r` restent à valider en jeu. Aucun
client utilisateur n'a été ouvert et aucun message de test n'a été envoyé à
des utilisateurs.

## Déroulement et services

| Étape | Heure UTC |
|---|---|
| Préparation et sauvegardes vérifiées | 18:06:09 |
| Arrêt demandé de Hermes | 18:06:45 |
| Arrêt demandé du monde | 18:06:48 |
| Ancien monde arrêté proprement | 18:06:57 |
| Démarrage du nouveau monde | 18:06:58 |
| Monde et passerelle prêts | 18:07:52 |
| Démarrage de Hermes | 18:07:52 |
| Deux services prêts | 18:07:54 |
| Stabilisation initiale validée | 18:08:25 |
| Contrôles réseau et journaux terminés | 18:15:47 |

| Service | PID après activation | Résultat |
|---|---:|---|
| `arthas-worldserver.dungeon-clear-8224099.service` | 1910234 | Nouvelle version, active, zéro redémarrage automatique. |
| `hermesproxy-wotlk.service` | 1910530 | Nouvelle version, protocole `V3_4_3_54261`, zéro redémarrage automatique. |
| `wotlk-launcher-api.service` | 1866560 | PID et instant de démarrage inchangés. |
| `arthas-authserver.service` | 323656 | PID et instant de démarrage inchangés. |

`arthas-worldserver.service` reste l'alias du service du monde ci-dessus.

## Versions et configuration effectives

- Monde : `/opt/arthas-next/candidates/atlas-chat-whispers-20260907/server/bin/worldserver`.
  SHA-256 : `de9f14523f7b933b714904f05cb0d4bb6d65f6592c9cd4d2aaeb0de125bfe315`.
- Configuration du processus monde :
  `/opt/arthas-next/candidates/atlas-chat-whispers-20260907/server/etc/worldserver.conf`.
  Elle reprend la configuration précédente, ajoute `AtlasChat.Enable = 1` et
  `AtlasChat.PollIntervalMs = 1000`, et fixe `Updates.EnableDatabases = 0`.
- Hermes : `/opt/hermesproxy-wotlk/releases/atlas-chat-whispers-20260907`.
  SHA-256 de `HermesProxy.dll` :
  `9790faf84cba359474945aeeeb64ee17416b93fc22913074c1c5aed726aa1f85`.
  La version est construite sur `01667dc73262db01b10a5fcb99b505528e52211c`
  avec le [patch des whispers](../../mod-atlas-chat/hermes/atlas-whispers.patch).
  Son libellé embarqué conserve cette base et l'indication `dirty` ; l'empreinte
  ci-dessus identifie le binaire patché.
- Hermes conserve `/opt/hermesproxy-wotlk/appsettings.atlas.json`, les certificats
  et les liens vers `AccountData`, `Logs` et `PacketsLog`. Les 114 fichiers CSV
  sont identiques à la précédente version.
- Une surcharge `60-atlas-chat-whispers.conf` est ajoutée dans chacun des deux
  répertoires systemd du monde et de Hermes. Les surcharges antérieures sont
  conservées. Le répertoire de travail du monde reste celui de
  `modules-update-20260905T1016Z`.

Les variables `AC_UPDATES_ENABLE_DATABASES=0` et
`AC_PLAYERBOTS_UPDATES_ENABLE_DATABASES=0` sont présentes dans le vrai processus
monde. Elles empêchent aussi les configurations de modules de réactiver les
mises à jour SQL automatiques au démarrage. Cette activation ne demande aucune
migration. L'historique des huit migrations du launcher, y compris empreintes
et dates, est inchangé. Les **19 fichiers préexistants contrôlés** (configurations,
surcharges et certificats) conservent leurs empreintes.

Le monde réutilise les objets validés de la version précédente avec ses sept
enregistrements de modules existants. La méthode de compilation et ses limites
concernant les informations de débogage sont décrites dans le
[module](../../mod-atlas-chat/README.md).

## Contrôles après démarrage

- Le chemin `/proc/<pid>/exe` et l'empreinte du monde correspondent au candidat.
  Le processus Hermes exécute la nouvelle release et sa DLL possède l'empreinte
  attendue.
- Le monde émet un vrai `SMSG_AUTH_CHALLENGE` de 40 octets sur le port 4000.
- Les ports modernes 8084 et 8086 émettent leur salutation WoW V2 exacte.
- Les ports 1119 et 8081 négocient TLS 1.2. Ce contrôle vérifie la négociation
  avec les certificats de jeu conservés, pas une validation par une autorité
  de certification publique.
- La santé publique du launcher répond HTTP 200. Les routes privées du chat
  v1/v2 répondent HTTP 401 sans session, avec `Cache-Control: no-store`.
- Le service interne de tickets sur 8099 refuse une requête sans secret avec
  HTTP 401. Aucun ticket de compte utilisateur n'est créé par ce contrôle.
- Aucune erreur Hermes depuis le démarrage de la nouvelle version, aucune
  erreur d'initialisation du pont et aucune erreur de table de chat observée.

Les journaux du monde ne sont pas exempts d'erreurs : deux deadlocks SQL ont été
observés, l'un pendant le nettoyage des courriers orphelins au démarrage et
l'autre dans la persistance des sorts de familiers. Des doublons `pet_spell`
sont également visibles ; ce type de doublon existait déjà dans le journal
sauvegardé avant la bascule. Deux avertissements de chemins de créatures absents
ont été relevés. Ces requêtes ne concernent pas les tables du pont de discussion.
Les sondes TCP sans dialogue TLS complet ont en outre produit des avertissements
EOF locaux attendus dans Hermes. Ces observations sont conservées dans les
preuves ; elles ne sont pas présentées comme un journal sans erreurs.

## Retour arrière et preuves

Les anciennes versions restent en place : monde `armory-live-20260905T1310Z`
et Hermes `01667dc`. Les sauvegardes privées et le script d'activation sont dans
`/opt/atlas-deployments/atlas-chat-whispers-20260907`, accessible uniquement à root.
Le script a été lancé par un service systemd temporaire pour poursuivre la
bascule et son éventuel repli même si la connexion SSH se ferme.

Son action `rollback` arrête les deux services, rétablit les anciennes versions
et la configuration précédente, puis contrôle leur reprise. Elle conserve une
surcharge qui désactive les mises à jour SQL automatiques. Elle ne restaure pas
la base et ne supprime aucune donnée de message. **Aucun rollback réel n'a été
exécuté.** Un retour arrière en production nécessite une nouvelle autorisation
pour les interruptions correspondantes.

Les preuves sans secrets sont conservées localement sous
`artifacts/atlas-profile-video-whisper-20260907/deployment` : `result.json`,
`post-verification.json`, `preflight.json` et `events.jsonl`. La préparation et
l'ancienne vérification locale sont des photographies antérieures ; leurs
indicateurs « en attente d'activation » ne décrivent pas l'état présent.
