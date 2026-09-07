const fs = require('node:fs');
const path = require('node:path');
const net = require('node:net');
const { spawn } = require('node:child_process');

async function main() {
  const output = path.resolve(__dirname, '../../artifacts/armory-prototype');
  if (!fs.existsSync(path.join(output, 'assets/character.json'))) throw new Error('Export the character before starting the viewer');
  let port = 4387;
  for (; port < 4400; port++) {
    const available = await new Promise(resolve => {
      const probe = net.createServer();
      probe.once('error', () => resolve(false));
      probe.listen(port, '127.0.0.1', () => probe.close(() => resolve(true)));
    });
    if (available) break;
  }
  if (port === 4400) throw new Error('No free local port between 4387 and 4399');
  const stdout = fs.openSync(path.join(output, 'server.log'), 'a');
  const stderr = fs.openSync(path.join(output, 'server-error.log'), 'a');
  const child = spawn(process.execPath, [path.join(__dirname, 'server.cjs')], {
    cwd: __dirname, detached: true, windowsHide: true, stdio: ['ignore', stdout, stderr],
    env: { ...process.env, PORT: String(port) }
  });
  child.unref();
  fs.closeSync(stdout);
  fs.closeSync(stderr);
  fs.writeFileSync(path.join(output, 'server-instance.json'), JSON.stringify({ pid:child.pid, port }, null, 2));
  console.log(`http://127.0.0.1:${port} (PID ${child.pid})`);
}
main().catch(error => { console.error(error); process.exitCode=1; });
