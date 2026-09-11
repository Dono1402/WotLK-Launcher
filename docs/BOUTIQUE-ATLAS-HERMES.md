# Validation du renommage via Hermes, sans interface graphique

Cette campagne du 11 septembre 2026 utilise les vrais services du royaume isolé
et un client réseau écrit pour le protocole 3.4.3.54261. Elle n'utilise ni Computer
Use, ni l'exécutable du jeu, ni le launcher installé. Elle complète les
[36 contrôles natifs](BOUTIQUE-ATLAS-TEST-ROYAUME.md).

## Périmètre et résultats

Le premier passage a validé **19 contrôles** avec le Hermes existant
`f859d0c59696b62483a98133b1e064c15dcb5604` :

- inscription d'un compte fictif par l'API et préparation d'un personnage par
  les vrais gestionnaires natifs ;
- émission réelle du ticket SSO du launcher, authentification BNet TLS/protobuf,
  choix du royaume et connexion 3.4.3 chiffrée en AES-GCM ;
- traduction de l'énumération des personnages, des GUID et du droit de renommage ;
- refus d'un renommage non acheté ou d'un nom invalide, consommation après un
  nom valide, absence de second débit lors d'une nouvelle tentative ;
- achats sur chacun des deux soldes fictifs, attente pendant la session et
  remise du droit après déconnexion ;
- entrée réelle dans le monde via les connexions de royaume et d'instance,
  refus de l'achat en ligne, déconnexion normale, sauvegarde et reconnexion.

Le passage final avec le correctif valide **27 contrôles**, le 11 septembre
à **20:56:32 UTC**, soit **63 contrôles réels** avec la campagne native. Il ajoute
les coupures réseau, la fermeture avant l'accusé de chiffrement, le remplacement
d'une connexion et la fermeture de la connexion d'instance après un reset TCP
explicitement provoqué. La livraison après coupure en jeu a pris **62,90 secondes**.
Le compte rendu final est conservé dans `hermes-test-result.json` hors Git.

À la fin de la campagne, les processus API/World/auth/Hermes de test et le
conteneur MySQL jetable sont arrêtés. Le contrôle de 20:57:21 UTC retrouve les
trois services publics actifs, avec leurs PID initiaux et leurs compteurs de
redémarrage à zéro. La preuve locale complète est
`artifacts/shop-realm/verified-hermes-report.json`, ignorée par Git.

Le chemin SSO est celui du launcher : Hermes provisionne la clé héritée à partir
du ticket API. Il ne passe pas par un échange SRP classique avec l'authserver.
L'authserver de test existe, mais son lancement n'est pas compté comme une
validation SRP. Aucun compte, personnage ou argent réel n'est utilisé.

## Défaut reproduit et correction

Sans `CMSG_LOG_DISCONNECT`, fermer la connexion moderne laissait son
`WorldClient` hérité actif. Son minuteur continuait à envoyer des pings au core.
La boutique attendait donc une session qui ne disparaissait pas. L'échec a été
reproduit avec le paquet Hermes existant, après les 19 contrôles normaux ; la
commande était toujours en attente à l'expiration du test de coupure.

Le [correctif fourni](../mod-atlas-shop/patches/hermes-f859d0c-disconnect-owner.patch)
lie explicitement chaque connexion moderne à son propre `WorldClient`. Sa
fermeture arrête ce client hérité et ses pings, y compris avant
`ENTER_ENCRYPTED_MODE_ACK`. Le nettoyage ne ferme pas le client d'une connexion
de remplacement. La session d'authentification nécessaire au changement de
royaume est conservée ; la connexion d'instance associée est fermée si nécessaire.

Un second défaut concernait les réinitialisations TCP : le code commun
`SocketBase.CloseSocket()` quittait immédiatement la méthode si le système
avait déjà marqué le socket déconnecté. Le nettoyage de session n'était donc
jamais appelé. Le correctif garantit son exécution une seule fois, même après
un reset ou des fermetures concurrentes. Trois tests réseau ciblés couvrent un
socket déjà déconnecté, un vrai reset du pair et des fermetures concurrentes.

En cas de coupure pendant le jeu, le core conserve volontairement le personnage
dans ses sessions hors ligne pendant **60 secondes**. Le test conserve ce délai
réel : la commande reste en attente durant cette période, puis la sauvegarde
et la livraison sont contrôlées. Ce délai normal est distinct de la connexion
orpheline, qui continuait auparavant à entretenir ses pings.

Le patch modifie `HermesProxy/World/Server/WorldSocket.cs` et
`Framework/Networking/SocketBase.cs`, et ajoute
`HermesProxy.Tests/Framework/SocketCloseLifecycleTests.cs`, à partir du commit
cité ci-dessus. Il est compilé dans une copie indépendante ; le dépôt source
initial, le service Hermes public et les installations du joueur ne sont pas
modifiés. Le patch n'est **pas déployé en production** ; aucune activation
publique n'a été effectuée.

SHA-256 des bibliothèques effectivement exécutées dans la fixture Linux :

| Fichier | SHA-256 |
| --- | --- |
| `HermesProxy.dll` | `52e9bbd6f4069e0a9a1b8b255e611274de127cbf63a0b1477f45bb1c1724d933` |
| `Framework.dll` | `521113a35bfd1051db86fd0ebd2b7ac9c2f07f81a92a84494d0837adcbbf5e6e` |

## Scripts et reproduction

Les fichiers de test se trouvent dans `mod-atlas-shop/tests/` :

- `run_realm_fixture.py` : pilote API/World, option `--with-hermes` pour la suite
  moderne, arrêt de ses processus même en cas d'erreur ;
- `hermes_realm_fixture.py` : configurations propres au test, connexion MySQL
  privée, secret SSO éphémère, écoute limitée au réseau privé ;
- `hermes_protocol_client.py` : BNet, formats 3.4.3, chiffrement et connexions de
  royaume/instance ;
- `test_hermes_realm.py` : achats, renommages, sessions et régressions réseau.

Le client de test utilise Python 3 et `cryptography` (43.0.0 sur la fixture).
Il reste volontairement limité à cette fixture et à cette version du protocole.

1. Préparer ou reprendre la fixture décrite dans le dossier du royaume. Le
   conteneur MySQL doit être celui dont l'identifiant et le montage ont été
   vérifiés. Le pilote attend désormais que son socket accepte une requête.
2. Cloner la copie Hermes revue dans un nouveau dossier et vérifier son commit
   `f859d0c59696b62483a98133b1e064c15dcb5604`. Pour un clone depuis une copie locale,
   conserver aussi sa référence `origin/master`, nécessaire à GitVersion.
3. Exécuter `git apply --check`, puis appliquer le patch dans cette nouvelle
   copie. Construire avec le SDK .NET 10 et les options de publication existantes :

   ```powershell
   dotnet publish HermesProxy/HermesProxy.csproj -c Release -r linux-x64 `
     --self-contained true -p:UsePublishBuildSettings=true `
     -p:PublishSingleFile=false -p:NuGetAudit=false -p:UseSharedCompilation=false `
     -m:1 -o <publication-de-test>
   ```

4. Placer le paquet initial dans `hermes/` ou le paquet corrigé dans
   `hermes-disconnect/`, sous la racine privée. Ne pas copier une configuration
   de production ; le pilote écrit ses propres fichiers.
5. Lancer le pilote sous les mêmes restrictions systemd que le test natif :
   `PrivateNetwork=yes`, `PrivateTmp=yes`, `ProtectSystem=strict`, seule la racine
   de test inscriptible, CPU 150 %, mémoire 4 Gio, sans swap, durée maximale
   1 200 secondes. Ajouter `--with-hermes --hermes-package hermes-disconnect`.
   Sans `--with-hermes`, le pilote exécute les 36 contrôles natifs.
6. Lire `hermes-test-result.json`, les journaux et les hashes du paquet exécuté.
   `passed` ne devient vrai qu'au terme de tous les contrôles. `--hold` sert
   uniquement au diagnostic et ne remplace pas un passage de validation.
7. Arrêter le MySQL jetable avec `prepare_realm_fixture.py --root <racine> --stop`.
   Les paquets, configurations privées et preuves restent hors Git.

## Vérifications du code et limites

La publication Linux du correctif réussit. Les avertissements de trimming déjà
présents dans le projet restent visibles ; cette campagne ne les corrige pas.
**350 tests ciblés passent dans la configuration par défaut.** Le filtre couvre
`SocketCloseLifecycleTests`, `NetworkThreadTests`, `BnetServer`, `World.Server`,
`SessionKeyGenerator`, `WorldCrypt` et `PacketHeader`. Sous
`HERMES_TEST_MODERN_BUILD=3.4.3`, 348 passent ; les deux cas
`DFProposalResponsePktTests.Read_ParsesWholePacketWithoutOverrunning` utilisent
un ancien format LFG et échouent aussi dans le code initial inchangé. Ce constat
est conservé dans les résultats TRX ; il n'est pas présenté comme une régression
du correctif ni comme une validation générale du LFG en 3.4.3.

La campagne couvre le comportement réseau et les effets SQL réels. Elle ne
valide pas le rendu du client, les textes de ses fenêtres, ni toutes les règles
de vérification du binaire client. Le TLS utilise le certificat de développement
de la fixture, sans modification du magasin de confiance du système. Le client
de test vérifie le format de la réponse Ed25519 d'entrée en mode chiffré, mais
pas sa signature ; le chiffrement AES-GCM des paquets est réellement utilisé et
ses tags sont vérifiés. Les autres
modules et services de boutique restent hors périmètre. Toute mise en service
publique demande une préparation et une autorisation distinctes.
