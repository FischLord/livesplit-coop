# Einrichtung und Arbeitsstand

LiveSplit Coop ist ein selbst hostbarer Coop-Timer. Der Spielhost misst; alle Mitspieler sehen seine Werte in ihren eigenen LiveSplit-Fenstern. Auf den Spiel-PCs ist nur eine zusätzliche LiveSplit-DLL nötig. Kein VPN, kein separates Begleitprogramm.

## Zuerst lokal testen

1. DLL mit `scripts/build.ps1` bauen (siehe README).
2. Für jeden Teilnehmer eine eigene Testkopie von LiveSplit anlegen. Original-Splits und Original-Layouts nicht überschreiben.
3. `dist/LiveSplit.Coop.dll` nach `Components` der Testkopie kopieren; LiveSplit neu starten.
4. Im Layout **Control → Coop Relay** hinzufügen.
5. Serveradresse, Raum und Zugangsschlüssel eintragen. Host wählt **Host**, Mitspieler **Viewer**.
6. Nur beim Host bleibt der Autosplitter aktiv. Beim Viewer sowohl den registrierten Autosplitter deaktivieren als auch die Layout-Komponente **Scriptable Auto Splitter** entfernen. Ein laufender lokaler Timer verhindert den Viewer-Beitritt.
7. **Connect** drücken. Verbindungen starten absichtlich nicht automatisch.

Für einen lokalen Test nutzt ihr `ws://127.0.0.1:8787/coop` auf demselben PC. Bei verschiedenen PCs wird ein per HTTPS/WSS erreichbarer Relay benötigt, etwa `wss://timer.example.com/coop`. Ein localhost-Link ist nicht die Adresse für eure Mitspieler.

## Verhalten

- Der Host behält seine Messmethode, seinen Autosplitter und seine maßgebliche Splitdatei.
- Mitspieler erhalten auch vergangene Checkpoints, PBs und Bestsegmente. Real Time bleibt Real Time; leere Game-Time-Werte werden nicht erfunden.
- Der Viewer arbeitet mit einer temporären Run-Kopie ohne Dateipfad. **Disconnect** stellt die zuvor geladenen lokalen Splits wieder her.
- Die Endzeit wird exakt vom Host übernommen. Die laufende Anzeige kann durch die Übertragung etwas nachlaufen.
- Bei Verbindungsabbruch erscheint **OFFLINE — timer frozen**. Spätestens nach 1,5 Sekunden ohne frischen Snapshot wird angehalten. Nach Wiederverbindung folgt der Viewer dem vollständigen aktuellen Zustand.
- Nach Ende bleibt das Ergebnis sichtbar, solange ihr verbunden seid und der Host den Run nicht zurücksetzt. Für dauerhaftes Aufbewahren gilt zunächst die beim Host gespeicherte `.lss`-Datei. Automatischer Ergebnisexport bei allen Teilnehmern ist noch nicht implementiert.

## Schlüssel

`hostKey` darf nur der Spielhost haben. `viewerKey` ist für die Mitspieler. Gespeicherte Layouts enthalten den Schlüssel verschlüsselt für dieses Windows-Konto auf diesem PC. Beim Weitergeben eines Layouts muss die andere Person ihren Schlüssel selbst eintragen.

## Noch ausstehend

Die automatisierten lokalen Tests sind durchgeführt. Ein echter Duo-/Trio-Test, ein öffentlicher TLS-Endpunkt und ein vollständiger Spielrun stehen noch aus. Der bestehende VPS wurde weder per SSH geprüft noch verändert; vor der Installation müssen nginx-Einbindung und Zertifikatserneuerung anhand seines tatsächlichen Zustands geplant werden.
