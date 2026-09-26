# Sélection des jeux Atlas — première étape locale

Le sélecteur de la barre supérieure, entre **Atlas Launcher** et l'onglet **Jeu**,
propose **WOTLK Server** et **Minecraft**.
WotLK reste le choix initial à chaque ouverture. Le changement de sélection
ouvre l'accueil du jeu choisi sans recréer la session Atlas ni les états des
opérations WotLK. Cette première étape ne mémorise pas encore le dernier jeu.

Minecraft dispose d'un accueil indépendant, explicitement « Bientôt disponible ».
Aucun téléchargement, lancement, compte Microsoft ou serveur Minecraft n'est
configuré par cette modification. Addons, Boutique et le portefeuille WotLK sont
masqués dans cet espace. Les notes du launcher, les messages, les amis, le compte
et les paramètres existants restent communs. Les paramètres du jeu existants
concernent toujours WotLK ; les paramètres Minecraft viendront avec son intégration.

Un lien vers une page propre à WotLK, depuis une activité ou un profil par
exemple, remet également le sélecteur sur WOTLK Server. Les fenêtres modales
d'authentification et de recadrage bloquent le changement d'espace. Le sélecteur
est utilisable au clavier et ne déclenche pas le déplacement de la fenêtre.

La fenêtre conserve ses dimensions fixes existantes et reste non redimensionnable.
L'espacement de la barre accueille le sélecteur sans chevauchement. La version
locale garde son badge LOCAL et ses réglages séparés.

La barre conserve le gabarit précédant l'ajout du sélecteur : hauteur 80,
marges extérieures 22, logo et avatar 42, nom Atlas 20 et navigation 16
(unités WPF). Seuls les espaces horizontaux sont resserrés pour accueillir
le sélecteur et les indicateurs d'opérations dans la même fenêtre fixe.

Le sélecteur utilise un libellé sans cadre, un accent discret et un chevron
animé. Son menu apparaît par fondu et léger déplacement. Le passage entre jeux
superpose temporairement une capture de l'ancien espace au nouveau pour un
fondu de 320 ms, sans retarder la sélection ni bloquer les clics. Les panneaux
du compte et la barre ne sont pas capturés. Une nouvelle navigation interrompt
proprement l'animation et libère l'image ; les changements rapides suivent le
dernier choix. L'armurerie WebView2 utilise un fondu d'entrée. La transition
d'espace respecte la désactivation des animations Windows.

Les crédits Atlas et le portefeuille conservent leur présentation originale
côte à côte dans la fenêtre fixe, sans la limite de largeur du mode compact.

## Vérification

`--service-navigation-wpf <dossier>` utilise le shell WPF réel avec des données
synthétiques, hors écran, sans activation, session réelle ou serveur. Le contrôle
couvre le changement d'espace, le retour à WotLK, les fenêtres modales, les liens
contextuels, la continuité du compte et les zones cliquables de la barre. Les
dimensions contrôlées sont celles de la fenêtre fixe existante (1597,6×996,8
unités WPF), en français et en anglais. Les captures sont écrites dans le dossier
fourni, hors Git.

Cette évolution est préparée dans le client local ; la release publique 1.8.2
et l'installation sous Program Files ne sont pas remplacées.
