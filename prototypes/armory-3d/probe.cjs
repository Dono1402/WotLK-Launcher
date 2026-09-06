const fs = require('node:fs/promises');
const path = require('node:path');
const { openClient } = require('./local-client.cjs');

async function main() {
  const { client, tableIds, vendor, output } = await openClient(process.argv[2]);
  const report = { version: client.build.Version, fileCount: client.rootEntries.size, tables: {} };
  const db2 = require(path.join(vendor, 'casc/db2.js'));
  const names = process.argv.slice(3);
  for (const name of names.length ? names : ['ChrRaces', 'CharSections', 'CharHairGeosets', 'CharacterFacialHairStyles', 'ItemDisplayInfo', 'ChrModel']) {
    const id = tableIds.get(name.toLowerCase());
    const entry = report.tables[name] = { fileDataId: id, present: id ? client.fileExists(id) : false };
    if (entry.present) {
      try {
        const rows = await db2[name].getAllRows();
        entry.count = rows.size;
        entry.fields = Object.keys(rows.values().next().value ?? {});
        entry.sample = Array.from(rows.entries()).filter(([key, row]) =>
          name === 'ChrRaces' ? key === 10 :
          name === 'CharSections' || name === 'CharHairGeosets' || name === 'CharacterFacialHairStyles'
            ? row.RaceID === 10 && row.SexID === 1 :
          name === 'ItemDisplayInfo' ? [10169, 18120, 44339].includes(key) : true).slice(0, 12);
      } catch (error) { entry.error = error.stack; }
    }
    console.log(name, JSON.stringify(entry));
  }
  await fs.writeFile(path.join(output, 'client-probe.json'), JSON.stringify(report, null, 2));
}

main().catch(error => { console.error(error); process.exitCode = 1; });
