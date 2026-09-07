# Whispers Atlas dans Hermes

Le patch [`atlas-whispers.patch`](atlas-whispers.patch) s'applique à la base Atlas
`01667dc73262db01b10a5fcb99b505528e52211c` : la version réellement exécutée par
`hermesproxy-wotlk.service` lors de la préparation du 7 septembre 2026. Il conserve
les modifications Atlas et les corrections upstream de cette base.

Il ajoute deux traitements bornés :

- Les whispers adressés à `Pseudo#Launcher` sont transformés en soumission du
  pont privé avant la normalisation des noms de personnages. Les noms acceptent
  uniquement 3 à 32 caractères ASCII alphanumériques ou `_`. Un suffixe de realm
  ajouté par le client est retiré. Le texte reste complet et la langue initiale
  est conservée. Les messages d'addons gardent leur traitement existant.
- L'enveloppe legacy du module, avec nom explicite, GUID expéditeur nul et aucun
  indicateur GM, devient un `WHISPER` ou `WHISPER_INFORM` moderne. Les tailles, le
  GUID réel du destinataire, UTF-8 et les NUL terminaux sont contrôlés avant le
  traitement. Aucun compte, faux GUID ni cache de personnages n'est ajouté.

L'autorisation d'écrire à un compte reste contrôlée par le module et l'API :
session authentifiée, amitié acceptée, joueur humain, restriction de discussion
et quotas. Le proxy ne détermine pas le compte expéditeur et ne contourne pas ces
contrôles. La confirmation affichée correspond à la persistance du message,
pas à sa lecture par le destinataire.

## Préparation et tests

Depuis un checkout isolé de la base ci-dessus :

```sh
git apply --check /chemin/atlas-whispers.patch
git apply /chemin/atlas-whispers.patch
dotnet test HermesProxy.Tests/HermesProxy.Tests.csproj -c Release
HERMES_TEST_MODERN_BUILD=3.4.3 dotnet test HermesProxy.Tests/HermesProxy.Tests.csproj -c Release --no-build --filter FullyQualifiedName~AtlasLauncherWhisperTests
dotnet publish HermesProxy/HermesProxy.csproj -c Release -r linux-x64 --self-contained true -p:UsePublishBuildSettings=true -p:PublishSingleFile=false --output ../publish-linux-x64
```

`HERMES_TEST_MODERN_BUILD` ne concerne que l'initialisation de l'assemblage de test.
La configuration de la production conserve la sélection du protocole du client.

Vérification du 7 septembre 2026 sur Linux : **1 089 tests réussis**, puis
**16 tests ciblés réussis en 3.4.3**, incluant la lecture du paquet moderne sérialisé,
les réponses complètes de plus de 255 caractères, les noms et trames invalides,
la limite de 1000 caractères et la préservation du chemin des addons. Ces 16 tests
passent aussi sur Windows. Sur le Windows français, le test upstream de métriques
`GetSummary_IncludesGcLineWhenSupplied` attend un séparateur décimal anglais ;
la suite complète y obtient 1 088 réussites et cet échec de formatage indépendant.

Publication native Linux x64 self-contained réussie dans
`/opt/hermesproxy-candidates/atlas-chat-whispers-20260907/publish-linux-x64`.
SHA-256 de `HermesProxy.dll` :
`9790faf84cba359474945aeeeb64ee17416b93fc22913074c1c5aed726aa1f85`.

## Activation restante

Le candidat est préparé, sans bascule du service. Il doit être déployé avec le
nouveau module du worldserver et `AtlasChat.Enable = 1`. La configuration privée,
le certificat et les dossiers d'état existants doivent être conservés.

Les redémarrages nécessitent une autorisation explicite et interrompent les
connexions en cours. Les tests de paquets et de compilation ne certifient pas
l'affichage dans le client WoW : un aller-retour réel, la touche de réponse et
les changements de session restent à vérifier après activation.
