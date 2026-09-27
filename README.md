# LiveSplit Coop

One player measures the run. Teammates see the same splits in their own LiveSplit windows — through a relay you host yourself, without a VPN or a desktop companion app.

**Status: 0.1.0 field-test prototype.** Built and tested with Windows LiveSplit 1.8.34; deployed on a Linux VPS using rootless Docker and a publicly trusted TLS endpoint. Local three-window tests and native .NET clients over the public endpoint passed. A complete real game run remains untested.

```text
Host LiveSplit + Coop component ──WSS──┐
                                      │ Self-hosted relay
Viewer LiveSplit + Coop component ─WSS─┤ (Linux / Node / Docker)
Viewer LiveSplit + Coop component ─WSS─┘
```

## What works

- Host authority: separate publish and view keys; one publisher per room.
- Real Time and optional Game Time, exact completed split/final values, PB/best-segment/comparison values.
- Complete snapshots: late joining and reconnecting clients receive previous checkpoints too.
- Start, finish, reset, pause, resume, skipped splits and undo are represented by the current state, rather than replayed button presses.
- Viewer connections create a temporary run; disconnect restores the original local splits. No automatic file writes or PB updates.
- Visible connection status. A lost/stale connection freezes the viewer within 1.5 seconds.
- Outbound connections only on player PCs. No public LiveSplit control port.
- Access keys in saved layouts use Windows DPAPI, tied to the Windows user and machine.

## Build the component (Windows)

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

The key generator creates `rooms.json` and refuses to overwrite an existing file. Keep `hostKey` private; give teammates only `viewerKey`. This file is gitignored. The local endpoint is `ws://127.0.0.1:8787/coop`; plain `ws://` is accepted by the component **only for loopback development**. Remote players use WSS through a TLS reverse proxy, or direct TLS on a separate port using `compose.tls.yaml` and `TLS_PEM_FILE`.

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
- Restarting the relay loses its in-memory cache. The connected host republishes after reconnection. Host handover uses the room's publish key and requires the previous host connection to end.
- Maximum 256 splits, 32 comparisons per segment, 256 KiB per message, seven-day magnitude for times, 12 participants per room and 64 connections per relay.
- The component follows state directly and does not replay local timer-control events. Other plugins that depend on those events need separate compatibility testing.
- Use trusted teammates and a TLS endpoint. A view key is access to the room's timing data, not a user account. The prototype has no per-person revocation; rotate the room key instead.

[Deutsche Anleitung](docs/setup.de.md) · [Protocol](docs/protocol.md) · [Deployment](docs/deployment.md) · [License and provenance](THIRD_PARTY.md)
