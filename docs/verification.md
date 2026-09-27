# Local verification

Verified on Windows with installed LiveSplit 1.8.34 and Node 25.9.0. The relay targets Node 22+; the Dockerfile selects Node 24 LTS. Container execution and a public TLS endpoint have not been tested on this machine.

`npm test`: six integration/validation tests covering multiple viewers, late joining, exact nullable RTA data, host/viewer authorization, duplicate host rejection, disconnect/offline final cache, host reconnection, old sequence rejection, malformed messages and the health endpoint.

`scripts/test-native.ps1`: 24 checks using the actual installed LiveSplit.Core, the built Coop DLL and native .NET WebSocket clients connected to an isolated local Node relay. Covers RTA without synthesized game time, exact checkpoint/end values, PB import, interpolation, stale freezing, pause, undo, reset, skip, paused GT, original run restoration, two simultaneous viewers, late joining, automatic reconnection, host-loss notification and component construction/settings/rendering.

The build uses warnings as errors. Native tests create only in-memory sample runs and start/stop their own loopback relay. No game process, real autosplitter, production SSH connection, active LiveSplit instance or saved split/layout is modified.

Still required before calling this a release: manual layout/UI check in a copied LiveSplit installation, full Duo/Trio game run, real WAN/WSS disconnect/reconnect, container/permission check on Linux, certificate renewal and proxy integration review. Event-driven third-party components and history-dependent comparisons need additional compatibility coverage.
