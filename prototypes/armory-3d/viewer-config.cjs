const fs = require('node:fs/promises');
const path = require('node:path');

async function readViewerConfig(localAppData = process.env.LOCALAPPDATA) {
  if (localAppData) {
    for (const [directory,source] of [['Atlas Launcher Local','launcher-local'],['WotLK Launcher','launcher']]) {
      try {
        const json = await fs.readFile(path.join(localAppData,directory,'settings.json'),'utf8');
        const settings = JSON.parse(json.replace(/^\uFEFF/,''));
        if (!settings || typeof settings!=='object' || Array.isArray(settings)) continue;
        return { locale: typeof settings.InterfaceLocale==='string' && /^en/i.test(settings.InterfaceLocale) ? 'en' : 'fr', source };
      } catch (error) {
        if (!['ENOENT','ENOTDIR'].includes(error.code) && !(error instanceof SyntaxError)) throw error;
      }
    }
  }
  return { locale:null, source:'browser' };
}
module.exports = { readViewerConfig };
