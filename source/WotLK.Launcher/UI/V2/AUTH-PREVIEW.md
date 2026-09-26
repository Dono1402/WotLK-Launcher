# Atlas Launcher V2 - Authentification legacy et preview 02F.1

## Formulaires locaux du 26 septembre 2026

La version locale conserve le décor Atlas approuvé et la description « Tes jeux,
tes services, un seul compte Atlas. ». Elle ajoute l’affichage/masquage des trois
champs de mot de passe, des erreurs sous les champs à la sortie du focus, des
libellés plus lisibles et une confirmation après création du compte. Aucun
message d’erreur n’apparaît pour un champ vide ou effacé : les champs obligatoires
gardent simplement l’envoi désactivé. Les erreurs de format et de confirmation
concernent uniquement les valeurs déjà saisies. Aucun
indicateur Verr. Maj. n’est ajouté. Les valeurs révélées sont synchronisées puis
effacées lors du masquage, de l’envoi, du changement de formulaire ou de la fermeture.
Les modèles de présentation ne contiennent que les messages de validation.

« Mot de passe oublié ? » ouvre un formulaire e-mail. Il appelle la nouvelle
route `POST /api/v1/auth/password-reset/request`. Une API ancienne, un service
d’e-mail indisponible ou une limitation de débit affiche une erreur explicite,
jamais une fausse confirmation d’envoi. Le message accepté reste identique pour
une adresse connue ou inconnue. Le lien reçu ouvre la page de réinitialisation,
puis l’utilisateur revient se connecter dans le launcher.

Cette récupération nécessite la future version serveur, Brevo configuré hors
sandbox, une URL publique HTTPS et le schéma 0015. Le build local ne déploie pas
le serveur et ne modifie ni le schéma ni l’installation publique.

Vérifications ciblées : `--auth-shell-wpf` rend les formulaires FR/EN en mémoire
sans ouvrir de fenêtre ; `--auth-runtime --headless` vérifie le raccordement
d’authentification ; `--password-recovery-mysql` exige une base jetable locale
préfixée `atlas_auth_session_test_` dans `ATLAS_AUTH_SESSION_TEST_DB` et simule
les transports e-mail/Hermes. Aucun e-mail réel n’est envoyé par cette suite.

Les sections suivantes décrivent le contrat historique initial.

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

## Isolation du preview

`--ui-v2 --preview-auth[=<scenario>]` construit exclusivement des états de présentation fictifs. Il ne crée ni `LauncherRuntime`, ni `LauncherAuthService`, ni client HTTP, ni session, ni accès au client, ni timer, ni processus enfant.
