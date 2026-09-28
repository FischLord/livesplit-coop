# LiveSplit Coop

One player measures the run. Teammates see the same splits in their own LiveSplit windows — through a relay you host yourself, without a VPN or a desktop companion app.

**Status: 0.1.0-beta.2 — public testing beta.** Built and tested with Windows LiveSplit 1.8.34; deployed on a Linux VPS using rootless Docker and a publicly trusted TLS endpoint. Protocol v2 passed the local three-window and native tests; the native tests over the public endpoint were run with 0.1.0-beta.1 (protocol v1). The `main` branch now speaks protocol v3 (room in the URL path), which beta.2 clients and relays do not understand. A complete real game run remains untested.

```text
Host LiveSplit + Coop component ──WSS──┐
                                      │ Self-hosted relay
Viewer LiveSplit + Coop component ─WSS─┤ (Linux / Node / Docker)
Viewer LiveSplit + Coop component ─WSS─┘
```

## What works

- Host authority: separate publish and view keys; one publisher per room.
- Real Time and optional Game Time, exact completed split/final values, PB/best-segment/comparison values.
- Complete snapshots: late joining and reconnecting clients receive previous checkpoints too. Between changes only a small clock tick is sent.
- Start, finish, reset, pause, resume, skipped splits and undo are represented by the current state, rather than replayed button presses.
- Viewer connections create a temporary run; disconnect restores the original local splits. No automatic file writes or PB updates. Loading or editing splits while viewing stops viewer mode instead of writing host data into them.
- Viewer mode refuses to start, and stops, while a Scriptable Auto Splitter/Auto Splitting Runtime component or registered autosplitter is active.
- Visible connection status. A lost/stale connection freezes the viewer within 1.5 seconds.
- Outbound connections only on player PCs. No public LiveSplit control port.
- Access keys in saved layouts use Windows DPAPI, tied to the Windows user and machine.

## Build the component (Windows)

To use a prebuilt beta, extract `LiveSplit.Coop-v0.1.0-beta.2.zip` and copy its `Components/LiveSplit.Coop.dll` into your LiveSplit installation. This is an **add-on**, not a complete LiveSplit distribution. Windows 10/11 with .NET Framework 4.8 and LiveSplit 1.8.34 are the tested client environment. A group member must operate a relay; there is no bundled public server or account. See the [quick start](docs/quickstart.md).

The public archives contain no game autosplitters, personal splits/PBs, live room keys or hosted service credentials. Bring your own host splits and autosplitter. The relay is game-independent; RV There Yet was the initial development use case.

Use Windows PowerShell 5.1 and an installed LiveSplit release. No .NET SDK or Visual Studio installation is required; the build uses the Windows .NET Framework compiler.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -LiveSplitPath 'C:\path\to\LiveSplit'
```

Output: `dist/LiveSplit.Coop.dll`. Copy this DLL into **a test copy** of LiveSplit's `Components` folder, restart that copy, then add **Control → Coop Relay** to the layout. Configure the address, room, role and matching key, then click **Connect**. Saved layouts do not automatically connect on startup.

The host keeps their existing autosplitter. Viewers must remove Scriptable Auto Splitter from their viewer layout and deactivate the registered autosplitter. Joining as viewer is refused while a local attempt or an autosplitter is active. Use a test copy first; existing files are never migrated automatically.

## Run a local relay

Node.js 22+ is required on the relay host only.

```sh
cd relay
npm ci --ignore-scripts
npm run room -- coop
npm start
```

The key generator creates `rooms.json` and refuses to overwrite an existing file. Alternatively start with `ROOM_CREATION=1` to let clients create rooms via `POST /rooms` (see [protocol](docs/protocol.md)); such rooms live in memory and expire after seven idle days. Keep `hostKey` private; give teammates only `viewerKey`. This file is gitignored. The local endpoint is `ws://127.0.0.1:8787/coop`; plain `ws://` is accepted by the component **only for loopback development**. Remote players use WSS through a TLS reverse proxy, or direct TLS on a separate port using `compose.tls.yaml` and `TLS_PEM_FILE`.

For Linux/container deployment, see [deployment](docs/deployment.md). Docker is optional; the relay can also run as a restricted service account. No hosted account, third-party relay or VPN is used.

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
- No unattended startup, automatic viewer result export, room-creation UI or persistent server history yet. The host's saved `.lss` remains the authoritative record.
- Restarting the relay loses its in-memory cache. The connected host republishes after reconnection. A new connection with the room's publish key replaces the current host connection; the replaced component stops instead of reconnecting.
- Maximum 256 splits, 32 comparisons per segment, 256 KiB per message, seven-day magnitude for times, 12 participants per room (one host slot is always reserved) and 64 authenticated connections per relay.
- The component follows state directly and does not replay local timer-control events. Other plugins that depend on those events need separate compatibility testing.
- Use trusted teammates and a TLS endpoint. A view key is access to the room's timing data, not a user account. The prototype has no per-person revocation; rotate the room key instead.

[Deutsche Anleitung](docs/setup.de.md) · [Protocol](docs/protocol.md) · [Deployment](docs/deployment.md) · [License and provenance](THIRD_PARTY.md)

For beta feedback, open an issue with client/relay versions, host/viewer role, timing method, expected behavior and reproducible steps. Remove access keys and private room data from screenshots/logs. See [release notes](docs/releases/v0.1.0-beta.1.md).
