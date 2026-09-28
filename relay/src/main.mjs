import { readFile } from 'node:fs/promises';
import { createRelay } from './server.mjs';
import { readTls, reloadTls } from './tls.mjs';
const roomCreation = process.env.ROOM_CREATION === '1';
// With room creation enabled a rooms file is optional; configured rooms stay permanent.
const rooms = await readFile(process.env.ROOMS_FILE || './rooms.json', 'utf8').then(JSON.parse,
  error => { if (roomCreation && error.code === 'ENOENT') return []; throw error; });
const tlsFile = process.env.TLS_PEM_FILE;
const tls = tlsFile ? await readTls(tlsFile) : undefined;
const relay = createRelay({ rooms, tls, roomCreation });
const renewal = tlsFile ? reloadTls(relay.server, tlsFile, tls) : undefined;
const port = Number(process.env.PORT || 8787);
const host = process.env.BIND || '127.0.0.1';
relay.server.listen(port, host, () => console.log(`LiveSplit Coop relay listening on ${tls ? 'https' : 'http'}://${host}:${port}`));
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { renewal?.stop(); relay.close().then(() => process.exit(0)); });
