const fs = require('node:fs/promises');
const path = require('node:path');
const { openClient } = require('./local-client.cjs');
const {renderError,selectAppearance} = require('./equipment-rendering.cjs');

async function main() {
  const { client, vendor, output } = await openClient(process.argv[2]);
  const snapshot = JSON.parse(await fs.readFile(path.join(output, 'flowmage.json'), 'utf8'));
  const c = snapshot.character;
  const db2 = require(path.join(vendor, 'casc/db2.js'));
  await db2.preload.ChrCustomizationMaterial();
  const customization = require(path.join(vendor, 'db/caches/DBCharacterCustomization.js'));
  await customization.ensureInitialized();
  const modelId = customization.get_chr_model_id(c.race, c.gender);
  if (!modelId) throw new Error('Character model mapping missing');
  const fileId = customization.get_model_file_data_id(modelId);
  const layoutId = customization.get_texture_layout_id(modelId);
  const options = Array.from((await db2.ChrCustomizationOption.getAllRows()).values()).filter(row => row.ChrModelID === modelId);
  const choices = Array.from((await db2.ChrCustomizationChoice.getAllRows()).values());
  const selected = selectAppearance(options,choices,c);
  const M2Loader = require(path.join(vendor, '3D/loaders/M2Loader.js'));
  const model = new M2Loader(await client.getFile(fileId, false, true, false));
  await model.load();
  const skin = await model.getSkin(0);
  const itemModels = require(path.join(vendor, 'db/caches/DBItemModels.js'));
  const itemTextures = require(path.join(vendor, 'db/caches/DBItemCharTextures.js'));
  await itemModels.ensureInitialized();
  await itemTextures.ensureInitialized();
  const itemRows = await db2.Item.getAllRows();
  const sparseItems = await db2.ItemSparse.getAllRows();
  const displays = await db2.ItemDisplayInfo.getAllRows();
  const componentModels = require(path.join(vendor,'db/caches/DBComponentModelFileData.js'));
  const resolvedEquipment = snapshot.equipment.map(item => {
    const display = displays.get(item.displayId);
    if (!display) throw renderError(item,'missing client display');
    const models = itemModels.getDisplayData(item.displayId,c.race,c.gender);
    if (display.ModelResourcesID.some(id => id>0) && !models?.models?.length) throw renderError(item,'missing local model variant');
    for (const id of models?.models || []) {
      const component = componentModels.getInfo(id);
      if (component?.raceID>0 && (component.raceID!==c.race
        || (component.genderIndex<2 && component.genderIndex!==c.gender))) throw renderError(item,'model variant does not match character race and gender');
    }
    const base = itemRows.get(item.itemId);
    return {...item,inventoryType:item.inventoryType ?? sparseItems.get(item.itemId)?.InventoryType,
      itemClassId:base?.ClassID,itemSubclassId:base?.SubclassID,models,geosetGroup:display.GeosetGroup,
      textures:itemTextures.getTexturesByDisplayId(item.displayId,c.race,c.gender)};
  });
  const report = {
    snapshot, modelId, fileId, layoutId, selected,
    vertices: model.vertices.length / 3, bones: model.bones.length,
    animations: model.animations.length, submeshes: skin.subMeshes.map(mesh => mesh.submeshID),
    equipment: resolvedEquipment,
    textures: selected.map(choice => ({ ...choice, geoset: customization.get_choice_geoset_id(choice.choiceID),
      materials: (customization.get_choice_materials(choice.choiceID) ?? []).map(entry => ({
        ...entry, material: customization.get_chr_cust_material(entry.ChrCustomizationMaterialID)
      }))
    }))
  };
  await fs.writeFile(path.join(output, 'prepared.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ fileId, modelId, layoutId, selected, vertices: report.vertices, bones: report.bones, equipment: resolvedEquipment }, null, 2));
}

main().catch(error => { console.error(error); process.exitCode = 1; });
