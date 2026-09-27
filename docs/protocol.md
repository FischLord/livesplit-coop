# Protocol v1

Transport: UTF-8 JSON text messages over WSS at `/coop`. Maximum message size 256 KiB; no compression. Plain WS is development-only on loopback. Credentials never appear in the URL. Browser Origin requests are rejected; this endpoint is for native clients.

The first message, within five seconds, is `{ "type":"hello", "v":1, "room":"coop", "role":"host|viewer", "key":"..." }`. The relay responds with `ready`. Room names are preconfigured, not created by unauthenticated traffic. A second simultaneous host is rejected. Invalid authentication/messages close with 1008.

The host publishes `snapshot` messages. See `component/Snapshot.cs` and `relay/src/protocol.mjs` for the schema. Times are nullable integer .NET ticks (100 ns), bounded to seven days, well inside JavaScript's exact integer range. Missing Game Time remains `null`. Snapshots include every segment, completed times, PBs, best segments and named comparisons. All array indexes are validated. Ended snapshots must exactly match the last split's values.

`seq` must increase within a host connection. The relay ignores older/duplicate sequence values. Reconnecting creates a new connection sequence domain. `runId` identifies the in-memory run; `attemptId` changes on start/reset. No receiver-side command replay is necessary: a newer snapshot replaces the displayed state.

Viewers receive `{ "type":"state", "live":true|false, "ageMs":0, "snapshot":{...} }`. A new viewer immediately receives the cached snapshot, if any. Cached snapshots include their server residence age; disconnected/old snapshots are not considered live. `{ "type":"host", "online":false }` announces loss of the publisher. A host reconnect must publish afresh before existing viewers resume.

The component interpolates running time using a local monotonic stopwatch since receipt, plus the cached packet's age. It does not claim network-clock synchronization or subtract guessed latency. When stale, interpolation is bounded to 1.5 seconds and frozen. Paused/final times and all completed split values are never interpolated. Missing heartbeats and slow clients are disconnected; application `ping` gets `pong` and WebSocket ping/pong checks transport liveness.

The relay retains only the latest snapshot per configured room in memory, even after a host disconnect, until another snapshot replaces it or the service restarts. It does not write run data to disk, execute commands, accept arbitrary filenames or retrieve URLs supplied by clients.
