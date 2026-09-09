(function (global) {
  'use strict';
  const R = global.AtlasChatRender, M = global.AtlasChatMedia;
  if (!R) return;
  const $ = name => document.getElementById(name);
  const e = R.element;
  const defaultFileExtensions = ['png','jpg','jpeg','gif','webp','pdf','txt','md','docx','xlsx','pptx','odt','ods','odp','mp3','ogg','wav','mp4','webm'];
  let fileExtensions = new Set(defaultFileExtensions);
  const strings = {
    mediaPlaybackUnavailable:['Lecture intégrée indisponible.','In-app playback unavailable.'],
    saveAs:['Enregistrer sous…','Save as…'],
    viewMedia:['Agrandir l’aperçu','Expand preview'],
    mediaOpened:['Aperçu ouvert','Preview open'],
    deletingMessage:['Suppression en cours…','Deleting message…'],
    subtitle:['Gardez le contact, en jeu comme ailleurs.','Keep in touch, in game and beyond.'], conversations:['Conversations','Conversations'], unread:['Non lus','Unread'], searchContacts:['Rechercher un contact…','Find a contact…'], newConversation:['Nouvelle conversation','New conversation'], chooseConversation:['Une conversation commence ici','A conversation starts here'], chooseConversationHint:['Retrouvez vos amis ou créez un groupe pour votre prochaine aventure.','Catch up with friends or make a group for your next adventure.'], dnd:['Ne pas déranger','Do not disturb'], online:['En ligne','Online'], offline:['Hors ligne','Offline'], inGame:['En jeu','In game'], playingOn:['En jeu sur','Playing as'], today:['Aujourd’hui','Today'], yesterday:['Hier','Yesterday'], noConversations:['Vos conversations apparaîtront ici.','Your conversations will appear here.'], noResults:['Aucun contact ne correspond à votre recherche.','No contacts match your search.'], pinned:['Épinglées','Pinned'], recent:['Récentes','Recent'], contacts:['Contacts','Contacts'], invitation:['Invitation à un groupe','Group invitation'], invitations:['Invitations','Invitations'], newMessages:['Nouveaux messages','New messages'], loadEarlier:['Afficher les messages précédents','Show earlier messages'], loading:['Chargement…','Loading…'], noMessages:['Écrivez le premier message.','Write the first message.'], details:['Participants et détails','Members and details'], conversationActions:['Actions de la conversation','Conversation actions'], pinnedMessages:['Messages épinglés','Pinned messages'], pinnedMessage:['Message épinglé','Pinned message'], noPinned:['Aucun message épinglé.','No pinned messages.'], composerPlaceholder:['Écrire un message…','Write a message…'], composerName:['Texte du message','Message text'], composerHint:['Entrée pour envoyer · Maj + Entrée pour une nouvelle ligne','Enter to send · Shift + Enter for a new line'], send:['Envoyer','Send'], sending:['Envoi…','Sending…'], save:['Enregistrer','Save'], cancel:['Annuler','Cancel'], close:['Fermer','Close'], draftLabel:['Brouillon','Draft'], reply:['Répondre','Reply'], replyingTo:['Réponse à','Replying to'], editingMessage:['Modification du message','Editing message'], edited:['modifié','edited'], edit:['Modifier','Edit'], delete:['Supprimer','Delete'], deleteMessage:['Supprimer ce message ?','Delete this message?'], deleteMessageHint:['Il sera retiré de l’historique du launcher pour tous les participants. Un texte déjà affiché en jeu ne peut pas être effacé.','It will be removed from the launcher history for everyone. Text already shown in game cannot be erased.'], copy:['Copier le texte','Copy text'], copied:['Texte copié','Text copied'], copyFailed:['Impossible de copier le texte.','Could not copy the text.'], react:['Ajouter une réaction','Add a reaction'], addReaction:['Ajouter la réaction','Add reaction'], removeReaction:['Retirer ma réaction','Remove my reaction'], messageActions:['Actions du message','Message actions'], read:['Lu','Read'], pinMessage:['Épingler le message','Pin message'], unpinMessage:['Désépingler le message','Unpin message'], pinConversation:['Épingler la conversation','Pin conversation'], unpinConversation:['Désépingler la conversation','Unpin conversation'], goToMessage:['Revenir au message','Go to message'], messageNotLoaded:['Ce message se trouve plus haut dans la conversation.','This message is further up in the conversation.'], revealSpoiler:['Afficher le texte masqué','Reveal spoiler'], hideSpoiler:['Masquer le texte','Hide spoiler'], attachFiles:['Ajouter des fichiers','Add files'], attachmentHint:['Images, GIF, documents, audio et vidéo · 500 Mo par fichier','Images, GIFs, documents, audio and video · 500 MB per file'], dropFiles:['Déposez vos fichiers ici','Drop your files here'], dropUnavailable:['Utilisez le bouton + pour ajouter ces fichiers.','Use the + button to add these files.'], shareGame:['Partager l’Armory d’un personnage','Share a character’s Armory'], download:['Télécharger','Download'], viewImage:['Afficher l’image','View image'], image:['Image','Image'], imageUnavailable:['Image indisponible','Image unavailable'], video:['Vidéo','Video'], audio:['Audio','Audio'], playVideo:['Lire la vidéo','Play video'], openLink:['Ouvrir le lien','Open link'], openOnSite:['Ouvrir sur le site','Open on website'], removePreviewForEveryone:['Retirer cet aperçu pour tous les participants','Remove this preview for everyone'], previewRemoved:['Aperçu retiré pour tous les participants.','Preview removed for everyone.'], waiting:['En attente','Waiting'], uploading:['Transfert','Uploading'], processing:['Préparation','Preparing'], ready:['Prêt','Ready'], failed:['Échec','Failed'], retry:['Réessayer','Retry'], retrySend:['Réessayer l’envoi','Retry send'], cancelSend:['Annuler l’envoi en attente','Cancel queued message'], cancelUpload:['Annuler le transfert','Cancel upload'], removeAttachment:['Retirer la pièce jointe','Remove attachment'], disconnected:['Connexion interrompue. Vos messages restent en attente.','Connection lost. Your messages remain queued.'], unavailable:['La messagerie est indisponible. Votre brouillon est conservé.','Messaging is unavailable. Your draft is kept.'], accessRevoked:['Vous ne pouvez plus écrire dans cette conversation.','You can no longer send messages in this conversation.'], genericError:['L’action n’a pas abouti. Réessayez dans un instant.','The action could not be completed. Try again shortly.'], group:['Groupe','Group'], direct:['Un ami','One friend'], createGroup:['Créer le groupe','Create group'], groupName:['Nom du groupe','Group name'], groupNamePlaceholder:['Par exemple : Les aventuriers du soir','For example: Evening adventurers'], selectFriends:['Choisissez vos amis','Choose your friends'], newConversationHint:['Échangez avec un ami ou réunissez votre groupe.','Chat with a friend or bring your group together.'], members:['participants','members'], activeMembers:['Participants','Members'], pendingMembers:['Invitations en attente','Pending invitations'], inviteMembers:['Inviter des amis','Invite friends'], invite:['Inviter','Invite'], invited:['Invité','Invited'], accept:['Accepter','Accept'], decline:['Refuser','Decline'], invitationHint:['Vous êtes invité à rejoindre cette conversation.','You’ve been invited to join this conversation.'], manageGroup:['Modifier le groupe','Edit group'], groupImage:['Image du groupe','Group image'], chooseGroupImage:['Choisir une image','Choose an image'], useGroupImage:['Utiliser pour le groupe','Use for the group'], groupImageHint:['Ajoutez une image, puis choisissez-la ci-dessous.','Add an image, then choose it below.'], leaveGroup:['Quitter le groupe','Leave group'], leaveGroupHint:['Vous ne recevrez plus les nouveaux messages de ce groupe.','You will no longer receive new messages from this group.'], removeMember:['Retirer du groupe','Remove from group'], makeAdmin:['Nommer administrateur','Make administrator'], makeMember:['Retirer le rôle administrateur','Remove administrator role'], admin:['Administrateur','Administrator'], owner:['Créateur','Owner'], self:['Vous','You'], openProfile:['Ouvrir le profil','Open profile'], typingOne:['écrit…','is typing…'], typingMany:['sont en train d’écrire…','are typing…'], gameCard:['Carte Atlas','Atlas card'], item:['Objet','Item'], character:['Personnage','Character'], quest:['Quête','Quest'], location:['Lieu','Location'], outing:['Sortie','Outing'], title:['Titre','Title'], description:['Description','Description'], reference:['Identifiant de référence','Reference ID'], optional:['facultatif','optional'], date:['Date','Date'], level:['Niveau','Level'], tank:['Tank','Tank'], healer:['Soigneur','Healer'], damage:['DPS','Damage'], joining:['Participe','Joining'], cardAttached:['Armory joint','Armory attached'], limitReached:['Un message peut contenir jusqu’à 1 000 caractères.','A message can contain up to 1,000 characters.'], attachmentLimit:['Vous pouvez joindre jusqu’à 10 fichiers par message.','You can attach up to 10 files to a message.'], noFriends:['Votre liste d’amis est vide.','Your friends list is empty.'], legacyHint:['Les fonctions avancées seront disponibles après la mise à jour du service.','Advanced features will be available after the service is updated.'],
    away:['Absent','Away'], characterArmory:['Armory du personnage','Character Armory'], openArmory:['Ouvrir l’Armory','Open Armory'], chooseOwnCharacter:['Choisissez l’un de vos personnages.','Choose one of your characters.'], yourCharacters:['Vos personnages','Your characters'], shareCharacter:['Partager l’Armory de','Share the Armory of'], loadingCharacters:['Chargement de vos personnages…','Loading your characters…'], noCharacters:['Vous n’avez pas encore de personnage à partager.','You do not have a character to share yet.'], charactersUnavailable:['Impossible de charger vos personnages. Réessayez dans un instant.','Your characters could not be loaded. Try again shortly.'], addingArmory:['Ajout de l’Armory…','Adding the Armory…'], warrior:['Guerrier','Warrior'], paladin:['Paladin','Paladin'], hunter:['Chasseur','Hunter'], rogue:['Voleur','Rogue'], priest:['Prêtre','Priest'], deathKnight:['Chevalier de la mort','Death Knight'], shaman:['Chaman','Shaman'], mage:['Mage','Mage'], warlock:['Démoniste','Warlock'], druid:['Druide','Druid']
  };
  let snapshot = { sessionId: '', ownerAccountId: 0, sequence: '0', locale: 'fr', isActive: false, isAvailable: false, state: { threads: [], contacts: [], preferences: {}, capabilities: [] }, messages: [], selectedThreadId: null, draft: {}, pending: [], typing: [], mediaOrigin: 'https://atlas-chat-media.invalid/' };
  let selectedThread = null, editTarget = null, editBackup = null;
  let replyTarget = null, composerCard = null, draftDirty = false, draftTimer = 0, typingTimer = 0, lastTypingAt = 0, typingActive = false;
  let renderVersion = 0, layoutPending = false, followBottom = true, stableAnchor = null, unreadBoundary = null, lastReadKey = '';
  let lastUserScroll = 0, programmaticScroll = false, dialogRefresh = null, toastTimer = 0, menuReturnFocus = null;
  let armoryPickerOpen = false, armoryPickerFocusPending = false, armoryRequestPending = false, armorySelection = null, armoryError = '';
  let lastComposerStateKey = '', imageReturnFocus = null, mediaViewer = null, viewerVersion = 0;
  let smoothScroll = null, uploadsSession = '', timelineSession = '', newestRenderedMessageId = null, timelineWasLoading = false;
  const motionPreference = global.matchMedia('(prefers-reduced-motion: reduce)');
  const runningMotions = new Set(), exitingUploads = new Set(), draftImageSizes = new Map();
  const requests = new Map(), localDrafts = new Map(), inFlightSends = new Map(), armorySelections = new Map();
  const localSendFailures = new Map(), messageFeedback = new Map();
  const timeline = $('timeline'), messageList = $('message-list'), composer = $('composer-input');
  const t = key => strings[key] ? strings[key][String(snapshot.locale).startsWith('en') ? 1 : 0] : key;
  const uuid = () => global.crypto && crypto.randomUUID ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 3 | 8)).toString(16); });
  const capable = name => (snapshot.state.capabilities || []).includes(name);
  const sessionKey = value => String(value.sessionId || '') + ':' + String(value.ownerAccountId || 0);
  const active = () => !!snapshot.isActive && !document.hidden && !!snapshot.ownerAccountId && !!selectedThread;
  const playbackActive = (value = snapshot) => !!(value.isMediaActive ?? value.isActive) && !!value.sessionId && value.ownerAccountId > 0;
  const currentThreadId = () => selectedThread && selectedThread.id;
  const hasIdentity = () => !!snapshot.sessionId && snapshot.ownerAccountId > 0;
  const normalizedBody = text => String(text || '').replace(/\r\n/g, '\n').trim();
  let searchReadSuppressed = false;
  const conversationSearch = global.AtlasChatSearch?.create({
    snapshot: () => snapshot, messageList, action,
    canOpen: () => !!selectedThread && !!snapshot.isActive && !$('app-dialog').open && !$('image-dialog').open,
    open: () => {
      closeMenus(true); closeArmoryPicker(); cancelSmoothScroll(); searchReadSuppressed = true;
      $('load-earlier-button').disabled = true;
      followBottom = false; stableAnchor = captureAnchor(); settleLayout(stableAnchor, false);
    },
    close: () => { updateEarlierButton(); if (selectedThread) settleLayout(captureAnchor(), false); },
    historyChanged: updateEarlierButton,
    jump: node => {
      cancelSmoothScroll(); ++renderVersion; layoutPending = false; programmaticScroll = true;
      const bounds = node.getBoundingClientRect(), viewport = timeline.getBoundingClientRect();
      timeline.scrollTop += bounds.top - viewport.top - Math.max(0, (viewport.height - bounds.height) / 2);
      followBottom = false; stableAnchor = captureAnchor();
      requestAnimationFrame(() => { programmaticScroll = false; updateJumpButton(); });
    }
  });
  function updateEarlierButton() { $('load-earlier-button').disabled = snapshot.isLoadingEarlier || !snapshot.isAvailable || !!conversationSearch?.isOpen() || !!conversationSearch?.isLoadingHistory(); }

  function animate(node, frames, duration = 160, options = {}) {
    if (!node || motionPreference.matches || !active() || typeof node.animate !== 'function') return null;
    node._atlasMotion?.cancel();
    const animation = node.animate(frames, { duration, easing: 'cubic-bezier(.2,.8,.2,1)', ...options });
    node._atlasMotion = animation; runningMotions.add(animation);
    const finished = () => { runningMotions.delete(animation); if (node._atlasMotion === animation) node._atlasMotion = null; };
    animation.finished.then(finished, finished);
    return animation;
  }
  function afterAnimation(animation, complete) { if (animation) animation.finished.then(complete, complete); else complete(); }
  function enter(node, distance = 4, duration = 160) {
    return animate(node, [{ opacity: 0, transform: 'translateY(' + distance + 'px)' }, { opacity: 1, transform: 'translateY(0)' }], duration);
  }
  function revealSurface(node, distance = 4) {
    node._surfaceVersion = (node._surfaceVersion || 0) + 1;
    node._atlasMotion?.cancel(); node.inert = false; node.hidden = false; node.removeAttribute('aria-hidden');
    enter(node, distance, 150);
  }
  function hideSurface(node, immediate = false) {
    if (node.hidden || node.inert && !immediate) return;
    const version = (node._surfaceVersion || 0) + 1; node._surfaceVersion = version;
    node.inert = true; node.setAttribute('aria-hidden', 'true');
    const finish = () => { if (node._surfaceVersion === version) { node.hidden = true; node.inert = false; node.removeAttribute('aria-hidden'); } };
    if (immediate) { node._atlasMotion?.cancel(); finish(); }
    else afterAnimation(animate(node, [{ opacity: 1, transform: 'translateY(0)' }, { opacity: 0, transform: 'translateY(3px)' }], 110), finish);
  }
  function clearMotion() {
    cancelSmoothScroll();
    for (const animation of runningMotions) animation.cancel();
    for (const node of exitingUploads) node.remove();
    exitingUploads.clear();
  }
  motionPreference.addEventListener('change', () => { if (motionPreference.matches) clearMotion(); });

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
    const threadId = payload?.threadId || currentThreadId();
    const feedbackKey = payload?.messageId ? 'message:' + payload.messageId : payload?.clientMessageId ? 'pending:' + payload.clientMessageId : payload?.uploadId ? 'upload:' + payload.uploadId : null;
    const handle = options?.result || ((_, error) => {
      if (feedbackKey) {
        if (threadId === currentThreadId()) {
          if (error) messageFeedback.set(feedbackKey, { threadId, error }); else messageFeedback.delete(feedbackKey);
          if (feedbackKey.startsWith('upload:')) renderUploads(); else renderTimeline(false);
        }
      } else if (error && !options?.silent && threadId === currentThreadId()) toast(errorText(error));
    });
    const result = post(name, payload, options && options.files);
    if (result.delivered && (options?.result || !options?.silent)) requests.set(result.requestId, { handler: handle, session: sessionKey(snapshot) });
    if (!result.delivered && name !== 'ready' && (options?.result || !options?.silent)) handle(null, 'chat-bridge-unavailable');
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
      : (snapshot.state.contacts || []).find(contact => contact.accountId === profile.accountId)
        || (snapshot.state.threads || []).flatMap(thread => thread.members || []).find(member => member.profile?.accountId === profile.accountId)?.profile;
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
    if (saved && (saved.body || saved.card)) return t('draftLabel') + ' · ' + saved.body;
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
  function selectThread(threadId) { if (threadId === currentThreadId()) return; conversationSearch?.close(false); flushDraft(); stopTyping(); closeMenus(); action('selectThread', { threadId }); }

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
    $('sidebar-status').textContent = snapshot.isLegacyFallback ? t('legacyHint') : '';
    $('sidebar-status').classList.toggle('inline-busy', !snapshot.isLegacyFallback && snapshot.isLoading && !snapshot.state.threads.length);
    $('sidebar-status').setAttribute('aria-label', snapshot.isLoading ? t('loading') : '');
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
  function cancelSmoothScroll() {
    if (!smoothScroll) return;
    cancelAnimationFrame(smoothScroll.frame); smoothScroll = null; programmaticScroll = false;
    followBottom = isAtBottom(36); stableAnchor = followBottom ? null : captureAnchor();
  }
  function smoothScrollTo(target, toBottom = false) {
    cancelSmoothScroll();
    if (motionPreference.matches || !active()) {
      programmaticScroll = true; timeline.scrollTop = target(); followBottom = toBottom || isAtBottom(36);
      stableAnchor = followBottom ? null : captureAnchor();
      requestAnimationFrame(() => { programmaticScroll = false; updateJumpButton(); requestRead(); }); return;
    }
    const operation = { start: timeline.scrollTop, at: performance.now(), frame: 0, session: sessionKey(snapshot) + ':' + currentThreadId() };
    smoothScroll = operation; programmaticScroll = true;
    const step = now => {
      if (smoothScroll !== operation) return;
      if (operation.session !== sessionKey(snapshot) + ':' + currentThreadId() || !active()) { cancelSmoothScroll(); return; }
      const progress = Math.min(1, (now - operation.at) / 190), eased = 1 - Math.pow(1 - progress, 3);
      const end = Math.max(0, Math.min(target(), timeline.scrollHeight - timeline.clientHeight));
      timeline.scrollTop = operation.start + (end - operation.start) * eased;
      if (progress < 1) operation.frame = requestAnimationFrame(step);
      else { smoothScroll = null; programmaticScroll = false; followBottom = toBottom || isAtBottom(36); stableAnchor = followBottom ? null : captureAnchor(); updateJumpButton(); requestRead(); }
    };
    operation.frame = requestAnimationFrame(step);
  }
  function scrollBottom(smooth = false) {
    if (smooth) { smoothScrollTo(() => timeline.scrollHeight, true); return; }
    if (smoothScroll) return;
    programmaticScroll = true; timeline.scrollTop = timeline.scrollHeight; followBottom = true; stableAnchor = null;
    requestAnimationFrame(() => { if (!smoothScroll) programmaticScroll = false; });
  }
  function requestRead() {
    if (searchReadSuppressed || conversationSearch?.isOpen() || layoutPending || smoothScroll || !active() || !isAtBottom(2) || timeline.clientHeight <= 0) return;
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
      if (smoothScroll) { layoutPending = false; return; }
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
    const toBottom = changedThread || !conversationSearch?.isOpen() && !searchReadSuppressed && (followBottom || isAtBottom(36));
    const session = sessionKey(snapshot) + ':' + currentThreadId();
    const animateNew = !changedThread && timelineSession === session && !timelineWasLoading && !snapshot.isLoading && active();
    timelineWasLoading = !!snapshot.isLoading;
    const previousNewest = timelineSession === session ? newestRenderedMessageId : null;
    timelineSession = session;
    const allMessages = (snapshot.messages || []).filter(message => R.id(message.id)).slice().sort((a, b) => R.compareIds(a.id, b.id));
    if (changedThread || previousNewest === null || allMessages.length && R.compareIds(allMessages.at(-1).id, previousNewest) > 0) newestRenderedMessageId = allMessages.at(-1)?.id || null;
    const messages = allMessages.filter(message => !message.deletedAt);
    const existing = new Map(Array.from(messageList.children).map(node => [node.dataset.key, node]));
    const arrivals = [];
    let cursor = messageList.firstElementChild, previous = null, lastDay = '', unreadAdded = false;
    const keep = (key, node) => { node.dataset.key = key; if (node !== cursor) messageList.insertBefore(node, cursor); cursor = node.nextElementSibling; existing.delete(key); };
    const context = { ownerAccountId: snapshot.ownerAccountId, locale: snapshot.locale, mediaOrigin: snapshot.mediaOrigin, t, action, profile: resolveProfile, isActive: active, isMediaActive: playbackActive, capable, reply: beginReply, copy: copyText, react: showReactions, messageMenu: showMessageMenu, jumpTo, viewImage, viewMedia,
      attachmentMenu: showAttachmentMenu,
      mediaError: player => { if (mediaViewer?.player === player) closeImageViewer(true, true, true); }, dismissPreview };
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
      const pendingKey = message.clientMessageId ? 'pending:' + message.clientMessageId : null;
      const pendingNode = pendingKey ? existing.get(pendingKey) : null;
      const previousNode = existing.get(key) || pendingNode;
      const node = R.reconcileMessage(previousNode, message, { ...context, messageError: errorText(messageFeedback.get(key)?.error) });
      if (pendingNode === node) existing.delete(pendingKey);
      if (!previousNode && animateNew && (previousNewest === null || R.compareIds(message.id, previousNewest) > 0) && !snapshot.isLoadingEarlier) arrivals.push(node);
      keep(key, node); previous = message;
    }
    const nativePending = snapshot.pending || [];
    const pending = [...nativePending, ...Array.from(localSendFailures.values()).filter(item => !nativePending.some(native => native.clientMessageId === item.clientMessageId))]
      .filter(item => item.threadId === currentThreadId() && !allMessages.some(message => message.clientMessageId === item.clientMessageId));
    for (const queued of pending) {
      const key = 'pending:' + queued.clientMessageId;
      let node = existing.get(key);
      const signature = JSON.stringify([queued, snapshot.state.self, snapshot.isAvailable, messageFeedback.get(key), snapshot.locale]);
      if (!node || node._signature !== signature) {
        if (!node) { node = renderPending(queued); if (animateNew) arrivals.push(node); }
        else renderPending(queued, node);
        node._signature = signature;
      }
      keep(key, node);
    }
    for (const node of existing.values()) { R.suspendMedia(node, true); node.remove(); }
    $('no-messages').hidden = messages.length > 0 || pending.length > 0 || snapshot.isLoading;
    $('load-earlier-button').hidden = !snapshot.hasEarlier;
    updateEarlierButton();
    $('load-earlier-button').textContent = t('loadEarlier');
    $('load-earlier-button').setAttribute('aria-busy', String(!!snapshot.isLoadingEarlier));
    $('timeline-busy').hidden = !snapshot.isLoading || messages.length > 0 || pending.length > 0;
    $('timeline-busy').setAttribute('aria-label', t('loading'));
    if (changedThread) followBottom = true;
    settleLayout(oldAnchor, toBottom);
    conversationSearch?.refresh();
    const version = renderVersion;
    if (arrivals.length) requestAnimationFrame(() => requestAnimationFrame(() => {
      if (version !== renderVersion || session !== sessionKey(snapshot) + ':' + currentThreadId()) return;
      const viewport = timeline.getBoundingClientRect();
      for (const node of arrivals) { const rect = node.getBoundingClientRect(); if (node.isConnected && rect.bottom > viewport.top && rect.top < viewport.bottom) enter(node, 5, 150); }
    }));
  }

  function renderPending(queued, node = null) {
    if (!node) node = e('article', 'message is-own is-group-start');
    else { R.suspendMedia(node, true); node.replaceChildren(); }
    node.oncontextmenu = null; node.dataset.clientMessageId = queued.clientMessageId;
    const avatar = e('div', 'message-avatar'); avatar.append(R.avatar(snapshot.state.self, { small: true, presence: true, mediaOrigin: snapshot.mediaOrigin }));
    const main = e('div', 'message-main');
    const heading = e('div', 'message-heading'); heading.append(e('span', 'message-author', snapshot.state.self?.username || t('self')), e('span', 'message-time', R.time(queued.createdAt, snapshot.locale)));
    main.append(heading, R.renderMarkdown(queued.body, t));
    if (queued.card) main.append(R.renderCard(queued.card, { threadId: queued.threadId }, { t, action, locale: snapshot.locale, mediaOrigin: snapshot.mediaOrigin, ownerAccountId: snapshot.ownerAccountId }));
    if (queued.attachments?.length) main.append(e('div', 'message-content', queued.attachments.map(attachment => attachment.fileName || '').filter(Boolean).join(', ')));
    const deleting = queued.status === 'deleting' || !!queued.deleteRequested;
    const failed = queued.status === 'failed' && !deleting, sending = ['sending', 'posting'].includes(queued.status);
    const status = e('div', 'message-state' + (failed ? ' is-failed' : ''));
    const progress = Math.min(100, Math.max(0, Math.round((Number(queued.progress) || 0) * 100)));
    const stateLabel = deleting ? t('deletingMessage') + (!snapshot.isAvailable ? ' ' + t('disconnected') : '')
      : queued.status === 'uploading' ? t('uploading') + ' ' + progress + ' %' : !snapshot.isAvailable && !sending ? t('disconnected') : t(sending ? 'sending' : 'waiting');
    const error = messageFeedback.get('pending:' + queued.clientMessageId)?.error || queued.error || queued.errorCode;
    status.classList.toggle('is-failed', failed || !!error); status.setAttribute('role', error || failed ? 'alert' : 'status');
    status.append(R.icon(error || failed ? 'alert' : 'clock'), document.createTextNode(error ? (deleting ? stateLabel + ' ' : '') + errorText(error) : failed ? t('failed') : stateLabel));
    if (failed) { const retry = R.button('', t('retrySend'), null, () => queued.localOnly ? retryLocalSend(queued) : action('retrySend', { clientMessageId: queued.clientMessageId })); retry.textContent = t('retry'); status.append(retry); }
    const deleteAction = queued.canDelete === true, deleteLabel = failed || deleteAction;
    if (!sending && !deleting && (deleteAction || queued.canCancel !== false)) {
      const remove = () => {
        if (queued.localOnly) { localSendFailures.delete(queued.clientMessageId); messageFeedback.delete('pending:' + queued.clientMessageId); renderTimeline(false); }
        else action(deleteAction ? 'deleteFailedSend' : 'cancelSend', { clientMessageId: queued.clientMessageId });
      };
      const cancel = R.button('', t(deleteLabel ? 'delete' : 'cancelSend'), null, remove); cancel.textContent = t(deleteLabel ? 'delete' : 'cancel'); status.append(cancel);
      node.oncontextmenu = event => { event.preventDefault(); showMenu([{ label: t(deleteLabel ? 'delete' : 'cancel'), icon: 'trash', danger: true, run: remove }], event.clientX, event.clientY); };
    }
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
    localSendFailures.delete(clientMessageId);
    const send = inFlightSends.get(clientMessageId); if (!send) return;
    messageFeedback.delete('pending:' + clientMessageId);
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
    if (body.length > 1000) { toast(t('limitReached')); return; }
    if (editTarget) {
      if (!body || !snapshot.isAvailable) return;
      const target = editTarget;
      action('editMessage', { threadId: target.threadId, messageId: target.id, body, expectedVersion: target.version }, { result: (_, error) => {
        if (target.threadId !== currentThreadId()) return;
        if (error) messageFeedback.set('message:' + target.id, { threadId: target.threadId, error });
        else { messageFeedback.delete('message:' + target.id); if (editTarget?.id === target.id) cancelContext(); }
        renderTimeline(false);
      } });
      return;
    }
    const uploads = snapshot.draft?.attachments || [];
    if (uploads.some(upload => upload.status === 'failed' || upload.status === 'cancelled')) return;
    const attachmentIds = uploads.map(upload => upload.id || upload.localId || upload.attachment?.id).filter(Boolean);
    if (!body && !attachmentIds.length && !composerCard) return;
    if (inFlightSends.size && Array.from(inFlightSends.values()).some(send => send.threadId === currentThreadId() && send.signature === draftSignature(currentDraft()))) return;
    const clientMessageId = uuid(), draft = currentDraft();
    const payload = { threadId: currentThreadId(), clientMessageId, body, replyToMessageId: draft.replyToMessageId, attachmentIds, card: draft.card };
    const send = { threadId: currentThreadId(), signature: draftSignature(draft), payload, createdAt: new Date().toISOString(), attachments: uploads.map(upload => upload.attachment || upload) }; inFlightSends.set(clientMessageId, send);
    localDrafts.set(send.threadId, draft); flushDraft(); stopTyping();
    submitLocalSend(send);
    updateComposer();
  }
  function submitLocalSend(send) {
    const clientMessageId = send.payload.clientMessageId;
    inFlightSends.set(clientMessageId, send);
    action('send', send.payload, { result: (_, error) => {
      if (error) {
        inFlightSends.delete(clientMessageId);
        const accepted = (snapshot.pending || []).some(item => item.clientMessageId === clientMessageId) || snapshot.messages.some(item => item.clientMessageId === clientMessageId);
        if (!accepted) localSendFailures.set(clientMessageId, { ...send.payload, createdAt: send.createdAt, attachments: send.attachments, status: 'failed', errorCode: error, canCancel: true, localOnly: true, send });
      } else completeLocalSend(clientMessageId);
      if (send.threadId === currentThreadId()) renderTimeline(false);
      updateComposer();
    } });
  }
  function retryLocalSend(queued) {
    if (!queued.send || inFlightSends.has(queued.clientMessageId)) return;
    localSendFailures.set(queued.clientMessageId, { ...queued, status: 'sending', errorCode: '', canCancel: false });
    messageFeedback.delete('pending:' + queued.clientMessageId);
    submitLocalSend(queued.send); renderTimeline(false); updateComposer();
  }
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
    const host = $('typing-indicator'), signature = JSON.stringify([sessionKey(snapshot), currentThreadId(), names, snapshot.locale]);
    if (host._signature === signature) return;
    host._signature = signature; host.replaceChildren();
    if (names.length) { const dots = e('span', 'typing-dots'); dots.append(e('i'), e('i'), e('i')); host.append(dots, document.createTextNode(names.join(', ') + ' ' + t(names.length > 1 ? 'typingMany' : 'typingOne'))); }
  }

  function uploadPresentation(upload, value = snapshot) {
    const contentType = String(upload?.contentType || upload?.attachment?.contentType || '').toLowerCase();
    const type = String(upload?.kind || upload?.attachment?.kind || '');
    const kind = contentType.startsWith('image/') || ['image', 'animated-image', 'animation'].includes(type) ? 'image'
      : contentType.startsWith('video/') || type === 'video' ? 'video' : contentType.startsWith('audio/') || type === 'audio' ? 'audio' : 'file';
    return { kind, url: R.mediaUrl(upload?.previewUrl || upload?.attachment?.url, value.mediaOrigin),
      name: upload?.fileName || upload?.attachment?.fileName || t(kind === 'file' ? 'attachFiles' : kind) };
  }
  function removeUploadWithMotion(node) {
    R.suspendMedia(node, true);
    if (motionPreference.matches || !active()) { node.remove(); return; }
    const rect = node.getBoundingClientRect();
    node.classList.add('queued-file-exit'); node.inert = true; node.setAttribute('aria-hidden', 'true');
    Object.assign(node.style, { position: 'fixed', left: rect.left + 'px', top: rect.top + 'px', width: rect.width + 'px', height: rect.height + 'px' });
    document.body.append(node); exitingUploads.add(node);
    afterAnimation(animate(node, [{ opacity: 1, transform: 'translateY(0)' }, { opacity: 0, transform: 'translateY(4px)' }], 130), () => { node.remove(); exitingUploads.delete(node); });
  }
  function rememberDraftImage(image, url) {
    if (image.naturalWidth > 0 && image.naturalHeight > 0) {
      draftImageSizes.delete(url); draftImageSizes.set(url, { width: image.naturalWidth, height: image.naturalHeight });
      if (draftImageSizes.size > 128) draftImageSizes.delete(draftImageSizes.keys().next().value);
    }
    const queue = $('attachment-queue'); if (!queue.contains(image)) return;
    const previous = queue._imagePositions;
    for (const node of queue.children) {
      const old = previous?.get(node), current = node.getBoundingClientRect();
      if (old && !mediaViewer && Math.abs(old.left - current.left) > .5) animate(node, [{ transform: 'translateX(' + (old.left - current.left) + 'px)' }, { transform: 'translateX(0)' }], 180);
    }
    queue._imagePositions = new Map(Array.from(queue.children).map(node => [node, node.getBoundingClientRect()]));
  }
  function renderUploads() {
    const uploads = snapshot.draft?.attachments || [], queue = $('attachment-queue');
    const session = sessionKey(snapshot) + ':' + currentThreadId(), changedThread = uploadsSession !== session;
    uploadsSession = session;
    const oldHeight = queue.hidden ? 0 : queue.getBoundingClientRect().height;
    if (changedThread) { R.suspendMedia(queue, true); queue.replaceChildren(); }
    const oldNodes = new Map(Array.from(queue.children).map(node => [node.dataset.key, { node, rect: node.getBoundingClientRect() }]));
    const ids = new Set(uploads.map(upload => String(upload.id || upload.localId)));
    for (const [key, previous] of oldNodes) if (!ids.has(key)) {
      if (!changedThread && !editTarget) removeUploadWithMotion(previous.node);
    }
    const oldKeys = Array.from(oldNodes.keys()).join('|'), nextKeys = Array.from(ids).join('|');
    const structureChanged = oldKeys !== nextKeys || queue.hidden !== (!uploads.length || !!editTarget);
    if (structureChanged || changedThread) { queue._layoutVersion = (queue._layoutVersion || 0) + 1; queue._atlasMotion?.cancel(); queue.style.height = ''; queue.style.minHeight = ''; queue.style.paddingTop = ''; queue.style.paddingBottom = ''; queue.style.overflow = ''; }
    queue.hidden = !uploads.length || !!editTarget;
    queue.classList.toggle('has-visual-media', !queue.hidden && uploads.some(upload => ['image', 'video'].includes(uploadPresentation(upload).kind)));
    R.reconcileKeyed(queue, uploads, upload => upload.id || upload.localId, upload => {
      const { kind: mediaKind, url, name } = uploadPresentation(upload);
      const node = e('div', 'queued-file is-' + mediaKind);
      const preview = e('div', 'queued-preview');
      node.setAttribute('aria-label', name); node.title = name;
      let player = null, open = null, mediaShell = null;
      if (url && mediaKind === 'image') {
        const image = e('img'), dimensions = draftImageSizes.get(url);
        if (dimensions) { image.width = dimensions.width; image.height = dimensions.height; }
        image.alt = name; image.decoding = 'async'; image.draggable = false;
        image.addEventListener('load', () => rememberDraftImage(image, url), { once: true }); image.src = url;
        image.addEventListener('error', () => { if (mediaViewer?.origin === node) closeImageViewer(true, true, true); node.dataset.playbackUnavailable = 'true'; preview.replaceChildren(R.icon('file')); preview.setAttribute('aria-label', t('imageUnavailable')); }, { once: true });
        preview.append(image);
      } else if (url && (mediaKind === 'audio' || mediaKind === 'video')) {
        player = e(mediaKind); player.controls = false; player.preload = 'metadata'; player.src = url;
        player.setAttribute('aria-label', name); player.setAttribute('controlsList', 'nodownload');
        if (mediaKind === 'video') { player.playsInline = true; const poster = R.mediaUrl(upload.attachment?.thumbnailUrl, snapshot.mediaOrigin); if (poster) player.poster = poster; }
        player.addEventListener('error', () => {
          if (mediaViewer?.origin === node) closeImageViewer(true, true, true);
          if (mediaShell) M.dispose(mediaShell, { pause: true });
          node.dataset.playbackUnavailable = 'true'; preview.replaceChildren(R.icon('file')); updateUploadState(node, node._upload);
        }, { once: true });
        mediaShell = M.create(player, { kind: mediaKind, fileName: name, locale: snapshot.locale, t, mode: 'draft',
          onExpand: (_, opener) => viewDraftMedia(node, opener) });
        preview.append(mediaShell);
      } else preview.append(R.icon('file'));
      if (url && mediaKind !== 'file') {
        node.classList.add('is-previewable');
        if (mediaKind === 'image') {
          open = R.button('queued-preview-open', t('viewMedia') + ' · ' + name, null, event => viewDraftMedia(node, event.currentTarget));
          open.setAttribute('aria-haspopup', 'dialog'); preview.append(open);
        }
      }
      node.addEventListener('contextmenu', event => {
        const current = node._upload, info = uploadPresentation(current);
        showAttachmentMenu({ uploadId: current.id || current.localId, kind: info.kind }, event);
      });
      const uploadId = upload.id || upload.localId;
      const remove = R.button('icon-button queued-file-remove', t('removeAttachment'), 'close', () => { if (mediaViewer?.origin === node) closeImageViewer(false, true, true); action('removeAttachment', { threadId: currentThreadId(), uploadId }); });
      const progress = e('div', 'queued-progress'), bar = e('span'); progress.append(bar);
      progress.setAttribute('role', 'progressbar'); progress.setAttribute('aria-label', t('uploading')); progress.setAttribute('aria-valuemin', '0'); progress.setAttribute('aria-valuemax', '100');
      const error = e('div', 'queued-file-error'); error.setAttribute('role', 'alert');
      const retry = R.button('queued-file-retry', t('retry'), null, () => action('retryUpload', { threadId: currentThreadId(), uploadId })); retry.textContent = t('retry');
      node.append(preview, remove, progress, error, retry); node._parts = { preview, player, mediaShell, open, remove, progress, bar, error, retry };
      updateUploadState(node, upload);
      return node;
    }, upload => { const info = uploadPresentation(upload); return JSON.stringify([info.kind, info.url]); });
    for (let index = 0; index < uploads.length; index++) {
      const node = queue.children[index], info = uploadPresentation(uploads[index]);
      updateUploadState(node, uploads[index]); node.setAttribute('aria-label', info.name); node.title = info.name;
      node._parts.open?.setAttribute('aria-label', t('viewMedia') + ' · ' + info.name);
      node._parts.remove.setAttribute('aria-label', t('removeAttachment')); node._parts.remove.title = t('removeAttachment');
      node._parts.progress.setAttribute('aria-label', t('uploading'));
      if (node._parts.player) node._parts.player.setAttribute('aria-label', info.name);
      if (node._parts.mediaShell) M.update(node._parts.mediaShell, { fileName: info.name, locale: snapshot.locale, t });
      const image = node._parts.preview.querySelector('img'); if (image) image.alt = info.name;
    }
    if (mediaViewer && (!mediaViewer.origin.isConnected || !viewerValidFor(snapshot))) closeImageViewer(false, true, true);
    if (!changedThread && structureChanged && !editTarget) {
      for (const node of queue.children) {
        const previous = oldNodes.get(node.dataset.key);
        if (!previous) enter(node);
        else { const delta = previous.rect.left - node.getBoundingClientRect().left; if (Math.abs(delta) > .5) animate(node, [{ transform: 'translateX(' + delta + 'px)' }, { transform: 'translateX(0)' }], 180); }
      }
      const newHeight = queue.hidden ? 0 : queue.getBoundingClientRect().height;
      if (Math.abs(newHeight - oldHeight) > .5 && !motionPreference.matches && active()) {
        const hidden = queue.hidden, version = (queue._layoutVersion || 0) + 1; queue._layoutVersion = version;
        queue.hidden = false; queue.style.overflow = 'hidden'; queue.style.minHeight = '0';
        queue.style.height = newHeight + 'px';
        if (!newHeight) { queue.style.paddingTop = '0'; queue.style.paddingBottom = '0'; }
        const animation = animate(queue, [
          { height: oldHeight + 'px', paddingTop: oldHeight ? '7px' : '0px', paddingBottom: oldHeight ? '12px' : '0px' },
          { height: newHeight + 'px', paddingTop: newHeight ? '7px' : '0px', paddingBottom: newHeight ? '12px' : '0px' }
        ], 180);
        afterAnimation(animation, () => { if (queue._layoutVersion !== version) return; queue.hidden = hidden; queue.style.height = ''; queue.style.minHeight = ''; queue.style.paddingTop = ''; queue.style.paddingBottom = ''; queue.style.overflow = ''; });
      }
    }
    queue._imagePositions = new Map(Array.from(queue.children).map(node => [node, node.getBoundingClientRect()]));
  }
  function updateUploadState(node, upload) {
    if (!node || !upload) return;
    node._upload = upload;
    const failed = upload.status === 'failed' || upload.status === 'cancelled' || !upload.status && (!!upload.error || !!upload.errorCode);
    const complete = upload.isComplete || !!upload.attachment;
    const size = Number(upload.size), offset = Number(upload.offset), percent = size > 0 ? Math.min(100, Math.max(0, Math.round(offset * 100 / size))) : 0;
    const actionError = messageFeedback.get('upload:' + (upload.id || upload.localId))?.error;
    const { remove, progress, bar, error, retry } = node._parts;
    node.classList.toggle('is-failed', !!failed || !!actionError); node.setAttribute('aria-busy', String(!complete && !failed));
    remove.title = t(complete ? 'removeAttachment' : 'cancelUpload'); remove.setAttribute('aria-label', remove.title);
    progress.hidden = complete || failed; bar.style.width = percent + '%'; progress.setAttribute('aria-valuenow', String(percent));
    error.textContent = actionError ? errorText(actionError) : failed ? errorText(upload.error || upload.errorCode) || t('failed') : node.dataset.playbackUnavailable ? t('mediaPlaybackUnavailable') : '';
    error.hidden = !error.textContent; retry.hidden = !failed;
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
    } else if (!snapshot.isLoading) {
      if (!snapshot.isAvailable) banner.textContent = t('disconnected');
      else if (!selectedThread.canSend) banner.textContent = t('accessRevoked');
    }
    banner.hidden = !banner.childNodes.length;
  }

  function applySnapshot(value) {
    if (!value || value.type && value.type !== 'snapshot' || !value.state) return false;
    const next = { ...value, sequence: typeof value.sequence === 'string' ? value.sequence : String(value.sequence || '0'), state: { threads: [], contacts: [], preferences: {}, capabilities: [], ...value.state }, messages: value.messages || [], pending: value.pending || value.outbox || [], typing: value.typing || [] };
    if (!R.id(next.sequence)) return false;
    const identityChanged = sessionKey(next) !== sessionKey(snapshot);
    if (!identityChanged && R.compareIds(next.sequence, String(snapshot.sequence || '0')) < 0) return false;
    const changedThread = identityChanged || next.selectedThreadId !== snapshot.selectedThreadId;
    if (changedThread) { conversationSearch?.close(false); searchReadSuppressed = false; }
    if (!next.draft) {
      const draft = (next.drafts || []).find(item => item.threadId === next.selectedThreadId) || {};
      next.draft = { ...draft, attachments: (next.uploads || []).filter(item => item.threadId === next.selectedThreadId).map(upload => ({ ...upload, id: upload.id || upload.localId, isComplete: !!upload.attachment })) };
    }
    if (!viewerValidFor(next)) closeImageViewer(false, true, true);
    if (changedThread || !playbackActive(next)) closeImageViewer(false, true, true);
    if (changedThread || !next.isActive) { clearMotion(); closeMenus(true); }
    if (identityChanged) {
      R.suspendMedia(document, true); localDrafts.clear(); inFlightSends.clear(); localSendFailures.clear(); messageFeedback.clear(); requests.clear(); armorySelections.clear(); messageList.replaceChildren();
      clearTimeout(draftTimer); clearTimeout(typingTimer); draftDirty = false; typingActive = false; lastTypingAt = 0; lastReadKey = ''; lastComposerStateKey = ''; unreadBoundary = null;
      closeMenus(true); closeDialog(true); closeArmoryPicker(); armoryRequestPending = false; armorySelection = null; armoryError = ''; editTarget = null; editBackup = null; replyTarget = null; composerCard = null; composer.value = '';
      $('armory-character-list').replaceChildren(); $('armory-picker-status').replaceChildren(); $('armory-picker-status')._signature = null;
    }
    if (changedThread) { R.suspendMedia(messageList, true); messageList.replaceChildren(); messageFeedback.clear(); toast(''); closeImageViewer(false, true, true); editTarget = null; editBackup = null; replyTarget = null; composerCard = null; stableAnchor = null; lastReadKey = ''; closeMenus(true); closeArmoryPicker(); armorySelection = null; dragDepth = 0; nativeDropActive = false; }
    const oldLocale = snapshot.locale;
    snapshot = next;
    if (Array.isArray(next.supportedAttachmentExtensions)) fileExtensions = new Set(next.supportedAttachmentExtensions.map(extension => String(extension).replace(/^\./, '').toLowerCase()).filter(extension => /^[a-z0-9]{1,12}$/.test(extension)));
    else if (identityChanged) fileExtensions = new Set(defaultFileExtensions);
    selectedThread = (next.state.threads || []).find(thread => thread.id === next.selectedThreadId) || null;
    if (oldLocale !== next.locale || identityChanged) localize();
    if (changedThread) unreadBoundary = selectedThread && selectedThread.unreadCount > 0 ? R.id(selectedThread.lastReadMessageId) || '0' : null;
    for (const message of next.messages) if (message.clientMessageId) { completeLocalSend(message.clientMessageId); messageFeedback.delete('pending:' + message.clientMessageId); }
    for (const pending of next.pending) if (pending.clientMessageId) completeLocalSend(pending.clientMessageId);
    if (selectedThread && !editTarget) {
      const local = localDrafts.get(selectedThread.id);
      if (local && draftSignature(local) === draftSignature(next.draft)) localDrafts.delete(selectedThread.id);
      if (changedThread || !local) setComposerFromDraft(local || next.draft);
    }
    if (!selectedThread) { R.suspendMedia(messageList, true); messageList.replaceChildren(); if (next.selectedThreadId) localDrafts.delete(next.selectedThreadId); composer.value = ''; replyTarget = null; composerCard = null; }
    if (replyTarget && next.messages.some(message => message.id === (replyTarget.id || replyTarget.messageId) && message.deletedAt)) { replyTarget = null; saveLocalDraft(); }
    renderConversations(); renderThreadHeader();
    if (selectedThread) renderTimeline(changedThread);
    conversationSearch?.update();
    renderUploads(); updateComposer(); renderTyping(); renderArmoryPicker(); renderDropOverlay();
    if (changedThread && selectedThread) { enter(document.querySelector('.thread-header'), 0, 140); enter(timeline, 0, 140); }
    if (!active()) { lastReadKey = ''; stopTyping(); }
    if (!playbackActive()) { R.suspendMedia(document); closeImageViewer(false, true, true); }
    if (dialogRefresh) dialogRefresh();
    return true;
  }

  function closeMenus(immediate = false) {
    hideSurface($('context-menu'), immediate); hideSurface($('reaction-picker'), immediate);
    if ($('context-menu').parentElement !== document.body) document.body.append($('context-menu'));
    if ($('reaction-picker').parentElement !== document.body) document.body.append($('reaction-picker'));
    document.querySelectorAll('.message.has-menu').forEach(node => node.classList.remove('has-menu'));
  }
  function positionFloating(node, x, y) {
    node.hidden = false;
    const width = node.offsetWidth, height = node.offsetHeight;
    node.style.left = Math.max(9, Math.min(x - width, innerWidth - width - 9)) + 'px';
    node.style.top = Math.max(9, Math.min(y + 4, innerHeight - height - 9)) + 'px';
    revealSurface(node);
  }
  function showMenu(items, x, y) {
    closeMenus(true); menuReturnFocus = document.activeElement;
    const menu = $('context-menu'); menu.replaceChildren();
    if ($('image-dialog').open) $('image-dialog').append(menu);
    else if ($('app-dialog').open) $('app-dialog').append(menu);
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
  function showAttachmentMenu(attachment, event) {
    event.preventDefault(); event.stopPropagation();
    const kind = String(attachment.kind || ''), contentType = String(attachment.contentType || '');
    if (kind === 'video' || contentType.startsWith('video/')) { closeMenus(true); return; }
    const payload = attachment.uploadId ? { uploadId: attachment.uploadId }
      : attachment.id ? { attachmentId: attachment.id } : null;
    if (!payload || !hasIdentity()) return;
    showMenu([{ label: t('saveAs'), icon: 'download', run: () => action('downloadAttachment', payload) }], event.clientX, event.clientY);
  }
  function showReactions(message, anchor) {
    if (!capable('reactions') || message.deletedAt) return;
    closeMenus(true); menuReturnFocus = document.activeElement;
    const picker = $('reaction-picker'); picker.replaceChildren();
    for (const emoji of ['👍', '❤️', '😂', '🎉', '🔥', '👀', '✅', '🙏', '😮', '😢', '💪', '⚔️', '🛡️', '✨', '👋', '🤔', '💀', '💯']) {
      const mine = message.reactions?.some(reaction => reaction.emoji === emoji && reaction.accountIds?.includes(snapshot.ownerAccountId));
      const choice = R.button('', t(mine ? 'removeReaction' : 'addReaction') + ' ' + emoji, null, () => { closeMenus(); action('reaction', { threadId: message.threadId, messageId: message.id, emoji, active: !mine }); });
      choice.textContent = emoji; picker.append(choice);
    }
    const rect = anchor.getBoundingClientRect(); positionFloating(picker, rect.right, rect.bottom); picker.firstElementChild?.focus({ preventScroll: true });
  }
  async function copyText(text) {
    try { await navigator.clipboard.writeText(String(text || '')); }
    catch (_) {
      const selected = document.getSelection(), ranges = [];
      if (selected) for (let i = 0; i < selected.rangeCount; i++) ranges.push(selected.getRangeAt(i));
      const temporary = e('textarea'); temporary.value = String(text || ''); temporary.style.position = 'fixed'; temporary.style.opacity = '0'; document.body.append(temporary); temporary.select();
      let success = false; try { success = document.execCommand('copy'); } catch (_) {} temporary.remove();
      if (selected) { selected.removeAllRanges(); for (const range of ranges) selected.addRange(range); }
      if (!success) toast(t('copyFailed'));
    }
  }
  function jumpTo(messageId) {
    const node = Array.from(messageList.children).find(item => item.dataset.messageId === messageId);
    if (!node) { toast(t('messageNotLoaded')); return; }
    closeDialog(); followBottom = false;
    smoothScrollTo(() => node.isConnected ? timeline.scrollTop + node.getBoundingClientRect().top - timeline.getBoundingClientRect().top - (timeline.clientHeight - node.offsetHeight) / 2 : timeline.scrollTop);
    node.classList.remove('flash'); requestAnimationFrame(() => node.classList.add('flash')); node.focus({ preventScroll: true }); stableAnchor = captureAnchor(); updateJumpButton();
  }
  function dismissPreview(message, preview) {
    if (R.isVideoPreview(preview) || preview.canRemove === false || !capable('link-previews')) return;
    action('dismissPreview', { threadId: message.threadId, messageId: message.id, previewId: preview.id }, { result: (_, error) => { if (error) toast(errorText(error)); } });
  }

  function openDialog(title, subtitle) {
    closeMenus(true); dialogRefresh = null; $('dialog-title').textContent = title; $('dialog-subtitle').textContent = subtitle || ''; $('dialog-content').replaceChildren(); $('dialog-footer').replaceChildren();
    const dialog = $('app-dialog'); dialog._dialogVersion = (dialog._dialogVersion || 0) + 1;
    dialog._atlasMotion?.cancel(); dialog.inert = false; dialog.classList.remove('is-closing');
    if (!dialog.open) { dialog.classList.add('is-opening'); dialog.showModal(); requestAnimationFrame(() => dialog.classList.remove('is-opening')); }
    enter(dialog, 5, 160);
    return { content: $('dialog-content'), footer: $('dialog-footer') };
  }
  function closeDialog(immediate = false) {
    dialogRefresh = null; const dialog = $('app-dialog'); if (!dialog.open) return;
    const version = (dialog._dialogVersion || 0) + 1; dialog._dialogVersion = version;
    dialog.inert = true; dialog.classList.add('is-closing');
    const finish = () => { if (dialog._dialogVersion === version) { dialog.close(); dialog.inert = false; dialog.classList.remove('is-opening', 'is-closing'); } };
    if (immediate === true) { dialog._atlasMotion?.cancel(); finish(); }
    else afterAnimation(animate(dialog, [{ opacity: 1, transform: 'translateY(0)' }, { opacity: 0, transform: 'translateY(4px)' }], 110), finish);
  }
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
  function viewImage(attachment, opener) {
    const url = R.mediaUrl(attachment.url, snapshot.mediaOrigin) || (attachment.id ? snapshot.mediaOrigin + 'attachments/' + encodeURIComponent(attachment.id) : '');
    if (url) openMediaViewer({ kind: 'image', url, name: attachment.fileName, attachmentId: attachment.id, contentType: attachment.contentType, opener: opener || document.activeElement });
  }
  function viewMedia(attachment, player, opener) {
    const url = R.mediaUrl(player?.currentSrc || player?.src, snapshot.mediaOrigin);
    if (url && player?.isConnected && !player.error) openMediaViewer({ kind: player.tagName.toLowerCase(), url, name: attachment.fileName, attachmentId: attachment.id, previewId: attachment.previewId, contentType: attachment.contentType, player, opener });
  }
  function viewDraftMedia(node, opener) {
    const upload = node._upload, info = uploadPresentation(upload);
    if (!info.url || !['image', 'video', 'audio'].includes(info.kind) || node.dataset.playbackUnavailable === 'true') return;
    openMediaViewer({ kind: info.kind, url: info.url, name: info.name, uploadId: upload.id || upload.localId, player: node._parts.player, opener });
  }
  function movePlayer(player, parent, before = null, continuePlayback = true) {
    const surface = M?.shellFor(player) || player;
    const time = player.currentTime, playing = !player.paused && !player.ended;
    const rate = player.playbackRate, volume = player.volume, muted = player.muted;
    const session = sessionKey(snapshot), threadId = currentThreadId();
    const version = (player._atlasMoveVersion || 0) + 1; player._atlasMoveVersion = version;
    if (player._atlasRestoreMetadata) player.removeEventListener('loadedmetadata', player._atlasRestoreMetadata);
    // A state-preserving move avoids the media reset caused by remove/append.
    if (typeof parent.moveBefore === 'function' && surface.isConnected && parent.isConnected) parent.moveBefore(surface, before);
    else {
      parent.insertBefore(surface, before);
      const restore = () => {
        if (player._atlasMoveVersion !== version || !player.isConnected || session !== sessionKey(snapshot) || threadId !== currentThreadId()) return;
        if (Number.isFinite(time) && Math.abs(player.currentTime - time) > .05) { try { player.currentTime = time; } catch (_) {} }
      };
      player.playbackRate = rate; player.volume = volume; player.muted = muted;
      restore();
      if (player.readyState === 0) { player._atlasRestoreMetadata = restore; player.addEventListener('loadedmetadata', restore, { once: true }); }
      // Request resumption only once. A later metadata event must not undo a
      // user's pause while this initial play request was waiting for data.
      if (continuePlayback && playing && player.paused && playbackActive()) player.play().catch(() => {});
    }
    if (!continuePlayback) player.pause();
  }
  function openMediaViewer(details) {
    if (!hasIdentity() || !currentThreadId() || !details.opener?.isConnected) return;
    closeImageViewer(false, true, true); closeMenus(); closeArmoryPicker();
    const dialog = $('image-dialog'), image = $('image-dialog-image'), host = $('media-dialog-player-host');
    const origin = details.opener.closest('.queued-file, .attachment, .link-preview') || details.opener;
    const sourceImage = origin.querySelector('img');
    const sourceRect = (sourceImage || details.player || origin).getBoundingClientRect();
    const viewer = { ...details, origin, sourceRect, session: sessionKey(snapshot), threadId: currentThreadId(),
      messageId: origin.closest('.message')?.dataset.messageId, version: ++viewerVersion, closing: false };
    mediaViewer = viewer; imageReturnFocus = details.opener;
    dialog.classList.remove('is-image', 'is-video', 'is-audio', 'is-closing');
    dialog.classList.add('is-' + details.kind, 'is-opening');
    dialog.setAttribute('aria-label', details.name || t(details.kind));
    image.hidden = details.kind !== 'image'; host.hidden = details.kind === 'image';
    if (details.kind === 'image') {
      document.getSelection()?.removeAllRanges();
      image.alt = details.name || t('image'); image.src = details.url;
    }
    if (details.player) {
      const player = details.player, surface = M?.shellFor(player) || player, rect = surface.getBoundingClientRect();
      const placeholder = e('div', 'queued-player-placeholder'); placeholder.setAttribute('aria-hidden', 'true');
      placeholder.style.width = rect.width + 'px'; placeholder.style.height = rect.height + 'px'; placeholder.append(R.icon('play'));
      surface.before(placeholder); viewer.placeholder = placeholder; viewer.surface = surface;
      movePlayer(player, host);
      if (surface !== player) M.update(surface, { mode: 'viewer', locale: snapshot.locale, t,
        onExpand: () => closeImageViewer() });
      // A load failure must never leave detached playback or a dead modal open.
      viewer.onError = () => closeImageViewer(true, true, true);
      player.addEventListener('error', viewer.onError, { once: true });
    }
    if (!dialog.open) dialog.showModal();
    requestAnimationFrame(() => {
      if (mediaViewer !== viewer || viewer.closing) return;
      dialog.classList.remove('is-opening');
      const target = details.kind === 'image' ? image : host;
      const reveal = () => {
        if (mediaViewer !== viewer || viewer.closing) return;
        const rect = target.getBoundingClientRect();
        const fromThumbnail = details.kind === 'image' && rect.width > 0 && sourceRect.width > 0;
        const scale = fromThumbnail ? Math.min(1, sourceRect.width / rect.width) : .97;
        const dx = fromThumbnail ? sourceRect.left + sourceRect.width / 2 - rect.left - rect.width / 2 : 0;
        const dy = fromThumbnail ? sourceRect.top + sourceRect.height / 2 - rect.top - rect.height / 2 : 0;
        animate(target, [{ opacity: 0, transform: 'translate(' + dx + 'px,' + dy + 'px) scale(' + scale + ')' }, { opacity: 1, transform: 'translate(0,0) scale(1)' }], 200);
      };
      if (details.kind === 'image' && !image.complete) image.addEventListener('load', reveal, { once: true }); else reveal();
    });
    dialog.focus({ preventScroll: true }); publishComposerState(); renderDropOverlay();
  }
  function viewerValidFor(value) {
    const viewer = mediaViewer;
    if (!viewer) return true;
    if (viewer.session !== sessionKey(value) || viewer.threadId !== value.selectedThreadId || !playbackActive(value)) return false;
    if (viewer.uploadId) {
      const upload = (value.draft?.attachments || []).find(item => (item.id || item.localId) === viewer.uploadId);
      if (!upload || editTarget) return false;
      const info = uploadPresentation(upload, value);
      return info.url === viewer.url && info.kind === viewer.kind;
    }
    const message = (value.messages || []).find(item => item.id === viewer.messageId && !item.deletedAt);
    if (viewer.previewId) return !!message?.linkPreviews?.some(item => item.id === viewer.previewId && !item.isRemoved);
    return !!message?.attachments?.some(item => item.id === viewer.attachmentId);
  }
  function closeImageViewer(restoreFocus = true, immediate = false, pause = false) {
    const dialog = $('image-dialog'), viewer = mediaViewer;
    if (!dialog.open && !viewer) return;
    if (viewer?.closing && !immediate) return;
    if (viewer) viewer.closing = true;
    if (viewer?.player && pause) viewer.player.pause();
    if (document.fullscreenElement && dialog.contains(document.fullscreenElement)) document.exitFullscreen().catch(() => {});
    const version = ++viewerVersion, returnFocus = imageReturnFocus;
    const finish = () => {
      if (version !== viewerVersion) return;
      mediaViewer = null; imageReturnFocus = null;
      closeMenus(true);
      if (dialog.open) dialog.close();
      if (viewer?.player) {
        const player = viewer.player;
        player.removeEventListener('error', viewer.onError);
        if (pause || !viewer.origin.isConnected) player.pause();
        if (viewer.placeholder?.isConnected && viewer.origin.isConnected) {
          movePlayer(player, viewer.placeholder.parentElement, viewer.placeholder, !pause);
          const shell = M?.shellFor(player);
          if (shell) M.update(shell, { mode: viewer.uploadId ? 'draft' : 'inline', locale: snapshot.locale, t,
            onExpand: (_, opener) => viewer.uploadId ? viewDraftMedia(viewer.origin, opener)
              : viewMedia({ id: viewer.attachmentId, previewId: viewer.previewId, fileName: viewer.name }, player, opener) });
        }
        else {
          player._atlasMoveVersion = (player._atlasMoveVersion || 0) + 1;
          const shell = M?.shellFor(player);
          if (shell) M.dispose(shell, { pause: true }); else player.pause();
          (shell || player).remove();
        }
        viewer.placeholder?.remove();
      }
      $('image-dialog-image').removeAttribute('src');
      $('image-dialog-image')._atlasMotion?.cancel(); $('media-dialog-player-host')._atlasMotion?.cancel();
      $('media-dialog-player-host').replaceChildren();
      dialog.classList.remove('is-opening', 'is-closing');
      publishComposerState(); renderDropOverlay();
      if (restoreFocus && returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
    };
    if (immediate || !viewer || motionPreference.matches || !active()) { finish(); return; }
    dialog.classList.add('is-closing');
    const target = viewer.kind === 'image' ? $('image-dialog-image') : $('media-dialog-player-host');
    afterAnimation(animate(target, [{ opacity: 1, transform: 'scale(1)' }, { opacity: 0, transform: 'scale(.98)' }], 130), finish);
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
      } else if (choosing || loading) {
        const indicator = e('span', 'inline-busy');
        indicator.setAttribute('role', 'status');
        indicator.setAttribute('aria-label', t(choosing ? 'addingArmory' : 'loadingCharacters'));
        status.append(indicator);
      }
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
  $('load-earlier-button').addEventListener('click', () => { if (conversationSearch?.isOpen() || conversationSearch?.isLoadingHistory()) return; const first = snapshot.messages.filter(message => R.id(message.id)).sort((a, b) => R.compareIds(a.id, b.id))[0]; if (selectedThread && first) action('loadEarlier', { threadId: selectedThread.id, beforeId: first.id }); });
  $('jump-latest-button').addEventListener('click', () => { conversationSearch?.close(false); searchReadSuppressed = false; scrollBottom(true); updateJumpButton(); requestAnimationFrame(requestRead); });
  $('send-button').addEventListener('click', sendMessage); $('cancel-context-button').addEventListener('click', cancelContext);
  $('attach-button').addEventListener('click', () => { if (currentThreadId()) action('pickFiles', { threadId: currentThreadId() }); });
  $('share-game-button').addEventListener('click', showCardComposer); $('close-armory-picker').addEventListener('click', () => closeArmoryPicker(true));
  composer.addEventListener('input', () => { if (!conversationSearch?.isOpen()) searchReadSuppressed = false; resizeComposer(); saveLocalDraft(); updateComposer(); sendTyping(); });
  composer.addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey && !event.isComposing && event.keyCode !== 229) { event.preventDefault(); sendMessage(); } });
  composer.addEventListener('paste', event => {
    if (!canReceiveFiles()) return;
    const items = Array.from(event.clipboardData?.items || []);
    if (items.some(item => item.kind === 'file')) { event.preventDefault(); action('pasteImage', { threadId: currentThreadId() }); }
  });
  let dragDepth = 0, nativeDropActive = false;
  function canReceiveFiles() { return !!currentThreadId() && hasIdentity() && capable('attachments') && !!selectedThread?.canSend && !editTarget && !$('image-dialog').open; }
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
    if (!programmaticScroll && !layoutPending) { followBottom = !conversationSearch?.isOpen() && isAtBottom(36); stableAnchor = followBottom ? null : captureAnchor(); lastUserScroll = performance.now(); }
    updateJumpButton(); requestAnimationFrame(requestRead);
  }, { passive: true });
  const interruptScroll = () => { if (!conversationSearch?.isOpen()) searchReadSuppressed = false; cancelSmoothScroll(); if (layoutPending) { layoutPending = false; ++renderVersion; } programmaticScroll = false; };
  timeline.addEventListener('wheel', interruptScroll, { passive: true });
  timeline.addEventListener('pointerdown', interruptScroll, { passive: true });
  timeline.addEventListener('touchstart', interruptScroll, { passive: true });
  timeline.addEventListener('keydown', event => { if (['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' '].includes(event.key)) interruptScroll(); });
  if (global.ResizeObserver) { const observer = new ResizeObserver(() => {
    if (!selectedThread || layoutPending || smoothScroll || timeline.clientHeight <= 0) return;
    if (followBottom) scrollBottom(); else if (stableAnchor && performance.now() - lastUserScroll > 40) restoreAnchor(stableAnchor);
    updateJumpButton(); requestAnimationFrame(requestRead);
  }); observer.observe(messageList); observer.observe(timeline); }
  global.addEventListener('resize', () => { cancelSmoothScroll(); if (selectedThread) settleLayout(stableAnchor, followBottom); resizeComposer(); });
  document.addEventListener('click', event => {
    const link = event.target.closest('a[data-external-link]');
    if (link) { event.preventDefault(); const url = R.safeUrl(link.getAttribute('href')); if (url) action('openExternal', { url }); }
    if (!event.target.closest('#context-menu, #reaction-picker, .message-actions, #thread-menu-button, .details-member')) closeMenus();
  });
  document.addEventListener('keydown', event => {
    if (event.isComposing || event.keyCode === 229) return;
    if (event.ctrlKey && !event.shiftKey && !event.altKey && !event.metaKey && event.key.toLowerCase() === 'f') {
      if (conversationSearch?.open()) event.preventDefault();
      return;
    }
    if ($('image-dialog').open) {
      if (event.key === 'Escape') {
        event.preventDefault();
        if (!$('context-menu').hidden && !$('context-menu').inert) { closeMenus(); menuReturnFocus?.focus({ preventScroll: true }); }
        else closeImageViewer();
      }
      return;
    }
    const floating = !$('reaction-picker').hidden && !$('reaction-picker').inert ? $('reaction-picker') : !$('context-menu').hidden && !$('context-menu').inert ? $('context-menu') : null;
    if (event.key === 'Escape' && !floating && !$('app-dialog').open && conversationSearch?.isOpen()) { event.preventDefault(); conversationSearch.close(); return; }
    if (event.key === 'Escape') { if (floating) { event.preventDefault(); closeMenus(); menuReturnFocus?.focus({ preventScroll: true }); } else if (armoryPickerOpen) { event.preventDefault(); closeArmoryPicker(true); } else if (editTarget || replyTarget) { event.preventDefault(); cancelContext(); } }
    if (floating && ['ArrowDown', 'ArrowUp', 'ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
      event.preventDefault(); const buttons = Array.from(floating.querySelectorAll('button')), index = buttons.indexOf(document.activeElement);
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : (index + (event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -1 : 1) + buttons.length) % buttons.length;
      buttons[next]?.focus();
    }
    if ((event.key === 'ContextMenu' || event.key === 'F10' && event.shiftKey) && document.activeElement?.classList.contains('message')) { event.preventDefault(); const node = document.activeElement, rect = node.getBoundingClientRect(); showMessageMenu(node._message, rect.right - 25, rect.top + 15); }
  });
  $('app-dialog').addEventListener('close', () => { dialogRefresh = null; closeMenus(true); });
  $('app-dialog').addEventListener('cancel', event => { event.preventDefault(); closeDialog(); });
  $('app-dialog').querySelector('form').addEventListener('submit', event => { event.preventDefault(); closeDialog(); });
  $('app-dialog').addEventListener('click', event => { if (event.target === $('app-dialog')) { const rect = $('app-dialog').getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) closeDialog(); } });
  $('image-dialog').addEventListener('cancel', event => { event.preventDefault(); closeImageViewer(); });
  $('image-dialog').addEventListener('click', event => { if (event.target === $('image-dialog')) closeImageViewer(); });
  $('image-dialog').addEventListener('contextmenu', event => {
    if (mediaViewer && (event.target === $('image-dialog-image') || $('media-dialog-player-host').contains(event.target)))
      showAttachmentMenu({ id: mediaViewer.attachmentId, uploadId: mediaViewer.uploadId, kind: mediaViewer.kind }, event);
  });
  $('image-dialog-image').addEventListener('dragstart', event => event.preventDefault());
  $('image-dialog-image').addEventListener('selectstart', event => event.preventDefault());
  document.addEventListener('visibilitychange', () => { if (document.hidden) { flushDraft(); stopTyping(); clearMotion(); closeMenus(true); dragDepth = 0; nativeDropActive = false; renderDropOverlay(); } else requestAnimationFrame(requestRead); });
  global.addEventListener('pagehide', () => { flushDraft(); stopTyping(); closeImageViewer(false, true, true); clearMotion(); R.suspendMedia(document, true); });
  setInterval(renderTyping, 2000);
  if (global.chrome?.webview) chrome.webview.addEventListener('message', event => receiveMessage(event.data));
  global.AtlasChat = Object.freeze({ applySnapshot, receive: receiveMessage, version: 2 });
  localize(); renderConversations(); updateComposer(); action('ready', {}, { silent: true });
})(window);
