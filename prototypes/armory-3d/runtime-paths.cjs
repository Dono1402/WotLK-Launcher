const path = require('node:path');

function absoluteEnvironmentPath(environment,name,fallback,{required=false}={}) {
  const value = environment[name];
  if (value===undefined || value==='') {
    if (required) throw new Error(`Missing ${name}`);
    return fallback;
  }
  if (typeof value!=='string' || !path.isAbsolute(value) || value.includes('\0')) throw new Error(`Invalid ${name}`);
  return path.resolve(value);
}

function readRuntimePaths(environment=process.env) {
  const source = environment.ATLAS_ARMORY_SOURCE || 'legacy';
  if (!['legacy','rpc'].includes(source)) throw new Error('Invalid armory data source');
  const publicMode = source==='rpc';
  const legacyRoot = path.resolve(__dirname,'../../artifacts/armory-prototype');
  const dataRoot = absoluteEnvironmentPath(environment,'ATLAS_ARMORY_DATA_ROOT',legacyRoot,{required:publicMode});
  const vendorRoot = absoluteEnvironmentPath(environment,'ATLAS_ARMORY_VENDOR_ROOT',path.join(legacyRoot,'tools/wow-export/src/js'),{required:publicMode});
  const assetRoot = absoluteEnvironmentPath(environment,'ATLAS_ARMORY_ASSET_ROOT',path.resolve(__dirname,'../../source/WotLK.Launcher/Assets'),{required:publicMode});
  const metadataRoot = absoluteEnvironmentPath(environment,'ATLAS_ARMORY_METADATA_ROOT',path.join(dataRoot,'metadata'),{required:publicMode});
  const clientRoot = absoluteEnvironmentPath(environment,'ATLAS_ARMORY_CLIENT_ROOT',undefined);
  const outputRoot = absoluteEnvironmentPath(environment,'ARMORY_EXPORT_DIR',dataRoot);
  return {source,publicMode,dataRoot,vendorRoot,assetRoot,metadataRoot,clientRoot,outputRoot};
}

const paths = readRuntimePaths();
module.exports = {...paths,readRuntimePaths};
