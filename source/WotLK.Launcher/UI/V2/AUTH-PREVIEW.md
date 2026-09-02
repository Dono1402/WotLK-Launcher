# Atlas Launcher V2 - Authentification legacy et preview 02F.1

## Contrat legacy observé

- Connexion : nom d'utilisateur et mot de passe. Le contrat `LoginRequest` n'accepte pas l'adresse e-mail comme identifiant.
- Inscription : nom d'utilisateur, adresse e-mail, mot de passe et confirmation locale.
- Nom d'utilisateur : 3 à 20 caractères, uniquement lettres ASCII, chiffres ou underscore.
- Adresse e-mail : format d'adresse valide.
- Mot de passe d'inscription : 10 à 128 caractères.
- Tous les champs d'inscription sont obligatoires et la confirmation doit correspondre.
- Une inscription réussie crée immédiatement une session et connecte l'utilisateur.
- Une adresse e-mail non vérifiée ne bloque ni le téléchargement ni le jeu. Le legacy permet le renvoi depuis la page Compte, pas depuis l'overlay d'authentification.

Messages legacy conservés ou raccourcis sans détail serveur :

- `Renseigne ton nom d'utilisateur et ton mot de passe.`
- `Nom d'utilisateur ou mot de passe incorrect.` côté service, présenté comme `Identifiants incorrects.` dans le preview.
- `Tous les champs sont obligatoires.`
- `Les deux mots de passe ne correspondent pas.`
- `Adresse e-mail invalide.`
- `Le mot de passe doit contenir entre 10 et 128 caractères.`

Le legacy ne propose dans cet overlay ni fournisseur externe, ni mot de passe oublié, ni double authentification, ni conditions générales, ni case de mémorisation. Aucun de ces éléments n'est ajouté en 02F.1.

## Règle d'overlay

L'authentification est prioritaire. Son ouverture ferme le drawer Amis. Une tentative d'ouverture des amis pendant l'authentification est refusée. Le shell conserve ainsi un seul voile et un seul piège de focus.

## Activation Atlas d'un compte existant

Les scénarios `atlas-enrollment` et `atlas-enrollment-error` couvrent l'état dédié présenté après `AtlasProfileRequired`. Ils réutilisent le nom de compte saisi, demandent l'adresse e-mail et le mot de passe actuel, puis simulent respectivement le formulaire initial et un refus contrôlé. Le simple login ne crée jamais de profil Atlas.

```powershell
WotLK.Launcher.exe --ui-v2 --preview-auth=atlas-enrollment
WotLK.Launcher.exe --ui-v2 --preview-auth=atlas-enrollment-error
```

## Isolation du preview

`--ui-v2 --preview-auth[=<scenario>]` construit exclusivement des états de présentation fictifs. Il ne crée ni `LauncherRuntime`, ni `LauncherAuthService`, ni client HTTP, ni session, ni accès au client, ni timer, ni processus enfant.
