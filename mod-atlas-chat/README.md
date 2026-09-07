# Messages privés Atlas : module AzerothCore

Module autonome préparé localement pour la migration launcher **0006_private_chat.sql**.
Il utilise la base auth déjà ouverte par AzerothCore et le worker de messagerie du
launcher. Il ne crée aucune table, aucun compte SQL, aucun service HTTP ni secret.
Il est **désactivé par défaut** et n'a pas été installé sur le serveur.

## Utilisation en jeu

Un message reçu apparaît uniquement chez le destinataire :

```text
[Atlas] Dono_42 : Bonjour !
[Atlas] Pour repondre : .atlasmsg Dono_42 votre message (pseudo du compte Atlas).
```

La seconde ligne apparaît à la première réception de chaque connexion au jeu.
La réponse utilise le **pseudo du compte Atlas**, indépendamment du personnage :

```text
.atlasmsg Dono_42 Bonjour depuis le jeu !
```

Après confirmation de la persistance par l'API, l'expéditeur voit son message :

```text
[Atlas -> Dono_42] Bonjour depuis le jeu !
```

Cette confirmation signifie que le launcher a conservé le message. Elle ne
signifie pas que l'autre personne l'a lu ou que son client de jeu l'a affiché.
Les messages restent consultables dans le launcher. La copie en jeu est limitée
à une connexion active : aucune récupération de l'historique à la connexion.

Le transport utilise `SMSG_MESSAGECHAT / CHAT_MSG_SYSTEM`, envoyé à la seule
session du destinataire. `/w`, `/r`, les faux GUID de personnages et les faux noms
de personnages ne sont pas utilisés. Dans le protocole 3.3.5, le whisper normal
résout un GUID de personnage. La variante avec nom explicite traverse une autre
branche Hermes, qui conserve actuellement le NUL du nom lu avec `ReadString`.
Ce chemin n'a pas été validé en jeu ; le module conserve donc une réponse
explicite et fiable par `.atlasmsg`.

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
3. Installer le fichier `conf/mod_atlas_chat.conf.dist` selon le déploiement,
   définir `AtlasChat.Enable = 1` et conserver le `RealmID` existant.
4. Déployer le nouveau binaire et redémarrer le worldserver dans une opération
   distincte et autorisée. Le module lit son activation au démarrage.

Ces étapes ne sont pas exécutées par ce travail local. La version du core dont
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

Exécution locale vérifiée le 6 septembre 2026 : **199 546 assertions réussies**,
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

**Limites de validation :** le module n'a pas été lié avec le worldserver ;
les bibliothèques natives nécessaires au build complet restent à valider. Aucun
worldserver, Hermes, launcher ou client WoW n'a été démarré pour ces tests. Il
reste à valider le binaire du module puis un aller-retour réel entre deux comptes
amis, la coupure/reconnexion du destinataire et le retrait d'amitié.
