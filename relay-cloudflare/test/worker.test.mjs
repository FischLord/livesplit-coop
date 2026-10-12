// Runs the shared protocol conformance suite against the Worker in the local workerd runtime.
import { test, before, after } from 'node:test';
import { conformance } from '../../relay/test/conformance.mjs';
import { startDev } from './dev.mjs';

let dev;
before(async () => { dev = await startDev(); }, { timeout: 120000 });
after(() => dev?.stop());
conformance(test, () => dev.base);
