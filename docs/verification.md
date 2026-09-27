# Verification

Verified on Windows with installed LiveSplit 1.8.34 and Node 25.9.0. The relay targets Node 22+; the Dockerfile selects Node 24 LTS. On 2026-09-27 it was also deployed and checked on Linux using rootless Docker/cgroup v2 and a publicly trusted TLS endpoint on a dedicated port.

`npm test`: seven integration/validation tests covering multiple viewers, late joining, exact nullable RTA data, host/viewer authorization, duplicate host rejection, disconnect/offline final cache, host reconnection, old sequence rejection, malformed messages and the health endpoint. The TLS test checks trusted-CA validation, WSS authentication, live certificate rotation without dropping an existing socket, rejection of the old certificate, and retention of working TLS after a malformed replacement. The TLS test requires OpenSSL (Git for Windows includes it).

`scripts/test-native.ps1`: 24 checks using the actual installed LiveSplit.Core, the built Coop DLL and native .NET WebSocket clients connected to an isolated local Node relay. Covers RTA without synthesized game time, exact checkpoint/end values, PB import, interpolation, stale freezing, pause, undo, reset, skip, paused GT, original run restoration, two simultaneous viewers, late joining, automatic reconnection, host-loss notification and component construction/settings/rendering.

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
