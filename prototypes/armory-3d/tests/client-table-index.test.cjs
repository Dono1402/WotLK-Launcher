const {test} = require('node:test');
const assert = require('node:assert/strict');
const {withIndexedTableRows} = require('../local-client.cjs');

class Table {
  constructor(rows) { this.rows=rows; this.binaryReads=0; this.indexReads=0; }
  getAllRows() { this.indexReads++; return this.rows; }
  getRow(id) {
    this.binaryReads++;
    const row=this.rows.get(parseInt(id));
    return row===undefined ? null : structuredClone(row);
  }
}

test('indexed local lookups preserve row IDs, inflated copies, missing rows and fresh mutable records',async () => {
  const rows=new Map([[10,{ID:10,targets:[1,2],fileId:100}],[20,{ID:20,targets:[1,2],fileId:100}]]);
  const table=new Table(rows);
  const expected=[10,'20','10suffix',999,'invalid'].map(id => table.getRow(id));
  const binaryReads=table.binaryReads;
  const result=await withIndexedTableRows(table,async () => {
    const result=[];
    for (const id of [10,'20','10suffix',999,'invalid']) result.push(await table.getRow(id));
    const first=await table.getRow(10); first.targets[0]=999; first.fileId=200;
    assert.deepEqual(await table.getRow(10),rows.get(10),'a consumer cannot modify the decoded table or another lookup');
    return result;
  });
  assert.deepEqual(result,expected);
  assert.equal(table.binaryReads,binaryReads,'repeated indexed lookups must not scan binary records');
  assert.equal(table.indexReads,1);
  assert.equal(Object.hasOwn(table,'getRow'),false,'the original prototype reader is restored');
  assert.deepEqual(table.getRow(20),rows.get(20));
  assert.equal(table.binaryReads,binaryReads+1);
});

test('the index is scoped to one table load and cannot survive client changes, errors or cancellation',async () => {
  const table=new Table(new Map([[1,{ID:1,version:'client-a'}]]));
  const ownGetRow=table.getRow.bind(table);
  Object.defineProperty(table,'getRow',{value:ownGetRow,writable:true,configurable:true,enumerable:false});
  const descriptor=Object.getOwnPropertyDescriptor(table,'getRow');
  assert.equal(await withIndexedTableRows(table,async () => (await table.getRow(1)).version),'client-a');
  assert.deepEqual(Object.getOwnPropertyDescriptor(table,'getRow'),descriptor);
  table.rows=new Map([[1,{ID:1,version:'client-b'}]]);
  assert.equal(await withIndexedTableRows(table,async () => (await table.getRow(1)).version),'client-b');
  const abort=new DOMException('Cancelled','AbortError');
  await assert.rejects(withIndexedTableRows(table,async () => { throw abort; }),error => error===abort);
  assert.deepEqual(Object.getOwnPropertyDescriptor(table,'getRow'),descriptor);
  assert.equal(table.getRow(1).version,'client-b');
  const other=new Table(new Map([[1,{ID:1,version:'another-build'}]]));
  assert.equal(await withIndexedTableRows(other,async () => (await other.getRow(1)).version),'another-build');
  assert.equal(table.indexReads,3);
});
