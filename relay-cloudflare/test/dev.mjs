// Starts `wrangler dev --env test` on a free local port for integration tests and stops the whole process tree afterwards.
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const freePort = () => new Promise(resolve => { const s = createServer().listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); }); });

export async function startDev() {
  const port = await freePort();
  // Test files run in parallel, and two workerd processes must not share one SQLite state directory.
  const state = mkdtempSync(join(tmpdir(), 'coop-relay-test-'));
  const dev = spawn('npx', ['wrangler', 'dev', '--env', 'test', '--ip', '127.0.0.1', '--port', String(port), '--persist-to', state],
    { cwd: new URL('..', import.meta.url), shell: process.platform === 'win32', env: { ...process.env, WRANGLER_SEND_METRICS: 'false' } });
  let output = '';
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('wrangler dev did not start:\n' + output)), 90000);
    const watch = data => { output += data; if (/Ready on/.test(output)) { clearTimeout(timer); resolve(); } };
    dev.stdout.on('data', watch); dev.stderr.on('data', watch);
    dev.on('exit', code => { try { rmSync(state, { recursive: true, force: true }); } catch {} clearTimeout(timer); reject(new Error(`wrangler dev exited (${code}):\n` + output)); });
  });
  return {
    base: `ws://127.0.0.1:${port}`,
    // npx under a shell on Windows leaves workerd running unless the whole tree is stopped.
    stop() { if (process.platform === 'win32') spawn('taskkill', ['/pid', String(dev.pid), '/T', '/F']); else dev.kill(); }
  };
}
