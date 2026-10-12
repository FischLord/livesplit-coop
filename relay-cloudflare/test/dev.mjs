// Starts `wrangler dev --env test` on a free local port for integration tests and stops the whole process tree afterwards.
import { spawn, spawnSync } from 'node:child_process';
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
  let output = '', exited = false;
  const exit = new Promise(resolve => dev.on('exit', () => { exited = true; resolve(); }));
  const cleanup = () => { try { rmSync(state, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 }); } catch (e) { console.error('temp cleanup failed:', e.message); } };
  // npx under a shell on Windows leaves workerd running unless the whole tree is stopped.
  const stop = async () => {
    if (!exited) {
      if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(dev.pid), '/T', '/F'], { stdio: 'ignore' }); else dev.kill();
      await Promise.race([exit, new Promise(r => setTimeout(r, 10000))]);
      await new Promise(r => setTimeout(r, 300)); // let workerd release its files
    }
    cleanup();
  };
  try {
    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('wrangler dev did not start: ' + output)), 90000);
      const watch = data => { output += data; if (/Ready on/.test(output)) { clearTimeout(timer); resolve(); } };
      dev.stdout.on('data', watch); dev.stderr.on('data', watch);
      dev.on('exit', code => { clearTimeout(timer); reject(new Error(`wrangler dev exited (${code}): ` + output)); });
    });
  } catch (e) { await stop(); throw e; }
  return { base: `ws://127.0.0.1:${port}`, stop };
}
