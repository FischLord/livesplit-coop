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

// Failed-login lockout key: IPv4 as-is, IPv6 by its /64 (a single subscriber owns a whole /64).
export function addressBucket(ip) {
  if (typeof ip !== 'string' || !ip.includes(':')) return typeof ip === 'string' && ip ? ip : 'unknown';
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/i.exec(ip);
  if (mapped) return mapped[1];
  const [head, tail, extra] = ip.split('::');
  if (extra !== undefined) return 'unknown';
  const a = head ? head.split(':') : [], b = tail === undefined ? [] : tail ? tail.split(':') : [];
  const groups = tail === undefined ? a : [...a, ...Array(Math.max(0, 8 - a.length - b.length)).fill('0'), ...b];
  if (groups.length !== 8 || !groups.every(g => /^[0-9a-f]{1,4}$/i.test(g))) return 'unknown';
  return groups.slice(0, 4).map(g => parseInt(g, 16).toString(16)).join(':');
}

// Published profile label: same text rule as the relay's snapshot validation (string, no control characters), at most 60 chars.
export function cleanLabel(raw) {
  return typeof raw === 'string' && raw.length <= 60 && !/[\x00-\x1f]/.test(raw) ? raw : null;
}
