// Lobby endpoints of the Cloudflare relay, against the local workerd runtime.
import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
import WebSocket from 'ws';
import { startDev } from './dev.mjs';
import { snapshot } from '../../relay/test/conformance.mjs';

let dev;
before(async () => { dev = await startDev(); }, { timeout: 120000 });
after(() => dev?.stop());
const http = () => dev.base.replace('ws:', 'http:');
// State persists between runs (.wrangler/test-state), so every test uses fresh names.
const unique = prefix => (prefix + Date.now().toString(36) + Math.random().toString(36).slice(2, 7)).slice(0, 32);
const key = c => c.repeat(43);
const create = body => fetch(http() + '/lobbies', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
async function client(roomId, role, k) {
  const ws = new WebSocket(`${dev.base}/coop/${roomId}`); const queue = [], waiters = [];
  ws.on('message', data => { const m = JSON.parse(data); const i = waiters.findIndex(w => w.type === m.type); if (i < 0) queue.push(m); else waiters.splice(i, 1)[0].resolve(m); });
  ws.next = type => new Promise((resolve, reject) => {
    const index = queue.findIndex(m => m.type === type); if (index >= 0) { resolve(queue.splice(index, 1)[0]); return; }
    const timer = setTimeout(() => reject(new Error('Timeout waiting for ' + type)), 5000);
    waiters.push({ type, resolve: m => { clearTimeout(timer); resolve(m); } });
  });
  await once(ws, 'open'); ws.send(JSON.stringify({ type: 'hello', v: 3, role, key: k })); return ws;
}
const closed = async ws => { const [code, reason] = await once(ws, 'close'); return [code, reason.toString()]; };

test('lobbies: create, connect host and viewer with the derived key, refuse a taken name', async () => {
  const name = unique('cr');
  const response = await create({ name: '  ' + name.toUpperCase() + ' ', viewerKey: key('v'), public: false, locked: true });
  assert.equal(response.status, 201);
  const made = await response.json();
  assert.equal(made.roomId, 'lobby_' + name);
  assert.match(made.hostKey, /^[A-Za-z0-9_-]{43}$/);
  assert.equal(made.idleExpiryDays, 7);
  assert.equal('viewerKey' in made, false, 'the relay never returns the viewer key');
  const host = await client(made.roomId, 'host', made.hostKey); assert.equal((await host.next('ready')).role, 'host');
  const viewer = await client(made.roomId, 'viewer', key('v')); await viewer.next('ready');
  host.send(JSON.stringify(snapshot())); assert.equal((await viewer.next('state')).snapshot.seq, 1);
  assert.equal((await create({ name, viewerKey: key('w'), public: false, locked: true })).status, 409);
  const wrong = await client(made.roomId, 'viewer', key('w')); assert.equal((await closed(wrong))[0], 1008);
  host.close(); viewer.close();
});

test('lobbies: invalid bodies are rejected with 400', async () => {
  const ok = { name: unique('iv'), viewerKey: key('v'), public: true, locked: false };
  for (const bad of [{ ...ok, name: 'ab' }, { ...ok, name: 'a b c' }, { ...ok, viewerKey: 'short' }, { ...ok, viewerKey: key('v') + '!' },
                     { ...ok, public: 'yes' }, { ...ok, locked: undefined }]) {
    assert.equal((await create(bad)).status, 400, JSON.stringify(bad));
  }
  const notJson = await fetch(http() + '/lobbies', { method: 'POST', body: '{' });
  assert.equal(notJson.status, 400);
  const tooBig = await fetch(http() + '/lobbies', { method: 'POST', body: JSON.stringify({ ...ok, pad: 'x'.repeat(2000) }) });
  assert.equal(tooBig.status, 400);
});

const lobbies = async () => { const r = await fetch(http() + '/lobbies'); assert.equal(r.status, 200); return (await r.json()).lobbies; };
// Directory updates are reported asynchronously by the room; poll briefly.
async function eventually(check) {
  let last;
  for (let i = 0; i < 40; i++) { try { return await check(); } catch (e) { last = e; await new Promise(r => setTimeout(r, 100)); } }
  throw last;
}

test('lobbies: public lobbies are listed with live host, viewer and profile info; private ones are not', async () => {
  const pub = unique('pu'), priv = unique('pr');
  const made = await (await create({ name: pub, viewerKey: key('v'), public: true, locked: true })).json();
  await create({ name: priv, viewerKey: key('v'), public: false, locked: false });
  const first = await eventually(async () => { const e = (await lobbies()).find(l => l.name === pub); assert.ok(e); return e; });
  assert.deepEqual({ ...first, updated: 0 }, { name: pub, locked: true, hostOnline: false, viewers: 0, game: null, category: null, label: null, updated: 0 });
  assert.equal((await lobbies()).some(l => l.name === priv), false);
  const host = await client(made.roomId, 'host', made.hostKey); await host.next('ready');
  host.send(JSON.stringify({ ...snapshot(), profileLabel: 'Duo / Fullgame' }));
  const viewer = await client(made.roomId, 'viewer', key('v')); await viewer.next('ready');
  await eventually(async () => {
    const e = (await lobbies()).find(l => l.name === pub);
    assert.equal(e.hostOnline, true); assert.equal(e.viewers, 1); assert.equal(e.game, 'Test'); assert.equal(e.category, 'Coop'); assert.equal(e.label, 'Duo / Fullgame');
  });
  host.close();
  await eventually(async () => assert.equal((await lobbies()).find(l => l.name === pub).hostOnline, false));
  viewer.close();
  await eventually(async () => assert.equal((await lobbies()).find(l => l.name === pub).viewers, 0));
});

test('lobbies: ten wrong passwords lock that address out of this lobby, even with the right one, but not out of others', async () => {
  const made = await (await create({ name: unique('lk'), viewerKey: key('v'), public: false, locked: true })).json();
  const other = await (await create({ name: unique('ot'), viewerKey: key('v'), public: false, locked: true })).json();
  for (let i = 0; i < 10; i++) {
    const [code, reason] = await closed(await client(made.roomId, 'viewer', key('w')));
    assert.equal(code, 1008); assert.match(reason, /Authentication failed/);
  }
  const [code, reason] = await closed(await client(made.roomId, 'viewer', key('v')));
  assert.equal(code, 1008); assert.match(reason, /Too many attempts/);
  const fine = await client(other.roomId, 'viewer', key('v')); await fine.next('ready'); fine.close();
});

test('lobbies: a client cannot pick its own address header', async () => {
  const made = await (await create({ name: unique('hd'), viewerKey: key('v'), public: false, locked: true })).json();
  for (let i = 0; i < 10; i++) {
    const ws = new WebSocket(`${dev.base}/coop/${made.roomId}`, { headers: { 'X-Coop-IP': 'spoof-' + i } });
    await once(ws, 'open'); ws.send(JSON.stringify({ type: 'hello', v: 3, role: 'viewer', key: key('w') })); await closed(ws);
  }
  const [, reason] = await closed(await client(made.roomId, 'viewer', key('v')));
  assert.match(reason, /Too many attempts/, 'spoofed headers must not spread failures over fake addresses');
});
