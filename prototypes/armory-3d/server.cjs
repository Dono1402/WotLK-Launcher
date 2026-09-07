const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const { readViewerConfig } = require('./viewer-config.cjs');
const {readArmory,readActiveStatistics} = require('./armory-cache.cjs');

const {dataRoot:output} = require('./runtime-paths.cjs');
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.gltf': 'model/gltf+json', '.png': 'image/png', '.ttf': 'font/ttf', '.bin': 'application/octet-stream' };
const {resolveFile} = require('./viewer-assets.cjs');

function createServer({ outputDir=output,getViewerConfig = readViewerConfig, getStatistics = () => readActiveStatistics(outputDir),getArmory = () => readArmory(outputDir) } = {}) {
  return http.createServer(async (request, response) => {
    response.setHeader('Cache-Control', 'no-store');
    response.setHeader('X-Content-Type-Options', 'nosniff');
    response.setHeader('Referrer-Policy', 'no-referrer');
    response.setHeader('Content-Security-Policy', "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; worker-src 'self' blob:; object-src 'none'; frame-ancestors 'none'");
    if (!['GET', 'HEAD'].includes(request.method)) { response.writeHead(405); return response.end(); }
    const host = request.headers.host?.split(':')[0];
    if (!['127.0.0.1', 'localhost'].includes(host)) { response.writeHead(403); return response.end(); }
    if (request.url.split('?')[0]==='/armory.json') {
      try {
        const body = JSON.stringify(await getArmory());
        response.writeHead(200,{'Content-Type':'application/json','Content-Length':Buffer.byteLength(body)});
        return response.end(request.method==='HEAD' ? undefined : body);
      } catch { response.writeHead(503); return response.end(); }
    }
    if (request.url.split('?')[0]==='/statistics.json') {
      try {
        const body = JSON.stringify(await getStatistics());
        response.writeHead(200,{'Content-Type':'application/json','Content-Length':Buffer.byteLength(body)});
        return response.end(request.method==='HEAD' ? undefined : body);
      } catch { response.writeHead(503); return response.end(); }
    }
    if (request.url.split('?')[0]==='/viewer-config.json') {
      try {
        const config = await getViewerConfig();
        const body = JSON.stringify({locale:config.locale, source:config.source});
        response.writeHead(200, { 'Content-Type':'application/json', 'Content-Length':Buffer.byteLength(body) });
        return response.end(request.method==='HEAD' ? undefined : body);
      } catch { response.writeHead(503); return response.end(); }
    }
    const file = resolveFile(request.url,outputDir);
    if (!file) { response.writeHead(404); return response.end(); }
    try {
      const data = await fs.readFile(file);
      response.setHeader('Content-Type', types[path.extname(file)] || 'application/octet-stream');
      response.setHeader('Content-Length', data.length);
      response.writeHead(200);
      response.end(request.method === 'HEAD' ? undefined : data);
    } catch (error) {
      response.writeHead(error.code === 'ENOENT' ? 404 : 500);
      response.end();
    }
  });
}

if (require.main === module) {
  const port = Number(process.env.PORT || 4387);
  const server = createServer();
  let sync;
  server.listen(port, '127.0.0.1', async () => {
    console.log(`Armurerie locale : http://127.0.0.1:${port}`);
    sync = await require('./statistics-sync.cjs').attachStatisticsSync(server);
  });
  const stop = async () => {
    server.close();
    server.closeAllConnections();
    await sync?.stop();
  };
  process.once('SIGINT',stop);
  process.once('SIGTERM',stop);
}
module.exports = { createServer, resolveFile };
