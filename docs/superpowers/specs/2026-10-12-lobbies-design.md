# Lobbys, Timer-GUI, Profil-Pakete und Viewer-Splits

Stand: 2026-10-12 · Branch `feature/lobbies` · Basis: Graph/Category-Patch v4 (`a6f6099`)

## Ziel

Coop ohne kryptische Einladungslinks und ohne CMD-Fragen:

1. **Lobbys** mit Namen und optionalem Passwort, wahlweise öffentlich gelistet oder privat.
2. **Ein Fenster „Coop…“ im Timer** nur für Lobby und Status. Spiel-, Modus- und Profilwahl gehören nicht dazu.
3. **Generisch:** Die Komponente kennt kein Spiel. Spielwissen steckt in einem optionalen
   Daten-Paket (`coop-profiles.json`). RV There Yet? ist das erste Paket.
4. **Viewer speichern Splits:** Mitspieler zeichnen mitverfolgte Versuche in ihre eigenen
   Splits-Dateien auf und haben so eine eigene Historie, falls sie später selbst hosten.

Nicht-Ziele: Accounts, Kick/Ban, Passwort ändern, ein gemeinsames Programm für Host und Viewer,
Lobbys im selbst gehosteten Node-Relay (das bekommt sie in einem späteren Schritt; bis dahin
meldet die GUI dort „Server unterstützt keine Lobbys“ und bietet den Einladungscode an).

## 1. Schichten

| Teil | Ort | Inhalt |
|---|---|---|
| Relay (Cloudflare) | `relay-cloudflare/` | Räume wie bisher + Lobby-Endpunkte + Verzeichnis + Fehlversuchs-Limit |
| Coop-Komponente | `component/` | Verbindung, Snapshot/Mirror, Lobby-Client, Coop-Fenster (nur Lobby), Profil-Katalog (`ProfilePack`, `ProfilePreparer`), Viewer-Recorder |
| Spiel-Paket | z. B. `LS RVtheer/Timer/` | `coop-profiles.json`, Layouts, Autosplitter, Splits-Vorlagen, schlanker Starter |

Keine Zeile Spielwissen in `component/`. `RunProfile.cs` (RV-Pfadlogik) wird durch den
Profil-Katalog ersetzt: Variablen kommen aus dem Paket-Profil, dessen Splits-Datei geladen ist.

## 2. Lobbys (Relay)

**Identität.** Lobby-Name: 3–32 Zeichen `[a-z0-9_-]`, Eingabe wird kleingeschrieben und getrimmt.
Raum-ID = `lobby_<name>`; WebSocket-Pfad wie bisher `/coop/<raum-id>`, Protokoll v3 unverändert.

**Schlüssel.**
- Viewer-Schlüssel = base64url(PBKDF2-HMAC-SHA256(Passwort, Salt `livesplit-coop-lobby:v1:<name>`,
  100 000 Iterationen, 32 Byte)), berechnet **im Client**. Das Passwort verlässt den Rechner nie.
  Leeres Passwort ist erlaubt (offene Lobby, nur Zuschauen) und ergibt ebenfalls einen Schlüssel.
- Host-Schlüssel: zufällig vom Relay, nur beim Erstellen übertragen, lokal DPAPI-geschützt.
- Der Relay speichert wie bisher nur SHA-256 beider Schlüssel.

**Endpunkte** (`POST` nur für native Clients ohne `Origin`-Header, wie `/rooms`; `GET /lobbies` ist eine öffentliche Leseabfrage ohne Origin-Beschränkung):
- `POST /lobbies` Body `{name, viewerKey, public, locked}` → `201 {roomId, hostKey, idleExpiryDays}`;
  `409` Name vergeben; `400` ungültig; `429`/`503` wie `/rooms` (gleiches IP-Limit, gleiches Tageskontingent).
- `GET /lobbies` → `200 {lobbies:[{name, locked, hostOnline, viewers, game, category, label, updated}]}`,
  nur öffentliche, max. 100, sortiert: Host online zuerst, dann zuletzt aktiv. Lobbys ohne Aktivität
  seit 24 h werden nicht gelistet.
- `locked` sagt nur, ob ein Passwort gesetzt ist (für das Schloss-Symbol); der Client setzt es.

**Verzeichnis.** Ein Durable Object `Directory` („global“) hält die öffentlichen Einträge.
Der Raum meldet: Erstellen, Host verbunden/getrennt, Viewerzahl, Strukturänderung des Snapshots
(Spiel, Kategorie, `profileLabel`); höchstens ein Verzeichnis-Update pro Raum alle 10 s, Verzeichnisfehler stören nie das Relaying, bei Host online stündliche Auffrischung, Ablauf → Eintrag entfernen.

**Fehlversuche.** Der Worker reicht `CF-Connecting-IP` als `X-Coop-IP` an den Raum (ein vom Client
gesendeter Header wird überschrieben); der Raum merkt sie im Socket-Attachment. Jeder Raum zählt
fehlgeschlagene `hello` pro IP in seinem Storage (`fail:<ip>`, Fenster 60 s). Ab 10 Fehlversuchen
wird diese IP **vor** der Schlüsselprüfung mit `1008 "Too many attempts"` abgewiesen, auch mit
richtigem Passwort – sonst könnte ein Angreifer den Treffer trotzdem erkennen. (Ein Rate-Limit-Binding
eignet sich nicht, weil es sich nicht abfragen lässt, ohne zu zählen.)

**Ablauf.** Wie bisher 7 Tage ohne Nutzung → Lobby gelöscht, Name wieder frei.

## 3. Komponente

### 3.1 Lokaler Zustand `coop-local.json`
Neben `LiveSplit.exe` der jeweiligen Kopie:
`{version:1, lastProfile, lobby:{server, name, roomId, role, public, locked, protectedHostKey, protectedViewerKey, invite?}}`.
Schlüssel DPAPI (CurrentUser). Layouts enthalten **keine** Raum-Zugangsdaten mehr; beim Laden
alter Layouts werden vorhandene Raumdaten einmalig nach `coop-local.json` übernommen (falls dort
leer) und nicht mehr ins Layout geschrieben. „Save Layout“ ist für Coop nie mehr nötig.
`lastHostLobby` (gleiche Form wie `lobby`) merkt sich die zuletzt verlassene benannte Host-Lobby, damit sie im Fenster wieder geöffnet werden kann.
`migratedLegacyRooms` ist die Liste der aus Layouts übernommenen Raum-IDs, damit ein verlassener alter Raum nicht erneut migriert wird.

### 3.2 Coop-Fenster (Rechtsklick → „Coop…“)
Ersetzt Host…, Join…, Copy invite, Reconnect, Disconnect. Bereiche:
- Das Fenster ist spielunabhängig und nur für die Lobby da. Es gibt keine Profilwahl und keinen
  Profilwechsel im Timer. Das Profil-Label des Hosts (Snapshot `profileLabel`) steht in der
  Lobby-Info, z. B. „rvgang · privat · Duo / Fullgame“; Viewer sehen so, was der Host läuft.
- **Lobby, verbunden:** Name · öffentlich/privat · Rolle · Mitspieler online · Status-Punkt;
  „Passwort kopieren“ (Host), „Lobby verlassen“.
- **Lobby, nicht verbunden:** in der Viewer-Kopie *Beitreten* (öffentliche Liste + Aktualisieren,
  Name, Passwort, Beitreten); in der Host-Kopie *Erstellen* (Name, Passwort, „In Liste anzeigen“)
  und, falls vorhanden, „Letzte Lobby <name> wieder öffnen“. Die Rolle folgt aus der Kopie: die
  Host-Kopie (mit Autosplitter) misst, die Viewer-Kopie folgt.
- **Erweitert** (eingeklappt): Server-Adresse, „Mit Einladungscode beitreten“ (Legacy v3-Räume),
  „Diagnose exportieren“.
- Texte Deutsch/Englisch nach `CultureInfo.CurrentUICulture`.

Beim Start verbindet die Komponente automatisch mit der Lobby aus
`coop-local.json`. Statuszeile im Layout: `Coop: rvgang · Host · 2 online` bzw.
`Coop: rvgang · folgt Host`, rechts weiter das Profil-Label.

### 3.3 Profil-Paket `coop-profiles.json`
Gesucht neben `LiveSplit.exe` und in bis zu drei Elternordnern; Pfade darin relativ zur Datei.

```json
{
  "version": 1,
  "game": "RV There Yet?",
  "autosplitter": "Autosplitter/RVThereYet-AllMaps.asl",
  "viewerLayout": "Layouts/Viewer.lsl",
  "server": "wss://your-relay.your-subdomain.workers.dev/coop",
  "dimensions": [
    {"id": "map", "label": {"de": "Karte", "en": "Map"},
     "options": [{"id": "valley", "label": "Mabutts Valley"}]}
  ],
  "profiles": [
    {"select": {"map": "valley", "mode": "ac", "players": "duo", "rules": "nmg"},
     "splits": "Splits/Mabutts Valley/All Checkpoints/No Major Glitches/Duo.lss",
     "layout": "Layouts/Host-Valley.lsl",
     "label": "Duo / Fullgame",
     "variables": {"Number of Players": "Duo", "Glitch Type": "No Major Glitches"}}
  ]
}
```

`server` ist optional: ein vorbelegter Relay (`wss://host/coop`, `ws://` nur auf Loopback; leer oder
fehlend ist erlaubt, alles andere lehnt `Validate()` ab). Die Komponente hat keinen eingebauten Server;
`CoopComponent.Server` ist der in `coop-local.json` gespeicherte Wert, sonst `server` aus dem Paket, sonst leer.
Ohne Server zeigt das Coop-Fenster einen Hinweis, öffnet „Erweitert“ und sperrt Erstellen/Beitreten/Liste.

**Profilwahl ist Aufgabe des Spiel-Pakets**, nicht der Komponente. Der Starter des Pakets
(RV: Start-CMD / `Start-Timer.ps1`) fragt je Dimension aus `coop-profiles.json` (Enter = zuletzt
benutzte Auswahl aus `lastProfile`), löst das Profil mit `ProfilePack.Resolve` auf, ruft
`ProfilePreparer.Prepare` (Layout-ScriptPath auf Paket-Autosplitter, Start/Split aktiv, `LayoutPath`
und Variablen in die LSS, Variablen mit Rand-Leerzeichen als CDATA; Zeiten bleiben unverändert) und
`RememberInSettings` auf, speichert `lastProfile` und startet LiveSplit. Gibt es für die Kombination
kein Profil, fragt der Starter erneut. Ein Profilwechsel im laufenden Timer entfällt (LiveSplits
`LoadRunFromFile` übernimmt den Run nicht verlässlich); wer wechseln will, startet neu.

Der Viewer-Starter stellt keine Fragen. Die Lobby wird im Timer über „Coop…“ gewählt.
Ohne Paket verhält sich die Komponente wie bisher (Splits über LiveSplits „Open Splits“).

### 3.4 Snapshot-Erweiterung (optional, abwärtskompatibel)
`runVariables` (gibt es schon) und neu `profileLabel` (≤ 60 Zeichen). Der Relay leitet optionale
Felder unverändert weiter; alte Viewer ignorieren sie.

### 3.5 Viewer-Recorder
- **Ziel-Datei:** Paket-Profil mit gleichem Spiel, gleicher Kategorie und passenden Variablen;
  sonst `Splits/Coop/<Spiel>/<Kategorie>[ <Variablen>].lss` neben der App (ungültige Zeichen ersetzt).
- **Anlegen:** fehlt die Datei, wird sie aus Segmentnamen, PB-Splits und Golds des Hosts erzeugt.
- **Aufzeichnen:** pro `attemptId` des Hosts. `Running→Ended` = abgeschlossener Versuch,
  `Running/Paused→NotRunning` = abgebrochener Versuch. Die Aktualisierung (Versuchszahl,
  Attempt-History, Segment-History, Golds, PB) läuft über LiveSplits eigene `TimerModel`-Logik auf
  einem Schatten-`LiveSplitState`; danach Speichern mit dem LiveSplit-XML-Saver (atomar: temp + rename).
- **Host-PB übernehmen:** beim ersten Snapshot einer Sitzung, wenn die Host-PB schneller ist,
  PB-Splits und schnellere Golds übernehmen; im Diagnose-Log vermerkt.
- **Sperre:** hat eine lokale Host-Kopie dieselbe Datei geladen (Sperrdatei `<datei>.coop-lock`
  mit PID), schreibt der Recorder nach `Splits/Coop/Pending/` und meldet das im Coop-Fenster.
- Nur verbunden mitverfolgte Versuche werden aufgezeichnet; Abbrüche der Verbindung mitten im
  Versuch zählen nicht als Versuch.

## 4. Fehlerfälle (Texte im Fenster, nicht als MessageBox-Kaskade)
| Situation | Meldung / Verhalten |
|---|---|
| Name vergeben | „Diese Lobby gibt es schon. Anderen Namen wählen oder beitreten.“ |
| Lobby unbekannt (4004) | „Lobby nicht gefunden oder abgelaufen.“ |
| Falsches Passwort (1008 Auth) | „Passwort falsch.“ |
| Zu viele Versuche | „Zu viele Fehlversuche. Eine Minute warten.“ |
| Relay ohne `/lobbies` (404) | „Server unterstützt keine Lobbys – Einladungscode unter Erweitert.“ |
| Netzwerk weg | automatischer Reconnect wie bisher; Status „verbinde neu…“ |
| Recorder kann nicht schreiben | Versuch nach `Pending/`, Hinweis im Fenster, Diagnose-Eintrag |

## 5. Tests
- **Relay:** `node --test` gegen `wrangler dev`: Lobby erstellen, 409, Listing (öffentlich/privat,
  24-h-Filter, Viewerzahl, Host online), Viewer mit richtigem/falschem Passwortschlüssel,
  Fehlversuchs-Limit, Ablauf entfernt Verzeichniseintrag, `/rooms` + Einladungen unverändert.
- **Komponente** (bestehender csc-Testharness `tests/*.cs`): PBKDF2-Vektoren, Namensnormalisierung,
  `coop-local.json`-Roundtrip + Layout-Migration, Katalog laden/auflösen, `ProfilePreparer`
  (CDATA, ScriptPath, keine Zeitänderung), Recorder (abgeschlossen, Reset, Gold, PB, Host-PB-Übernahme,
  Sperre), Dialog-Logik mit injizierbarem Lobby-Client.
- **Nativ:** echter LiveSplit-Prozess (Host bleibt in Lobby, Viewer folgt),
  Host+2 Viewer über lokalen `wrangler dev`.
- **Paket:** alle 48 RV-Profile auflösbar, alte PB-Zeiten (Mabutts Duo/Trio) unverändert.

## 6. Auslieferung
1. Relay deployen (`wrangler deploy`) – **nur nach ausdrücklicher Freigabe**; abwärtskompatibel.
2. RV-Paket: `coop-profiles.json` aus `profiles.json` generiert, neue DLL, schlanker Starter,
   Update-ZIP ohne Splits. Alte Einladungscodes funktionieren weiter (Erweitert).
3. Kollegen: Update-ZIP wie bisher; ohne Update können sie weiter per Einladungscode folgen,
   aber keinen Lobbys beitreten.
