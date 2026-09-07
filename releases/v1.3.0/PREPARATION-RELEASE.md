# Préparation de la version 1.3.0

État au 6 septembre 2026 : **notes complètes préparées ; nouveau client public non publié**. Le numéro demandé reste 1.3.0. Cette préparation éditoriale ne change ni le manifeste de mise à jour ni les fichiers déjà distribués.

## Contenu

- `PATCH-NOTES-DRAFT.md` : texte français complet, 9 rubriques et 50 points.
- `patch-note.draft.json` : même contenu structuré, avec `isDraft: true` et aucune date de publication inventée. Il ne doit pas être utilisé tel quel comme entrée publiée : le flux nécessite une vraie date `publishedAt`.
- `source/WotLK.Launcher/UI/V2/Presentation/LocalPatchNotesDraft.cs` : contenu identique dans le client local ; badge de brouillon, version cible 1.3.0 et mention Non publiée conservés.
- `source/WotLK.Launcher/UI/V2/Localization/LauncherLocalization.cs` : traduction anglaise des 50 points et des nouvelles rubriques.
- `patch-note.json` et `release.json` : archives de la distribution du 4 septembre, conservées sans modification.

Les notes réunissent les 27 points de la base publiée et les changements locaux récents, en regroupant les sujets liés et en retirant les formulations devenues obsolètes. La présence des amis dans le launcher est déjà prise en charge par l’API déployée le 6 septembre après accord ; elle est distincte de la présence en jeu.

## Points concrets avant publication du client

1. **Intégrer le profil et l’armurerie au paquet distribué.** L’attachement de l’armurerie dépend actuellement de `LauncherBuildFlavor.IsLocalClient`. Le paquet local référence Node et les fichiers du dépôt par leurs chemins absolus ; la lecture de l’armurerie utilise le dispositif SSH de développement. Ce fonctionnement n’est pas un paquet destiné aux joueurs. Une distribution doit fournir ses dépendances et un accès aux données adapté aux comptes des joueurs. Le patchnote réserve donc explicitement sa dernière rubrique à un aperçu local.
2. **Vérifier le parcours public Gérer mon profil.** Sans armurerie attachée, le shell dirige vers la section Profil de Compte, alors que la vue actuelle masque cette section et redirige vers Sécurité. Avant publication, intégrer l’éditeur de profil au parcours public ou rétablir le repli. C’est un écart vérifié dans les sources en préparation ; il ne décrit pas le binaire distribué le 4 septembre.
3. **Prévoir la mise à disposition pour les utilisateurs déjà en 1.3.0.** L’updater n’accepte qu’une version strictement supérieure à la version installée. Une nouvelle distribution portant le même numéro ne sera donc pas proposée automatiquement aux installations déjà en 1.3.0. Le choix de version demandé est conservé ; le mode de distribution devra tenir compte de cette contrainte.
4. **Valider le paquet public une fois préparé.** Les contrôles existants du client local ne prouvent pas le fonctionnement d’un installateur public sur un poste de joueur. Les statistiques d’armurerie décrivent le dernier relevé disponible, et la bannière reste enregistrée par compte sur l’appareil ; aucune synchronisation de bannière avec les amis n’est annoncée.

Les points 1 et 2 doivent être résolus avant de présenter le profil immersif comme une fonctionnalité disponible dans le client public et de retirer la mention aperçu local. Le présent travail prépare le texte et son affichage dans le brouillon ; il n’effectue pas ce portage.

## Éléments vérifiés

- Base publiée : `releases/v1.3.0/patch-note.json`, commit `3954661` ; binaire décrit par `release.json`, issu de `17178fc`.
- Refonte : `artifacts/atlas-visualpilot/all-pages/RAPPORT_TOUTES_PAGES.md`.
- Interactions : `artifacts/atlas-ux-fixes/RAPPORT_AUDIT_ET_CORRECTIONS.md`, `QA_FRIENDS.md` et `artifacts/atlas-focus-fixes/CORRECTIONS.md`.
- Fenêtre et texte : `artifacts/atlas-fixed-window/RESULTAT.md`, `artifacts/atlas-global-typography/RESULTAT.md`.
- Jeu, démarrage et présence : `artifacts/atlas-launcher-behavior/RESULTAT.md` et `API_DEPLOYMENT_RESULTAT.md`.
- Dernier masquage de la barre du profil : `artifacts/atlas-profile-immediate-hide/armory-integration.log` et `artifacts/atlas-profile-immediate-hide/armory-integration/armory-profile-title-bar.json`.
- Périmètre de l’armurerie : `App.xaml.cs`, `Runtime/LauncherRuntime.cs`, `Runtime/LauncherArmoryLocalHost.cs`, `scripts/build-local-client.ps1` et `prototypes/armory-3d/README.md`.
- Repli Profil : `UI/V2/LauncherShellV2.xaml.cs` et `UI/V2/Views/AccountViewV2.xaml.cs`.
- Comparaison des versions : `Runtime/LauncherSelfUpdateCoordinator.cs`.

Les anciens modes Image entière de bannière et le délai de masquage après sortie ne sont pas repris dans ce brouillon. Les limites non corrigées du prototype, notamment la reprise après un échec initial du manifeste et le fonctionnement sans WebGL, ne sont pas annoncées comme résolues.
