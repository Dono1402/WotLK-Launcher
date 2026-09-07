(function (global) {
  'use strict';
  const R = global.AtlasChatRender;
  if (!R) return;
  const $ = name => document.getElementById(name);
  const e = R.element;
  const fileExtensions = new Set(['png','jpg','jpeg','gif','webp','pdf','txt','md','docx','xlsx','pptx','odt','ods','odp','mp3','ogg','wav','mp4','webm']);
  const strings = {
    subtitle:['Gardez le contact, en jeu comme ailleurs.','Keep in touch, in game and beyond.'], conversations:['Conversations','Conversations'], unread:['Non lus','Unread'], searchContacts:['Rechercher un contact…','Find a contact…'], newConversation:['Nouvelle conversation','New conversation'], chooseConversation:['Une conversation commence ici','A conversation starts here'], chooseConversationHint:['Retrouvez vos amis ou créez un groupe pour votre prochaine aventure.','Catch up with friends or make a group for your next adventure.'], dnd:['Ne pas déranger','Do not disturb'], online:['En ligne','Online'], offline:['Hors ligne','Offline'], inGame:['En jeu','In game'], playingOn:['En jeu sur','Playing as'], today:['Aujourd’hui','Today'], yesterday:['Hier','Yesterday'], noConversations:['Vos conversations apparaîtront ici.','Your conversations will appear here.'], noResults:['Aucun contact ne correspond à votre recherche.','No contacts match your search.'], pinned:['Épinglées','Pinned'], recent:['Récentes','Recent'], contacts:['Contacts','Contacts'], invitation:['Invitation à un groupe','Group invitation'], invitations:['Invitations','Invitations'], newMessages:['Nouveaux messages','New messages'], loadEarlier:['Afficher les messages précédents','Show earlier messages'], loading:['Chargement…','Loading…'], noMessages:['Écrivez le premier message.','Write the first message.'], details:['Participants et détails','Members and details'], conversationActions:['Actions de la conversation','Conversation actions'], pinnedMessages:['Messages épinglés','Pinned messages'], pinnedMessage:['Message épinglé','Pinned message'], noPinned:['Aucun message épinglé.','No pinned messages.'], composerPlaceholder:['Écrire un message…','Write a message…'], composerName:['Texte du message','Message text'], composerHint:['Entrée pour envoyer · Maj + Entrée pour une nouvelle ligne','Enter to send · Shift + Enter for a new line'], send:['Envoyer','Send'], sending:['Envoi…','Sending…'], save:['Enregistrer','Save'], cancel:['Annuler','Cancel'], close:['Fermer','Close'], draftSaved:['Brouillon conservé','Draft saved'], reply:['Répondre','Reply'], replyingTo:['Réponse à','Replying to'], editingMessage:['Modification du message','Editing message'], edited:['modifié','edited'], edit:['Modifier','Edit'], delete:['Supprimer','Delete'], deleteMessage:['Supprimer ce message ?','Delete this message?'], deleteMessageHint:['Il sera retiré de l’historique du launcher pour tous les participants. Un texte déjà affiché en jeu ne peut pas être effacé.','It will be removed from the launcher history for everyone. Text already shown in game cannot be erased.'], copy:['Copier le texte','Copy text'], copied:['Texte copié','Text copied'], copyFailed:['Impossible de copier le texte.','Could not copy the text.'], react:['Ajouter une réaction','Add a reaction'], addReaction:['Ajouter la réaction','Add reaction'], removeReaction:['Retirer ma réaction','Remove my reaction'], messageActions:['Actions du message','Message actions'], read:['Lu','Read'], pinMessage:['Épingler le message','Pin message'], unpinMessage:['Désépingler le message','Unpin message'], pinConversation:['Épingler la conversation','Pin conversation'], unpinConversation:['Désépingler la conversation','Unpin conversation'], goToMessage:['Revenir au message','Go to message'], messageNotLoaded:['Ce message se trouve plus haut dans la conversation.','This message is further up in the conversation.'], revealSpoiler:['Afficher le texte masqué','Reveal spoiler'], hideSpoiler:['Masquer le texte','Hide spoiler'], attachFiles:['Ajouter des fichiers','Add files'], attachmentHint:['Images, GIF, documents, audio et vidéo · 500 Mo par fichier','Images, GIFs, documents, audio and video · 500 MB per file'], dropFiles:['Déposez vos fichiers ici','Drop your files here'], dropUnavailable:['Utilisez le bouton + pour ajouter ces fichiers.','Use the + button to add these files.'], shareGame:['Partager l’Armory d’un personnage','Share a character’s Armory'], download:['Télécharger','Download'], viewImage:['Afficher l’image','View image'], image:['Image','Image'], imageUnavailable:['Image indisponible','Image unavailable'], video:['Vidéo','Video'], audio:['Audio','Audio'], playVideo:['Lire la vidéo','Play video'], openLink:['Ouvrir le lien','Open link'], openOnSite:['Ouvrir sur le site','Open on website'], removePreviewForEveryone:['Retirer cet aperçu pour tous les participants','Remove this preview for everyone'], previewRemoved:['Aperçu retiré pour tous les participants.','Preview removed for everyone.'], waiting:['En attente','Waiting'], uploading:['Transfert','Uploading'], processing:['Préparation','Preparing'], ready:['Prêt','Ready'], failed:['Échec','Failed'], retry:['Réessayer','Retry'], retrySend:['Réessayer l’envoi','Retry send'], cancelSend:['Annuler l’envoi en attente','Cancel queued message'], cancelUpload:['Annuler le transfert','Cancel upload'], removeAttachment:['Retirer la pièce jointe','Remove attachment'], disconnected:['Connexion interrompue. Vos messages restent en attente.','Connection lost. Your messages remain queued.'], unavailable:['La messagerie est indisponible. Votre brouillon est conservé.','Messaging is unavailable. Your draft is kept.'], accessRevoked:['Vous ne pouvez plus écrire dans cette conversation.','You can no longer send messages in this conversation.'], genericError:['L’action n’a pas abouti. Réessayez dans un instant.','The action could not be completed. Try again shortly.'], group:['Groupe','Group'], direct:['Un ami','One friend'], createGroup:['Créer le groupe','Create group'], groupName:['Nom du groupe','Group name'], groupNamePlaceholder:['Par exemple : Les aventuriers du soir','For example: Evening adventurers'], selectFriends:['Choisissez vos amis','Choose your friends'], newConversationHint:['Échangez avec un ami ou réunissez votre groupe.','Chat with a friend or bring your group together.'], members:['participants','members'], activeMembers:['Participants','Members'], pendingMembers:['Invitations en attente','Pending invitations'], inviteMembers:['Inviter des amis','Invite friends'], invite:['Inviter','Invite'], invited:['Invité','Invited'], accept:['Accepter','Accept'], decline:['Refuser','Decline'], invitationHint:['Vous êtes invité à rejoindre cette conversation.','You’ve been invited to join this conversation.'], manageGroup:['Modifier le groupe','Edit group'], groupImage:['Image du groupe','Group image'], chooseGroupImage:['Choisir une image','Choose an image'], useGroupImage:['Utiliser pour le groupe','Use for the group'], groupImageHint:['Ajoutez une image, puis choisissez-la ci-dessous.','Add an image, then choose it below.'], leaveGroup:['Quitter le groupe','Leave group'], leaveGroupHint:['Vous ne recevrez plus les nouveaux messages de ce groupe.','You will no longer receive new messages from this group.'], removeMember:['Retirer du groupe','Remove from group'], makeAdmin:['Nommer administrateur','Make administrator'], makeMember:['Retirer le rôle administrateur','Remove administrator role'], admin:['Administrateur','Administrator'], owner:['Créateur','Owner'], self:['Vous','You'], openProfile:['Ouvrir le profil','Open profile'], typingOne:['écrit…','is typing…'], typingMany:['sont en train d’écrire…','are typing…'], gameCard:['Carte Atlas','Atlas card'], item:['Objet','Item'], character:['Personnage','Character'], quest:['Quête','Quest'], location:['Lieu','Location'], outing:['Sortie','Outing'], title:['Titre','Title'], description:['Description','Description'], reference:['Identifiant de référence','Reference ID'], optional:['facultatif','optional'], date:['Date','Date'], level:['Niveau','Level'], tank:['Tank','Tank'], healer:['Soigneur','Healer'], damage:['DPS','Damage'], joining:['Participe','Joining'], cardAttached:['Armory joint','Armory attached'], limitReached:['Un message peut contenir jusqu’à 1 000 caractères.','A message can contain up to 1,000 characters.'], attachmentLimit:['Vous pouvez joindre jusqu’à 10 fichiers par message.','You can attach up to 10 files to a message.'], noFriends:['Votre liste d’amis est vide.','Your friends list is empty.'], legacyHint:['Les fonctions avancées seront disponibles après la mise à jour du service.','Advanced features will be available after the service is updated.'],
    away:['Absent','Away'], characterArmory:['Armory du personnage','Character Armory'], openArmory:['Ouvrir l’Armory','Open Armory'], chooseOwnCharacter:['Choisissez l’un de vos personnages.','Choose one of your characters.'], yourCharacters:['Vos personnages','Your characters'], shareCharacter:['Partager l’Armory de','Share the Armory of'], loadingCharacters:['Chargement de vos personnages…','Loading your characters…'], noCharacters:['Vous n’avez pas encore de personnage à partager.','You do not have a character to share yet.'], charactersUnavailable:['Impossible de charger vos personnages. Réessayez dans un instant.','Your characters could not be loaded. Try again shortly.'], addingArmory:['Ajout de l’Armory…','Adding the Armory…'], warrior:['Guerrier','Warrior'], paladin:['Paladin','Paladin'], hunter:['Chasseur','Hunter'], rogue:['Voleur','Rogue'], priest:['Prêtre','Priest'], deathKnight:['Chevalier de la mort','Death Knight'], shaman:['Chaman','Shaman'], mage:['Mage','Mage'], warlock:['Démoniste','Warlock'], druid:['Druide','Druid']
  };
  let snapshot = { sessionId: '', ownerAccountId: 0, sequence: '0', locale: 'fr', isActive: false, isAvailable: false, state: { threads: [], contacts: [], preferences: {}, capabilities: [] }, messages: [], selectedThreadId: null, draft: {}, pending: [], typing: [], mediaOrigin: 'https://atlas-chat-media.invalid/' };
  let selectedThread = null, editTarget = null, editBackup = null;
  let replyTarget = null, composerCard = null, draftDirty = false, draftTimer = 0, typingTimer = 0, lastTypingAt = 0, typingActive = false;
  let renderVersion = 0, layoutPending = false, followBottom = true, stableAnchor = null, unreadBoundary = null, lastReadKey = '';
  let lastUserScroll = 0, programmaticScroll = false, dialogRefresh = null, toastTimer = 0, menuReturnFocus = null;
  let armoryPickerOpen = false, armoryPickerFocusPending = false, armoryRequestPending = false, armorySelection = null, armoryError = '';
  let lastComposerStateKey = '';
  const requests = new Map(), localDrafts = new Map(), inFlightSends = new Map(), armorySelections = new Map();
  const timeline = $('timeline'), messageList = $('message-list'), composer = $('composer-input');
  const t = key => strings[key] ? strings[key][String(snapshot.locale).startsWith('en') ? 1 : 0] : key;
  const uuid = () => global.crypto && crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 3 | 8)).toString(16); });
  const capable = name => (snapshot.state.capabilities || []).includes(name);
  const sessionKey = value => String(value.sessionId || '') + ':' + String(value.ownerAccountId || 0);
  const active = () => !!snapshot.isActive && !document.hidden && !!snapshot.ownerAccountId && !!selectedThread;
  const currentThreadId = () => selectedThread && selectedThread.id;
  const hasIdentity = () => !!snapshot.sessionId && snapshot.ownerAccountId > 0;
  const normalizedBody = text => String(text || '').replace(/\r\n/g, '\n').trim();

  function post(action, payload, additionalObjects) {
    const envelope = { type: 'action', requestId: uuid(), sessionId: snapshot.sessionId, ownerAccountId: snapshot.ownerAccountId, sequence: String(snapshot.sequence || '0'), action, payload: payload || {} };
    const bridge = global.chrome && chrome.webview;
    if (!bridge) return { requestId: envelope.requestId, delivered: false };
    try {
      if (additionalObjects && additionalObjects.length) {
        if (!bridge.postMessageWithAdditionalObjects) return { requestId: envelope.requestId, delivered: false };
        bridge.postMessageWithAdditionalObjects(envelope, additionalObjects);
      } else bridge.postMessage(envelope);
      return { requestId: envelope.requestId, delivered: true };
    } catch (_) { return { requestId: envelope.requestId, delivered: false }; }
  }

  function action(name, payload, options) {
    if (name !== 'ready' && !hasIdentity()) return null;
    const result = post(name, payload, options && options.files);
    if (result.delivered && (options?.result || !options?.silent)) requests.set(result.requestId, { handler: options?.result || ((_, error) => { if (error) toast(errorText(error)); }), session: sessionKey(snapshot) });
    if (!result.delivered && name !== 'ready' && !(options && options.silent)) toast(t('genericError'));
    return result.delivered ? result.requestId : null;
  }

  function handleResult(result) {
    const request = requests.get(result.requestId);
    requests.delete(result.requestId);
    if (request && request.session === sessionKey(snapshot)) request.handler(result.payload, result.error);
    else if (!request && /^native-drop-[a-f0-9-]{32,36}$/i.test(result.requestId || '') && result.error && hasIdentity()
      && (!result.sessionId || result.sessionId === snapshot.sessionId)
      && (!result.ownerAccountId || result.ownerAccountId === snapshot.ownerAccountId)) toast(errorText(result.error));
  }
  function errorText(code) {
    if (!code) return '';
    const errors = { 'chat-unavailable': 'unavailable', 'chat-unauthorized': 'unavailable', 'chat-not-friends': 'accessRevoked', 'chat-forbidden': 'accessRevoked', 'chat-invalid-message': 'limitReached', 'chat-too-many-attachments': 'attachmentLimit', 'chat-file-type-not-supported': 'attachmentHint', 'chat-file-too-large': 'attachmentHint', 'chat-armory-unavailable': 'charactersUnavailable', 'chat-character-not-owned': 'charactersUnavailable' };
    if (typeof code === 'string' && !code.startsWith('chat-') && !code.startsWith('http-') && code.length < 300) return code;
    return t(errors[code] || 'genericError');
  }
  function toast(text) { clearTimeout(toastTimer); $('toast').textContent = text; $('toast').hidden = !text; toastTimer = setTimeout(() => { $('toast').hidden = true; }, 4200); }

  function localize() {
    document.documentElement.lang = String(snapshot.locale).startsWith('en') ? 'en' : 'fr';
    for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = t(node.dataset.i18n);
    for (const node of document.querySelectorAll('[data-title]')) { node.title = t(node.dataset.title); node.setAttribute('aria-label', t(node.dataset.title)); }
    for (const node of document.querySelectorAll('[data-placeholder]')) node.placeholder = t(node.dataset.placeholder);
    for (const node of document.querySelectorAll('[data-label]')) node.setAttribute('aria-label', t(node.dataset.label));
    $('conversation-list').setAttribute('aria-label', t('conversations'));
    $('attachment-queue').setAttribute('aria-label', t('attachFiles'));
  }

  function resolveProfile(profile) {
    if (!profile) return null;
    const current = snapshot.state.self?.accountId === profile.accountId ? snapshot.state.self
      : (snapshot.state.contacts || []).find(contact => contact.accountId === profile.accountId);
    return current ? { ...profile, ...current } : profile;
  }
  function peer(thread) { return resolveProfile((thread && thread.members || []).find(member => member.profile && member.profile.accountId !== snapshot.ownerAccountId)?.profile || null); }
  function threadTitle(thread) { return thread.kind === 'group' ? thread.title || t('group') : peer(thread)?.username || thread.title || ''; }
  function threadProfile(thread) { return thread.kind === 'group' ? { username: thread.title, avatarUrl: thread.avatarUrl || (thread.avatarAttachmentId ? snapshot.mediaOrigin + 'attachments/' + encodeURIComponent(thread.avatarAttachmentId) : '') } : peer(thread); }
  function threadPresence(thread) { return thread.kind === 'group' ? (thread.members || []).filter(member => member.status === 'active').length + ' ' + t('members') : R.presenceText(peer(thread), t); }
  function conversationPreview(thread) {
    const message = thread.lastMessage?.deletedAt
      ? (snapshot.messages || []).filter(item => item.threadId === thread.id && !item.deletedAt && R.id(item.id)).sort((a, b) => R.compareIds(a.id, b.id)).at(-1)
      : thread.lastMessage;
    const saved = localDrafts.get(thread.id);
    if (saved && (saved.body || saved.card)) return t('draftSaved') + ' · ' + saved.body;
    if (thread.isInvited) return t('invitation');
    if (!message) return thread.lastMessage?.deletedAt ? '' : t('noMessages');
    const prefix = message.sender?.accountId === snapshot.ownerAccountId ? t('self') + ' : ' : thread.kind === 'group' ? (message.sender?.username || '') + ' : ' : '';
    return prefix + (message.body || message.card?.title || message.attachments?.map(attachment => attachment.fileName).join(', ') || '');
  }
  function shortDate(value) {
    if (!value) return '';
    if (R.dayKey(value) === R.dayKey(new Date())) return R.time(value, snapshot.locale);
    const date = new Date(value);
    return Number.isFinite(date.getTime()) ? date.toLocaleDateString(snapshot.locale, { day: 'numeric', month: 'short' }) : '';
  }
  function selectThread(threadId) { if (threadId === currentThreadId()) return; flushDraft(); stopTyping(); closeMenus(); action('selectThread', { threadId }); }

  function renderConversations() {
    const threads = (snapshot.state.threads || []).slice();
    threads.sort((a, b) => Number(!!b.isInvited) - Number(!!a.isInvited) || Number(!!b.isPinned) - Number(!!a.isPinned) || R.compareIds(b.lastMessage?.id || '0', a.lastMessage?.id || '0') || threadTitle(a).localeCompare(threadTitle(b), snapshot.locale));
    const rows = [];
    let section = '';
    for (const thread of threads) {
      const next = thread.isInvited ? 'invitations' : thread.isPinned ? 'pinned' : 'recent';
      if (next !== section) { section = next; rows.push({ kind: 'section', key: 'section:' + next, label: next }); }
      rows.push({ kind: 'thread', key: thread.id, thread });
    }
    R.reconcileKeyed($('conversation-list'), rows, row => row.key, row => {
      if (row.kind === 'section') { const label = e('div', 'conversation-section-label', t(row.label)); if (row.label === 'pinned') label.prepend(R.icon('pin')); return label; }
      const thread = row.thread;
      const profile = threadProfile(thread);
      const title = threadTitle(thread);
      const node = R.button('conversation-row' + (thread.id === currentThreadId() ? ' is-selected' : '') + (thread.unreadCount > 0 ? ' is-unread' : ''), title, null, () => selectThread(thread.id));
      node.setAttribute('role', 'listitem');
      if (thread?.id === currentThreadId()) node.setAttribute('aria-current', 'true');
      if (thread?.unreadCount > 0) node.setAttribute('aria-label', title + ', ' + thread.unreadCount + ' ' + t('unread'));
      node.append(R.avatar(profile, { group: thread?.kind === 'group', presence: true, mediaOrigin: snapshot.mediaOrigin }));
      const text = e('span', 'conversation-row-text'); text.append(e('span', 'conversation-row-title', title), e('span', 'conversation-row-preview', conversationPreview(thread)));
      const meta = e('span', 'conversation-row-meta'); meta.append(e('span', 'conversation-time', shortDate(thread.lastMessage?.createdAt)));
      if (thread?.unreadCount > 0) meta.append(e('span', 'conversation-unread', thread.unreadCount > 99 ? '99+' : thread.unreadCount));
      else if (thread?.isPinned) { const pin = R.icon('pin'); pin.classList.add('conversation-pin'); meta.append(pin); }
      node.append(text, meta);
      node.addEventListener('contextmenu', event => { event.preventDefault(); showThreadMenu(thread, event.clientX, event.clientY); });
      return node;
    }, row => JSON.stringify([row, currentThreadId(), snapshot.locale, row.thread && localDrafts.get(row.thread.id), row.thread && threadProfile(row.thread), row.thread && conversationPreview(row.thread)]));
    const empty = $('conversation-empty'); empty.hidden = rows.length > 0;
    empty.querySelector('p').textContent = t('noConversations');
    $('sidebar-status').textContent = snapshot.isLegacyFallback ? t('legacyHint') : snapshot.isLoading && !snapshot.state.threads.length ? t('loading') : '';
    $('new-conversation-button').disabled = !hasIdentity(); $('empty-new-button').disabled = !hasIdentity();
  }

  function captureAnchor() {
    const top = timeline.getBoundingClientRect().top;
    for (const node of messageList.children) {
      if (node.dataset.messageId && node.getBoundingClientRect().bottom > top + 1) return { id: node.dataset.messageId, offset: node.getBoundingClientRect().top - top };
    }
    return null;
  }
  function restoreAnchor(anchor) {
    if (!anchor) return;
    const node = Array.from(messageList.children).find(item => item.dataset.messageId === anchor.id);
    if (node) timeline.scrollTop += node.getBoundingClientRect().top - timeline.getBoundingClientRect().top - anchor.offset;
  }
  function isAtBottom(threshold) { return timeline.scrollHeight - timeline.clientHeight - timeline.scrollTop <= (threshold === undefined ? 2 : threshold); }
  function scrollBottom() { programmaticScroll = true; timeline.scrollTop = timeline.scrollHeight; followBottom = true; stableAnchor = null; requestAnimationFrame(() => { programmaticScroll = false; }); }
  function requestRead() {
    if (layoutPending || !active() || !isAtBottom(2) || timeline.clientHeight <= 0) return;
    const messages = (snapshot.messages || []).filter(message => R.id(message.id)).sort((a, b) => R.compareIds(a.id, b.id));
    const last = messages.at(-1);
    if (!last) return;
    const lastVisible = messages.filter(message => !message.deletedAt).at(-1);
    if (lastVisible) {
      const node = Array.from(messageList.children).find(item => item.dataset.messageId === lastVisible.id);
      if (!node) return;
      const bounds = node.getBoundingClientRect(), viewport = timeline.getBoundingClientRect();
      if (bounds.bottom > viewport.bottom + 2 || bounds.bottom < viewport.top) return;
    }
    const key = sessionKey(snapshot) + ':' + currentThreadId() + ':' + last.id;
    if (key === lastReadKey) return;
    lastReadKey = key;
    action('read', { threadId: currentThreadId(), throughMessageId: last.id }, { silent: true });
  }
  function updateJumpButton() { $('jump-latest-button').hidden = isAtBottom(36) || !selectedThread || !snapshot.messages.length; }
  function settleLayout(anchor, toBottom) {
    const version = ++renderVersion; layoutPending = true;
    requestAnimationFrame(() => {
      if (version !== renderVersion) return;
      if (toBottom) scrollBottom(); else restoreAnchor(anchor);
      requestAnimationFrame(() => {
        if (version !== renderVersion) return;
        layoutPending = false; stableAnchor = followBottom ? null : captureAnchor();
        updateJumpButton(); requestRead();
      });
    });
  }

  function renderTimeline(changedThread) {
    const oldAnchor = changedThread ? null : captureAnchor();
    const toBottom = changedThread || followBottom || isAtBottom(36);
    const allMessages = (snapshot.messages || []).filter(message => R.id(message.id)).slice().sort((a, b) => R.compareIds(a.id, b.id));
    const messages = allMessages.filter(message => !message.deletedAt);
    const existing = new Map(Array.from(messageList.children).map(node => [node.dataset.key, node]));
    let cursor = messageList.firstElementChild, previous = null, lastDay = '', unreadAdded = false;
    const keep = (key, node) => { node.dataset.key = key; if (node !== cursor) messageList.insertBefore(node, cursor); cursor = node.nextElementSibling; existing.delete(key); };
    const context = { ownerAccountId: snapshot.ownerAccountId, locale: snapshot.locale, mediaOrigin: snapshot.mediaOrigin, t, action, profile: resolveProfile, isActive: active, capable, reply: beginReply, copy: copyText, react: showReactions, messageMenu: showMessageMenu, jumpTo, viewImage, dismissPreview };
    for (const message of messages) {
      const day = R.dayKey(message.createdAt);
      if (day !== lastDay) {
        lastDay = day;
        const key = 'day:' + day, divider = existing.get(key) || e('div', 'date-divider');
        divider.textContent = R.dayLabel(message.createdAt, snapshot.locale, t); keep(key, divider);
      }
      const beginsUnread = !unreadAdded && unreadBoundary !== null && R.compareIds(message.id, unreadBoundary) > 0 && message.sender?.accountId !== snapshot.ownerAccountId;
      if (beginsUnread) { unreadAdded = true; const key = 'unread', divider = existing.get(key) || e('div', 'unread-divider'); divider.textContent = t('newMessages'); keep(key, divider); }
      context.grouped = !beginsUnread && R.canGroup(previous, message);
      const key = 'message:' + message.id;
      const node = R.reconcileMessage(existing.get(key), message, { ...context });
      keep(key, node); previous = message;
    }
    const pending = (snapshot.pending || []).filter(item => item.threadId === currentThreadId() && !allMessages.some(message => message.clientMessageId === item.clientMessageId));
    for (const queued of pending) {
      const key = 'pending:' + queued.clientMessageId;
      let node = existing.get(key);
      const signature = JSON.stringify([queued, snapshot.locale]);
      if (!node || node._signature !== signature) {
        const replacement = renderPending(queued); replacement._signature = signature;
        if (node) { node.replaceWith(replacement); if (cursor === node) cursor = replacement; }
        node = replacement;
      }
      keep(key, node);
    }
    for (const node of existing.values()) { R.suspendMedia(node); node.remove(); }
    $('no-messages').hidden = messages.length > 0 || pending.length > 0 || snapshot.isLoading;
    $('load-earlier-button').hidden = !snapshot.hasEarlier;
    $('load-earlier-button').disabled = snapshot.isLoadingEarlier || !snapshot.isAvailable;
    $('load-earlier-button').textContent = t(snapshot.isLoadingEarlier ? 'loading' : 'loadEarlier');
    if (changedThread) followBottom = true;
    settleLayout(oldAnchor, toBottom);
  }

  function renderPending(queued) {
    const node = e('article', 'message is-own is-group-start'); node.dataset.clientMessageId = queued.clientMessageId;
    const avatar = e('div', 'message-avatar'); avatar.append(R.avatar(snapshot.state.self, { small: true, presence: true, mediaOrigin: snapshot.mediaOrigin }));
    const main = e('div', 'message-main');
    const heading = e('div', 'message-heading'); heading.append(e('span', 'message-author', snapshot.state.self?.username || t('self')), e('span', 'message-time', R.time(queued.createdAt, snapshot.locale)));
    main.append(heading, R.renderMarkdown(queued.body, t));
    if (queued.card) main.append(e('div', 'game-card', queued.card.title || t('cardAttached')));
    if (queued.attachments?.length) main.append(e('div', 'message-content', queued.attachments.map(attachment => attachment.fileName || '').filter(Boolean).join(', ')));
    const failed = queued.status === 'failed', sending = ['sending', 'posting'].includes(queued.status);
    const status = e('div', 'message-state' + (failed ? ' is-failed' : ''));
    const progress = Math.min(100, Math.max(0, Math.round((Number(queued.progress) || 0) * 100)));
    const stateLabel = queued.status === 'uploading' ? t('uploading') + ' ' + progress + ' %' : t(sending ? 'sending' : 'waiting');
    status.append(R.icon(failed ? 'alert' : 'clock'), document.createTextNode(failed ? errorText(queued.error || queued.errorCode) || t('failed') : stateLabel));
    if (failed) { const retry = R.button('', t('retrySend'), null, () => action('retrySend', { clientMessageId: queued.clientMessageId })); retry.textContent = t('retry'); status.append(retry); }
    if (!sending && queued.canCancel !== false) { const cancel = R.button('', t('cancelSend'), null, () => action('cancelSend', { clientMessageId: queued.clientMessageId })); cancel.textContent = t('cancel'); status.append(cancel); }
    main.append(status); node.append(avatar, main); return node;
  }

  function currentDraft() { return { body: composer.value, replyToMessageId: replyTarget?.messageId || replyTarget?.id || null, card: composerCard || null }; }
  function draftSignature(draft) { return JSON.stringify([draft.body || '', draft.replyToMessageId || null, draft.card || null]); }
  function saveLocalDraft() {
    if (!currentThreadId() || editTarget) return;
    const draft = currentDraft(); localDrafts.set(currentThreadId(), draft); draftDirty = true;
    clearTimeout(draftTimer); draftTimer = setTimeout(flushDraft, 250);
  }
  function flushDraft() { clearTimeout(draftTimer); if (!draftDirty || !currentThreadId() || editTarget) return; draftDirty = false; action('draft', { threadId: currentThreadId(), ...currentDraft() }, { silent: true }); }
  function setComposerFromDraft(draft) {
    composer.value = draft?.body || ''; composerCard = draft?.card || null;
    const replyId = draft?.replyToMessageId;
    replyTarget = replyId ? snapshot.messages.find(message => message.id === replyId) || { messageId: replyId, body: '', sender: { username: '' } } : null;
    if (replyTarget?.deletedAt) replyTarget = null;
    resizeComposer(); renderComposerContext();
  }
  function completeLocalSend(clientMessageId) {
    const send = inFlightSends.get(clientMessageId); if (!send) return;
    inFlightSends.delete(clientMessageId);
    const local = localDrafts.get(send.threadId);
    if (local && draftSignature(local) === send.signature) {
      localDrafts.set(send.threadId, { body: '', replyToMessageId: null, card: null });
      if (send.threadId === currentThreadId() && !editTarget && draftSignature(currentDraft()) === send.signature) setComposerFromDraft({});
    }
    updateComposer();
  }
  function sendMessage() {
    if (!selectedThread || !selectedThread.canSend || !hasIdentity()) return;
    const body = normalizedBody(composer.value);
    if (body.length > 1000) { showComposerError(t('limitReached')); return; }
    if (editTarget) {
      if (!body || !snapshot.isAvailable) return;
      const target = editTarget;
      action('editMessage', { threadId: target.threadId, messageId: target.id, body, expectedVersion: target.version }, { result: (_, error) => { if (error) showComposerError(errorText(error)); else if (editTarget?.id === target.id) cancelContext(); } });
      return;
    }
    const uploads = snapshot.draft?.attachments || [];
    if (uploads.some(upload => upload.status === 'failed' || upload.status === 'cancelled')) return;
    const attachmentIds = uploads.map(upload => upload.id || upload.localId || upload.attachment?.id).filter(Boolean);
    if (!body && !attachmentIds.length && !composerCard) return;
    if (inFlightSends.size && Array.from(inFlightSends.values()).some(send => send.threadId === currentThreadId() && send.signature === draftSignature(currentDraft()))) return;
    const clientMessageId = uuid(), draft = currentDraft();
    const send = { threadId: currentThreadId(), signature: draftSignature(draft) }; inFlightSends.set(clientMessageId, send);
    localDrafts.set(send.threadId, draft); flushDraft(); stopTyping();
    const requestId = action('send', { threadId: send.threadId, clientMessageId, body, replyToMessageId: draft.replyToMessageId, attachmentIds, card: draft.card }, { result: (_, error) => {
      if (error) { inFlightSends.delete(clientMessageId); showComposerError(errorText(error)); updateComposer(); }
      else completeLocalSend(clientMessageId);
    } });
    if (!requestId) inFlightSends.delete(clientMessageId);
    else showComposerError('');
    updateComposer();
  }
  function showComposerError(text) { $('composer-error').textContent = text; $('composer-error').hidden = !text; }
  function resizeComposer() { composer.style.height = 'auto'; composer.style.height = Math.min(148, Math.max(46, composer.scrollHeight)) + 'px'; }
  function publishComposerState() {
    const threadId = currentThreadId(), acceptsFiles = canReceiveFiles();
    if (!acceptsFiles) { dragDepth = 0; nativeDropActive = false; renderDropOverlay(); }
    if (!hasIdentity() || !threadId) { lastComposerStateKey = ''; return; }
    const key = JSON.stringify([sessionKey(snapshot), threadId, acceptsFiles]);
    if (lastComposerStateKey === key) return;
    if (action('composerState', { threadId, acceptsFiles }, { silent: true })) lastComposerStateKey = key;
  }
  function updateComposer() {
    publishComposerState();
    const editable = !!selectedThread && selectedThread.canSend && hasIdentity();
    composer.disabled = !editable;
    const length = normalizedBody(composer.value).length;
    $('composer-counter').textContent = length > 800 ? length + ' / 1000' : '';
    $('composer-counter').classList.toggle('is-near-limit', length > 950);
    const uploads = snapshot.draft?.attachments || [];
    const failedUpload = uploads.some(upload => upload.status === 'failed' || upload.status === 'cancelled');
    const empty = !length && !uploads.length && !composerCard;
    const submitting = Array.from(inFlightSends.values()).some(send => send.threadId === currentThreadId() && send.signature === draftSignature(currentDraft()));
    const sendButton = $('send-button');
    sendButton.disabled = !editable || length > 1000 || empty || failedUpload || submitting || (editTarget && !snapshot.isAvailable);
    const sendLabel = t(editTarget ? 'save' : submitting ? 'sending' : 'send');
    sendButton.title = sendLabel; sendButton.setAttribute('aria-label', sendLabel);
    sendButton.querySelector('use').setAttribute('href', editTarget ? '#i-check' : '#i-send');
    $('attach-button').hidden = !capable('attachments'); $('attach-button').disabled = !editable || !!editTarget || uploads.length >= 10;
    $('share-game-button').hidden = !capable('cards'); $('share-game-button').disabled = !editable || !!editTarget;
    if (!editable || editTarget || !capable('cards')) closeArmoryPicker();
    $('draft-status').textContent = !editTarget && (composer.value || composerCard) ? t('draftSaved') : '';
    resizeComposer();
    renderComposerContext();
  }
  function renderComposerContext() {
    const visible = !!editTarget || !!replyTarget || !!composerCard;
    $('composer-context').hidden = !visible;
    $('composer-context').querySelector('use').setAttribute('href', editTarget ? '#i-edit' : composerCard && !replyTarget ? '#i-game' : '#i-reply');
    $('composer-context-title').textContent = editTarget ? t('editingMessage') : replyTarget ? t('replyingTo') + ' ' + (replyTarget.sender?.username || replyTarget.senderUsername || '') : t('cardAttached');
    $('composer-context-body').textContent = editTarget ? editTarget.body : replyTarget ? replyTarget.body : composerCard?.title || '';
  }
  function beginReply(message) { if (!capable('replies') || message.deletedAt) return; if (editTarget) cancelContext(); replyTarget = message; saveLocalDraft(); updateComposer(); composer.focus(); }
  function beginEdit(message) {
    if (!capable('edit-delete') || message.sender?.accountId !== snapshot.ownerAccountId || message.deletedAt) return;
    flushDraft(); editBackup = currentDraft(); editTarget = message; composer.value = message.body || ''; replyTarget = null; composerCard = null; resizeComposer(); updateComposer(); composer.focus(); composer.setSelectionRange(composer.value.length, composer.value.length);
  }
  function cancelContext() {
    if (editTarget) { editTarget = null; const backup = editBackup; editBackup = null; setComposerFromDraft(backup || {}); }
    else if (replyTarget) replyTarget = null;
    else composerCard = null;
    saveLocalDraft(); updateComposer(); composer.focus();
  }
  function sendTyping() {
    if (!capable('typing') || !snapshot.state.preferences.shareTyping || !currentThreadId() || !composer.value || !active()) return;
    const now = Date.now();
    if (now - lastTypingAt > 3500) { lastTypingAt = now; typingActive = true; action('typing', { threadId: currentThreadId(), isTyping: true }, { silent: true }); }
    clearTimeout(typingTimer); typingTimer = setTimeout(stopTyping, 5000);
  }
  function stopTyping() { clearTimeout(typingTimer); if (typingActive && currentThreadId()) action('typing', { threadId: currentThreadId(), isTyping: false }, { silent: true }); typingActive = false; lastTypingAt = 0; }
  function renderTyping() {
    const names = (snapshot.typing || []).filter(item => item.threadId === currentThreadId() && item.accountId !== snapshot.ownerAccountId && new Date(item.expiresAt).getTime() > Date.now()).map(item => item.username);
    const host = $('typing-indicator'); host.replaceChildren();
    if (names.length) { const dots = e('span', 'typing-dots'); dots.append(e('i'), e('i'), e('i')); host.append(dots, document.createTextNode(names.join(', ') + ' ' + t(names.length > 1 ? 'typingMany' : 'typingOne'))); }
  }

  function renderUploads() {
    const uploads = snapshot.draft?.attachments || [];
    $('attachment-queue').hidden = !uploads.length || !!editTarget;
    R.reconcileKeyed($('attachment-queue'), uploads, upload => upload.id || upload.localId, upload => {
      const failed = upload.status === 'failed' || !upload.status && (!!upload.error || !!upload.errorCode);
      const complete = upload.isComplete || !!upload.attachment;
      const node = e('div', 'queued-file' + (failed ? ' is-failed' : ''));
      const preview = e('div', 'queued-file-icon');
      const thumbnail = R.mediaUrl(upload.previewUrl || upload.attachment?.thumbnailUrl || (String(upload.contentType).startsWith('image/') && upload.attachment?.url), snapshot.mediaOrigin);
      if (thumbnail) { const image = e('img'); image.src = thumbnail; image.alt = ''; preview.append(image); } else preview.append(R.icon('file'));
      const uploadId = upload.id || upload.localId;
      const remove = R.button('icon-button queued-file-remove', t(complete ? 'removeAttachment' : 'cancelUpload'), 'close', () => action('removeAttachment', { threadId: currentThreadId(), uploadId }));
      const size = Number(upload.size), offset = Number(upload.offset), percent = size > 0 ? Math.min(100, Math.round(offset * 100 / size)) : 0;
      const status = e('div', 'queued-file-status', failed ? t('failed') : complete ? R.formatBytes(upload.size, snapshot.locale) + ' · ' + t('ready') : t(offset ? 'uploading' : 'waiting') + (offset ? ' ' + percent + ' %' : ''));
      node.append(preview, remove, e('div', 'queued-file-name', upload.fileName || upload.attachment?.fileName), status);
      if (!complete && !failed) { const progress = e('div', 'queued-progress'), bar = e('span'); bar.style.width = percent + '%'; progress.append(bar); node.append(progress); }
      if (failed) { const retry = R.button('secondary-button', t('retry'), null, () => action('retryUpload', { threadId: currentThreadId(), uploadId })); retry.textContent = t('retry'); node.append(retry); }
      return node;
    }, upload => JSON.stringify([upload, snapshot.locale]));
  }

  function renderThreadHeader() {
    const exists = !!selectedThread; $('thread-empty').hidden = exists; $('active-thread').hidden = !exists;
    if (!exists) return;
    $('thread-avatar').replaceChildren(R.avatar(threadProfile(selectedThread), { group: selectedThread.kind === 'group', presence: true, mediaOrigin: snapshot.mediaOrigin }));
    $('thread-title').textContent = threadTitle(selectedThread);
    $('thread-presence').textContent = threadPresence(selectedThread);
    const presence = selectedThread.kind === 'group' ? '' : R.presenceStatus(peer(selectedThread));
    $('thread-presence').dataset.presence = presence;
    $('thread-presence').classList.toggle('is-online', presence === 'online');
    $('thread-profile-button').title = selectedThread.kind === 'group' ? t('details') : t('openProfile');
    $('thread-details-button').hidden = selectedThread.kind !== 'group';
    const pinned = (selectedThread.pinnedMessages || []).filter(message => !message.deletedAt);
    $('pinned-strip').hidden = !pinned.length || !capable('pins');
    $('pinned-excerpt').textContent = pinned[0]?.body || pinned[0]?.attachments?.[0]?.fileName || pinned[0]?.card?.title || '';
    $('pinned-count').textContent = pinned.length > 1 ? pinned.length : '';
    const banner = $('connection-banner'); banner.replaceChildren();
    if (selectedThread.isInvited) {
      banner.append(document.createTextNode(t('invitationHint') + ' '));
      for (const name of ['accept', 'decline']) { const response = R.button('secondary-button', t(name), null, () => action('member', { threadId: selectedThread.id, accountId: snapshot.ownerAccountId, action: name })); response.textContent = t(name); banner.append(response); }
    } else if (!snapshot.isAvailable) banner.textContent = t(snapshot.isLoading ? 'loading' : 'disconnected');
    else if (!selectedThread.canSend) banner.textContent = t('accessRevoked');
    banner.hidden = !banner.childNodes.length;
  }

  function applySnapshot(value) {
    if (!value || value.type && value.type !== 'snapshot' || !value.state) return false;
    const next = { ...value, sequence: typeof value.sequence === 'string' ? value.sequence : String(value.sequence || '0'), state: { threads: [], contacts: [], preferences: {}, capabilities: [], ...value.state }, messages: value.messages || [], pending: value.pending || value.outbox || [], typing: value.typing || [] };
    if (!R.id(next.sequence)) return false;
    const identityChanged = sessionKey(next) !== sessionKey(snapshot);
    if (!identityChanged && R.compareIds(next.sequence, String(snapshot.sequence || '0')) < 0) return false;
    const changedThread = identityChanged || next.selectedThreadId !== snapshot.selectedThreadId;
    if (!next.draft) {
      const draft = (next.drafts || []).find(item => item.threadId === next.selectedThreadId) || {};
      next.draft = { ...draft, attachments: (next.uploads || []).filter(item => item.threadId === next.selectedThreadId).map(upload => ({ ...upload, id: upload.id || upload.localId, isComplete: !!upload.attachment })) };
    }
    if (identityChanged) {
      R.suspendMedia(document); localDrafts.clear(); inFlightSends.clear(); requests.clear(); armorySelections.clear(); messageList.replaceChildren();
      clearTimeout(draftTimer); clearTimeout(typingTimer); draftDirty = false; typingActive = false; lastTypingAt = 0; lastReadKey = ''; lastComposerStateKey = ''; unreadBoundary = null;
      closeMenus(); closeDialog(); closeArmoryPicker(); armoryRequestPending = false; armorySelection = null; armoryError = ''; editTarget = null; editBackup = null; replyTarget = null; composerCard = null; composer.value = '';
      $('armory-character-list').replaceChildren(); $('armory-picker-status').replaceChildren(); $('armory-picker-status')._signature = null;
    }
    if (changedThread) { R.suspendMedia(messageList); messageList.replaceChildren(); editTarget = null; editBackup = null; replyTarget = null; composerCard = null; stableAnchor = null; lastReadKey = ''; closeMenus(); closeArmoryPicker(); armorySelection = null; dragDepth = 0; nativeDropActive = false; }
    const oldLocale = snapshot.locale;
    snapshot = next;
    selectedThread = (next.state.threads || []).find(thread => thread.id === next.selectedThreadId) || null;
    if (oldLocale !== next.locale || identityChanged) localize();
    if (changedThread) unreadBoundary = selectedThread && selectedThread.unreadCount > 0 ? R.id(selectedThread.lastReadMessageId) || '0' : null;
    for (const message of next.messages) if (message.clientMessageId) completeLocalSend(message.clientMessageId);
    for (const pending of next.pending) if (pending.clientMessageId) completeLocalSend(pending.clientMessageId);
    if (selectedThread && !editTarget) {
      const local = localDrafts.get(selectedThread.id);
      if (local && draftSignature(local) === draftSignature(next.draft)) localDrafts.delete(selectedThread.id);
      if (changedThread || !local) setComposerFromDraft(local || next.draft);
    }
    if (!selectedThread) { R.suspendMedia(messageList); messageList.replaceChildren(); if (next.selectedThreadId) localDrafts.delete(next.selectedThreadId); composer.value = ''; replyTarget = null; composerCard = null; }
    if (replyTarget && next.messages.some(message => message.id === (replyTarget.id || replyTarget.messageId) && message.deletedAt)) { replyTarget = null; saveLocalDraft(); }
    renderConversations(); renderThreadHeader();
    if (selectedThread) renderTimeline(changedThread);
    renderUploads(); updateComposer(); renderTyping(); renderArmoryPicker(); renderDropOverlay();
    if (snapshot.error || snapshot.errorCode) showComposerError(errorText(snapshot.error || snapshot.errorCode));
    if (!active()) { lastReadKey = ''; R.suspendMedia(document); stopTyping(); }
    if (dialogRefresh) dialogRefresh();
    return true;
  }

  function closeMenus() {
    $('context-menu').hidden = true; $('reaction-picker').hidden = true;
    if ($('context-menu').parentElement !== document.body) document.body.append($('context-menu'));
    if ($('reaction-picker').parentElement !== document.body) document.body.append($('reaction-picker'));
    document.querySelectorAll('.message.has-menu').forEach(node => node.classList.remove('has-menu'));
  }
  function positionFloating(node, x, y) {
    node.hidden = false;
    const width = node.offsetWidth, height = node.offsetHeight;
    node.style.left = Math.max(9, Math.min(x - width, innerWidth - width - 9)) + 'px';
    node.style.top = Math.max(9, Math.min(y + 4, innerHeight - height - 9)) + 'px';
  }
  function showMenu(items, x, y) {
    closeMenus(); menuReturnFocus = document.activeElement;
    const menu = $('context-menu'); menu.replaceChildren();
    if ($('app-dialog').open) $('app-dialog').append(menu);
    for (const item of items) {
      if (item === null) { const line = e('div', 'menu-separator'); line.setAttribute('role', 'separator'); menu.append(line); continue; }
      const choice = R.button('menu-item' + (item.danger ? ' is-danger' : ''), item.label, item.icon, () => { closeMenus(); item.run(); });
      choice.setAttribute('role', 'menuitem'); choice.append(e('span', '', item.label)); menu.append(choice);
    }
    positionFloating(menu, x, y); menu.querySelector('button')?.focus({ preventScroll: true });
  }
  function showThreadMenu(thread, x, y) {
    const items = [];
    if (capable('pins')) items.push({ label: t(thread.isPinned ? 'unpinConversation' : 'pinConversation'), icon: 'pin', run: () => action('threadSelf', { threadId: thread.id, isPinned: !thread.isPinned }) });
    if (thread.kind === 'group') items.push({ label: t('details'), icon: 'group', run: () => showDetails(thread) });
    else if (peer(thread)) items.push({ label: t('openProfile'), icon: 'external', run: () => action('openProfile', { accountId: peer(thread).accountId }) });
    if (items.length) showMenu(items, x, y);
  }
  function showMessageMenu(message, x, y) {
    const items = [];
    if (!message.deletedAt) {
      items.push({ label: t('copy'), icon: 'copy', run: () => copyText(message.body) });
      if (capable('replies')) items.push({ label: t('reply'), icon: 'reply', run: () => beginReply(message) });
      if (capable('reactions')) items.push({ label: t('react'), icon: 'smile', run: () => showReactions(message, { getBoundingClientRect: () => ({ right: x, bottom: y }) }) });
      if (capable('pins')) items.push({ label: t(message.isPinned ? 'unpinMessage' : 'pinMessage'), icon: 'pin', run: () => action('pinMessage', { threadId: message.threadId, messageId: message.id, pinned: !message.isPinned }) });
      if (message.sender?.accountId === snapshot.ownerAccountId && capable('edit-delete')) {
        items.push(null, { label: t('edit'), icon: 'edit', run: () => beginEdit(message) }, { label: t('delete'), icon: 'trash', danger: true, run: () => confirmAction(t('deleteMessage'), t('deleteMessageHint'), t('delete'), () => action('deleteMessage', { threadId: message.threadId, messageId: message.id })) });
      }
    }
    if (!items.length) return;
    showMenu(items, x, y);
    Array.from(messageList.children).find(node => node.dataset.messageId === message.id)?.classList.add('has-menu');
  }
  function showReactions(message, anchor) {
    if (!capable('reactions') || message.deletedAt) return;
    closeMenus(); menuReturnFocus = document.activeElement;
    const picker = $('reaction-picker'); picker.replaceChildren();
    for (const emoji of ['👍', '❤️', '😂', '🎉', '🔥', '👀', '✅', '🙏', '😮', '😢', '💪', '⚔️', '🛡️', '✨', '👋', '🤔', '💀', '💯']) {
      const mine = message.reactions?.some(reaction => reaction.emoji === emoji && reaction.accountIds?.includes(snapshot.ownerAccountId));
      const choice = R.button('', t(mine ? 'removeReaction' : 'addReaction') + ' ' + emoji, null, () => { closeMenus(); action('reaction', { threadId: message.threadId, messageId: message.id, emoji, active: !mine }); });
      choice.textContent = emoji; picker.append(choice);
    }
    const rect = anchor.getBoundingClientRect(); positionFloating(picker, rect.right, rect.bottom); picker.firstElementChild?.focus({ preventScroll: true });
  }
  async function copyText(text) {
    try { await navigator.clipboard.writeText(String(text || '')); toast(t('copied')); }
    catch (_) {
      const selected = document.getSelection(), ranges = [];
      if (selected) for (let i = 0; i < selected.rangeCount; i++) ranges.push(selected.getRangeAt(i));
      const temporary = e('textarea'); temporary.value = String(text || ''); temporary.style.position = 'fixed'; temporary.style.opacity = '0'; document.body.append(temporary); temporary.select();
      let success = false; try { success = document.execCommand('copy'); } catch (_) {} temporary.remove();
      if (selected) { selected.removeAllRanges(); for (const range of ranges) selected.addRange(range); }
      toast(t(success ? 'copied' : 'copyFailed'));
    }
  }
  function jumpTo(messageId) {
    const node = Array.from(messageList.children).find(item => item.dataset.messageId === messageId);
    if (!node) { toast(t('messageNotLoaded')); return; }
    closeDialog(); followBottom = false; node.scrollIntoView({ block: 'center', behavior: 'auto' }); node.classList.remove('flash'); requestAnimationFrame(() => node.classList.add('flash')); node.focus({ preventScroll: true }); stableAnchor = captureAnchor(); updateJumpButton();
  }
  function dismissPreview(message, preview) {
    if (R.isVideoPreview(preview) || preview.canRemove === false || !capable('link-previews')) return;
    action('dismissPreview', { threadId: message.threadId, messageId: message.id, previewId: preview.id }, { result: (_, error) => { if (error) toast(errorText(error)); } });
  }

  function openDialog(title, subtitle) {
    closeMenus(); dialogRefresh = null; $('dialog-title').textContent = title; $('dialog-subtitle').textContent = subtitle || ''; $('dialog-content').replaceChildren(); $('dialog-footer').replaceChildren();
    if (!$('app-dialog').open) $('app-dialog').showModal();
    return { content: $('dialog-content'), footer: $('dialog-footer') };
  }
  function closeDialog() { dialogRefresh = null; if ($('app-dialog').open) $('app-dialog').close(); }
  function footerButton(footer, label, handler, primary, danger) { const node = R.button((primary ? 'primary-button' : 'secondary-button') + (danger ? ' danger-button' : ''), label, null, handler); node.textContent = label; footer.append(node); return node; }
  function confirmAction(title, hint, label, run) { const dialog = openDialog(title, hint); footerButton(dialog.footer, t('cancel'), closeDialog); footerButton(dialog.footer, label, () => { closeDialog(); run(); }, true, true); }
  function searchInput(placeholder) { const label = e('label', 'search-field'); label.append(R.icon('search')); const input = e('input'); input.type = 'search'; input.placeholder = placeholder; input.setAttribute('aria-label', placeholder); label.append(input); return { label, input }; }
  function formField(label, placeholder, tag, type) { const wrapper = e('label', 'dialog-label', label), input = e(tag || 'input', 'dialog-input' + (tag === 'textarea' ? ' dialog-textarea' : '')); if (type) input.type = type; input.placeholder = placeholder || ''; wrapper.append(input); return { wrapper, input }; }
  function openDirect(accountId) {
    const existing = snapshot.state.threads.find(thread => thread.kind === 'direct' && peer(thread)?.accountId === accountId);
    closeDialog();
    if (existing) selectThread(existing.id);
    else action('createThread', { requestId: uuid(), isGroup: false, title: '', participantAccountIds: [accountId] });
  }
  function showNewConversation(inviteThread) {
    const dialog = openDialog(t(inviteThread ? 'inviteMembers' : 'newConversation'), t(inviteThread ? 'selectFriends' : 'newConversationHint'));
    let group = !!inviteThread; const selected = new Set();
    const tabs = e('div', 'dialog-tabs'); const direct = R.button('is-active', t('direct'), null, () => setGroup(false)); direct.textContent = t('direct'); const multiple = R.button('', t('group'), null, () => setGroup(true)); multiple.textContent = t('group'); tabs.append(direct, multiple);
    if (!inviteThread && capable('groups')) dialog.content.append(tabs);
    const title = formField(t('groupName'), t('groupNamePlaceholder')); title.input.maxLength = 80; title.wrapper.hidden = !group || !!inviteThread;
    const search = searchInput(t('searchContacts')), list = e('div', 'contact-picker-list'); dialog.content.append(title.wrapper, search.label, list);
    const count = e('div', 'dialog-helper'); dialog.content.append(count);
    footerButton(dialog.footer, t('cancel'), closeDialog);
    const submit = footerButton(dialog.footer, t(inviteThread ? 'invite' : 'createGroup'), () => {
      if (!selected.size || !group || !inviteThread && !title.input.value.trim()) return;
      if (inviteThread) for (const accountId of selected) action('member', { threadId: inviteThread.id, accountId, action: 'invite' });
      else action('createThread', { requestId: uuid(), isGroup: true, title: title.input.value.trim(), participantAccountIds: Array.from(selected) });
      closeDialog();
    }, true); submit.hidden = !group; submit.disabled = true;
    function setGroup(value) { group = value; selected.clear(); direct.classList.toggle('is-active', !group); multiple.classList.toggle('is-active', group); title.wrapper.hidden = !group; submit.hidden = !group; renderList(); }
    function renderList() {
      list.replaceChildren(); const query = search.input.value.trim().toLocaleLowerCase();
      const contacts = snapshot.state.contacts.filter(contact => contact.accountId !== snapshot.ownerAccountId && (!inviteThread || !inviteThread.members.some(member => member.profile.accountId === contact.accountId && member.status !== 'left')) && (!query || [contact.username, contact.characterName].some(value => String(value || '').toLocaleLowerCase().includes(query))));
      for (const contact of contacts) {
        const row = R.button('contact-picker-row' + (selected.has(contact.accountId) ? ' is-selected' : ''), contact.username, null, () => {
          if (!group) { openDirect(contact.accountId); return; }
          if (selected.has(contact.accountId)) selected.delete(contact.accountId); else if (selected.size < 49) selected.add(contact.accountId); renderList();
        });
        row.setAttribute('aria-pressed', String(selected.has(contact.accountId))); row.append(R.avatar(contact, { small: true, presence: true, mediaOrigin: snapshot.mediaOrigin }));
        const text = e('span', 'contact-picker-text'); text.append(e('strong', '', contact.username), e('span', '', R.presenceText(contact, t))); row.append(text);
        if (group) { const check = e('span', 'contact-check'); if (selected.has(contact.accountId)) check.append(R.icon('check')); row.append(check); }
        list.append(row);
      }
      if (!contacts.length) list.append(e('p', 'dialog-helper', t(snapshot.state.contacts.length ? 'noResults' : 'noFriends')));
      count.textContent = group && selected.size ? selected.size + ' / 49 ' + t('members') : '';
      submit.disabled = !selected.size || !inviteThread && !title.input.value.trim();
    }
    search.input.addEventListener('input', renderList); title.input.addEventListener('input', renderList); renderList(); search.input.focus();
  }

  function showPinned() {
    const dialog = openDialog(t('pinnedMessages'), '');
    for (const message of (selectedThread?.pinnedMessages || []).filter(item => !item.deletedAt)) { const row = R.button('pinned-dialog-message', t('goToMessage'), null, () => jumpTo(message.id)); row.append(e('strong', '', resolveProfile(message.sender)?.username), e('span', '', message.body || message.card?.title || message.attachments?.map(item => item.fileName).join(', ')), e('small', '', R.fullDate(message.createdAt, snapshot.locale))); dialog.content.append(row); }
    if (!dialog.content.childElementCount) dialog.content.append(e('p', 'dialog-helper', t('noPinned')));
    footerButton(dialog.footer, t('close'), closeDialog);
  }
  function showDetails(thread) {
    thread = snapshot.state.threads.find(item => item.id === thread.id) || thread;
    const dialog = openDialog(threadTitle(thread), threadPresence(thread));
    if (thread.kind !== 'group') { footerButton(dialog.footer, t('openProfile'), () => { closeDialog(); action('openProfile', { accountId: peer(thread).accountId }); }, true); return; }
    if (thread.canManage) {
      const title = formField(t('groupName')); title.input.value = thread.title; title.input.maxLength = 80; dialog.content.append(title.wrapper);
      footerButton(dialog.footer, t('save'), () => { action('threadUpdate', { threadId: thread.id, title: title.input.value.trim(), expectedVersion: thread.version }); closeDialog(); }, true);
      const changeImage = R.button('secondary-button', t('chooseGroupImage'), 'plus', () => showGroupImage(thread)); changeImage.append(document.createTextNode(' ' + t('chooseGroupImage'))); dialog.content.append(changeImage);
      const invite = R.button('secondary-button', t('inviteMembers'), 'group', () => showNewConversation(thread)); invite.append(document.createTextNode(' ' + t('inviteMembers'))); dialog.content.append(invite);
    }
    dialog.content.append(e('h3', 'dialog-helper', t('activeMembers')));
    for (const member of thread.members || []) {
      const row = e('div', 'details-member'); row.append(R.avatar(resolveProfile(member.profile), { small: true, presence: member.status === 'active', mediaOrigin: snapshot.mediaOrigin }));
      row.append(e('span', '', member.profile.username + (member.profile.accountId === snapshot.ownerAccountId ? ' · ' + t('self') : '')));
      if (member.status === 'invited') row.append(e('small', '', t('invited'))); else if (member.role === 'owner' || member.role === 'admin') row.append(e('small', '', t(member.role)));
      if (thread.canManage && member.profile.accountId !== snapshot.ownerAccountId && member.role !== 'owner') {
        row.append(R.button('icon-button', t('conversationActions'), 'more', event => { const rect = event.currentTarget.getBoundingClientRect(); showMenu([{ label: t(member.role === 'admin' ? 'makeMember' : 'makeAdmin'), icon: 'group', run: () => action('member', { threadId: thread.id, accountId: member.profile.accountId, action: 'role', role: member.role === 'admin' ? 'member' : 'admin' }) }, { label: t('removeMember'), icon: 'trash', danger: true, run: () => confirmAction(t('removeMember') + ' · ' + member.profile.username, '', t('delete'), () => action('member', { threadId: thread.id, accountId: member.profile.accountId, action: 'remove' })) }], rect.right, rect.bottom); }));
      }
      dialog.content.append(row);
    }
    const leave = R.button('secondary-button danger-button', t('leaveGroup'), null, () => confirmAction(t('leaveGroup') + ' ?', t('leaveGroupHint'), t('leaveGroup'), () => action('member', { threadId: thread.id, accountId: snapshot.ownerAccountId, action: 'leave' }))); leave.textContent = t('leaveGroup'); dialog.content.append(leave);
    footerButton(dialog.footer, t('close'), closeDialog);
  }
  function showGroupImage(thread) {
    const dialog = openDialog(t('groupImage'), t('groupImageHint'));
    const pick = R.button('secondary-button', t('chooseGroupImage'), 'plus', () => action('pickFiles', { threadId: thread.id })); pick.append(document.createTextNode(' ' + t('chooseGroupImage'))); dialog.content.append(pick);
    const images = e('div', 'contact-picker-list'); dialog.content.append(images);
    dialogRefresh = () => {
      images.replaceChildren();
      if (thread.id !== currentThreadId()) return;
      for (const upload of snapshot.draft?.attachments || []) if (upload.attachment && String(upload.attachment.contentType).startsWith('image/')) {
        const row = R.button('contact-picker-row', t('useGroupImage'), null, () => { action('threadUpdate', { threadId: thread.id, avatarAttachmentId: upload.attachment.id, expectedVersion: thread.version }, { result: (_, error) => { if (error) toast(errorText(error)); else { action('removeAttachment', { threadId: thread.id, uploadId: upload.id }); closeDialog(); } } }); });
        row.append(R.avatar({ username: upload.fileName, avatarUrl: upload.attachment.url }, { small: true, mediaOrigin: snapshot.mediaOrigin }), e('span', '', upload.fileName)); images.append(row);
      }
    }; dialogRefresh(); footerButton(dialog.footer, t('close'), closeDialog);
  }
  function viewImage(attachment) {
    const url = R.mediaUrl(attachment.url, snapshot.mediaOrigin) || (attachment.id ? snapshot.mediaOrigin + 'attachments/' + encodeURIComponent(attachment.id) : ''); if (!url) return;
    const dialog = openDialog(attachment.fileName || t('image'), R.formatBytes(attachment.size, snapshot.locale));
    const image = e('img', 'image-viewer'); image.src = url; image.alt = attachment.fileName || t('image'); dialog.content.append(image);
    footerButton(dialog.footer, t('download'), () => action('downloadAttachment', { attachmentId: attachment.id, fileName: attachment.fileName })); footerButton(dialog.footer, t('close'), closeDialog);
  }
  function closeArmoryPicker(restoreFocus) {
    armoryPickerOpen = false; armoryPickerFocusPending = false;
    $('armory-picker').hidden = true; $('share-game-button').setAttribute('aria-expanded', 'false');
    if (restoreFocus) $('share-game-button').focus({ preventScroll: true });
  }
  function characterClass(character) {
    const names = { 1: 'warrior', 2: 'paladin', 3: 'hunter', 4: 'rogue', 5: 'priest', 6: 'deathKnight', 7: 'shaman', 8: 'mage', 9: 'warlock', 11: 'druid' };
    return names[character.classId] ? t(names[character.classId]) : t('character');
  }
  function requestOwnCharacters() {
    if (armoryRequestPending || !hasIdentity()) return;
    armoryRequestPending = true; armoryError = ''; renderArmoryPicker();
    const requestId = action('requestOwnCharacters', {}, { result: (_, error) => {
      armoryRequestPending = false; if (error) armoryError = errorText(error);
      renderArmoryPicker();
    } });
    if (!requestId) { armoryRequestPending = false; armoryError = t('charactersUnavailable'); renderArmoryPicker(); }
  }
  function showCardComposer() {
    if (!capable('cards') || !selectedThread?.canSend || editTarget) return;
    if (armoryPickerOpen) { closeArmoryPicker(); return; }
    closeMenus(); armoryPickerOpen = true; armoryPickerFocusPending = true; armoryError = '';
    renderArmoryPicker(); requestOwnCharacters();
  }
  function selectOwnCharacter(character) {
    const characterGuid = R.id(character.guid);
    if (!characterGuid || characterGuid === '0' || armorySelection || !selectedThread?.canSend || editTarget) return;
    const threadId = currentThreadId();
    const selection = { threadId, characterGuid };
    flushDraft(); armoryError = ''; armorySelection = selection; armorySelections.set(threadId, selection); renderArmoryPicker();
    const requestId = action('selectOwnCharacter', { threadId, characterGuid }, { result: (payload, error) => {
      if (armorySelections.get(threadId) !== selection) return;
      armorySelections.delete(threadId);
      if (armorySelection === selection) armorySelection = null;
      const card = payload?.card;
      if (error || !card || card.kind !== 'character' || String(card.fields?.characterGuid || card.referenceId || '') !== characterGuid
        || String(card.fields?.ownerAccountId || '') !== String(snapshot.ownerAccountId)) {
        if (threadId === currentThreadId()) { armoryError = errorText(error) || t('charactersUnavailable'); renderArmoryPicker(); }
        return;
      }
      const ownDraft = localDrafts.get(threadId);
      if (ownDraft) localDrafts.set(threadId, { ...ownDraft, card });
      if (threadId !== currentThreadId() || editTarget) return;
      composerCard = card; closeArmoryPicker(); saveLocalDraft(); updateComposer(); composer.focus();
    } });
    if (!requestId) { armorySelections.delete(threadId); armorySelection = null; armoryError = t('charactersUnavailable'); renderArmoryPicker(); }
  }
  function renderArmoryPicker() {
    const panel = $('armory-picker'); panel.hidden = !armoryPickerOpen;
    $('share-game-button').setAttribute('aria-expanded', String(armoryPickerOpen));
    if (!armoryPickerOpen) return;
    const roster = snapshot.ownCharacters || { status: 'idle', characters: [] };
    const loading = armoryRequestPending || roster.status === 'loading' || roster.status === 'idle';
    const choosing = armorySelection?.threadId === currentThreadId();
    const error = armoryError || (roster.status === 'error' ? t('charactersUnavailable') : '');
    const status = $('armory-picker-status'), list = $('armory-character-list');
    const characters = (roster.characters || []).filter(character => R.id(character.guid) && character.guid !== '0');
    panel.setAttribute('aria-busy', String(loading || !!choosing));
    const statusKey = JSON.stringify([loading, choosing && armorySelection.characterGuid, error, characters.length, snapshot.locale]);
    if (status._signature !== statusKey) {
      status._signature = statusKey; status.replaceChildren();
      if (error) {
        status.append(e('span', '', error));
        const retry = R.button('armory-retry', t('retry'), null, requestOwnCharacters); retry.textContent = t('retry'); status.append(retry);
      } else if (choosing) status.append(e('span', '', t('addingArmory')));
      else if (loading) status.append(e('span', '', t('loadingCharacters')));
      else if (!characters.length) status.append(e('span', '', t('noCharacters')));
      status.hidden = !status.childElementCount;
    }
    R.reconcileKeyed(list, characters, character => character.guid, character => {
      const row = e('div', 'armory-character-row'); row.setAttribute('role', 'listitem');
      const name = String(character.name || t('character'));
      const choice = R.button('armory-character', t('shareCharacter') + ' ' + name, null, () => selectOwnCharacter(character));
      choice.dataset.characterGuid = character.guid; choice.dataset.classId = String(character.classId || '');
      choice.disabled = loading || !!choosing || !!error;
      const badge = e('span', 'armory-character-emblem', R.initials(name)); badge.setAttribute('aria-hidden', 'true');
      const text = e('span', 'armory-character-text');
      const details = [Number(character.level) > 0 ? t('level') + ' ' + character.level : '', characterClass(character), character.realmName || ''].filter(Boolean);
      text.append(e('strong', '', name), e('small', '', details.join(' · ')));
      choice.append(badge, text, R.icon('plus')); row.append(choice); return row;
    }, character => JSON.stringify([character, loading, !!choosing, error, snapshot.locale]));
    if (armoryPickerFocusPending && !loading && list.querySelector('button:not(:disabled)')
      && (document.activeElement === $('share-game-button') || panel.contains(document.activeElement))) {
      armoryPickerFocusPending = false; list.querySelector('button:not(:disabled)').focus({ preventScroll: true });
    }
  }

  $('new-conversation-button').addEventListener('click', () => showNewConversation()); $('empty-new-button').addEventListener('click', () => showNewConversation());
  $('thread-profile-button').addEventListener('click', () => { if (!selectedThread) return; if (selectedThread.kind === 'group') showDetails(selectedThread); else if (peer(selectedThread)) action('openProfile', { accountId: peer(selectedThread).accountId }); });
  $('thread-details-button').addEventListener('click', () => { if (selectedThread) showDetails(selectedThread); });
  $('thread-menu-button').addEventListener('click', event => { if (!selectedThread) return; const rect = event.currentTarget.getBoundingClientRect(); showThreadMenu(selectedThread, rect.right, rect.bottom); });
  $('pinned-strip').addEventListener('click', showPinned);
  $('load-earlier-button').addEventListener('click', () => { const first = snapshot.messages.filter(message => R.id(message.id)).sort((a, b) => R.compareIds(a.id, b.id))[0]; if (selectedThread && first) action('loadEarlier', { threadId: selectedThread.id, beforeId: first.id }); });
  $('jump-latest-button').addEventListener('click', () => { scrollBottom(); updateJumpButton(); requestAnimationFrame(requestRead); });
  $('send-button').addEventListener('click', sendMessage); $('cancel-context-button').addEventListener('click', cancelContext);
  $('attach-button').addEventListener('click', () => { if (currentThreadId()) action('pickFiles', { threadId: currentThreadId() }); });
  $('share-game-button').addEventListener('click', showCardComposer); $('close-armory-picker').addEventListener('click', () => closeArmoryPicker(true));
  composer.addEventListener('input', () => { resizeComposer(); saveLocalDraft(); updateComposer(); showComposerError(''); sendTyping(); });
  composer.addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && !event.isComposing && event.keyCode !== 229) { event.preventDefault(); sendMessage(); } });
  composer.addEventListener('paste', event => {
    if (!capable('attachments') || !currentThreadId() || editTarget) return;
    const items = Array.from(event.clipboardData?.items || []);
    if (items.some(item => item.kind === 'file')) { event.preventDefault(); action('pasteImage', { threadId: currentThreadId() }); }
  });
  let dragDepth = 0, nativeDropActive = false;
  function canReceiveFiles() { return !!currentThreadId() && hasIdentity() && capable('attachments') && !!selectedThread?.canSend && !editTarget; }
  function renderDropOverlay() { $('drop-overlay').hidden = !canReceiveFiles() || !(nativeDropActive || dragDepth > 0); }
  function receiveMessage(message) {
    if (message?.type === 'result') return handleResult(message);
    if (message?.type === 'dropState') {
      if (!hasIdentity() || message.sessionId !== snapshot.sessionId || message.ownerAccountId !== snapshot.ownerAccountId) return false;
      nativeDropActive = !!message.active && canReceiveFiles(); renderDropOverlay(); return true;
    }
    return applySnapshot(message);
  }
  document.addEventListener('dragenter', event => {
    if (!canReceiveFiles() || !Array.from(event.dataTransfer?.types || []).includes('Files')) return;
    event.preventDefault(); dragDepth++; renderDropOverlay();
  });
  document.addEventListener('dragover', event => { if (dragDepth) { event.preventDefault(); if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy'; } });
  document.addEventListener('dragleave', event => { if (!dragDepth) return; event.preventDefault(); dragDepth = Math.max(0, dragDepth - 1); renderDropOverlay(); });
  document.addEventListener('drop', event => {
    dragDepth = 0; nativeDropActive = false; renderDropOverlay();
    const files = Array.from(event.dataTransfer?.files || []); if (!files.length) return;
    event.preventDefault(); if (!canReceiveFiles()) return;
    if (files.some(file => !fileExtensions.has(String(file.name).split('.').at(-1).toLocaleLowerCase()))) { toast(t('attachmentHint')); return; }
    if (files.some(file => file.size > 500000000)) { toast(t('attachmentHint')); return; }
    if (files.length + (snapshot.draft?.attachments || []).length > 10) { toast(t('attachmentLimit')); return; }
    if (!action('dropFiles', { threadId: currentThreadId() }, { files, silent: true })) toast(t('dropUnavailable'));
  });
  timeline.addEventListener('scroll', () => {
    if (!programmaticScroll && !layoutPending) { followBottom = isAtBottom(36); stableAnchor = followBottom ? null : captureAnchor(); lastUserScroll = performance.now(); }
    updateJumpButton(); requestAnimationFrame(requestRead);
  }, { passive: true });
  timeline.addEventListener('wheel', () => { if (layoutPending) { layoutPending = false; ++renderVersion; } programmaticScroll = false; }, { passive: true });
  if (global.ResizeObserver) new ResizeObserver(() => {
    if (!selectedThread || layoutPending || timeline.clientHeight <= 0) return;
    if (followBottom) scrollBottom(); else if (stableAnchor && performance.now() - lastUserScroll > 40) restoreAnchor(stableAnchor);
    updateJumpButton(); requestAnimationFrame(requestRead);
  }).observe(messageList);
  global.addEventListener('resize', () => { if (selectedThread) settleLayout(stableAnchor, followBottom); resizeComposer(); });
  document.addEventListener('click', event => {
    const link = event.target.closest('a[data-external-link]');
    if (link) { event.preventDefault(); const url = R.safeUrl(link.getAttribute('href')); if (url) action('openExternal', { url }); }
    if (!event.target.closest('#context-menu, #reaction-picker, .message-actions, #thread-menu-button, .details-member')) closeMenus();
  });
  document.addEventListener('keydown', event => {
    const floating = !$('reaction-picker').hidden ? $('reaction-picker') : !$('context-menu').hidden ? $('context-menu') : null;
    if (event.key === 'Escape') { if (floating) { event.preventDefault(); closeMenus(); menuReturnFocus?.focus({ preventScroll: true }); } else if (armoryPickerOpen) { event.preventDefault(); closeArmoryPicker(true); } else if (editTarget || replyTarget) { event.preventDefault(); cancelContext(); } }
    if (floating && ['ArrowDown', 'ArrowUp', 'ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
      event.preventDefault(); const buttons = Array.from(floating.querySelectorAll('button')), index = buttons.indexOf(document.activeElement);
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : (index + (event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 1) + buttons.length) % buttons.length;
      buttons[next]?.focus();
    }
    if ((event.key === 'ContextMenu' || event.key === 'F10' && event.shiftKey) && document.activeElement?.classList.contains('message')) { event.preventDefault(); const node = document.activeElement, rect = node.getBoundingClientRect(); showMessageMenu(node._message, rect.right - 25, rect.top + 15); }
  });
  $('app-dialog').addEventListener('close', () => { dialogRefresh = null; closeMenus(); });
  $('app-dialog').addEventListener('click', event => { if (event.target === $('app-dialog')) { const rect = $('app-dialog').getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) closeDialog(); } });
  document.addEventListener('visibilitychange', () => { if (document.hidden) { flushDraft(); stopTyping(); R.suspendMedia(document); dragDepth = 0; nativeDropActive = false; renderDropOverlay(); } else requestAnimationFrame(requestRead); });
  global.addEventListener('pagehide', () => { flushDraft(); stopTyping(); R.suspendMedia(document); });
  setInterval(renderTyping, 2000);
  if (global.chrome?.webview) chrome.webview.addEventListener('message', event => receiveMessage(event.data));
  global.AtlasChat = Object.freeze({ applySnapshot, receive: receiveMessage, version: 2 });
  localize(); renderConversations(); updateComposer(); action('ready', {}, { silent: true });
})(window);
