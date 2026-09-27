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

`hostKey` darf nur der Spielhost haben. `viewerKey` ist für die Mitspieler. Gespeicherte Layouts enthalten den Schlüssel verschlüsselt für dieses Windows-Konto auf diesem PC. Beim Weitergeben eines einzelnen Layouts muss die andere Person ihren Schlüssel selbst eintragen. Ein vorbereitetes portables Paket erledigt dies über `Start-Coop.cmd` aus seiner privaten Zugangsdatei; solche Pakete nur innerhalb der eigenen Gruppe weitergeben.

## Noch ausstehend

Die automatisierten lokalen Tests und ein Test mit drei echten LiveSplit-Fenstern und Dashboard-Kopien sind durchgeführt. Start, Splits, Pause, Wiederverbindung, Zieleinlauf und Reset wurden mit Beispieldaten geprüft. Die Originaldateien blieben unverändert.

Am 27.09.2026 wurde nach Freigabe ein separater Relay auf einem Linux-VPS mit rootless Docker und direktem TLS auf Port 8443 eingerichtet. Die vorhandenen Webapps auf 80/443 blieben unverändert. 24 native Prüfungen mit einem Host und zwei Viewern bestanden auch über den öffentlichen WSS-Endpunkt; zusätzliche Prüfungen bestätigen die Zugriffsgrenzen. Zertifikatsaustausch ohne Verbindungsabbruch wurde lokal getestet, der unabhängige Kopierdienst auf dem VPS erfolgreich ausgeführt.

Ein vollständiger Duo-/Trio-Spielrun einschließlich echtem Auto-Ende steht weiterhin aus. Die sichtbaren Drei-Fenster-Prüfungen liefen lokal; deren zusätzliche WAN-Variante konnte bei gesperrtem Desktop nicht starten. Der erfolgreiche native WAN-Test verwendet dieselbe Coop-DLL und das echte LiveSplit-Timermodell.
