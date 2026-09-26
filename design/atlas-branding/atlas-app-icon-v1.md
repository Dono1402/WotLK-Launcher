# Icône Atlas sans texte

Emblème argenté A avec orbite bleue, extrait du logo approuvé pour remplacer l'ancienne icône du launcher. Le texte ATLAS LAUNCHER et le fond ont été retirés avec l'outil ImageGen intégré (mode édition, fond transparent).

- Référence : `atlas-logo-approved-reference.png`.
- Master : `atlas-app-icon-v1.png`, PNG carré de 1254 × 1254 pixels avec alpha.
- Conversion Windows : `scripts/build-app-icons.ps1` produit un PNG de 512 pixels et un ICO contenant les tailles 16, 20, 24, 32, 40, 48, 64, 128 et 256 pixels.
- Les ressources du launcher et de l'installateur partagent cet emblème. Les références XAML existantes restent valides ; l'icône du tray provient de l'exécutable.
- L'intégration est destinée au client local. Ces sources serviront également à une future version publique, sans publier de mise à jour maintenant.

## Prompt utilisé

```text
Use case: background-extraction.
Asset type: Windows application icon master, square PNG with genuine transparent alpha.
Input image 1 is the EDIT TARGET: the approved Atlas logo.
Extract ONLY the upper emblem: the silver beveled angular capital A with its nested triangular inset, thin luminous blue elliptical orbit, and small blue glowing sphere at the upper right. Remove ALL wording below (ATLAS and LAUNCHER) and remove the entire navy textured background. Keep the exact approved emblem geometry, proportions, silver facets, blue hues, orbital angle, and sphere placement. Do not reinterpret or redesign this logo.
Center the extracted emblem on a square transparent canvas, scaling it to fill approximately 88 percent of the canvas width with balanced safe margins. Preserve the entire orbit and sphere, no clipping. Crisp clean edges, compact blue glow, clear metallic facets, suitable to downsample into small desktop application icons.
BACKGROUND MUST BE ACTUAL TRANSPARENCY with an alpha channel, including the empty interior between the A strokes and the orbit. No simulated checkerboard, no solid background, no background square or rounded tile, no added shapes, no letters or words underneath, no shadow panel, no watermark. Output one icon only.
```
