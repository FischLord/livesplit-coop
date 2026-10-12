// Local Cloudflare relay for the component's native tests: prints the ws:// coop endpoint, then runs until killed (max. 10 min).
import { startDev } from './dev.mjs';
const dev = await startDev();
console.log(`${dev.base}/coop`);
const stop = () => dev.stop().finally(() => process.exit(0));
process.on('SIGTERM', stop); process.on('SIGINT', stop);
setTimeout(stop, 600000);
