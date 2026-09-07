const fs = require('node:fs/promises');
const path = require('node:path');
const {readStatistics} = require('./statistics-cache.cjs');

const {dataRoot:output} = require('./runtime-paths.cjs');
const revisionPattern = /^[a-f0-9]{32}$/;

async function readArmory(outputDir=output) {
  let manifest;
  try { manifest = JSON.parse(await fs.readFile(path.join(outputDir,'armory-current.json'),'utf8')); }
  catch (error) { if (error.code==='ENOENT') return {revision:'legacy',assetBase:'/assets/'}; throw error; }
  if (manifest.schemaVersion!==1 || !revisionPattern.test(manifest.revision)) throw new Error('Invalid armory manifest');
  return {revision:manifest.revision,assetBase:`/snapshots/${manifest.revision}/assets/`};
}

function revisionDirectory(revision,outputDir=output) {
  if (revision==='legacy') return outputDir;
  if (!revisionPattern.test(revision)) throw new Error('Invalid armory revision');
  return path.join(outputDir,'snapshots',revision);
}

async function readActiveStatistics(outputDir=output) {
  const {revision} = await readArmory(outputDir);
  return readStatistics(path.join(revisionDirectory(revision,outputDir),'assets/statistics.json'));
}

module.exports = {readArmory,readActiveStatistics,revisionDirectory,revisionPattern};
