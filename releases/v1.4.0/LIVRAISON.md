# Atlas Launcher 1.4.0 — version publiée

Publiée et vérifiée le 6 septembre 2026. **Le client public, l’installateur, les notes et la nouvelle API sont en ligne.** La publication s’est terminée à 18:38:28 UTC ; le manifeste a été signé à 18:31:16 UTC. L’exemplaire local habituel est passé en `1.4.0-local` ; sa configuration a été conservée, sans fermer ni ouvrir le launcher de l’utilisateur.

## Résultat

Le client public embarque le profil et l’armurerie, avec Node, le moteur d’export, les ressources du viewer et le programme Microsoft WebView2 nécessaire si ce composant est absent ou trop ancien. Le moteur lit les fichiers du jeu choisi dans les paramètres ; il ne dépend pas du dépôt de développement, de Codex, de Playwright ou de SSH sur le poste du joueur.

Les personnages proviennent de nouvelles routes API authentifiées et limitées au compte connecté. L’hôte C# garde les jetons et transmet seulement les données autorisées au processus de rendu local. La déconnexion et les réponses tardives après une reconnexion sont contrôlées. L’absence de GPU ou de fichiers du jeu laisse le profil, l’équipement et les données disponibles consultables.

Les notes comprennent **53 points dans 10 rubriques**, en [français](PATCH-NOTES.md) et en [anglais](PATCH-NOTES.en.md). Les archives 1.3.0 sont conservées.

## Fichiers

| Fichier | Taille | Version |
| --- | ---: | --- |
| [Client public](../../artifacts/atlas-release-140/public-client/WotLK-Launcher.exe) | 398 996 965 octets | 1.4.0 |
| [Installateur public](../../artifacts/atlas-release-140/public-setup/AtlasLauncherSetup.exe) | 504 368 217 octets | 1.4.0 |
| [Exemplaire local habituel](../../artifacts/AtlasLauncherLocal/AtlasLauncherLocal.exe) | 100 724 709 octets | 1.4.0-local |
| [Paquet API](../../artifacts/atlas-release-140/atlas-api-armory-1.4.0.tar.gz) | Voir manifeste | API armurerie |

Le composant WebView2 hors ligne explique une grande partie de la taille supplémentaire du client public. L’installateur public contient ce client et le moteur d’installation ; le contrôle d’espace disque tient compte de leurs tailles réelles.

Les empreintes SHA-256, les chemins et les résultats sont consignés dans [release-candidate.json](release-candidate.json), désormais marqué publié. Le [manifeste signé](launcher-update.json) est identique au manifeste HTTPS public et à `current/launcher-update.json`. L’ancien manifeste candidat non signé est conservé uniquement comme document de préparation. Le passage de 1.3.0 à 1.4.0 satisfait la comparaison stricte des versions ; le client de 399 Mo reste sous le plafond actuel de 1 Gio du mécanisme de mise à jour.

## Vérifications effectuées

- Compilation finale : zéro erreur et zéro avertissement.
- Paquet réel : 318 fichiers manifestés, tailles et SHA-256 vérifiés ; dépendances Node et frontend complètes, aucune configuration privée ou donnée de joueur embarquée.
- Cache initialement vide : export réel du personnage en 45,7 secondes, modèles, textures, animation et 13 infobulles complètes dans la fixture utilisée. Fichiers du jeu lus sans modification.
- Intégration WPF publique : ZIP réellement embarqué extrait et vérifié, processus Node empaqueté, données reçues du pont C#, rendu Three.js réel, 19 emplacements, polices Inter, édition du profil, français/anglais et déconnexion. Fenêtres de test hors écran et sans activation.
- Cas sans WebGL : profil éditable, équipement, statistiques et infobulles conservés, avec message d’aperçu indisponible et aucune erreur JavaScript.
- API et fichiers malformés : authentification, appartenance des personnages, plafonds de réponse, archives altérées, annulation et erreurs réseau vérifiés. Tests MySQL et HTTP exécutés sur des bases temporaires.
- Sessions : refresh refusé, réponses 401, reconnexion au même compte ou à un autre compte, annulation et réponses tardives couverts par des tests dédiés.
- Serveur réel, lecture seule : 1 personnage, 14 emplacements équipés, 1 capture native, 14 objets du catalogue et 7 requêtes SELECT validés avec les droits du service API. Historique du schéma inchangé à la version 5. Cette vérification a utilisé les méthodes du build serveur dans un harnais dédié, sans démarrer l’API candidate ni réutiliser un jeton utilisateur.
- Installateur : cycle d’installation et de désinstallation avec le payload 1.4.0 dans un dossier temporaire isolé, registre factice et raccourcis limités au dossier du test ; tailles, versions et empreintes vérifiées. Le launcher et l’installateur Microsoft WebView2 n’ont pas été exécutés sur le bureau de l’utilisateur.
- Notes françaises/anglaises et `git diff --check` validés.

Les logs, captures et contrôles détaillés se trouvent dans [artifacts/atlas-release-140](../../artifacts/atlas-release-140), notamment `armory-public-wpf-final.log`, `public-graphics-verification.json`, `package-content-verification.json`, `api-real-readonly-smoke.log` et `installer-artifact-tests.log`.

## Publication effectuée

- API sauvegardée, contrôlée sur un port séparé puis remplacée atomiquement après autorisation. Seul `wotlk-launcher-api.service` a redémarré : retour à une réponse saine en 1 255 ms. Les routes d’armurerie existent et refusent les requêtes anonymes ; les données réelles du compte ont été relues avec succès. Configuration, droits SQL et schéma conservés.
- Les deux exécutables versionnés ont été intégralement téléchargés par HTTPS et leurs tailles et SHA-256 comparés aux fichiers testés avant l’annonce de mise à jour. Les trois alias de téléchargement présents sur le serveur ont été actualisés ; les anciennes versions sont conservées.
- Manifeste signé avec la clé Atlas existante, publié en dernier et vérifié indépendamment depuis Windows avec le code C# exact du client et sa clé publique. Les 53 points ont rejoint le flux des notes ; les trois publications précédentes sont inchangées.
- Bannière du compte 101 copiée du stockage local de développement vers le launcher public, recadrage conservé et empreintes identiques. L’original reste intact. Le prochain lancement complet du launcher rechargera cette copie.

Une première vérification finale a rencontré des connexions IPv6 sans réponse depuis le serveur. La première tentative a restauré ses six fichiers mutables sans erreur ; la reprise a limité les requêtes de contrôle à IPv4, avec les mêmes URL et contrôles TLS. Aucune configuration réseau de production n’a été modifiée.

Les preuves sont dans [le résultat API](../../artifacts/atlas-release-140/deployment/api-deployment-result.json), [le résultat de publication](../../artifacts/atlas-release-140/deployment/client-publication-result.json), [la vérification C# du manifeste](../../artifacts/atlas-release-140/deployment/manifest-verification-1.4.0.json) et [le compte rendu détaillé](../../artifacts/atlas-release-140/deployment/CLIENT-PUBLICATION.md). Les sauvegardes de l’API et des fichiers publics sont conservées. **Le worldserver, Hermes et Caddy n’ont pas été redémarrés.**

La vérification Windows a utilisé le WebView2 déjà présent. L’installation conditionnelle d’un WebView2 absent a été testée avec des dépendances simulées et le programme Microsoft téléchargé a une signature valide ; aucun test sur une machine Windows entièrement vierge n’est revendiqué. Les statistiques natives dépendent des captures serveur disponibles, et la bannière reste stockée localement par compte. Le launcher de l’utilisateur était fermé lors du dernier contrôle : son affichage avec une session publique réelle reste à constater à sa prochaine ouverture ; les tests WPF isolés et les contrôles API décrits ci-dessus ont réussi.
