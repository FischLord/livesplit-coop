# Plan 1: Relay-Lobbys (Cloudflare) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Der Cloudflare-Relay bietet benannte Lobbys (`POST /lobbies`), ein öffentliches Verzeichnis (`GET /lobbies`) und eine Fehlversuchssperre pro IP und Raum – ohne Änderung am WebSocket-Protokoll v3.

**Architecture:** Eine Lobby ist ein normaler `Room`-Durable-Object mit Raum-ID `lobby_<name>` und zusätzlichem `meta.lobby = {name, public, locked}`. Der Viewer-Schlüssel wird vom Client aus dem Passwort abgeleitet und hier nur gehasht gespeichert. Öffentliche Lobbys melden sich bei einem neuen `Directory`-Durable-Object (`"global"`). Reine Logik (Namensprüfung, Listenfilter) liegt in `src/directory.mjs` und ist ohne Wrangler testbar.

**Tech Stack:** Cloudflare Workers + Durable Objects (SQLite-Klassen, Hibernation-API), Wrangler 4, `node --test`, `ws`.

Spec: `docs/superpowers/specs/2026-10-12-lobbies-design.md`, Abschnitt 2.

**Arbeitsverzeichnis:** `C:\home\repos\livesplit-coop` (Branch `feature/lobbies`). Shell: Git Bash. Nie `wrangler deploy` ausführen.

---

## File Structure

- Create `relay-cloudflare/src/directory.mjs` – `normalizeName`, `visible` (pure Funktionen).
- Modify `relay-cloudflare/src/index.mjs` – Routen `/lobbies`, IP-Weitergabe, `Directory`-Klasse, `Room`: `exists`, `init` mit Lobby-Meta, `report`, Fehlversuchssperre.
- Modify `relay-cloudflare/wrangler.jsonc` – `DIRECTORY`-Binding + Migration `v2` (beide Envs).
- Create `relay-cloudflare/test/dev.mjs` – startet/stoppt `wrangler dev` (aus `worker.test.mjs` herausgezogen).
- Modify `relay-cloudflare/test/worker.test.mjs` – nutzt `dev.mjs`.
- Create `relay-cloudflare/test/directory.test.mjs` – Unit-Tests der pure Funktionen.
- Create `relay-cloudflare/test/lobbies.test.mjs` – Integrationstests gegen `wrangler dev`.
- Modify `docs/protocol.md` – Abschnitt „Lobbies“.

---

### Task 1: Baseline lauffähig machen und Dev-Helper extrahieren

**Files:**
- Create: `relay-cloudflare/test/dev.mjs`
- Modify: `relay-cloudflare/test/worker.test.mjs`

- [ ] **Step 1: Abhängigkeiten installieren**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && npm ci && cd ../relay && npm ci`
Expected: beide ohne Fehler (relay hat `node_modules` evtl. schon).

- [ ] **Step 2: Baseline-Tests laufen lassen**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && npm test`
Expected: alle Conformance-Tests PASS. Falls `wrangler dev` nicht startet: Fehlerausgabe melden (BLOCKED), nichts umbauen.

- [ ] **Step 3: `test/dev.mjs` anlegen**

```js
// Starts `wrangler dev --env test` on a free local port for integration tests and stops the whole process tree afterwards.
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';

const freePort = () => new Promise(resolve => { const s = createServer().listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)); }); });

export async function startDev() {
  const port = await freePort();
  const dev = spawn('npx', ['wrangler', 'dev', '--env', 'test', '--ip', '127.0.0.1', '--port', String(port), '--persist-to', '.wrangler/test-state'],
    { cwd: new URL('..', import.meta.url), shell: process.platform === 'win32', env: { ...process.env, WRANGLER_SEND_METRICS: 'false' } });
  let output = '';
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('wrangler dev did not start:\n' + output)), 90000);
    const watch = data => { output += data; if (/Ready on/.test(output)) { clearTimeout(timer); resolve(); } };
    dev.stdout.on('data', watch); dev.stderr.on('data', watch);
    dev.on('exit', code => { clearTimeout(timer); reject(new Error(`wrangler dev exited (${code}):\n` + output)); });
  });
  return {
    base: `ws://127.0.0.1:${port}`,
    // npx under a shell on Windows leaves workerd running unless the whole tree is stopped.
    stop() { if (process.platform === 'win32') spawn('taskkill', ['/pid', String(dev.pid), '/T', '/F']); else dev.kill(); }
  };
}
```

- [ ] **Step 4: `test/worker.test.mjs` ersetzen**

```js
// Runs the shared protocol conformance suite against the Worker in the local workerd runtime.
import { test, before, after } from 'node:test';
import { conformance } from '../../relay/test/conformance.mjs';
import { startDev } from './dev.mjs';

let dev;
before(async () => { dev = await startDev(); }, { timeout: 120000 });
after(() => dev?.stop());
conformance(test, () => dev.base);
```

- [ ] **Step 5: Tests erneut laufen lassen**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && npm test`
Expected: gleiche Anzahl PASS wie in Step 2.

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add relay-cloudflare/test/dev.mjs relay-cloudflare/test/worker.test.mjs
git commit -m "Relay tests: extract wrangler dev helper

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Pure Lobby-Logik (`directory.mjs`)

**Files:**
- Create: `relay-cloudflare/src/directory.mjs`
- Test: `relay-cloudflare/test/directory.test.mjs`

- [ ] **Step 1: Failing test schreiben**

```js
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { normalizeName, visible, LIST_MAX } from '../src/directory.mjs';

test('normalizeName trims, lowercases and enforces 3-32 of [a-z0-9_-]', () => {
  assert.equal(normalizeName('  RvGang '), 'rvgang');
  assert.equal(normalizeName('team_1-a'), 'team_1-a');
  assert.equal(normalizeName('ab'), null);
  assert.equal(normalizeName('a'.repeat(33)), null);
  assert.equal(normalizeName('a b c'), null);
  assert.equal(normalizeName('bäh'), null);
  assert.equal(normalizeName(42), null);
  assert.equal(normalizeName(undefined), null);
});

test('visible hides entries older than 24 h, sorts online first then newest, and strips unknown fields', () => {
  const now = 10 * 86400000;
  const e = (name, hostOnline, ago) => ({ name, locked: true, hostOnline, viewers: 1, game: 'G', category: 'C', label: null, updated: now - ago, secret: 'x' });
  const list = visible([e('old', true, 86400000), e('a', false, 1000), e('b', true, 5000), e('c', true, 2000)], now);
  assert.deepEqual(list.map(x => x.name), ['c', 'b', 'a']);
  assert.equal('secret' in list[0], false);
  assert.deepEqual(Object.keys(list[0]).sort(), ['category', 'game', 'hostOnline', 'label', 'locked', 'name', 'updated', 'viewers']);
});

test('visible returns at most LIST_MAX entries', () => {
  const now = 1000000;
  const many = Array.from({ length: LIST_MAX + 5 }, (_, i) => ({ name: 'n' + i, locked: false, hostOnline: false, viewers: 0, game: null, category: null, label: null, updated: now - i }));
  assert.equal(visible(many, now).length, LIST_MAX);
});
```

- [ ] **Step 2: Test laufen lassen, muss scheitern**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && node --test test/directory.test.mjs`
Expected: FAIL (`Cannot find module ... directory.mjs`).

- [ ] **Step 3: Implementierung**

```js
// Pure lobby helpers shared by the Worker and its unit tests (no Cloudflare imports here).
export const LIST_MAX = 100, LIST_WINDOW = 86400000;

// Lobby names are typed by people: case and surrounding spaces do not matter, everything else must match exactly.
export function normalizeName(raw) {
  if (typeof raw !== 'string') return null;
  const name = raw.trim().toLowerCase();
  return /^[a-z0-9_-]{3,32}$/.test(name) ? name : null;
}

// Public directory view: recently active lobbies, hosts online first, newest first, only the published fields.
export function visible(entries, now) {
  return entries
    .filter(e => now - e.updated < LIST_WINDOW)
    .sort((a, b) => (Number(b.hostOnline) - Number(a.hostOnline)) || (b.updated - a.updated))
    .slice(0, LIST_MAX)
    .map(({ name, locked, hostOnline, viewers, game, category, label, updated }) => ({ name, locked, hostOnline, viewers, game, category, label, updated }));
}
```

- [ ] **Step 4: Test laufen lassen**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && node --test test/directory.test.mjs`
Expected: 3 PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add relay-cloudflare/src/directory.mjs relay-cloudflare/test/directory.test.mjs
git commit -m "Relay: lobby name normalisation and directory listing rules

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `POST /lobbies` – Lobby erstellen

**Files:**
- Modify: `relay-cloudflare/src/index.mjs`
- Modify: `relay-cloudflare/wrangler.jsonc`
- Test: `relay-cloudflare/test/lobbies.test.mjs`

- [ ] **Step 1: Failing Integrationstest schreiben** (`test/lobbies.test.mjs`)

```js
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
```

- [ ] **Step 2: Test laufen lassen, muss scheitern**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && node --test test/lobbies.test.mjs`
Expected: FAIL – Status 404 statt 201.

- [ ] **Step 3: `index.mjs` erweitern**

Import ergänzen (Zeile 2 folgend):

```js
import { normalizeName, visible } from './directory.mjs';
```

In `fetch` direkt nach der `/rooms`-Zeile einfügen:

```js
    if (request.method === 'POST' && url.pathname === '/lobbies' && !url.search && !request.headers.get('Origin')) return createLobby(request, env);
```

Nach `createRoom` neue Funktion:

```js
// A lobby is a room with a chosen name. The client derives the viewer key from the lobby password,
// so the relay only ever sees (and stores the hash of) that key, never the password.
async function createLobby(request, env) {
  if (env.ROOM_CREATION !== '1') return json(404);
  let body;
  try { const text = await request.text(); if (text.length > 1024) return json(400); body = JSON.parse(text); } catch { return json(400); }
  const name = normalizeName(body?.name);
  if (!name || typeof body.viewerKey !== 'string' || !/^[A-Za-z0-9_-]{32,256}$/.test(body.viewerKey) ||
      typeof body.public !== 'boolean' || typeof body.locked !== 'boolean') return json(400);
  const { success } = await env.CREATE_LIMIT.limit({ key: request.headers.get('CF-Connecting-IP') || 'unknown' });
  if (!success) return json(429, undefined, { 'Retry-After': '60' });
  const roomId = 'lobby_' + name, room = env.ROOMS.get(env.ROOMS.idFromName(roomId));
  if (await room.exists()) return json(409);
  if (!await env.QUOTA.get(env.QUOTA.idFromName('global')).take(Number(env.ROOMS_PER_DAY) || 0)) return json(503);
  const hostKey = token(32);
  if (!await room.init(hex(await digest(hostKey)), hex(await digest(body.viewerKey)), { name, public: body.public, locked: body.locked })) return json(409);
  return json(201, { roomId, hostKey, idleExpiryDays: IDLE_MS / DAY });
}
```

In `class Room` `init` ersetzen und `exists` ergänzen:

```js
  exists() { return !!this.meta; }

  // Returns false when the room already exists (a lobby name that is taken).
  async init(hostHash, viewerHash, lobby = null) {
    if (this.meta) return false;
    this.meta = lobby ? { hostHash, viewerHash, lobby } : { hostHash, viewerHash };
    await this.ctx.storage.put('meta', this.meta);
    await this.touch();
    await this.wakeAt(Date.now() + IDLE_MS);
    await this.report();
    return true;
  }
```

Vorläufig (Task 4 ersetzt es) in `class Room` ergänzen:

```js
  async report() {}
```

- [ ] **Step 4: Tests laufen lassen**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && npm test`
Expected: Lobby-Tests 2 PASS, Conformance unverändert PASS, directory 3 PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add relay-cloudflare/src/index.mjs relay-cloudflare/test/lobbies.test.mjs
git commit -m "Relay: create named lobbies with a client-derived viewer key

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Öffentliches Verzeichnis (`GET /lobbies`)

**Files:**
- Modify: `relay-cloudflare/src/index.mjs`
- Modify: `relay-cloudflare/wrangler.jsonc`
- Test: `relay-cloudflare/test/lobbies.test.mjs`

- [ ] **Step 1: Failing Test anhängen** (an `test/lobbies.test.mjs`)

```js
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
```

- [ ] **Step 2: Test laufen lassen, muss scheitern**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && node --test test/lobbies.test.mjs`
Expected: FAIL – `GET /lobbies` liefert 404.

- [ ] **Step 3: Binding und Migration in `wrangler.jsonc`**

Top-Level `durable_objects.bindings` und `env.test.durable_objects.bindings` je um
`{ "name": "DIRECTORY", "class_name": "Directory" }` ergänzen. `migrations` wird zu:

```jsonc
  "migrations": [
    { "tag": "v1", "new_sqlite_classes": ["Room", "Quota"] },
    { "tag": "v2", "new_sqlite_classes": ["Directory"] }
  ],
```

- [ ] **Step 4: Route, `Directory`-Klasse und `Room.report` in `index.mjs`**

In `fetch` nach der `POST /lobbies`-Zeile:

```js
    if (request.method === 'GET' && url.pathname === '/lobbies' && !url.search) return json(200, { lobbies: await env.DIRECTORY.get(env.DIRECTORY.idFromName('global')).list() });
```

Nach `class Quota`:

```js
// Public lobby directory; one instance named "global". Rooms publish their own entry.
export class Directory extends DurableObject {
  async put(name, entry) { await this.ctx.storage.put('l:' + name, entry); }
  async remove(name) { await this.ctx.storage.delete('l:' + name); }
  async list() {
    const now = Date.now();
    const all = [...(await this.ctx.storage.list({ prefix: 'l:' })).values()];
    // Rooms remove their entry when they expire; this only sweeps leftovers.
    const stale = all.filter(e => now - e.updated >= IDLE_MS + DAY).map(e => 'l:' + e.name).slice(0, 128);
    if (stale.length) await this.ctx.storage.delete(stale);
    return visible(all, now);
  }
}
```

In `class Room` den Platzhalter `async report() {}` ersetzen:

```js
  directory() { return this.env.DIRECTORY.get(this.env.DIRECTORY.idFromName('global')); }
  // Publishes this lobby's public entry. Private lobbies and plain rooms are never listed.
  async report() {
    const lobby = this.meta?.lobby;
    if (!lobby?.public) return;
    const s = this.snapshot;
    await this.directory().put(lobby.name, {
      name: lobby.name, locked: lobby.locked, hostOnline: !!this.host(), viewers: this.viewers().length,
      game: s?.game ?? null, category: s?.category ?? null,
      label: typeof s?.profileLabel === 'string' ? s.profileLabel.slice(0, 60) : null, updated: Date.now()
    });
  }
```

Aufrufstellen:
- In `hello`, direkt nach `ws.send(JSON.stringify({ type: 'ready', ... }))`: `await this.report();`
- In `left`, im Block `if (a.role === 'host' || a.role === 'viewer' || a.role === 'gone') { ... }` am Ende: `await this.report();`
- In `webSocketMessage`, nach `await this.ctx.storage.put('snapshot', m);`:

```js
    const reported = [m.game, m.category, m.profileLabel].join('|');
    if (reported !== this.reported) { this.reported = reported; await this.report(); }
```

- In `alarm`, im Ablauf-Zweig **vor** `await this.ctx.storage.deleteAll();`:

```js
        if (this.meta.lobby?.public) await this.directory().remove(this.meta.lobby.name);
```

- [ ] **Step 5: Tests laufen lassen**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && npm test`
Expected: alle PASS (Conformance, directory, 3 Lobby-Tests).

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add relay-cloudflare/src/index.mjs relay-cloudflare/wrangler.jsonc relay-cloudflare/test/lobbies.test.mjs
git commit -m "Relay: public lobby directory with live host, viewer and profile info

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Fehlversuchssperre pro IP und Raum

**Files:**
- Modify: `relay-cloudflare/src/index.mjs`
- Test: `relay-cloudflare/test/lobbies.test.mjs`

- [ ] **Step 1: Failing Test anhängen**

```js
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
```

- [ ] **Step 2: Test laufen lassen, muss scheitern**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && node --test test/lobbies.test.mjs`
Expected: FAIL – richtiger Schlüssel bekommt `ready` statt Sperre.

- [ ] **Step 3: Implementierung**

Konstanten in Zeile 7 ergänzen: `FAIL_MAX = 10, FAIL_WINDOW = 60000`.

In `fetch` des Workers die Zeile `return env.ROOMS.get(env.ROOMS.idFromName(path[1])).fetch(request);` ersetzen:

```js
    // The room counts failed logins per address; a client-supplied header is always overwritten.
    const headers = new Headers(request.headers);
    headers.set('X-Coop-IP', request.headers.get('CF-Connecting-IP') || 'unknown');
    return env.ROOMS.get(env.ROOMS.idFromName(path[1])).fetch(new Request(request, { headers }));
```

`Room.fetch()` bekommt den Parameter `request` und speichert die Adresse:

```js
  async fetch(request) {
```

und die Attachment-Zeile wird:

```js
    server.serializeAttachment({ since: now, count: 0, window: now, ip: request.headers.get('X-Coop-IP') || 'unknown' });
```

In `hello` nach der `if (!this.meta) { ... 4004 ... }`-Zeile den Schlüsselvergleich ersetzen:

```js
    // Lobby passwords can be short, so failed logins are limited per address and room.
    // A locked-out address is refused before the key check, so a correct guess stays invisible.
    const failKey = 'fail:' + (a.ip || 'unknown'), now = Date.now();
    const fail = await this.ctx.storage.get(failKey);
    const recent = fail && now - fail.since < FAIL_WINDOW ? fail : null;
    if (recent && recent.count >= FAIL_MAX) { ws.close(1008, 'Too many attempts'); return; }
    const expected = m.role === 'host' ? this.meta.hostHash : this.meta.viewerHash;
    if (typeof m.key !== 'string' || m.key.length < 32 || m.key.length > 256 ||
        !crypto.subtle.timingSafeEqual(await digest(m.key), Uint8Array.from(expected.match(/../g), h => parseInt(h, 16)))) {
      await this.ctx.storage.put(failKey, { since: recent ? recent.since : now, count: (recent ? recent.count : 0) + 1 });
      ws.close(1008, 'Authentication failed'); return;
    }
```

(Die bisherige `const expected = ...`-Zeile und der alte `if (typeof m.key ...)`-Block entfallen dadurch.)

- [ ] **Step 4: Tests laufen lassen**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && npm test`
Expected: alle PASS. Hinweis: Lokal ist die Adresse für alle Clients `unknown`, die Sperre wirkt aber pro Raum, deshalb beeinflussen sich die Tests nicht.

- [ ] **Step 5: Node-Relay-Tests unverändert grün**

Run: `cd /c/home/repos/livesplit-coop/relay && npm test`
Expected: PASS (keine Änderung dort).

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add relay-cloudflare/src/index.mjs relay-cloudflare/test/lobbies.test.mjs
git commit -m "Relay: lock an address out of a lobby after ten failed logins per minute

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Protokoll-Doku

**Files:**
- Modify: `docs/protocol.md` (am Ende anhängen)

- [ ] **Step 1: Abschnitt anhängen**

```markdown
## Lobbies (Cloudflare relay)

A lobby is a room with a chosen name. The WebSocket protocol is unchanged (v3); only creation differs.

- Name: 3–32 characters `[a-z0-9_-]`; the relay trims and lowercases. Room ID: `lobby_<name>`.
- Viewer key: derived by the client, `base64url(PBKDF2-HMAC-SHA256(password, "livesplit-coop-lobby:v1:" + name, 100000, 32 bytes))`, no padding. An empty password is allowed. The password never leaves the client.
- `POST /lobbies` with `{"name","viewerKey","public","locked"}` (max. 1 KiB, no `Origin` header) → `201 {"roomId","hostKey","idleExpiryDays"}`; `409` name taken; `400` invalid; `429`/`503` as for `/rooms`.
- `GET /lobbies` → `200 {"lobbies":[{"name","locked","hostOnline","viewers","game","category","label","updated"}]}`: public lobbies active within 24 h, hosts online first, at most 100. `label` is the host snapshot's optional `profileLabel`.
- Ten failed logins within 60 s from one address lock that address out of that room for the rest of the window (`1008 Too many attempts`), regardless of the key.
- Lobbies expire like rooms (7 days unused); the name becomes free again.
- Relays without lobby support answer `404` to `/lobbies`; clients fall back to invitations.
```

- [ ] **Step 2: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add docs/protocol.md
git commit -m "Docs: lobby endpoints and key derivation

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```
