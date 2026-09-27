# Dependencies and provenance

- **LiveSplit**: https://github.com/LiveSplit/LiveSplit. The component references the user's installed `LiveSplit.Core.dll` and `UpdateManager.dll`. LiveSplit binaries are not redistributed by this repository. Build against a trusted installed release; the local prototype was tested against 1.8.34.
- **ws**: https://github.com/websockets/ws, MIT. Installed by npm; exact version and integrity are recorded in `relay/package-lock.json`. Its license is included in the installed npm package.
- **.NET Framework / Windows**: supplies WinForms, ClientWebSocket, JSON serialization and Windows DPAPI. No additional desktop process or VPN software is required.

The earlier feasibility review examined oJumpy/LiveSplit.TimerSync at commit `28fe3dbcdd8400e664a7273e07f1e220c618a457`, version 1.1. No license file was present in that checkout. This repository therefore contains **no copied source or binaries from TimerSync** and is not a licensed fork of that project. The relay protocol, component, settings and tests here were written independently against LiveSplit's public API. There is no affiliation with either upstream project.
