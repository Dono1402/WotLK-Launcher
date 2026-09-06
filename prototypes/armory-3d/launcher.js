import {className,classColor} from './character-labels.mjs';

const $ = id => document.getElementById(id);
const text = {
  fr:{characters:'Mes personnages',retry:'Réessayer',search:'Rechercher un personnage',empty:'Aucun personnage sur ce compte',
    loading:'Chargement des personnages…',refreshing:'Actualisation…',unavailable:'Personnages indisponibles',cached:'Dernières données enregistrées',select:'Sélectionne un personnage',pending:'Données du personnage en cours de récupération',noMatch:'Aucun résultat',level:'Niveau',armory:'Armurerie',profile:'Profil',
    statusLabel:'Statut',bioLabel:'Bio',statusPlaceholder:'Ton statut du moment',bioPlaceholder:'Quelques mots pour te présenter',profilePicture:'Photo de profil',
    changeAvatar:'Changer la photo',
    save:'Enregistrer',cancel:'Annuler',saving:'Enregistrement…',saved:'✓ Enregistré',dismissNotice:'Fermer la notification',apply:'Appliquer',applying:'Application…',back:'Retour',
    editingUnavailable:'La modification du profil est actuellement indisponible.',saveRejected:'Le profil n’a pas pu être enregistré. Réessaie.',bridgeUnavailable:'La personnalisation est momentanément indisponible.',
    profileTooLong:'Le statut est limité à 80 caractères et la bio à 280 caractères.',
    editText:'Modifier le statut et la bio',bannerMenu:'Modifier la bannière',cropBanner:'Recadrer la bannière',cropArea:'Zone de recadrage',chooseBanner:'Changer l’image',resetBanner:'Restaurer la bannière par défaut',
    resetBannerQuestion:'Rétablir la bannière par défaut ?',confirmResetBanner:'Oui, rétablir',dragBanner:'Glisse l’image ou utilise les flèches pour la déplacer. Utilise la molette pour zoomer ou dézoomer. La touche Début ou un double-clic recentre l’image et rétablit le zoom.',zoom:'Zoom',zoomOut:'Réduire le zoom',zoomIn:'Augmenter le zoom',loadingImage:'Chargement de l’image…',
    bannerFailed:'La bannière n’a pas pu être enregistrée. Réessaie.'},
  en:{characters:'My characters',retry:'Retry',search:'Search characters',empty:'No characters on this account',
    loading:'Loading characters…',refreshing:'Refreshing…',unavailable:'Characters unavailable',cached:'Last saved data',select:'Select a character',pending:'Retrieving character data',noMatch:'No results',level:'Level',armory:'Armory',profile:'Profile',
    statusLabel:'Status',bioLabel:'Bio',statusPlaceholder:'Your current status',bioPlaceholder:'A few words about yourself',profilePicture:'Profile picture',
    changeAvatar:'Change picture',
    save:'Save',cancel:'Cancel',saving:'Saving…',saved:'✓ Saved',dismissNotice:'Dismiss notification',apply:'Apply',applying:'Applying…',back:'Back',
    editingUnavailable:'Profile editing is currently unavailable.',saveRejected:'Your profile could not be saved. Please try again.',bridgeUnavailable:'Customization is temporarily unavailable.',
    profileTooLong:'Your status is limited to 80 characters and your bio to 280 characters.',
    editText:'Edit status and bio',bannerMenu:'Edit banner',cropBanner:'Crop banner',cropArea:'Crop area',chooseBanner:'Change image',resetBanner:'Restore the default banner',
    resetBannerQuestion:'Restore the default banner?',confirmResetBanner:'Yes, restore',dragBanner:'Drag the image or use the arrow keys to move it. Use the mouse wheel to zoom in or out. Press Home or double-click to center the image and reset the zoom.',zoom:'Zoom',zoomOut:'Zoom out',zoomIn:'Zoom in',loadingImage:'Loading image…',
    bannerFailed:'Your banner could not be saved. Please try again.'}
};
let locale = new URLSearchParams(location.search).get('lang')==='en' ? 'en' : 'fr';
let roster = [], selected, status = 'loading', timer, pending, syncing = false, profile = {};
let editing = false, draft, profileSave, profileFeedback, profileNotice, avatarPending, avatarFeedback, avatarRestoreFocus = false;
let bannerEditing = false, bannerDraft, bannerPicking = false, bannerRequest, bannerFeedback, bannerRestoreFocus = false, bannerDrag;
let profileTrigger, notificationSource, notificationTimer;
const announcedFeedback = new WeakSet();
const label = key => text[locale][key];
const icons = () => window.lucide.createIcons();
const profileValues = value => ({statusMessage:typeof value.statusMessage==='string'?value.statusMessage:'',bio:typeof value.bio==='string'?value.bio:''});
const normalized = value => ({statusMessage:value.statusMessage.trim(),bio:value.bio.trim()});
const sameValues = (left,right) => left.statusMessage===right.statusMessage && left.bio===right.bio;
const profileIsBusy = () => Boolean(profileSave || profile.profileBusy);
const avatarIsBusy = () => Boolean(avatarPending || profile.avatarBusy);
const bannerIsBusy = () => Boolean(bannerPicking || bannerRequest || profile.bannerBusy);
const editorIsBusy = () => profileIsBusy() || avatarIsBusy() || bannerIsBusy();
const hasAvatar = () => typeof profile.avatar==='string' && profile.avatar.startsWith('data:image/png;base64,') && profile.avatar.length<2000000;
const bannerImage = value => typeof value==='string' && value.startsWith('data:image/png;base64,') && value.length<16000000 ? value : null;
const clamp = value => Math.max(0,Math.min(1,value));
const zoomValue = value => Number.isFinite(value)?Math.max(1,Math.min(3,value)):1;
const fitValue = value => value==='cover'?'cover':'contain';
const savedBanner = () => ({image:bannerImage(profile.banner),positionX:Number.isFinite(profile.bannerPositionX)?clamp(profile.bannerPositionX):.5,positionY:Number.isFinite(profile.bannerPositionY)?clamp(profile.bannerPositionY):.3,zoom:zoomValue(profile.bannerZoom),fit:fitValue(profile.bannerFit)});

function postProfileAction(message) {
  try {
    if (!window.chrome?.webview?.postMessage) return false;
    window.chrome.webview.postMessage(message); return true;
  } catch { return false; }
}
function dismissNotification() {
  clearTimeout(notificationTimer); notificationSource = undefined; renderNotification();
}
function renderNotification() {
  const sources = [profileFeedback,avatarFeedback,bannerFeedback,profileNotice].filter(Boolean);
  if (notificationSource && !sources.includes(notificationSource)) {
    clearTimeout(notificationTimer); notificationSource = undefined;
  }
  for (const source of sources) if (!announcedFeedback.has(source)) {
    announcedFeedback.add(source); clearTimeout(notificationTimer); notificationSource = source;
    if (source.kind!=='error') notificationTimer = setTimeout(() => {
      if (notificationSource===source) { notificationSource = undefined; renderNotification(); }
    },4000);
  }
  const container = $('profile-notice');
  const destination = $('banner-editor').open ? $('banner-notification-slot') : document.querySelector('.hero-feedbacks');
  if (container.parentElement!==destination) destination.append(container);
  container.hidden = !notificationSource;
  const isError = notificationSource?.kind==='error';
  container.dataset.kind = isError?'error':'success';
  $('notification-text').textContent = notificationSource ? isError ? notificationSource.message || label(notificationSource.key) : label('saved') : '';
  $('dismiss-notification').ariaLabel = $('dismiss-notification').title = label('dismissNotice');
}
function closeBannerConfirmation(restoreFocus=false) {
  const wasOpen = !$('reset-banner-confirm').hidden;
  $('reset-banner-confirm').hidden = true; $('reset-banner').setAttribute('aria-expanded','false');
  if (restoreFocus && wasOpen) $('reset-banner').focus({preventScroll:true});
}
function closeBannerMenu(restoreFocus=false) {
  const wasOpen = !$('banner-menu').hidden;
  $('banner-menu').hidden = true; $('edit-banner').setAttribute('aria-expanded','false');
  closeBannerConfirmation();
  if (restoreFocus && wasOpen) $('edit-banner').focus({preventScroll:true});
}
function toggleBannerMenu() {
  if (editorIsBusy() || profile.canModifyBanner!==true) return;
  if (!$('banner-menu').hidden) { closeBannerMenu(true); return; }
  $('banner-menu').hidden = false; $('edit-banner').setAttribute('aria-expanded','true');
  $('reposition-banner').focus({preventScroll:true});
}
function coverGeometry(image,width,height,zoom=1) {
  const scale = image.naturalWidth && image.naturalHeight ? Math.max(width/image.naturalWidth,height/image.naturalHeight)*zoomValue(zoom) : 1;
  const renderedWidth = image.naturalWidth ? image.naturalWidth*scale : width;
  const renderedHeight = image.naturalHeight ? image.naturalHeight*scale : height;
  return {width:renderedWidth,height:renderedHeight,x:Math.max(0,renderedWidth-width),y:Math.max(0,renderedHeight-height)};
}
function cropFrame() {
  const stage = $('banner-crop-stage'), hero = $('profile-hero');
  const ratio = hero.clientWidth/Math.max(1,hero.clientHeight);
  const width = Math.max(1,Math.min(stage.clientWidth-48,(stage.clientHeight-48)*ratio));
  const height = width/ratio;
  return {x:(stage.clientWidth-width)/2,y:(stage.clientHeight-height)/2,width,height};
}
function paintImage(image,frame,value) {
  const contain = value.fit!=='cover';
  const scale = contain && image.naturalWidth && image.naturalHeight ? Math.min(frame.width/image.naturalWidth,frame.height/image.naturalHeight) : null;
  const geometry = scale===null ? coverGeometry(image,frame.width,frame.height,value.zoom) : {width:image.naturalWidth*scale,height:image.naturalHeight*scale,x:0,y:0};
  image.style.width = geometry.width+'px'; image.style.height = geometry.height+'px';
  image.style.left = (frame.x+(contain?(frame.width-geometry.width)/2:-geometry.x*clamp(value.positionX)))+'px';
  image.style.top = (frame.y+(contain?(frame.height-geometry.height)/2:-geometry.y*clamp(value.positionY)))+'px';
  return geometry;
}
function renderBanner() {
  const value = bannerDraft || savedBanner(), source = value.image || '/banner.png';
  const image = document.querySelector('.banner-image'), hero = $('profile-hero'), dialog = $('banner-editor');
  hero.dataset.fit = value.fit;
  for (const backdrop of document.querySelectorAll('.banner-backdrop')) {
    if (backdrop.getAttribute('src')!==source) backdrop.src = source;
    backdrop.hidden = value.fit==='cover';
  }
  if (image.getAttribute('src')!==source) image.src = source;
  paintImage(image,{x:0,y:0,width:hero.clientWidth,height:hero.clientHeight},value);
  if (bannerEditing && !dialog.open) dialog.showModal();
  else if (!bannerEditing && dialog.open) dialog.close();
  document.body.classList.toggle('banner-modal-open',dialog.open);
  const preview = $('banner-crop-image');
  if (preview.getAttribute('src')!==source) preview.src = source;
  const ready = preview.complete && preview.naturalWidth>0;
  if (dialog.open) {
    const frame = cropFrame();
    for (const id of ['banner-crop-frame','banner-crop-backdrop']) Object.assign($(id).style,{left:frame.x+'px',top:frame.y+'px',width:frame.width+'px',height:frame.height+'px'});
    paintImage(preview,frame,value);
    $('banner-crop-frame').hidden = !ready; $('banner-image-loading').hidden = ready;
  }
  const busy = editorIsBusy(), permitted = profile.canModifyBanner===true;
  const canCrop = value.fit==='cover';
  dialog.setAttribute('aria-busy',String(bannerIsBusy()));
  $('banner-crop-stage').dataset.fit = value.fit;
  $('banner-crop-stage').title = $('banner-hint').textContent = label('dragBanner');
  $('banner-crop-stage').setAttribute('aria-busy',String(busy || !ready));
  for (const id of ['edit-banner','reposition-banner','choose-banner','reset-banner','confirm-reset-banner']) $(id).disabled = busy || !permitted;
  $('reset-banner').hidden = !profile.hasBannerCustomization && !bannerImage(profile.banner);
  $('cancel-reset-banner').disabled = busy;
  $('save-banner').disabled = busy || !permitted || !ready;
  $('banner-zoom-controls').hidden = !canCrop;
  $('banner-zoom').disabled = busy || !ready || !canCrop;
  $('banner-zoom-out').disabled = busy || !ready || !canCrop || value.zoom<=1;
  $('banner-zoom-in').disabled = busy || !ready || !canCrop || value.zoom>=3;
  $('cancel-banner').disabled = busy;
  $('save-banner').textContent = label(bannerIsBusy()?'applying':'apply');
  $('banner-zoom').value = String(zoomValue(value.zoom));
  const zoomText = Math.round(zoomValue(value.zoom)*100)+' %';
  $('banner-zoom').setAttribute('aria-valuetext',zoomText);
  if (bannerRestoreFocus && !busy && !$('edit-banner').disabled) {
    bannerRestoreFocus = false;
    (dialog.open?$('banner-crop-stage'):$('edit-banner')).focus({preventScroll:true});
  }
}
function renderEditor() {
  const busy = editorIsBusy();
  // Reflow to the resting hero while cropping without throwing away its text draft.
  $('profile-editor').hidden = !editing || bannerEditing;
  $('profile-details').hidden = editing && !bannerEditing;
  $('profile-editor').setAttribute('aria-busy',String(busy));
  document.body.classList.toggle('editing-profile',editing && !bannerEditing);
  $('edit-profile').setAttribute('aria-expanded',String(editing));
  $('edit-profile').disabled = busy;
  $('edit-status').placeholder = label('statusPlaceholder'); $('edit-bio').placeholder = label('bioPlaceholder');
  $('status-count').textContent = (draft?.statusMessage.length ?? 0)+' / 80';
  $('bio-count').textContent = (draft?.bio.length ?? 0)+' / 280';
  $('edit-status').disabled = $('edit-bio').disabled = busy || profile.canUpdateSocialProfile!==true;
  $('profile-permission').hidden = profile.canUpdateSocialProfile===true || busy;
  $('save-profile').disabled = busy || profile.canUpdateSocialProfile!==true || !draft || sameValues(normalized(draft),normalized(profileValues(profile)));
  $('save-profile').setAttribute('aria-busy',String(profileIsBusy()));
  $('save-profile').querySelector('span').textContent = label(profileIsBusy()?'saving':'save');
  $('cancel-profile').disabled = busy;
  $('change-avatar').disabled = busy || profile.canModifyAvatar!==true;
  $('change-avatar').setAttribute('aria-busy',String(avatarIsBusy()));
  $('change-avatar').ariaLabel = $('change-avatar').title = label('changeAvatar');
  if (avatarRestoreFocus && !busy) {
    avatarRestoreFocus = false;
    if (!$('change-avatar').disabled) $('change-avatar').focus({preventScroll:true});
  }
  renderBanner(); renderNotification();
}
function openProfileEditor(trigger=$('edit-profile')) {
  if (bannerEditing) return;
  closeBannerMenu();
  if (!editing) {
    editing = true; draft = profileValues(profile); profileTrigger = trigger;
    $('edit-status').value = draft.statusMessage; $('edit-bio').value = draft.bio;
    profileFeedback = undefined; profileNotice = undefined;
  }
  renderEditor();
  if (!$('edit-status').disabled) $('edit-status').focus({preventScroll:true});
  else $('cancel-profile').focus({preventScroll:true});
  $('profile-editor').scrollIntoView({block:'nearest'});
}
function closeProfileEditor(saved=false) {
  if (editorIsBusy()) return;
  editing = false; draft = undefined; profileFeedback = undefined;
  if (!saved) profileNotice = undefined;
  renderEditor(); window.scrollTo({top:0});
  (profileTrigger || $('edit-profile')).focus({preventScroll:true});
}
function saveProfile() {
  if (!editing || !draft || editorIsBusy() || profile.canUpdateSocialProfile!==true) return;
  if (draft.statusMessage.length>80 || draft.bio.length>280) {
    profileFeedback = {kind:'error',key:'profileTooLong'}; renderEditor();
    (draft.statusMessage.length>80?$('edit-status'):$('edit-bio')).focus(); return;
  }
  const values = normalized(draft);
  if (sameValues(values,normalized(profileValues(profile)))) return;
  profileSave = {...values,seenBusy:false,acknowledged:false,previousError:profile.profileError};
  profileFeedback = undefined; renderEditor();
  if (!postProfileAction({action:'save-profile',...values})) {
    profileSave = undefined; profileFeedback = {kind:'error',key:'bridgeUnavailable'}; renderEditor();
  }
}
function changeAvatar() {
  if (editorIsBusy() || profile.canModifyAvatar!==true) return;
  avatarPending = true; avatarFeedback = undefined; avatarRestoreFocus = true; renderEditor();
  if (!postProfileAction({action:'change-avatar'})) {
    avatarPending = false; avatarFeedback = {kind:'error',key:'bridgeUnavailable'}; renderEditor();
  }
}
function openBannerEditor() {
  if (editorIsBusy() || profile.canModifyBanner!==true) return;
  closeBannerMenu();
  // Keep legacy display choices until Apply; the crop editor now always fills its frame.
  bannerDraft = {...(bannerDraft || savedBanner()),fit:'cover'};
  bannerEditing = true; bannerFeedback = undefined; closeBannerConfirmation(); renderEditor();
  $('banner-crop-stage').focus({preventScroll:true});
}
function chooseBanner() {
  if (editorIsBusy() || profile.canModifyBanner!==true) return;
  closeBannerMenu();
  bannerPicking = true; bannerFeedback = undefined; closeBannerConfirmation(); renderEditor();
  if (!postProfileAction({action:'choose-banner'})) {
    bannerPicking = false; bannerFeedback = {kind:'error',key:'bridgeUnavailable'}; renderEditor();
  }
}
function setBannerZoom(value) {
  if (!bannerEditing || !bannerDraft || bannerDraft.fit!=='cover' || editorIsBusy() || !$('banner-crop-image').naturalWidth) return;
  const image = $('banner-crop-image'), frame = cropFrame();
  const before = coverGeometry(image,frame.width,frame.height,bannerDraft.zoom);
  const focalX = (frame.width/2+before.x*bannerDraft.positionX)/before.width;
  const focalY = (frame.height/2+before.y*bannerDraft.positionY)/before.height;
  bannerDraft.zoom = Math.round(zoomValue(value)*100)/100;
  const after = coverGeometry(image,frame.width,frame.height,bannerDraft.zoom);
  bannerDraft.positionX = after.x>.01?clamp((focalX*after.width-frame.width/2)/after.x):.5;
  bannerDraft.positionY = after.y>.01?clamp((focalY*after.height-frame.height/2)/after.y):.5;
  renderBanner();
}
function recenterBanner() {
  if (!bannerEditing || !bannerDraft || bannerDraft.fit!=='cover' || editorIsBusy()) return;
  Object.assign(bannerDraft,{zoom:1,positionX:.5,positionY:.5}); renderBanner();
}
function saveBanner() {
  if (!bannerEditing || !bannerDraft || editorIsBusy() || profile.canModifyBanner!==true) return;
  bannerRequest = {kind:'save',value:{...bannerDraft}}; bannerFeedback = undefined; renderEditor();
  if (!postProfileAction({action:'save-banner',positionX:bannerDraft.positionX,positionY:bannerDraft.positionY,zoom:bannerDraft.zoom,fit:bannerDraft.fit})) {
    bannerRequest = undefined; bannerFeedback = {kind:'error',key:'bridgeUnavailable'}; renderEditor();
  }
}
function cancelBanner() {
  if (editorIsBusy()) return;
  bannerDraft = undefined; bannerEditing = false; bannerFeedback = undefined; bannerDrag = undefined;
  $('banner-crop-stage').classList.remove('banner-dragging');
  postProfileAction({action:'cancel-banner'}); renderEditor();
  $('edit-banner').focus({preventScroll:true});
}
function resetBanner() {
  if (editorIsBusy() || profile.canModifyBanner!==true || $('reset-banner-confirm').hidden) return;
  bannerRequest = {kind:'reset',value:{image:null,positionX:.5,positionY:.3,zoom:1,fit:'contain'}}; bannerFeedback = undefined;
  closeBannerMenu(); renderEditor();
  if (!postProfileAction({action:'reset-banner',confirmed:true})) {
    bannerRequest = undefined; bannerFeedback = {kind:'error',key:'bridgeUnavailable'}; renderEditor();
  }
}
function receiveBannerResult(message) {
  if (!bannerRequest) return;
  const completed = message.completed===true || (message.completed!==false && (message.succeeded===true || message.accepted===false));
  if (!completed) return;
  if (message.accepted===true && message.succeeded===true) {
    const {kind,value} = bannerRequest;
    profile = {...profile,banner:value.image,bannerPositionX:value.positionX,bannerPositionY:value.positionY,bannerZoom:value.zoom,bannerFit:value.fit,hasBannerCustomization:kind!=='reset',bannerBusy:false,bannerError:null};
    bannerDraft = undefined; bannerEditing = false; bannerFeedback = {kind:'success'}; bannerRestoreFocus = true;
  } else bannerFeedback = {kind:'error',message:typeof message.error==='string'?message.error:'',key:'bannerFailed'};
  bannerRequest = undefined; renderEditor();
}
function receiveProfile(next) {
  const previous = profile; profile = next;
  if (profileSave) {
    if (next.profileBusy) profileSave.seenBusy = true;
    else {
      const failed = next.profileError && (profileSave.seenBusy || profileSave.acknowledged || next.profileError!==profileSave.previousError);
      const persisted = sameValues(normalized(profileValues(next)),profileSave);
      if (failed) { profileSave = undefined; profileFeedback = {kind:'error',message:next.profileError}; }
      else if (persisted && (next.profileNotice || profileSave.seenBusy)) {
        profileSave = undefined; profileNotice = {kind:'success'}; closeProfileEditor(true);
      } else if (profileSave.seenBusy) { profileSave = undefined; profileFeedback = {kind:'error',key:'saveRejected'}; }
    }
  }
  if (!next.avatarBusy) {
    const wasPending = Boolean(avatarPending || previous.avatarBusy); avatarPending = false;
    if (next.avatarError && (wasPending || next.avatarError!==previous.avatarError)) avatarFeedback = {kind:'error',message:next.avatarError};
    else if (next.avatarNotice && wasPending) avatarFeedback = {kind:'success'};
  }
  if (next.bannerError && next.bannerError!==previous.bannerError) bannerFeedback = {kind:'error',message:next.bannerError};
}

function applyProfile() {
  document.documentElement.lang = locale;
  document.title = `Atlas | ${label('profile')}`;
  document.querySelectorAll('[data-label]').forEach(el => el.textContent=label(el.dataset.label));
  $('characters').ariaLabel = label('characters');
  $('character-panel').ariaLabel = label('armory');
  $('search').placeholder = $('search').ariaLabel = label('search');
  $('profile-name').textContent = profile.username || '';
  $('profile-initial').textContent = (profile.username || '').slice(0,1).toUpperCase();
  for (const [id,key] of [['profile-status','statusMessage'],['profile-bio','bio']]) {
    $(id).textContent = profile[key] || ''; $(id).hidden = !profile[key];
  }
  const avatar = hasAvatar() ? profile.avatar : null;
  $('profile-avatar').hidden = !avatar;
  if (avatar) $('profile-avatar').src = avatar;
  else $('profile-avatar').removeAttribute('src');
  for (const [id,key] of [['edit-profile','editText'],['profile-editor','editText'],['edit-banner','bannerMenu'],['banner-menu','bannerMenu'],['banner-crop-stage','cropArea'],['cancel-banner','back'],['banner-zoom-out','zoomOut'],['banner-zoom-in','zoomIn']]) {
    $(id).ariaLabel = label(key);
    if ($(id).tagName==='BUTTON') $(id).title = label(key);
  }
  $('banner-crop-stage').title = label('dragBanner');
  renderEditor(); render();
}

function select(id,force=false) {
  const character = roster.find(row => row.id===id);
  if (!character) return;
  const changed = selected!==id;
  selected = id;
  if (character.available) {
    const src = `/characters/${encodeURIComponent(id)}/view?lang=${locale}`;
    if (force || changed || $('character-view').getAttribute('src')!==src) $('character-view').src=src;
    $('character-view').hidden = false; $('empty-state').hidden = true;
    $('character-view').title = character.name;
  } else {
    $('character-view').hidden = true; $('character-view').removeAttribute('src');
    $('empty-state').hidden = false; $('empty-state').textContent=label('pending');
  }
  render();
}

function render() {
  $('character-count').textContent = String(roster.length);
  document.querySelector('.search').hidden = roster.length<=1;
  if (roster.length<=1) $('search').value = '';
  const needle = $('search').value.trim().toLocaleLowerCase(locale);
  const visible = roster.filter(row => row.name.toLocaleLowerCase(locale).includes(needle));
  const focused = document.activeElement?.closest('.character')?.dataset.id;
  $('characters').replaceChildren(...visible.map(row => {
    const button = document.createElement('button'); button.tabIndex=-1; button.className='character'; button.type='button'; button.dataset.id=row.id;
    button.setAttribute('aria-pressed',String(selected===row.id));
    const icon = document.createElement('span'); icon.className='class-icon';
    const image = document.createElement('img'); image.src=`/class-icons/${row.classId}.jpg`; image.alt='';
    const presence = document.createElement('span'); presence.className='presence'+(row.online?' online':'');
    icon.append(image,presence);
    const copy = document.createElement('span'); copy.className='character-copy';
    const name = document.createElement('strong'); name.textContent=row.name; name.style.color=classColor(row.classId);
    const subtitle = document.createElement('small'); subtitle.textContent=`${className(row.classId,locale)} · ${label('level')} ${row.level}`;
    copy.append(name,subtitle); button.append(icon,copy); button.addEventListener('click',() => select(row.id)); return button;
  }));
  if (focused) $('characters').querySelector(`[data-id="${focused}"]`)?.focus({preventScroll:true});
  const message = syncing ? 'refreshing' : status==='unavailable' ? 'unavailable' : status==='loading' ? 'loading' : status==='cached' ? 'cached' : !roster.length ? 'empty' : !visible.length ? 'noMatch' : null;
  $('roster-status').textContent = message ? label(message) : '';
  $('retry').hidden = !['cached','unavailable'].includes(status);
  $('retry').disabled = Boolean(pending || syncing);
  $('retry').ariaBusy = String(Boolean(pending || syncing));
  $('retry').querySelector('[data-label]').textContent = label(syncing ? 'refreshing' : 'retry');
  if (!selected) { $('empty-state').hidden=false; $('empty-state').textContent=label(roster.length?'select':message || 'empty'); }
}

async function refresh(force=false) {
  if (pending || document.hidden || (force && syncing)) return;
  pending = true;
  render();
  try {
    const response = await fetch(force ? '/characters.json?refresh=1' : '/characters.json',{signal:AbortSignal.timeout(10000)});
    if (!response.ok) throw new Error('Roster unavailable');
    const result = await response.json();
    if (!Array.isArray(result.characters) || result.characters.some(row => !row || typeof row.id!=='string'
      || !/^[1-9][0-9]{0,9}$/.test(row.id) || typeof row.name!=='string' || !row.name.length
      || ![1,2,3,4,5,6,7,8,9,11].includes(row.classId) || !Number.isInteger(row.level)
      || row.level<1 || row.level>80 || typeof row.available!=='boolean')
      || new Set(result.characters.map(row => row.id)).size!==result.characters.length) throw new Error('Invalid roster');
    roster=result.characters; status=['loading','ready','cached','unavailable'].includes(result.status)?result.status:'unavailable'; syncing=result.refreshing===true;
    if (selected && !roster.some(row => row.id===selected)) { selected=null; $('character-view').hidden=true; $('character-view').removeAttribute('src'); }
    if (!selected && roster.length) select(roster[0].id);
    else if (selected) select(selected);
  } catch { status=roster.length?'cached':'unavailable'; syncing=false; }
  finally { pending=false; render(); clearTimeout(timer); if (!document.hidden) timer=setTimeout(refresh,5000); }
}

$('search').addEventListener('input',render);
$('retry').addEventListener('click',() => void refresh(true));
$('edit-profile').addEventListener('click',() => openProfileEditor($('edit-profile')));
$('cancel-profile').addEventListener('click',() => closeProfileEditor());
$('save-profile').addEventListener('click',saveProfile);
for (const [id,key] of [['edit-status','statusMessage'],['edit-bio','bio']]) {
  $(id).addEventListener('input',() => {
    if (!draft) return;
    draft[key] = $(id).value; profileFeedback = undefined; renderEditor();
  });
}
$('edit-status').addEventListener('keydown',event => {
  if (event.key==='Enter' && !event.isComposing) { event.preventDefault(); saveProfile(); }
});
$('edit-bio').addEventListener('keydown',event => {
  if (event.key==='Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) { event.preventDefault(); saveProfile(); }
});
$('change-avatar').addEventListener('click',changeAvatar);
$('edit-banner').addEventListener('click',toggleBannerMenu);
$('reposition-banner').addEventListener('click',openBannerEditor);
$('choose-banner').addEventListener('click',chooseBanner);
$('save-banner').addEventListener('click',saveBanner);
$('cancel-banner').addEventListener('click',cancelBanner);
$('banner-zoom').addEventListener('input',event => setBannerZoom(Number(event.target.value)));
$('banner-zoom-out').addEventListener('click',() => setBannerZoom((bannerDraft?.zoom || 1)-.1));
$('banner-zoom-in').addEventListener('click',() => setBannerZoom((bannerDraft?.zoom || 1)+.1));
$('reset-banner').addEventListener('click',() => {
  if (editorIsBusy() || profile.canModifyBanner!==true) return;
  $('reset-banner-confirm').hidden = false; $('reset-banner').setAttribute('aria-expanded','true');
  $('cancel-reset-banner').focus({preventScroll:true});
});
$('cancel-reset-banner').addEventListener('click',() => closeBannerConfirmation(true));
$('confirm-reset-banner').addEventListener('click',resetBanner);
$('dismiss-notification').addEventListener('click',dismissNotification);
$('banner-crop-stage').addEventListener('dblclick',recenterBanner);
$('banner-crop-stage').addEventListener('wheel',event => {
  if (!bannerEditing || !$('banner-editor').open) return;
  event.preventDefault(); event.stopPropagation();
  if (editorIsBusy() || bannerDrag || !bannerDraft || !Number.isFinite(event.deltaY) || event.deltaY===0) return;
  const unit = event.deltaMode===1 ? 16 : event.deltaMode===2 ? $('banner-crop-stage').clientHeight : 1;
  const delta = event.deltaY*unit;
  const step = Math.max(.01,Math.min(.25,Math.abs(delta)*.001));
  setBannerZoom(bannerDraft.zoom-Math.sign(delta)*step);
},{passive:false});
$('banner-crop-stage').addEventListener('pointerdown',event => {
  if (!bannerEditing || !bannerDraft || bannerDraft.fit!=='cover' || editorIsBusy() || event.button!==0 || !$('banner-crop-image').naturalWidth) return;
  const frame = cropFrame(), geometry = coverGeometry($('banner-crop-image'),frame.width,frame.height,bannerDraft.zoom);
  if (geometry.x<.01 && geometry.y<.01) return;
  bannerDrag = {id:event.pointerId,x:event.clientX,y:event.clientY,positionX:bannerDraft.positionX,positionY:bannerDraft.positionY,overflow:geometry};
  $('banner-crop-stage').setPointerCapture(event.pointerId);
  $('banner-crop-stage').classList.add('banner-dragging');
  $('banner-crop-stage').focus({preventScroll:true}); event.preventDefault();
});
$('banner-crop-stage').addEventListener('pointermove',event => {
  if (!bannerDrag || event.pointerId!==bannerDrag.id || !bannerDraft) return;
  if (bannerDrag.overflow.x>=.01) bannerDraft.positionX = clamp(bannerDrag.positionX-(event.clientX-bannerDrag.x)/bannerDrag.overflow.x);
  if (bannerDrag.overflow.y>=.01) bannerDraft.positionY = clamp(bannerDrag.positionY-(event.clientY-bannerDrag.y)/bannerDrag.overflow.y);
  renderBanner();
});
for (const type of ['pointerup','pointercancel','lostpointercapture']) $('banner-crop-stage').addEventListener(type,event => {
  if (!bannerDrag || event.pointerId!==bannerDrag.id) return;
  bannerDrag = undefined; $('banner-crop-stage').classList.remove('banner-dragging');
  if ($('banner-crop-stage').hasPointerCapture(event.pointerId)) $('banner-crop-stage').releasePointerCapture(event.pointerId);
});
$('banner-crop-stage').addEventListener('keydown',event => {
  if (event.key==='Home' && event.target===$('banner-crop-stage') && bannerEditing) { event.preventDefault(); recenterBanner(); return; }
  const direction = {ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]}[event.key];
  if (!direction || event.target!==$('banner-crop-stage') || !bannerEditing) return;
  event.preventDefault();
  if (!bannerDraft || bannerDraft.fit!=='cover' || editorIsBusy()) return;
  const frame = cropFrame(), geometry = coverGeometry($('banner-crop-image'),frame.width,frame.height,bannerDraft.zoom), step = event.shiftKey?30:10;
  if (geometry.x>=.01) bannerDraft.positionX = clamp(bannerDraft.positionX-direction[0]*step/geometry.x);
  if (geometry.y>=.01) bannerDraft.positionY = clamp(bannerDraft.positionY-direction[1]*step/geometry.y);
  renderBanner();
});
$('banner-editor').addEventListener('cancel',event => {
  event.preventDefault();
  if (!$('reset-banner-confirm').hidden && !editorIsBusy()) closeBannerConfirmation(true);
  else cancelBanner();
});
document.querySelector('.banner-image').addEventListener('load',renderBanner);
$('banner-crop-image').addEventListener('load',renderBanner);
$('banner-crop-image').addEventListener('error',() => { bannerFeedback = {kind:'error',key:'bannerFailed'}; renderEditor(); });
new ResizeObserver(renderBanner).observe($('profile-hero'));
new ResizeObserver(renderBanner).observe($('banner-crop-stage'));
for (const type of ['pointerenter','pointerleave']) $('profile-hero').addEventListener(type,event => {
  if (event.pointerType==='mouse') postProfileAction({action:'profile-header-hover',hovered:type==='pointerenter'});
});
document.addEventListener('pointerdown',event => {
  if (event.button!==0 || !event.isPrimary || event.pointerType!=='mouse' || event.clientY<0 || event.clientY>=124) return;
  const target = event.target instanceof Element ? event.target : null;
  if (!target?.closest('.profile-hero') || document.querySelector('dialog[open]')
      || target.closest('button,input,textarea,select,a,[contenteditable],dialog,[role=menu],[role=menuitem],.element-menu')) return;
  if (postProfileAction({action:'drag-window'})) event.preventDefault();
});
document.addEventListener('pointerdown',event => {
  if (!event.target.closest('.banner-controls')) closeBannerMenu();
  if (!event.target.closest('.banner-reset')) closeBannerConfirmation();
});
document.addEventListener('keydown',event => {
  if (event.key!=='Escape' || event.isComposing || editorIsBusy() || bannerEditing) return;
  if (!$('reset-banner-confirm').hidden) { closeBannerConfirmation(true); event.preventDefault(); }
  else if (!$('banner-menu').hidden) { closeBannerMenu(true); event.preventDefault(); }
  else if (editing) { closeProfileEditor(); event.preventDefault(); }
});
window.chrome?.webview?.addEventListener('message',event => {
  const message = event.data;
  if (message?.type==='profile-editor-open') { openProfileEditor(); return; }
  if (message?.type==='profile-save-result') {
    if (!profileSave) return;
    if (message.accepted===true) profileSave.acknowledged = true;
    else if (message.accepted===false) {
      profileSave = undefined;
      profileFeedback = {kind:'error',message:typeof message.message==='string'?message.message:'',key:'saveRejected'};
    }
    renderEditor(); return;
  }
  if (message?.type==='banner-selected') {
    bannerPicking = false;
    const image = bannerImage(message.image);
    if (image) {
      bannerDraft = {image,positionX:Number.isFinite(message.positionX)?clamp(message.positionX):.5,positionY:Number.isFinite(message.positionY)?clamp(message.positionY):.5,zoom:zoomValue(message.zoom),fit:'cover'};
      bannerEditing = true; bannerFeedback = undefined; bannerRestoreFocus = true; closeBannerConfirmation(); renderEditor();
    } else {
      bannerFeedback = {kind:'error',key:'bannerFailed'}; postProfileAction({action:'cancel-banner'}); renderEditor();
    }
    return;
  }
  if (message?.type==='banner-selection-cancelled') {
    bannerPicking = false; bannerRestoreFocus = true;
    if (typeof message.error==='string' && message.error) bannerFeedback = {kind:'error',message:message.error};
    renderEditor(); return;
  }
  if (message?.type==='banner-save-result') { receiveBannerResult(message); return; }
  if (message?.type!=='profile') return;
  const nextLocale = message.locale==='en'?'en':'fr', languageChanged = locale!==nextLocale;
  locale = nextLocale; receiveProfile(message);
  applyProfile(); if (selected && languageChanged) select(selected,true);
});
document.addEventListener('visibilitychange',() => { clearTimeout(timer); if (!document.hidden) void refresh(); });
window.addEventListener('pagehide',() => { clearTimeout(timer); clearTimeout(notificationTimer); });
window.addEventListener('pageshow',event => { if (event.persisted) void refresh(); });
window.chrome?.webview?.postMessage({action:'ready'});
applyProfile(); icons(); void refresh();
