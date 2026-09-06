import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { chooseLocale, t as translate, slotNames, itemName, itemType, itemLines } from './i18n.mjs';
import { averageEquippedItemLevel, characterStatsRows, statisticsModes, defaultStatisticsMode, statisticsSchools } from './character-stats.mjs';
import {className,raceName,classColor} from './character-labels.mjs';

const $ = id => document.getElementById(id);
const characterScope = location.pathname.match(/^\/characters\/([1-9][0-9]{0,9})\/view$/);
const apiPrefix = characterScope ? `/characters/${characterScope[1]}` : '';
document.documentElement.classList.toggle('embedded',Boolean(characterScope));
let locale = chooseLocale(new URLSearchParams(location.search).get('lang'),null,navigator.languages);
const t = (key,values) => translate(locale,key,values);
const slotIcons = ['crown','gem','shield','shirt','shirt','minus','columns-2','footprints','watch','hand','circle','circle','gem','gem','flag','sword','shield','wand-sparkles','shirt'];
const icons = () => window.lucide.createIcons();
const stage = $('scene');
const loading = $('loading');
const scene = new THREE.Scene();
scene.background = new THREE.Color('#101418');
const camera = new THREE.PerspectiveCamera(35, 1, 0.01, 100);
const sceneCanvas = document.createElement('canvas');
let renderer;
try {
  renderer = new THREE.WebGLRenderer({canvas:sceneCanvas,antialias:true,preserveDrawingBuffer:true});
  renderer.setPixelRatio(Math.min(devicePixelRatio,2));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.toneMapping = THREE.NoToneMapping;
  stage.prepend(sceneCanvas);
} catch (error) {
  renderer?.dispose(); renderer = undefined;
  console.warn('Character 3D preview unavailable',error);
}
sceneCanvas.setAttribute('aria-label',t('model'));
const controls = new OrbitControls(camera,sceneCanvas);
controls.enableDamping = true;
controls.enablePan = false;
controls.maxPolarAngle = Math.PI * .65;
controls.minPolarAngle = Math.PI * .25;
controls.autoRotateSpeed = 1;
scene.add(new THREE.HemisphereLight(0xffffff, 0x646f7b, 2.3));
const key = new THREE.DirectionalLight(0xfff3de, 2.0);
key.position.set(4, 6, 4);
scene.add(key);
const fill = new THREE.DirectionalLight(0xc7e8ee, 1.1);
fill.position.set(-4, 3, -3);
scene.add(fill);
let mixer, body, data, bounds, activeModel, weaponMode, modelStatus = 'loading', renderCount = 0, statisticsLoaded = false, statisticsRequest;
let revision = 'legacy', assetBase = '/assets/', bundleRequest, failedRevision, retryModelAfter = 0;
let animate = !matchMedia('(prefers-reduced-motion: reduce)').matches;
const clock = new THREE.Clock();
const root = new THREE.Group();
scene.add(root);

// RGB and alpha factors from the pinned client's M2-to-EGX blend mapping.
const nativeBlendFactors = {
  2:[THREE.SrcAlphaFactor,THREE.OneMinusSrcAlphaFactor,THREE.OneFactor,THREE.OneMinusSrcAlphaFactor],
  3:[THREE.OneFactor,THREE.OneFactor,THREE.ZeroFactor,THREE.OneFactor],
  4:[THREE.SrcAlphaFactor,THREE.OneFactor,THREE.ZeroFactor,THREE.OneFactor],
  5:[THREE.DstColorFactor,THREE.ZeroFactor,THREE.DstAlphaFactor,THREE.ZeroFactor],
  6:[THREE.DstColorFactor,THREE.SrcColorFactor,THREE.DstAlphaFactor,THREE.SrcAlphaFactor],
  7:[THREE.OneFactor,THREE.OneMinusSrcAlphaFactor,THREE.OneFactor,THREE.OneMinusSrcAlphaFactor]
};

function prepareMaterials(object) {
  object.traverse(mesh => {
    if (!mesh.isMesh) return;
    mesh.frustumCulled = false;
    for (const material of Array.isArray(mesh.material) ? mesh.material : [mesh.material]) {
      // Legacy exports lack the native material mode. New exports distinguish
      // opaque armor (whose alpha may encode reflections) from hair cutouts.
      if (!Number.isInteger(material.userData.m2BlendMode)) {
        material.side = THREE.DoubleSide;
        material.alphaTest = .45;
      } else {
        const factors = nativeBlendFactors[material.userData.m2BlendMode];
        material.depthWrite = !factors;
        if (factors) {
          material.transparent = true;
          material.blending = THREE.CustomBlending;
          material.blendEquation = THREE.AddEquation;
          [material.blendSrc,material.blendDst,material.blendSrcAlpha,material.blendDstAlpha] = factors;
        }
      }
      material.roughness = 1;
      material.metalness = 0;
      if (material.name === '117168') {
        material.emissiveMap = material.map;
        material.emissive.set(0xffffff);
        material.emissiveIntensity = .6;
      }
    }
  });
}

function framingDistance(targetBounds) {
  const size = targetBounds.getSize(new THREE.Vector3());
  const vertical = (size.y * 1.32) / (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)));
  const projectedWidth = size.x * .2 + size.z * .98;
  const horizontal = (projectedWidth * 1.35) / (2 * Math.tan(THREE.MathUtils.degToRad(camera.fov / 2)) * camera.aspect);
  return Math.max(vertical,horizontal);
}

function fit() {
  if (!bounds) return;
  const size = bounds.getSize(new THREE.Vector3());
  const center = bounds.getCenter(new THREE.Vector3());
  center.y -= size.y * .055;
  controls.target.copy(center);
  const distance = framingDistance(bounds);
  camera.position.copy(center).add(new THREE.Vector3(distance * .98, distance * .06, distance * .2));
  controls.minDistance = distance * .45;
  controls.maxDistance = distance * 2;
  controls.update();
}

function resize() {
  const width = stage.clientWidth, height = stage.clientHeight;
  if (width<=0 || height<=0) return;
  const offset = camera.position.clone().sub(controls.target);
  const previousDistance = bounds && offset.lengthSq()>0 ? framingDistance(bounds) : 0;
  renderer?.setSize(width,height,false);
  camera.aspect = width/height;
  camera.updateProjectionMatrix();
  if (previousDistance>0) {
    const distance = framingDistance(bounds);
    camera.position.copy(controls.target).add(offset.multiplyScalar(distance/previousDistance));
    controls.minDistance = distance*.45;
    controls.maxDistance = distance*2;
    controls.update();
  } else fit();
}
new ResizeObserver(resize).observe(stage);

const popover = $('item-popover');
let itemAnchor = null, pinnedItem = false, suppressHover = false, lastPointer, showTimer, hideTimer;

function positionItem() {
  if (!itemAnchor || popover.hidden) return;
  const rect = itemAnchor.getBoundingClientRect();
  const { width, height } = popover.getBoundingClientRect();
  let x, y;
  if (matchMedia('(max-width:760px)').matches) {
    x = (innerWidth - width) / 2;
    y = innerHeight - height - 12;
  } else {
    x = rect.left + rect.width / 2 < innerWidth / 2 ? rect.right + 10 : rect.left - width - 10;
    y = rect.top + (rect.height - height) / 2;
    if (itemAnchor.closest('.weapons')) { x = rect.left + (rect.width - width) / 2; y = rect.top - height - 12; }
  }
  popover.style.left = Math.max(12, Math.min(x, innerWidth - width - 12)) + 'px';
  popover.style.top = Math.max(12, Math.min(y, innerHeight - height - 12)) + 'px';
}

function closeItem(restoreFocus = false) {
  clearTimeout(showTimer);
  clearTimeout(hideTimer);
  const previous = itemAnchor;
  const wasPinned = pinnedItem;
  if (restoreFocus) suppressHover = true;
  itemAnchor?.setAttribute('aria-expanded', 'false');
  itemAnchor?.removeAttribute('aria-describedby');
  itemAnchor = null;
  pinnedItem = false;
  popover.hidden = true;
  if (restoreFocus && wasPinned) previous?.focus({ preventScroll: true });
  // Restoring keyboard focus should not immediately reopen the hover preview.
  clearTimeout(showTimer);
}

function showItem(item, anchor, pin = false) {
  clearTimeout(showTimer);
  clearTimeout(hideTimer);
  if (pinnedItem && !pin) return;
  itemAnchor?.setAttribute('aria-expanded', 'false');
  itemAnchor?.removeAttribute('aria-describedby');
  itemAnchor = anchor;
  pinnedItem = pin;
  anchor.setAttribute('aria-expanded', String(pin));
  if (!pin) anchor.setAttribute('aria-describedby', 'item-popover');
  popover.setAttribute('role', pin ? 'dialog' : 'tooltip');
  popover.dataset.quality = item.quality;
  $('close-item').hidden = !pin;
  $('detail-slot').textContent = slotNames[locale][item.slot];
  $('detail-name').textContent = itemName(item,locale);
  $('detail-level').textContent = item.itemLevel;
  $('detail-icon').hidden = !item.icon;
  if (item.icon) $('detail-icon').src = assetBase + item.icon;
  else $('detail-icon').removeAttribute('src');
  $('detail-type').textContent = item.details ? itemType(item.details,locale) : '';
  $('detail-type').hidden = !$('detail-type').textContent;
  $('detail-lines').replaceChildren(...itemLines(item.details,locale).map(line => {
    const element = document.createElement('p');
    element.className = 'tooltip-line ' + line.kind;
    element.textContent = line.text;
    return element;
  }));
  popover.hidden = false;
  positionItem();
}

function previewItem(item, anchor) {
  clearTimeout(hideTimer);
  clearTimeout(showTimer);
  if (!pinnedItem) showTimer = setTimeout(() => showItem(item, anchor), 160);
}
function leaveItem() {
  clearTimeout(showTimer);
  if (!pinnedItem) hideTimer = setTimeout(() => closeItem(), 130);
}
popover.addEventListener('pointerenter', () => clearTimeout(hideTimer));
popover.addEventListener('pointerleave', leaveItem);
$('close-item').onclick = () => closeItem(true);
document.addEventListener('pointerdown', event => {
  if (!popover.hidden && !popover.contains(event.target) && !event.target.closest('button.slot, #weapon-mode')) closeItem();
});
document.addEventListener('pointermove', event => {
  if (event.pointerType==='touch') return;
  const moved = !lastPointer || lastPointer.x!==event.clientX || lastPointer.y!==event.clientY;
  lastPointer = {x:event.clientX,y:event.clientY};
  // Dismissing a sheet may uncover another slot without the pointer actually moving.
  if (suppressHover && moved) {
    suppressHover = false;
    const anchor = event.target.closest('button.slot');
    const item = data?.equipment.find(item => item.slot===Number(anchor?.dataset.slot));
    if (item && anchor) previewItem(item,anchor);
  }
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    clearTimeout(showTimer);
    if (!popover.hidden) { event.preventDefault(); closeItem(true); }
  }
});
window.addEventListener('resize', positionItem);
window.addEventListener('scroll', () => { if (pinnedItem) positionItem(); else closeItem(); }, true);

function equipmentColumn(target, slots) {
  $(target).replaceChildren();
  for (const slot of slots) {
    const item = data.equipment.find(item => item.slot === slot);
    const button = document.createElement(item ? 'button' : 'div');
    button.className = item ? 'slot' : 'slot empty-slot';
    button.dataset.slot = slot;
    button.dataset.quality = item?.quality ?? 0;
    if (item) {
      button.tabIndex = -1;
      button.setAttribute('aria-expanded', 'false');
      button.setAttribute('aria-haspopup', 'dialog');
      button.setAttribute('aria-controls', 'item-popover');
      if (item.icon) {
        const img = document.createElement('img');
        img.src = assetBase + item.icon;
        img.alt = '';
        button.append(img);
      } else {
        const icon = document.createElement('span');
        icon.className = 'empty-icon';
        icon.innerHTML = `<i data-lucide="${slotIcons[slot]}"></i>`;
        button.append(icon);
      }
    } else {
      button.setAttribute('role', 'img');
      button.setAttribute('aria-label', t('empty',{slot:slotNames[locale][slot]}));
      button.title = slotNames[locale][slot];
      const empty = document.createElement('span');
      empty.className = 'empty-icon';
      empty.innerHTML = `<i data-lucide="${slotIcons[slot]}"></i>`;
      button.append(empty);
      $(target).append(button);
      continue;
    }
    const copy = document.createElement('span');
    copy.className = 'slot-copy';
    const label = document.createElement('span');
    label.className = 'slot-label';
    label.textContent = slotNames[locale][slot];
    const name = document.createElement('span');
    name.className = 'slot-name';
    name.textContent = itemName(item,locale);
    button.title = itemName(item,locale);
    copy.append(label, name);
    button.append(copy);
    button.addEventListener('pointerenter', event => { if (event.pointerType !== 'touch' && !suppressHover) previewItem(item, button); });
    button.addEventListener('pointerleave', leaveItem);
    button.addEventListener('focus', () => previewItem(item, button));
    button.addEventListener('blur', leaveItem);
    button.addEventListener('click', event => {
      if (pinnedItem && itemAnchor === button) closeItem();
      else {
        showItem(item, button, true);
        if (event.detail === 0) $('close-item').focus({ preventScroll: true });
      }
    });
    $(target).append(button);
  }
}

function setAnimation(value) {
  animate = value;
  const button = $('animate');
  button.setAttribute('aria-pressed', String(value));
  button.title = button.ariaLabel = t(value ? 'pause' : 'play');
  button.innerHTML = `<i data-lucide="${value ? 'pause' : 'play'}"></i>`;
  icons();
}
$('animate').onclick = () => setAnimation(!animate);
$('rotate').onclick = () => {
  controls.autoRotate = !controls.autoRotate;
  $('rotate').setAttribute('aria-pressed', String(controls.autoRotate));
};
$('reset').onclick = () => { controls.autoRotate = false; $('rotate').setAttribute('aria-pressed','false'); fit(); };
$('weapon-mode').onclick = () => {
  if (!activeModel || data.weaponModes?.length!==2) return;
  const previousBounds = bounds;
  const previousOffset = camera.position.clone().sub(controls.target);
  setModelWeaponMode(activeModel,data,weaponMode==='ranged'?'melee':'ranged');
  weaponMode = activeModel.weaponMode; bounds = activeModel.bounds;
  const ratio = framingDistance(bounds)/framingDistance(previousBounds);
  controls.target.copy(bounds.getCenter(new THREE.Vector3()));
  controls.target.y -= bounds.getSize(new THREE.Vector3()).y*.055;
  camera.position.copy(controls.target).add(previousOffset.multiplyScalar(ratio));
  controls.minDistance *= ratio; controls.maxDistance *= ratio;
  controls.update();
  renderWeaponMode();
};
for (const [id, factor] of [['zoom-in', .82], ['zoom-out', 1.22]]) {
  $(id).onclick = () => { camera.position.sub(controls.target).multiplyScalar(factor).add(controls.target); controls.update(); };
}
$('fullscreen').onclick = async () => {
  closeItem();
  try { if (document.fullscreenElement) await document.exitFullscreen(); else await stage.requestFullscreen(); }
  catch { $('fullscreen').disabled = true; }
};
document.addEventListener('fullscreenchange', () => {
  const active = Boolean(document.fullscreenElement);
  $('fullscreen').title = $('fullscreen').ariaLabel = t(active ? 'exitFullscreen' : 'fullscreen');
  $('fullscreen').innerHTML = `<i data-lucide="${active ? 'minimize' : 'maximize'}"></i>`;
  icons();
});

renderer?.setAnimationLoop(() => {
  const delta = Math.min(clock.getDelta(), .05);
  if (mixer && animate && !document.hidden) mixer.update(delta);
  controls.update(delta);
  renderer.render(scene, camera);
  renderCount++;
});

let statisticsMode, statisticsSchool = 0;
$('stats-school').addEventListener('change',() => { statisticsSchool = Number($('stats-school').value); renderCharacterSummary(); });
function renderCharacterSummary() {
  const average = averageEquippedItemLevel(data?.equipment);
  $('average-item-level').textContent = average===null ? '—' : new Intl.NumberFormat(locale==='fr'?'fr-FR':'en-US',{maximumFractionDigits:1}).format(average);
  $('average-item-level').title = t(average===null?'statUnavailable':'averageLevelHint');
  const character = data || {class:'Mage'};
  const choices = statisticsModes(character,locale);
  const mode = choices.some(choice => choice.key===statisticsMode) ? statisticsMode : defaultStatisticsMode(character);
  const focusedMode = document.activeElement?.closest('#stats-modes button')?.dataset.mode;
  $('stats-modes').hidden = choices.length<2;
  $('stats-modes').replaceChildren(...choices.map(choice => {
    const button = document.createElement('button');
    button.tabIndex = -1;
    button.type = 'button'; button.dataset.mode = choice.key;
    button.title = button.ariaLabel = choice.label;
    button.setAttribute('aria-pressed',String(choice.key===mode));
    const icon = document.createElement('i'); icon.dataset.lucide = choice.icon; button.append(icon);
    button.addEventListener('click',() => { statisticsMode = choice.key; renderCharacterSummary(); });
    return button;
  }));
  $('stats-school').hidden = !['spell','healing'].includes(mode);
  $('stats-school').disabled = !data?.statistics?.schools?.length;
  $('stats-school').replaceChildren(...statisticsSchools(locale).map(school => new Option(school.label,String(school.id),false,school.id===statisticsSchool)));
  icons();
  if (focusedMode) $('stats-modes').querySelector(`[data-mode="${focusedMode}"]`)?.focus();
  const rows = characterStatsRows(character,locale,mode,statisticsSchool);
  $('stats-title').title = data?.statistics?.source==='arthas-combat-stats' ? t('combatSnapshotHint',{date:new Date(data.statistics.savedAt).toLocaleString(locale==='fr'?'fr-FR':'en-US')}) : '';
  $('character-stats').replaceChildren(...rows.map(row => {
    const group = document.createElement('div');
    group.className = 'character-stat';
    group.dataset.stat = row.key;
    const label = document.createElement('dt');
    label.textContent = row.label;
    if (row.hint) label.title = row.hint;
    const value = document.createElement('dd');
    value.textContent = row.value;
    if (row.negative) value.className = 'negative';
    if (!row.known) { value.className = 'unavailable'; value.title = t('statUnavailable'); value.setAttribute('aria-label',t('statUnavailable')); }
    group.append(label,value);
    return group;
  }));
  const missing = rows.filter(row => !row.known).length;
  $('stats-status').hidden = missing===0;
  $('stats-status').textContent = !data ? t('loadingData') : t(missing===rows.length?'statsMissing':'statsPartial');
}

function applyLocale() {
  document.documentElement.lang = locale;
  document.title = `${data?.name || 'Atlas'} | ${t('armory')}`;
  document.querySelectorAll('[data-i18n]').forEach(el => { el.textContent = t(el.dataset.i18n); });
  document.querySelectorAll('[data-i18n-label]').forEach(el => {
    el.setAttribute('aria-label',t(el.dataset.i18nLabel));
    if (el.matches('button')) el.title = t(el.dataset.i18nLabel);
  });
  sceneCanvas.setAttribute('aria-label',t('model'));
  document.querySelector('h1').textContent = data?.name || '';
  $('character-class').textContent = data ? className(data.classId,locale) : '';
  $('character-class').style.color = classColor(data?.classId);
  $('character-race').textContent = data ? raceName(data.raceId || 10,locale) : '';
  $('animate').title = $('animate').ariaLabel = t(animate ? 'pause' : 'play');
  $('fullscreen').title = $('fullscreen').ariaLabel = t(document.fullscreenElement ? 'exitFullscreen' : 'fullscreen');
  if (data && characterScope) showModelState();
  renderWeaponMode();
  renderCharacterSummary();
  document.querySelectorAll('.slot').forEach(button => {
    const slot = Number(button.dataset.slot);
    const item = data?.equipment.find(item => item.slot===slot);
    if (item) {
      button.querySelector('.slot-label').textContent = slotNames[locale][slot];
      button.querySelector('.slot-name').textContent = itemName(item,locale);
      button.title = itemName(item,locale);
      button.setAttribute('aria-label',`${slotNames[locale][slot]} : ${itemName(item,locale)}`);
    } else {
      button.title = slotNames[locale][slot];
      button.setAttribute('aria-label',t('empty',{slot:slotNames[locale][slot]}));
    }
  });
  if (itemAnchor && !popover.hidden) showItem(data.equipment.find(item => item.slot===Number(itemAnchor.dataset.slot)),itemAnchor,pinnedItem);
}
let localeRequest;
let statisticsTimer, statisticsAbort, statisticsPaused = false;
async function fetchJson(url) {
  const response = await fetch(url,{signal:AbortSignal.timeout(10000)});
  if (!response.ok) throw new Error(`Armory resource unavailable (${response.status})`);
  return response.json();
}

async function readManifest() {
  const manifest = await fetchJson(apiPrefix+'/armory.json');
  const expected = manifest.revision==='legacy' ? '/assets/' : `${apiPrefix}/snapshots/${manifest.revision}/assets/`;
  if (!/^(legacy|[a-f0-9]{32})$/.test(manifest.revision) || manifest.assetBase!==expected) throw new Error('Invalid armory revision');
  return manifest;
}

async function loadCharacter(manifest) {
  const next = await fetchJson(manifest.assetBase+'character.json');
  const details = await fetchJson(manifest.assetBase+'item-details.json');
  if ((characterScope ? next.characterId!==characterScope[1] : next.name!=='Flowmage') || !Array.isArray(next.equipment) || details.characterCapturedAt!==next.capturedAt) throw new Error('Incompatible armory data');
  for (const item of next.equipment) {
    item.details = details.items.find(row => row.itemId===item.itemId && (row.slot===undefined || row.slot===item.slot));
    if (!item.details) throw new Error('Missing item details');
  }
  return next;
}

function disposeModel(object) {
  const geometries = new Set(), materials = new Set(), textures = new Set(), skeletons = new Set();
  object.traverse(mesh => {
    if (mesh.geometry) geometries.add(mesh.geometry);
    if (mesh.skeleton) skeletons.add(mesh.skeleton);
    for (const material of Array.isArray(mesh.material) ? mesh.material : mesh.material ? [mesh.material] : []) {
      materials.add(material);
      for (const value of Object.values(material)) if (value?.isTexture) textures.add(value);
    }
  });
  for (const entry of [...geometries,...materials,...textures,...skeletons]) entry.dispose();
}

function renderedBounds(object) {
  object.updateMatrixWorld(true);
  const result = new THREE.Box3(), point = new THREE.Vector3();
  object.traverseVisible(mesh => {
    if (!mesh.isMesh) return;
    const indices = mesh.geometry.index?.array ?? Array.from({length:mesh.geometry.attributes.position.count},(_,index) => index);
    for (const index of new Set(indices)) result.expandByPoint(mesh.localToWorld(mesh.getVertexPosition(index,point)));
  });
  if (result.isEmpty()) throw new Error('Empty character model');
  return result;
}

function setModelWeaponMode(model,character,preferred) {
  const modes = character.weaponModes?.filter(mode => ['melee','ranged'].includes(mode)) || [];
  const mode = modes.includes(preferred) ? preferred : modes.includes(character.defaultWeaponMode) ? character.defaultWeaponMode : modes[0];
  const clip = mode ? model.animations.find(animation => animation.name===character.animationByWeaponMode?.[mode]) : model.animations[0];
  if (!clip?.tracks.length) throw new Error('Missing weapon animation');
  const time = model.mixer.time;
  model.mixer.stopAllAction();
  model.mixer.clipAction(clip).reset().play();
  model.mixer.setTime(time || .001);
  model.body.traverse(object => {
    if (object.userData.weaponMode) object.visible=object.userData.weaponMode===mode;
  });
  model.weaponMode = mode;
  model.bounds = renderedBounds(model.body);
}

function renderWeaponMode() {
  const button = $('weapon-mode');
  button.hidden = !body || data?.weaponModes?.length!==2;
  const label = t(weaponMode==='ranged'?'weaponRanged':'weaponMelee');
  button.title = button.ariaLabel = t(weaponMode==='ranged'?'showMelee':'showRanged');
  button.dataset.mode = weaponMode || '';
  button.querySelector('span').textContent = label;
}

async function loadModel(next,base) {
  const manager = new THREE.LoadingManager();
  const loader = new GLTFLoader(manager);
  const objects = [];
  let expired = false, timeout, nextMixer;
  const resource = name => {
    if (!/^[a-zA-Z0-9_-]+\.(png|gltf)$/.test(name)) throw new Error('Invalid model resource');
    return base+name;
  };
  const load = async name => {
    const gltf = await loader.loadAsync(resource(name));
    if (expired) { disposeModel(gltf.scene); throw new Error('Armory loading expired'); }
    objects.push(gltf.scene);
    prepareMaterials(gltf.scene);
    return gltf;
  };
  try {
    return await Promise.race([
      (async () => {
        const gltf = await load('flowmage.gltf');
        const nextBody = gltf.scene;
        nextMixer = new THREE.AnimationMixer(nextBody);
        if (!gltf.animations[0]?.tracks.length) throw new Error('Missing idle animation');
        for (const attachment of next.attached) {
          const gear = await load(attachment.url);
          const bone = nextBody.getObjectByName(attachment.bone);
          if (!bone) throw new Error('Missing attachment bone');
          const anchor = new THREE.Group();
          anchor.name = `equipment-slot-${attachment.slot}-${attachment.attachmentId}`;
          anchor.userData = {equipmentSlot:attachment.slot,attachmentId:attachment.attachmentId,weaponMode:attachment.weaponMode};
          anchor.position.fromArray(attachment.offset);
          bone.add(anchor); anchor.add(gear.scene);
        }
        await Promise.all(next.equipment.filter(item => item.icon).map(async item => {
          const icon = new Image(); icon.src = resource(item.icon); await icon.decode();
        }));
        const model = {body:nextBody,mixer:nextMixer,animations:gltf.animations};
        setModelWeaponMode(model,next,weaponMode);
        await renderer.compileAsync(nextBody,camera,scene);
        if (expired) throw new Error('Armory loading expired');
        return model;
      })(),
      new Promise((_,reject) => { timeout = setTimeout(() => { expired = true; manager.abort(); reject(new Error('Armory loading timed out')); },30000); })
    ]);
  } catch (error) {
    expired = true; manager.abort(); nextMixer?.stopAllAction();
    for (const object of objects) if (!object.parent) disposeModel(object);
    throw error;
  } finally { clearTimeout(timeout); }
}

function commitBundle(next,manifest,model) {
  const pinned = pinnedItem && itemAnchor ? {slot:Number(itemAnchor.dataset.slot),id:data.equipment.find(item => item.slot===Number(itemAnchor.dataset.slot))?.itemId} : null;
  const focusSlot = document.activeElement?.closest('.slot')?.dataset.slot;
  const focusInPopover = popover.contains(document.activeElement);
  const oldBody = body, oldMixer = mixer;
  closeItem();
  const ratio = bounds && model ? framingDistance(model.bounds)/framingDistance(bounds) : 1;
  const offset = camera.position.clone().sub(controls.target).multiplyScalar(ratio);
  data = next; revision = manifest.revision; assetBase = manifest.assetBase;
  activeModel = model; weaponMode = model?.weaponMode ?? weaponMode; modelStatus = renderer ? manifest.modelStatus || (model?'ready':'unavailable') : 'graphics-unavailable';
  body = model?.body; mixer = model?.mixer; bounds = model?.bounds;
  if (body) root.add(body);
  if (oldBody) root.remove(oldBody);
  mixer?.setTime(oldMixer?.time ?? .001);
  $('level').textContent = data.level;
  equipmentColumn('left',[0,1,2,14,4,3,18,8]);
  equipmentColumn('right',[9,5,6,7,10,11,12,13]);
  equipmentColumn('weapons',[15,16,17]);
  applyLocale(); setAnimation(animate);
  if (oldBody && model) {
    const size = bounds.getSize(new THREE.Vector3());
    controls.target.copy(bounds.getCenter(new THREE.Vector3()));
    controls.target.y -= size.y*.055;
    camera.position.copy(controls.target).add(offset);
    controls.minDistance *= ratio; controls.maxDistance *= ratio;
    controls.update();
  } else resize();
  if (oldBody) { oldMixer?.stopAllAction(); oldMixer?.uncacheRoot(oldBody); disposeModel(oldBody); }
  showModelState();
  const item = pinned && data.equipment.find(item => item.slot===pinned.slot && item.itemId===pinned.id);
  if (item) {
    showItem(item,document.querySelector(`.slot[data-slot="${item.slot}"]`),true);
    if (focusInPopover) $('close-item').focus({preventScroll:true});
  } else if (focusInPopover) $('animate').focus({preventScroll:true});
  if (focusSlot) document.querySelector(`button.slot[data-slot="${focusSlot}"]`)?.focus({preventScroll:true});
}

async function refreshArmory() {
  if (!window.armory?.ready || statisticsPaused || document.hidden) return;
  if (bundleRequest) return bundleRequest;
  bundleRequest = (async () => {
    let model, manifest;
    try {
      manifest = await readManifest();
      if (manifest.revision===revision) {
        modelStatus = renderer ? manifest.modelStatus || (body?'ready':manifest.modelReady===false?'unavailable':'loading') : 'graphics-unavailable';
        showModelState();
        if (body || !renderer || manifest.modelReady===false) return;
      }
      if (manifest.revision===failedRevision && Date.now()<retryModelAfter) return;
      const next = await loadCharacter(manifest);
      model = !renderer || manifest.modelReady===false ? null : await loadModel(next,manifest.assetBase);
      if (statisticsPaused || (await readManifest()).revision!==manifest.revision) return;
      commitBundle(next,manifest,model);
      failedRevision = undefined;
      model = null;
    } catch (error) {
      failedRevision = manifest?.revision; retryModelAfter = Date.now()+30000;
      console.warn('Keeping the previous armory snapshot',error);
    }
    finally { if (model) { model.mixer.stopAllAction(); disposeModel(model.body); } }
  })();
  try { await bundleRequest; } finally { bundleRequest = undefined; }
}

async function refreshStatistics() {
  if (!data) return;
  if (statisticsRequest) return statisticsRequest;
  statisticsRequest = (async () => {
    statisticsAbort = new AbortController();
    const timeout = setTimeout(() => statisticsAbort.abort(),10000);
    try {
      const response = await fetch(apiPrefix+'/statistics.json',{signal:statisticsAbort.signal});
      if (!response.ok) return;
      const result = await response.json();
      if (result.status==='ready' && result.record?.characterCapturedAt===data.capturedAt && result.record?.characterName===data.name) {
        if (data.statistics?.schemaVersion>result.record.schemaVersion || Date.parse(data.statistics?.savedAt)>Date.parse(result.record.savedAt)) return;
        const content = ({observedAt,...record}) => JSON.stringify(record);
        if (data.statistics && content(data.statistics)===content(result.record)) return;
        data.statistics = result.record;
        renderCharacterSummary();
      } else if (characterScope && result.status==='unavailable' && data.statistics) {
        // A successful account-scoped response can explicitly withdraw a snapshot.
        // Network failures still keep the last usable statistics above.
        delete data.statistics;
        renderCharacterSummary();
      }
    } catch (error) { if (error.name!=='AbortError') console.warn('Statistics cache unavailable',error); }
    finally { clearTimeout(timeout); statisticsAbort = undefined; statisticsLoaded = true; }
  })();
  try { await statisticsRequest; } finally { statisticsRequest = undefined; }
}
function scheduleStatistics() {
  clearTimeout(statisticsTimer);
  if (statisticsPaused || document.hidden) return;
  statisticsTimer = setTimeout(async () => {
    void refreshArmory();
    await refreshStatistics();
    scheduleStatistics();
  },5000);
}
async function syncLocale() {
  if (localeRequest) return localeRequest;
  localeRequest = (async () => {
    let config = {};
    try { const response = await fetch('/viewer-config.json'); if (response.ok) config = await response.json(); }
    catch (error) { console.warn('Locale configuration unavailable',error); }
    locale = chooseLocale(new URLSearchParams(location.search).get('lang'),config.locale,navigator.languages);
    applyLocale();
  })();
  try { await localeRequest; } finally { localeRequest = undefined; }
}
function resumeStatistics() {
  if (statisticsPaused || document.hidden) return;
  void syncLocale();
  void refreshArmory();
  void refreshStatistics();
  scheduleStatistics();
}
window.addEventListener('focus',resumeStatistics);
document.addEventListener('visibilitychange',() => {
  if (!document.hidden) resumeStatistics();
  else clearTimeout(statisticsTimer);
});
window.addEventListener('pagehide',() => {
  statisticsPaused = true;
  clearTimeout(statisticsTimer);
  statisticsAbort?.abort();
});
window.addEventListener('pageshow',() => { statisticsPaused = false; resumeStatistics(); });
applyLocale();

try {
  await syncLocale();
  const manifest = await readManifest();
  assetBase = manifest.assetBase; revision = manifest.revision;
  modelStatus = renderer ? manifest.modelStatus || (manifest.modelReady===false?'unavailable':'loading') : 'graphics-unavailable';
  data = await loadCharacter(manifest);
  void refreshStatistics();
  scheduleStatistics();
  $('level').textContent = data.level;
  equipmentColumn('left', [0,1,2,14,4,3,18,8]);
  equipmentColumn('right', [9,5,6,7,10,11,12,13]);
  equipmentColumn('weapons', [15,16,17]);
  applyLocale();
  const model = !renderer || manifest.modelReady===false ? null : await loadModel(data,assetBase);
  activeModel = model; weaponMode = model?.weaponMode;
  body = model?.body; mixer = model?.mixer; bounds = model?.bounds;
  if (body) root.add(body);
  resize();
  setAnimation(animate);
  showModelState();
  window.armory = {
    ready: true, renderer, camera, controls, root,
    get data() { return data; }, get mixer() { return mixer; }, get bounds() { return bounds; },
    get revision() { return revision; }, get refreshingModel() { return Boolean(bundleRequest); },
    get weaponMode() { return weaponMode; },
    get frames() { return renderCount; },
    get statisticsLoaded() { return statisticsLoaded; },
    get animatedTime() { return mixer?.time ?? 0; }
  };
} catch (error) {
  console.error(error);
  loading.removeAttribute('data-i18n');
  loading.textContent = t('loadFailed');
  window.armory = { ready: false, error: error.message };
}
icons();

function showModelState() {
  loading.hidden = Boolean(body);
  if (!body) {
    loading.removeAttribute('data-i18n');
    loading.textContent = t(modelStatus==='building'?'modelBuilding':modelStatus==='loading'?'loading':modelStatus==='client-missing'?'modelClientMissing':modelStatus==='graphics-unavailable'?'modelGraphicsUnavailable':'modelUnavailable');
  }
  document.querySelector('.scene-toolbar').hidden = !body;
  renderWeaponMode();
}
