# Beta quick start / Schnellstart

## English

1. Install LiveSplit separately (tested: 1.8.34 on Windows 10/11, .NET Framework 4.8). Back up your splits/layout and try the beta in a separate LiveSplit copy first.
2. Download the component ZIP from this repository's Releases and copy `Components/LiveSplit.Coop.dll` into LiveSplit's `Components` directory. Restart LiveSplit. The archive does not include LiveSplit itself.
3. The server field is preset to the community relay (`wss://livesplit-coop-relay.janneck.workers.dev/coop`, a closed beta without availability guarantee). A group can instead run its own relay: follow [deployment](deployment.md) and use `wss://your-domain/coop`.
4. In LiveSplit, right-click **Edit Layout**, add **Control → Coop Relay**, then open its settings. **Host:** click **Create room (host)**; room, role and host key are filled in, and an invite code (`lscoop:1:…`) appears. Click **Copy** and send the invite to your teammates privately. **Teammates:** copy the invite, then click **Join with invite** (or paste it into the Room field). Save the layout and click **Connect**. The invite contains only the viewer key; never share the host key. On a relay without room creation, enter the operator's room and the key for your role by hand.
5. **Host:** load your own splits and autosplitter, choose **Host**, and connect. In a coop game this should be the person hosting the game lobby. Wait for **Host connected**.
6. **Viewers:** use a separate layout without a Scriptable Auto Splitter and deactivate the registered autosplitter in Edit Splits. Disable local timer-control hotkeys. Reset any local attempt before joining. Choose **Viewer** and connect; wait for **Following host**. Viewers do not select Duo/Trio or match the host's split file. Their visible layout remains their own; the host supplies the run and timing data.
7. Start the run on the host. Start, completed splits, pause, undo, reset and finish propagate. Ended times remain visible until the host resets or the viewer disconnects. The host uses **Save Splits** to preserve results; viewers do not automatically archive host history.

Use **Coop: Connect / Coop: Disconnect** in the LiveSplit context menu after setup. Connections do not start automatically after launching LiveSplit. On network loss or a temporarily full room the component retries; invalid room credentials or an incompatible protocol require you to correct the settings and connect again. A second client using the host key takes over the host slot, stopping the previous host connection. Running digits can lag by network delay; completed split and final times copy the host's exact values.

Keys stored in layouts are encrypted for that Windows account and machine. On another PC, enter that PC's role key again. Do not publish initialized layouts or private launcher packages. No VPN or router port-forwarding is required on player PCs. The relay operator exposes only the chosen TLS endpoint.

## Deutsch

1. LiveSplit separat installieren (getestet: 1.8.34, Windows 10/11, .NET Framework 4.8). Zuerst eine Kopie mit gesicherten Splits/Layouts verwenden.
2. Aus dem Komponenten-ZIP `Components/LiveSplit.Coop.dll` nach `Components` kopieren und LiveSplit neu starten.
3. Als Server ist der Community-Relay voreingestellt (geschlossene Beta ohne Verfügbarkeitsgarantie). Wer lieber selbst betreibt, folgt [Deployment](deployment.md) und trägt `wss://eure-domain/coop` ein.
4. **Edit Layout → Control → Coop Relay** hinzufügen. **Host:** **Create room (host)** klicken; Raum, Rolle und Host-Schlüssel werden ausgefüllt, darunter erscheint ein Einladungscode (`lscoop:1:…`). Mit **Copy** kopieren und privat an die Mitspieler schicken. **Mitspieler:** Einladungscode kopieren und **Join with invite** klicken (oder den Code ins Raumfeld einfügen). Layout speichern und **Connect** drücken. Der Code enthält nur den Viewer-Schlüssel; den Host-Schlüssel nie weitergeben. Bei einem Relay ohne Raum-Erstellung Raum und Schlüssel vom Betreiber von Hand eintragen.
5. Nur der **Host** verwendet seine Splits und seinen Autosplitter. **Viewer** deaktivieren ihren Autosplitter und lokale Timer-Hotkeys; Duo/Trio und Splits müssen sie nicht auswählen.
6. Sobald beim Host **Host connected** und bei Viewern **Following host** erscheint, kann der Host starten. Bei Netzwerkausfall oder vorübergehend vollem Raum verbindet sich die Komponente erneut; bei falschem Zugang oder inkompatiblem Protokoll Angaben korrigieren und nochmals **Connect** drücken. Der Host speichert die Ergebnisse anschließend mit **Save Splits**.

Die Beta enthält keine RV-Autosplitter, privaten PBs oder Zugangsschlüssel. Ein vollständiger echter Coop-Spielrun ist noch nicht verifiziert. Weitere Grenzen stehen in der README.
