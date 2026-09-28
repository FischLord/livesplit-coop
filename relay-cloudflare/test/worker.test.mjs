// Runs the shared protocol conformance suite against the Worker in the local workerd runtime.
import { test, before, after } from 'node:test';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { conformance } from '../../relay/test/conformance.mjs';

let dev, base;
const freePort = () => new Promise(resolve => { const s = createServer().listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); }); });
before(async () => {
  const port = await freePort();
  dev = spawn('npx', ['wrangler', 'dev', '--env', 'test', '--ip', '127.0.0.1', '--port', String(port), '--persist-to', '.wrangler/test-state'],
    { cwd: new URL('..', import.meta.url), shell: process.platform === 'win32', env: { ...process.env, WRANGLER_SEND_METRICS: 'false' } });
  let output = '';
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('wrangler dev did not start:\n' + output)), 90000);
    const watch = data => { output += data; if (/Ready on/.test(output)) { clearTimeout(timer); resolve(); } };
    dev.stdout.on('data', watch); dev.stderr.on('data', watch);
    dev.on('exit', code => { clearTimeout(timer); reject(new Error(`wrangler dev exited (${code}):\n` + output)); });
  });
  base = `ws://127.0.0.1:${port}`;
}, { timeout: 120000 });
after(() => {
  if (!dev) return;
  // npx under a shell on Windows leaves workerd running unless the whole tree is stopped.
  if (process.platform === 'win32') spawn('taskkill', ['/pid', String(dev.pid), '/T', '/F']); else dev.kill();
});
conformance(test, () => base);
