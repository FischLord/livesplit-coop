// Protocol v3 behaviour every relay must share. Runs against the Node relay and the Cloudflare Worker
// (relay-cloudflare/test). `base` is the ws:// origin; rooms are created through POST /rooms.
import assert from 'node:assert/strict';
import { once } from 'node:events';
import WebSocket from 'ws';

export function snapshot(seq = 1) {
  return {type:'snapshot',v:3,seq,runId:'run1',attemptId:'attempt1',game:'Test',category:'Coop',attempts:1,
    phase:'Running',index:1,timingMethod:'RealTime',comparison:'Personal Best',offsetTicks:0,
    realTicks:150000000,gameTicks:null,gamePaused:false,
    segments:[0,1,2].map(i=>({name:`CP${i}`,splitRT:i===0?100000000:null,splitGT:null,pbRT:(i+1)*120000000,pbGT:null,bestRT:100000000,bestGT:null,comparisons:{}}))};
}

export function conformance(test, getBase) {
  const http = () => getBase().replace('ws:', 'http:');
  async function room() {
    const response = await fetch(http() + '/rooms', { method: 'POST' });
    assert.equal(response.status, 201);
    return response.json();
  }
  async function client(made, role = 'viewer', key = role === 'host' ? made.hostKey : made.viewerKey, id = made.roomId) {
    const ws = new WebSocket(`${getBase()}/coop/${id}`); const queue = [], waiters = [];
    ws.on('message', data => { const m = JSON.parse(data); const i = waiters.findIndex(w => w.type === m.type); if (i < 0) queue.push(m); else waiters.splice(i, 1)[0].resolve(m); });
    ws.next = type => new Promise((resolve, reject) => {
      const index = queue.findIndex(m => m.type === type); if (index >= 0) { resolve(queue.splice(index, 1)[0]); return; }
      const timer = setTimeout(() => reject(new Error('Timeout waiting for ' + type)), 5000);
      waiters.push({ type, resolve: m => { clearTimeout(timer); resolve(m); } });
    });
    await once(ws, 'open'); ws.send(JSON.stringify({ type: 'hello', v: 3, role, key })); return ws;
  }
  const closed = async ws => { const [code, reason] = await once(ws, 'close'); return [code, reason.toString()]; };

  test('conformance: created room carries snapshot to two viewers and a late joiner', async () => {
    const made = await room();
    assert.match(made.roomId, /^[A-Za-z0-9_-]{22}$/); assert.notEqual(made.hostKey, made.viewerKey); assert.equal(made.idleExpiryDays, 7);
    const host = await client(made, 'host'); assert.equal((await host.next('ready')).role, 'host');
    const a = await client(made), b = await client(made); await Promise.all([a.next('ready'), b.next('ready')]);
    const state = snapshot(); host.send(JSON.stringify(state));
    for (const viewer of [a, b]) assert.deepEqual((await viewer.next('state')).snapshot, state);
    const late = await client(made); const cached = await late.next('state'); assert.deepEqual(cached.snapshot, state); assert.equal(cached.live, true);
    for (const ws of [host, a, b, late]) ws.close();
  });
  test('conformance: ticks move only the clock and late joiners get the merged snapshot', async () => {
    const made = await room(); const host = await client(made, 'host'); await host.next('ready');
    const viewer = await client(made); await viewer.next('ready'); await viewer.next('host');
    host.send(JSON.stringify({ type: 'tick', v: 3, seq: 1, realTicks: 1, gameTicks: null, gamePaused: false }));
    assert.equal((await closed(host))[0], 1008, 'tick before snapshot');
    const next = await client(made, 'host'); await next.next('ready');
    next.send(JSON.stringify(snapshot(1))); await viewer.next('state');
    next.send(JSON.stringify({ type: 'tick', v: 3, seq: 2, realTicks: 160000000, gameTicks: null, gamePaused: false }));
    assert.deepEqual(await viewer.next('tick'), { type: 'tick', seq: 2, realTicks: 160000000, gameTicks: null, gamePaused: false });
    const late = await client(made); assert.deepEqual((await late.next('state')).snapshot, { ...snapshot(2), realTicks: 160000000 });
    for (const ws of [next, viewer, late]) ws.close();
  });
  test('conformance: wrong keys, viewer publishing, old clients and unknown rooms are refused', async () => {
    const made = await room(), other = await room();
    assert.equal((await closed(await client(made, 'viewer', 'x'.repeat(43))))[0], 1008);
    assert.equal((await closed(await client(made, 'viewer', other.viewerKey)))[0], 1008, 'keys are per room');
    assert.equal((await closed(await client(made, 'host', made.viewerKey)))[0], 1008, 'viewer key cannot host');
    const viewer = await client(made); await viewer.next('ready'); viewer.send(JSON.stringify(snapshot()));
    assert.equal((await closed(viewer))[0], 1008);
    const [code, reason] = await closed(await client(made, 'viewer', made.viewerKey, 'nosuchroom'));
    assert.equal(code, 4004); assert.match(reason, /expired or unknown/);
    for (const v of [1, 2]) {
      const old = new WebSocket(`${getBase()}/coop`); await once(old, 'open');
      old.send(JSON.stringify({ type: 'hello', v, room: 'coop', role: 'viewer', key: made.viewerKey }));
      const [c, r] = await closed(old); assert.equal(c, 1008); assert.match(r, /protocol version/);
    }
    const bare = new WebSocket(`${getBase()}/coop`); await once(bare, 'open');
    bare.send(JSON.stringify({ type: 'hello', v: 3, role: 'viewer', key: made.viewerKey })); assert.equal((await closed(bare))[0], 1008);
  });
  test('conformance: a newer host replaces the old one with 4001 while viewers stay connected', async () => {
    const made = await room(); const first = await client(made, 'host'); await first.next('ready');
    const viewer = await client(made); await viewer.next('ready'); await viewer.next('host');
    first.send(JSON.stringify(snapshot(5))); await viewer.next('state');
    const replaced = closed(first); const second = await client(made, 'host'); await second.next('ready');
    const [code, reason] = await replaced; assert.equal(code, 4001); assert.match(reason, /Replaced/);
    second.send(JSON.stringify(snapshot(1))); assert.equal((await viewer.next('state')).snapshot.seq, 1, 'new host starts its own sequence');
    assert.equal(viewer.readyState, WebSocket.OPEN);
    second.close(); viewer.close();
  });
  test('conformance: host loss is announced and the final result stays available offline', async () => {
    const made = await room(); const host = await client(made, 'host'); await host.next('ready');
    const viewer = await client(made); await viewer.next('ready'); assert.equal((await viewer.next('host')).online, true);
    const final = snapshot(); final.phase = 'Ended'; final.index = 3; final.realTicks = 300000001;
    final.segments.forEach((s, i) => { s.splitRT = (i + 1) * 100000000; }); final.segments[2].splitRT = 300000001;
    host.send(JSON.stringify(final)); await viewer.next('state');
    host.close(); assert.equal((await viewer.next('host')).online, false);
    const late = await client(made); assert.equal((await late.next('host')).online, false);
    const cached = await late.next('state'); assert.equal(cached.live, false); assert.deepEqual(cached.snapshot, final);
    viewer.close(); late.close();
  });
  test('conformance: ping, health and refused paths', async () => {
    const made = await room(); const viewer = await client(made); await viewer.next('ready');
    viewer.send('{"type":"ping"}'); await viewer.next('pong'); viewer.close();
    const health = await fetch(http() + '/healthz'); assert.equal(health.status, 200); assert.deepEqual(await health.json(), { ok: true });
    assert.equal((await fetch(http() + '/rooms', { method: 'POST', headers: { Origin: 'https://example.com' } })).status, 404, 'browsers cannot create rooms');
    for (const path of [`/coop/${made.roomId}?key=secret`, '/coop/', '/coop/a/b', '/other']) {
      const ws = new WebSocket(getBase() + path); const [error] = await once(ws, 'error'); assert.match(error.message, /40[34]/, path);
    }
  });
}
