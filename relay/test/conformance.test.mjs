import { test, before, after } from 'node:test';
import { once } from 'node:events';
import { createRelay } from '../src/server.mjs';
import { conformance } from './conformance.mjs';

let relay, base;
before(async () => {
  relay = createRelay({ roomCreation: true, createPerHour: 100 });
  relay.server.listen(0, '127.0.0.1'); await once(relay.server, 'listening');
  base = `ws://127.0.0.1:${relay.server.address().port}`;
});
after(() => relay.close());
conformance(test, () => base);
