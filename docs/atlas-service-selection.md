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
