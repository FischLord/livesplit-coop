import http from 'node:http';
import https from 'node:https';
import { randomBytes, timingSafeEqual } from 'node:crypto';
import { WebSocketServer, WebSocket } from 'ws';
import { PROTOCOL, validSnapshot, validTick } from './protocol.mjs';

const equal = (a, b) => typeof a === 'string' && Buffer.byteLength(a) === Buffer.byteLength(b) && timingSafeEqual(Buffer.from(a), Buffer.from(b));
const DAY = 86400000;
// roomCreation enables POST /rooms. Created rooms live in memory only and expire after roomIdleMs without
// members; configured rooms never expire. createPerHour is global because client addresses are not used.
export function createRelay({ rooms = [], tls, roomCreation = false, maxRooms = 256, createPerHour = 30, roomIdleMs = 7 * DAY,
  maxConnections = 64, maxPending = 16, maxRoomConnections = 12, heartbeatMs = 15000, authMs = 5000 }) {
  if (!Array.isArray(rooms) || (!rooms.length && !roomCreation)) throw new Error('At least one configured room is required');
  const sessions = new Map();
  const open = (name, keys, expires) => sessions.set(name, { name, ...keys, expires, active: Date.now(), host: null, viewers: new Set(), snapshot: null, updated: 0, fresh: false });
  for (const r of rooms) {
    if (!/^[a-zA-Z0-9_-]{1,64}$/.test(r.name) || sessions.has(r.name) ||
        ![r.hostKey, r.viewerKey].every(k => typeof k === 'string' && k.length >= 32 && k.length <= 256) || r.hostKey === r.viewerKey) throw new Error('Invalid room configuration');
    open(r.name, { hostKey: r.hostKey, viewerKey: r.viewerKey }, false);
  }
  const created = [];
  function sweep() {
    const now = Date.now();
    for (const [name, r] of sessions) if (r.expires && !r.host && !r.viewers.size && now - r.active >= roomIdleMs) sessions.delete(name);
  }
  function createRoom(res) {
    const now = Date.now();
    while (created.length && now - created[0] >= 3600000) created.shift();
    if (created.length >= createPerHour) { res.writeHead(429, { 'Retry-After': String(Math.ceil((created[0] + 3600000 - now) / 1000)) }); res.end(); return; }
    sweep();
    if (sessions.size >= maxRooms) { res.writeHead(503); res.end(); return; }
    let name; do name = randomBytes(16).toString('base64url'); while (sessions.has(name));
    const keys = { hostKey: randomBytes(32).toString('base64url'), viewerKey: randomBytes(32).toString('base64url') };
    open(name, keys, true); created.push(now);
    res.writeHead(201, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ roomId: name, ...keys, idleExpiryDays: roomIdleMs / DAY }));
  }
  const handler = (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    if (req.method === 'GET' && req.url === '/healthz') { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{"ok":true}'); }
    // Browsers could send this cross-site; only native clients may create rooms.
    else if (roomCreation && req.method === 'POST' && req.url === '/rooms' && !req.headers.origin) { req.resume(); createRoom(res); }
    else { res.writeHead(404); res.end(); }
  };
  const server = tls ? https.createServer({ handshakeTimeout: 10000, ...tls }, handler) : http.createServer(handler);
  server.headersTimeout = 10000;
  server.requestTimeout = 10000;
  const wss = new WebSocketServer({ noServer: true, maxPayload: 256 * 1024, perMessageDeflate: false });
  // Unauthenticated sockets never occupy member slots. When the waiting pool is full the oldest
  // waiter is dropped, so a flood must outpace a real client's hello instead of just holding sockets.
  // Client addresses are not used: rootless port forwarding and proxies can hide them.
  const pending = new Set();
  const members = new Set();
  server.on('upgrade', (req, socket, head) => {
    // Native client only. Browsers and URL credentials are deliberately unsupported.
    // v3 addresses the room in the path. Bare /coop is still accepted so older clients learn to update.
    const path = /^\/coop(?:\/([a-zA-Z0-9_-]{1,64}))?$/.exec(req.url);
    if (!path || req.headers.origin) {
      socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n'); return;
    }
    wss.handleUpgrade(req, socket, head, ws => wss.emit('connection', ws, path[1]));
  });
  function send(ws, data) {
    if (ws.readyState !== WebSocket.OPEN) return;
    if (ws.bufferedAmount > 512 * 1024) { ws.terminate(); return; }
    ws.send(data);
  }
  function broadcast(room, msg) { const data = JSON.stringify(msg); for (const ws of room.viewers) send(ws, data); }
  wss.on('connection', (ws, pathRoom) => {
    let room, role, seq = -1, count = 0, windowStart = Date.now();
    ws.alive = true;
    ws.on('pong', () => { ws.alive = true; });
    ws.on('error', () => {});
    pending.add(ws);
    if (pending.size > maxPending) { const oldest = pending.values().next().value; pending.delete(oldest); oldest.terminate(); }
    const deadline = setTimeout(() => ws.close(1008, 'Authentication required'), authMs);
    deadline.unref();
    ws.on('message', (data, binary) => {
      if (Date.now() - windowStart >= 1000) { count = 0; windowStart = Date.now(); }
      if (++count > 30 || binary) { ws.close(1008, 'Invalid traffic'); return; }
      let m;
      try { m = JSON.parse(data.toString()); } catch { ws.close(1008, 'Invalid JSON'); return; }
      if (!m || typeof m !== 'object') { ws.close(1008, 'Invalid message'); return; }
      if (!room) {
        if (m.type === 'hello' && m.v !== PROTOCOL) { ws.close(1008, 'Unsupported protocol version; update LiveSplit Coop'); return; }
        if (m.type !== 'hello' || !['host', 'viewer'].includes(m.role)) { ws.close(1008, 'Authentication failed'); return; }
        if (!pathRoom) { ws.close(1008, 'Room missing from path'); return; }
        const candidate = sessions.get(pathRoom);
        if (!candidate) { ws.close(4004, 'Room expired or unknown'); return; }
        if (!equal(m.key, m.role === 'host' ? candidate.hostKey : candidate.viewerKey)) { ws.close(1008, 'Authentication failed'); return; }
        // The host always has a reserved slot; viewers cannot lock the publisher out.
        const previousHost = m.role === 'host' ? candidate.host : null;
        const replacingMember = previousHost && members.has(previousHost);
        if (members.size - (replacingMember ? 1 : 0) >= maxConnections || (m.role === 'viewer' && candidate.viewers.size >= maxRoomConnections - 1)) {
          ws.close(1008, 'Room occupied or full'); return;
        }
        clearTimeout(deadline); pending.delete(ws); members.add(ws); room = candidate; role = m.role; room.active = Date.now();
        if (role === 'host') {
          // Holding the host key proves authority. The newest connection wins, so a host whose
          // previous socket silently died can publish again at once.
          const previous = room.host;
          room.host = ws; room.fresh = false;
          if (previous) { members.delete(previous); previous.close(4001, 'Replaced by a newer host connection'); }
        } else room.viewers.add(ws);
        send(ws, JSON.stringify({ type: 'ready', v: PROTOCOL, role }));
        // A reconnecting host must send a fresh snapshot before viewers resume.
        if (role === 'viewer') {
          send(ws, JSON.stringify({ type: 'host', online: !!room.host }));
          if (room.snapshot) send(ws, JSON.stringify({ type: 'state', live: !!room.host && room.fresh && Date.now() - room.updated < 1500, ageMs: Date.now() - room.updated, snapshot: room.snapshot }));
        }
        return;
      }
      if (m.type === 'ping') { send(ws, '{"type":"pong"}'); return; }
      if (role !== 'host') { ws.close(1008, 'Viewers cannot publish'); return; }
      if (room.host !== ws) return; // replaced; its close handshake is in progress
      if (m.type === 'tick') {
        if (!room.fresh) { ws.close(1008, 'Snapshot required before tick'); return; }
        if (!validTick(m)) { ws.close(1008, 'Invalid tick'); return; }
        if (m.seq <= seq) return;
        const next = { ...room.snapshot, seq: m.seq, realTicks: m.realTicks, gameTicks: m.gameTicks, gamePaused: m.gamePaused };
        if (!validSnapshot(next)) { ws.close(1008, 'Invalid tick'); return; }
        seq = m.seq; room.snapshot = next; room.updated = Date.now();
        broadcast(room, { type: 'tick', seq: m.seq, realTicks: m.realTicks, gameTicks: m.gameTicks, gamePaused: m.gamePaused });
        return;
      }
      if (!validSnapshot(m)) { ws.close(1008, 'Invalid snapshot'); return; }
      if (m.seq <= seq) return;
      seq = m.seq; room.snapshot = m; room.updated = Date.now(); room.fresh = true;
      broadcast(room, { type: 'state', live: true, ageMs: 0, snapshot: m });
    });
    ws.on('close', () => {
      clearTimeout(deadline);
      pending.delete(ws);
      if (!room) return;
      members.delete(ws); room.active = Date.now();
      if (room.host === ws) { room.host = null; broadcast(room, { type: 'host', online: false }); }
      room.viewers.delete(ws);
    });
  });
  const pulse = setInterval(() => {
    for (const ws of wss.clients) {
      if (!ws.alive) { ws.terminate(); continue; }
      ws.alive = false; ws.ping();
    }
    sweep();
  }, heartbeatMs);
  pulse.unref();
  return {
    server,
    async close() {
      clearInterval(pulse);
      for (const ws of wss.clients) ws.terminate();
      await new Promise(resolve => wss.close(resolve));
      await new Promise(resolve => server.close(resolve));
    }
  };
}
