# Messages privés Atlas : module AzerothCore

Module autonome pour la migration launcher **0006_private_chat.sql**.
Il utilise la base auth déjà ouverte par AzerothCore et le worker de messagerie du
launcher. Il ne crée aucune table, aucun compte SQL, aucun service HTTP ni secret.
Il est **désactivé par défaut**. Après autorisation explicite, les candidats du
7 septembre 2026 ont été activés ensemble à 18:07:54 UTC. Le
[rapport de déploiement](../docs/chat-refonte/whispers-deployment-20260907.md)
précise les contrôles de démarrage et les essais en jeu restant à effectuer.

## Utilisation en jeu

Un message reçu apparaît uniquement chez le destinataire :

```text
Dono_42#Launcher vous chuchote : Bonjour !
```

La réponse vise le **pseudo du compte Atlas**, indépendamment du personnage.
Le client peut utiliser son interface de whisper habituelle, `/r` ou :

```text
/w Dono_42#Launcher Bonjour depuis le jeu !
```

Après confirmation de la persistance par l'API, l'expéditeur voit son message :

```text
À Dono_42#Launcher : Bonjour depuis le jeu !
```

Cette confirmation signifie que le launcher a conservé le message. Elle ne
signifie pas que l'autre personne l'a lu ou que son client de jeu l'a affiché.
Les messages restent consultables dans le launcher. La copie en jeu est limitée
à une connexion active : aucune récupération de l'historique à la connexion.

Le transport utilise l'enveloppe avec nom explicite `SMSG_GM_MESSAGECHAT`,
`CHAT_MSG_WHISPER` à la réception et `CHAT_MSG_WHISPER_INFORM` après confirmation
de l'envoi. L'indicateur GM reste nul. Aucun GUID de personnage n'est inventé.
L'adaptation [Hermes](hermes/README.md) reconnaît cette enveloppe bornée et écrit
un whisper moderne dont le nom ne contient pas le NUL terminal du protocole
ancien. Le GUID réel sert uniquement à identifier le personnage destinataire.

À l'envoi, Hermes reconnaît `Pseudo#Launcher` et transporte une soumission
`.atlasmsg Pseudo texte` vers le core avant la résolution du nom de personnage.
La commande n'est pas demandée à l'utilisateur ; elle reste utilisable comme
repli. La langue du joueur, le texte complet et les contrôles du module sont
conservés. Un message long reste une seule soumission, sans découpage préalable
en plusieurs messages par Hermes. Les captures de paquets sont testées ;
l'affichage effectif et la touche de réponse `/r` restent à vérifier en jeu
après une activation autorisée.

## Contrat et isolation

- L'identité d'envoi vient uniquement de `WorldSession::GetAccountId()` et du
  GUID du `Player` connecté. Les bots, la console et les joueurs muets sont exclus.
- Le pseudo cible doit correspondre à un profil Atlas avec une amitié acceptée.
  L'API revalide le profil, le propriétaire du personnage, l'amitié et le quota
  commun de 30 messages par minute avant d'écrire le message canonique.
- Chaque soumission utilise un UUID v4 sous forme `BINARY(16)` en ordre réseau.
  Un nouvel essai SQL réutilise cet UUID ; il n'écrase pas le premier contenu.
- Les résultats asynchrones conservent uniquement compte, personnage et numéro
  de connexion. Un résultat de l'ancienne connexion n'est jamais affiché après
  une reconnexion, même au même compte et au même personnage.
- Une copie en jeu n'est créée par l'API que si le compte a un personnage marqué
  en ligne. Le module vérifie ensuite le vrai `Player`, sa connexion et l'amitié.
- Un core ne réclame une ligne `realm_id=0` que pour un compte réellement présent
  chez lui. Une ligne déjà affectée à son realm mais devenue hors ligne est
  marquée `skipped`. Une ligne sans destinataire présent expire après 60 secondes.
- La date du message doit être postérieure au début de la connexion actuelle.
  Cela empêche une livraison après une déconnexion/reconnexion dans la fenêtre
  des 60 secondes. Core et base doivent avoir des horloges synchronisées ; les
  calculs SQL utilisent explicitement UTC et ne dépendent pas du fuseau SQL.
- Une livraison acquiert un bail de 30 secondes avec un jeton aléatoire. La
  lecture et l'ACK exigent ce même jeton. Le code abandonne un lot devenu lent
  avant l'expiration du bail. Un cache de 90 secondes empêche de répéter une
  écriture réseau déjà faite si son ACK SQL doit être retenté.
- La normalisation commune applique CRLF vers LF, supprime les espaces Unicode
  aux extrémités et limite à 1000 unités UTF-16 / 4000 octets UTF-8. Elle refuse
  les contrôles intérieurs sauf LF/TAB et les neuf contrôles bidi U+202A–U+202E /
  U+2066–U+2069. Les contenus restent stockés sans échappement WoW. Au rendu, les pipes deviennent `||`, les
  retours à la ligne deviennent des espaces et les fragments gardent des points
  de code UTF-8 complets. Chaque ligne fait au plus 230 octets.
- Les requêtes et transactions utilisent exclusivement les workers asynchrones
  `LoginDatabase`. Leurs callbacks sont consommés dans `WorldScript::OnUpdate`.
  Aucun pointeur `Player*` n'est conservé dans un callback SQL.

Le module borne les soumissions en attente à 128, les livraisons d'un lot à 64,
les comptes examinés d'un lot à 1024 et la déduplication à 8192 éléments. En cas
de saturation ou de délai de confirmation, il indique de consulter le launcher
avant de renvoyer. Il n'existe pas d'ACK applicatif du client WoW : l'affichage
effectif n'est pas prouvé par un appel réussi à `SendPacket`.

## Préparation d'une installation

1. Appliquer la migration auth 0006 du launcher et démarrer son worker de chat.
2. Placer ce dossier sous `core/modules/mod-atlas-chat`, puis reconfigurer et
   compiler AzerothCore. Les fichiers `src/` et `conf/` sont découverts par le
   système de modules du core ; aucun patch du core n'est nécessaire.
3. Appliquer et compiler l'adaptation Hermes correspondante. Les deux binaires
   doivent être déployés ensemble ; un ancien Hermes ne préserve pas ces noms.
4. Installer le fichier `conf/mod_atlas_chat.conf.dist` selon le déploiement,
   définir `AtlasChat.Enable = 1` et conserver le `RealmID` existant.
5. Déployer les nouveaux binaires et redémarrer Hermes et le worldserver dans
   une opération distincte et autorisée. Le module lit son activation au démarrage.

L'activation et les redémarrages ne sont pas exécutés par la préparation. La version du core dont
les hooks ont été examinés est `f67b86df8bec0d06b76ad17a9512f08d615f2057`, dans
`C:/Codex/Server WoTLK Custom/Arthas/core`.

## Validation locale

Depuis la racine du dépôt du launcher, exécuter d'abord
`Set-Location ./mod-atlas-chat` pour utiliser les commandes ci-dessous.
Les sources sont versionnées ici ; les compilateurs et en-têtes externes ne sont
pas copiés. Le contrat `mod-atlas-chat/tests/bridge-contract.sql` est également
découvert par les tests C# lancés depuis la racine du dépôt, sans chemin voisin.

`src/atlas_chat_policy.h` est inclus à la fois par le vrai module et par les
tests C++. Les tests ne réimplémentent pas ces règles.

```powershell
./tests/Run-PolicyTests.ps1 -ZigPath /chemin/vers/zig.exe
```

Exécution locale vérifiée le 7 septembre 2026 : **199 552 assertions réussies**,
compilation C++20 avec `-Wall -Wextra -Werror -pedantic`, puis exécution du binaire.
Les cas comprennent UTF-8 invalide, liens/textures/couleurs WoW, fragmentations
sur différentes limites en octets, message avant/après reconnexion, autre compte,
amitié retirée, expiration exacte et quota par compte.

Le compilateur portable utilisé est Zig 0.15.2 Windows x86_64, obtenu depuis la
[page officielle](https://ziglang.org/download/). L'archive de 92 614 574 octets
correspond au SHA-256 publié dans son manifeste officiel :
`3a0ed1e8799a2f8ce2a6e6290a9ff22e6906f8227865911fb7ddedc3cc14cb0c`.
Le compilateur, son cache et les résultats restent dans `artifacts/`, ignoré par
Git. Aucun programme d'installation ni changement de PATH persistant.

`tests/bridge-contract.sql` fournit les requêtes équivalentes paramétrées pour
les tests MySQL du launcher : claim, récupération du bail, jeton incorrect,
amitié retirée, TTL, ACK et soumission idempotente.
Les tests du launcher l'ont exercé sur MySQL 8.4 dans un schéma jetable :
**21 assertions réussies**, y compris une connexion SQL réglée sur `+05:30`
pour vérifier les comparaisons UTC.

Le vrai `atlas_chat.cpp` et son loader ont également été compilés en objets COFF
contre les en-têtes AzerothCore locaux inchangés, sans simulation des API du core :

```powershell
./tests/Test-CoreSyntax.ps1 -CoreRoot C:/chemin/vers/core -ZigPath C:/chemin/vers/zig.exe -BoostRoot C:/chemin/vers/boost_1_85_0
./tests/Test-CoreSyntax.ps1 -CoreRoot C:/chemin/vers/core -ZigPath C:/chemin/vers/zig.exe -BoostRoot C:/chemin/vers/boost_1_85_0 -Playerbots
```

Le compilateur utilise les en-têtes Boost 1.85.0, dont l'archive officielle a été
vérifiée avant extraction (`7009fe1faa1697476bdc7027703a2badb84e849b7b0baad5086b087b971f8617`).
La définition `FMT_CONSTEVAL=` vient du CMake réel du core. Deux avertissements
proviennent des en-têtes tiers fmt et G3D. Zig ne termine pas correctement son
mode `-fsyntax-only` sans objet émis ; le script compile donc de vrais objets
avec `-c`, ce qui vérifie aussi la génération de code, sans édition de liens.

Le candidat Linux a aussi été compilé et lié contre les objets exacts du
worldserver actif, avec Playerbots. Le script de préparation
[`build-atlas-chat-world-candidate.py`](../scripts/build-atlas-chat-world-candidate.py)
reconstruit d'abord les sections exécutables de la base et compare toutes les
sections ELF allouées (sauf la note d'identifiant de build). Le mode de liaison
économe en mémoire modifie les informations de débogage et l'identifiant de build,
mais les sections utilisées à l'exécution sont identiques avant l'ajout du module.
Les sept enregistrements de modules existants sont conservés. Le script écrit
uniquement dans le dossier du nouveau candidat et ne démarre aucun service.

Candidat : `/opt/arthas-next/candidates/atlas-chat-whispers-20260907/server/bin/worldserver`.
SHA-256 : `de9f14523f7b933b714904f05cb0d4bb6d65f6592c9cd4d2aaeb0de125bfe315`.
Les sources, commandes et contrôles de la base figurent dans son `build-manifest.json`
et son dossier `build-overlay`.
Pour ce candidat construit par superposition d'objets, les clés `AtlasChat.Enable`
et `AtlasChat.PollIntervalMs` doivent être placées dans le `worldserver.conf`
effectivement utilisé : la liste de fichiers de configuration des modules de la
base n'a pas été régénérée. Une compilation CMake complète peut utiliser le
fichier de configuration du module normalement.

**Limites de validation :** les tests locaux ne démarrent ni monde de test ni client
WoW. Le pont est maintenant actif en production, avec initialisation et connexions
vérifiées. Il reste à vérifier un aller-retour entre deux comptes amis dans le jeu
réel, la réponse par `/r`, la reconnexion du destinataire et le retrait d'amitié.
