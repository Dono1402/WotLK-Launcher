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

## Activation vérifiée

Le candidat a été déployé avec le nouveau module du worldserver et
`AtlasChat.Enable = 1`, après l'accord explicite « GO » du 7 septembre 2026.
Les deux services répondent depuis 18:07:54 UTC. La configuration privée,
les certificats et les dossiers d'état existants ont été conservés.

Le [rapport d'activation](../../docs/chat-refonte/whispers-deployment-20260907.md)
précise la bascule de 69,079 secondes, les versions actives, les sondes de
protocole et les observations des journaux. Les tests de paquets et les contrôles
de démarrage ne certifient pas l'affichage dans le client WoW : un aller-retour
réel, la touche de réponse et les changements de session restent à vérifier.

## Candidat du 9 septembre 2026 — non activé

Le nouveau [patch complet Atlas](atlas-update-20260909.patch) s'applique cette
fois à l'amont public `4247d957b78e6621047783560b3606f8fcf7ff42`, **pas** à
la base Atlas `01667dc` du patch historique ci-dessus. Il contient tous les
ajouts Atlas conservés, les whispers actifs et les tests de non-régression
ajoutés pendant la fusion. Ne pas appliquer les deux patches successivement.

- Commit du candidat source : `f859d0c59696b62483a98133b1e064c15dcb5604`.
- Parents : `01667dc73262db01b10a5fcb99b505528e52211c` et
  `4247d957b78e6621047783560b3606f8fcf7ff42`.
- Arbre Git : `0c3c4baca7b309142364e784618f2a44eba433f2`.
- SHA256 du patch :
  `6993061f19cb6485759ca20cd41afc9688afeb6cfb89c6f12a85aa9fec72baf5`.

L'application du patch contre `4247d957` a été vérifiée dans un index séparé :
elle reproduit exactement cet arbre Git, sans toucher aux fichiers du candidat.
Cinq avertissements de lignes vides de fin de fichier héritées restent dans
le patch ; ils n'altèrent pas la correspondance. Reproduire l'arbre ne
recrée pas automatiquement l'identité du commit de fusion ni son historique.

Les résultats, limites et conditions avant activation figurent dans le
[rapport de préparation](../../docs/WOTLK-UPDATE-PREPARATION-2026-09-09.md).
Ce candidat ne remplace pas la release du 7 septembre tant qu'une bascule
distincte n'a pas été autorisée et vérifiée.
