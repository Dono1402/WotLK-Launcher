# Visuels de la boutique Atlas

Ressources fournies par le propriétaire le 10 septembre 2026 dans
`Atlas_Boutique_Assets_Pour_Codex.zip`.

SHA-256 de l'archive :
`1e09e9baaa72709b098ec72c69bb1959a9e8d2df343b27a1057118f34eb3eb42`.

Les sept PNG sont copiés sans modification depuis `02_Illustrations` et
`03_Decor`. Les textes, badges, cadres, prix, listes, boutons et récapitulatifs
sont des contrôles WPF. La capture complète de référence n'est pas embarquée
comme écran ou comme fond.

| Fichier | Origine dans le pack | Taille native |
| --- | --- | --- |
| `Service_nom_sans_badge_recompose.png` | Illustration extraite ; ancien badge retiré dans le pack | 276 × 149 |
| `Service_personnalisation.png` | Illustration extraite | 251 × 147 |
| `Service_a_venir.png` | Illustration extraite | 272 × 147 |
| `Conversion_or_transparent.png` | Pièces détourées dans le pack | 190 × 105 |
| `Logo_Atlas_transparent.png` | Logo détouré dans le pack | 57 × 57 |
| `Fond_boutique_recompose_1586x992.png` | Fond de secours reconstitué dans le pack | 1586 × 992 |
| `Citadelle_fragment_original.png` | Décor original visible, rectangle (637, 97)–(1170, 306) | 533 × 209 |

Le fond complet sans interface n'est pas présent dans l'archive. Le fond de
secours est combiné au fragment original à sa position de référence, avec un
masque WPF pour fondre les bords. Il conserve une différence de texture avec
l'image de référence. La police exacte de cette image n'est pas identifiée par
le pack ; l'interface utilise Inter, déjà embarquée dans le launcher.

La barre supérieure utilise désormais le logo commun du launcher, comme les
autres pages. Le logo du pack reste conservé parmi les ressources fournies.
La petite pièce d'or du convertisseur est dessinée dans
`UI/V2/Resources/AtlasV2.Shop.xaml` (`ShopGoldCoin`) : cercles, relief et
motif vectoriels WPF, réutilisés par l'animation de transfert.

## Grandes cartes de services du 11 septembre

Quatre illustrations de 1672 × 941 sont utilisées dans la nouvelle grille :
`Service_name_change.png`, `Service_level_70.png`,
`Service_faction_change.png` et `Service_race_change.png`.
Le nom et la faction reprennent les compositions du pack en les redessinant ;
le sésame et la race sont des créations. Les originaux restent conservés.
Les modes, références, prompts et empreintes figurent dans
[GENERATED-ART.md](GENERATED-ART.md).

Le raccourci compact de conversion, désormais placé au-dessus des services,
réutilise `Conversion_or_transparent.png` sans modification. La grille reprend
les quatre illustrations tout en utilisant les surfaces bleu nuit, les bordures
et les arrondis WPF communs au launcher.

## Logos des moyens de paiement du 11 septembre

Les fichiers `Payment_visa.png`, `Payment_mastercard.png`, `Payment_paypal.png`
et `Payment_bancontact.png` proviennent de la [banque de logos du prestataire
Buckaroo](https://github.com/buckaroo-it/Media). Les PNG de 96 × 72 pixels sont
intégrés sans modification, avec leurs couleurs et proportions, et affichés
à 64 × 48 DIPs. Visa et Mastercard partagent la carte « Carte bancaire ».
Aucun téléchargement distant de logo ne se produit dans le launcher.

Sources des fichiers :

- [Visa](https://github.com/buckaroo-it/Media/blob/main/Creditcard%20issuers/PNG/VISA.png)
- [Mastercard](https://github.com/buckaroo-it/Media/blob/main/Creditcard%20issuers/PNG/Mastercard.png)
- [PayPal](https://github.com/buckaroo-it/Media/blob/main/Payment%20methods/PNG/PayPal.png)
- [Bancontact](https://github.com/buckaroo-it/Media/blob/main/Payment%20methods/PNG/Bancontact.png)

Ces marques appartiennent à leurs titulaires ; elles ne sont pas des créations
Atlas et ne relèvent pas de la licence du code. Le dépôt fournisseur décrit
leur usage pour ses marchands et partenaires. Leur présence dans cette
prévisualisation identifie les moyens envisagés et n'active aucun paiement.
