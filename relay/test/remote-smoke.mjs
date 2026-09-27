// Opt-in test against an owned deployment. Credentials are loaded from a private file.
import { readFile } from 'node:fs/promises';
import { once } from 'node:events';
import assert from 'node:assert/strict';
import WebSocket from 'ws';
const [url, file] = process.argv.slice(2);
if (!url?.startsWith('wss://') || !file) throw new Error('Usage: node test/remote-smoke.mjs wss://host/coop private-rooms.json');
const [room] = JSON.parse(await readFile(file, 'utf8'));
const clients = [];
const deadline = setTimeout(() => { console.error('Remote smoke test timeout'); process.exit(1); }, 15000);
async function connect(role, key) {
  const ws = new WebSocket(url, { handshakeTimeout: 5000 }); clients.push(ws);
  await once(ws, 'open');
  return { ws, hello: () => ws.send(JSON.stringify({type:'hello',v:1,room:room.name,role,key})) };
}
try {
  const health = await fetch(url.replace('wss:', 'https:').replace('/coop', '/healthz'));
  assert.equal(health.status, 200); assert.deepEqual(await health.json(), {ok:true});
  const wrong = await connect('viewer', 'invalid'.repeat(8)); const rejected = once(wrong.ws, 'close'); wrong.hello(); assert.equal((await rejected)[0], 1008);
  const host = await connect('host', room.hostKey); const ready = once(host.ws, 'message'); host.hello(); assert.equal(JSON.parse((await ready)[0]).type, 'ready');
  const second = await connect('host', room.hostKey); const duplicate = once(second.ws, 'close'); second.hello(); assert.equal((await duplicate)[0], 1008);
  const viewer = await connect('viewer', room.viewerKey); const viewerReady = once(viewer.ws, 'message'); viewer.hello(); assert.equal(JSON.parse((await viewerReady)[0]).type, 'ready');
  const denied = once(viewer.ws, 'close'); viewer.ws.send(JSON.stringify({type:'snapshot'})); assert.equal((await denied)[0], 1008);
  console.log('PASS: public certificate validation, HTTPS health, wrong-key rejection, authenticated host/viewer, duplicate host rejection, viewer publish rejection');
} finally { clearTimeout(deadline); for (const ws of clients) ws.terminate(); }
