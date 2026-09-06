const fs = require('node:fs/promises');
const path = require('node:path');
const { openClient } = require('./local-client.cjs');
const { composeTextureLayers } = require('./texture-compositor.cjs');
const {planEquipment,weaponPresentation,handGrip,animationTracks,hideHelmetGeosets,attachmentBinding,
  collectionMask,visibleVertices,remapCollectionBones,collectionTextureUri,nativeMeshMaterial,applyNativeMaterials,renderError} = require('./equipment-rendering.cjs');

async function main() {
  const { client, core, vendor, output } = await openClient(process.argv[2]);
  const prepared = JSON.parse(await fs.readFile(path.join(output, 'prepared.json'), 'utf8'));
  const character = prepared.snapshot.character;
  const plans = planEquipment(prepared.equipment);
  const presentation = weaponPresentation(plans);
  const assets = path.join(output, 'assets');
  await fs.mkdir(assets, { recursive: true });
  Object.assign(core.view.config, {
    overwriteFiles: true, modelsExportTextures: true, modelsExportAlpha: true,
    modelsExportAnimations: true, modelsExportWithBonePrefix: true,
    modelsExportUV2: true, enableSharedTextures: false, pathFormat: 'posix',
    exportDirectory: assets
  });
  const db2 = require(path.join(vendor, 'casc/db2.js'));
  const BLP = require(path.join(vendor, 'casc/blp.js'));
  const PNGWriter = require(path.join(vendor, 'png-writer.js'));
  const M2Exporter = require(path.join(vendor, '3D/exporters/M2Exporter.js'));
  const BoneMapper = require(path.join(vendor, '3D/BoneMapper.js'));
  const slots = require(path.join(vendor, 'wow/EquipmentSlots.js'));
  const all = async name => Array.from((await db2[name].getAllRows()).values());
  const materials = (await all('ChrModelMaterial')).filter(r => r.CharComponentTextureLayoutsID === prepared.layoutId);
  const layers = (await all('ChrModelTextureLayer')).filter(r => r.CharComponentTextureLayoutsID === prepared.layoutId);
  const sections = (await all('CharComponentTextureSections')).filter(r => r.CharComponentTextureLayoutID === prepared.layoutId);
  const displays = await db2.ItemDisplayInfo.getAllRows();
  const textureData = await all('TextureFileData');
  const pngCache = new Map();
  const rgbaCache = new Map();
  async function png(id) {
    if (!pngCache.has(id)) {
      const blp = new BLP(await client.getFile(id, false, true, false));
      const buffer = await blp.toPNG(15);
      pngCache.set(id, 'data:image/png;base64,' + buffer.raw.toString('base64'));
    }
    return pngCache.get(id);
  }
  async function rgba(id) {
    if (!rgbaCache.has(id)) {
      const blp = new BLP(await client.getFile(id, false, true, false));
      // This typed-array decoder also backs toPNG and supports palette BLPs.
      rgbaCache.set(id, {width: blp.width, height: blp.height, data: blp.toUInt8Array(0, 15)});
    }
    return rgbaCache.get(id);
  }
  const composite = [];
  const direct = new Map();
  async function addTexture(id, order, layer, section) {
    if (!layer || !section) throw new Error(`Missing texture layout for ${id}`);
    const material = materials.find(r => r.TextureType === layer.TextureType);
    if (!material) throw new Error(`Missing material ${layer.TextureType}`);
    if (![1, 8].includes(layer.TextureType)) {
      direct.set(layer.TextureType, await png(id));
      return;
    }
    if (![0, 1, 9, 15].includes(layer.BlendMode)) throw new Error(`Unsupported compositing mode ${layer.BlendMode}`);
    composite.push({ id, order, type: layer.TextureType, mode: layer.BlendMode, material, section, image: await rgba(id) });
  }
  for (const choice of prepared.textures) {
    for (const entry of choice.materials) {
      if (entry.RelatedChrCustomizationChoiceID && !prepared.selected.some(c => c.choiceID === entry.RelatedChrCustomizationChoiceID)) continue;
      const { FileDataID: id, ChrModelTextureTargetID: target } = entry.material;
      const layer = layers.find(r => r.ChrModelTextureTargetID[0] === target);
      if (!layer) throw new Error(`Missing appearance target ${target}`);
      const material = materials.find(r => r.TextureType === layer.TextureType);
      const section = layer.TextureSectionTypeBitMask === -1
        ? { X: 0, Y: 0, Width: material.Width, Height: material.Height }
        : sections.find(r => (1 << r.SectionType) & layer.TextureSectionTypeBitMask);
      await addTexture(id, target, layer, section);
    }
  }
  for (const item of prepared.equipment) {
    for (const texture of item.textures ?? []) {
      const section = sections.find(r => r.SectionType === texture.section);
      const layer = layers.find(r => r.TextureSectionTypeBitMask !== -1 && ((1 << texture.section) & r.TextureSectionTypeBitMask))
        ?? layers.find(r => r.TextureSectionTypeBitMask === -1 && r.TextureType === 1);
      await addTexture(texture.fileDataID, slots.get_slot_layer(item.slot + 1) * 100 + texture.section,
        { ...layer, BlendMode: [0, 1].includes(layer.BlendMode) ? 15 : layer.BlendMode }, section);
    }
  }
  const cloak = prepared.equipment.find(item => item.slot === 14);
  if (cloak) {
    const resource = displays.get(cloak.displayId).ModelMaterialResourcesID[0];
    const texture = textureData.find(r => r.MaterialResourcesID === resource);
    if (!texture) throw new Error('Cloak texture missing');
    direct.set(2, await png(texture.FileDataID));
  }
  console.log('Compositing', composite.length, 'real texture layers');
  for (const [type, bitmap] of composeTextureLayers(composite)) {
    const writer = new PNGWriter(bitmap.width, bitmap.height);
    writer.getPixelData().set(bitmap.data);
    direct.set(type, 'data:image/png;base64,' + writer.getBuffer().raw.toString('base64'));
  }

  const body = new M2Exporter(await client.getFile(prepared.fileId, false, true, false), [], prepared.fileId);
  await body.m2.load();
  const model = body.m2;
  console.log('Model skeleton:', model.skeletonFileID, 'animations:', model.animations.length);
  if (model.skeletonFileID) throw new Error('External skeleton not implemented by this isolated adapter');
  const idleIndex = model.animations.findIndex(a => a.id === 0 && a.variationIndex === 0);
  const gripIndex = model.animations.findIndex(a => a.id === 15 && a.variationIndex === 0);
  if (idleIndex < 0) throw new Error('Stand animation missing');
  if (gripIndex < 0) throw new Error('HandsClosed animation missing');
  const modes = presentation.weaponModes.length ? presentation.weaponModes : [null];
  const idle = model.animations[idleIndex];
  const grips = modes.map(mode => handGrip(plans,mode));
  // Export the real idle motion with the native HandsClosed frame only on occupied hands.
  model.loadAnims = async () => {
    await model.loadAnimsForIndex(idleIndex);
    await model.loadAnimsForIndex(gripIndex);
    for (const bone of model.bones) {
      for (const key of ['translation', 'rotation', 'scale']) {
        Object.assign(bone[key],animationTracks(bone,key,idleIndex,gripIndex,grips));
      }
    }
    model.animations = modes.map((mode,index) => ({...idle,variationIndex:index}));
  };
  const bodySkin = await model.getSkin(0);
  const mask = bodySkin.subMeshes.map(mesh => ({
    id: mesh.submeshID,
    checked: (mesh.submeshID === 0 || String(mesh.submeshID).endsWith('01') || String(mesh.submeshID).startsWith('32'))
      && !String(mesh.submeshID).startsWith('17') && !String(mesh.submeshID).startsWith('35')
  }));
  for (const choice of prepared.textures.filter(c => c.geoset !== undefined)) {
    const group = Math.floor(choice.geoset / 100);
    for (const mesh of mask) {
      if (mesh.id && Math.floor(mesh.id / 100) === group) mesh.checked = mesh.id === choice.geoset;
    }
  }
  const geosets = require(path.join(vendor, 'db/caches/DBItemGeosets.js'));
  await geosets.ensureInitialized();
  const equipped = new Map(prepared.equipment.map(item => [item.slot + 1, item.displayId]));
  const values = geosets.calculateEquipmentGeosetsByDisplay(equipped);
  for (const group of geosets.getAffectedCharGeosetsByDisplay(equipped)) {
    for (const mesh of mask) {
      if (mesh.id > group * 100 && mesh.id < group * 100 + 100) mesh.checked = mesh.id === group * 100 + values.get(group);
    }
  }
  const head = prepared.equipment.find(item => item.slot===0);
  const helmetHideGroups = head ? geosets.getHelmetHideGeosetsByDisplayId(head.displayId,character.race,character.gender) : [];
  hideHelmetGeosets(mask,helmetHideGroups);
  body.setGeosetMask(mask);
  for (const [type, uri] of direct) await body.addURITexture(type, uri);
  const helper = { isCancelled: () => false };
  const collections = [];
  const collectionMetadata = [];
  const hiddenCollectionReferences = [];
  for (let itemIndex=0;itemIndex<prepared.equipment.length;itemIndex++) {
    const item = prepared.equipment[itemIndex];
    for (const entry of plans[itemIndex].collections) {
      const exporter = new M2Exporter(await client.getFile(entry.modelFileId,false,true,false),item.models.textures,entry.modelFileId);
      const collection = exporter.m2;
      await collection.load();
      const skin = await collection.getSkin(0);
      const selected = collectionMask(item,skin.subMeshes);
      if (!selected.some(mesh => mesh.checked)) {
        hiddenCollectionReferences.push({slot:item.slot,itemId:item.itemId,modelFileId:entry.modelFileId});
        continue;
      }
      if (collection.skeletonFileID) {
        const SKELLoader = require(path.join(vendor,'3D/loaders/SKELLoader.js'));
        let skeleton = new SKELLoader(await client.getFile(collection.skeletonFileID,false,true,false));
        await skeleton.load();
        if (skeleton.parent_skel_file_id) {
          skeleton = new SKELLoader(await client.getFile(skeleton.parent_skel_file_id,false,true,false));
          await skeleton.load();
        }
        collection.bones = skeleton.bones;
      }
      const vertices = visibleVertices(skin,selected);
      const binding = remapCollectionBones(item,collection,model,vertices);
      const meshes = selected.flatMap((mesh,index) => mesh.checked ? [{geoset:mesh.id,
        textureUri:collectionTextureUri(collection,skin,index,item.models.textures,direct),
        nativeMaterial:nativeMeshMaterial(collection,skin,index)}] : []);
      const embeddedTextures = collection.textures.filter((texture,index) => collection.textureTypes[index]===0 && texture.fileDataID>0).map(texture => texture.fileDataID);
      collections.push({slot_id:item.slot+1,item_id:item.itemId,renderer:{m2:collection,draw_calls:selected.map(mesh => ({visible:mesh.checked}))},
        vertices:collection.vertices,normals:collection.normals,uv:collection.uv,uv2:collection.uv2,
        boneIndices:binding.indices,boneWeights:collection.boneWeights,textures:[...item.models.textures,...embeddedTextures],is_collection_style:true});
      collectionMetadata.push({slot:item.slot,itemId:item.itemId,modelFileId:entry.modelFileId,meshes,boneRemap:binding.remap});
    }
  }
  body.setEquipmentModelsGLTF(collections);
  await body.exportAsGLTF(path.join(assets, 'flowmage.gltf'), helper);
  const gltfFile = path.join(assets,'flowmage.gltf');
  const exported = JSON.parse(await fs.readFile(gltfFile,'utf8'));
  const materialAssignments = [];
  for (let index=0;index<mask.length;index++) {
    if (mask[index].checked) materialAssignments.push({meshIndex:materialAssignments.length,material:nativeMeshMaterial(model,bodySkin,index)});
  }
  for (let index=0;index<modes.length;index++) {
    if (!exported.animations?.[index]?.channels?.length) throw new Error('Missing exported idle mode');
    exported.animations[index].name = modes[index] ? presentation.animationByWeaponMode[modes[index]] : 'Stand';
  }
  const consumedCollectionNodes = new Set();
  for (const collection of collectionMetadata) {
    const prefix = `${slots.get_slot_name(collection.slot+1) || `Slot${collection.slot+1}`}_Item${collection.itemId}_`;
    const nodes = exported.nodes.filter(node => node.mesh!==undefined && node.name?.startsWith(prefix) && !consumedCollectionNodes.has(node)).slice(0,collection.meshes.length);
    if (nodes.length!==collection.meshes.length) throw renderError(collection,'incomplete exported collection');
    for (let index=0;index<nodes.length;index++) {
      const node = nodes[index];
      const mesh = collection.meshes[index];
      const material = exported.materials.findIndex(entry => {
        const texture = exported.textures[entry.pbrMetallicRoughness?.baseColorTexture?.index];
        return exported.images[texture?.source]?.uri===mesh.textureUri;
      });
      if (material<0) throw renderError(collection,`missing collection material ${mesh.textureUri}`);
      for (const primitive of exported.meshes[node.mesh].primitives) primitive.material = material;
      materialAssignments.push({meshIndex:node.mesh,material:mesh.nativeMaterial});
      node.extras = {equipmentSlot:collection.slot,itemId:collection.itemId,modelFileId:collection.modelFileId,geoset:mesh.geoset};
      consumedCollectionNodes.add(node);
    }
  }
  applyNativeMaterials(exported,materialAssignments);
  await fs.writeFile(gltfFile,JSON.stringify(exported,null,2));
  console.log('Character exported', mask.filter(m => m.checked).map(m => m.id));
  const attached = [];
  for (let itemIndex=0;itemIndex<prepared.equipment.length;itemIndex++) {
    const item = prepared.equipment[itemIndex];
    const modelData = item.models;
    for (const entry of plans[itemIndex].attachments) {
      const binding = attachmentBinding(model,entry.attachmentId,(bone,index) => BoneMapper.get_bone_name(bone.boneID,index,bone.boneNameCRC));
      const name = `item-${item.slot}-${entry.modelIndex}`;
      const exporter = new M2Exporter(await client.getFile(entry.modelFileId,false,true,false),modelData.textures,entry.modelFileId);
      core.view.config.modelsExportAnimations = false;
      const attachmentFile = path.join(assets,name+'.gltf');
      await exporter.exportAsGLTF(attachmentFile,helper);
      const attachmentGltf = JSON.parse(await fs.readFile(attachmentFile,'utf8'));
      const attachmentSkin = await exporter.m2.getSkin(0);
      applyNativeMaterials(attachmentGltf,attachmentSkin.subMeshes.map((_,meshIndex) => ({meshIndex,
        material:nativeMeshMaterial(exporter.m2,attachmentSkin,meshIndex)})));
      await fs.writeFile(attachmentFile,JSON.stringify(attachmentGltf,null,2));
      attached.push({url:name+'.gltf',slot:item.slot,itemId:item.itemId,modelFileId:entry.modelFileId,...binding,
        ...entry.weaponMode ? {weaponMode:entry.weaponMode} : {}});
    }
  }
  const itemRows = await db2.Item.getAllRows();
  const equipment = [];
  for (const { name, slot, itemId, displayId, quality, itemLevel } of prepared.equipment) {
    const iconId = itemRows.get(itemId)?.IconFileDataID;
    if (!iconId) throw new Error(`Missing item icon ${itemId}`);
    const icon = `icon-${itemId}.png`;
    await fs.writeFile(path.join(assets, icon), Buffer.from((await png(iconId)).split(',')[1], 'base64'));
    equipment.push({ name, slot, itemId, displayId, quality, itemLevel, icon });
  }
  const publicData = {
    renderSchemaVersion:3,
    name: prepared.snapshot.character.name, level: prepared.snapshot.character.level,
    raceId:character.race,classId:character.classId,realm:'Arthas',build:client.build.Version,
    source: 'Instantane serveur en lecture seule', capturedAt: prepared.snapshot.capturedAtUtc,
    appearance: prepared.selected, modelFileId: prepared.fileId, attached,
    equipment,
    visibleGeosets: mask.filter(m => m.checked).map(m => m.id),helmetHideGroups,...presentation,
    equipmentVisuals:plans.map((plan,index) => {
      const renderedCollections = collectionMetadata.filter(entry => entry.slot===plan.slot);
      return {slot:plan.slot,itemId:plan.itemId,
        kind:plan.kind==='collection' && !renderedCollections.length ? (prepared.equipment[index].textures?.length ? 'bodyTexture' : 'nonVisual') : plan.kind,
        attachments:plan.attachments.map(entry => ({modelFileId:entry.modelFileId,attachmentId:entry.attachmentId,...entry.weaponMode ? {weaponMode:entry.weaponMode} : {}})),
        collections:renderedCollections.map(({modelFileId,meshes,boneRemap}) => ({modelFileId,geosets:meshes.map(mesh => mesh.geoset),boneRemap})),
        hiddenModelReferences:hiddenCollectionReferences.filter(entry => entry.slot===plan.slot).map(entry => entry.modelFileId)};
    })
  };
  await fs.writeFile(path.join(assets, 'character.json'), JSON.stringify(publicData, null, 2));
  await fs.writeFile(path.join(output, 'texture-layers.json'), JSON.stringify(composite.map(({ image, ...entry }) => entry), null, 2));
  console.log('Ready:', assets);
}

main().catch(error => { console.error(error); process.exitCode = 1; });
