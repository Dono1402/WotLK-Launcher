# Préparation des notes de version 1.4.0

État au 6 septembre 2026 : **brouillon complet en français et en anglais ; publication publique non effectuée par ce travail éditorial.**

Le numéro cible est 1.4.0. Les notes reprennent les 50 entrées du brouillon complet précédent : 49 sont conservées mot pour mot et la restriction devenue obsolète à l’aperçu local est remplacée par l’intégration du profil et de l’armurerie au client public en préparation. Trois entrées décrivent les composants inclus, l’accès aux personnages du compte connecté et l’installation automatique du composant WebView2 nécessaire. Le résultat contient **53 points dans 10 rubriques**.

## Fichiers préparés

- `PATCH-NOTES-DRAFT.md` : notes françaises complètes.
- `PATCH-NOTES-DRAFT.en.md` : notes anglaises complètes.
- `patch-note.draft.json` et `patch-note.en.draft.json` : versions structurées correspondantes, identifiant `atlas-launcher-1-4-0`, version `1.4.0` et `isDraft: true`.
- `source/WotLK.Launcher/UI/V2/Presentation/LocalPatchNotesDraft.cs` : mêmes notes françaises dans le brouillon local du launcher, avec la mention Non publiée.
- `source/WotLK.Launcher/UI/V2/Localization/LauncherLocalization.cs` : traduction anglaise de l’introduction, des rubriques et des 53 points.

Aucune date `publishedAt`, URL de téléchargement, signature ni empreinte de binaire n’est inventée. Les deux fichiers JSON sont des brouillons éditoriaux ; leur statut et la date réelle devront être adaptés lors de leur publication dans le flux. Le manifeste de mise à jour signé doit être produit à partir des binaires finaux vérifiés.

## Périmètre des formulations

Les notes décrivent le client 1.4.0 en préparation, sans annoncer qu’il est déjà distribué. Les statistiques restent explicitement liées au dernier relevé serveur disponible, et les valeurs absentes sont signalées. Aucune disponibilité universelle de captures natives n’est annoncée. La bannière reste enregistrée localement par compte, sans promesse de synchronisation entre appareils ou avec les amis.

L’inclusion des composants de l’armurerie dispense d’installer manuellement des outils de développement. Elle n’annonce pas un fonctionnement hors connexion : les données des personnages proviennent du compte connecté, et le rendu 3D utilise les données du jeu configuré sur l’appareil.

## Vérification

La comparaison du contenu structuré avec les sources vérifie 53 points français, 53 traductions anglaises, 10 catégories et la conservation des 49 textes inchangés. Le contrôle est consigné dans `artifacts/atlas-release-140/notes-content-verification.json`. Les assertions de `LauncherDashboardTests.ProjectCategorizedPatchNotesWithLegacyFallback` vérifient la version cible, le statut brouillon, les rubriques, le nombre d’entrées, la couverture anglaise et la conservation des notes publiées.

Les archives `releases/v1.3.0` restent intactes, notamment le patchnote publié, les anciens brouillons et les manifestes de la distribution précédente. Aucun EXE habituel, fichier projet ou point d’entrée de compilation n’est modifié par cette préparation.

Les validations de l’API, du paquet public et du parcours de profil sont suivies séparément dans `artifacts/atlas-release-140`. Leur résultat final, le déploiement API et la publication du client relèvent de la préparation technique de la version, pas de ce brouillon éditorial.
