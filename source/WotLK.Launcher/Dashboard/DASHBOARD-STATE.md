# Dashboard V2 : sources et état global

`LauncherDashboardCoordinator` est une projection de lecture seule de
`ILauncherAuthService`. Il réutilise `GetStatusAsync` et `GetNewsAsync`, donc les
endpoints, les DTO, la session et le client HTTP déjà possédés par
`LauncherAuthService`. Il ne crée ni client HTTP, ni timer, ni cache persistant.

## Déclenchement

- une seule lecture après la restauration réussie de la session ;
- une lecture explicite via la commande d'actualisation ;
- une future authentification pourra appeler la même actualisation en 02F.

La commande est single-flight et refuse immédiatement un second appel. Le
dashboard ne prend aucun bail de maintenance et ne modifie jamais `GameAction`.

## Combinaison des cinq services

| API | Authentification | Passerelle royaume | Passerelle monde | Monde | État global |
|---|---|---|---|---|---|
| en ligne | en ligne | en ligne | en ligne | en ligne | `Online` |
| hors ligne | quelconque | en ligne | en ligne | en ligne | `Degraded` |
| quelconque | hors ligne | en ligne | en ligne | en ligne | `Degraded` |
| quelconque | quelconque | hors ligne | quelconque | quelconque | `Offline` |
| quelconque | quelconque | quelconque | hors ligne | quelconque | `Offline` |
| quelconque | quelconque | quelconque | quelconque | hors ligne | `Offline` |

Une absence de session, une panne réseau, un timeout ou une réponse inutilisable
produit `Unavailable`. Ces échecs ne constituent jamais une preuve que le monde
est hors ligne.

## Note de mise à jour

Le tri reste celui du legacy : `PublishedAt` décroissant. LINQ étant stable, deux
dates identiques conservent l'ordre reçu de l'API. La première note est affichée.
Le contrat réseau n'ayant pas de champ version, une version n'est exposée que si
l'identifiant officiel `atlas-launcher-x-y-z` la fournit sans ambiguïté. Aucun
contenu n'est fabriqué lorsque la liste est vide.

Après un succès puis un échec, la dernière note et le dernier état fiable sont
conservés dans le snapshot. L'état courant du royaume devient néanmoins
`Unavailable`, afin de ne pas présenter une donnée ancienne comme actuelle, et
`IsStale` indique la conservation. Aucun nouveau cache disque n'est écrit.
