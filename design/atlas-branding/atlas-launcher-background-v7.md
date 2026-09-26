# Atlas Launcher — fond multiservice, proposition 7

Visuel recomposé le 26 septembre 2026 avec l’outil de génération d’images intégré à Codex, à partir des cinq images fournies par l’utilisateur.

Fichier : `atlas-launcher-background-v7.png`.

Le décor et le logo reprennent la première image. Ses personnages sont remplacés par Homura Akemi, Mitsuha Miyamizu, Rem et Sinon, dans les versions visuelles fournies par l’utilisateur. Le prompt privilégie leurs visages, coiffures, tenues et détails propres aux références ; les poses, l’orientation et l’éclairage sont adaptés à la composition.

## Références, dans l’ordre transmis au générateur

1. `atlas-launcher-background-v1.png` : décor et logo uniquement. L’image jointe a été vérifiée identique à ce fichier.
2. `references/homura-recueillement.png` : Homura de profil, yeux fermés, ruban et nœud rouges.
3. `references/mitsuha-red-cord.png` : Mitsuha, uniforme scolaire, cordon et nœud rouges.
4. `references/rem-blue-violet.png` : Rem, visage incliné et lumière bleu-violet, adapté à une orientation verticale.
5. `references/sinon-ggo.png` : Sinon dans sa version GGO, cheveux turquoise et tenue verte avec écharpe blanche.

Les références sont conservées sans modification. Cette version est retenue pour les écrans de connexion et d’inscription du launcher. Elle est embarquée sans retouche dans `source/WotLK.Launcher/Assets/Launcher/visuals/atlas-auth-background.png` (chemin depuis la racine du dépôt). Le formulaire est placé sous le logo intégré au fond. Les versions précédentes sont conservées.

## Prompt de composition

```text
Use case: compositing.
Create one complete, polished Atlas Launcher background by combining the FIVE supplied images. Respect the exact roles of each input. Landscape approximately 16:10, retain image 1's canvas composition, at the highest available native resolution.

REFERENCE ROLES:
Image 1 = BASE BACKGROUND AND BRAND TO PRESERVE. Keep its atmospheric fantasy landscape: the luminous distant castle in the upper center/right, moon and celestial orbital arcs, floating islands, cliffside city, waterfalls, lake reflections, dark blue left side and warm sky on the right, foreground foliage/flowers. Keep the ORIGINAL silver beveled A orbital emblem and its exact ATLAS / LAUNCHER wordmark, preserving their design, shape, proportions, colors, typography, and upper-left position. REMOVE ALL FOUR of image 1's characters completely (Goku, Gojo, Luffy, Frieren). Reconstruct any background behind them as needed, consistent with the original landscape. Image 1 is NOT a character-design reference.
Image 2 = HOMURA AKEMI's precise appearance and drawing reference. Use THIS supplied version: long straight dark hair, the RED hair ribbon/headband and large red-outlined dark bow at the side of her hair, delicate original Madoka Magica face shape and linework, lowered/closed eyes and introspective composed expression, pale gray/lavender and white outfit with the distinctive collar and dark geometric details shown. Preserve her reference identity and red ribbon; do NOT replace them with the black-headband/generic Homura designs from unrelated images.
Image 3 = MITSUHA MIYAMIZU's precise appearance reference: the supplied Your Name girl with brown eyes, dark hair gathered with the red braided hair cord, loose cheek-framing strands, white short-sleeved collared school blouse, red neck bow, dark pleated skirt, and subtle thoughtful expression. Preserve this illustration's face, hair arrangement and clothing. Do NOT reproduce the Japanese title or any writing, outdoor tree or reference-image background.
Image 4 = REM's precise face, eye, hair and drawing reference. The reference is tilted; adapt/rotate her naturally upright for the finished ensemble. Preserve the very recognizable blue bob and asymmetric fringe, hair clips, floral maid headpiece, detailed blue eye and expressive original anime eye shape, crisp hair strands and blue-violet lighting accents. Retain her recognizable black-and-white maid outfit. Her appearance must come from THIS reference, not a generic blue-haired maid. Do not bring the reference's bokeh background or its tilted rectangular frame into the output.
Image 5 = SINON / SHINO ASADA's precise GGO appearance reference from Sword Art Online: aqua/teal hair with its pointed locks and longer face-framing strands, vivid turquoise eyes with this reference's facial geometry, crossed hairpins and angular dark side clips, white scarf/high collar with the square fastener, green-and-charcoal outfit and pale armor/jacket panels. Preserve this exact supplied Sinon design; do not substitute her schoolgirl or cat-ear/elf variants. Do not add a gun or other weapon.

CHARACTER IDENTITY IS THE MAIN PRIORITY. These are four specific illustrated versions supplied by the user, not loose inspiration. Preserve their recognizable faces, individual eye shapes, hairstyles, ribbons, costume details and original illustration identities. Do NOT average their faces into a single generic AI anime style. Harmonize lighting and scale to integrate them, while retaining the visual character of each supplied reference.

NEW COMPOSITION: exactly FOUR characters, Homura + Mitsuha + Rem + Sinon, grouped in the RIGHT 60%-62% of image 1, with comfortable space between every face. Make a natural, elegant layered ensemble: Homura toward the left/front of this group in a composed three-quarter/profile pose inspired by image 2; Mitsuha behind in the upper middle, with her red cord and neck bow readable; Sinon toward the upper/right in a natural three-quarter turn inspired by image 5; Rem in the right foreground, upright with her face clearly visible. Poses can be gently adapted for the common scene, with natural anatomy and hands, not four cropped rectangular portraits. Use upper-body to waist-up framing as appropriate, integrating the figures behind the existing foreground foliage. Keep the left 36%-38%, especially below the logo, dark, calm and free of people for a future login form. No character, hair or accessory over the logo. Do not place the characters on the left.

FINISH: rebuild the four figures freshly from these source references with crisp clean linework, faithful facial proportions, clear colors and finely resolved eyes/hair. Match soft cool moonlight and gentle warm rim light from image 1, without muddying the source drawings or losing detail. Keep image 1's scenic beauty and deep atmosphere, and avoid changing its architecture, logo or palette unnecessarily. No pasted edges, rectangular panels, ghosting, duplicate faces, distorted hands, fused hair, extra limbs or over-sharpening. No sexualized framing or exaggerated anatomy.

HARD REQUIREMENTS: exactly Homura, Mitsuha, Rem and Sinon. None of image 1's original four characters remain. First-version logo unchanged. No weapons, no invented accessories, no extra people, no text besides ATLAS and LAUNCHER, no Japanese title, watermark, UI controls or mockup frame. Deliver the full background artwork itself.
```
