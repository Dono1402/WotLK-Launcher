const fs = require('node:fs/promises');
const path = require('node:path');
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const {createServer} = require('./server.cjs');

const shared = path.resolve(__dirname,'../../artifacts/armory-prototype');
const sourceRevision = 'c2fd7bde36a712be78a5da896c995b84fbfa2545';
const definitions = [
  {name:'current-helmet',description:'Current recorded equipment, including Hooded Cowl 3732'},
  {name:'no-helmet',description:'Same equipment with the helmet omitted locally',remove:[0]},
  {name:'dual-weapons',description:'Two Worn Daggers, one in each hand',hands:[[2092,15],[2092,16]],expected:{melee:[[15,1],[16,2]]}},
  {name:'shield',description:'Worn Dagger and Worn Wooden Shield',hands:[[2092,15],[2362,16]],expected:{melee:[[15,1],[16,0]]}},
  {name:'held',description:'Eerie Stable Lantern in the off hand',hands:[[6341,16]],expected:{melee:[[16,2]]}},
  {name:'bow',description:'Worn Shortbow in the ranged slot',hands:[[2504,17]],expected:{ranged:[[17,2]]}},
  {name:'gun',description:'Ornate Blunderbuss in the ranged slot',hands:[[2509,17]],expected:{ranged:[[17,1]]}},
  {name:'crossbow',description:'Light Crossbow in the ranged slot',hands:[[15807,17]],expected:{ranged:[[17,1]]}}
];

async function writeJson(filename,value) {
  await fs.writeFile(filename,JSON.stringify(value,null,2)+'\n');
}

async function fixtureWorker(options) {
  const {openClient} = require('./local-client.cjs');
  const {client,vendor} = await openClient(options.clientRoot,{locale:'en'});
  const snapshot = JSON.parse(await fs.readFile(options.snapshot,'utf8'));
  assert.equal(snapshot.character.name,'Flowmage');
  assert.equal(snapshot.character.race,10);
  assert.equal(snapshot.character.gender,1);
  assert.equal(snapshot.equipment.find(item => item.slot===0)?.itemId,3732,'Use the recorded snapshot with Hooded Cowl');
  const db2 = require(path.join(vendor,'casc/db2.js'));
  const models = require(path.join(vendor,'db/caches/DBItemModels.js'));
  const geosets = require(path.join(vendor,'db/caches/DBItemGeosets.js'));
  await models.ensureInitialized();
  await geosets.ensureInitialized();
  const items = await db2.Item.getAllRows(), sparse = await db2.ItemSparse.getAllRows();
  const loadItem = (id,slot) => {
    const item = items.get(id), detail = sparse.get(id), display = models.getItemDisplay(id,10,1);
    assert.ok(item && detail && display?.models?.length,`Missing installed client model for fixture item ${id}`);
    return {slot,itemId:id,name:detail.Display_lang,displayId:display.ID,quality:detail.OverallQualityID,
      itemLevel:detail.ItemLevel,inventoryType:detail.InventoryType,classId:item.ClassID,subclassId:item.SubclassID,
      randomPropertyId:0,enchantments:'0 '.repeat(36).trim()};
  };
  const M2Loader = require(path.join(vendor,'3D/loaders/M2Loader.js'));
  const BoneMapper = require(path.join(vendor,'3D/BoneMapper.js'));
  const native = new M2Loader(await client.getFile(116921,false,true,false));
  await native.load();
  const nativeAttachments = native.attachments.map(attachment => {
    const bone = native.bones[attachment.bone], [x,y,z] = attachment.position;
    return {id:attachment.id,bone:BoneMapper.get_bone_name(bone.boneID,attachment.bone,bone.boneNameCRC),
      offset:[x-bone.pivot[0],z-bone.pivot[1],-y-bone.pivot[2]]};
  });
  const idleIndex=native.animations.findIndex(animation => animation.id===0 && animation.variationIndex===0);
  const gripIndex=native.animations.findIndex(animation => animation.id===15 && animation.variationIndex===0);
  assert.ok(idleIndex>=0 && gripIndex>=0,'Native Stand/HandsClosed animation missing');
  await native.loadAnimsForIndex(idleIndex);await native.loadAnimsForIndex(gripIndex);
  const nativeFingers=native.bones.flatMap((bone,index) => bone.boneID>=8 && bone.boneID<=17 ? [{
    name:BoneMapper.get_bone_name(bone.boneID,index,bone.boneNameCRC),side:bone.boneID<=12?'right':'left',
    idle:bone.rotation.values[idleIndex],grip:bone.rotation.values[gripIndex]?.[0]
  }] : []);
  await writeJson(path.join(options.output,'native-skeleton.json'),{nativeAttachments,nativeFingers});
  if(options.nativeOnly) return;
  const cases = [];
  for (const definition of definitions.filter(entry => !options.cases || options.cases.includes(entry.name))) {
    const current = structuredClone(snapshot);
    current.source = definition.name==='current-helmet' ? snapshot.source : 'local-visual-fixture';
    current.equipment = current.equipment.filter(item => !definition.remove?.includes(item.slot) && (!definition.hands || ![15,16,17].includes(item.slot)));
    for (const item of current.equipment) {
      const local = items.get(item.itemId);
      assert.ok(local,`Missing installed client item ${item.itemId}`);
      item.classId = local.ClassID; item.subclassId = local.SubclassID;
    }
    if (definition.hands) current.equipment.push(...definition.hands.map(([id,slot]) => loadItem(id,slot)));
    current.equipment.sort((a,b) => a.slot-b.slot);
    const directory = path.join(options.output,definition.name);
    await fs.mkdir(directory,{recursive:true});
    await writeJson(path.join(directory,'flowmage.json'),current);
    const head = current.equipment.find(item => item.slot===0);
    cases.push({...definition,directory,helmetHiddenGroups:head?geosets.getHelmetHideGeosetsByDisplayId(head.displayId,10,1):[],
      equipment:current.equipment.map(({itemId,slot,name,displayId,inventoryType,classId,subclassId}) => ({itemId,slot,name,displayId,inventoryType,classId,subclassId}))});
  }
  await writeJson(path.join(options.output,'fixtures.json'),{sourceRevision,clientBuild:client.build.Version,
    snapshot:options.snapshot,notice:'Alternative loadouts are local visual fixtures, not equipment changes on the account.',nativeAttachments,nativeFingers,cases});
}

async function run(args,env,log) {
  let tail = '';
  const begin = Date.now();
  try {
    await new Promise((resolve,reject) => {
      const child = spawn(process.execPath,args,{cwd:__dirname,windowsHide:true,env:{...process.env,...env},stdio:['ignore','pipe','pipe'],signal:AbortSignal.timeout(10*60*1000)});
      const output = chunk => {tail=(tail+chunk.toString()).slice(-100000);};
      child.stdout.on('data',output); child.stderr.on('data',output);
      child.once('error',reject);
      child.once('close',code => code===0?resolve():reject(new Error(`Equipment verification step failed (${code}): ${path.basename(args[0])}\n${tail.slice(-5000)}`)));
    });
  } finally {await fs.appendFile(log,`${new Date().toISOString()} ${args[0]} ${Date.now()-begin}ms\n${tail}\n`);}
}

async function exportCase(entry,options) {
  for (const script of ['prepare.cjs','export.cjs']) {
    await run([path.join(__dirname,script),options.clientRoot],{ARMORY_EXPORT_DIR:entry.directory},path.join(entry.directory,'export.log'));
  }
  // These render tests intentionally omit unverified item effects. Real equipment
  // descriptions are validated independently by the armory synchronization tests.
  const snapshot = JSON.parse(await fs.readFile(path.join(entry.directory,'flowmage.json'),'utf8'));
  await writeJson(path.join(entry.directory,'assets/item-details.json'),{characterCapturedAt:snapshot.capturedAtUtc,
    items:snapshot.equipment.map(item => ({slot:item.slot,itemId:item.itemId,name:{fr:item.nameFr||item.name,en:item.name},
      inventoryType:item.inventoryType,classId:item.classId,subclassId:item.subclassId,damage:[],stats:[],resistances:[],effects:[],enchantments:[],sockets:[],incomplete:true}))});
}

async function verifyCase(entry,manifest,browser,output) {
  const character = JSON.parse(await fs.readFile(path.join(entry.directory,'assets/character.json'),'utf8'));
  const prepared = JSON.parse(await fs.readFile(path.join(entry.directory,'prepared.json'),'utf8'));
  const gltf = JSON.parse(await fs.readFile(path.join(entry.directory,'assets/flowmage.gltf'),'utf8'));
  const modes = character.weaponModes?.length ? character.weaponModes : [null];
  const buffers=new Map();
  for(const mode of modes.filter(Boolean)) {
    const animation=gltf.animations.find(animation => animation.name===character.animationByWeaponMode[mode]);
    assert.ok(animation,`${entry.name}: missing exported clip ${mode}`);
    for(const finger of manifest.nativeFingers || []) {
      const closed=character.attached.some(attachment => attachment.weaponMode===mode && attachment.attachmentId===(finger.side==='right'?1:2));
      const expected=closed?finger.grip:finger.idle?.[0];
      if(!expected?.length) continue;
      const channel=animation.channels.find(channel => channel.target.path==='rotation' && gltf.nodes[channel.target.node].name===finger.name);
      assert.ok(channel,`${entry.name}/${mode}: missing finger track ${finger.name}`);
      const accessor=gltf.accessors[animation.samplers[channel.sampler].output],view=gltf.bufferViews[accessor.bufferView];
      assert.equal(accessor.count,closed?1:finger.idle.length,`${entry.name}/${mode}: incorrect ${finger.side} hand grip`);
      const filename=gltf.buffers[view.buffer].uri;
      if(!buffers.has(filename)) buffers.set(filename,await fs.readFile(path.join(entry.directory,'assets',filename)));
      const bytes=buffers.get(filename),offset=(view.byteOffset||0)+(accessor.byteOffset||0);
      const actual=expected.map((_,index) => bytes.readFloatLE(offset+index*4));
      assert.ok(actual.every((value,index) => Math.abs(value-expected[index])<1e-6),`${entry.name}/${mode}: hand pose differs from native ${closed?'HandsClosed':'Stand'}`);
    }
  }
  for (const attached of character.attached) {
    const native = manifest.nativeAttachments.find(row => row.id===attached.attachmentId);
    assert.ok(native,`${entry.name}: attachment ${attached.attachmentId} absent from native character`);
    assert.equal(attached.bone,native.bone);
    assert.ok(attached.offset.every((value,index) => Math.abs(value-native.offset[index])<1e-6),'Attachment offset differs from the installed native skeleton');
  }
  for (const [mode,expected] of Object.entries(entry.expected || {})) {
    assert.ok(modes.includes(mode),`${entry.name}: missing ${mode} view`);
    const actual = character.attached.filter(attachment => attachment.weaponMode===mode).map(attachment => [attachment.slot,attachment.attachmentId]).sort((a,b) => a[0]-b[0]);
    assert.deepEqual(actual,expected,`${entry.name}: wrong hand/shield attachment`);
  }
  if (entry.equipment.some(item => item.slot===0)) assert.ok(character.attached.some(item => item.slot===0 && item.attachmentId===11),'Helmet not attached to the native helmet point');
  for (const group of entry.helmetHiddenGroups) {
    assert.ok(!character.visibleGeosets.some(id => id>group*100 && id<group*100+100),`Helmet should hide geoset group ${group}`);
    for (const mesh of gltf.meshes) {
      const index = mesh.name?.match(/^flowmage_Geoset(\d+)$/)?.[1];
      if (index===undefined) continue;
      const id = prepared.submeshes[Number(index)];
      assert.ok(!(id>group*100 && id<group*100+100),`Hidden helmet geoset ${id} remains in exported geometry`);
    }
  }
  const server = createServer({outputDir:entry.directory,getViewerConfig:async () => ({locale:'fr',source:'visual-test'}),getStatistics:async () => ({status:'unavailable'})});
  await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
  const url = `http://127.0.0.1:${server.address().port}`;
  const page = await browser.newPage({viewport:{width:1440,height:960}});
  const errors = [], captures = [];
  page.on('pageerror',error => errors.push(error.message));
  page.on('response',response => {if(response.status()>=400 && !response.url().endsWith('favicon.ico')) errors.push(response.status()+' '+response.url());});
  try {
    await page.goto(url+'/?lang=fr');
    await page.waitForFunction(() => window.armory?.ready===true,null,{timeout:60000});
    await page.locator('#animate').click();
    await page.locator('button.slot').first().click();
    const originalPopover = await page.locator('#detail-name').textContent();
    for (const mode of modes) {
      if (mode && await page.evaluate(() => window.armory.weaponMode)!==mode) {
        const before = await cameraState(page);
        await page.locator('#weapon-mode').click();
        assert.equal(await page.evaluate(() => window.armory.weaponMode),mode);
        const after = await cameraState(page);
        assert.ok(before.direction.every((value,index) => Math.abs(value-after.direction[index])<1e-5),'Weapon view reset camera direction');
        assert.ok(Math.abs(before.zoom-after.zoom)<0.04,'Weapon view reset relative zoom');
        assert.equal(await page.locator('#item-popover').isVisible(),true,'Weapon view closed the pinned tooltip');
        assert.equal(await page.locator('#detail-name').textContent(),originalPopover);
      }
      const geometry = await page.evaluate(() => {
        const {root,mixer,weaponMode} = window.armory, attachments=[];
        root.traverse(object => {
          if (object.userData.equipmentSlot===undefined) return;
          let meshes=0,vertices=0;
          object.traverse(mesh => {if(mesh.isMesh){meshes++;vertices+=mesh.geometry.attributes.position.count;}});
          attachments.push({...object.userData,visible:object.visible,meshes,vertices,bone:object.parent.name});
        });
        return {weaponMode,attachments,activeClips:mixer._actions.filter(action => action.isRunning()).map(action => action.getClip().name),
          fingers:root.getObjectByName('bone_IndexFingerL')?.quaternion.toArray()};
      });
      for (const attachment of geometry.attachments) {
        assert.ok(attachment.meshes>0 && attachment.vertices>0,'Attachment contains no geometry');
        assert.equal(attachment.visible,!attachment.weaponMode || attachment.weaponMode===mode,'Melee and ranged weapons rendered together');
      }
      if (mode) assert.deepEqual(geometry.activeClips,[character.animationByWeaponMode[mode]],'Wrong hand-grip animation is active');
      await page.keyboard.press('Escape');
      for (const facing of ['front','back']) {
        await page.evaluate(facing => {
          const armory=window.armory, offset=armory.camera.position.clone().sub(armory.controls.target),distance=offset.length();
          armory.camera.position.copy(armory.controls.target);
          armory.camera.position.x+=(facing==='front'?1:-1)*distance;
          armory.camera.position.y+=distance*.06;
          armory.controls.update();
        },facing);
        await page.waitForTimeout(120);
        const pixels = await page.evaluate(() => {
          const gl=window.armory.renderer.getContext(),width=gl.drawingBufferWidth,height=gl.drawingBufferHeight;
          const bytes=new Uint8Array(width*height*4);gl.readPixels(0,0,width,height,gl.RGBA,gl.UNSIGNED_BYTE,bytes);
          let count=0,minX=width,maxX=0,minY=height,maxY=0;
          for(let i=0;i<bytes.length;i+=4) if(Math.abs(bytes[i]-bytes[0])+Math.abs(bytes[i+1]-bytes[1])+Math.abs(bytes[i+2]-bytes[2])>40){
            count++;const x=(i/4)%width,y=Math.floor(i/4/width);minX=Math.min(minX,x);maxX=Math.max(maxX,x);minY=Math.min(minY,y);maxY=Math.max(maxY,y);
          }
          return {width,height,count,minX,maxX,minY,maxY};
        });
        assert.ok(pixels.count>1000,`${entry.name}/${mode}/${facing}: blank canvas`);
        assert.ok(pixels.minX>2 && pixels.maxX<pixels.width-2 && pixels.minY>2 && pixels.maxY<pixels.height-2,`${entry.name}/${mode}/${facing}: equipment clipped ${JSON.stringify(pixels)}`);
        const screenshot=path.join(output,`${entry.name}-${mode||'default'}-${facing}.png`);
        await page.screenshot({path:screenshot});
        captures.push({mode,facing,screenshot,pixels,geometry});
      }
      // Pin a detail again before the next mode, to exercise preservation.
      await page.locator('button.slot').first().click();
    }
    if (entry.name==='current-helmet') await verifyPreparationState(browser,url,character);
    assert.deepEqual(errors,[]);
    return {name:entry.name,helmetHiddenGroups:entry.helmetHiddenGroups,visibleGeosets:character.visibleGeosets,captures,errors};
  } finally {
    await page.close();server.closeAllConnections();await new Promise(resolve => server.close(resolve));
  }
}

async function verifyPreparationState(browser,url,character) {
  for (const locale of ['fr','en']) {
    const page=await browser.newPage({viewport:{width:1228,height:794}});
    let modelStatus='building';
    try {
      await page.route('**/armory.json',route => route.fulfill({json:{revision:'legacy',assetBase:'/assets/',modelReady:modelStatus==='ready',modelStatus}}));
      await page.goto(url+'/?lang='+locale);
      await page.waitForFunction(() => window.armory?.ready===true);
      assert.equal(await page.locator('#loading').textContent(),locale==='fr'?'Préparation du modèle 3D…':'Preparing 3D model…');
      assert.equal(await page.locator('button.slot').count(),character.equipment.length);
      modelStatus='unavailable';
      await page.evaluate(() => window.dispatchEvent(new Event('focus')));
      await page.waitForFunction(expected => document.querySelector('#loading').textContent===expected,
        locale==='fr'?'Modèle 3D indisponible pour cet équipement':'3D model unavailable for this equipment');
      modelStatus='ready';
      await page.evaluate(() => window.dispatchEvent(new Event('focus')));
      await page.waitForFunction(() => window.armory.root.children.length>0 && document.querySelector('#loading').hidden);
      assert.equal(await page.locator('#weapon-mode span').textContent(),locale==='fr'?'Mêlée':'Melee');
      await page.locator('#weapon-mode').click();
      assert.equal(await page.locator('#weapon-mode span').textContent(),locale==='fr'?'À distance':'Ranged');
      assert.equal(await page.locator('#weapon-mode').getAttribute('aria-label'),locale==='fr'?'Afficher les armes de mêlée':'Show melee weapons');
    } finally {await page.close();}
  }
}

async function cameraState(page) {
  return page.evaluate(() => {
    const {camera,controls,bounds}=window.armory;
    const size=bounds.getSize(camera.position.clone());
    return {direction:camera.position.clone().sub(controls.target).normalize().toArray(),zoom:camera.position.distanceTo(controls.target)/size.y};
  });
}

async function main() {
  if(process.argv[2]==='--fixture-worker') return fixtureWorker(JSON.parse(process.argv[3]));
  const args=process.argv.slice(2), options={};
  for(let i=0;i<args.length;i+=2) {
    const key=args[i];assert.ok(['--client-root','--snapshot','--output','--reuse','--cases'].includes(key),'Unknown argument: '+key);
    assert.ok(args[i+1],'Missing argument: '+key);options[key.slice(2)]=args[i+1];
  }
  const output=path.resolve(options.reuse || options.output || path.join(shared,'equipment-verification',new Date().toISOString().replaceAll(':','-')));
  const allowed=path.join(shared,'equipment-verification')+path.sep;
  assert.ok(output.startsWith(allowed),'Verification output must be a subdirectory of artifacts/armory-prototype/equipment-verification');
  await fs.mkdir(output,{recursive:true});
  const settings={output,clientRoot:options['client-root'] || 'C:\\Program Files (x86)\\WotLK',
    snapshot:path.resolve(options.snapshot || path.join(shared,'equipment-current/flowmage.json')),cases:options.cases?.split(',')};
  if(!options.reuse) {
    await run([__filename,'--fixture-worker',JSON.stringify(settings)],{},path.join(output,'fixture-export.log'));
  }
  const manifest=JSON.parse(await fs.readFile(path.join(output,'fixtures.json'),'utf8'));
  Object.assign(manifest,JSON.parse(await fs.readFile(path.join(output,'native-skeleton.json'),'utf8').catch(() => '{}')));
  const selected=manifest.cases.filter(entry => !settings.cases || settings.cases.includes(entry.name));
  assert.ok(selected.length,'No equipment cases selected');
  if(!options.reuse) for(const entry of selected) {
    console.log(`Exporting local visual fixture: ${entry.name}`);
    await exportCase(entry,settings);
  }
  const {chromium}=require(process.env.PLAYWRIGHT_MODULE || path.join(process.env.USERPROFILE,'.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright'));
  const browser=await chromium.launch({channel:'msedge',headless:true,args:['--enable-unsafe-swiftshader']});
  const results=[];
  try {
    for(const entry of selected) results.push(await verifyCase(entry,manifest,browser,output));
    const without=results.find(entry => entry.name==='no-helmet'),withHelmet=results.find(entry => entry.name==='current-helmet');
    if(without && withHelmet) assert.ok(without.visibleGeosets.some(id => withHelmet.helmetHiddenGroups.some(group => id>group*100 && id<group*100+100)),
      'Removing the helmet did not restore any hair/ear geoset');
    await writeJson(path.join(output,'verification.json'),{passed:results.length,sourceRevision,notice:manifest.notice,results});
    console.log(JSON.stringify({passed:results.length,output,captures:results.reduce((count,result)=>count+result.captures.length,0)}));
  } finally {await browser.close();}
}
main().catch(error => {console.error(error);process.exitCode=1;});
