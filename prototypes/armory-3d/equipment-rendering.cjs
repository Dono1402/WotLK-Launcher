// Adapt the pinned wow.export equipment rules to AzerothCore's zero-based equipment slots.
const collectionGroups = {
  0:[27,21],4:[8,10,13,22,28],5:[18],6:[11,9,13],7:[5,20],9:[4,23],14:[15]
};
const nonVisualSlots = new Set([1,10,11,12,13]);
// ChrCustomizationID aliases in the pinned 3.4.3 client. The server stores five
// legacy bytes, including piercings, markings, tusks and horns as facialStyle.
const legacyCustomizationFields = {
  1:'skin',2:'face',3:'hairStyle',4:'hairColor',5:'facialStyle',
  10:'facialStyle',11:'facialStyle',12:'facialStyle',13:'facialStyle',14:'facialStyle',
  15:'facialStyle',16:'hairStyle',17:'hairColor',18:'facialStyle',19:'facialStyle',
  21:'facialStyle',23:'facialStyle',165:'skin',177:'face'
};

function selectAppearance(options,choices,character) {
  const fields = new Set();
  const selected = options.filter(option => Object.hasOwn(legacyCustomizationFields,option.ChrCustomizationID)).map(option => {
    const field = legacyCustomizationFields[option.ChrCustomizationID];
    if (fields.has(field)) throw new Error(`Ambiguous appearance option ${field}`);
    fields.add(field);
    const value = character[field];
    const choice = choices.find(row => row.ChrCustomizationOptionID===option.ID && row.OrderIndex===value);
    if (!choice) throw new Error(`No exact choice for ${option.Name_lang}`);
    return {optionID:option.ID,choiceID:choice.ID,name:option.Name_lang,value};
  });
  if (selected.length!==5) throw new Error(`Expected five appearance choices, found ${selected.length}`);
  return selected;
}

function renderError(item,reason) {
  return Object.assign(new Error(`Item ${item.itemId} in slot ${item.slot}: ${reason}`),{code:'ARMORY_EQUIPMENT_UNSUPPORTED',itemId:item.itemId,slot:item.slot});
}

function planEquipment(equipment) {
  return equipment.map(item => {
    const models = item.models?.models || [];
    if (!Array.isArray(models) || models.some(id => !Number.isSafeInteger(id) || id<1)) throw renderError(item,'invalid model data');
    const result = {slot:item.slot,itemId:item.itemId,attachments:[],collections:[],kind:'nonVisual'};
    if (nonVisualSlots.has(item.slot) || (item.slot===17 && item.inventoryType===28)) return result;
    const bow = item.itemClassId===2 && item.itemSubclassId===2;
    let points = [];
    if (item.slot===0) points = [11];
    else if (item.slot===2) points = [6,5];
    else if (item.slot===14) points = [12];
    else if (item.slot===15 || item.slot===17) points = [bow ? 2 : 1];
    else if (item.slot===16) points = [item.inventoryType===14 ? 0 : 2];
    const weaponMode = [15,16].includes(item.slot) ? 'melee' : item.slot===17 ? 'ranged' : undefined;
    for (let i=0;i<models.length;i++) {
      if (i<points.length) result.attachments.push({modelIndex:i,modelFileId:models[i],attachmentId:points[i],...weaponMode ? {weaponMode} : {}});
      else {
        if (!collectionGroups[item.slot]) throw renderError(item,'additional model has no verified collection binding');
        result.collections.push({modelIndex:i,modelFileId:models[i]});
      }
    }
    if (result.attachments.length) result.kind = 'attachment';
    else if (result.collections.length) result.kind = 'collection';
    else if ((item.textures || []).length || ([0,2,3,4,5,6,7,8,9,14,18].includes(item.slot) && item.geosetGroup)) result.kind = 'bodyTexture';
    return result;
  });
}

function weaponPresentation(plans) {
  const modes = ['melee','ranged'].filter(mode => plans.some(plan => plan.attachments.some(entry => entry.weaponMode===mode)));
  return {weaponModes:modes,defaultWeaponMode:modes[0] || null,
    animationByWeaponMode:Object.fromEntries(modes.map(mode => [mode,`Stand - ${mode}`]))};
}

function handGrip(plans,mode) {
  const attachments = plans.flatMap(plan => plan.attachments).filter(entry => entry.weaponMode===mode && mode);
  return {right:attachments.some(entry => entry.attachmentId===1),left:attachments.some(entry => entry.attachmentId===2)};
}

function useGrip(boneId,grip) {
  return (grip.right && boneId>=8 && boneId<=12) || (grip.left && boneId>=13 && boneId<=17);
}

function animationTracks(bone,track,idleIndex,gripIndex,grips) {
  const source = bone[track];
  const data = grips.map(grip => {
    const closed = useGrip(bone.boneID,grip);
    const selected = closed ? gripIndex : idleIndex;
    const values = source.values[selected] || [];
    return {timestamps:closed && values.length ? [0] : source.timestamps[selected] || [],values:closed ? values.slice(0,1) : values};
  });
  return {timestamps:data.map(entry => entry.timestamps),values:data.map(entry => entry.values)};
}

function hideHelmetGeosets(mask,groups) {
  const hidden = new Set(groups);
  for (const mesh of mask) if (mesh.id%100!==0 && hidden.has(Math.floor(mesh.id/100))) mesh.checked = false;
  return mask;
}

function attachmentBinding(model,attachmentId,boneName) {
  const attachment = model.attachments.find(entry => entry.id===attachmentId);
  if (!attachment || !model.bones[attachment.bone]) throw new Error(`Missing attachment ${attachmentId}`);
  const bone = model.bones[attachment.bone];
  const [x,y,z] = attachment.position;
  const offset = [x-bone.pivot[0],z-bone.pivot[1],-y-bone.pivot[2]];
  if (!offset.every(Number.isFinite)) throw new Error(`Invalid attachment ${attachmentId}`);
  return {attachmentId,offset,bone:boneName(bone,attachment.bone)};
}

function collectionMask(item,subMeshes) {
  const groups = collectionGroups[item.slot];
  if (!groups) throw renderError(item,'unknown collection geosets');
  const selections = new Set(groups.flatMap((group,index) => {
    const value = item.models?.attachmentGeosetGroup?.[index];
    return Number.isInteger(value) && value>=0 && value<99 ? [group*100+1+value] : [];
  }));
  const mask = subMeshes.map(mesh => ({id:mesh.submeshID,checked:selections.has(mesh.submeshID)}));
  // As in the native collection renderer, an unmatched mesh stays hidden.
  // Classic body armor can retain unused shoulder/weapon model references
  // containing only mesh 0; those are not extra geometry to attach to a bone.
  return mask;
}

function visibleVertices(skin,mask) {
  const vertices = new Set();
  for (let i=0;i<skin.subMeshes.length;i++) {
    if (!mask[i]?.checked) continue;
    const mesh = skin.subMeshes[i];
    for (let index=mesh.triangleStart;index<mesh.triangleStart+mesh.triangleCount;index++) vertices.add(skin.indices[skin.triangles[index]]);
  }
  return vertices;
}

function remapCollectionBones(item,collection,character,vertices) {
  const samePivot = (a,b) => a?.pivot?.length===3 && b?.pivot?.length===3 && a.pivot.every((value,index) => Math.abs(value-b.pivot[index])<0.0001);
  const remap = new Map();
  const indices = new Uint8Array(collection.boneIndices.length);
  for (const vertex of vertices) {
    let total = 0;
    for (let influence=0;influence<4;influence++) {
      const index = vertex*4+influence;
      const weight = collection.boneWeights[index];
      if (!Number.isInteger(weight) || weight<0 || weight>255) throw renderError(item,'invalid collection weights');
      total += weight;
      if (!weight) continue;
      const source = collection.boneIndices[index];
      if (!remap.has(source)) {
        const bone = collection.bones[source];
        let matches = character.bones.map((other,index) => ({other,index})).filter(entry => samePivot(bone,entry.other));
        const named = matches.filter(entry => entry.other.boneNameCRC===bone?.boneNameCRC);
        if (named.length) matches = named;
        const keyed = matches.filter(entry => bone?.boneID>=0 && entry.other.boneID===bone.boneID);
        if (keyed.length) matches = keyed;
        const match = matches.length===1 ? matches[0] : matches.find(entry => entry.index===source);
        if (!match || match.index>255) throw renderError(item,`collection bone ${source} has no verified character binding`);
        remap.set(source,match.index);
      }
      indices[index] = remap.get(source);
    }
    if (!total) throw renderError(item,`collection vertex ${vertex} is not bound to the character`);
  }
  return {indices,remap:[...remap].map(([source,target]) => ({source,target}))};
}

function collectionTextureUri(m2,skin,meshIndex,variants,directTypes) {
  const unit = skin.textureUnits.find(unit => unit.skinSectionIndex===meshIndex);
  const textureIndex = unit && m2.textureCombos[unit.textureComboIndex];
  const type = m2.textureTypes[textureIndex];
  if (directTypes.has(type)) return `data-${type}.png`;
  let id;
  if (type>=11 && type<14) id = variants[type-11];
  else if (type>1 && type<5) id = variants[type-2];
  else if (type===0) id = m2.textures[textureIndex]?.fileDataID;
  if (!Number.isSafeInteger(id) || id<1) throw new Error(`Unresolved collection texture ${type}`);
  return `${id}.png`;
}

function nativeMeshMaterial(model,skin,meshIndex) {
  const unit = skin.textureUnits.find(entry => entry.skinSectionIndex===meshIndex);
  const material = unit && model.materials[unit.materialIndex];
  if (!material || !Number.isInteger(material.blendingMode) || material.blendingMode<0 || material.blendingMode>7
    || !Number.isInteger(material.flags)) throw new Error(`Invalid native material for mesh ${meshIndex}`);
  return material;
}

function applyNativeMaterials(gltf,assignments) {
  const variants = new Map();
  for (const {meshIndex,material:{blendingMode,flags}} of assignments) {
    const mesh = gltf.meshes[meshIndex];
    if (!mesh?.primitives?.length) throw new Error(`Missing exported mesh ${meshIndex}`);
    for (const primitive of mesh.primitives) {
      const sourceIndex = primitive.material;
      const source = gltf.materials[sourceIndex];
      if (!source) throw new Error(`Missing exported material for mesh ${meshIndex}`);
      const key = `${sourceIndex}:${blendingMode}:${flags}`;
      if (!variants.has(key)) {
        const material = structuredClone(source);
        material.alphaMode = blendingMode===0 ? 'OPAQUE' : blendingMode===1 ? 'MASK' : 'BLEND';
        delete material.alphaCutoff;
        if (blendingMode===1) material.alphaCutoff = 128/255;
        material.doubleSided = Boolean(flags & 4);
        material.extras = {...material.extras,m2BlendMode:blendingMode,m2Flags:flags};
        variants.set(key,gltf.materials.length);
        gltf.materials.push(material);
      }
      primitive.material = variants.get(key);
    }
  }
  return gltf;
}

module.exports = {selectAppearance,planEquipment,weaponPresentation,handGrip,useGrip,animationTracks,hideHelmetGeosets,attachmentBinding,
  collectionMask,visibleVertices,remapCollectionBones,collectionTextureUri,nativeMeshMaterial,applyNativeMaterials,renderError};
