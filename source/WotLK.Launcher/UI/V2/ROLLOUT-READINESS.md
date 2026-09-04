# Atlas Launcher V2 - audit de readiness 04B.4

Date de l'audit : 2026-09-03

Branche : `ui/redesign-v2`

Baseline auditee : `1fbcfe4275388f8bddf91d449c7a9ab205989fcd`
Decision : **B - V2 pas encore prete a devenir l'interface par defaut.**

La parite fonctionnelle locale est proche et l'architecture ne presente pas de duplication d'autorite dangereuse. Le rollout public reste bloque par la chaine de confiance du self-update. Deux validations de production et plusieurs validations Windows/DPI restent egalement a effectuer avant 05A.

## Corrections 04B.4

- La page Jeu utilise un visuel immersif plein onglet avec `Mises a jour` et l'action principale alignees en bas a droite.
- Le retour vers Jeu replace le focus sur l'action principale.
- `Lire la note de mise a jour` ouvre maintenant un overlay WPF leger alimente par la vraie note du dashboard.
- L'overlay de note prend en charge fermeture X, Echap, clic exterieur, piege de focus, retour du focus et exclusivite avec les autres overlays.
- L'overlay ferme devient `Collapsed` et non hit-testable.
- Aucun nouvel endpoint, service, timer, stockage ou pipeline n'a ete ajoute.

## Inventaire V2

Legende : `REAL`, `PREVIEW ONLY`, `READ ONLY`, `A VENIR`, `LEGACY ONLY`, `NON APPLICABLE`.

| Zone | Fonction | Etat V2 | Observation |
|---|---|---:|---|
| Shell | Statut Atlas | REAL | Projection du dashboard reel, sans timer V2 supplementaire. |
| Shell | Centre Activite | REAL | Agrege les operations existantes, historique borne a 10. |
| Shell | Amis | REAL | Coordination reelle et rafraichissement unique de 15 s. |
| Shell | Parametres | REAL | Categories mixtes, detaillees plus bas. |
| Shell | Profil | REAL | Profil, acces Compte et deconnexion. |
| Shell | Fenetre personnalisee | REAL | Commandes implementees ; validation physique multi-DPI restante. |
| Jeu | Etat client | REAL | Etat local et connaissance de mise a jour restent distincts. |
| Jeu | Installer | REAL | Pipeline de maintenance existant. |
| Jeu | Mettre a jour | REAL | Meme pipeline et meme bail global. |
| Jeu | Jouer | REAL | Single-flight separe, SSO et lancement existants. |
| Jeu | Mises a jour | REAL | Ouvre la derniere note disponible dans le lecteur leger. |
| Jeu | Verifier | REAL | Verification rapide compatible legacy. |
| Jeu | Verifier et reparer | REAL | Verification complete puis pipeline de reparation unique. |
| Jeu | Dossier | REAL | `LauncherLocalActionCoordinator`. |
| Jeu | Diagnostic | REAL | Meme chemin local que Parametres. |
| Jeu | Progression | REAL | Snapshot runtime et Centre Activite. |
| Jeu | Derniere patch note | REAL | Overlay leger ; le backend fournit actuellement un resume, utilise comme corps. |
| Addons | Catalogue | REAL | Catalogue legacy reutilise. |
| Addons | Recherche | REAL | Locale, testee avec catalogue et UI virtualisee. |
| Addons | Filtres | REAL | Categorie et statut. |
| Addons | Installation | REAL | Bail global et pipeline legacy reutilise. |
| Addons | Mise a jour | REAL | Individuelle et coordonnee. |
| Addons | Traitement par lot | REAL | Execution serialisee, aucune file utilisateur implicite globale. |
| Addons | Reparation | REAL | Pipeline addons existant. |
| Addons | Suppression | REAL | Confirmation et traitement existants. |
| Addons | Jeu ouvert | REAL | Blocage/retour utilisateur conserves. |
| Compte | Profil | REAL | Projection de la session Atlas. |
| Compte | Avatar | REAL | Upload, crop, suppression et cache client ; smoke production restant. |
| Compte | E-mail | REAL | Affichage, changement et renvoi de verification. |
| Compte | Mot de passe | REAL | Changement authentifie. |
| Compte | Sessions | REAL | Liste et revocation individuelle. |
| Compte | Deconnexion | REAL | Single-flight, nettoyage transversal. |
| Compte | Double authentification | A VENIR | Desactivee ; aucun backend. |
| Compte | Recuperation de compte | A VENIR | Non exposee comme action active. |
| Compte | Deconnecter les autres appareils | A VENIR | Desactive ; aucun endpoint global. |
| Social | Liste d'amis | REAL | Donnees serveur, etats vide/erreur/charge. |
| Social | Demandes recues | REAL | Accepter/refuser. |
| Social | Demandes envoyees | REAL | Affichage et etat. |
| Social | Suppression | REAL | Mutation serveur existante. |
| Social | Presence | REAL | Projection des donnees serveur. |
| Social | Avatars | READ ONLY | Client pret ; delta serveur 03B.1 non deploye pour les vrais avatars sociaux. |
| Social | Rafraichissement 15 s | REAL | Un seul timer, seulement en session authentifiee. |
| Self-update | Recherche | REAL | Automatique et manuelle selon le reglage legacy. |
| Self-update | Telechargement | REAL | Bail global, progression et annulation cycle de vie. |
| Self-update | Activite | REAL | Visible dans le Centre Activite. |
| Self-update | Application atomique | REAL | Updater dedie et handoff. |
| Self-update | Rollback | REAL | Couvert par tests de remplacement atomique. |

## Audit Parametres

| Categorie | Controle | Etat | Comportement |
|---|---|---:|---|
| General | Langue de l'interface | A VENIR | Desactive et identifie comme indisponible. |
| General | Demarrer avec Windows | A VENIR | Desactive. |
| General | Action du bouton Fermer | A VENIR | Desactive. |
| General | Fermer apres lancement du jeu | REAL | Lit/ecrit le reglage legacy. |
| Jeu | Dossier d'installation | REAL | Lecture, selection et sauvegarde legacy. |
| Jeu | Ouvrir le dossier | REAL | Action locale partagee. |
| Jeu | Langue du jeu | REAL | Lit/ecrit la valeur existante. |
| Jeu | Texte de quete instantane | REAL | Modifie uniquement `instantQuestText` dans `Config.wtf`. |
| Jeu | Verifier et reparer | REAL | Reutilise `VerifyCommand`, navigue vers Jeu et montre la progression. |
| Mises a jour | Verifier maintenant | REAL | Commande du coordinateur self-update. |
| Mises a jour | Mise a jour automatique | READ ONLY | Le runtime respecte la valeur legacy stockee ; le controle V2 n'est pas activable. |
| Mises a jour | Canal de publication | A VENIR | Desactive. |
| Mises a jour | Comportement de mise a jour client | READ ONLY | Information seulement. |
| Notifications | Reglages | A VENIR | Controles desactives. |
| Apparence | Reglages | A VENIR | Controles desactives. |
| Diagnostic | Ouvrir les logs | REAL | Meme coordinateur que l'acces Jeu. |
| Diagnostic | Versions et etats | READ ONLY | Informations runtime. |
| Diagnostic | Copier le rapport | A VENIR | Desactive. |
| Diagnostic | Ouvrir le dossier du launcher | A VENIR | Desactive. |
| Diagnostic | Reinitialiser l'interface | A VENIR | Desactive. |

La barre de sauvegarde differee est masquee en V2 reelle et conservee uniquement dans les previews. Aucun switch visuellement actif n'est reste sans ecriture reelle.

## Autorites runtime

| Etat/responsabilite | Autorite unique | Projection/consommateurs |
|---|---|---|
| Composition et cycle de vie | `LauncherRuntime` | `LauncherShellV2` |
| Session et identite | `LauncherSessionCoordinator` | Profil, Compte, Amis, clients autorises |
| Bail de maintenance global | `LauncherOperationCoordinator` | Jeu, Addons, self-update, Activite |
| Etat et actions Jeu | `GameRuntimeCoordinator` | `GameStateAdapter`, `GameUiState` |
| Addons | `LauncherAddonsCoordinator` | `AddonsStateAdapter`, Activite |
| Activite | `LauncherActivityCoordinator` | Projection agregee, pas une seconde source d'operations |
| Self-update | `LauncherSelfUpdateCoordinator` | Top bar, Activite, Parametres |
| Compte/avatar/sessions | `LauncherAccountCoordinator` | `AccountStateAdapter` |
| Amis/presence | `LauncherFriendsCoordinator` | `FriendsStateAdapter` |
| Parametres | `LauncherSettingsCoordinator` | `SettingsStateAdapter` |
| Royaume/patch note | `LauncherDashboardCoordinator` | `DashboardStateAdapter` |

Aucune duplication d'autorite dangereuse n'a ete detectee. Les `UiState` et adapters restent des projections. Le Centre Activite ne telecharge rien et ne possede aucune seconde annulation.

## Matrice de concurrence

- `TryBegin` refuse immediatement en cas de conflit ; aucune commande utilisateur n'est mise en attente.
- Un seul bail de maintenance peut etre actif.
- `Play` utilise un verrou single-flight distinct.
- `Play` peut coexister avec `Verify` uniquement si le client local est jouable.
- `Play` ne coexiste pas avec Install, Update, Repair, mutation Addon, batch Addon ou self-update.
- `Verify` ne coexiste avec aucune operation mutante.
- Install, Update, Repair, Addons et self-update sont mutuellement exclusifs.
- Annulation utilisateur et annulation de fermeture sont distinctes.
- Un bail ou callback ancien ne peut pas terminer une operation plus recente : `OperationId` monotone et verification du bail courant.

Les suites couvrent succes, erreur, annulation, double annulation, callback obsolete, fermeture et nouvelle operation. L'historique Activite a ete alimente avec 50 resultats et reste borne aux 10 plus recents.

## Timers V2 reels

| Proprietaire | Cadence | Demarrage | Arret/dispose |
|---|---:|---|---|
| `LauncherSelfUpdateCoordinator` | 30 s | Composition V2 reelle selon le reglage legacy | `BeginShutdown` puis `Dispose` |
| `LauncherFriendsCoordinator` | 15 s | Session authentifiee | Logout, fermeture puis `Dispose` |

Il n'existe pas de timer periodique V2 pour Dashboard, Realm, Avatar, Activite, Compte ou Addons. Les autres usages de `TimeProvider` concernent le throttling de progression ou des horodatages, pas du polling periodique. Les timers legacy existent toujours mais les deux branches de demarrage sont exclusives.

## HTTP et session

- `LauncherAuthService` possede le client d'authentification.
- `LauncherRuntime` cree un client autorise partage pour manifeste/transfert Jeu, Addons, self-update et medias avatar.
- L'autorisation lit dynamiquement la session courante ; aucun token n'est stocke dans une vue.
- Compte, Amis et Dashboard passent par les services d'authentification existants.
- Les tokens d'annulation de cycle de vie sont propages aux operations reseau.
- Les previews ne construisent ni `LauncherRuntime`, ni client HTTP, ni timer, ni picker, ni processus, et n'ecrivent aucune donnee metier.

## Lifecycle, session et navigation

Automatise et vert :

- composition unique Legacy/V2/Preview ;
- restauration absente, valide, expiree, refusee et tardive apres fermeture ;
- aucune publication WPF apres desinscription/disposal ;
- fermeture pendant verification, maintenance Jeu, Addons, compte et self-update via les suites specialisees ;
- logout single-flight, refuse pendant les operations incompatibles et nettoyage transversal apres succes ;
- invalidation centrale sur `401` pour Compte et Amis ;
- 100 changements de page Jeu/Addons/Parametres/Compte sans recreation des `UiState` ;
- exclusivite des overlays Auth, Activity, Friends, Profile, AvatarCrop et PatchNote ;
- fermeture et retour du focus testes pour les overlays modifies.

La separation compte A/compte B repose sur la session centrale, l'invalidation des projections au logout/401 et le cache avatar segmente par compte. Les tests couvrent ces primitives, mais un scenario end-to-end production A vers B reste une validation manuelle de rollout.

## Responsive, DPI et performance

Validation automatisee :

- session Windows reellement observee a 120 DPI / 125 % ;
- 1080 x 680 : Jeu, Addons, Parametres et Compte sans debordement horizontal ;
- bouton principal Jeu et commandes de fenetre dans les limites ;
- 1440 x 860 et 1920 x 1080 : overlays, drawers et contenu centre ;
- largeur maximale Jeu a 1920 x 1080 ;
- Addons 50 entrees et Amis 100 entrees dans les harnais WPF ;
- demarrage preview environ 0,5 s ;
- 100 navigations environ 35 a 50 ms dans le harnais ;
- ouverture Activite environ 3 a 4 ms ;
- memoire managee observee environ 19 a 20 Mio.

Le manifeste ne declare pas explicitement PerMonitorV2. Le processus observe reste `PROCESS_SYSTEM_DPI_AWARE`, volontairement conserve dans ce checkpoint.

**Validation DPI reelle restante a effectuer manuellement** a 100 % et 150 % dans des sessions Windows reellement ouvertes a ces echelles. Restent aussi a verifier physiquement, en fenetre normale et maximisee : drag, double-clic, minimiser, maximiser/restaurer, fermer, resize, hit-tests, Tab/Shift+Tab/Enter/Espace et nettete des fontes/icones. Aucun zoom, `LayoutTransform` ou etirement bitmap n'a ete presente comme preuve DPI.

## Parite Legacy / V2

| Fonction | Legacy | V2 | Parite | Difference intentionnelle | Bloquant rollout |
|---|---:|---:|---:|---|---:|
| Demarrage/auth/session | Oui | Oui | Oui | Composition V2 centralisee | Non |
| Etat/installation/update Jeu | Oui | Oui | Oui | Presentation par snapshots | Non |
| Jouer/SSO | Oui | Oui | Oui | Auth overlay V2 | Non |
| Verification/reparation | Oui | Oui | Oui | Progression V2 + Activite | Non |
| Addons | Oui | Oui | Oui | Catalogue V2 et batch | Non |
| Amis | Oui | Oui | Oui | Drawer V2 ; avatars serveur a deployer | Oui pour parite avatar |
| Profil/Compte | Partiel | Oui | Superieur | Architecture Compte V2 | Non |
| Parametres existants | Oui | Oui | Oui | Fonctions futures clairement desactivees | Non |
| Patch notes | Page/zone legacy | Overlay V2 | Oui | Pas de page Actualites dediee | Non |
| Centre Activite | Non | Oui | Superieur | Nouvelle fonction V2 | Non |
| Self-update | Oui | Oui | Fonctionnelle | Remplacement atomique V2 | **Oui, securite transport** |
| 2FA/recuperation globale | Non | A venir | Identique | Aucun backend invente | Non |
| Demarrage par defaut | Legacy | `--ui-v2` | Intentionnel | Bascule reservee a 05A | Non |

Le lancement sans argument reste strictement legacy. `--ui-v2` reste requis pour la V2 reelle. Aucun changement de `OnStartup` vers la V2 par defaut n'est inclus.

## Base et serveur

### Migration 0004

`0004_atlas_profile_identity_boundary.sql` reste non deployee. Elle retargete les cles etrangeres Atlas (sessions, verification e-mail, amities, avatars) vers `atlas_launcher_profile`, sans supprimer ni convertir les comptes AzerothCore.

La V2 actuelle ne depend pas de 0004 et reste compatible avec la production 0001-0003 grace aux gardes applicatives de profil Atlas. Son deploiement peut donc etre reporte. Elle reste utile comme defense d'integrite en profondeur, a appliquer plus tard avec sauvegarde et validation MySQL dediee. Aucun deploiement n'a ete effectue pendant 04B.4.

### Delta social 03B.1

Le client V2 contient deja la presentation des avatars sociaux. Le serveur de production doit encore recevoir le delta de `03B.1` dans :

- `WotLK.Launcher.Server/FriendDatabase.cs` : jointures avatar et chargement groupe des personnages, supprimant le N+1 ;
- `WotLK.Launcher.Server/FriendModels.cs` : `AvatarDescriptor` optionnel.

Ce delta est compatible avec la production 0001-0003 si les tables avatar 0002/0003 sont presentes. Il peut etre deploye sans 0004. Aucun serveur n'a ete modifie ou deploye pendant cet audit.

## Manifeste self-update

Le manifeste self-update est encore charge depuis :

`http://152.228.225.7/launcher/launcher-update.json`

Le SHA-256 du binaire provient du meme manifeste HTTP non signe. Un attaquant en position d'interception pourrait donc remplacer simultanement le binaire et son empreinte. Le remplacement atomique et le rollback protegent contre un echec local, pas contre cette substitution.

Classification : **RED, bloquant pour une publication publique et pour le passage V2 par defaut tant que l'auto-update peut appliquer ce contenu.**

Recommandation :

1. distribuer le manifeste et les artefacts sur un nom HTTPS stable et refuser tout downgrade HTTP ;
2. signer le manifeste et verifier la signature avec une cle publique embarquee dans le launcher ;
3. jusqu'a cette chaine de confiance, desactiver l'application automatique et distribuer uniquement une release manuelle provenant d'un canal de confiance.

Le manifeste Jeu par defaut du launcher est en HTTPS, mais l'installeur contient encore une valeur HTTP historique pour le manifeste Jeu. Ce point doit etre aligne avant une nouvelle diffusion de l'installeur.

## Resultats de validation

### Builds

Les quatre projets compilent en `Release` avec le SDK explicite `C:\Users\Dono\.dotnet\sdk-8.0.424\dotnet.exe` :

- `WotLK.Launcher` ;
- `WotLK.Launcher.Installer` ;
- `WotLK.Launcher.Server` ;
- `WotLK.Launcher.IntegrationTests`.

Resultat observe : 0 erreur, 0 avertissement.

### Suites locales executees

29 suites fonctionnelles et 7 suites WPF/visuelles sont vertes, dont : caracterisation legacy, composition/runtime, verification, coordinateur global, maintenance Jeu, reparation, dashboard, auth, lancement, logout, parametres, compte/avatar/sessions, amis, addons, activite, self-update atomique/runtime, backend avatar sans MySQL et le nouveau `--v2-rollout-audit`.

Les tests externes suivants n'ont pas ete annonces comme verts dans cet audit :

- `--avatar-migrations-mysql`, `--avatar-backend-mysql`, `--atlas-identity-mysql` : base MySQL jetable non configuree dans cette session ;
- tests live auth/ticket/e-mail/reseau : volontairement non lances contre la production ;
- `--local-shell-windows-smoke` : interactif et ouvre Explorer, non lance conformement au choix de ne pas controler le PC.

## Matrice GREEN / YELLOW / RED

### GREEN - pret

- Quatre builds Release sans erreur ni avertissement.
- 36 suites locales fonctionnelles/WPF vertes.
- Legacy sans argument conserve et tests de caracterisation verts.
- V2 reelle toujours derriere `--ui-v2` ; previews isolees sans effet de bord.
- Autorites runtime, operation ids, annulations et compatibilites coherentes.
- Navigation longue, responsive minimal, overlays et focus testes automatiquement.
- Le bouton Mises a jour de la page Jeu et le lecteur de patch note ne sont pas decoratifs.
- Aucun bouton actif sans action reelle trouve apres correction.

### YELLOW - validations manuelles ou de production

- DPI reel 100 % et 150 %, chrome Windows et clavier a revalider dans des sessions adaptees.
- Smoke avatar reel sur la production deja deployee.
- Scenario compte A vers compte B contre le serveur reel.
- Suites MySQL jetables a rejouer lorsque leurs variables de connexion sont disponibles.
- Test destructif controle de l'updater sous UAC/`Program Files` avec un package signe de test.
- Installation/update/lancement Jeu et mutations Addons contre la production a revalider avant rollout.
- Migration 0004 reportee, non bloquante pour la V2 actuelle.

### RED - bloque 05A/publication

1. Manifeste self-update et empreinte distribues par HTTP non signe : chaine de confiance insuffisante.
2. Delta serveur social 03B.1 non deploye : les vrais avatars sociaux ne sont pas encore disponibles en production.
3. Handoff updater et remplacement reel sous UAC/`Program Files` non valides manuellement.

## Decision et gates 05A

**Decision B : ne pas rendre la V2 par defaut maintenant.**

Avant 05A :

1. securiser la chaine de distribution self-update ;
2. deployer puis smocker le delta social 03B.1 sans 0004 ;
3. effectuer le smoke avatar production ;
4. valider l'updater reel sous UAC avec rollback ;
5. valider DPI 100/125/150 %, chrome et clavier sur Windows ;
6. executer une recette production courte Jeu/Addons/Auth/Amis ;
7. conserver un rollback explicite vers le lancement legacy.

04B.4 ne bascule pas le demarrage, ne supprime pas la legacy, ne pousse rien et ne deploie rien.
