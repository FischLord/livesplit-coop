# Local Coop diagnostics

The local `0.1.0-beta.3+diagnostics.1` build retains protocol v3 and works with existing beta.3 clients and relays. No server or teammate update is required. The assembly version remains unchanged; the diagnostic session header identifies this local build.

Logs start when a Coop component loads. They are JSON Lines files in `Logs/Coop` beside that LiveSplit executable (the component's parent directory). If that directory is not writable, storage falls back to `%LOCALAPPDATA%/LiveSplit.Coop/Logs`.

Right-click the timer, choose **Coop...**, open **Advanced** and click **Export diagnostics...** to save the retained logs as one `.jsonl` file. Export includes earlier sessions still retained in that log directory, so it also works after a restart. Host and viewer installations normally have separate log directories. Nothing is uploaded automatically.

Recorded data:
- UTC and monotonic session timestamps, a random session/component/connection identifier, local diagnostic build, protocol, runtime and process architecture.
- Start, split, finish, undo/skip, pause/resume/reset, phase, segment index/count, timing method and real/game time in .NET ticks (100 ns). Viewer snapshot adoption and live/waiting/offline/stale transitions are distinct events.
- Connection attempts, handshake readiness, reconnects, host presence/takeover, close codes and fixed policy descriptions.
- Cumulative transmitted/received application-message counts and UTF-8 byte counts every 10 seconds while connected and on disposal; received snapshot/tick counts and maximum data-arrival gap. Bytes exclude WebSocket/TLS framing; arrival gaps are not ping latency.
- Exception chain types, HRESULTs and failing method names, excluding exception messages and file-bearing stack traces.

No invitation, room name, URL, access key, clipboard contents, raw network payload, game/category/segment names, or personal split history is logged. Existing split files are not included in an export. Event fields must remain allowlisted when extending this feature; do not log arbitrary server/user strings.

The writer batches disk I/O once per second off the timer thread, caps its queue at 512 records and rotates at 1 MiB. It retains the latest 20 matching log files per directory (active files from other processes may temporarily exceed that limit). Queue overload is reported as `log_overflow`. Log storage failures do not stop timing or networking; retry occurs on the next flush. A crash can lose the last buffered second, and failed disk writes can lose records. Export reports a storage error instead of claiming success if its flush fails.

This is diagnostic logging, not an authoritative run replay. Previous runs from the older DLL cannot be reconstructed. Autosplitter internals and events on an unupdated teammate's computer are not visible.

Validation: `scripts/test-diagnostics.ps1 -LiveSplitPath <LiveSplit>` covers bounded rotation, valid export, concurrent/overflow handling, write failure and recovery, private-data exclusion and actual TimerModel events. Existing native and menu suites exercise networking and room actions with the instrumented DLL.
