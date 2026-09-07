const path = require('node:path');
const {dataRoot:output,assetRoot} = require('./runtime-paths.cjs');

const fixed = new Map([
  ['/', path.join(__dirname, 'index.html')],
  ['/app.js', path.join(__dirname, 'app.js')],
  ['/i18n.mjs', path.join(__dirname, 'i18n.mjs')],
  ['/character-stats.mjs', path.join(__dirname, 'character-stats.mjs')],
  ['/character-labels.mjs', path.join(__dirname, 'character-labels.mjs')],
  ['/style.css', path.join(__dirname, 'style.css')],
  ['/inter-fonts.css', path.join(__dirname, 'inter-fonts.css')],
  ...['Regular','Medium','SemiBold','ExtraBold'].map(face => [`/fonts/Inter-${face}.ttf`,
    path.join(assetRoot,`Fonts/Inter-${face}.ttf`)]),
  ['/lucide.js', path.join(__dirname, 'node_modules/lucide/dist/umd/lucide.min.js')]
]);

function resolveFile(url,outputDir=output) {
  let pathname;
  try { pathname = decodeURIComponent(url.split('?')[0]); } catch { return null; }
  if (fixed.has(pathname)) return fixed.get(pathname);
  if (pathname.toLowerCase()==='/assets/statistics.json') return null;
  if (pathname.includes('..') || pathname.includes('\\') || pathname.includes('\0')) return null;
  const revisionAsset = pathname.match(/^\/snapshots\/([a-f0-9]{32})\/assets\/([a-zA-Z0-9_-]+\.(?:gltf|bin|png|json))$/);
  if (revisionAsset) return revisionAsset[2].toLowerCase()==='statistics.json' ? null : path.join(outputDir,'snapshots',revisionAsset[1],'assets',revisionAsset[2]);
  if (/^\/assets\/[a-zA-Z0-9_-]+\.(gltf|bin|png|json)$/.test(pathname)) return path.join(outputDir,'assets', pathname.slice(8));
  if (/^\/three\/[a-zA-Z0-9_./-]+\.js$/.test(pathname)) return path.join(__dirname, 'node_modules/three', pathname.slice(7));
  return null;
}


module.exports = {resolveFile};
