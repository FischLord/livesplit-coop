# Verification

Verified on Windows with installed LiveSplit 1.8.34 and Node 25.9.0. The relay targets Node 22+; the Dockerfile selects Node 24 LTS. On 2026-09-27 it was also deployed and checked on Linux using rootless Docker/cgroup v2 and a publicly trusted TLS endpoint on a dedicated port.

`npm test`: thirteen integration/validation tests covering multiple viewers, late joining, exact nullable RTA data, host/viewer authorization, outdated-protocol rejection, host takeover by a newer connection (4001), takeover at the global connection limit without admitting extra viewers, the reserved host slot, eviction of silent unauthenticated sockets, clock ticks and their merge into the cached snapshot, tick rejection before a snapshot or against a finish, disconnect/offline final cache, host reconnection, old sequence rejection, malformed messages and the health endpoint. The TLS test checks trusted-CA validation, WSS authentication, live certificate rotation without dropping an existing socket, rejection of the old certificate, and retention of working TLS after a malformed replacement. The TLS test requires OpenSSL (Git for Windows includes it).

`scripts/test-native.ps1`: 35 checks using the actual installed LiveSplit.Core, the built Coop DLL and native .NET WebSocket clients connected to an isolated local Node relay. Covers RTA without synthesized game time, exact checkpoint/end values, PB import, interpolation, stale freezing, pause, undo, reset, skip, paused GT, original run restoration, two simultaneous viewers, late joining, automatic reconnection, host-loss notification and component construction/settings/rendering. Since protocol v2 it also covers recognition of the installed Scriptable Auto Splitter, refusal to write into splits loaded while viewing, clock-only ticks (including a snapshot and tick arriving within one frame), host takeover without reconnect fights, and detection of a relay that accepts the socket but never answers.

## Protocol v2 local verification, 2026-09-27

Rebuilt the current uncommitted component with warnings as errors. The current relay passes **13/13** tests and the native component passes **35/35** checks. An additional regression reproduced HTTP 403 on a valid host takeover when authenticated connections had reached the global cap. Capacity is now checked after authentication and accounts for the host being replaced; replaced sockets no longer retain membership slots. The new test also verifies that extra viewers remain rejected.

The local `scripts/test-trio.ps1` test passed **17/17** acceptance checks with three real LiveSplit windows and copies of the Bay dashboard, using protocol v2. Confirmed start, checkpoint/PB values, pause, visible disconnect, reconnect with a missed checkpoint, skip, undo, exact final times, original viewer run restoration, late joining after finish and reset. Original split/layout/settings hashes were unchanged. The successful test session is `trio-20260927-213616` under ignored `.local`; its checks and pause/finish screenshots are retained locally. Test processes and their loopback relay were closed afterward.

The first attempt exposed a sharing violation in the test harness while the driver atomically replaced its status file. The reader now permits file replacement while holding the read handle. A subsequent attempt timed out waiting for window initialization and is not counted as a pass; the complete repeat above passed. This remains a simulated five-segment run, not an actual game run or a WAN test.

No VPS deployment, distributed package update, version bump, commit or public release was performed during this verification. Protocol v1 deployment evidence remains historical below. Deployment of protocol v2 requires a coordinated relay and host/viewer component update.

## Historical protocol v1 verification
The public deployment checks below were run with protocol v1 (0.1.0-beta.1). A relay and components built from later commits use protocol v2 and must be deployed and distributed together.

`scripts/test-trio.ps1`: 17 acceptance checks passed on 2026-09-27 in three real LiveSplit.exe processes with copied RV dashboards. A test-only fixture component drives the same timing model on each window's UI thread; transport uses the actual Coop component and loopback relay. Checks cover full dashboard height, two viewers, start, exact checkpoints/PBs, pause, visibly offline state, automatic reconnection with a missed checkpoint, skip/undo, exact final time, restoration of the original viewer run, joining after finish, reset, and unchanged original file hashes. Screenshots of all three windows at pause and finish were inspected. This is a simulated five-segment run, not a game completion.

The first UI test run's restore-path assertion incorrectly required an absolute path; LiveSplit had preserved the relative `test.lss` path correctly. The assertion was corrected. No production component change was required by this test. An apparent cropped-dashboard issue in the image preview was disproved by inspecting the original 360x786 screenshots and live window dimensions.

The build uses warnings as errors. By default native tests create in-memory sample runs and start/stop their own loopback relay. Both native/trio scripts now accept an explicit WSS URL and private rooms file for an owned external deployment. The trio test launches separate visible LiveSplit test copies and closes only its own processes. No game process, original split/layout file or existing LiveSplit instance is modified.

## Public deployment checks, 2026-09-27

- All 24 native checks passed over the actual public WSS endpoint, including two viewers, late join, reconnect and exact final RTA.
- `relay/test/remote-smoke.mjs` passed public CA validation, HTTPS health, wrong-key rejection, successful host/viewer authentication, duplicate-host rejection and viewer-publish rejection.
- The additional three-window WAN test could not initialize on the locked desktop. An attempt inside the offline sandbox loaded the windows but correctly had no external network. Neither is counted as a successful WAN GUI test; the successful native WAN checks above are separate evidence.
- The deployed container was healthy. Verified limits: 268435456 bytes RAM, 250000000 NanoCPUs (0.25 CPU), 64 PIDs, read-only root filesystem. Observed idle memory approximately 19 MiB.
- Certificate-copy service executed successfully; its 15-minute timer is enabled. Actual Let's Encrypt renewal has not occurred during this session. Existing renewal hooks and application proxy configuration were not changed.
- Existing webapp container IDs/start times and application Compose SHA-256 were identical before/after deployment; the public webapp still returned HTTP 200.
- Portable host/viewer copies were moved to a path containing spaces. All four host profiles and the viewer profile prepared successfully; DPAPI keys, resolved paths, separate roles, 23 Bay segments and preserved Valley PB/history checked. Both bundled ASLs compiled with the installed parser. Original split/layout/settings hashes stayed unchanged.

Still required before calling this a release: real game/autosplitter smoke test and full Duo/Trio game run, plus observation of the next real certificate renewal. Event-driven third-party components and history-dependent comparisons need additional compatibility coverage. The RV Bay final autosplit in a completed game remains unverified; the older Valley ASL was packaged unchanged and was not revalidated against the updated game.
