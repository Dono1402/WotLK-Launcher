const {test} = require('node:test');
const assert = require('node:assert/strict');
const {enrichItemDetails} = require('../export-pipeline.cjs');

test('local tables enrich each supported tooltip while retaining honest partial details for unsupported items',() => {
  const enchantments = Array(36).fill(0); enchantments[0] = 5;
  const snapshot = {capturedAtUtc:'2026-09-06 18:00:00.000',equipment:[
    {slot:0,itemId:100,randomPropertyId:0,enchantments:enchantments.join(' ')},
    {slot:1,itemId:200,randomPropertyId:0,enchantments:Array(36).fill(0).join(' ')}]};
  const base = {name:{en:'Hood',fr:'Capuche'},description:{en:'',fr:''},classId:4,subclassId:1,inventoryType:1,
    requiredLevel:20,armor:15,block:0,bonding:1,stats:[[3,2]],damage:[],delay:0,resistances:[],spells:[],sockets:[],scalingDistribution:0,scalingValue:0};
  const catalog = {items:[{...base,itemId:100},{...base,itemId:200,scalingDistribution:1}]};
  const fallback = {characterCapturedAt:'old',items:[{slot:0,itemId:100,incomplete:true},{slot:1,itemId:200,incomplete:true,stats:[]}]};
  const tables = {fr:{enchantments:{5:{Name_lang:'+5 Force'}}},en:{enchantments:{5:{Name_lang:'+5 Strength'}}}};
  const result = enrichItemDetails(snapshot,catalog,tables,fallback);
  assert.equal(result.characterCapturedAt,snapshot.capturedAtUtc);
  assert.deepEqual(result.items[0].enchantments,[{slot:0,name:{fr:'+5 Force',en:'+5 Strength'}}]);
  assert.equal(result.items[0].incomplete,undefined);
  assert.deepEqual(result.items[1],fallback.items[1]); assert.equal(result.items[1].incomplete,true);
  assert.equal(fallback.characterCapturedAt,'old'); assert.equal(fallback.items[0].incomplete,true);
  assert.deepEqual(enrichItemDetails(snapshot,catalog,undefined,fallback).items,fallback.items);
  assert.throws(() => enrichItemDetails(snapshot,catalog,undefined,{items:[]}),/Missing fallback/);
});
