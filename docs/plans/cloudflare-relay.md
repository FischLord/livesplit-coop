# Feature-Plan: Community-Relay auf Cloudflare Durable Objects

**Status:** Phasen 1–3 erledigt; Relay deployt unter `livesplit-coop-relay.janneck.workers.dev` (geschlossene Beta) · offen: Test über das öffentliche Netz, Phase 4–5 · **Stand:** 2026-09-28 · **Basis:** Protokoll v3

## Ziel

Spieler sollen LiveSplit Coop nutzen können, ohne selbst einen Server zu betreiben. In der Komponente klickt man „Raum erstellen“ und gibt einen Einladungscode an die Mitspieler weiter, fertig.

Selbst hosten bleibt vollständig möglich: Das Docker-Relay bleibt bestehen, und die Komponente akzeptiert weiterhin jede eigene `wss://`-Adresse.

### Nicht-Ziele (erste Version)

- Keine Benutzerkonten, Logins oder Profile.
- Keine dauerhafte Run-Historie auf dem Server; der Host bleibt die einzige Quelle für gespeicherte Splits.
- Keine Ende-zu-Ende-Verschlüsselung (siehe „Offene Entscheidungen“).
- Keine Web-Ansicht für Zuschauer im Browser.

## Architektur

```text
LiveSplit (Host)   ──WSS──┐
LiveSplit (Viewer) ──WSS──┤  Cloudflare Worker  ──►  Durable Object „Raum <id>“
LiveSplit (Viewer) ──WSS──┘  (Routing, Limits)       (Zustand, Relay-Logik, SQLite)
```

- **Worker** (zustandslos): nimmt HTTP-Anfragen und WebSocket-Upgrades an, prüft Origin/Pfad/Rate-Limits und leitet an das Durable Object des Raums weiter.
- **Ein Durable Object pro Raum**: Cloudflare garantiert genau eine Instanz pro Raum-ID weltweit. Host und Viewer landen automatisch im selben Objekt. Das entspricht dem heutigen `sessions`-Eintrag in `relay/src/server.mjs`.
- `relay/src/protocol.mjs` (Validierung von Snapshot und Tick) wird unverändert von beiden Relay-Varianten genutzt.

## Protokoll v3 (Änderungen gegenüber v2)

| Thema | v2 heute | v3 |
|---|---|---|
| Raum-Adressierung | Raumname in der `hello`-Nachricht | Raum-ID im Pfad: `wss://<relay>/coop/<roomId>`. Der Worker muss das Durable Object **vor** dem Upgrade kennen. Der Key bleibt in `hello`, nie in der URL. |
| Räume anlegen | nur per `rooms.json` | `POST /rooms` liefert `{ roomId, hostKey, viewerKey, expiresAt }` |
| Einladung | Adresse, Raum und Key einzeln | ein Einladungscode, z. B. `lscoop:v1:<relay-host>/<roomId>/<viewerKey>` |
| Liveness-Ping | JSON `{"type":"ping"}` alle 5 s | über `setWebSocketAutoResponse` beantworten, damit das Objekt nicht aufwacht (Verhalten vor Umsetzung verifizieren) |
| Raum abgelaufen | – | Close-Code `4004 Room expired or unknown`, die Komponente zeigt einen verständlichen Hinweis |

Das Docker-Relay unterstützt v3 ebenfalls: `/coop/<roomId>` plus die optionale Raum-Erstellung. Dann gilt dieselbe Komponente für beide Varianten, und `/coop` mit `rooms.json` bleibt als Kompatibilitätsmodus erhalten.

## Durable Object: Design

### Zustand

| Daten | Ort | Grund |
|---|---|---|
| Hashes von Host-/Viewer-Key (SHA-256), `createdAt`, `lastActivity` | SQLite (Storage) | muss Neustarts und Hibernation überstehen |
| letzter **kompletter** Snapshot | SQLite, nur bei Strukturänderung geschrieben (Split, Pause, Reset …) | späte Beitritte nach Hibernation; wenige Schreibvorgänge |
| letzter Tick | Attachment der Host-Verbindung (`serializeAttachment`, max. 16 KiB) | Ticks nie in den Storage schreiben, sonst Schreiblimits |
| Rolle, Sequenz, Rate-Limit-Fenster je Verbindung | Attachment der Verbindung | übersteht Hibernation |

Die Keys werden nur als Hash gespeichert, weil sie 256 Bit zufällig sind; ein einfacher SHA-256-Vergleich in konstanter Zeit genügt.

### Regeln für Hibernation

Das ist der wichtigste Kostenfaktor. Laufzeit wird nicht berechnet, solange das Objekt hibernieren darf.

- Nur die Hibernation-API verwenden (`ctx.acceptWebSocket`, `webSocketMessage`, `webSocketClose`, `ctx.getWebSockets()`), keine `ws`-Bibliothek.
- **Kein `setInterval`/`setTimeout`** im Objekt, sonst läuft die Laufzeitabrechnung durchgehend weiter:
  - Das heutige Heartbeat-Intervall entfällt; Cloudflare beantwortet Protokoll-Pings selbst.
  - Die 5-Sekunden-Frist für die Anmeldung wird beim nächsten Ereignis geprüft oder per `ctx.storage.setAlarm()` umgesetzt.
- Der Konstruktor baut den Zustand aus Storage und Attachments neu auf, denn In-Memory-Zustand geht bei Hibernation verloren.

### Ablauf und Aufräumen

- Jede Aktivität setzt `lastActivity`. Ein Alarm löscht den Raum nach **7 Tagen** ohne Aktivität (`deleteAll`).
- Maximal 12 Teilnehmer pro Raum, ein Platz ist für den Host reserviert. Host-Übernahme per 4001 wie in v2.

## Worker: Routing und Missbrauchsschutz

- `POST /rooms`: Raum-Erstellung, **Rate-Limit pro IP** (`CF-Connecting-IP`), z. B. 5 Räume pro Stunde. Umsetzung über das Rate-Limiting-Binding von Workers oder ein kleines Limiter-Objekt.
- `GET /coop/<roomId>`: nur Upgrade-Anfragen, keine `Origin`-Header (Browser weiterhin ausgeschlossen), gültiges ID-Format; dann Weiterleitung an das Objekt.
- `GET /healthz` wie heute.
- Globale Obergrenzen als Konfiguration: maximale Räume pro Tag, Kill-Switch für neue Räume.
- Die bestehenden Limits bleiben: 256 KiB pro Nachricht, 30 Nachrichten pro Sekunde, Validierung jedes Snapshots und Ticks.

## Komponente (Windows)

- Neuer Button **„Raum erstellen“** (nur für die Rolle Host). Er ruft `POST /rooms` auf, übernimmt Raum-ID und Host-Key und zeigt den Einladungscode zum Kopieren an.
- Neues Feld **„Einladungscode einfügen“** für Viewer, das Adresse, Raum und Key automatisch ausfüllt.
- Server-URL mit Voreinstellung auf den Community-Server; eigene URL bleibt möglich.
- Verständliche Meldungen für abgelaufene Räume (4004) und veraltete Versionen.
- `Start-Coop.ps1`: `Zugang.private.json` kann alternativ einen Einladungscode enthalten.

## Kosten (Workers Free Plan)

Die Zahlen stammen von der [Preisseite](https://developers.cloudflare.com/durable-objects/platform/pricing/) und der [Limits-Seite](https://developers.cloudflare.com/durable-objects/platform/limits/) (Stand 2026-09): 100.000 Requests pro Tag, eingehende WebSocket-Nachrichten im Verhältnis 20:1, ausgehende Nachrichten und Protokoll-Pings kostenlos, 13.000 GB-s pro Tag, im Free-Plan nur SQLite-basierte Durable Objects, 5 GB Speicher pro Account.

Grobe Schätzung pro aktivem Raum: Der Host schickt 4 Ticks pro Sekunde, also etwa 14.400 eingehende Nachrichten pro Stunde oder rund **720 abgerechnete Requests pro Stunde**. Damit reicht der Free-Plan für **etwa 130 Raum-Stunden pro Tag**, z. B. 30 Gruppen mit je 4 Stunden. Die Laufzeit bleibt gering, solange die Hibernation-Regeln eingehalten werden.

Mögliche Hebel, falls das knapp wird:
- Ticks nur während `Running` senden, sonst ein Heartbeat alle 5 s; die Viewer-Schwelle muss dafür phasenabhängig werden.
- Die Tick-Rate auf 2 pro Sekunde senken. Die Interpolation deckt das ab, die Offline-Erkennung wird etwas langsamer.
- Wechsel in den Workers-Paid-Plan (ab ca. 5 USD pro Monat).

**Vor der Umsetzung prüfen:** die Speicher- und Schreibkontingente von SQLite im Free-Plan und ob Nachrichten mit Auto-Response als Requests zählen. Beides ist aus den gelesenen Seiten nicht eindeutig.

## Betrieb und Deployment

- Neues Verzeichnis `relay-cloudflare/` mit `wrangler.toml`, dem Worker und der Durable-Object-Klasse, und es importiert `relay/src/protocol.mjs`.
- Tests mit `@cloudflare/vitest-pool-workers` (lokale Workers-Laufzeit); die bestehenden Protokoll-Tests laufen gegen beide Relays.
- Deployment per GitHub Actions (`wrangler deploy`) mit Secret `CLOUDFLARE_API_TOKEN`, getrennte Umgebungen `staging` und `production`.
  - Hinweis: Workflow-Dateien liegen bisher nur als Vorlage unter `docs/ci/`, weil das Pushen von Workflows noch nicht freigegeben ist. Dafür braucht der Push-Zugang den `workflow`-Scope.
- Eigene Domain optional; `*.workers.dev` reicht technisch aus (TLS inklusive).
- Monitoring: Workers Analytics (Requests, Fehler), Alarm bei 80 % des Tageskontingents.
- Versionierung: Protokollversion in `hello`; das Relay lehnt veraltete Clients mit Update-Hinweis ab, wie in v2 bereits umgesetzt.

## Recht und Organisation

Keine Rechtsberatung, sondern Punkte, die vor dem öffentlichen Start geklärt sein müssen:

- **Impressum** und **Datenschutzerklärung** für den öffentlichen Dienst: Der Dienst verarbeitet IP-Adressen (Rate-Limits) und Timer-Daten.
- Cloudflare als Auftragsverarbeiter: DPA/AVV von Cloudflare prüfen, Serverstandorte und Datenfluss dokumentieren.
- Datensparsamkeit: keine Speicherung von IPs über das Rate-Limit-Fenster hinaus, keine Inhalts-Logs, Löschung nach 7 Tagen Inaktivität.
- Transparenz im README: Der Betreiber kann Timer-Daten (Splits, PBs, Spiel/Kategorie) technisch sehen.
- Nutzungsbedingungen: kurz, ohne Verfügbarkeitsgarantie, mit Missbrauchsregeln.
- Kontaktweg für Missbrauchsmeldungen.

## Phasen

| Phase | Inhalt | grober Aufwand |
|---|---|---|
| 0. Prüfen | offene Kostenfragen klären, Cloudflare-Account, Entscheidung zu E2E und Domain | 0,5 Tag |
| 1. Protokoll v3 | Raum-ID im Pfad, `POST /rooms`, Einladungscode, 4004; im Docker-Relay umsetzen und testen | 1–1,5 Tage |
| 2. Durable-Object-Relay | Worker, Objekt, Hibernation, Storage, Alarme; Tests in der Workers-Laufzeit | 2 Tage |
| 3. Komponente | „Raum erstellen“, Einladungscode, Voreinstellung, Fehlermeldungen; native Tests und Trio-Test | 1–1,5 Tage |
| 4. Betrieb | Staging-Deployment, Lasttest gegen Staging, Monitoring, Actions-Workflow | 1 Tag |
| 5. Start | Impressum/Datenschutz, README/Anleitungen, Beta mit wenigen Gruppen, danach öffentlich | nach Bedarf |

## Risiken

| Risiko | Gegenmaßnahme |
|---|---|
| Free-Kontingent reicht bei Wachstum nicht | Tick-Hebel oben; Paid-Plan; Kill-Switch für neue Räume |
| Hibernation falsch umgesetzt, dadurch hohe Laufzeitkosten | keine Timer im Objekt; Test, der prüft, dass keine Intervalle registriert sind; Analytics beobachten |
| Missbrauch als allgemeines Relay für fremde Daten | strikte Validierung jedes Snapshots und Ticks (bereits vorhanden), 256-KiB-Limit, Raum-Limits pro IP |
| Abhängigkeit von einem Anbieter | Docker-Relay bleibt gleichwertig; dieselbe Komponente spricht beide |
| Betreiberpflichten (Recht, Support) | klein starten (geschlossene Beta), klare Nutzungsbedingungen |

## Entscheidungen (2026-09-28)

- Keine Ende-zu-Ende-Verschlüsselung in v1; im README transparent dokumentieren.
- Domain: `*.workers.dev`.
- Räume verfallen nach 7 Tagen ohne Aktivität.
- Cloudflare-Account ist vorhanden.

Umsetzung Phase 1 abweichend vom Entwurf: `hello` enthält keinen Raum mehr, und der nackte Pfad `/coop` bleibt nur offen, damit v1/v2-Clients die Update-Meldung erhalten. `POST /rooms` liefert `idleExpiryDays` statt `expiresAt`, weil der Ablauf an Inaktivität hängt. Phase 3: Einladungscode `lscoop:1:<server>/<room>#<viewerKey>` (die Server-URL steht im Code, damit auch eigene Relays funktionieren). Die Server-URL ist für neue Komponenten auf den Community-Relay voreingestellt; bestehende Layouts behalten ihre Adresse.

## Ursprünglich offene Entscheidungen

1. **Ende-zu-Ende-Verschlüsselung?** Sie würde verhindern, dass der Betreiber Timer-Daten sieht. Dann kann das Relay aber weder validieren noch Ticks zusammenführen. Empfehlung: in v1 nicht, stattdessen transparent dokumentieren.
2. **Domain:** `*.workers.dev` oder eine eigene Domain wie `coop.<deine-domain>`?
3. **Ablaufzeit der Räume:** 7 Tage? Oder feste Räume für Stammgruppen verlängerbar machen?
4. **Wer betreibt den Dienst offiziell**, also wer steht im Impressum?
5. **Voreinstellung der Server-URL** in der Komponente: Community-Server oder leer?
