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
