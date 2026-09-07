(function (global) {
  'use strict';

  const controllers = new WeakMap(), shells = new WeakMap(), liveControllers = new Set();
  let nextVolumeId = 0;
  const motionPreference = global.matchMedia('(prefers-reduced-motion: reduce)');
  const SVG_NS = 'http://www.w3.org/2000/svg';
  const words = {
    mediaPlay: ['Lire', 'Play'], mediaPause: ['Mettre en pause', 'Pause'],
    mediaSeek: ['Position de lecture', 'Playback position'], mediaVolume: ['Volume', 'Volume'],
    mediaMute: ['Couper le son', 'Mute'], mediaUnmute: ['Rétablir le son', 'Unmute'],
    mediaExpand: ['Agrandir le lecteur', 'Expand player'], mediaBack: ['Revenir à la conversation', 'Return to conversation'],
    mediaFullscreen: ['Plein écran', 'Fullscreen'], mediaExitFullscreen: ['Quitter le plein écran', 'Exit fullscreen'],
    mediaLoading: ['Chargement…', 'Loading…'], mediaUnavailable: ['Lecture intégrée indisponible.', 'In-app playback unavailable.'],
    mediaPlayFailed: ['La lecture n’a pas démarré. Réessayez.', 'Playback could not start. Try again.'],
    mediaSave: ['Enregistrer sous…', 'Save as…'], audio: ['Audio', 'Audio'], video: ['Vidéo', 'Video']
  };
  const paths = {
    play: ['M8 5l11 7-11 7V5Z'], pause: ['M8 5v14', 'M16 5v14'],
    volume: ['M11 5 6 9H3v6h3l5 4V5Z', 'M15 8a6 6 0 0 1 0 8', 'M18 5a10 10 0 0 1 0 14'],
    mute: ['M11 5 6 9H3v6h3l5 4V5Z', 'm16 9 6 6', 'm22 9-6 6'],
    expand: ['M14 4h6v6', 'm20 4-7 7', 'M10 20H4v-6', 'm4 20 7-7'],
    back: ['M9 5 3 11l6 6', 'M3 11h11a6 6 0 0 1 6 6v2'],
    fullscreen: ['M8 3H3v5', 'M16 3h5v5', 'M21 16v5h-5', 'M8 21H3v-5'],
    restore: ['M3 8h5V3', 'M16 3v5h5', 'M21 16h-5v5', 'M8 21v-5H3'],
    save: ['M12 3v12', 'm7 10 5 5 5-5', 'M4 16v5h16v-5'],
    alert: ['M12 3 2 21h20L12 3Z', 'M12 9v5', 'M12 17h.01']
  };

  function element(tag, className) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return node;
  }
  function icon(name) {
    const svg = document.createElementNS(SVG_NS, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('class', 'media-icon'); svg.setAttribute('aria-hidden', 'true');
    for (const d of paths[name] || paths.play) { const path = document.createElementNS(SVG_NS, 'path'); path.setAttribute('d', d); svg.append(path); }
    return svg;
  }
  function setIcon(node, name) {
    if (node._mediaIcon === name) return;
    node._mediaIcon = name; node.replaceChildren(icon(name));
  }
  function label(node, text) { node.setAttribute('aria-label', text); node.title = text; }
  function button(className) { const node = element('button', 'media-button ' + className); node.type = 'button'; return node; }
  function seconds(value) {
    if (!Number.isFinite(value) || value < 0) return '--:--';
    const total = Math.floor(value), hours = Math.floor(total / 3600), minutes = Math.floor(total / 60) % 60, remainder = total % 60;
    return (hours ? hours + ':' + String(minutes).padStart(2, '0') : String(minutes)) + ':' + String(remainder).padStart(2, '0');
  }
  function controllerFor(value) { return controllers.get(value) || controllers.get(shells.get(value)); }
  // Shared document listeners retain weak references only. Removing a message
  // cannot keep its media alive even when the caller only removes its DOM node.
  function eachController(callback) {
    for (const reference of liveControllers) {
      const state = reference.deref();
      if (!state || state.disposed) liveControllers.delete(reference); else callback(state);
    }
  }
  document.addEventListener('pointerdown', event => eachController(state => state.dismissVolume(event.target)));
  document.addEventListener('scroll', () => eachController(state => state.positionVolume()), true);
  global.addEventListener('resize', () => eachController(state => state.positionVolume()));
  document.addEventListener('fullscreenchange', () => eachController(state => state.sync()));
  document.addEventListener('visibilitychange', () => eachController(state => state.updateProgressLoop()));
  motionPreference.addEventListener('change', () => eachController(state => state.updateProgressLoop()));

  function create(player, initialOptions = {}) {
    if (!player || !['AUDIO', 'VIDEO'].includes(player.tagName)) throw new TypeError('AtlasChatMedia requires an audio or video element.');
    const existing = shells.get(player);
    if (existing) { update(existing, initialOptions); return existing; }

    const kind = player.tagName.toLowerCase();
    const shell = element('div', 'media-player is-' + kind), stage = element('div', 'media-stage');
    const controls = element('div', 'media-controls'), play = button('media-play');
    const timeline = element('div', 'media-timeline'), seek = element('input', 'media-range media-seek');
    const times = element('div', 'media-times'), elapsed = element('span', 'media-elapsed'), duration = element('span', 'media-duration');
    const utilities = element('div', 'media-utilities'), volumeGroup = element('div', 'media-volume-group');
    const volumeButton = button('media-volume-button'), volumePanel = element('div', 'media-volume-panel');
    const mute = button('media-mute'), volume = element('input', 'media-range media-volume');
    const expand = button('media-expand'), fullscreen = button('media-fullscreen'), save = button('media-save');
    const status = element('div', 'media-status');
    const state = { shell, player, kind, options: { mode: 'inline', locale: 'fr', ...initialOptions }, listeners: [],
      disposed: false, pendingPlay: false, waiting: false, playError: false, playToken: 0, scrubbing: false, volumeOpen: false, previousVolume: player.volume || 1,
      progressFrame: 0, range: null };

    shell.setAttribute('role', 'group'); shell.tabIndex = 0;
    player.controls = false; player.classList.add('media-element'); player.setAttribute('controlsList', 'nodownload');
    if (kind === 'video') { player.playsInline = true; player.disablePictureInPicture = true; }
    stage.append(player);
    seek.type = 'range'; seek.min = '0'; seek.max = '0'; seek.step = 'any'; seek.value = '0'; seek.disabled = true;
    seek.setAttribute('aria-valuetext', '0:00 / --:--');
    times.setAttribute('aria-hidden', 'true'); times.append(elapsed, duration); timeline.append(seek, times);
    volume.type = 'range'; volume.min = '0'; volume.max = '1'; volume.step = '.05'; volume.value = String(player.volume);
    volumePanel.hidden = true; volumePanel.setAttribute('role', 'group'); volumePanel.id = 'atlas-media-volume-' + (++nextVolumeId);
    if (typeof volumePanel.showPopover === 'function') volumePanel.setAttribute('popover', 'manual');
    volumeButton.setAttribute('aria-expanded', 'false'); volumeButton.setAttribute('aria-controls', volumePanel.id);
    volumePanel.append(mute, volume); volumeGroup.append(volumeButton, volumePanel);
    expand.hidden = true; save.hidden = true; fullscreen.hidden = kind !== 'video';
    utilities.append(volumeGroup, save, expand, fullscreen); controls.append(play, timeline, utilities);
    status.hidden = true; status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
    shell.append(stage, controls, status);

    const text = key => {
      const translated = typeof state.options.t === 'function' ? state.options.t(key) : null;
      return typeof translated === 'string' && translated && translated !== key ? translated
        : words[key]?.[String(state.options.locale).startsWith('en') ? 1 : 0] || key;
    };
    function listen(node, type, handler, options) { node.addEventListener(type, handler, options); state.listeners.push(() => node.removeEventListener(type, handler, options)); }
    function closeVolume(restoreFocus = false) {
      if (!state.volumeOpen) return;
      state.volumeOpen = false;
      if (volumePanel.hasAttribute('popover') && volumePanel.matches(':popover-open')) volumePanel.hidePopover();
      volumePanel.hidden = true; volumeButton.setAttribute('aria-expanded', 'false');
      if (restoreFocus && volumeButton.isConnected) volumeButton.focus({ preventScroll: true });
    }
    function positionVolume() {
      if (!state.volumeOpen || !volumePanel.hasAttribute('popover') || !volumePanel.matches(':popover-open')) return;
      const anchor = volumeButton.getBoundingClientRect(), bounds = volumePanel.getBoundingClientRect();
      const left = Math.max(8, Math.min(anchor.right - bounds.width, global.innerWidth - bounds.width - 8));
      const above = anchor.top - bounds.height - 8;
      const top = Math.max(8, Math.min(above >= 8 ? above : anchor.bottom + 8, global.innerHeight - bounds.height - 8));
      volumePanel.style.left = left + 'px'; volumePanel.style.top = top + 'px';
    }
    function bounds() {
      if (Number.isFinite(player.duration) && player.duration > 0) return { start: 0, end: player.duration };
      try {
        const ranges = player.seekable;
        if (ranges?.length) { const index = ranges.length - 1, start = ranges.start(index), end = ranges.end(index); if (Number.isFinite(start) && Number.isFinite(end) && end > start) return { start, end }; }
      } catch (_) {}
      return null;
    }
    function seekTo(value) {
      const range = bounds(); if (!range || !Number.isFinite(value) || player.error) return;
      try { player.currentTime = Math.max(range.start, Math.min(range.end, value)); } catch (_) {}
      sync();
    }
    function setVolume(value) {
      const amount = Math.max(0, Math.min(1, value)); if (!Number.isFinite(amount)) return;
      player.volume = amount; player.muted = amount === 0;
      if (amount > 0) state.previousVolume = amount;
      sync();
    }
    function toggleMute() {
      if (player.muted || player.volume === 0) { if (player.volume === 0) player.volume = state.previousVolume || 1; player.muted = false; }
      else player.muted = true;
      sync();
    }
    async function togglePlay() {
      if (state.disposed || player.error) return;
      if (state.pendingPlay || !player.paused && !player.ended) {
        ++state.playToken; state.pendingPlay = false; state.waiting = false; player.pause(); sync(); return;
      }
      state.playError = false; state.pendingPlay = true; const token = ++state.playToken; sync();
      try {
        if (player.ended) { const range = bounds(); if (range) player.currentTime = range.start; }
        await player.play();
        if (state.disposed || token !== state.playToken) return;
        state.pendingPlay = false; sync();
      } catch (error) {
        if (state.disposed || token !== state.playToken) return;
        state.pendingPlay = false; state.waiting = false; state.playError = error?.name !== 'AbortError'; sync();
      }
    }
    async function toggleFullscreen() {
      if (kind !== 'video' || state.disposed) return;
      closeVolume();
      try {
        if (document.fullscreenElement === shell) await document.exitFullscreen();
        else if (typeof shell.requestFullscreen === 'function') await shell.requestFullscreen();
      } catch (_) { /* An unavailable fullscreen transition leaves the inline player usable. */ }
      if (!state.disposed) sync();
    }
    function syncProgress(refreshRange = false) {
      if (state.disposed) return;
      if (refreshRange) state.range = bounds();
      const range = state.range, time = Number.isFinite(player.currentTime) ? Math.max(0, player.currentTime) : 0;
      const start = String(range?.start || 0), end = String(range?.end || 0);
      if (seek.min !== start) seek.min = start;
      if (seek.max !== end) seek.max = end;
      const disabled = !range || !!player.error;
      if (seek.disabled !== disabled) seek.disabled = disabled;
      if (!state.scrubbing) {
        const value = String(range ? Math.max(range.start, Math.min(range.end, time)) : 0);
        if (seek.value !== value) seek.value = value;
      }
      const shownTime = state.scrubbing ? Number(seek.value) : time;
      const percent = range ? Math.max(0, Math.min(100, (shownTime - range.start) * 100 / (range.end - range.start))) : 0;
      const fill = percent.toFixed(3) + '%';
      if (seek.style.getPropertyValue('--media-fill') !== fill) seek.style.setProperty('--media-fill', fill);
      const elapsedText = seconds(shownTime), durationText = seconds(player.duration);
      if (elapsed.textContent !== elapsedText) elapsed.textContent = elapsedText;
      if (duration.textContent !== durationText) duration.textContent = durationText;
      const accessibleTime = elapsedText + ' / ' + durationText;
      if (seek.getAttribute('aria-valuetext') !== accessibleTime) seek.setAttribute('aria-valuetext', accessibleTime);
    }
    function animateProgress() {
      return !state.disposed && !player.paused && !player.ended && !player.error && !document.hidden && shell.isConnected && !motionPreference.matches;
    }
    function stopProgress() { if (state.progressFrame) global.cancelAnimationFrame(state.progressFrame); state.progressFrame = 0; }
    function updateProgressLoop() {
      if (!animateProgress()) { stopProgress(); return; }
      if (state.progressFrame) return;
      const frame = () => {
        state.progressFrame = 0;
        if (!animateProgress()) return;
        syncProgress(); state.progressFrame = global.requestAnimationFrame(frame);
      };
      state.progressFrame = global.requestAnimationFrame(frame);
    }
    function sync() {
      if (state.disposed) return;
      const playing = !player.paused && !player.ended, muted = player.muted || player.volume === 0;
      const full = document.fullscreenElement === shell;
      const loading = !player.error && (state.pendingPlay && player.readyState < 3 || state.waiting && playing);
      const error = !!player.error || state.playError;
      shell.classList.toggle('is-playing', playing); shell.classList.toggle('is-loading', loading); shell.classList.toggle('is-error', error);
      shell.classList.toggle('is-fullscreen', full); shell.setAttribute('aria-busy', String(loading));
      setIcon(play, playing || state.pendingPlay ? 'pause' : 'play'); label(play, text(playing || state.pendingPlay ? 'mediaPause' : 'mediaPlay')); play.disabled = !!player.error;
      label(seek, text('mediaSeek')); syncProgress(true);
      setIcon(volumeButton, muted ? 'mute' : 'volume'); label(volumeButton, text('mediaVolume'));
      label(volumePanel, text('mediaVolume')); label(volume, text('mediaVolume'));
      volume.value = String(muted ? 0 : player.volume); volume.style.setProperty('--media-fill', (muted ? 0 : player.volume * 100) + '%');
      volume.setAttribute('aria-valuetext', Math.round((muted ? 0 : player.volume) * 100) + '%');
      setIcon(mute, muted ? 'mute' : 'volume'); label(mute, text(muted ? 'mediaUnmute' : 'mediaMute')); mute.setAttribute('aria-pressed', String(muted));
      expand.hidden = typeof state.options.onExpand !== 'function';
      setIcon(expand, state.options.mode === 'viewer' ? 'back' : 'expand'); label(expand, state.options.expandLabel || text(state.options.mode === 'viewer' ? 'mediaBack' : 'mediaExpand'));
      save.hidden = kind === 'video' || typeof state.options.onSave !== 'function'; setIcon(save, 'save'); label(save, text('mediaSave'));
      fullscreen.hidden = kind !== 'video' || !document.fullscreenEnabled;
      setIcon(fullscreen, full ? 'restore' : 'fullscreen'); label(fullscreen, text(full ? 'mediaExitFullscreen' : 'mediaFullscreen'));
      const message = player.error ? text('mediaUnavailable') : state.playError ? text('mediaPlayFailed') : loading ? text('mediaLoading') : '';
      if (status.textContent !== message) status.textContent = message;
      status.hidden = !message;
      const name = state.options.fileName || text(kind); shell.setAttribute('aria-label', name); player.setAttribute('aria-label', name);
      shell.dataset.mode = ['inline', 'draft', 'viewer', 'dock'].includes(state.options.mode) ? state.options.mode : 'inline';
      if (kind === 'video' && player.videoWidth > 0 && player.videoHeight > 0) shell.style.setProperty('--media-aspect', player.videoWidth + ' / ' + player.videoHeight);
      updateProgressLoop();
    }
    const activate = (node, handler) => listen(node, 'click', event => { event.stopPropagation(); handler(event); });
    activate(play, togglePlay);
    activate(volumeButton, () => {
      if (state.volumeOpen) { closeVolume(); return; }
      state.volumeOpen = true; volumePanel.hidden = false; volumeButton.setAttribute('aria-expanded', 'true');
      if (volumePanel.hasAttribute('popover')) { volumePanel.showPopover(); positionVolume(); }
      volume.focus({ preventScroll: true });
    });
    activate(mute, toggleMute);
    activate(expand, () => { closeVolume(); state.options.onExpand?.(player, expand, shell); });
    activate(save, () => { closeVolume(); if (kind !== 'video') state.options.onSave?.(player, save, shell); });
    activate(fullscreen, toggleFullscreen);
    if (kind === 'video') activate(stage, togglePlay);
    listen(seek, 'pointerdown', () => { state.scrubbing = true; });
    listen(seek, 'input', () => seekTo(Number(seek.value)));
    listen(seek, 'change', () => { seekTo(Number(seek.value)); state.scrubbing = false; sync(); });
    listen(seek, 'pointerup', () => { state.scrubbing = false; sync(); });
    listen(seek, 'pointercancel', () => { state.scrubbing = false; sync(); });
    listen(seek, 'blur', () => { state.scrubbing = false; sync(); });
    listen(seek, 'keydown', event => {
      const range = bounds(); if (!range) return;
      const value = Number(seek.value), amounts = { ArrowLeft: -5, ArrowDown: -5, ArrowRight: 5, ArrowUp: 5, PageDown: -(range.end - range.start) / 10, PageUp: (range.end - range.start) / 10 };
      if (event.key === 'Home' || event.key === 'End' || Object.hasOwn(amounts, event.key)) {
        event.preventDefault(); event.stopPropagation(); seekTo(event.key === 'Home' ? range.start : event.key === 'End' ? range.end : value + amounts[event.key]);
      }
    });
    listen(volume, 'input', () => setVolume(Number(volume.value)));
    listen(volumeGroup, 'focusout', event => { if (!volumeGroup.contains(event.relatedTarget)) closeVolume(); });
    listen(shell, 'keydown', event => {
      if (event.key === 'Escape' && state.volumeOpen) { event.preventDefault(); event.stopPropagation(); closeVolume(true); return; }
      if (event.target.closest('input,button,a') || event.altKey || event.ctrlKey || event.metaKey) return;
      if (event.key === ' ' || event.key.toLowerCase() === 'k') { event.preventDefault(); event.stopPropagation(); togglePlay(); }
      else if (event.key.toLowerCase() === 'm') { event.preventDefault(); event.stopPropagation(); toggleMute(); }
      else if (event.key.toLowerCase() === 'f' && kind === 'video') { event.preventDefault(); event.stopPropagation(); toggleFullscreen(); }
      else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); event.stopPropagation(); seekTo(player.currentTime + (event.key === 'ArrowLeft' ? -5 : 5)); }
    });
    for (const name of ['loadedmetadata', 'durationchange', 'volumechange', 'canplay']) listen(player, name, sync);
    for (const name of ['timeupdate', 'seeked', 'progress']) listen(player, name, () => syncProgress(true));
    listen(player, 'play', () => { sync(); state.options.onPlay?.(player, shell); });
    listen(player, 'playing', () => { state.waiting = false; state.pendingPlay = false; state.playError = false; sync(); });
    listen(player, 'waiting', () => { state.waiting = true; sync(); });
    listen(player, 'pause', () => { state.waiting = false; state.pendingPlay = false; sync(); });
    listen(player, 'ended', () => { state.waiting = false; state.pendingPlay = false; sync(); });
    listen(player, 'emptied', () => { state.waiting = false; state.playError = false; sync(); });
    listen(player, 'error', () => { state.waiting = false; state.pendingPlay = false; sync(); });
    state.sync = sync; state.closeVolume = closeVolume; state.positionVolume = positionVolume; state.updateProgressLoop = updateProgressLoop; state.stopProgress = stopProgress;
    state.dismissVolume = target => { if (state.volumeOpen && !volumeGroup.contains(target)) closeVolume(); };
    state.weakReference = new WeakRef(state); liveControllers.add(state.weakReference);
    controllers.set(shell, state); shells.set(player, shell); sync(); return shell;
  }

  function update(value, options = {}) {
    const state = controllerFor(value); if (!state || state.disposed) return null;
    if (options.mode !== undefined && options.mode !== state.options.mode) state.closeVolume();
    state.options = { ...state.options, ...options }; state.sync(); return state.shell;
  }
  function pause(value) {
    const state = controllerFor(value); if (!state || state.disposed) return;
    ++state.playToken; state.pendingPlay = false; state.waiting = false; state.player.pause(); state.closeVolume(); state.sync();
  }
  function dispose(value, options = {}) {
    const state = controllerFor(value); if (!state || state.disposed) return;
    if (options.pause !== false) pause(state.shell);
    state.closeVolume(); state.disposed = true; ++state.playToken;
    state.stopProgress();
    for (const remove of state.listeners) remove(); state.listeners.length = 0;
    liveControllers.delete(state.weakReference);
    controllers.delete(state.shell); shells.delete(state.player);
  }
  function shellFor(player) { return shells.get(player) || null; }
  global.AtlasChatMedia = Object.freeze({ create, update, pause, dispose, shellFor });
})(window);
