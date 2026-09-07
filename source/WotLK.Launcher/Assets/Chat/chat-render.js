(function (global) {
  'use strict';

  const SVG_NS = 'http://www.w3.org/2000/svg';
  const MAX_BODY = 1000;
  const DECIMAL_ID = /^(0|[1-9][0-9]*)$/;
  const MEDIA_ORIGIN = 'https://atlas-chat-media.invalid';

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function icon(name) {
    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('class', 'icon');
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS(SVG_NS, 'use');
    use.setAttribute('href', '#i-' + name);
    svg.append(use);
    return svg;
  }

  function button(className, label, iconName, handler) {
    const node = element('button', className);
    node.type = 'button';
    node.title = label;
    node.setAttribute('aria-label', label);
    if (iconName) node.append(icon(iconName));
    if (handler) node.addEventListener('click', handler);
    return node;
  }

  // Every database Int64 stays decimal text. No Number conversion or subtraction.
  function id(value) { return typeof value === 'string' && DECIMAL_ID.test(value) ? value : ''; }
  function compareIds(left, right) {
    left = id(left); right = id(right);
    return left.length - right.length || (left < right ? -1 : left > right ? 1 : 0);
  }
  function safeUrl(value) {
    if (typeof value !== 'string' || value.length > 8192 || /[\u0000-\u001f\u007f]/.test(value)) return '';
    try {
      const url = new URL(value);
      return (url.protocol === 'https:' || url.protocol === 'http:') && !url.username && !url.password ? url.href : '';
    } catch (_) { return ''; }
  }
  function mediaUrl(value, origin) {
    if (typeof value !== 'string' || !value) return '';
    try {
      const expected = new URL(origin || MEDIA_ORIGIN).origin;
      if (expected !== MEDIA_ORIGIN) return '';
      const url = new URL(value, MEDIA_ORIGIN);
      if (url.username || url.password || !['http:', 'https:'].includes(url.protocol)) return '';
      if (url.origin === expected && !url.pathname.startsWith('/api/')) return url.href;
      const attachment = /^\/api\/v2\/chat\/attachments\/([A-Za-z0-9_-]+)$/.exec(url.pathname);
      if (attachment) return expected + '/attachments/' + attachment[1];
      if (['/api/v2/chat/preview-image', '/api/v2/chat/linked-media'].includes(url.pathname)) {
        const candidate = url.searchParams.get('url');
        const remote = safeUrl(candidate) ? candidate : '';
        return remote ? expected + '/' + url.pathname.split('/').at(-1) + '?url=' + encodeURIComponent(remote) : '';
      }
      return '';
    } catch (_) { return ''; }
  }
  function previewImageUrl(value, origin) {
    return mediaUrl(value, origin) || (safeUrl(value) ? MEDIA_ORIGIN + '/preview-image?url=' + encodeURIComponent(value) : '');
  }
  function embedUrl(value) {
    const safe = safeUrl(value);
    if (!safe) return '';
    const url = new URL(safe);
    if (url.protocol !== 'https:') return '';
    if (['www.youtube.com', 'www.youtube-nocookie.com'].includes(url.hostname) && /^\/embed\/[a-zA-Z0-9_-]{11}$/.test(url.pathname)) {
      const clean = new URL('https://www.youtube-nocookie.com' + url.pathname);
      clean.searchParams.set('rel', '0');
      clean.searchParams.set('playsinline', '1');
      const start = url.searchParams.get('start');
      if (start && /^\d{1,7}$/.test(start)) clean.searchParams.set('start', start);
      return clean.href;
    }
    if (url.hostname === 'player.vimeo.com' && /^\/video\/\d+$/.test(url.pathname)) return url.origin + url.pathname + '?dnt=1';
    return '';
  }
  function linkedMediaUrl(value, origin) {
    const local = mediaUrl(value, origin);
    if (local) return local;
    const remote = safeUrl(value) ? value : '';
    return remote ? MEDIA_ORIGIN + '/linked-media?url=' + encodeURIComponent(remote) : '';
  }
  function initials(name) {
    const text = String(name || '').trim();
    if (!text) return '?';
    if (global.Intl && Intl.Segmenter) return new Intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text)[Symbol.iterator]().next().value.segment.toLocaleUpperCase();
    return Array.from(text)[0].toLocaleUpperCase();
  }
  function formatBytes(value, locale) {
    const bytes = Number(value);
    if (!Number.isFinite(bytes) || bytes < 0) return '';
    const english = String(locale || '').startsWith('en');
    if (bytes < 1000) return Math.round(bytes) + (english ? ' B' : ' o');
    const units = english ? ['kB', 'MB', 'GB'] : ['Ko', 'Mo', 'Go'];
    let amount = bytes / 1000, position = 0;
    while (amount >= 1000 && position < 2) { amount /= 1000; position++; }
    return new Intl.NumberFormat(english ? 'en-US' : 'fr-FR', { maximumFractionDigits: amount < 10 ? 1 : 0 }).format(amount) + ' ' + units[position];
  }
  function validDate(value) { const date = new Date(value); return Number.isFinite(date.getTime()) ? date : null; }
  function time(value, locale) { const date = validDate(value); return date ? date.toLocaleTimeString(locale || 'fr', { hour: '2-digit', minute: '2-digit' }) : ''; }
  function fullDate(value, locale) { const date = validDate(value); return date ? date.toLocaleString(locale || 'fr', { dateStyle: 'full', timeStyle: 'short' }) : ''; }
  function dayKey(value) {
    const date = validDate(value);
    return date ? date.getFullYear() + '-' + String(date.getMonth() + 1).padStart(2, '0') + '-' + String(date.getDate()).padStart(2, '0') : '';
  }
  function dayLabel(value, locale, t) {
    const date = validDate(value);
    if (!date) return '';
    if (dayKey(date) === dayKey(new Date())) return t('today');
    const yesterday = new Date(); yesterday.setDate(yesterday.getDate() - 1);
    if (dayKey(date) === dayKey(yesterday)) return t('yesterday');
    return date.toLocaleDateString(locale || 'fr', { day: 'numeric', month: 'long', year: 'numeric' });
  }
  function presenceText(profile, t) {
    if (!profile) return '';
    if (profile.presence === 'game' || profile.presence === 'in-game' || profile.presence === 'ingame') {
      return profile.characterName ? t('playingOn') + ' ' + profile.characterName + (profile.zoneName ? ' · ' + profile.zoneName : '') : t('inGame');
    }
    if (profile.presence === 'online' || profile.presence === 'launcher') return t('online');
    if (profile.presence === 'dnd') return t('dnd');
    return t('offline');
  }
  function avatar(profile, options) {
    options = options || {};
    const node = element('span', 'avatar' + (options.small ? ' is-small' : '') + (options.tiny ? ' is-tiny' : '') + (options.group ? ' is-group' : ''));
    const name = profile && (profile.username || profile.title) || '';
    const url = mediaUrl(profile && profile.avatarUrl, options.mediaOrigin);
    if (url) {
      const image = element('img'); image.alt = ''; image.src = url; image.draggable = false; image.loading = 'lazy'; image.decoding = 'async';
      image.addEventListener('error', () => { image.remove(); node.prepend(options.group ? icon('group') : document.createTextNode(initials(name))); }, { once: true });
      node.append(image);
    } else if (options.group) node.append(icon('group'));
    else node.append(document.createTextNode(initials(name)));
    if (options.presence && !options.group) {
      const status = String(profile && profile.presence || 'offline');
      const online = ['online', 'launcher'].includes(status);
      const game = ['game', 'ingame', 'in-game'].includes(status);
      node.append(element('span', 'presence-dot ' + (online ? 'online' : game ? 'game' : status === 'dnd' ? 'dnd' : 'offline')));
    }
    node.setAttribute('aria-hidden', 'true');
    return node;
  }

  let markdown;
  function getMarkdown() {
    if (markdown) return markdown;
    if (typeof global.markdownit !== 'function') return null;
    markdown = global.markdownit({ html: false, linkify: true, breaks: true, typographer: false });
    markdown.validateLink = value => !!safeUrl(value);
    const linkOpen = markdown.renderer.rules.link_open || ((tokens, idx, options, env, self) => self.renderToken(tokens, idx, options));
    markdown.renderer.rules.link_open = (tokens, idx, options, env, self) => {
      tokens[idx].attrSet('rel', 'noopener noreferrer');
      tokens[idx].attrSet('data-external-link', 'true');
      return linkOpen(tokens, idx, options, env, self);
    };
    // Remote Markdown images never load on receipt. Attachments are rendered separately.
    markdown.renderer.rules.image = (tokens, idx) => {
      const token = tokens[idx], url = safeUrl(token.attrGet('src'));
      const label = markdown.utils.escapeHtml(token.content || url || 'Image');
      return url ? '<a data-external-link="true" rel="noopener noreferrer" href="' + markdown.utils.escapeHtml(url) + '">' + label + '</a>' : label;
    };
    // Run on text tokens, after Markdown has isolated links/code. The built-in
    // text tokenizer intentionally consumes pipes, so an inline ruler alone
    // would miss spoilers surrounded by ordinary prose.
    markdown.core.ruler.after('inline', 'atlas_spoiler', state => {
      for (const block of state.tokens) {
        if (block.type !== 'inline' || !block.children) continue;
        const children = [];
        let inLink = 0;
        for (const child of block.children) {
          if (child.type === 'link_open') inLink++;
          if (child.type === 'link_close') inLink--;
          if (child.type !== 'text' || inLink || !child.content.includes('||')) { children.push(child); continue; }
          const pattern = /\|\|([^\n]+?)\|\|/g;
          let match, offset = 0;
          while ((match = pattern.exec(child.content))) {
            if (match.index > offset) { const text = new state.Token('text', '', 0); text.content = child.content.slice(offset, match.index); children.push(text); }
            const spoiler = new state.Token('atlas_spoiler', '', 0); spoiler.content = match[1]; children.push(spoiler); offset = match.index + match[0].length;
          }
          if (offset === 0) children.push(child);
          else if (offset < child.content.length) { const text = new state.Token('text', '', 0); text.content = child.content.slice(offset); children.push(text); }
        }
        block.children = children;
      }
    });
    markdown.renderer.rules.atlas_spoiler = (tokens, idx) => '<button type="button" class="spoiler" aria-expanded="false"><span aria-hidden="true">' + markdown.utils.escapeHtml(tokens[idx].content) + '</span></button>';
    return markdown;
  }
  function renderMarkdown(body, t) {
    const node = element('div', 'message-content');
    const parser = getMarkdown();
    if (parser) node.innerHTML = parser.render(String(body || '').slice(0, MAX_BODY));
    else node.textContent = String(body || '').slice(0, MAX_BODY);
    for (const spoiler of node.querySelectorAll('.spoiler')) {
      spoiler.setAttribute('aria-label', t('revealSpoiler'));
      spoiler.addEventListener('click', () => {
        const expanded = spoiler.getAttribute('aria-expanded') !== 'true';
        spoiler.setAttribute('aria-expanded', String(expanded));
        spoiler.firstElementChild.setAttribute('aria-hidden', String(!expanded));
        spoiler.setAttribute('aria-label', expanded ? t('hideSpoiler') : t('revealSpoiler'));
      });
    }
    return node;
  }

  function fileFooter(attachment, context) {
    const footer = element('div', 'attachment-footer');
    footer.append(icon('file'));
    const details = element('span', 'attachment-info');
    const name = button('attachment-name', attachment.fileName || context.t('download'), null, () => context.action('openAttachment', { attachmentId: attachment.id, fileName: attachment.fileName }));
    name.textContent = attachment.fileName;
    details.append(name, element('span', 'attachment-size', formatBytes(attachment.size, context.locale)));
    footer.append(details, button('icon-button', context.t('download'), 'download', () => context.action('downloadAttachment', { attachmentId: attachment.id, fileName: attachment.fileName })));
    return footer;
  }
  function attachmentNode(attachment, context) {
    const node = element('div', 'attachment'); node.dataset.attachmentId = String(attachment.id || '');
    const url = mediaUrl(attachment.url, context.mediaOrigin) || (attachment.id ? MEDIA_ORIGIN + '/attachments/' + encodeURIComponent(attachment.id) : '');
    const contentType = String(attachment.contentType || '');
    const kind = String(attachment.kind || 'document');
    const isImage = kind === 'image' || kind === 'animated-image' || kind === 'animation' || /^image\//.test(contentType);
    if (isImage && url) {
      const open = button('attachment-image' + (kind === 'animation' || contentType === 'image/gif' ? ' is-animated' : ''), context.t('viewImage'), null, () => context.viewImage(attachment));
      const image = element('img'); image.alt = attachment.fileName || context.t('image'); image.loading = 'lazy'; image.decoding = 'async'; image.draggable = false; image.src = url;
      image.addEventListener('error', () => { open.replaceChildren(element('span', 'media-placeholder-label', context.t('imageUnavailable'))); open.style.minHeight = '80px'; }, { once: true });
      open.append(image); node.append(open);
    } else if (kind === 'video' && url) {
      const video = element('video'); video.controls = true; video.preload = 'none'; video.playsInline = true; video.src = url;
      video.setAttribute('aria-label', attachment.fileName || context.t('video'));
      video.setAttribute('controlsList', 'nodownload');
      const thumbnail = mediaUrl(attachment.thumbnailUrl, context.mediaOrigin); if (thumbnail) video.poster = thumbnail;
      node.append(video);
    } else if (kind === 'audio' && url) {
      const audio = element('audio'); audio.controls = true; audio.preload = 'none'; audio.src = url;
      audio.setAttribute('aria-label', attachment.fileName || context.t('audio')); audio.setAttribute('controlsList', 'nodownload');
      node.append(audio);
    }
    node.append(fileFooter(attachment, context));
    return node;
  }
  function isVideoPreview(preview) { return ['video', 'youtube', 'vimeo'].includes(String(preview.kind || '').toLowerCase()); }
  function linkPreviewNode(preview, message, context) {
    const node = element('div', 'link-preview'); node.dataset.previewId = String(preview.id || '');
    const video = isVideoPreview(preview);
    const url = safeUrl(preview.url);
    const imageUrl = previewImageUrl(preview.imageUrl, context.mediaOrigin);
    if (!video && preview.canRemove !== false && context.capable('link-previews')) {
      node.append(button('icon-button preview-dismiss', context.t('removePreviewForEveryone'), 'close', () => context.dismissPreview(message, preview)));
    }
    const main = button('link-preview-main', preview.title || url || context.t('openLink'), null, () => { if (url) context.action('openExternal', { url }); });
    main.append(element('div', 'link-preview-provider', preview.provider || (url ? new URL(url).hostname.replace(/^www\./, '') : '')));
    main.append(element('div', 'link-preview-title', preview.title || url));
    if (preview.description) main.append(element('div', 'link-preview-description', preview.description));
    if (!video && imageUrl) {
      const image = element('img', 'link-preview-image'); image.alt = ''; image.src = imageUrl; image.loading = 'lazy'; image.decoding = 'async';
      image.addEventListener('error', () => image.remove(), { once: true }); main.append(image);
    }
    node.append(main);
    const embedded = embedUrl(preview.embedUrl);
    const sourceUrl = safeUrl(preview.embedUrl || preview.url);
    const isProviderPage = sourceUrl && /(^|\.)(youtube\.com|youtube-nocookie\.com|youtu\.be|vimeo\.com)$/.test(new URL(sourceUrl).hostname);
    const directMedia = !embedded && !isProviderPage && (video || preview.kind === 'audio') ? linkedMediaUrl(preview.embedUrl || preview.url, context.mediaOrigin) : '';
    if (video && (embedded || directMedia)) {
      const playerHost = element('div', 'preview-player');
      const showPlaceholder = () => {
        const play = button('media-placeholder', context.t('playVideo'), null, () => {
          if (!context.isActive()) return;
          if (embedded) {
            const frame = element('iframe'); frame.title = preview.title || context.t('video'); frame.src = embedded;
            frame.loading = 'lazy'; frame.allowFullscreen = true;
            frame.setAttribute('allow', 'autoplay; encrypted-media; picture-in-picture; fullscreen');
            frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-presentation');
            frame.referrerPolicy = 'strict-origin-when-cross-origin';
            playerHost.replaceChildren(frame);
          } else {
            const player = element('video'); player.controls = true; player.preload = 'none'; player.playsInline = true; player.src = directMedia;
            player.setAttribute('aria-label', preview.title || context.t('video')); playerHost.replaceChildren(player);
            player.play().catch(() => {});
          }
        });
        if (imageUrl) { const image = element('img'); image.alt = ''; image.src = imageUrl; image.loading = 'lazy'; image.addEventListener('error', () => image.remove(), { once: true }); play.append(image); }
        const circle = element('span', 'media-play'); circle.append(icon('play')); play.append(circle, element('span', 'media-placeholder-label', context.t('playVideo')));
        playerHost.replaceChildren(play);
      };
      playerHost._suspendPlayer = showPlaceholder;
      showPlaceholder(); node.append(playerHost);
    } else if (preview.kind === 'audio' && directMedia) {
      const audio = element('audio'); audio.controls = true; audio.preload = 'none'; audio.src = directMedia; node.append(audio);
    }
    if (video && url) {
      const fallback = button('preview-fallback', context.t('openOnSite'), 'external', () => context.action('openExternal', { url }));
      fallback.append(element('span', '', context.t('openOnSite'))); node.append(fallback);
    }
    return node;
  }

  function cardNode(card, message, context) {
    const node = element('div', 'game-card');
    const heading = element('div', 'game-card-heading');
    const imageUrl = previewImageUrl(card.imageUrl, context.mediaOrigin);
    if (imageUrl) { const image = element('img', 'game-card-image'); image.alt = ''; image.src = imageUrl; image.loading = 'lazy'; image.addEventListener('error', () => image.remove(), { once: true }); heading.append(image); }
    const title = element('div', 'game-card-title', card.title || context.t('gameCard'));
    const qualityColors = { poor: '#a2a8ae', common: '#dce7ef', uncommon: '#70cd79', rare: '#71acf0', epic: '#c799f3', legendary: '#efb36d', '0': '#a2a8ae', '1': '#dce7ef', '2': '#70cd79', '3': '#71acf0', '4': '#c799f3', '5': '#efb36d' };
    const quality = String(card.fields?.quality || '').toLowerCase(); if (card.kind === 'item' && qualityColors[quality]) title.style.color = qualityColors[quality];
    heading.append(title); node.append(heading);
    const labels = { item: 'item', character: 'character', quest: 'quest', location: 'location', outing: 'outing' };
    node.append(element('div', 'game-card-subtitle', context.t(labels[card.kind] || 'gameCard')));
    if (card.description) node.append(element('div', 'game-card-details', card.description));
    const fields = card.fields || {};
    const lines = [];
    for (const [key, label] of [['date', 'date'], ['startsAt', 'date'], ['zone', 'location'], ['level', 'level']]) if (fields[key]) lines.push(context.t(label) + ' · ' + (key === 'startsAt' && validDate(fields[key]) ? fullDate(fields[key], context.locale) : fields[key]));
    if (fields.stats) lines.push(fields.stats);
    if (lines.length) node.append(element('div', 'game-card-details', lines.join('\n')));
    const actions = element('div', 'game-card-actions');
    if (card.kind === 'character' && fields.accountId && /^\d+$/.test(fields.accountId)) {
      const open = button('', context.t('openProfile'), null, () => context.action('openProfile', { accountId: Number(fields.accountId), characterGuid: fields.characterGuid && /^\d+$/.test(fields.characterGuid) ? Number(fields.characterGuid) : undefined }));
      open.textContent = context.t('openProfile'); actions.append(open);
    } else if (safeUrl(card.url)) {
      const open = button('', context.t('openLink'), null, () => context.action('openExternal', { url: safeUrl(card.url) })); open.textContent = context.t('openLink'); actions.append(open);
    }
    if (card.kind === 'outing') {
      const own = (card.responses || []).find(response => response.accountId === context.ownerAccountId);
      for (const role of ['tank', 'healer', 'damage']) {
        const joined = (card.responses || []).filter(response => response.status === 'joining' && response.role === role);
        const active = own && own.status === 'joining' && own.role === role;
        const join = button(active ? 'is-selected' : '', context.t(role), null, () => context.action('cardResponse', { threadId: message.threadId, messageId: message.id, status: active ? 'declined' : 'joining', role }));
        join.textContent = context.t(role) + (joined.length ? ' · ' + joined.length : ''); join.setAttribute('aria-pressed', String(!!active));
        if (joined.length) join.title = joined.map(response => response.username).join(', ');
        actions.append(join);
      }
    }
    if (actions.childElementCount) node.append(actions);
    return node;
  }

  function canGroup(previous, message) {
    if (!previous || previous.deletedAt || message.deletedAt) return false;
    const previousDate = validDate(previous.createdAt), currentDate = validDate(message.createdAt);
    return !!previousDate && !!currentDate && previous.sender && message.sender && previous.sender.accountId === message.sender.accountId
      && previous.origin === message.origin && previous.senderCharacterName === message.senderCharacterName
      && dayKey(previousDate) === dayKey(currentDate) && currentDate - previousDate >= 0 && currentDate - previousDate <= 5 * 60 * 1000;
  }

  function reconcileKeyed(container, values, keyOf, render, signatureOf) {
    const byKey = new Map(Array.from(container.children).map(node => [node.dataset.key, node]));
    let cursor = container.firstElementChild;
    for (const value of values) {
      const key = String(keyOf(value));
      let node = byKey.get(key);
      const signature = signatureOf ? signatureOf(value) : JSON.stringify(value);
      if (!node || node._signature !== signature) {
        const replacement = render(value);
        replacement.dataset.key = key; replacement._signature = signature;
        if (node) { suspendMedia(node); node.replaceWith(replacement); if (cursor === node) cursor = replacement; }
        node = replacement;
      }
      if (node !== cursor) container.insertBefore(node, cursor);
      cursor = node.nextElementSibling;
      byKey.delete(key);
    }
    for (const node of byKey.values()) { suspendMedia(node); node.remove(); }
  }

  function reconcileMessage(node, message, context) {
    const own = message.sender && message.sender.accountId === context.ownerAccountId;
    if (!node) {
      node = element('article', 'message'); node.dataset.messageId = message.id; node.tabIndex = -1;
      const avatarHost = element('div', 'message-avatar');
      const main = element('div', 'message-main');
      const heading = element('div', 'message-heading');
      const pin = element('div', 'message-pin-label');
      const reply = element('div', 'message-reply-host');
      const body = element('div', 'message-body-host');
      const attachments = element('div', 'message-attachments');
      const previews = element('div', 'message-previews');
      const cards = element('div', 'message-cards');
      const reactions = element('div', 'message-reactions');
      const status = element('div', 'message-state');
      main.append(pin, heading, reply, body, attachments, previews, cards, reactions, status);
      const actions = element('div', 'message-actions');
      const hoverTime = element('span', 'message-hover-time');
      node.append(avatarHost, main, actions, hoverTime);
      node._parts = { avatarHost, main, heading, pin, reply, body, attachments, previews, cards, reactions, status, actions, hoverTime };
      node.addEventListener('contextmenu', event => { event.preventDefault(); context.messageMenu(node._message, event.clientX, event.clientY); });
    }
    node._message = message;
    node.classList.toggle('is-own', !!own);
    node.classList.toggle('is-continuation', !!context.grouped);
    node.classList.toggle('is-group-start', !context.grouped);
    node.setAttribute('aria-label', (message.sender && message.sender.username || '') + ', ' + fullDate(message.createdAt, context.locale));
    const p = node._parts;
    const headerKey = JSON.stringify([message.sender, message.origin, message.senderCharacterName, message.createdAt, context.locale]);
    if (node._headerKey !== headerKey) {
      node._headerKey = headerKey;
      p.avatarHost.replaceChildren(avatar(message.sender, { small: true, mediaOrigin: context.mediaOrigin }));
      const author = element('span', 'message-author', message.sender && message.sender.username);
      const timestamp = element('time', 'message-time', time(message.createdAt, context.locale)); timestamp.dateTime = message.createdAt; timestamp.title = fullDate(message.createdAt, context.locale);
      p.heading.replaceChildren(author, timestamp);
      if (['game', 'in-game', 'ingame'].includes(message.origin)) p.heading.append(element('span', 'message-origin', message.senderCharacterName || context.t('inGame')));
      p.hoverTime.textContent = time(message.createdAt, context.locale);
    }
    p.pin.hidden = !message.isPinned;
    if (message.isPinned && p.pin._locale !== context.locale) { p.pin.replaceChildren(icon('pin'), document.createTextNode(context.t('pinnedMessage'))); p.pin._locale = context.locale; }
    const bodyKey = JSON.stringify([message.body, message.deletedAt, message.editedAt, context.locale]);
    if (node._bodyKey !== bodyKey) {
      node._bodyKey = bodyKey;
      p.body.replaceChildren(message.deletedAt ? element('div', 'message-deleted', context.t('deletedMessage')) : renderMarkdown(message.body, context.t));
      if (message.editedAt && !message.deletedAt) {
        const edited = element('span', 'message-edited', '(' + context.t('edited') + ')'); edited.title = fullDate(message.editedAt, context.locale);
        const last = p.body.firstElementChild.lastElementChild;
        if (last && last.tagName === 'P') last.append(edited); else p.body.append(edited);
      }
    }
    const replyKey = JSON.stringify([message.replyTo, message.deletedAt, context.locale]);
    if (node._replyKey !== replyKey) {
      node._replyKey = replyKey; p.reply.replaceChildren();
      if (message.replyTo && !message.deletedAt) {
        const reply = button('message-reply', context.t('goToMessage'), 'reply', () => context.jumpTo(message.replyTo.messageId));
        reply.append(element('strong', '', message.replyTo.senderUsername), element('span', '', message.replyTo.isDeleted ? context.t('deletedMessage') : message.replyTo.body)); p.reply.append(reply);
      }
    }
    const attachments = message.deletedAt ? [] : (message.attachments || []);
    const previews = message.deletedAt ? [] : (message.linkPreviews || []).filter(preview => !preview.isRemoved);
    reconcileKeyed(p.attachments, attachments, attachment => attachment.id, attachment => attachmentNode(attachment, context), attachment => JSON.stringify([attachment, context.locale]));
    reconcileKeyed(p.previews, previews, preview => preview.id, preview => linkPreviewNode(preview, message, context), preview => JSON.stringify([preview, context.locale]));
    p.attachments.hidden = !attachments.length; p.previews.hidden = !previews.length;
    const cardKey = JSON.stringify([message.card, message.deletedAt, context.locale]);
    if (node._cardKey !== cardKey) { node._cardKey = cardKey; p.cards.replaceChildren(); if (message.card && !message.deletedAt) p.cards.append(cardNode(message.card, message, context)); }
    p.cards.hidden = !p.cards.childElementCount;
    const reactionKey = JSON.stringify([message.reactions, message.deletedAt, context.locale]);
    if (node._reactionKey !== reactionKey) {
      node._reactionKey = reactionKey; p.reactions.replaceChildren();
      if (!message.deletedAt) for (const reaction of message.reactions || []) {
        const mine = (reaction.accountIds || []).includes(context.ownerAccountId);
        const chip = button('reaction-chip' + (mine ? ' is-mine' : ''), context.t(mine ? 'removeReaction' : 'addReaction') + ' ' + reaction.emoji, null,
          () => context.action('reaction', { threadId: message.threadId, messageId: message.id, emoji: reaction.emoji, active: !mine }));
        chip.setAttribute('aria-pressed', String(mine));
        chip.append(element('span', '', reaction.emoji), element('span', '', reaction.count === undefined ? (reaction.accountIds || []).length : reaction.count)); p.reactions.append(chip);
      }
    }
    p.reactions.hidden = !p.reactions.childElementCount;
    const readKey = JSON.stringify([own, message.readByAccountIds, message.deletedAt, context.locale]);
    if (node._readKey !== readKey) {
      node._readKey = readKey; p.status.replaceChildren();
      if (own && !message.deletedAt && (message.readByAccountIds || []).some(accountId => accountId !== context.ownerAccountId)) p.status.append(icon('check'), document.createTextNode(context.t('read')));
    }
    p.status.hidden = !p.status.childNodes.length;
    const actionsKey = JSON.stringify([own, message.deletedAt, context.locale, context.capable('replies'), context.capable('reactions')]);
    if (node._actionsKey !== actionsKey) {
      node._actionsKey = actionsKey; p.actions.replaceChildren();
      if (!message.deletedAt) {
        if (context.capable('reactions')) p.actions.append(button('icon-button', context.t('react'), 'smile', event => context.react(node._message, event.currentTarget)));
        if (context.capable('replies')) p.actions.append(button('icon-button', context.t('reply'), 'reply', () => context.reply(node._message)));
        p.actions.append(button('icon-button', context.t('copy'), 'copy', () => context.copy(node._message.body)));
      }
      p.actions.append(button('icon-button', context.t('messageActions'), 'more', event => { const rect = event.currentTarget.getBoundingClientRect(); context.messageMenu(node._message, rect.right, rect.bottom); }));
    }
    return node;
  }

  function suspendMedia(root) {
    for (const media of root.querySelectorAll('audio,video')) { try { media.pause(); } catch (_) {} }
    for (const host of root.querySelectorAll('.preview-player')) if (host.querySelector('iframe') && host._suspendPlayer) host._suspendPlayer();
  }

  global.AtlasChatRender = Object.freeze({ element, icon, button, id, compareIds, safeUrl, mediaUrl, linkedMediaUrl, embedUrl, initials, formatBytes, time, fullDate, dayKey, dayLabel, presenceText, avatar, renderMarkdown, isVideoPreview, canGroup, reconcileKeyed, reconcileMessage, suspendMedia });
})(window);
