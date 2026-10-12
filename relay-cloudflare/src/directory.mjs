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
