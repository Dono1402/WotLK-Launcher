# Recadrage et résolution des avatars — 7 septembre 2026

Deux défauts côté client expliquaient le cadrage excessif à l'import et le flou
des avatars dans les profils visités. La correction ne modifie pas l'API.

## Recadrage indépendant des métadonnées DPI

L'image fournie fait 3808 × 3840 pixels, avec un DPI embarqué de 25,4. WPF la
mesure donc en environ 14392 × 14513 unités d'affichage. Le recadreur passait
ses coordonnées de pixels à un `ImageBrush.Viewbox` absolu, qui attend ces unités
d'affichage : l'aperçu montrait un gros plan environ 3,78 fois trop grand.
L'export serveur utilisait déjà le vrai cadrage en pixels, créant également un
écart entre l'aperçu et le résultat envoyé.

Les quatre brosses utilisent désormais des coordonnées relatives à l'image.
Le recadreur s'ouvre au zoom minimum, avec le plus grand carré disponible. Pour
ce fichier, il couvre 3808 × 3808 pixels et ne retire que 16 pixels en haut et
en bas, avant le masque circulaire de l'avatar. La molette permet ensuite de
zoomer et de revenir à ce cadrage ; le nouveau bouton « Recentrer et dézoomer »
rétablit le cadrage initial. Le bouton suit l'état d'envoi et sa traduction
anglaise est disponible. La réduction des aperçus utilise un filtre de qualité.

L'image originale reste intacte. Le recadrage demeure carré avec un affichage
circulaire : les coins extérieurs au cercle sont masqués normalement.

## Variante adaptée aux profils visités

La liste d'amis charge une vignette 64 × 64. Le profil réutilisait cette vignette
dans un cercle de 160 pixels CSS, soit environ 200 pixels physiques à une mise
à l'échelle de 125 %. Il agrandissait donc une image trop petite.

Le profil conserve maintenant le descripteur de l'avatar et demande la variante
256 × 256 déjà fournie par l'API, via le cache existant. C'est 16 fois plus de
pixels que la vignette de la liste. Le propre profil utilisait déjà cette
variante. Aucun réenvoi de photo n'est nécessaire pour corriger ce défaut de
résolution sur les profils visités.

La vignette sert pendant le chargement ou si le téléchargement échoue. Une
nouvelle version d'avatar utilise sa propre entrée de cache. Les contrôles de
session, de compte et de version empêchent une réponse tardive pour un ancien
profil de remplacer la photo du profil affiché.

## Validation

La fixture `--avatar-crop-rendering` accepte l'image source par argument ; elle
ne l'intègre pas au dépôt. Elle compare les cadrages initiaux, déplacés et
réinitialisés sur les quatre aperçus, avec plusieurs DPI source et affichage,
y compris une rotation et des DPI asymétriques. Elle compare aussi l'export du
vrai processeur serveur, exécuté en mémoire sans service HTTP.

La suite `--friend-profile-wpf` traverse la projection réelle des données d'amis,
le cache et le message transmis à WebView. Elle vérifie les dimensions 256 × 256
et les détails des pixels, le repli pendant le chargement ou l'échec, le changement
de version, la suppression de photo et la navigation vers un autre compte alors
qu'un chargement est encore en cours. Les contrôles existants de navigation,
de profil personnel, de langue et de reconnexion sont conservés.

Les fixtures utilisent leurs propres fenêtres hors écran et des comptes
synthétiques. Elles ne pilotent ni le launcher ni le jeu de l'utilisateur.

Validation finale en Release : compilation sans avertissement ni erreur,
`--avatar-crop-rendering` réussi (576 comparaisons de rendu et 24 exports),
`--friend-profile-wpf` réussi, puis `--account-avatar-client` réussi avec ses
contrôles HTTP simulés, EXIF, cache, runtime et WPF à 120 DPI. La capture issue
de l'image fournie montre les deux personnages et des petits aperçus lissés.

L'oracle de rendu HighQuality conserve le bitmap complet et exprime le cadrage
attendu en unités WPF absolues correctement converties : l'écart mesuré est nul,
avec une tolérance de 0,01. Un second oracle découpe physiquement le bitmap et
conserve sa tolérance géométrique en filtrage bilinéaire. Cela sépare la géométrie
du cadrage des différences de filtre produites par les deux chemins de réduction
WPF. Les exports du processeur serveur sont également comparés aux aperçus.

L'empreinte SHA-256 du fichier original est inchangée :
`35087f4286afe25ccc87a8b4f1c7035e68e8a7d681295febc74a0679c94a4dcc`.
Les captures et les preuves restent dans `artifacts/avatar-crop-fix-20260907`
et `artifacts/verification/avatar-profile-quality-release`, hors Git.
