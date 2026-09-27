import { readFile } from 'node:fs/promises';
import { createRelay } from './server.mjs';
const rooms = JSON.parse(await readFile(process.env.ROOMS_FILE || './rooms.json', 'utf8'));
const relay = createRelay({ rooms });
const port = Number(process.env.PORT || 8787);
const host = process.env.BIND || '127.0.0.1';
relay.server.listen(port, host, () => console.log(`LiveSplit Coop relay listening on ${host}:${port}`));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => relay.close().then(() => process.exit(0)));
