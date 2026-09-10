# Livraison Atlas Launcher 1.6.0

État : candidat compilé, vérifié et signé dans un espace privé ; publication publique en cours de préparation.

## Distribution

- Produit : **Atlas Launcher 1.6.0**, Windows x64, version PE **1.6.0.0**.
- Installateur joueur : **AtlasLauncherSetup.exe**, 507033689 octets.
- Client public embarqué : **WotLK-Launcher.exe**, 401669959 octets, SHA-256 `e8f3f3dd231581f12475271ce07ff1bf2d61e030449d0670bba9d4abec19e935`.
- Paquet armurerie : **318 fichiers**, 298278314 octets ; sources, dépendances, tailles et SHA-256 vérifiés.
- GitHub : un seul exécutable joueur, l'installateur, avec le runtime d'armurerie, le manifeste signé, les notes FR/EN et leurs empreintes.
- Le client autonome est conservé sur le serveur pour les mises à jour automatiques.

## Notes de version

**55 changements dans 9 rubriques**, en français et en anglais. Les deux versions sont alignées exactement avec les traductions embarquées. La nouvelle note est ajoutée devant les cinq publications précédentes sans les modifier. Après réception de la note publiée, le client local cesse d'ajouter son ancien brouillon.

## Corrections de compatibilité avant publication

Le lecteur strict du manifeste accepte le champ `generatedAt` du flux effectivement déployé. Son ancienne adresse de base Atlas est remplacée par l'origine HTTPS officielle avant toute requête. Le manifeste vérifié contient **1 950 fichiers** ; le catalogue actuel contient **14 addons**. Ces deux documents sont seulement lus pendant cette livraison.

Le démarrage après une mise à jour depuis la 1.5.0 reconnaît la transaction historique pour envoyer les deux confirmations attendues par l'ancien helper. Les opérations élevées et la récupération normale continuent d'exiger le nouveau schéma authentifié. Le test vérifie les signaux, l'absence de réécriture de la transaction et le rejet des cibles, versions, empreintes, phases et chemins incohérents.

## Validation

- Compilation publique et compilation des tests : **0 avertissement, 0 erreur**.
- **16 contrôles ciblés réussis** : notes et traductions, flux jeu/addons actuels, maintenance du jeu, remplacement atomique et signatures des mises à jour, coordinateur de mise à jour, installation/désinstallation isolées, artefact final, paquet armurerie, intégrité des addons, sécurité et concurrence des sessions, routage de démarrage, messagerie et médias.
- Contrôle du ZIP d'armurerie : **12 vérifications**, 318 fichiers déclarés, aucun fichier privé ou de développement interdit.
- Scripts de publication : **29 assertions** sur fichiers jetables et HTTP simulé.
- Analyse Microsoft Defender des fichiers de livraison : **aucune détection**.
- L'installation isolée utilise le client canonique et l'installateur final, puis vérifie leur retrait. Aucun launcher joueur ni jeu réel n'est démarré.

## Périmètre et limites

Cette livraison publie le client et l'installateur. Elle ne déploie pas les nouvelles migrations ou le nouveau backend d'authentification et ne nécessite aucun redémarrage du royaume, d'HermesProxy ou de l'API.

Les tests de migration sont simulés ; aucune validation UAC complète sous `Program Files` n'est revendiquée. L'accès authentifié réel à la page de notes de version n'a pas été observé avec un compte joueur. La suite WPF complète de toutes les pages n'est pas présentée comme réussie : son échec de référence `SettingsToggleDisabled`, distinct de ces modifications, reste connu.

Les exécutables ne disposent pas d'une signature Authenticode. Le manifeste de mise à jour du launcher est signé ECDSA P-256 avec la clé de production existante ; les signatures des flux du jeu et des addons restent un travail distinct.

Les empreintes exactes et les contrôles sont consignés dans [release-candidate.json](release-candidate.json). Le manifeste et les fichiers de version précédents sont conservés pour permettre un retour contrôlé.
