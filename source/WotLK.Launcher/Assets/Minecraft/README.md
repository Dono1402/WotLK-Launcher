# Décor Minecraft

Fichiers fournis par l’utilisateur dans `Atlas_Minecraft_Assets_Essentiels`, copiés sans modification :

- `Minecraft_Overworld.png` : décor seul, résolution native **1672 × 941**. Les personnages font partie du décor. Recadrage WPF proportionnel (`UniformToFill`), sans étirement.
- `Overworld_Cube_Separateur.png` : petit ornement transparent de la devise. Les mots de la devise restent des textes WPF ; le PNG complet de la devise n’est pas utilisé.

La vue réutilise `GameViewV2` et la barre du launcher. Les titres, la carte serveur et les boutons sont de vrais composants liés à leurs états de présentation. La palette émeraude est locale à Minecraft ; les valeurs par défaut WotLK sont conservées.

La carte identifie le serveur existant **Vanilla Survival / Java 26.2**, d’après sa configuration documentée dans le projet Minecraft. Cette identité est fournie par `MinecraftGameState`, pas par le décor. Le suivi Minecraft et son lancement ne sont pas encore raccordés au launcher : les compteurs restent indisponibles et Jouer désactivé. Seules les notes de version du launcher sont partagées avec le tableau de bord existant.

Consigne du pack : capture réelle à valider avant publication ou push. Aucun changement du serveur ni de l’installateur pour cette intégration visuelle.
