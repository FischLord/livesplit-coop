# Run your own relay on Cloudflare (Windows)

This guide sets up a private LiveSplit Coop relay for your group on Cloudflare's free Workers plan. You deploy it once from your Windows PC; afterwards it runs on Cloudflare, so nothing has to stay open on your PC.

Your group gets one server address, and everyone uses it. Lobbies (name plus optional password, public or private) and invitation codes both work as described in the [quick start](quickstart.md).

Technical background: [protocol](protocol.md) (lobbies section) and the [relay README](../relay-cloudflare/README.md).

## 1. What you get

- A relay at `https://<name>.<your-subdomain>.workers.dev`, reachable for LiveSplit as `wss://…/coop`.
- Lobbies: a host creates a lobby with a name and an optional password. Public lobbies appear in the list; private lobbies are only reached by name.
- Invitation codes (`lscoop:1:…`) for rooms and lobbies.
- Nothing to install on player PCs and nothing to run locally after deployment.

## 2. Prerequisites (Windows)

- A free [Cloudflare account](https://dash.cloudflare.com/sign-up). Choosing a `workers.dev` subdomain is part of the first deployment.
- Node.js LTS:

  ```powershell
  winget install OpenJS.NodeJS.LTS
  ```

- Git:

  ```powershell
  winget install Git.Git
  ```

Open a **new** PowerShell window afterwards, so that `node`, `npm` and `git` are on the PATH.

## 3. Deploy

Run these in PowerShell:

```powershell
git clone https://github.com/FischLord/livesplit-coop.git
cd livesplit-coop\relay-cloudflare
npm ci
npx wrangler login
npm run deploy
```

`npx wrangler login` opens a browser window; sign in and allow access. `npm run deploy` runs `wrangler deploy --env=""`.

Optional: before deploying, you can change `"name"` in `wrangler.jsonc` (default `livesplit-coop-relay`). The name becomes part of the URL, so choose it once.

Wrangler prints the address of your worker, `https://<name>.<your-subdomain>.workers.dev`. Your server address for LiveSplit is the same host with `wss://` and `/coop`:

```text
wss://<name>.<your-subdomain>.workers.dev/coop
```

## 4. Check it

```powershell
Invoke-RestMethod https://<name>.<your-subdomain>.workers.dev/healthz
Invoke-RestMethod https://<name>.<your-subdomain>.workers.dev/lobbies
```

The first call returns `ok : True`. The second returns the lobby list, which is empty at first.

## 5. Use it in LiveSplit

1. Right-click the timer and open **Coop...**, then **Advanced**.
2. Set **Server** to your `wss://…/coop` address. Everyone in the group must use the same server.
3. Create or join lobbies as described in the [quick start](quickstart.md).

## 6. Settings

The settings are in the `"vars"` block of `wrangler.jsonc`:

| Setting | Default | Meaning |
|---|---|---|
| `ROOM_CREATION` | `"1"` | Set to `"0"` as a kill switch: no new rooms or lobbies. Existing ones keep working. |
| `ROOMS_PER_DAY` | `"500"` | Daily cap for new rooms and lobbies together (UTC day). |
| `CREATE_LIMIT` | 3 per minute | Rate limit for creating rooms and lobbies per IP and Cloudflare location. Set under `"ratelimits"`. |

After any change, deploy again:

```powershell
npm run deploy
```

## 7. Free plan limits

Cloudflare's numbers change from time to time. The figures below are as of October 2026; for current values see [Durable Objects pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/) and [Workers limits](https://developers.cloudflare.com/workers/platform/limits/).

| Resource | Free plan |
|---|---|
| Worker requests | 100,000 per day |
| Durable Object requests | 100,000 per day, including WebSocket messages and alarms. Incoming WebSocket messages count 20:1; outgoing messages and automatic pings are free. |
| Durable Object duration | 13,000 GB-s per day. Hibernating objects accrue no duration while idle. |
| SQLite rows | 5 million rows read and 100,000 rows written per day |
| Storage | 5 GB in total |
| CPU | 10 ms per invocation |

The counters reset daily at 00:00 UTC. When a limit is reached, further requests fail until the reset. The free plan never charges you automatically. If you need more, the paid Workers plan starts at US$5 per month.

**Capacity estimate** (derived from the component's behaviour, not measured on Cloudflare): a connected host sends about 4 time updates per second, which is roughly 14,400 messages per hour, or about 720 billed Durable Object requests per hour. Viewers cost almost nothing: receiving is free and joining is a single request. The daily budget therefore covers roughly 130 host-hours per day, for example:

- about 5 lobbies connected around the clock,
- about 45 lobbies for a 3-hour evening,
- about 130 one-hour sessions.

The estimate leaves out application-level pings (one every 5 seconds per client), which add a few percent. Idle lobbies cost only a few KB of storage. New lobbies are limited by `ROOMS_PER_DAY` and the per-IP rate limit. Lobbies that have not been used for 7 days are deleted automatically. A lobby holds up to 12 people: 1 host and 11 viewers.

## 8. Privacy and security

- Lobby passwords never leave the player's PC. The component derives a key from the password (PBKDF2), and the relay stores only a SHA-256 hash of that key. Host keys are stored the same way.
- The relay uses the client IP address (`CF-Connecting-IP`; for IPv6 the /64 network) only to lock out an address for the rest of the minute after 10 failed lobby logins within 60 seconds. The code does not log addresses. Cloudflare's own request metadata in the Workers logs is separate; see below.
- Public lobbies show their name, a lock icon, the game, category and profile label, and the number of online members.
- The relay can read timer data (splits, PBs, game, category). There is no end-to-end encryption, so only invite people you trust.

Details: [protocol](protocol.md), section "Lobbies (Cloudflare relay)".

## 9. Maintenance

**Update** to a newer version of the project:

```powershell
cd livesplit-coop\relay-cloudflare
git pull
npm ci
npm run deploy
```

**Roll back** to the previous deployment:

```powershell
npx wrangler rollback
```

**Logs**: Cloudflare dashboard → Workers → your worker → Logs. The relay logs errors and request metadata, but no timer data or keys. `npx wrangler tail` streams live logs.

**Remove** the worker completely:

```powershell
npx wrangler delete
```

The worker stops answering, so the group's server address stops working. Stored rooms and lobbies are not recoverable afterwards.

## 10. Testing locally (developers, optional)

```powershell
cd livesplit-coop\relay-cloudflare
npm test
```

This starts a local `wrangler dev` with a higher room-creation limit and runs the conformance and lobby tests against it. You do not need this to run your own relay.
