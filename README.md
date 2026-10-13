# LiveSplit Coop

One player measures the run. Teammates see the same splits in their own LiveSplit windows — through a relay you host yourself, without a VPN or a desktop companion app.

**Status: 1.0.0.** Built and tested with Windows LiveSplit 1.8.34 and 1.8.37. Protocol v3 with named lobbies (name plus optional password), a public lobby list and optional invitation codes; there is no built-in server: your group runs its own relay (free on Cloudflare) or gets an address from whoever does. The UI is English, and German when Windows is set to German. See the [1.0.0 release notes](docs/releases/v1.0.0.md).

## Quick start (no build needed)

1. Download **`LiveSplit.Coop-v1.0.0.zip`** from the [latest release](https://github.com/FischLord/livesplit-coop/releases/latest). It is an add-on for an installed LiveSplit (Windows 10/11, tested with LiveSplit 1.8.34 and 1.8.37). LiveSplit itself: <https://livesplit.org/downloads/>.
2. Close LiveSplit. Copy `Components/LiveSplit.Coop.dll` from the ZIP into the `Components` folder of your LiveSplit (next to `LiveSplit.exe`). Tip: if Windows blocks downloaded files, right-click the ZIP → **Properties** → **Unblock** before extracting.
3. Start LiveSplit → right-click → **Edit Layout** → **+** → **Control** → **Coop Relay** → OK. Then **Layout Settings** → **Coop Relay** → choose your role: **Host** (the player whose timer measures; keeps the autosplitter) or **Viewer** (teammates; remove any autosplitter from the viewer layout). Save the layout.
4. Set the relay: right-click → **Coop...** → **Advanced** → **Server**: enter your group's relay address (`wss://…/coop`). Everyone in the group uses the same one. No relay yet? Set one up on Cloudflare's free plan in about 10 minutes: [docs/self-hosting-cloudflare.md](docs/self-hosting-cloudflare.md).

   ![Coop window before a relay is set](docs/images/en/coop-no-server.png)

5. Host: right-click → **Coop...** → enter a lobby name (and optional password; tick "Show in the public list" if you want) → **Create lobby**. Name and password are copied to the clipboard; send them to your teammates.

   ![Coop window: creating a lobby](docs/images/en/coop-host-create.png)

6. Teammates: right-click → **Coop...** → pick the lobby from the public list (or type its name) → password → **Join**.

   ![Coop window: joining a lobby](docs/images/en/coop-viewer-join.png)

7. Done. The status line shows e.g. "team-night · Host · 2 online" or "team-night · following host". Next time, LiveSplit reconnects to the lobby automatically.

   ![Status line](docs/images/en/status-line.png)

The relay operator can technically see timer data (splits, PBs, game, category). A game package can preset a server for its players via `coop-profiles.json` (`server`). To run your own relay, follow [Run your own relay on Cloudflare](docs/self-hosting-cloudflare.md).

Full guide: [docs/quickstart.md](docs/quickstart.md).

**Deutsch:** Kurzfassung: ZIP herunterladen, `Components/LiveSplit.Coop.dll` in den `Components`-Ordner von LiveSplit kopieren, dann Control → Coop Relay hinzufügen und die Rolle wählen. Per Rechtsklick **Coop...** → **Erweitert** → **Server** die Relay-Adresse eurer Gruppe eintragen (`wss://…/coop`; noch keinen Relay? Anleitung: [docs/self-hosting-cloudflare.de.md](docs/self-hosting-cloudflare.de.md)), dann eine Lobby erstellen oder beitreten. Ausführlich: [docs/setup.de.md](docs/setup.de.md).

## How it fits together

```text
Host LiveSplit + Coop component ──WSS──┐
                                      │ Relay: your own on Cloudflare,
Viewer LiveSplit + Coop component ─WSS─┤ or your own (Cloudflare / Node / Docker)
Viewer LiveSplit + Coop component ─WSS─┘
```

## What works

- Host authority: separate publish and view keys; one publisher per room.
- Real Time and optional Game Time, exact completed split/final values, PB/best-segment/comparison values.
- Complete snapshots: late joining and reconnecting clients receive previous checkpoints too. Between changes only a small clock tick is sent.
- Start, finish, reset, pause, resume, skipped splits and undo are represented by the current state, rather than replayed button presses.
- Viewer connections create a temporary run. Attempts a viewer follows are recorded into the viewer's own splits file (`Splits/Coop/<game>/<category>.lss` or a matching profile), and a faster host PB is taken over on first contact. Loading or editing splits while viewing stops viewer mode instead of writing host data into them.
- Viewer mode refuses to start, and stops, while a Scriptable Auto Splitter/Auto Splitting Runtime component or registered autosplitter is active.
- Visible connection status. A lost/stale connection freezes the viewer within 1.5 seconds.
- Outbound connections only on player PCs. No public LiveSplit control port.
- The current lobby and its keys are stored in `coop-local.json` next to `LiveSplit.exe` using Windows DPAPI, tied to the Windows user and machine, and reconnect automatically on start. Layouts hold only the role.

## Build from source (developers)

The public archives contain no game autosplitters, personal splits/PBs, live room keys or hosted service credentials. Bring your own host splits and autosplitter. The relay is game-independent; RV There Yet was the initial development use case.

Use Windows PowerShell 5.1 and an installed LiveSplit release. No .NET SDK or Visual Studio installation is required; the build uses the Windows .NET Framework compiler.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -LiveSplitPath 'C:\path\to\LiveSplit'
```

Output: `dist/LiveSplit.Coop.dll`. To test it, copy this DLL into **a test copy** of LiveSplit's `Components` folder and restart that copy. The usage steps are in the [quick start](#quick-start-no-build-needed) and [docs/quickstart.md](docs/quickstart.md).

The host keeps their existing autosplitter. Viewers must remove Scriptable Auto Splitter from their viewer layout and deactivate the registered autosplitter. Joining as viewer is refused while a local attempt or an autosplitter is active. Use a test copy first; rooms stored in layouts by older versions move to `coop-local.json` once, and no split files are migrated.

## Run a local relay

Node.js 22+ is required on the relay host only.

```sh
cd relay
npm ci --ignore-scripts
npm run room -- coop
npm start
```

The key generator creates `rooms.json` and refuses to overwrite an existing file. Alternatively start with `ROOM_CREATION=1` to let clients create rooms via `POST /rooms` (see [protocol](docs/protocol.md)); such rooms live in memory and expire after seven idle days. Keep `hostKey` private; give teammates only `viewerKey`. This file is gitignored. The local endpoint is `ws://127.0.0.1:8787/coop`; plain `ws://` is accepted by the component **only for loopback development**. Remote players use WSS through a TLS reverse proxy, or direct TLS on a separate port using `compose.tls.yaml` and `TLS_PEM_FILE`.

For Linux/container deployment, see [deployment](docs/deployment.md). Windows users who want their own group relay without a server can follow [Run your own relay on Cloudflare](docs/self-hosting-cloudflare.md) ([Deutsch](docs/self-hosting-cloudflare.de.md)). Docker is optional; the relay can also run as a restricted service account. No hosted account, third-party relay or VPN is used.

## Verify

```sh
cd relay
npm test
```

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-native.ps1 -LiveSplitPath 'C:\path\to\LiveSplit'
```

The native tests start an isolated loopback relay, connect real .NET WebSocket clients and exercise the installed LiveSplit timing model. They do not connect to a game or change live layouts/split files. See [verification](docs/verification.md).

To exercise three actual LiveSplit windows with a copy of an existing dashboard:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/test-trio.ps1 -LiveSplitPath 'C:\path\to\LiveSplit' -LayoutPath 'C:\path\to\dashboard.lsl'
```

This creates three disposable installations under `.local`, disables autosplitters/hotkeys in those copies, runs a clearly labeled simulated attempt and captures each window. It closes its processes afterwards. Original split/layout/settings files are hash-checked. `tests/UiDriver.cs` is a test fixture only and is never part of the distributed component DLL.

For an owned external deployment, the native and trio scripts accept `-RelayAddress wss://your-domain:8443/coop -RoomsFile C:\private\rooms.json`. This temporarily occupies the configured host slot and publishes sample data. Run only when teammates are disconnected. The UI test additionally requires an unlocked interactive desktop; the native test does not.

`scripts/Start-Coop.ps1` and `.cmd` are portable-package launcher templates. They consume `Paket.json` (profile list) and `Zugang.private.json` (address, room, role, key), resolve package paths and encrypt the key for the recipient's Windows user. These private packages are not public release archives. See [portable packaging](docs/portable.md).

## Current limits

- This is a **viewer**, not a second authoritative run recorder. Attempt/segment history, icons, game-specific metadata, layout files and scripts are not sent. PBs, best segments and named comparison values are sent; history-dependent components may differ.
- Running digits interpolate from received snapshots (four per second plus changes), so they can trail the host by network delay. Completed checkpoint and final values are copied exactly, with 100 ns tick precision.
- No persistent server history. The host's saved `.lss` remains the authoritative record; viewers keep their own splits file as described above.
- Restarting the relay loses its in-memory cache. The connected host republishes after reconnection. A new connection with the room's publish key replaces the current host connection; the replaced component stops instead of reconnecting.
- Maximum 256 splits, 32 comparisons per segment, 256 KiB per message, seven-day magnitude for times, 12 participants per room (one host slot is always reserved) and 64 authenticated connections per relay.
- The component follows state directly and does not replay local timer-control events. Other plugins that depend on those events need separate compatibility testing.
- Use trusted teammates and a TLS endpoint. A view key is access to the room's timing data, not a user account. The prototype has no per-person revocation; rotate the room key instead.

[Deutsche Anleitung](docs/setup.de.md) · [Run your own relay (Cloudflare)](docs/self-hosting-cloudflare.md) · [Eigenen Relay betreiben](docs/self-hosting-cloudflare.de.md) · [Protocol](docs/protocol.md) · [Deployment](docs/deployment.md) · [License and provenance](THIRD_PARTY.md)

For feedback, open an issue with client/relay versions, host/viewer role, timing method, expected behavior and reproducible steps. Remove access keys and private room data from screenshots/logs. See [release notes](docs/releases/v1.0.0.md).
