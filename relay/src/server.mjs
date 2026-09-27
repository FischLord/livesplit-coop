import http from 'node:http';
import https from 'node:https';
import { timingSafeEqual } from 'node:crypto';
import { WebSocketServer, WebSocket } from 'ws';
import { validSnapshot } from './protocol.mjs';

const equal = (a, b) => typeof a === 'string' && Buffer.byteLength(a) === Buffer.byteLength(b) && timingSafeEqual(Buffer.from(a), Buffer.from(b));
export function createRelay({ rooms, tls, maxConnections = 64, maxRoomConnections = 12, heartbeatMs = 15000 }) {
  if (!rooms || !Array.isArray(rooms) || !rooms.length) throw new Error('At least one configured room is required');
  const sessions = new Map();
  for (const r of rooms) {
    if (!/^[a-zA-Z0-9_-]{1,64}$/.test(r.name) || sessions.has(r.name) ||
        ![r.hostKey, r.viewerKey].every(k => typeof k === 'string' && k.length >= 32 && k.length <= 256) || r.hostKey === r.viewerKey) throw new Error('Invalid room configuration');
    sessions.set(r.name, { ...r, host: null, viewers: new Set(), snapshot: null, updated: 0, fresh: false });
  }
  const handler = (req, res) => {
    res.setHeader('Cache-Control', 'no-store');
    res.setHeader('X-Content-Type-Options', 'nosniff');
    if (req.method === 'GET' && req.url === '/healthz') { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end('{"ok":true}'); }
    else { res.writeHead(404); res.end(); }
  };
  const server = tls ? https.createServer(tls, handler) : http.createServer(handler);
  server.headersTimeout = 10000;
  server.requestTimeout = 10000;
  const wss = new WebSocketServer({ noServer: true, maxPayload: 256 * 1024, perMessageDeflate: false });
  server.on('upgrade', (req, socket, head) => {
    // Native client only. Browsers and URL credentials are deliberately unsupported.
    if (req.url !== '/coop' || req.headers.origin || wss.clients.size >= maxConnections) {
      socket.end('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n'); return;
    }
    wss.handleUpgrade(req, socket, head, ws => wss.emit('connection', ws));
  });
  function send(ws, msg) {
    if (ws.readyState !== WebSocket.OPEN) return;
    if (ws.bufferedAmount > 512 * 1024) { ws.terminate(); return; }
    ws.send(JSON.stringify(msg));
  }
  function status(room) { for (const ws of room.viewers) send(ws, { type: 'host', online: !!room.host }); }
  wss.on('connection', ws => {
    let room, role, seq = -1, count = 0, windowStart = Date.now();
    ws.alive = true;
    ws.on('pong', () => { ws.alive = true; });
    ws.on('error', () => {});
    const deadline = setTimeout(() => ws.close(1008, 'Authentication required'), 5000);
    deadline.unref();
    ws.on('message', (data, binary) => {
      if (Date.now() - windowStart >= 1000) { count = 0; windowStart = Date.now(); }
      if (++count > 30 || binary) { ws.close(1008, 'Invalid traffic'); return; }
      let m;
      try { m = JSON.parse(data.toString()); } catch { ws.close(1008, 'Invalid JSON'); return; }
      if (!m || typeof m !== 'object') { ws.close(1008, 'Invalid message'); return; }
      if (!room) {
        const candidate = sessions.get(m.room);
        if (m.type !== 'hello' || m.v !== 1 || !candidate || !['host', 'viewer'].includes(m.role) ||
            !equal(m.key, m.role === 'host' ? candidate.hostKey : candidate.viewerKey)) {
          ws.close(1008, 'Authentication failed'); return;
        }
        if ((m.role === 'host' && candidate.host) || candidate.viewers.size + (candidate.host ? 1 : 0) >= maxRoomConnections) {
          ws.close(1008, 'Room occupied or full'); return;
        }
        clearTimeout(deadline); room = candidate; role = m.role;
        if (role === 'host') { room.host = ws; room.fresh = false; }
        else room.viewers.add(ws);
        send(ws, { type: 'ready', v: 1, role });
        // A reconnecting host must send a fresh snapshot before viewers resume.
        if (role === 'viewer') {
          send(ws, { type: 'host', online: !!room.host });
          if (room.snapshot) send(ws, { type: 'state', live: !!room.host && room.fresh && Date.now() - room.updated < 1500, ageMs: Date.now() - room.updated, snapshot: room.snapshot });
        }
        return;
      }
      if (m.type === 'ping') { send(ws, { type: 'pong' }); return; }
      if (role !== 'host') { ws.close(1008, 'Viewers cannot publish'); return; }
      if (!validSnapshot(m)) { ws.close(1008, 'Invalid snapshot'); return; }
      if (m.seq <= seq) return;
      seq = m.seq; room.snapshot = m; room.updated = Date.now(); room.fresh = true;
      for (const viewer of room.viewers) send(viewer, { type: 'state', live: true, ageMs: 0, snapshot: m });
    });
    ws.on('close', () => {
      clearTimeout(deadline);
      if (!room) return;
      if (room.host === ws) { room.host = null; status(room); }
      room.viewers.delete(ws);
    });
  });
  const pulse = setInterval(() => {
    for (const ws of wss.clients) {
      if (!ws.alive) { ws.terminate(); continue; }
      ws.alive = false; ws.ping();
    }
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
