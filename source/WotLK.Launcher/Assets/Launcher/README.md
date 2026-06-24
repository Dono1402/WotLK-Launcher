# WotLK Launcher — Asset Pack

Ce pack contient des assets propres basés sur l'image de référence du launcher WotLK.

## Contenu

- `reference/launcher_reference_full.png` : image de référence complète à envoyer à Codex.
- `reference/codex_failed_example.png` : exemple du rendu qui ne correspondait pas assez, utile pour comparer.
- `textures/bg_dark_frost_1600x900.png` : fond dark frost sans interface.
- `textures/panel_overlay_1480x780.png` : overlay de grand panneau sombre avec bordure/glow.
- `decor/rune_watermark_top_right.png` : motif runique transparent à placer à droite.
- `decor/divider_frost.png` : séparateur décoratif transparent.
- `buttons/play_button_plate_normal.png` : plaque du bouton JOUER sans texte.
- `buttons/play_button_plate_hover.png` : état hover.
- `buttons/play_button_plate_disabled.png` : état disabled.
- `controls/input_frame_760x64.png` : cadre de champ sombre.
- `controls/small_button_frame_220x64.png` : cadre bouton secondaire.
- `controls/select_frame_360x64.png` : cadre ComboBox sombre.
- `controls/status_pill_empty.png` : badge status sans texte.
- `controls/progress_track_1200x18.png` et `progress_fill_1200x18.png` : barre de progression.
- `icons/*.svg` : icônes simples.
- `docs/palette.json` : palette couleurs.

## Conseil d'intégration WPF

Le plus fiable est de garder les contrôles en XAML pour conserver les bindings et l'accessibilité, puis d'utiliser ces images comme textures/fonds :

- `bg_dark_frost_1600x900.png` en ImageBrush sur la fenêtre.
- `panel_overlay_1480x780.png` comme fond du conteneur principal ou en Image derrière la grille.
- `rune_watermark_top_right.png` en Image avec `Opacity="0.35"`.
- `play_button_plate_*.png` comme fond du bouton principal via un ControlTemplate.
- Le texte `JOUER` doit rester un vrai TextBlock au-dessus du PNG, pas être intégré dans l'image.
- Les champs peuvent être faits en XAML pur ou avec les frames fournis.

## Important

Ces assets ne remplacent pas le XAML. Ils servent à empêcher Codex d'improviser le visuel. Le prompt doit lui demander de reproduire la composition de `launcher_reference_full.png` et d'utiliser ces assets comme base.
