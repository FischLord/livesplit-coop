import { DurableObject } from 'cloudflare:workers';
import { PROTOCOL, validSnapshot, validTick } from '../../relay/src/protocol.mjs';
import { normalizeName, visible, addressBucket, cleanLabel } from './directory.mjs';

// Same wire protocol as relay/src/server.mjs, rebuilt on the Durable Object hibernation API:
// one object per room, no timers (alarms only), state that must survive hibernation lives in
// storage (keys, last complete snapshot) or in per-socket attachments (role, sequence, last tick).
const DAY = 86400000, IDLE_MS = 7 * DAY, AUTH_MS = 5000, MAX_PENDING = 16, MAX_ROOM = 12, FAIL_MAX = 10, FAIL_WINDOW = 60000;
const REPORT_GAP = 10000, REPORT_REFRESH = 3600000, LIST_CACHE = 5000, BODY_MAX = 1024;
const UPDATE = 'Unsupported protocol version; update LiveSplit Coop';
const secure = { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' };
const json = (status, body, headers = {}) => new Response(body === undefined ? null : JSON.stringify(body),
  { status, headers: { ...secure, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }), ...headers } });
const token = bytes => btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(bytes)))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
const digest = async key => new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(key)));
const hex = bytes => [...bytes].map(b => b.toString(16).padStart(2, '0')).join('');

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (request.method === 'GET' && url.pathname === '/healthz' && !url.search) return json(200, { ok: true });
    // Browsers could send this cross-site; only native clients may create rooms.
    if (request.method === 'POST' && url.pathname === '/rooms' && !url.search && !request.headers.get('Origin')) return createRoom(request, env);
    if (request.method === 'POST' && url.pathname === '/lobbies' && !url.search && !request.headers.get('Origin')) return createLobby(request, env);
    if (request.method === 'GET' && url.pathname === '/lobbies' && !url.search) return json(200, { lobbies: await env.DIRECTORY.get(env.DIRECTORY.idFromName('global')).list() });
    const path = /^\/coop(?:\/([a-zA-Z0-9_-]{1,64}))?$/.exec(url.pathname);
    if (!path) return json(404);
    // Native client only. Browsers and URL credentials are deliberately unsupported.
    if (url.search || request.headers.get('Origin') || request.headers.get('Upgrade')?.toLowerCase() !== 'websocket') return json(403);
    if (!path[1]) return legacy();
    // The room counts failed logins per address; a client-supplied header is always overwritten.
    const headers = new Headers(request.headers);
    headers.set('X-Coop-IP', request.headers.get('CF-Connecting-IP') || 'unknown');
    return env.ROOMS.get(env.ROOMS.idFromName(path[1])).fetch(new Request(request, { headers }));
  }
};

async function createRoom(request, env) {
  if (env.ROOM_CREATION !== '1') return json(404);
  const { success } = await env.CREATE_LIMIT.limit({ key: request.headers.get('CF-Connecting-IP') || 'unknown' });
  if (!success) return json(429, undefined, { 'Retry-After': '60' });
  if (!await env.QUOTA.get(env.QUOTA.idFromName('global')).take(Number(env.ROOMS_PER_DAY) || 0)) return json(503);
  const roomId = token(16), hostKey = token(32), viewerKey = token(32);
  await env.ROOMS.get(env.ROOMS.idFromName(roomId)).init(hex(await digest(hostKey)), hex(await digest(viewerKey)));
  return json(201, { roomId, hostKey, viewerKey, idleExpiryDays: IDLE_MS / DAY });
}

// A lobby is a room with a chosen name. The client derives the viewer key from the lobby password,
// so the relay only ever sees (and stores the hash of) that key, never the password.
async function createLobby(request, env) {
  if (env.ROOM_CREATION !== '1') return json(404);
  if (Number(request.headers.get('Content-Length')) > BODY_MAX) return json(400);
  let body;
  try { const text = await readCapped(request, BODY_MAX); if (text === null || text.length > BODY_MAX) return json(400); body = JSON.parse(text); } catch { return json(400); }
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

// Reads at most `max` bytes of the body; null when it is larger (chunked bodies carry no Content-Length).
async function readCapped(request, max) {
  if (!request.body) return '';
  const reader = request.body.getReader(), chunks = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.length;
    if (size > max) { await reader.cancel(); return null; }
    chunks.push(value);
  }
  const all = new Uint8Array(size); let at = 0;
  for (const c of chunks) { all.set(c, at); at += c.length; }
  return new TextDecoder().decode(all);
}

// Bare /coop only exists so clients older than v3 learn that they must update.
function legacy() {
  const [client, server] = Object.values(new WebSocketPair());
  server.accept();
  const deadline = setTimeout(() => server.close(1008, 'Authentication required'), AUTH_MS);
  server.addEventListener('message', event => {
    clearTimeout(deadline);
    let m; try { m = JSON.parse(event.data); } catch { m = null; }
    server.close(1008, m?.type === 'hello' && m.v !== PROTOCOL ? UPDATE : 'Room missing from path');
  });
  server.addEventListener('close', () => clearTimeout(deadline));
  return new Response(null, { status: 101, webSocket: client });
}

// Global daily cap for room creation; one instance named "global".
export class Quota extends DurableObject {
  async take(limit) {
    const day = new Date().toISOString().slice(0, 10);
    const used = await this.ctx.storage.get('quota');
    const count = used?.day === day ? used.count : 0;
    if (count >= limit) return false;
    await this.ctx.storage.put('quota', { day, count: count + 1 });
    return true;
  }
}

// Public lobby directory; one instance named "global". Rooms publish their own entry.
export class Directory extends DurableObject {
  async put(name, entry) { await this.ctx.storage.put('l:' + name, entry); }
  async remove(name) { await this.ctx.storage.delete('l:' + name); }
  async list() {
    const now = Date.now();
    if (this.cache && now - this.cache.at < LIST_CACHE) return this.cache.value;
    const all = [...(await this.ctx.storage.list({ prefix: 'l:' })).values()];
    // Rooms remove their entry when they expire; this only sweeps leftovers.
    const stale = all.filter(e => now - e.updated >= IDLE_MS + DAY).map(e => 'l:' + e.name).slice(0, 128);
    if (stale.length) await this.ctx.storage.delete(stale);
    this.cache = { at: now, value: visible(all, now) };
    return this.cache.value;
  }
}

export class Room extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    // Answered by the runtime without waking the object, so idle rooms stay hibernated.
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair('{"type":"ping"}', '{"type":"pong"}'));
    ctx.blockConcurrencyWhile(async () => {
      this.meta = await ctx.storage.get('meta') ?? null;
      this.snapshot = await ctx.storage.get('snapshot') ?? null;
      // Ticks are never written to storage; the host's attachment carries the latest one.
      const host = this.host();
      if (host && this.snapshot && host.a.tick) this.snapshot = { ...this.snapshot, ...host.a.tick };
    });
  }
  sockets() {
    return this.ctx.getWebSockets().map(ws => ({ ws, a: ws.deserializeAttachment() || {} }));
  }
  host() { return this.sockets().find(s => s.a.role === 'host' && s.ws.readyState === WebSocket.OPEN); }
  viewers() { return this.sockets().filter(s => s.a.role === 'viewer' && s.ws.readyState === WebSocket.OPEN); }
  broadcast(msg) { const data = JSON.stringify(msg); for (const { ws } of this.viewers()) ws.send(data); }
  async wakeAt(time) {
    const current = await this.ctx.storage.getAlarm();
    if (current === null || current > time) await this.ctx.storage.setAlarm(time);
  }
  async touch() { await this.ctx.storage.put('active', Date.now()); }

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

  // Tells the host how many viewers follow it. Older clients ignore unknown message types.
  notifyHost(leaving = null) {
    const host = this.host();
    if (host) host.ws.send(JSON.stringify({ type: 'peers', viewers: this.viewers().filter(s => s.ws !== leaving).length }));
  }
  directory() { return this.env.DIRECTORY.get(this.env.DIRECTORY.idFromName('global')); }
  // Requests a directory update. Never throws and never blocks relaying: at most one write per
  // REPORT_GAP, a request inside that window is parked in storage and performed by alarm().
  async report(leaving = null) {
    try {
      if (!this.meta?.lobby?.public) return;
      const wait = (this.lastReport ?? 0) + REPORT_GAP - Date.now();
      if (wait > 0) {
        await this.ctx.storage.put('reportDue', true);
        await this.wakeAt(this.lastReport + REPORT_GAP);
      } else await this.publish(leaving);
    } catch (e) { console.error('directory report failed', e?.name); }
  }
  // Writes this lobby's public entry. Private lobbies and plain rooms are never listed. Returns success.
  async publish(leaving = null) {
    try {
      const lobby = this.meta?.lobby;
      if (!lobby?.public) return true;
      const s = this.snapshot, now = Date.now();
      await this.directory().put(lobby.name, {
        name: lobby.name, locked: lobby.locked, hostOnline: !!this.host(), viewers: this.viewers().filter(v => v.ws !== leaving).length,
        game: s?.game ?? null, category: s?.category ?? null, label: cleanLabel(s?.profileLabel), updated: now
      });
      this.lastReport = now;
      await this.ctx.storage.delete('reportDue');
      // A host that stays online keeps its entry fresh.
      if (this.host()) await this.wakeAt(now + REPORT_REFRESH);
      return true;
    } catch (e) { console.error('directory publish failed', e?.name); return false; }
  }

  async fetch(request) {
    const pending = this.sockets().filter(s => !s.a.role && s.ws.readyState === WebSocket.OPEN);
    // A flood must outpace a real client's hello instead of just holding sockets.
    if (pending.length >= MAX_PENDING) pending.reduce((a, b) => a.a.since <= b.a.since ? a : b).ws.close(1008, 'Authentication required');
    const [client, server] = Object.values(new WebSocketPair());
    this.ctx.acceptWebSocket(server);
    const now = Date.now();
    server.serializeAttachment({ since: now, count: 0, window: now, ip: request.headers.get('X-Coop-IP') || 'unknown' });
    await this.wakeAt(now + AUTH_MS);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, data) {
    const a = ws.deserializeAttachment() || {};
    const now = Date.now();
    if (now - a.window >= 1000) { a.count = 0; a.window = now; }
    if (++a.count > 30 || typeof data !== 'string' || data.length > 256 * 1024) { ws.serializeAttachment(a); ws.close(1008, 'Invalid traffic'); return; }
    let m;
    try { m = JSON.parse(data); } catch { ws.close(1008, 'Invalid JSON'); return; }
    if (!m || typeof m !== 'object') { ws.close(1008, 'Invalid message'); return; }
    if (!a.role) { await this.hello(ws, a, m); return; }
    ws.serializeAttachment(a);
    if (m.type === 'ping') { ws.send('{"type":"pong"}'); return; }
    if (a.role === 'replaced') return; // its close handshake is in progress
    if (a.role !== 'host') { ws.close(1008, 'Viewers cannot publish'); return; }
    if (m.type === 'tick') {
      if (!a.fresh) { ws.close(1008, 'Snapshot required before tick'); return; }
      if (!validTick(m)) { ws.close(1008, 'Invalid tick'); return; }
      if (m.seq <= a.seq) return;
      const tick = { seq: m.seq, realTicks: m.realTicks, gameTicks: m.gameTicks, gamePaused: m.gamePaused };
      const next = { ...this.snapshot, ...tick };
      if (!validSnapshot(next)) { ws.close(1008, 'Invalid tick'); return; }
      Object.assign(a, { seq: m.seq, tick, updated: now }); ws.serializeAttachment(a);
      this.snapshot = next;
      this.broadcast({ type: 'tick', ...tick });
      return;
    }
    if (!validSnapshot(m)) { ws.close(1008, 'Invalid snapshot'); return; }
    if (m.seq <= a.seq) return;
    Object.assign(a, { seq: m.seq, tick: null, fresh: true, updated: now }); ws.serializeAttachment(a);
    this.snapshot = m;
    // Only structural changes reach storage, so a running timer costs no writes.
    await this.ctx.storage.put('snapshot', m);
    const reported = [m.game, m.category, m.profileLabel].join('|');
    this.broadcast({ type: 'state', live: true, ageMs: 0, snapshot: m });
    if (reported !== this.reported) { this.reported = reported; await this.report(); }
  }

  async hello(ws, a, m) {
    if (m.type === 'hello' && m.v !== PROTOCOL) { ws.close(1008, UPDATE); return; }
    if (m.type !== 'hello' || !['host', 'viewer'].includes(m.role)) { ws.close(1008, 'Authentication failed'); return; }
    if (!this.meta) { ws.close(4004, 'Room expired or unknown'); return; }
    // Lobby passwords can be short, so failed logins are limited per address bucket and room.
    // A locked-out address is refused before the key check, so a correct guess stays invisible.
    const failKey = 'fail:' + addressBucket(a.ip), now = Date.now();
    const keyOk = typeof m.key === 'string' && m.key.length >= 32 && m.key.length <= 256;
    const given = keyOk ? await digest(m.key) : null; // no non-storage await between the counter's get and put
    const fail = await this.ctx.storage.get(failKey);
    const recent = fail && now - fail.since < FAIL_WINDOW ? fail : null;
    if (recent && recent.count >= FAIL_MAX) { ws.close(1008, 'Too many attempts'); return; }
    const expected = m.role === 'host' ? this.meta.hostHash : this.meta.viewerHash;
    if (!keyOk || !crypto.subtle.timingSafeEqual(given, Uint8Array.from(expected.match(/../g), h => parseInt(h, 16)))) {
      const since = recent ? recent.since : now;
      await this.ctx.storage.put(failKey, { since, count: (recent ? recent.count : 0) + 1 });
      await this.wakeAt(since + FAIL_WINDOW);
      ws.close(1008, 'Authentication failed'); return;
    }
    // The host always has a reserved slot; viewers cannot lock the publisher out.
    if (m.role === 'viewer' && this.viewers().length >= MAX_ROOM - 1) { ws.close(1008, 'Room occupied or full'); return; }
    const previous = m.role === 'host' ? this.host() : null;
    Object.assign(a, { role: m.role, seq: -1, fresh: false, tick: null, updated: 0 });
    ws.serializeAttachment(a);
    await this.touch();
    if (previous) {
      // The newest host connection wins, so a host whose previous socket silently died can publish again at once.
      previous.a.role = 'replaced'; previous.ws.serializeAttachment(previous.a);
      previous.ws.close(4001, 'Replaced by a newer host connection');
    }
    ws.send(JSON.stringify({ type: 'ready', v: PROTOCOL, role: m.role }));
    this.notifyHost();
    if (m.role === 'viewer') {
      const host = this.host();
      ws.send(JSON.stringify({ type: 'host', online: !!host }));
      if (this.snapshot) {
        const age = host?.a.updated ? Date.now() - host.a.updated : Date.now() - (await this.ctx.storage.get('active') ?? 0);
        ws.send(JSON.stringify({ type: 'state', live: !!host && host.a.fresh && age < 1500, ageMs: Math.max(0, age), snapshot: this.snapshot }));
      }
    }
    await this.report();
  }

  async webSocketClose(ws) { await this.left(ws); }
  async webSocketError(ws) { await this.left(ws); }
  async left(ws) {
    const a = ws.deserializeAttachment() || {};
    if (a.role === 'host') {
      a.role = 'gone'; ws.serializeAttachment(a);
      // Keep the last clock for late viewers after the host is gone.
      if (a.fresh && this.snapshot) {
        if (a.tick) this.snapshot = { ...this.snapshot, ...a.tick };
        await this.ctx.storage.put('snapshot', this.snapshot);
      }
      this.broadcast({ type: 'host', online: false });
    }
    if (a.role === 'host' || a.role === 'viewer' || a.role === 'gone') {
      await this.touch();
      await this.wakeAt(Date.now() + IDLE_MS);
      if (a.role === 'viewer') this.notifyHost(ws);
      await this.report(ws);
    }
  }

  async alarm() {
    const now = Date.now();
    let next = Infinity;
    for (const { ws, a } of this.sockets()) {
      if (a.role || ws.readyState !== WebSocket.OPEN) continue;
      if (now - a.since >= AUTH_MS) ws.close(1008, 'Authentication required');
      else next = Math.min(next, a.since + AUTH_MS);
    }
    if (this.meta && !this.host() && !this.viewers().length) {
      const active = await this.ctx.storage.get('active') ?? 0;
      if (now - active >= IDLE_MS) {
        // Make the room unusable first, then unlist it: a failing directory must not leave a live room behind.
        const lobby = this.meta.lobby;
        await this.ctx.storage.deleteAll();
        this.meta = null; this.snapshot = null; this.lastReport = undefined;
        for (const { ws } of this.sockets()) if (ws.readyState === WebSocket.OPEN) ws.close(4004, 'Room expired or unknown');
        if (lobby?.public) { try { await this.directory().remove(lobby.name); } catch (e) { console.error('directory remove failed', e?.name); } }
      } else next = Math.min(next, active + IDLE_MS);
    }
    if (this.meta) {
      // Expired lockout counters.
      const fails = await this.ctx.storage.list({ prefix: 'fail:', limit: 128 });
      const expired = [];
      for (const [k, v] of fails) {
        if (now - v.since >= FAIL_WINDOW) expired.push(k); else next = Math.min(next, v.since + FAIL_WINDOW);
      }
      if (expired.length) await this.ctx.storage.delete(expired);
      if (fails.size >= 128) next = Math.min(next, now + 1000);
      // Parked directory update, and the hourly refresh while the host is online.
      if (this.meta.lobby?.public) {
        const due = await this.ctx.storage.get('reportDue');
        const hosted = !!this.host();
        if (due || (hosted && now - (this.lastReport ?? 0) >= REPORT_REFRESH)) {
          if (due && this.lastReport && now - this.lastReport < REPORT_GAP) next = Math.min(next, this.lastReport + REPORT_GAP);
          else if (!await this.publish()) next = Math.min(next, now + REPORT_GAP);
        }
        if (hosted && this.lastReport) next = Math.min(next, this.lastReport + REPORT_REFRESH);
      }
    }
    if (next < Infinity) await this.ctx.storage.setAlarm(next);
  }
}
