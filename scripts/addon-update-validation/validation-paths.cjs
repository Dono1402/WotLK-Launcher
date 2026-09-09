'use strict';

const fs = require('node:fs');
const path = require('node:path');
const {randomUUID} = require('node:crypto');

// Both locations are explicit. There is no default pointing at a game install.
const [stageArgument, baselineArgument] = process.argv.slice(2);
if (!stageArgument || !baselineArgument || process.argv.length !== 4) {
  throw new Error('Usage: node <validation-script.cjs> <prepared-stage> <baseline-AddOns>');
}
const stage = fs.realpathSync(stageArgument);
const live = fs.realpathSync(baselineArgument);
function contains(parent, child) {
  const relative = path.relative(parent, child);
  return relative === '' || (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith(`..${path.sep}`));
}
if (contains(stage, live) || contains(live, stage)) {
  throw new Error('Stage and read-only addon baseline must be separate, non-nested directories');
}
for (const location of [stage, live]) {
  if (!fs.statSync(location).isDirectory()) throw new Error(`Not a directory: ${location}`);
}
function writeReport(filename, value) {
  if (path.basename(filename) !== filename) throw new Error('Report must be a stage-root filename');
  const target = path.join(stage, filename);
  if (fs.existsSync(target)) {
    const info = fs.lstatSync(target);
    if (!info.isFile() || info.isSymbolicLink() || info.nlink > 1) throw new Error('Refusing linked or non-file report');
  }
  const temporary = path.join(stage, `.${filename}.${randomUUID()}.tmp`);
  let created = false;
  try {
    fs.writeFileSync(temporary, JSON.stringify(value,null,2)+'\n', {flag:'wx',mode:0o600});
    created = true;
    fs.renameSync(temporary, target);
    created = false;
  } finally {
    if (created) fs.unlinkSync(temporary);
  }
}
module.exports = {stage, live, writeReport};
