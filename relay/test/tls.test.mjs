import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { once } from 'node:events';
import https from 'node:https';
import WebSocket from 'ws';
import { createRelay } from '../src/server.mjs';
import { readTls, reloadTls } from '../src/tls.mjs';

test('TLS authenticates WSS, rotates certificates without disconnecting and retains valid TLS on bad reload', async t => {
  const folder = await mkdtemp(join(tmpdir(), 'coop-tls-'));
  t.after(() => rm(folder, { recursive: true, force: true }));
  const gitOpenSsl = join(process.env.ProgramFiles || '', 'Git', 'usr', 'bin', 'openssl.exe');
  const openssl = process.platform === 'win32' && existsSync(gitOpenSsl) ? gitOpenSsl : 'openssl';
  async function certificate(name) {
    const key = join(folder, name + '.key'), cert = join(folder, name + '.crt');
    execFileSync(openssl, ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1', '-keyout', key, '-out', cert], { stdio: 'ignore' });
    return { ca: await readFile(cert), pem: (await readFile(key, 'utf8')) + (await readFile(cert, 'utf8')) };
  }
  const a = await certificate('a'), b = await certificate('b'), file = join(folder, 'server.pem');
  await writeFile(file, a.pem);
  const tls = await readTls(file);
  const relay = createRelay({ tls, rooms: [{ name: 'test', hostKey: 'h'.repeat(32), viewerKey: 'v'.repeat(32) }] });
  relay.server.listen(0, '127.0.0.1'); await once(relay.server, 'listening');
  t.after(() => relay.close());
  const errors = [], renewal = reloadTls(relay.server, file, tls, m => errors.push(m));
  t.after(() => renewal.stop());
  const base = `127.0.0.1:${relay.server.address().port}`;
  const health = ca => new Promise((resolve, reject) => {
    const req = https.get(`https://${base}/healthz`, { ca, agent: false }, res => { res.resume(); resolve(res.statusCode); });
    req.on('error', reject);
  });
  assert.equal(await health(a.ca), 200);
  await assert.rejects(health(b.ca));
  const ws = new WebSocket(`wss://${base}/coop`, { ca: a.ca }); await once(ws, 'open');
  ws.send(JSON.stringify({ type: 'hello', v: 1, room: 'test', role: 'host', key: 'h'.repeat(32) }));
  assert.equal(JSON.parse((await once(ws, 'message'))[0]).type, 'ready');
  await writeFile(file, b.pem); assert.equal(await renewal.refresh(), true);
  assert.equal(await health(b.ca), 200); await assert.rejects(health(a.ca));
  ws.send(JSON.stringify({ type: 'ping' })); assert.equal(JSON.parse((await once(ws, 'message'))[0]).type, 'pong');
  await writeFile(file, 'invalid PEM'); assert.equal(await renewal.refresh(), false);
  assert.equal(errors.length, 1); assert.equal(await health(b.ca), 200);
  await assert.rejects(readTls(file));
  ws.close();
});
