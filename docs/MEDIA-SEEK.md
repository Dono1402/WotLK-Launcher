# Sauts dans la barre de lecture

Vérification locale du 9 septembre 2026, comparée au lecteur du commit `47ca58f`.

## Défaut reproduit et correction

Un clic dans la barre du lecteur produisait deux assignations successives à `currentTime` : une sur `input`, puis une sur `change`. Dans un vrai geste Edge, cela produisait deux événements `seeking`. Un glissement de vingt étapes provoquait vingt-deux assignations et recherches, relançant continuellement le décodage.

Le lecteur ignore maintenant les assignations identiques à un millième de seconde près. Pendant un glissement, la barre et le temps affiché suivent le pointeur ; le moteur reçoit seulement la destination finale au relâchement. Les commandes clavier s’appliquent immédiatement et leur destination survit à un relâchement ultérieur du pointeur.

Un lecteur en pause reste en pause. Si une lecture atteint naturellement sa fin pendant que la barre est maintenue, elle reprend à la destination choisie au relâchement. Quitter la portée du lecteur ou le détruire annule le geste en attente et ne peut pas relancer la lecture.

## Résultats et limites

- Clics successifs 28 → 52 → 35 secondes : deux recherches deviennent une par clic.
- Glissement de vingt étapes : vingt-deux recherches deviennent une au relâchement.
- Ces deux résultats sont reproduits avec des WAV/WebM de 70 secondes dans Edge et des gestes pointeur réels. Le comportement du clavier, de la pause, du relâchement hors barre, de la perte du focus et de la fin de lecture est aussi vérifié.

L’utilisateur a confirmé que le délai existe aussi dans le brouillon. Un contrôle supplémentaire utilise donc des fichiers physiques inspectés et copiés par `ChatAttachmentFileSource`, ouverts par `OpenPreviewAsync`, puis servis par `CreateLocalMedia` et le véritable pont WebView2. Les requêtes ne contactent aucun serveur. Les fenêtres synthétiques restent hors écran et sans activation ; les clics CDP ciblent uniquement leur document.

Le test natif utilise un MP3 et un MP4 H264/AAC silencieux de 70 secondes. Le son est activé dans le lecteur pour conserver le chemin de sortie audio ordinaire, mais le silence exact des pistes a été vérifié après décodage. Chaque format et chaque version possèdent une URL distincte pour éviter une réutilisation accidentelle du média précédent. La vidéo est vérifiée par ses dimensions décodées.

| Assignation de la position → événement `playing` natif | Avant | Après |
| --- | --- | --- |
| MP3 local, trois sauts | 3,3–4,1 ms | 3,2–3,7 ms |
| MP4 local, trois sauts | 4,1–6,0 ms | 3,3–3,6 ms |

Ces fichiers sont presque entièrement préparés au début du test. Les valeurs mesurent l’événement du moteur, pas le premier son physique. **Ces essais ne reproduisent pas tout le délai décrit par l’utilisateur et ne prouvent pas sa disparition sur ses fichiers.** La suppression des recherches redondantes est démontrée ; il reste à préciser le format concerné et le délai ressenti pour reproduire le reste du problème.

Un contrôle séparé de mise en tampon, avec réponses Range loopback ralenties, n’a montré aucun gain stable en passant simplement de `metadata` à `auto`. Le changement de préchargement n’a donc pas été ajouté à cette correction. Les buffers déjà reçus n’étaient pas effacés après la pause dans ce scénario. Les portions non encore reçues nécessitent toujours une lecture ou un transfert de données.

## Tests reproductibles

La suite `source/WotLK.Launcher.IntegrationTests/Frontend/chat-media-seek-tests.cjs` génère son WAV silencieux de 70 secondes en mémoire et utilise par défaut le petit WebM de test existant. Pour reproduire les positions absolues de 28, 52 et 35 secondes en vidéo, fournir aussi un WebM synthétique de 70 secondes :

```powershell
$env:ATLAS_CHAT_TEST_OUTPUT = 'artifacts/media-seek/gestures'
$env:ATLAS_CHAT_SEEK_VIDEO = '<chemin absolu du WebM synthetique de 70 secondes>'
node source/WotLK.Launcher.IntegrationTests/Frontend/chat-media-seek-tests.cjs
```

Pour le contrôle natif étendu, fournir un dossier contenant `seek-audio-70s.mp3` et `seek-video-audio-70s.mp4`, générés avec du silence et préalablement vérifiés au décodage. Le MP4 de cette mesure est en 320 × 180, 10 images/s, avec une image clé par seconde. Ces médias de test restent hors Git.

```powershell
$env:ATLAS_CHAT_SEEK_FIXTURES = '<dossier absolu des fixtures silencieuses>'
# Facultatif : comparer avec une copie de l'ancien chat-media.js.
$env:ATLAS_CHAT_SEEK_BASELINE = '<chemin absolu de la copie>'
dotnet source/WotLK.Launcher.IntegrationTests/bin/Debug/net8.0-windows10.0.17763.0/WotLK.Launcher.IntegrationTests.dll --chat-rich-host-wpf --capture-directory artifacts/media-seek/native
```

La mesure locale détaillée est conservée dans `artifacts/media-seek-20260909/native-draft-unique/native-media-seek.json`, et les gestes dans `gestures-long/media-seek.json`. Les rapports de mise en tampon et de sortie audio sont dans le sous-dossier `buffering`. Aucun de ces résultats générés n’est versionné.
