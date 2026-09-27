# Acceptation manuelle des quêtes — 27 septembre 2026

## Demande et état vérifié

L'utilisateur a demandé de désactiver l'acceptation automatique des quêtes.
La configuration de production est modifiée, sauvegardée et **activée après
redémarrage de World**, explicitement autorisé par l'utilisateur :
« Tu peux redémarrer le serveur de jeu. » Aucune commande GM n'a été nécessaire.
Le comportement dans le client graphique reste à confirmer par l'utilisateur.

Vérification de la préparation à **11:35:24 UTC / 13:35:24 Paris** :

- Une seule ligne modifiée : `Quests.IgnoreAutoAccept = 0` devient `1`.
- `Quests.IgnoreAutoComplete = 0` conservé ; la remise des quêtes est inchangée.
- Aucune écriture SQL, modification d'addon ou de progression des personnages.
- World, Auth et Hermes actifs, PID respectifs `839919`, `839912`, `840433`
  inchangés, `NRestarts=0`.
- Configuration toujours lisible par `acore`, propriétaire/groupe et mode
  conservés (`root`, GID `987`, mode `0640`).

## Activation autorisée et contrôles

- `systemctl restart arthas-worldserver` uniquement : arrêt demandé à 11:37:03 UTC,
  ancien processus terminé normalement, nouveau PID `1055358` démarré à 11:37:09.
- Le nouveau processus charge le fichier de production attendu ; aucun override
  d'environnement `AC_QUESTS_IGNORE_AUTO_ACCEPT` ou `AC_QUESTS_IGNORE_AUTO_COMPLETE`.
- Chargement des **9 464 définitions de quêtes** et initialisation du monde en
  **54 secondes**, puis message `worldserver-daemon ready`.
- Port World **4000** en écoute vérifié à 11:38:19 UTC ; reconnexion des bots en cours.
- À 11:38:54 UTC : processus actif, `NRestarts=0`, progression des bots à 940/1000
  dans le journal consulté. Un contrôle SQL un peu plus tard compte 996 personnages
  connectés au total.
- Contrôle final à **11:40:16 UTC** : même PID World, `NRestarts=0`, port 4000
  toujours ouvert, journal Playerbots arrivé à `1000/1000`. SQL compte 1 000
  personnages connectés au total, dont les 3 membres connectés de Puella Magi.
  L'empreinte de la configuration est toujours celle vérifiée après modification.
- Healthcheck public `https://animeclub.fr/wotlk/health` : HTTP 200, `status=ok`.
- Auth (`839912`) et Hermes (`840433`) n'ont pas été redémarrés ; MySQL tourne
  toujours depuis le 26 septembre à 05:51:52 UTC.

### Réserves observées, hors modification

Le journal de l'ancien processus signale un deadlock SQL `1213` à l'arrêt lors
de la sauvegarde du bot `Lenmishiw` (GUID `15487`). Aucune perte de données n'est
établie par ce message ; la réussite de cette transaction n'est pas démontrée ici.
Le nouveau processus ne présente pas de `1213` dans le contrôle à 11:38:54 UTC.

L'erreur déjà connue de doublon `pet_spell` réapparaît (`1062`, première occurrence :
familier `75657`, sort `53544`, transaction de 9 requêtes abandonnée ; 2 occurrences
au contrôle final à 11:40:16 UTC, toujours aucun `1213` après démarrage). Elle n'a pas été
modifiée, conformément au choix antérieur de laisser ce défaut en l'état.
Deux avertissements de chemins de déplacement d'Eye of Dar'Khan sont également
présents. Ces réserves n'empêchent pas le démarrage, mais interdisent de présenter
les journaux comme totalement exempts d'erreurs.

## Fichiers et sauvegarde privée

Chemin chargé par World :
`/opt/arthas-next/candidates/atlas-all-update-20260926/server/etc/worldserver.conf`.
Le lien `etc` résout vers `etc-production` ; c'est le fichier réel suivant qui
a été modifié :
`/opt/arthas-next/candidates/atlas-all-update-20260926/server/etc-production/worldserver.conf`.

Sauvegarde privée, vérifiée octet pour octet avant modification :
`/opt/arthas-next/backups/manual-quests-20260927/worldserver.conf.before`.
Son répertoire est protégé en `0700`. Ne pas placer la configuration complète
ou la sauvegarde dans Git : elles contiennent des paramètres privés.

- SHA-256 avant : `770dcd97377a2f421d302e7240830bb8c39705f09fddcf57d9f2b12aa9e85016`.
- SHA-256 après : `f071e950ba6488d13adc9e0d1da97a91f806ebe8ad8432cfa88c52b7aa27ad66`.

Le [script d'application](../scripts/wotlk-manual-quests-20260927/configure.py)
vérifie l'empreinte initiale, fait un essai à blanc du patch, sauvegarde le fichier,
puis vérifie que la seule différence est le réglage demandé. Il refuse une
réexécution ou une configuration différente : refaire l'audit dans ces cas.

## Autre procédure possible : activation sans redémarrage

La console standard, RA et SOAP sont désactivés. Aucun de ces accès n'a été activé.
Le compte administrateur de l'utilisateur permettrait d'exécuter en jeu, dans cet
ordre, lors d'une prochaine modification. **Inutile après le redémarrage effectué.**

```text
.reload config
.reload quest_template
```

Vérification du code du core actif : `HandleReloadConfigCommand` recharge la
configuration ; `HandleReloadQuestTemplateCommand` appelle `ObjectMgr::LoadQuests`.
Les constructeurs/chargements de `Quest` utilisent ensuite
`CONFIG_QUEST_IGNORE_AUTO_ACCEPT` pour retirer le marqueur normal d'auto-acceptation
et ne pas réappliquer celui de `SpecialFlags`.

**`.reload config` seul ne suffit pas**, car les marqueurs des définitions déjà
chargées restent en mémoire. Le `/reload` du client ne recharge pas le serveur.
Les messages attendus incluent `World config settings reloaded.` puis
``DB table `quest_template` (quest definitions) reloaded.``.

Après activation, fermer le dialogue du PNJ puis ouvrir une nouvelle quête éligible
sans l'accepter : elle doit rester absente du journal jusqu'au clic sur Accepter.
Ce test en jeu n'a pas encore été effectué. Ne pas abandonner une quête en cours
uniquement pour tester sans accord de l'utilisateur.

Une autre possibilité est un redémarrage contrôlé de World, **uniquement après
autorisation explicite de la coupure**. Auth, Hermes et MySQL n'ont pas besoin
d'être redémarrés pour ce réglage.

## Retour arrière et futures mises à jour

Pour rétablir le comportement précédent, remettre uniquement
`Quests.IgnoreAutoAccept = 0`, puis suivre la même activation. Ne pas écraser
d'éventuels changements ultérieurs avec la sauvegarde complète sans comparaison.
Conserver la valeur `1` lors des prochaines copies de configuration de production.

Le changement ne modifie pas les marqueurs des 179 définitions concernées en base,
ni les quêtes déjà acceptées. Il désactive leur prise automatique native côté
serveur ; un addon explicitement configuré pour envoyer Accepter reste distinct.
