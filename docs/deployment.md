# Deployment

The relay can run behind an existing TLS proxy or terminate TLS itself on a dedicated port. The latter avoids changing shared application proxies. A deployment using rootless Docker and separate port 8443 was verified on 2026-09-27; this document remains a generic operator guide. Keep machine-specific paths and room keys in private deployment records.

## 1. Inspect the existing host, read-only

Check OS/version, free memory/disk, occupied ports, rootless Docker context, container names/networks, running image references, proxy configuration/mounts, certificate renewal hooks, and current backups. Do not print secrets or dump entire container environments. In a shared application VPS, preserve the existing app stack and its resource allocation.

Record how the current nginx reaches upstreams. A proxy running inside a container cannot reach a host service through the container's own `127.0.0.1`. The loopback port in this repository's Compose example is directly usable by **host-based nginx only**. For a containerized proxy, plan an explicit shared network or another narrowly reachable upstream after inspecting that topology. Do not attach the relay to an application's private data network.

## 2. Prepare separate files and credentials

Use a separate deployment directory and Compose project, e.g. `~/livesplit-coop` and `livesplit-coop`. Keep a copy of the exact previous proxy configuration and image references before modifying anything. Generate keys locally on the target using `npm run room -- <room>` from `relay/`, or generate a JSON room configuration offline.

For the Compose example the configuration lives at `.private/rooms.json`. On Linux create `.private` with mode **0700**, owned by the deployment user. The mounted file must be readable by container UID 1000. With rootless UID mapping, an owner-only host file may not be readable by that UID. One explicit setup is a **0644 file inside the 0700 directory**, bind-mounted read-only as an individual file. Other host users cannot traverse the private directory; the container can read the mounted file. Verify permissions for the actual rootless setup before launch. Never commit or publish keys.

The service uses a read-only filesystem, no capabilities, no privilege escalation, 256 MiB memory, 0.25 CPU and bounded logs. These are initial budgets for a small group, not measured public-service capacity. The Node base image uses an LTS major tag; production can pin an approved digest.

## 3. Validate before routing public traffic

Run `docker compose config --quiet`, build the image, start only the new relay project, and check `/healthz`. Inspect the logs without dumping credentials. For public use, make the relay reachable only by the TLS proxy, not through an open raw backend port. Rootless Docker socket access is never mounted into the relay.

Example commands (after reviewing paths, permissions and topology):

```sh
docker compose -p livesplit-coop config --quiet
docker compose -p livesplit-coop build
docker compose -p livesplit-coop up -d
```

## 4. DNS and HTTPS

Choose a new subdomain pointing to the VPS. Add a separate nginx virtual host with a valid certificate and `/coop` WebSocket forwarding (`proxy_http_version 1.1`, `Upgrade`, `Connection`, suitable read timeout). Integrate certificate renewal with the existing arrangement; do not replace renewal hooks or restart the shared Docker daemon.

Validate with `nginx -t` in the actual proxy environment before a graceful reload. A public TLS smoke test must show a healthy endpoint, successful authenticated WSS connection, and rejected invalid key. Unencrypted remote URLs are rejected by the Windows component.

### Alternative: direct TLS on a separate port

Use `docker compose -p livesplit-coop -f compose.tls.yaml ...` instead of combining it with the HTTP Compose file. Set `TLS_SERVER_NAME` to the certificate DNS name in a private `.env`, and provide `.private/tls/server.pem` containing the private key followed by the certificate/full chain. The URL is `wss://your-domain:8443/coop`. Check the port is free and add only its firewall rule. No changes to the existing 80/443 service, DNS or proxy networks are necessary when reusing an existing domain certificate.

This example is specifically for a **rootless** Docker daemon: container UID 0 maps to the unprivileged deployment user and can read owner-only files. Do not silently reuse that UID choice with a rootful engine. Keep `.private` and `tls` directories 0700, with `rooms.json` and `server.pem` 0600. Rootless cgroup limits require the engine's systemd/cgroup v2 support; verify the effective limits and health.

`TLS_PEM_FILE` enables HTTPS; a missing or invalid file fails startup rather than falling back to plaintext. Every 30 seconds the process checks the PEM, validates a replacement, and installs a new TLS context without disconnecting existing clients. A bad replacement leaves the previous context intact and emits a generic error without certificate/key content.

If the certificate belongs to an existing application, use a **new, dedicated** root-owned copy script and systemd oneshot/timer. It must only read the original certificate/key, verify their match, write a 0600 temporary file in the relay directory, assign ownership to the deployment user, and atomically rename it over `server.pem` only when changed. Mount the directory rather than the individual PEM so replacements are visible. A 15-minute copy timer plus 30-second reload is sufficient for ordinary renewal. Never replace the application's renewal hooks, download its private key to a client, or force a production certificate renewal just to test this service. Document the certificate dependency and monitor expiration. A root oneshot can use `ProtectSystem=strict`, `ProtectHome=read-only`, `PrivateTmp=yes`, `NoNewPrivileges=yes` and one `ReadWritePaths` exception for the new TLS directory.

## 5. Acceptance

Connect one host and two viewers. Verify start, pause, resume, intermediate and final splits, skipped/undone splits, reset, late join, host interruption and reconnection. Compare exact final values, not just rendered milliseconds. Confirm that the existing application remains healthy. Do not perform load tests against that application.

## 6. Rollback

Disconnect the LiveSplit clients. For direct TLS, stop/remove only the `livesplit-coop` Compose project, disable its dedicated certificate-copy timer and remove only its dedicated firewall rule. For a proxy deployment, restore only the new/changed proxy configuration from its saved previous version, validate and reload. Leave application containers, volumes, original certificates and unrelated DNS intact. This relay has no run database to migrate. Existing LiveSplit files are outside the deployment.

Before deploying onto a shared server, present the exact file changes and expected reloads to its owner for approval.
