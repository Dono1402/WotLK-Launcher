const {test} = require('node:test');
const assert = require('node:assert/strict');
const {
  selectAppearance,planEquipment,weaponPresentation,handGrip,useGrip,animationTracks,hideHelmetGeosets,
  attachmentBinding,collectionMask,visibleVertices,remapCollectionBones,collectionTextureUri,nativeMeshMaterial,applyNativeMaterials
} = require('../equipment-rendering.cjs');

function item(slot,extra = {}) {
  return {itemId:1000+slot,slot,models:{models:[101],textures:[501]},...extra};
}

function bone(pivot,boneNameCRC,boneID = -1) {
  return {pivot,boneNameCRC,boneID};
}

test('legacy appearance bytes resolve all race and gender aliases without choosing another appearance',() => {
  const character = {skin:0,face:1,hairStyle:2,hairColor:3,facialStyle:4};
  const variants = [5,10,11,12,13,14,18,19,21,23].map(facial => [1,2,3,4,facial]);
  variants.push([165,177,16,17,5],[165,177,16,17,15]);
  for (const types of variants) {
    const options = types.map((type,index) => ({ID:index+10,ChrCustomizationID:type,Name_lang:`Option ${index}`}));
    const choices = options.flatMap((option,index) => [
      {ID:index+100,ChrCustomizationOptionID:option.ID,OrderIndex:index},
      {ID:index+200,ChrCustomizationOptionID:option.ID,OrderIndex:99}
    ]);
    assert.deepEqual(selectAppearance(options,choices,character).map(choice => [choice.choiceID,choice.value]),
      [[100,0],[101,1],[102,2],[103,3],[104,4]],`appearance types ${types}`);
    assert.throws(() => selectAppearance(options,choices,{...character,skin:255}),/No exact choice/);
    assert.throws(() => selectAppearance(options.slice(1),choices,character),/Expected five appearance choices/);
    assert.throws(() => selectAppearance([...options,options[0]],choices,character),/Ambiguous appearance option/);
  }
});

test('rigid equipment follows head, shoulder and the correct left-hand attachment points',() => {
  const cases = [
    [item(0),[11]],
    [item(2,{models:{models:[101,102]}}),[6,5]],
    [item(14),[12]],
    [item(15,{inventoryType:17}),[1]],
    [item(16,{inventoryType:14}),[0]],
    [item(16,{inventoryType:13}),[2]],
    [item(16,{inventoryType:22}),[2]],
    [item(16,{inventoryType:23}),[2]]
  ];
  for (const [equipment,expected] of cases) {
    const [plan] = planEquipment([equipment]);
    assert.deepEqual(plan.attachments.map(entry => entry.attachmentId),expected);
    assert.equal(plan.kind,'attachment');
    assert.equal(plan.collections.length,0);
    assert.deepEqual(plan.attachments.map(entry => entry.modelFileId),equipment.models.models);
  }
});

test('bow and gun presentation has separate ranged and melee hand grips',() => {
  for (const [subclass,attachment,grip] of [[2,2,{right:false,left:true}],[3,1,{right:true,left:false}]]) {
    const plans = planEquipment([
      item(15,{inventoryType:21}),item(16,{inventoryType:14}),
      item(17,{inventoryType:subclass===2 ? 15 : 26,itemClassId:2,itemSubclassId:subclass})
    ]);
    assert.deepEqual(weaponPresentation(plans),{
      weaponModes:['melee','ranged'],defaultWeaponMode:'melee',
      animationByWeaponMode:{melee:'Stand - melee',ranged:'Stand - ranged'}
    });
    assert.equal(plans[2].attachments[0].attachmentId,attachment);
    assert.equal(plans[2].attachments[0].weaponMode,'ranged');
    assert.deepEqual(handGrip(plans,'melee'),{right:true,left:false});
    assert.deepEqual(handGrip(plans,'ranged'),grip);
    assert.deepEqual(handGrip(plans,null),{right:false,left:false});
    assert.equal(plans.flatMap(plan => plan.attachments).filter(entry => entry.weaponMode==='ranged').length,1);
  }
  const [mainhandBow] = planEquipment([item(15,{inventoryType:15,itemClassId:2,itemSubclassId:2})]);
  assert.equal(mainhandBow.attachments[0].attachmentId,2);
  assert.deepEqual(weaponPresentation([]),{weaponModes:[],defaultWeaponMode:null,animationByWeaponMode:{}});
});

test('nonvisual items have no model attachments and unverified weapon model extras are rejected',() => {
  for (const equipment of [item(1),item(10),item(11),item(12),item(13),item(17,{inventoryType:28})]) {
    const [plan] = planEquipment([equipment]);
    assert.deepEqual(plan.attachments,[]);
    assert.deepEqual(plan.collections,[]);
    assert.equal(plan.kind,'nonVisual');
  }
  assert.throws(() => planEquipment([item(16,{models:{models:[101,102]}})]),{
    code:'ARMORY_EQUIPMENT_UNSUPPORTED',itemId:1016,slot:16
  });
  assert.throws(() => planEquipment([item(0,{models:{models:[0]}})]),/invalid model data/);
});

test('body collections remain on the character skeleton instead of using hand attachments',() => {
  for (const slot of [4,5,6,7,9]) {
    const [plan] = planEquipment([item(slot)]);
    assert.equal(plan.kind,'collection');
    assert.deepEqual(plan.attachments,[]);
    assert.deepEqual(plan.collections,[{modelIndex:0,modelFileId:101}]);
  }
  const [helmet] = planEquipment([item(0,{models:{models:[101,102]}})]);
  assert.deepEqual(helmet.attachments,[{modelIndex:0,modelFileId:101,attachmentId:11}]);
  assert.deepEqual(helmet.collections,[{modelIndex:1,modelFileId:102}]);
});

test('grip changes only the finger bones belonging to the occupied hand',() => {
  for (let id=-1;id<=20;id++) {
    assert.equal(useGrip(id,{right:true,left:false}),id>=8 && id<=12,`right finger ${id}`);
    assert.equal(useGrip(id,{right:false,left:true}),id>=13 && id<=17,`left finger ${id}`);
    assert.equal(useGrip(id,{right:false,left:false}),false);
  }
  assert.deepEqual(handGrip(planEquipment([item(16,{inventoryType:14})]),'melee'),{right:false,left:false});
  assert.deepEqual(handGrip(planEquipment([item(16,{inventoryType:23})]),'melee'),{right:false,left:true});
});

test('closed fingers use the first grip frame while the other mode keeps its idle animation',() => {
  const source = {boneID:13,rotation:{timestamps:[[0,1000],[50,100]],values:[[[0,0,0,1],[0,0,1,0]],[[1,0,0,0],[0,1,0,0]]]}};
  const before = structuredClone(source);
  const tracks = animationTracks(source,'rotation',0,1,[{right:true,left:false},{right:false,left:true}]);
  assert.deepEqual(tracks.timestamps,[[0,1000],[0]]);
  assert.deepEqual(tracks.values,[source.rotation.values[0],[source.rotation.values[1][0]]]);
  assert.deepEqual(source,before,'preparing two modes must not replace the source animation');
});

test('helmet visibility hides complete hair and ear groups while preserving body and group boundaries',() => {
  const ids = [0,1,99,100,101,699,700,701,799,800,801];
  const mask = ids.map(id => ({id,checked:true}));
  hideHelmetGeosets(mask,[0,7,7]);
  assert.deepEqual(mask.filter(mesh => mesh.checked).map(mesh => mesh.id),[0,100,101,699,700,800,801]);
  mask.find(mesh => mesh.id===801).checked = false;
  hideHelmetGeosets(mask,[]);
  assert.equal(mask.find(mesh => mesh.id===801).checked,false,'helmet changes must not enable other meshes');
});

test('attachment binding converts native coordinates exactly once and subtracts the glTF joint pivot',() => {
  const model = {
    attachments:[{id:11,bone:1,position:[10,20,30]}],
    bones:[bone([0,0,0],1),bone([2,3,4],42,6)]
  };
  const seen = [];
  const binding = attachmentBinding(model,11,(value,index) => { seen.push([value,index]); return 'Head'; });
  assert.deepEqual(binding,{attachmentId:11,offset:[8,27,-24],bone:'Head'});
  assert.deepEqual(binding.offset.map((value,index) => value+model.bones[1].pivot[index]),[10,30,-20]);
  assert.deepEqual(seen,[[model.bones[1],1]]);
  assert.throws(() => attachmentBinding(model,0,() => ''),/Missing attachment 0/);
  const invalid = structuredClone(model);
  invalid.attachments[0].position[1] = NaN;
  assert.throws(() => attachmentBinding(invalid,11,() => ''),/Invalid attachment 11/);
});

test('collection masks select the equipment variant in each body region and exclude unused geometry',() => {
  const cases = [[0,[27,21]],[4,[8,10,13,22,28]],[5,[18]],[6,[11,9,13]],[7,[5,20]],[9,[4,23]],[14,[15]]];
  for (const [slot,groups] of cases) {
    const choices = groups.map((_,index) => index);
    const wanted = groups.map((group,index) => group*100+1+choices[index]);
    const ids = [0,...groups.flatMap((group,index) => [group*100,group*100+1+choices[index],group*100+99]),9999];
    const mask = collectionMask(item(slot,{models:{attachmentGeosetGroup:choices}}),ids.map(submeshID => ({submeshID})));
    assert.deepEqual(mask.filter(mesh => mesh.checked).map(mesh => mesh.id),wanted,`slot ${slot}`);
  }
});

test('unselected collection geometry stays hidden, including dormant mesh-zero references in classic armor',() => {
  for (const choices of [undefined,[],[-1],[99],[1.5]]) {
    assert.deepEqual(collectionMask(item(5,{models:{attachmentGeosetGroup:choices}}),[{submeshID:1801}]),[{id:1801,checked:false}]);
  }
  assert.deepEqual(collectionMask(item(5,{models:{attachmentGeosetGroup:[0]}}),[{submeshID:1802}]),[{id:1802,checked:false}]);
  for (const slot of [4,5,6,7,9]) assert.deepEqual(collectionMask(item(slot,{models:{attachmentGeosetGroup:[0,0,0,0,0]}}),[{submeshID:0}]),[{id:0,checked:false}]);
  assert.throws(() => collectionMask(item(16),[{submeshID:0}]),/unknown collection geosets/);
});

test('visible vertices follow both skin index tables and ignore hidden triangles',() => {
  const skin = {
    subMeshes:[{triangleStart:0,triangleCount:3},{triangleStart:3,triangleCount:3},{triangleStart:6,triangleCount:3}],
    triangles:[0,2,1,3,4,5,2,1,0],indices:[4,2,8,6,0,9]
  };
  assert.deepEqual([...visibleVertices(skin,[{checked:true},{checked:false},{checked:true}])],[4,8,2]);
  assert.deepEqual([...visibleVertices(skin,[])],[]);
});

test('weighted visible vertices remap through pivot and CRC without binding invisible or zero-weight bones',() => {
  const collection = {
    bones:[bone([1,0,0],20,13),bone([2,0,0],30,3)],
    boneIndices:[0,1,99,99,99,99,99,99],boneWeights:[128,127,0,0,255,0,0,0]
  };
  const character = {bones:[bone([1,0,0],10,8),bone([1.00005,0,0],20,13),bone([2,0,0],30,3)]};
  const before = structuredClone(collection);
  const result = remapCollectionBones(item(5),collection,character,new Set([0]));
  assert.deepEqual([...result.indices],[1,2,0,0,0,0,0,0]);
  assert.deepEqual(result.remap,[{source:0,target:1},{source:1,target:2}]);
  assert.deepEqual(collection,before);
});

test('collection bone keys distinguish equal-pivot bones and permit an unambiguous pivot fallback',() => {
  const collection = {bones:[bone([1,2,3],42,13)],boneIndices:[0,0,0,0],boneWeights:[255,0,0,0]};
  const character = {bones:[bone([1,2,3],42,8),bone([1,2,3],42,13)]};
  assert.deepEqual(remapCollectionBones(item(7),collection,character,new Set([0])).remap,[{source:0,target:1}]);
  const fallback = {bones:[bone([0,0,0],9),bone([1,2,3],99)]};
  assert.deepEqual(remapCollectionBones(item(7),collection,fallback,new Set([0])).remap,[{source:0,target:1}]);
});

test('unmapped visible bones, invalid weights and unbound vertices cannot produce a collection skin',() => {
  const collection = {bones:[bone([1,2,3],42)],boneIndices:[0,0,0,0],boneWeights:[255,0,0,0]};
  const character = {bones:[bone([1.0002,2,3],42)]};
  assert.throws(() => remapCollectionBones(item(5),collection,character,new Set([0])),/no verified character binding/);
  const matching = {bones:[bone([1,2,3],42)]};
  for (const weight of [-1,256,0.5,NaN]) {
    assert.throws(() => remapCollectionBones(item(5),{...collection,boneWeights:[weight,0,0,0]},matching,new Set([0])),/invalid collection weights/);
  }
  assert.throws(() => remapCollectionBones(item(5),{...collection,boneWeights:[0,0,0,0]},matching,new Set([0])),/not bound to the character/);
  const absent = {...collection,boneIndices:[7,0,0,0]};
  assert.throws(() => remapCollectionBones(item(5),absent,matching,new Set([0])),/no verified character binding/);
});

test('collection textures use the mesh material type rather than its model or mesh index',() => {
  const m2 = {
    textureCombos:[3,2,0,1],textureTypes:[11,12,2,0],
    textures:[{},{},{},{fileDataID:777}]
  };
  const skin = {textureUnits:[
    {skinSectionIndex:0,textureComboIndex:3},
    {skinSectionIndex:1,textureComboIndex:2},
    {skinSectionIndex:2,textureComboIndex:1},
    {skinSectionIndex:3,textureComboIndex:0}
  ]};
  const variants = [501,502,503];
  assert.equal(collectionTextureUri(m2,skin,0,variants,new Set()),'502.png');
  assert.equal(collectionTextureUri(m2,skin,1,variants,new Set()),'501.png');
  assert.equal(collectionTextureUri(m2,skin,2,variants,new Set()),'501.png');
  assert.equal(collectionTextureUri(m2,skin,3,variants,new Set()),'777.png');
  assert.equal(collectionTextureUri(m2,skin,0,variants,new Set([12])),'data-12.png');
  assert.throws(() => collectionTextureUri(m2,skin,0,[501],new Set()),/Unresolved collection texture 12/);
  assert.throws(() => collectionTextureUri(m2,skin,8,variants,new Set()),/Unresolved collection texture/);
});

test('native material selection follows the exported mesh texture unit, not the texture index',() => {
  const model = {materials:[{blendingMode:1,flags:4},{blendingMode:0,flags:0},{blendingMode:4,flags:17}]};
  const skin = {textureUnits:[{skinSectionIndex:1,materialIndex:2},{skinSectionIndex:0,materialIndex:1},{skinSectionIndex:0,materialIndex:0}]};
  assert.deepEqual(nativeMeshMaterial(model,skin,0),{blendingMode:0,flags:0});
  assert.deepEqual(nativeMeshMaterial(model,skin,1),{blendingMode:4,flags:17});
  assert.throws(() => nativeMeshMaterial(model,skin,2),/Invalid native material/);
});

test('opaque reflection alpha is preserved while hair cutouts and blending get separate native materials',() => {
  const original = {name:'117168',pbrMetallicRoughness:{baseColorTexture:{index:0}},alphaCutoff:.45,extras:{kept:true}};
  const gltf = {materials:[structuredClone(original)],meshes:Array.from({length:5},() => ({primitives:[{material:0}]}))};
  applyNativeMaterials(gltf,[
    {meshIndex:0,material:{blendingMode:0,flags:0}},
    {meshIndex:1,material:{blendingMode:1,flags:4}},
    {meshIndex:2,material:{blendingMode:4,flags:17}},
    {meshIndex:3,material:{blendingMode:0,flags:0}},
    {meshIndex:4,material:{blendingMode:0,flags:4}}
  ]);
  const applied = gltf.meshes.map(mesh => gltf.materials[mesh.primitives[0].material]);
  assert.deepEqual(gltf.materials[0],original,'splitting native materials must not mutate the shared source');
  assert.equal(gltf.materials.length,5);
  assert.equal(applied[0],applied[3],'equal native settings can share a material');
  assert.equal(applied[0].alphaMode,'OPAQUE');
  assert.equal(applied[0].alphaCutoff,undefined);
  assert.equal(applied[1].alphaMode,'MASK');
  assert.equal(applied[1].alphaCutoff,128/255);
  assert.equal(applied[1].doubleSided,true);
  assert.equal(applied[2].alphaMode,'BLEND');
  assert.equal(applied[2].alphaCutoff,undefined);
  assert.deepEqual(applied[2].extras,{kept:true,m2BlendMode:4,m2Flags:17});
  assert.equal(applied[4].doubleSided,true);
  for (const material of applied) assert.equal(material.name,'117168','preserve native texture names');
});
