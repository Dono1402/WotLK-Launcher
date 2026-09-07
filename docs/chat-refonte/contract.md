# Atlas Messages — contrat de la refonte

Périmètre validé dans la conversation du 7 septembre 2026. Implémentation autorisée. Les déploiements de production et redémarrages de services restent une étape distincte, après validation locale et revue de leur impact.

## Produit

Une page complète, conversations à gauche et discussion à droite. Dimensions adaptatives définies par l'interface, sans splitter ni réglage de taille du texte. Avatars, présence de compte/personnage actuel, recherche de contacts/conversations, Non lus, nouvelle conversation, groupes avec nom/image/membres, conversations épinglées et archives (retour sur nouveau message), messages épinglés partagés.

Fil groupé par auteur/origine/date, séparateurs de jour et nouveaux messages, lecture conditionnée à la visibilité réelle, retour vers les nouveaux messages, texte sélectionnable/Copier, réponses, réactions Unicode, modification et suppression par l'auteur. Markdown avec HTML désactivé, liens sûrs, listes/citations/code/gras/italique/barré et extension spoiler `||texte||`.

Brouillons persistés par environnement et compte, idempotence conservée après redémarrage, attente hors ligne annulable avant POST, progression/retry/annulation des fichiers. DND global manuel et persistant, sans durée. Réglages globaux de partage Lu/Saisie. Le curseur lu privé reste indépendant de l'accusé partagé. **Son Messages désactivé jusqu'à choix définitif de l'utilisateur.**

Fichiers autorisés : images, images animées personnelles, documents, audio et vidéo, maximum **500 000 000 octets par fichier**. PNG/JPEG/WebP/GIF ; PDF/TXT/MD et documents bureautiques reconnus ; MP3/OGG/WAV ; MP4/WebM. Aucun ZIP/7Z/RAR en tant qu'archive, aucun catalogue de GIF externe. Sélection, dépôt multiple et collage de captures avec prévisualisation avant envoi. Les binaires restent hors du pont JSON.

Liens : YouTube intégré, autres lecteurs pris en charge et cartes de métadonnées pour pages ordinaires. Plusieurs aperçus empilés dans l'ordre. La croix retire uniquement l'aperçu **non vidéo**, pour tous les participants, de façon persistante ; texte, lien et autres aperçus restent. Aucun bouton de retrait ni mutation autorisée pour un aperçu vidéo. Pas de galerie partagée, pas de recherche d'historique, pas d'état À relire, pas de réglage de notifications par conversation.

Cartes Atlas typées : objet, personnage, quête, lieu et sortie (date, rôles tank/healer/damage et réponses). Ne pas inventer des statistiques absentes. Continuité du fil liée au compte ; personnage actif dans l'en-tête et, lorsque connu, personnage d'envoi conservé sur le message en jeu.

## Contrats .NET et wire

`source/WotLK.Launcher.Chat.Contracts/ChatContracts.cs` est partagé entre serveur et client. Namespace `WotLK.Launcher.Chat`. JSON camelCase avec `ChatJson.Options`. Tous les `long` sont des **chaînes décimales** JSON ; `uint accountId` reste numérique. `threadId` et identifiants d'upload sont des chaînes opaques. Ne pas arrondir les identifiants dans JavaScript.

## API `/api/v2/chat`

Toutes les routes sont authentifiées, réponses privées `no-store`, contrôles d'accès serveur par conversation. Une appartenance révoquée doit invalider l'accès aux messages et aux fichiers. Les groupes acceptent les invitations explicitement ; leur historique est borné par la date d'entrée. Les requêtes JSON restent petites, distinctes des transferts.

| Méthode et suffixe | Requête | Réponse |
|---|---|---|
| GET `/state` | — | `ChatStateDto` (self, contacts, threads, preferences, eventCursor, capabilities) |
| POST `/threads` | `ChatCreateThreadRequest` | `ChatThreadDto` |
| PATCH `/threads/{threadId}` | `ChatThreadUpdateRequest` | `ChatThreadDto` |
| PATCH `/threads/{threadId}/self` | `ChatThreadSelfRequest` | `ChatThreadDto` |
| POST `/threads/{threadId}/members` | `ChatMemberRequest` action invite/accept/decline/leave/remove/role | `ChatThreadDto` |
| GET `/threads/{threadId}/messages?beforeId=&limit=50` | — | `ChatMessagesPageDto` |
| GET `/threads/{threadId}/messages/by-client/{clientMessageId}` | — | message envoyé identifié par sa clé client, pour résoudre une réponse perdue |
| POST `/threads/{threadId}/messages` | `ChatSendMessageRequest` | `ChatSendMessageResult` |
| PATCH `/threads/{threadId}/messages/{messageId}` | `ChatEditMessageRequest` | `ChatMessageDto` |
| DELETE `/threads/{threadId}/messages/{messageId}` | — | `ChatMessageDto` tombstone |
| PUT `/threads/{threadId}/messages/{messageId}/reaction` | `ChatReactionRequest` | `ChatMessageDto` |
| PUT `/threads/{threadId}/messages/{messageId}/pin` | `ChatPinRequest` | `ChatMessageDto` |
| DELETE `/threads/{threadId}/messages/{messageId}/previews/{previewId}` | — | `ChatMessageDto` |
| PUT `/threads/{threadId}/messages/{messageId}/card-response` | `ChatCardResponseRequest` | `ChatMessageDto` |
| POST `/threads/{threadId}/read` | `ChatReadRequest` | `ChatThreadDto` |
| PUT `/threads/{threadId}/typing` | `ChatTypingRequest` | `{ accepted: true }` |
| PATCH `/preferences` | `ChatPreferencesRequest` | `ChatPreferencesDto` |
| GET `/events?afterId=&waitSeconds=25` | — | `ChatEventsDto` |
| POST `/uploads` | `ChatUploadRequest` | `ChatUploadDto` |
| GET `/uploads/{uploadId}` | — | `ChatUploadDto` |
| PUT `/uploads/{uploadId}?offset=…` | corps binaire, bloc <= 4 MiB | `ChatUploadDto` |
| POST `/uploads/{uploadId}/complete` | — | `ChatUploadDto` |
| DELETE `/uploads/{uploadId}` | — | `{ accepted: true }` |
| GET `/attachments/{attachmentId}` | `Range` optionnel | flux authentifié 200/206 |
| GET `/preview-image?url=…` | — | image publique validée/proxy borné, pour métadonnées de liens seulement |
| GET `/linked-media?url=…` | `Range` optionnel | audio/vidéo public validé, proxy borné 200/206 |

`GET events` reste en attente jusqu'à une mutation ou 25 secondes, réveillé immédiatement par un signal en processus et relecture du journal durable. Rejouer les événements après reconnexion, puis les appliquer par ID/version. Les nouveaux événements de réaction/édition/etc. ne changent pas le nombre de messages non lus. `RequiresResync` permet de reconstruire l'état si le journal demandé n'est plus disponible. Ne pas sérialiser des curseurs alloués mais non commités.

Les événements ont `kind` message/thread/preferences/typing/access et transportent le DTO complet mis à jour dans le champ correspondant. Un événement access indique une invalidation d'accès. Les mises à jour de lecture incluent le thread et/ou les messages dont les accusés autorisés changent. L'API doit filtrer les lectures privées à la source.

## Services serveur appartenant au coordinateur principal

`ChatAttachmentStorage` : stockage privé par UUID, états de transfert persistés, propriétaire vérifié, chunks bornés et contrôle du type réel. Méthodes publiques prévues :

```csharp
Task<ChatUploadDto> BeginAsync(uint owner, ChatUploadRequest request, CancellationToken ct);
Task<ChatUploadDto> GetAsync(uint owner, string id, CancellationToken ct);
Task<ChatUploadDto> AppendAsync(uint owner, string id, long offset, Stream source, long? contentLength, CancellationToken ct);
Task<ChatUploadDto> CompleteAsync(uint owner, string id, CancellationToken ct);
Task CancelAsync(uint owner, string id, CancellationToken ct);
Task<ChatAttachmentDto> RequireCompleteAsync(uint owner, string id, CancellationToken ct);
Task<ChatAttachmentRead> OpenReadAsync(string id, CancellationToken ct);
```

`ChatAttachmentRead` contient `Stream`, `Length`, `ContentType`, `FileName`; le caller est propriétaire du stream. `OpenReadAsync` ne remplace pas l'autorisation : les endpoints doivent vérifier le rattachement à un message accessible (ou avatar de groupe accessible). `CancelAsync` ne détruit pas un fichier déjà terminé. La suppression des fichiers terminés requiert un nettoyage qui vérifie les références SQL. Pas de chemin filesystem dans un DTO public. Les médias complets sont référencés transactionnellement dans la DB à l'envoi.

`ChatLinkPreviewService` :

```csharp
Task<IReadOnlyList<ChatLinkPreviewDto>> BuildAsync(string body, CancellationToken ct);
Task<ChatPreviewImage?> FetchImageAsync(string url, CancellationToken ct);
```

`ChatPreviewImage` contient `byte[] Bytes`, `string ContentType`. Extraction de liens ordonnés, IDs SHA stables d'URL, limite dix. YouTube/Vimeo et médias directs reconnus ; HTML distant uniquement lu comme métadonnées, jamais injecté. DNS/IP/redirects/tailles/délais bornés pour éviter un accès aux destinations privées. La DB préserve les IDs supprimés lors d'un recalcul après édition. Une indisponibilité de métadonnées conserve le lien ordinaire.

## Pont WebView natif

Assets locaux : `Assets/Chat/index.html`, `chat.css`, `chat.js`, `chat-render.js`, vendor local. Une seule WebView pour la page Messages. Origine locale synthétique servie nativement : `https://animeclub.fr/atlas-messages/`. Origine média interceptée : `https://atlas-chat-media.invalid/` (aucun token dans le DOM). CSP et navigation restreintes ; intégrations vidéo dans iframes isolées de la page.

L'état envoyé par C# est :

```text
{ type: 'snapshot', sessionId, ownerAccountId, sequence,
  locale: 'fr'|'en', isActive, isAvailable, isLoading, error,
  state: ChatStateDto, selectedThreadId: string|null,
  messages: ChatMessageDto[], hasEarlier, isLoadingEarlier,
  draft: { body, replyToMessageId, attachments: ChatUploadDto[], card },
  pending: [{clientMessageId,threadId,body,status,error,attachments,progress}],
  typing: ChatTypingDto[], mediaOrigin: 'https://atlas-chat-media.invalid/' }
```

Chaque message reçu du JS contient les identités de session/vue et un identifiant de requête :

```text
{ type:'action', requestId, sessionId, ownerAccountId, sequence, action, payload }
```

Actions UI (payload) :

| Action | Payload |
|---|---|
| ready | {} |
| selectThread | {threadId} |
| createThread | ChatCreateThreadRequest |
| loadEarlier | {threadId,beforeId} |
| draft | {threadId,body,replyToMessageId,card} |
| send | {threadId,clientMessageId,body,replyToMessageId,attachmentIds,card} |
| threadSelf | {threadId,isPinned?,isArchived?} |
| threadUpdate | {threadId,title?,avatarAttachmentId?,expectedVersion?} |
| member | {threadId,accountId,action,role?} |
| editMessage | {threadId,messageId,body,expectedVersion} |
| deleteMessage | {threadId,messageId} |
| reaction | {threadId,messageId,emoji,active} |
| pinMessage | {threadId,messageId,pinned} |
| dismissPreview | {threadId,messageId,previewId} |
| cardResponse | {threadId,messageId,status,role} |
| read | {threadId,throughMessageId} — seulement après rendu et fond réellement visible |
| typing | {threadId,isTyping} |
| preferences | ChatPreferencesRequest |
| pickFiles / pasteImage | {threadId} |
| removeAttachment / cancelUpload / retryUpload | {threadId,uploadId} |
| cancelSend / retrySend | {clientMessageId} |
| downloadAttachment / openAttachment | {attachmentId,fileName} |
| openExternal | {url} — http(s) validé nativement |
| openProfile | {accountId,characterGuid?} |

Le JS peut maintenir les filtres et panneaux locaux sans action réseau. C# répond si nécessaire `{type:'result',requestId,payload,error}`. Les actions mutations sont validées avec session, compte et sélection pertinents. Le host confirme aussi l'activité réelle de la fenêtre pour la lecture. Les métadonnées et médias ne donnent jamais accès aux jetons, fichiers système arbitraires ou domaines réseau privés.

API du host `ChatViewV2` : `ApplyRichSnapshot(object)`, `SendRichResult(string,object?,string?)`, événements `RichActionRequested` et `FilesAddedRequested`. Les chemins de fichiers proviennent exclusivement de sélections/dépôts/colles opérés par le host, pas d'une chaîne fournie par le JS. Le runtime transmet ses données riches au shell ; les notifications et actions natives restent hors JS.

## Validation et livraison

Fixtures de comptes synthétiques, MySQL loopback avec bases jetables préfixées, tests du journal/migrations/permissions/IDEMPOTENCE, transferts bornés (500Mo simulés sans allocation équivalente), parsing Markdown/liens, CSP, rendu WebView et véritables actions du pont. Aucune fenêtre utilisateur ni compte réel ne sert de fixture. Préserver les corrections Profil/Amis/Activité/Statut déjà livrées et les tests de la passerelle jeu. La candidate locale et le paquet serveur sont produits séparément avant toute décision de déploiement.
