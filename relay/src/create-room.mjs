import { randomBytes } from 'node:crypto';
import { writeFile } from 'node:fs/promises';
const name = process.argv[2] || 'coop';
if (!/^[a-zA-Z0-9_-]{1,64}$/.test(name)) throw new Error('Room name must use letters, digits, _ or -');
const rooms = [{ name, hostKey: randomBytes(32).toString('base64url'), viewerKey: randomBytes(32).toString('base64url') }];
await writeFile('rooms.json', JSON.stringify(rooms, null, 2) + '\n', { flag: 'wx', mode: 0o600 });
console.log('Created rooms.json. Keep hostKey private; share only viewerKey with teammates.');
