# Eigenen Relay auf Cloudflare betreiben (Windows)

Diese Anleitung richtet einen privaten LiveSplit-Coop-Relay für eure Gruppe auf Cloudflares kostenlosem Workers-Tarif ein. Ihr deployt ihn einmalig von eurem Windows-PC aus; danach läuft er bei Cloudflare, und auf dem PC muss nichts geöffnet bleiben.

Ihr bekommt eine Serveradresse, die alle verwenden. Lobbys (Name plus optionales Passwort, öffentlich oder privat) und Einladungscodes funktionieren wie in der [Schnellstart-Anleitung](quickstart.md) beschrieben.

Technischer Hintergrund: [Protokoll](protocol.md) (Abschnitt Lobbys) und die [Relay-README](../relay-cloudflare/README.md).

## 1. Was ihr bekommt

- Einen Relay unter `https://<name>.<eure-subdomain>.workers.dev`, den LiveSplit als `wss://…/coop` erreicht.
- Lobbys: Der Host legt eine Lobby mit Namen und optionalem Passwort an. Öffentliche Lobbys erscheinen in der Liste; private Lobbys findet man nur über ihren Namen.
- Einladungscodes (`lscoop:1:…`) für Räume und Lobbys.
- Auf den Spiel-PCs ist nichts zusätzlich zu installieren, und nach dem Deployment läuft nichts mehr lokal.

## 2. Voraussetzungen (Windows)

- Ein kostenloses [Cloudflare-Konto](https://dash.cloudflare.com/sign-up). Die `workers.dev`-Subdomain wählt ihr beim ersten Deployment.
- Node.js LTS:

  ```powershell
  winget install OpenJS.NodeJS.LTS
  ```

- Git:

  ```powershell
  winget install Git.Git
  ```

Danach ein **neues** PowerShell-Fenster öffnen, damit `node`, `npm` und `git` im PATH verfügbar sind.

## 3. Deployment

Diese Befehle in PowerShell ausführen:

```powershell
git clone https://github.com/FischLord/livesplit-coop.git
cd livesplit-coop\relay-cloudflare
npm ci --ignore-scripts
npx wrangler login
npm run deploy
```

`npx wrangler login` öffnet ein Browserfenster; dort anmelden und den Zugriff erlauben. `npm run deploy` führt `wrangler deploy --env=""` aus. `--ignore-scripts` verhindert, dass die installierten Pakete eigene Installationsskripte ausführen; der Relay braucht sie nicht.

Optional: Vor dem Deployment kann in `wrangler.jsonc` der Wert `"name"` geändert werden (Standard: `livesplit-coop-relay`). Der Name wird Teil der URL, deshalb am besten gleich passend wählen.

Wrangler gibt die Adresse eures Workers aus, `https://<name>.<eure-subdomain>.workers.dev`. Die Serveradresse für LiveSplit ist derselbe Host mit `wss://` und `/coop`:

```text
wss://<name>.<eure-subdomain>.workers.dev/coop
```

## 4. Prüfen

```powershell
Invoke-RestMethod https://<name>.<eure-subdomain>.workers.dev/healthz
Invoke-RestMethod https://<name>.<eure-subdomain>.workers.dev/lobbies
```

Der erste Aufruf liefert `ok : True`. Der zweite liefert die Lobbyliste, die anfangs leer ist.

## 5. In LiveSplit verwenden

1. Rechtsklick auf den Timer, dann **Coop...** und **Advanced** öffnen.
2. Bei **Server** die Adresse `wss://…/coop` eintragen. Alle in der Gruppe müssen denselben Server verwenden.
3. Lobbys anlegen oder beitreten, wie in der [Schnellstart-Anleitung](quickstart.md) beschrieben.

## 6. Einstellungen

Die Einstellungen stehen im Block `"vars"` der Datei `wrangler.jsonc`:

| Einstellung | Standard | Bedeutung |
|---|---|---|
| `ROOM_CREATION` | `"1"` | Auf `"0"` setzen, um neue Räume und Lobbys zu sperren (Notschalter). Bestehende funktionieren weiter. |
| `ROOMS_PER_DAY` | `"500"` | Tageslimit für neue Räume und Lobbys zusammen (UTC-Tag). |
| `CREATE_LIMIT` | 3 pro Minute | Rate-Limit für das Anlegen von Räumen und Lobbys pro IP-Adresse und Cloudflare-Standort. Steht unter `"ratelimits"`. |

Nach jeder Änderung neu deployen:

```powershell
npm run deploy
```

## 7. Grenzen des kostenlosen Tarifs

Cloudflare ändert seine Zahlen von Zeit zu Zeit. Die folgenden Werte gelten Stand Oktober 2026; die aktuellen Angaben stehen auf der [Preisseite für Durable Objects](https://developers.cloudflare.com/durable-objects/platform/pricing/) und den [Workers-Limits](https://developers.cloudflare.com/workers/platform/limits/).

| Ressource | Kostenloser Tarif |
|---|---|
| Worker-Anfragen | 100.000 pro Tag |
| Durable-Object-Anfragen | 100.000 pro Tag, einschließlich WebSocket-Nachrichten und Alarme. Eingehende WebSocket-Nachrichten zählen 20:1; ausgehende Nachrichten und automatische Pings sind kostenlos. |
| Durable-Object-Laufzeit | 13.000 GB-s pro Tag. Schlafende (hibernierte) Objekte verbrauchen im Leerlauf keine Laufzeit. |
| SQLite-Zeilen | 5 Millionen gelesene und 100.000 geschriebene Zeilen pro Tag |
| Speicher | insgesamt 5 GB |
| CPU | 10 ms pro Aufruf |

Die Zähler werden täglich um 00:00 UTC zurückgesetzt. Ist ein Limit erreicht, schlagen weitere Anfragen bis zum Zurücksetzen fehl. Der kostenlose Tarif berechnet nie automatisch Geld. Wer mehr braucht, kann den kostenpflichtigen Workers-Tarif nehmen, der ab 5 US-Dollar im Monat kostet.

**Kapazitätsschätzung** (aus dem Verhalten der Komponente abgeleitet, nicht bei Cloudflare gemessen): Solange ein Host verbunden ist, sendet er alle 250 ms eine Zeitaktualisierung (4 pro Sekunde, auch bei gestoppter Zeit) und alle 5 Sekunden einen Ping auf Anwendungsebene. Das sind etwa 15.000 eingehende Nachrichten pro Stunde, also rund 750 abgerechnete Durable-Object-Anfragen pro Stunde. Jeder Zuschauer sendet nur alle 5 Sekunden einen Ping, etwa 36 abgerechnete Anfragen pro Stunde; Empfangen ist kostenlos. Eine Lobby mit Host und 5 Zuschauern kostet also etwa 940 Anfragen pro Stunde, eine volle (11 Zuschauer) etwa 1.150. Das Tagesbudget von 100.000 reicht damit für ungefähr:

- 130 Host-Stunden pro Tag bei Lobbys ohne Zuschauer,
- etwa 100 Lobby-Stunden pro Tag mit je 5 Zuschauern,
- etwa 85 Lobby-Stunden pro Tag mit vollen Lobbys, zum Beispiel 28 volle Lobbys für einen 3-stündigen Abend.

Beitreten, Verlassen und die öffentliche Liste kosten jeweils nur wenige Anfragen und fallen kaum ins Gewicht. Ungenutzte Lobbys kosten nur wenige KB Speicher. Neue Lobbys sind durch `ROOMS_PER_DAY` und das Rate-Limit pro IP begrenzt. Lobbys, die 7 Tage lang nicht genutzt wurden, werden automatisch gelöscht. Eine Lobby fasst bis zu 12 Personen: 1 Host und 11 Zuschauer.

## 8. Datenschutz und Sicherheit

- Lobby-Passwörter verlassen den Spiel-PC nie. Die Komponente leitet daraus einen Schlüssel ab (PBKDF2), und der Relay speichert nur einen SHA-256-Hash davon. Host-Schlüssel werden genauso gespeichert.
- Der Relay verwendet die Client-IP-Adresse (`CF-Connecting-IP`; bei IPv6 das /64-Netz) nur, um eine Adresse nach 10 fehlgeschlagenen Lobby-Anmeldungen innerhalb von 60 Sekunden für den Rest der Minute zu sperren. Der Code protokolliert keine Adressen. Die eigenen Anfrage-Metadaten von Cloudflare in den Workers-Logs sind davon getrennt; siehe unten.
- Öffentliche Lobbys zeigen Namen, ein Schloss-Symbol, Spiel, Kategorie und Profilbezeichnung sowie die Anzahl der Online-Mitglieder.
- Der Relay kann Timer-Daten (Splits, PBs, Spiel, Kategorie) lesen. Es gibt keine Ende-zu-Ende-Verschlüsselung; lädt also nur Leute ein, denen ihr vertraut.

Details: [Protokoll](protocol.md), Abschnitt „Lobbies (Cloudflare-Relay)“.

## 9. Wartung

**Aktualisieren** auf eine neuere Version des Projekts:

```powershell
cd livesplit-coop\relay-cloudflare
git pull
npm ci --ignore-scripts
npm run deploy
```

**Zurückrollen** auf die vorherige Version:

```powershell
npx wrangler rollback
```

**Logs**: Cloudflare-Dashboard → Workers → euer Worker → Logs. Der Relay protokolliert Fehler und Anfrage-Metadaten, aber keine Timer-Daten oder Schlüssel. `npx wrangler tail` zeigt die Logs live an.

**Entfernen** des Workers:

```powershell
npx wrangler delete
```

Danach antwortet der Worker nicht mehr, und die Serveradresse der Gruppe funktioniert nicht mehr. Gespeicherte Räume und Lobbys lassen sich danach nicht wiederherstellen.

## 10. Lokal testen (für Entwickler, optional)

```powershell
cd livesplit-coop\relay-cloudflare
npm test
```

Dies startet ein lokales `wrangler dev` mit höherem Limit für das Anlegen von Räumen und führt die Konformitäts- und Lobby-Tests dagegen aus. Für den eigenen Relay ist das nicht nötig.
