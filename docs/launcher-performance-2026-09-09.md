# Langue du jeu asynchrone et mesures du launcher — 9 septembre 2026

## Changement de langue du jeu

La vérification du client, le contrôle d'écriture et la lecture/écriture de
`Config.wtf` s'exécutent désormais hors du thread WPF. L'éventuelle demande de
droits conserve le parcours Windows existant ; l'attente du processus élevé
est asynchrone. Seule une boîte de dialogue éventuelle revient sur le dispatcher.

Le coordinateur garde le changement occupé jusqu'à la fin de l'application au
client. Cela empêche une écriture concurrente du dossier, de la langue ou du
texte instantané des quêtes. La fermeture attend également cette seconde étape.
Si le fichier du jeu ne peut pas être modifié après la sauvegarde de la
préférence, celle-ci reste enregistrée et les paramètres affichent l'erreur.

Le test raccorde le vrai applier à un `Config.wtf` temporaire : écriture bloquée
volontairement, dispatcher toujours disponible, contrôle désactivé pendant le
travail, autre écriture refusée, option personnalisée conservée, erreur disque
visible et refus de permission sans écriture. Deux cas de fermeture couvrent
une sauvegarde de préférence en cours et une application au client en cours.

## Protocole des mesures

- Compilation Release, .NET 8.0.30, WebView2 152.0.4191.66, Ryzen 9 9950X3D,
  16 cœurs / 32 processeurs logiques.
- Trois paires de processus neufs : un premier passage avec des dossiers de
  navigateur neufs, puis un passage réutilisant les dossiers de cette paire.
  Le passage pilote ne participe pas aux résultats.
- Shell WPF réel à dimensions fixes, hors écran, inactif et protégé contre
  l'activation. Snapshots et médias locaux de test ; aucun accès à un compte
  réel, service distant ou installation du jeu.
- Messages : une conversation, trois messages, avatars et une pièce jointe
  image. Profil personnel : trois personnages, dont un modèle local déjà
  préparé ; la disponibilité du modèle et plus de deux frames rendues sont
  vérifiées. Profils amis : deux identités et leur roster via le vrai helper
  RPC, sans génération ni modèle 3D dans cette seconde fixture.
- Vingt cycles Jeu → Addons → Notes de version → Paramètres → Messages →
  Profil personnel, puis vingt changements entre les deux profils amis.
- Chaque phase attend 1,5 seconde, puis collecte seize échantillons espacés
  de 500 ms. Aucun GC forcé, vidage de cache système ou trimming mémoire.
- Les processus comptés sont exclusivement le processus de test, les PIDs
  exposés par ses deux environnements WebView2 et son helper Node. Leur arrêt
  est contrôlé avant de valider chaque résultat.

« À froid » signifie ici **caches applicatifs de test neufs**. Le cache de
fichiers de Windows reste intact. Le démarrage inclut le bootstrap du processus
de test et l'initialisation du shell ; il ne mesure pas l'authentification,
l'extraction du paquet public ou une vérification réelle du client.

## Résultats

Les six passages sont validés. Les ensembles de processus sont restés stables
pendant les 42 phases CPU ; tous les enfants ont quitté après chaque fermeture.
Les durées ci-dessous sont des médianes, suivies de la plage min–max observée.
Trois essais par mode donnent une référence locale, pas un intervalle de
confiance statistique.

| Ouverture | Caches neufs, ms | Caches réutilisés, ms |
| --- | ---: | ---: |
| Processus → shell disposé | 795 (790–810) | 760 (760–768) |
| Première ouverture de Messages | 517 (510–555) | 505 (502–509) |
| Premier profil avec modèle 3D déjà préparé | 703 (702–716) | 583 (582–584) |
| Premier profil ami et roster, sans modèle 3D | 425 (421–425) | 410 (399–414) |

La seule construction/configuration/disposition du shell représente 648 ms
avec caches neufs et 611 ms avec caches réutilisés. Les ouvertures des pages
partent du clic et s'arrêtent au critère DOM prêt ; elles ne s'ajoutent pas
automatiquement au démarrage, puisque ces pages sont ouvertes à la demande.

Pour la mémoire et le CPU, chaque ligne donne la médiane des six moyennes de
phase. La mémoire comprend le shell, WebView2 et Node. « Résidente cumulée »
additionne les working sets, avec double comptage possible des pages partagées.
« Privée engagée » mesure les allocations privées, qui ne sont pas forcément
toutes résidentes en RAM.

| Phase | Processus | Privée engagée, Mio | Résidente cumulée, Mio | CPU machine | CPU équivalent 1 cœur |
| --- | ---: | ---: | ---: | ---: | ---: |
| Shell seul | 1 | 144 | 198 | 0,08 % | 2,70 % |
| Messages ouvert | 6 | 443 | 631 | 0,33 % | 10,68 % |
| Profil 3D ouvert, Messages déjà initialisé | 12 | 795 | 1 117 | 1,81 % | 57,80 % |
| Même profil après 20 cycles de navigation | 12 | 843 | 1 169 | 1,90 % | 60,86 % |
| Premier profil ami après ce parcours | 12 | 814 | 1 123 | 1,31 % | 41,98 % |
| Après 10 changements d'ami | 12 | 1 015 | 1 242 | 1,36 % | 43,60 % |
| Après 20 changements d'ami | 12 | 1 025 | 1 254 | 1,40 % | 44,68 % |

« Repos » signifie ici sans interaction pendant huit secondes, avec la page
ouverte et ses animations éventuelles. Cela ne mesure pas le mode minimisé
ni la consommation GPU ou la mémoire vidéo.

| Action répétée | Échantillons | Médiane | p95 | Maximum |
| --- | ---: | ---: | ---: | ---: |
| Navigation entre les cinq pages natives/Messages | 600 | 4,43 ms | 11,88 ms | 1 983 ms |
| Réouverture du profil personnel conservé | 120 | 8,18 ms | 10,23 ms | 49,51 ms |
| Changement de profil ami jusqu'au roster | 120 | 424,01 ms | 456,11 ms | 561,09 ms |

Le p95 utilise le rang supérieur de 95 % des échantillons triés. La navigation
native est chronométrée jusqu'au dispatcher disponible ; Messages ajoute son
critère DOM prêt. Ce n'est pas un temps de présentation de frame à l'écran.

Deux points ressortent pour un prochain profilage :

1. La navigation répétée devient rapide, mais les premières ouvertures ont
   parfois un coût élevé. Les dix relevés au-dessus de 100 ms concernent
   exclusivement le premier cycle : Addons jusqu'à 1,61 s, Notes de version
   jusqu'à 1,98 s et Paramètres jusqu'à 0,87 s. La cause précise n'est pas
   attribuée par ce harnais.
2. Les vues web et les changements de profils constituent le poste mémoire
   dominant. La médiane passe de 814 à 1 015 Mio pendant les dix premiers
   changements d'ami, puis à 1 025 Mio au vingtième. La croissance ralentit sur
   ce parcours ; les collectes et les caches varient entre les passages.
   Vingt changements ne suffisent ni à prouver une fuite, ni à prouver son
   absence à long terme.

## Vérification du changement livré

Compilation Release : zéro avertissement et zéro erreur. Suites
`--settings-runtime`, `--optimization-storage`, `--runtime-composition` et
`--runtime-hardening` réussies après la dernière modification. Le test des
paramètres couvre aussi la fermeture pendant l'application de la langue au
client, après que la préférence a déjà été sauvegardée.

## Reproduction et données brutes

Sous Windows avec le SDK .NET 8, un WebView2 déjà installé et les dépendances
locales de la fixture d'armurerie :

```powershell
./scripts/measure-launcher-performance.ps1 -DotnetPath dotnet -Pairs 3
```

Le chemin Node peut être fourni par `ATLAS_ARMORY_TEST_NODE`. La mesure 3D
nécessite `artifacts/armory-prototype/armory-current.json` et le snapshot de
modèle correspondant. En leur absence, le rapport indique
`hasRendered3DModel: false` ; ce parcours ne doit pas être comparé à la ligne
« profil 3D » de cette série.

Le script compile puis démarre un processus neuf pour chaque passage. Le dossier
de sortie doit être nouveau. Les profils de navigateur restent isolés dans ce
dossier et sont conservés pour permettre l'examen des résultats.

Les sorties de cette série se trouvent dans
`artifacts/launcher-performance-20260909/measured/runs.json` et dans les fichiers
`pair-1`, `pair-2`, `pair-3` / `cold.json`, `warm.json`. Elles restent hors Git.
Le harnais et ce rapport sont versionnés.

La mémoire privée désigne les octets privés engagés, additionnés entre les
processus. Ce n'est pas une mesure des seules pages actuellement en RAM. La
somme des working sets décrit la mémoire résidente cumulée mais compte plusieurs
fois les pages partagées ; elle ne représente donc pas la RAM physique unique.
Le CPU « machine » est normalisé sur les 32 processeurs logiques ; le rapport
JSON contient aussi le pourcentage équivalent d'un cœur logique.

Ces relevés établissent une référence de la version courante. Ils ne quantifient
pas un gain avant/après et ne prédisent pas les performances sur une autre
machine, en fenêtre active ou pendant la préparation de nouveaux modèles.

Le [suivi navigation et profils](launcher-navigation-profile-performance-2026-09-09.md)
présente les modifications suivantes, leur comparaison avant/après et les
limites encore observées dans le parcours avec l'armurerie 3D.
