# Atlas Launcher 1.7.1 : correction de l’auto-update

Version publiée le 12 septembre 2026 : serveur à 10:26:03 UTC, puis
[GitHub v1.7.1](https://github.com/Dono1402/WotLK-Launcher/releases/tag/v1.7.1)
à 10:27:15 UTC. Les six fichiers GitHub correspondent aux tailles et empreintes
prévues. Le tag désigne le commit `6e8699948a898461e8fda69d74042221dcac3a98`.
Les preuves figurent dans `publication.json` et `github-publication.json`.

La transition 1.6.0 vers 1.7.0 pouvait échouer après un téléchargement valide.
Les journaux du 12 septembre 2026 montrent un helper accepté puis en attente de
fermeture du parent, tandis que le parent termine avec `ReplacementFailed`.
Le helper finit par abandonner avec `ParentDidNotExit`, avant tout remplacement.

Le nouveau contrôle introduit en 1.6.0 utilisait `Process.MainModule`, qui demande
`PROCESS_VM_READ` sous .NET 8. Le launcher non élevé ne peut pas toujours obtenir
ces droits sur son helper administrateur. La 1.7.1 utilise
`QueryFullProcessImageNameW` avec `PROCESS_QUERY_LIMITED_INFORMATION`, tout en
conservant la comparaison exacte du chemin et les validations de la transaction.

La mise à niveau précédente vers 1.6.0 utilisait encore le mécanisme de la 1.5.0.
Sa réussite ne validait donc pas le nouveau trajet d’élévation livré en 1.6.0.

## Installation

Les versions 1.6.0 et 1.7.0 nécessitent le passage par `AtlasLauncherSetup.exe`
1.7.1 une fois : fermer le launcher, exécuter l’installateur et conserver le
dossier d’installation habituel. Aucune désinstallation préalable n’est requise.
Le défaut est dans le parent déjà installé ; un nouveau manifeste ou candidat
ne peut pas modifier ce code avant le transfert de contrôle.

## Vérification

- Test de régression avec un processus enfant Windows jetable et masqué :
  refus réel de `VM_READ`, échec de l’ancien contrôle reproduit, réussite du
  contrôle corrigé. Mauvais chemin, ancienne date, PID absent et processus
  terminé refusés.
- Suites de remplacement atomique, récupération, runtime et manifeste signé
  réussies, puis relancées sur la compilation publique 1.7.1.
- Installation de l’artefact dans un dossier temporaire, octets du launcher,
  métadonnées, raccourcis de test et désinstallation de test vérifiés.
- Téléchargements HTTPS complets du client et de l’installateur vérifiés par
  SHA-256 ; manifeste public relu et validé depuis Windows avec le vérificateur
  réel du launcher. Le contrôle d’une version déjà identique retourne `NoUpdate`.
- Build public et build des tests sans avertissement ni erreur. Armurerie
  identique au paquet publié avec la 1.7.0.

Ces vérifications n’ouvrent pas le launcher installé ni le jeu et ne simulent
pas une installation interactive complète avec UAC. Le test de régression
reproduit directement le refus d’accès Windows responsable du contrôle erroné.

La distribution conserve les fichiers 1.7.0 immuables. Le script de livraison
ne contient aucun redémarrage de service ; il vérifie que les PID World,
Hermes, API et Auth restent identiques pendant la publication du client.

Références : [implémentation .NET 8](https://github.com/dotnet/runtime/blob/v8.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/ProcessManager.Win32.cs),
[QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew).
