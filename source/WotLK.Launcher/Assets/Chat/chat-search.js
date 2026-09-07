(function (global) {
  'use strict';
  // Find operates on rendered message bodies. It never replaces a message or its
  // interactive children, and never indexes concealed spoilers or deleted text.
  const fold = value => String(value).normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase().replace(/ς/g, 'σ').replace(/œ/g, 'oe').replace(/æ/g, 'ae');
  const labels = {
    find: ['Rechercher dans la conversation', 'Find in conversation'],
    previous: ['Résultat précédent (Maj + Entrée)', 'Previous result (Shift + Enter)'],
    next: ['Résultat suivant (Entrée)', 'Next result (Enter)'],
    close: ['Fermer la recherche (Échap)', 'Close search (Escape)'],
    hint: ['Texte des messages · les spoilers masqués sont exclus', 'Message text · concealed spoilers are excluded'],
    loading: ['Recherche dans l’historique…', 'Searching conversation history…'],
    complete: ['Tout l’historique parcouru', 'Entire history searched'],
    partial: ['Historique partiel', 'Partial history'],
    scanned: ['messages parcourus', 'messages searched'],
    results: ['résultats', 'results'],
    none: ['Aucun résultat', 'No results'],
    continue: ['Continuer dans l’historique', 'Continue through history'],
    retry: ['Réessayer', 'Retry'],
    failed: ['Chargement interrompu', 'History loading interrupted'],
    waiting: ['Réponse en attente', 'Waiting for a response'],
    offline: ['Connexion indisponible', 'Connection unavailable']
  };
  function create(options) {
    const $ = id => document.getElementById('conversation-search' + (id ? '-' + id : ''));
    const bar = $(''), input = $('input'), count = $('count'), status = $('status');
    const previous = $('previous'), next = $('next'), resume = $('continue');
    const list = options.messageList;
    let opened = false, returnFocus = null, identity = '', query = '', matches = [], current = -1;
    let debounce = 0, pumpTimer = 0, flight = null, pages = 0, failure = false;
    const snapshot = options.snapshot;
    const key = () => { const value = snapshot(); return value.sessionId + ':' + value.ownerAccountId + ':' + value.selectedThreadId; };
    const t = name => labels[name][String(snapshot().locale).startsWith('en') ? 1 : 0];
    const firstId = () => (snapshot().messages || []).filter(message => message.threadId === snapshot().selectedThreadId && global.AtlasChatRender.id(message.id))
      .reduce((first, message) => first === null || global.AtlasChatRender.compareIds(message.id, first) < 0 ? message.id : first, null);

    function clearMarks() {
      const parents = new Set();
      for (const mark of list.querySelectorAll('mark.search-match')) {
        parents.add(mark.parentNode); mark.replaceWith(document.createTextNode(mark.textContent));
      }
      for (const parent of parents) parent.normalize();
      for (const node of list.querySelectorAll('.is-search-current')) node.classList.remove('is-search-current');
    }
    function textIndex(root) {
      let text = '';
      const positions = [];
      const separate = () => { text += '\n'; positions.push(null); };
      const visit = node => {
        if (node.nodeType === Node.TEXT_NODE) {
          let offset = 0;
          for (const character of node.data) {
            const folded = fold(character);
            for (let i = 0; i < folded.length; i++) positions.push({ node, start: offset, end: offset + character.length });
            text += folded; offset += character.length;
          }
          return;
        }
        if (node.nodeType !== Node.ELEMENT_NODE) return;
        if (node.matches('.message-edited, [hidden], [aria-hidden="true"], .spoiler[aria-expanded="false"]')) { separate(); return; }
        if (node.tagName === 'BR') { separate(); return; }
        const block = /^(P|DIV|LI|PRE|BLOCKQUOTE|H[1-6])$/.test(node.tagName);
        if (block) separate();
        for (const child of node.childNodes) visit(child);
        if (block) separate();
      };
      visit(root); return { text, positions };
    }
    function refresh(jump = false) {
      if (!opened) return;
      const old = matches[current]?.key;
      clearMarks(); matches = [];
      const edits = new Map();
      if (query) for (const article of list.children) {
        if (!article.dataset.messageId || article._message?.deletedAt || article._message?.threadId !== snapshot().selectedThreadId) continue;
        const body = article.querySelector('.message-content');
        if (!body) continue;
        const indexed = textIndex(body);
        for (let start = indexed.text.indexOf(query); start >= 0; start = indexed.text.indexOf(query, start + query.length)) {
          const end = start + query.length, parts = [];
          const match = { key: article.dataset.messageId + ':' + start, article, marks: [] };
          for (let at = start; at < end; at++) {
            const position = indexed.positions[at];
            if (!position) continue;
            const last = parts.at(-1);
            if (last && last.node === position.node && position.start <= last.end) last.end = Math.max(last.end, position.end);
            else parts.push({ ...position });
          }
          if (!parts.length) continue;
          matches.push(match);
          for (const part of parts) { if (!edits.has(part.node)) edits.set(part.node, []); edits.get(part.node).push({ ...part, match }); }
        }
      }
      // Replace only text nodes, from right to left; links/spoiler buttons and
      // media retain their original nodes, state and event listeners.
      for (const [node, ranges] of edits) for (const range of ranges.sort((a, b) => b.start - a.start)) {
        node.splitText(range.end); const selected = node.splitText(range.start);
        const mark = document.createElement('mark'); mark.className = 'search-match';
        selected.replaceWith(mark); mark.append(selected); range.match.marks.unshift(mark);
      }
      current = Math.max(0, matches.findIndex(match => match.key === old));
      if (!matches.length) current = -1;
      select(jump); renderStatus();
    }
    function select(jump) {
      for (let index = 0; index < matches.length; index++) {
        const match = matches[index];
        for (const mark of match.marks) mark.classList.toggle('is-current', index === current);
        match.article.classList.toggle('is-search-current', match.article === matches[current]?.article);
      }
      if (jump && matches[current]) options.jump(matches[current].marks[0] || matches[current].article);
      count.textContent = !query ? '' : matches.length ? (current + 1) + ' / ' + matches.length : t('none');
      count.setAttribute('aria-label', !query ? '' : matches.length ? (current + 1) + ' / ' + matches.length + ' ' + t('results') : t('none'));
      previous.disabled = next.disabled = !matches.length;
    }
    function renderStatus() {
      if (!opened) return;
      input.placeholder = t('find'); input.setAttribute('aria-label', t('find')); bar.setAttribute('aria-label', t('find'));
      for (const name of ['previous', 'next', 'close']) { $(name).title = t(name); $(name).setAttribute('aria-label', t(name)); }
      bar.title = t('hint');
      const value = snapshot(), loaded = (value.messages || []).filter(message => !message.deletedAt && message.threadId === value.selectedThreadId).length;
      const full = !value.isLoading && !value.hasEarlier && !flight;
      const running = !!flight || value.isLoadingEarlier || value.isLoading;
      const partial = !query ? t('hint') : full ? t('complete') : failure ? t('failed') : !value.isAvailable ? t('offline') : flight?.timedOut ? t('waiting') : running || pages < 20 ? t('loading') : t('partial');
      status.textContent = partial + (query ? ' · ' + loaded + ' ' + t('scanned') + (full ? '' : ' · ' + t('partial')) : '');
      const canRetry = !flight && !running && !!query && !full;
      resume.hidden = !canRetry || !failure && pages < 20;
      resume.disabled = !value.isAvailable;
      resume.textContent = t(failure ? 'retry' : 'continue');
      bar.dataset.coverage = full ? 'complete' : 'partial';
      bar.setAttribute('aria-busy', String(!!query && running));
    }
    function schedulePump() {
      clearTimeout(pumpTimer);
      if (opened && query) pumpTimer = setTimeout(pump, 80);
    }
    function finishFlight(operation, error) {
      if (flight !== operation) return;
      clearTimeout(operation.timer); flight = null;
      options.historyChanged?.();
      if (operation.identity !== key()) return;
      failure = !!error;
      if (!error) pages++;
      renderStatus(); if (!error) schedulePump();
    }
    function observeFlight() {
      const operation = flight;
      if (!operation || operation.identity !== key()) return;
      const value = snapshot(), first = firstId();
      if (value.isLoadingEarlier) { operation.observedLoading = true; return; }
      const advanced = first && global.AtlasChatRender.compareIds(first, operation.beforeId) < 0;
      if (advanced || !value.hasEarlier) finishFlight(operation, false);
      else if (operation.observedLoading && value.errorCode) finishFlight(operation, true);
      else if (operation.resultReceived && operation.observedLoading) finishFlight(operation, true);
    }
    function pump() {
      if (!opened || !query || identity !== key() || document.hidden) return;
      observeFlight();
      const value = snapshot();
      if (flight || failure || pages >= 20 || !value.hasEarlier || value.isLoading || value.isLoadingEarlier || !value.isAvailable || !value.isActive) { renderStatus(); return; }
      const beforeId = firstId();
      if (!beforeId) { failure = true; renderStatus(); return; }
      const operation = { identity, beforeId, observedLoading: false, resultReceived: false, timedOut: false, timer: 0 };
      flight = operation;
      operation.timer = setTimeout(() => {
        if (flight !== operation) return;
        operation.timedOut = true;
        // A missing bridge result is not evidence that its request ended. Keep
        // the one-flight lock until a result or a completed snapshot arrives.
        if (operation.resultReceived && !snapshot().isLoadingEarlier) finishFlight(operation, true);
        else renderStatus();
      }, 15000);
      options.action('loadEarlier', { threadId: value.selectedThreadId, beforeId }, { result: (_, error) => {
        if (flight !== operation || operation.identity !== key()) return;
        operation.resultReceived = true;
        if (error) finishFlight(operation, true);
        else { observeFlight(); if (flight === operation && operation.timedOut && !snapshot().isLoadingEarlier) finishFlight(operation, true); }
      } });
      renderStatus();
    }
    function open() {
      if (!snapshot().sessionId || !snapshot().ownerAccountId || !options.canOpen()) return false;
      if (!opened) {
        opened = true; identity = key(); returnFocus = document.activeElement;
        pages = 0; failure = false; bar.hidden = false; options.open?.(); refresh(); schedulePump();
      }
      input.focus({ preventScroll: true }); input.select(); return true;
    }
    function close(restore = true) {
      if (!opened) return;
      opened = false; clearTimeout(debounce); clearTimeout(pumpTimer); clearMarks();
      matches = []; current = -1; query = ''; input.value = ''; bar.hidden = true;
      options.close?.();
      if (restore && returnFocus?.isConnected && !returnFocus.closest('[hidden], [inert]')) returnFocus.focus({ preventScroll: true });
      returnFocus = null;
      // Any dispatched page may still finish, but no next page is scheduled.
    }
    function update() {
      if (identity && identity !== key()) {
        close(false); if (flight) clearTimeout(flight.timer); flight = null; identity = ''; failure = false;
      }
      observeFlight();
      if (!opened) return;
      if (!options.canOpen()) { close(false); return; }
      refresh(); schedulePump();
    }
    function navigate(delta) {
      if (debounce) { clearTimeout(debounce); debounce = 0; query = fold(input.value.trim()); refresh(true); schedulePump(); return; }
      if (!matches.length) return;
      current = (current + delta + matches.length) % matches.length; select(true);
    }
    input.addEventListener('input', () => {
      clearTimeout(debounce); clearTimeout(pumpTimer); pages = 0;
      // Clear the previous query immediately; a rapid clear or IME composition
      // must not leave an old query loading additional pages.
      query = ''; refresh();
      debounce = setTimeout(() => { debounce = 0; query = fold(input.value.trim()); refresh(true); schedulePump(); }, 180);
    });
    previous.addEventListener('click', () => navigate(-1)); next.addEventListener('click', () => navigate(1));
    $('close').addEventListener('click', () => close());
    resume.addEventListener('click', () => { pages = 0; failure = false; pump(); });
    input.addEventListener('keydown', event => {
      if (event.key === 'Enter' && !event.isComposing && event.keyCode !== 229) { event.preventDefault(); event.stopPropagation(); navigate(event.shiftKey ? -1 : 1); }
    });
    list.addEventListener('click', event => { if (opened && event.target.closest('.spoiler')) refresh(); });
    document.addEventListener('visibilitychange', () => { if (document.hidden) clearTimeout(pumpTimer); else schedulePump(); });
    return Object.freeze({ open, close, update, refresh, isOpen: () => opened, isLoadingHistory: () => !!flight && flight.identity === key() });
  }
  global.AtlasChatSearch = Object.freeze({ create });
})(window);
