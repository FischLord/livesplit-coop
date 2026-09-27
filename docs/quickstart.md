# Beta quick start / Schnellstart

## English

1. Install LiveSplit separately (tested: 1.8.34 on Windows 10/11, .NET Framework 4.8). Back up your splits/layout and try the beta in a separate LiveSplit copy first.
2. Download the component ZIP from this repository's Releases and copy `Components/LiveSplit.Coop.dll` into LiveSplit's `Components` directory. Restart LiveSplit. The archive does not include LiveSplit itself.
3. Your group needs its own relay: use the separate relay ZIP or repository source and follow [deployment](deployment.md). The operator creates a room and gives the publisher its host key and teammates the separate viewer key. Use `wss://your-domain/coop`, or `wss://your-domain:8443/coop` for direct TLS. No public service is included.
4. In LiveSplit, right-click **Edit Layout**, add **Control → Coop Relay**, then open its settings. Enter server URL, room, role and the corresponding key. Save the layout.
5. **Host:** load your own splits and autosplitter, choose **Host**, and connect. In a coop game this should be the person hosting the game lobby. Wait for **Host connected**.
6. **Viewers:** use a separate layout without a Scriptable Auto Splitter and deactivate the registered autosplitter in Edit Splits. Disable local timer-control hotkeys. Reset any local attempt before joining. Choose **Viewer** and connect; wait for **Following host**. Viewers do not select Duo/Trio or match the host's split file. Their visible layout remains their own; the host supplies the run and timing data.
7. Start the run on the host. Start, completed splits, pause, undo, reset and finish propagate. Ended times remain visible until the host resets or the viewer disconnects. The host uses **Save Splits** to preserve results; viewers do not automatically archive host history.

Use **Coop: Connect / Coop: Disconnect** in the LiveSplit context menu after setup. Connections do not start automatically after launching LiveSplit. On network loss the viewer freezes and reconnects automatically. Running digits can lag by network delay; completed split and final times copy the host's exact values.

Keys stored in layouts are encrypted for that Windows account and machine. On another PC, enter that PC's role key again. Do not publish initialized layouts or private launcher packages. No VPN or router port-forwarding is required on player PCs. The relay operator exposes only the chosen TLS endpoint.

## Deutsch

1. LiveSplit separat installieren (getestet: 1.8.34, Windows 10/11, .NET Framework 4.8). Zuerst eine Kopie mit gesicherten Splits/Layouts verwenden.
2. Aus dem Komponenten-ZIP `Components/LiveSplit.Coop.dll` nach `Components` kopieren und LiveSplit neu starten.
3. Einer betreibt den Relay gemäß [Deployment](deployment.md). Eigener Server und eigene Raumzugänge sind erforderlich. Kein öffentlicher Dienst ist enthalten.
4. **Edit Layout → Control → Coop Relay** hinzufügen; Serveradresse, Raum, Rolle und passenden Schlüssel eintragen. Layout speichern.
5. Nur der **Host** verwendet seine Splits und seinen Autosplitter. **Viewer** deaktivieren ihren Autosplitter und lokale Timer-Hotkeys; Duo/Trio und Splits müssen sie nicht auswählen.
6. **Coop: Connect** drücken. Sobald der Host verbunden ist und die Viewer **Following host** anzeigen, kann der Host starten. Er speichert anschließend die Ergebnisse mit **Save Splits**.

Die Beta enthält keine RV-Autosplitter, privaten PBs oder Zugangsschlüssel. Ein vollständiger echter Coop-Spielrun ist noch nicht verifiziert. Weitere Grenzen stehen in der README.
