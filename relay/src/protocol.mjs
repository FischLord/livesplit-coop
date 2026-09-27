export const PROTOCOL = 2;
const bound = 7 * 86400 * 10000000;
const text = (s, n = 200) => typeof s === 'string' && s.length <= n && !/[\x00-\x1f]/.test(s);
const ticks = n => n === null || (Number.isSafeInteger(n) && Math.abs(n) <= bound);
const id = s => typeof s === 'string' && /^[a-zA-Z0-9_-]{1,80}$/.test(s);
export function validSnapshot(s) {
  if (!s || s.type !== 'snapshot' || s.v !== PROTOCOL || !Number.isSafeInteger(s.seq) || s.seq < 0 ||
      !id(s.runId) || !id(s.attemptId) || !text(s.game) || !text(s.category) ||
      !Number.isSafeInteger(s.attempts) || s.attempts < 0 ||
      !['NotRunning', 'Running', 'Paused', 'Ended'].includes(s.phase) ||
      !['RealTime', 'GameTime'].includes(s.timingMethod) || !text(s.comparison, 100) ||
      !ticks(s.offsetTicks) || s.offsetTicks === null || !ticks(s.realTicks) || !ticks(s.gameTicks) ||
      typeof s.gamePaused !== 'boolean' || !Array.isArray(s.segments) ||
      s.segments.length < 1 || s.segments.length > 256 || !Number.isInteger(s.index)) return false;
  if (s.phase === 'NotRunning' ? s.index !== -1 :
      s.phase === 'Ended' ? s.index !== s.segments.length : s.index < 0 || s.index >= s.segments.length) return false;
  if (s.phase !== 'NotRunning' && s.realTicks === null) return false;
  for (const seg of s.segments) {
    if (!seg || !text(seg.name) || !['splitRT', 'splitGT', 'pbRT', 'pbGT', 'bestRT', 'bestGT'].every(k => ticks(seg[k]))) return false;
    if (!seg.comparisons || typeof seg.comparisons !== 'object' || Array.isArray(seg.comparisons) || Object.keys(seg.comparisons).length > 32) return false;
    for (const [name, time] of Object.entries(seg.comparisons)) {
      if (!text(name, 100) || !time || !ticks(time.real) || !ticks(time.game)) return false;
    }
  }
  if (s.phase === 'Ended' && (s.segments.at(-1).splitRT !== s.realTicks || s.segments.at(-1).splitGT !== s.gameTicks)) return false;
  return true;
}
// A tick only updates the running clock of the host connection's latest snapshot.
export function validTick(t) {
  return !!t && t.type === 'tick' && t.v === PROTOCOL && Number.isSafeInteger(t.seq) && t.seq >= 0 &&
    ticks(t.realTicks) && ticks(t.gameTicks) && typeof t.gamePaused === 'boolean';
}
