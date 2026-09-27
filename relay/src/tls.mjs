import { readFile } from 'node:fs/promises';
import { createSecureContext } from 'node:tls';

// One atomically replaced PEM keeps certificate and private key consistent.
export async function readTls(file) {
  const pem = await readFile(file, 'utf8');
  const options = { key: pem, cert: pem, minVersion: 'TLSv1.2' };
  createSecureContext(options); // Fail closed on startup; validate before hot reload.
  return options;
}

export function reloadTls(server, file, initial, report = console.error) {
  let previous = initial.cert, busy = false;
  const refresh = async () => {
    if (busy) return false;
    busy = true;
    try {
      const next = await readTls(file);
      if (next.cert === previous) return false;
      server.setSecureContext(next);
      previous = next.cert;
      return true;
    } catch {
      report('TLS certificate reload failed; keeping the previous certificate.');
      return false;
    } finally { busy = false; }
  };
  const timer = setInterval(refresh, 30000);
  timer.unref();
  return { refresh, stop: () => clearInterval(timer) };
}
