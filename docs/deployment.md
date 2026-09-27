# Deployment plan — no automatic production deployment

The relay is prepared for a small Linux VPS behind an existing TLS proxy. These are instructions to review and adapt, not evidence that a server has been changed.

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

Validate with `nginx -t` in the actual proxy environment before a graceful reload. A public TLS smoke test must show a healthy endpoint, successful authenticated WSS connection, and rejected invalid key. The backend itself does not supply TLS. Unencrypted remote URLs are rejected by the Windows component.

## 5. Acceptance

Connect one host and two viewers. Verify start, pause, resume, intermediate and final splits, skipped/undone splits, reset, late join, host interruption and reconnection. Compare exact final values, not just rendered milliseconds. Confirm that the existing application remains healthy. Do not perform load tests against that application.

## 6. Rollback

Disconnect the LiveSplit clients. Restore only the new/changed proxy configuration from its saved previous version, validate and reload. Stop/remove only the `livesplit-coop` Compose project. Leave application containers, volumes, certificates and DNS unrelated to the new subdomain intact. This relay has no run database to migrate. Existing LiveSplit files are outside the deployment.

Before deploying onto a shared server, present the exact file changes and expected reloads to its owner for approval.
