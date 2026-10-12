# Plan 2: Lobby-Client, lokaler Zustand und Coop-Fenster Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Die LiveSplit-Komponente erstellt und betritt Lobbys über ein einziges Fenster „Coop…“, merkt sich die Lobby in `coop-local.json` statt im Layout und verbindet beim Start automatisch.

**Architecture:** Neue, klar getrennte Einheiten: `LobbyKey` (Name/PBKDF2), `Texts` (de/en), `LobbyClient` (HTTP zu `/lobbies`), `LocalState` (JSON + DPAPI), `CoopWindow` (WinForms, spricht nur mit dem Interface `ICoopSession`). `CoopComponent` implementiert `ICoopSession`; `CoopSettings` schrumpft auf Rolle + Knopf und liest alte Layout-Schlüssel nur noch zur einmaligen Migration. Die alten Schnelldialoge (`QuickDialogs.cs`) entfallen.

**Tech Stack:** C# **5** (Compiler `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` – kein `?.`, kein `$"..."`, kein `nameof`, keine Expression-Bodied Members, kein `out var`), .NET Framework 4.8, WinForms, `System.Web.Extensions` (JavaScriptSerializer), Windows PowerShell 5.1 `Add-Type` für Tests, Cloudflare-Relay aus Plan 1 über `wrangler dev`.

Spec: `docs/superpowers/specs/2026-10-12-lobbies-design.md`, Abschnitte 3.1, 3.2, 4.

**Arbeitsverzeichnis:** `C:\home\repos\livesplit-coop` (Branch `feature/lobbies`). LiveSplit-Referenzen: `C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host` (nur lesen, nichts dorthin kopieren). Nie `wrangler deploy`.

**Build:** `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"` → `dist/LiveSplit.Coop.dll` (kompiliert alle `component/*.cs`, Warnungen sind Fehler).

**Tests:** `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-lobby.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"` (ab Task 2).

---

## File Structure

- Modify `relay-cloudflare/src/index.mjs`, `relay-cloudflare/test/lobbies.test.mjs` – `peers`-Nachricht an den Host.
- Create `relay-cloudflare/test/native-fixture.mjs` – startet `wrangler dev`, druckt `ws://…/coop`.
- Create `component/LobbyKey.cs` – Namensregeln, Raum-ID, PBKDF2-Viewer-Schlüssel.
- Create `component/Texts.cs` – alle neuen UI-Texte de/en.
- Create `component/LobbyClient.cs` – `Create`, `List`, `Endpoint`; `LobbyGrant`, `LobbyInfo`, `LobbyException`.
- Create `component/LocalState.cs` – `coop-local.json`, `LobbyState`, DPAPI-Helfer.
- Create `component/CoopWindow.cs` – `ICoopSession` + Fenster.
- Modify `component/RelayConnection.cs` – `Viewers`, `StopReason`, „Too many attempts“.
- Modify `component/Settings.cs` – nur Rolle, Status, „Coop-Fenster öffnen“, Legacy-Lesen.
- Modify `component/Component.cs` – `ICoopSession`, Auto-Connect, Menü, Statuszeile.
- Modify `component/Invite.cs` – `Create`/`RoomGrant` entfernen (ersetzt durch Lobbys).
- Delete `component/QuickDialogs.cs`, `tests/MenuTests.cs`, `tests/ClipboardTests.cs`, `scripts/test-menu.ps1`, `scripts/test-clipboard.ps1`.
- Modify `tests/Regression.cs` – `StableSettings` auf neue Settings.
- Create `tests/LobbyTests.cs`, `scripts/test-lobby.ps1`.

---

### Task 1: Relay meldet dem Host die Viewerzahl (`peers`)

**Files:**
- Modify: `relay-cloudflare/src/index.mjs`
- Test: `relay-cloudflare/test/lobbies.test.mjs`
- Create: `relay-cloudflare/test/native-fixture.mjs`

- [ ] **Step 1: Failing Test anhängen** (an `test/lobbies.test.mjs`)

```js
test('lobbies: the host learns how many viewers follow it', async () => {
  const made = await (await create({ name: unique('pe'), viewerKey: key('v'), public: false, locked: true })).json();
  const host = await client(made.roomId, 'host', made.hostKey); await host.next('ready');
  assert.equal((await host.next('peers')).viewers, 0);
  const viewer = await client(made.roomId, 'viewer', key('v')); await viewer.next('ready');
  assert.equal((await host.next('peers')).viewers, 1);
  viewer.close();
  assert.equal((await host.next('peers')).viewers, 0);
  host.close();
});
```

- [ ] **Step 2: Test laufen lassen, muss scheitern**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && node --test test/lobbies.test.mjs`
Expected: FAIL `Timeout waiting for peers`.

- [ ] **Step 3: Implementierung in `class Room`**

```js
  // Tells the host how many viewers follow it. Older clients ignore unknown message types.
  notifyHost(leaving = null) {
    const host = this.host();
    if (host) host.ws.send(JSON.stringify({ type: 'peers', viewers: this.viewers().filter(s => s.ws !== leaving).length }));
  }
```

Aufrufe:
- In `hello` direkt nach `ws.send(JSON.stringify({ type: 'ready', v: PROTOCOL, role: m.role }));` → `this.notifyHost();`
- In `left(ws)`, im Block für `a.role === 'host' || a.role === 'viewer' || a.role === 'gone'`, **vor** `await this.report();`: `if (a.role === 'viewer') this.notifyHost(ws);`
- Falls `report()` in `left` die schließende Verbindung noch mitzählt (Test aus Plan 1 Task 4 „viewers 0“ würde sonst flackern): `report(leaving = null)` analog mit `.filter(s => s.ws !== leaving)` und in `left` `await this.report(ws);` aufrufen.

- [ ] **Step 4: Native Fixture anlegen** (`test/native-fixture.mjs`)

```js
// Local Cloudflare relay for the component's native tests: prints the ws:// coop endpoint, then runs until killed (max. 10 min).
import { startDev } from './dev.mjs';
const dev = await startDev();
console.log(`${dev.base}/coop`);
const stop = () => { dev.stop(); process.exit(0); };
process.on('SIGTERM', stop); process.on('SIGINT', stop);
setTimeout(stop, 600000);
```

- [ ] **Step 5: Tests laufen lassen**

Run: `cd /c/home/repos/livesplit-coop/relay-cloudflare && npm test`
Expected: alle PASS.

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add relay-cloudflare
git commit -m "Relay: tell the host how many viewers follow it; native test fixture

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `LobbyKey` + Testrunner

**Files:**
- Create: `component/LobbyKey.cs`
- Create: `tests/LobbyTests.cs`
- Create: `scripts/test-lobby.ps1`

- [ ] **Step 1: Testrunner anlegen** (`scripts/test-lobby.ps1`)

```powershell
param([Parameter(Mandatory=$true)][string]$LiveSplitPath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$live=(Resolve-Path -LiteralPath $LiveSplitPath.Trim().TrimEnd('"')).Path
[Environment]::CurrentDirectory=$live
$env:PATH=(Join-Path $live 'x64')+';'+$env:PATH
$dlls=@((Join-Path $live 'LiveSplit.Core.dll'),(Join-Path $live 'UpdateManager.dll'),(Join-Path $root 'dist\LiveSplit.Coop.dll'))
foreach($dll in $dlls){[void][Reflection.Assembly]::LoadFrom($dll)}
$refs=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xml.dll','System.Security.dll','System.Web.Extensions.dll')+$dlls
Add-Type -Path (Join-Path $root 'tests\LobbyTests.cs') -ReferencedAssemblies $refs
$work=Join-Path $root '.local\lobby-tests';[void][IO.Directory]::CreateDirectory($work)
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=(Get-Command node.exe).Source
$start.Arguments='"'+(Join-Path $root 'relay-cloudflare\test\native-fixture.mjs')+'"'
$start.WorkingDirectory=Join-Path $root 'relay-cloudflare'
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true
$relay=[Diagnostics.Process]::Start($start)
try {
    $endpoint=$relay.StandardOutput.ReadLine()
    if($endpoint -notmatch '^ws://127\.0\.0\.1:\d+/coop$'){throw 'Local Cloudflare relay did not start: '+$endpoint}
    [LobbyTests]::Run($endpoint,$work)
    Write-Output 'ALL LOBBY TESTS PASSED'
} finally {
    if(!$relay.HasExited){& taskkill /pid $relay.Id /T /F | Out-Null}
    $relay.Dispose()
}
```

- [ ] **Step 2: Failing Test** (`tests/LobbyTests.cs`)

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using LiveSplit.Coop;

public static class LobbyTests {
    static int passed;
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);passed++;Console.WriteLine("PASS: "+text);}
    static void Throws<T>(Action action,string text) where T:Exception {try{action();}catch(T){Check(true,text);return;}Check(false,text);}
    public static void Run(string relay,string work) {
        Texts.German=false;
        KeyTests();
        Console.WriteLine(passed+" checks passed");
    }
    static void KeyTests() {
        Check(LobbyKey.Normalize("  RvGang ")=="rvgang","name is trimmed and lowercased");
        Check(LobbyKey.Normalize("team_1-a")=="team_1-a","digits, - and _ are allowed");
        Check(LobbyKey.Normalize("ab")==null && LobbyKey.Normalize(new string('a',33))==null,"names need 3-32 characters");
        Check(LobbyKey.Normalize("a b c")==null && LobbyKey.Normalize("bäh")==null && LobbyKey.Normalize(null)==null,"spaces, umlauts and null are rejected");
        Check(LobbyKey.RoomId("rvgang")=="lobby_rvgang","room id carries the lobby prefix");
        // Vectors from Node: crypto.pbkdf2Sync(password, "livesplit-coop-lobby:v1:"+name, 100000, 32, "sha256").toString("base64url")
        Check(LobbyKey.DeriveViewerKey("rvgang","banane")=="2FlgON4zKgjnEC-q8WYwuza4zowHL1rTjwTzOb_7sR0","PBKDF2 vector rvgang/banane");
        Check(LobbyKey.DeriveViewerKey("rvgang","")=="JVAHdf1Hp1wDTb7CSkWYefP--tDQbeGW99nLdEddUuI","PBKDF2 vector with empty password");
        Check(LobbyKey.DeriveViewerKey(" Team_1-A ","Pässwört")=="5QBHS-ppwQmkAzPN299KtsVX-bqPOaTHvl23jg90QnI","PBKDF2 vector with UTF-8 password and normalized name");
        Throws<ArgumentException>(()=>LobbyKey.DeriveViewerKey("x","pw"),"deriving a key for an invalid name throws");
    }
}
```

- [ ] **Step 3: Bauen, Test muss scheitern**

Run: `cd /c/home/repos/livesplit-coop && powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host" && powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test-lobby.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"`
Expected: Add-Type-Fehler „LobbyKey/Texts existiert nicht“.

- [ ] **Step 4: `component/LobbyKey.cs`**

```csharp
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveSplit.Coop {
    // Lobby names and the viewer key derived from a lobby password. Must match docs/protocol.md "Lobbies".
    // The password itself never leaves this PC; the relay only sees (a hash of) the derived key.
    public static class LobbyKey {
        public const string RoomPrefix="lobby_";
        const string Salt="livesplit-coop-lobby:v1:";
        static readonly Regex Shape=new Regex("^[a-z0-9_-]{3,32}$");
        public static string Normalize(string raw) {
            if(raw==null) return null;
            string name=raw.Trim().ToLowerInvariant();
            return Shape.IsMatch(name)?name:null;
        }
        public static string RoomId(string name) { return RoomPrefix+name; }
        public static string DeriveViewerKey(string name,string password) {
            string normalized=Normalize(name);
            if(normalized==null) throw new ArgumentException("Invalid lobby name.");
            using(var kdf=new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password ?? ""),Encoding.UTF8.GetBytes(Salt+normalized),100000,HashAlgorithmName.SHA256))
                return Convert.ToBase64String(kdf.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        }
    }
}
```

Für den Testlauf braucht `Texts.German` bereits eine Definition. Minimal-Stub `component/Texts.cs` (Task 3 ersetzt ihn vollständig):

```csharp
namespace LiveSplit.Coop {
    public static class Texts {
        public static bool German;
    }
}
```

- [ ] **Step 5: Bauen und testen**

Run: wie Step 3.
Expected: 9 PASS, `ALL LOBBY TESTS PASSED`. Wenn das Vektor mit leerem Passwort scheitert, weil `Rfc2898DeriveBytes` ein leeres Passwort ablehnt: NEEDS_CONTEXT melden (nicht still umgehen).

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/LobbyKey.cs component/Texts.cs tests/LobbyTests.cs scripts/test-lobby.ps1
git commit -m "Component: lobby names and PBKDF2 viewer keys, lobby test runner

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `Texts` (de/en)

**Files:**
- Modify: `component/Texts.cs` (Stub vollständig ersetzen)
- Test: `tests/LobbyTests.cs`

- [ ] **Step 1: Failing Test** – in `Run` nach `KeyTests();` `TextTests();` aufrufen und ergänzen:

```csharp
    static void TextTests() {
        Texts.German=true;
        Check(Texts.T("lobby.wrongPassword")=="Passwort falsch.","German text");
        Check(Texts.T("status.online",2)=="2 online","formatted text");
        Texts.German=false;
        Check(Texts.T("lobby.wrongPassword")=="Wrong password.","English text");
        Check(Texts.T("no.such.key")=="no.such.key","unknown keys fall back to the key");
        foreach(var key in Texts.Keys) Check(Texts.Has(key),"every key has both languages: "+key);
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern** (Run wie Task 2 Step 3). Expected: Compile-Fehler `T`/`Keys`/`Has` fehlen.

- [ ] **Step 3: `component/Texts.cs`**

```csharp
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveSplit.Coop {
    // UI text in English or German, chosen by the Windows display language. Unknown keys show the key itself.
    public static class Texts {
        public static bool German=CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="de";
        static readonly Dictionary<string,string[]> All=new Dictionary<string,string[]> {
            {"menu.coop",new[]{"Coop...","Coop..."}},
            {"window.title",new[]{"Coop","Coop"}},
            {"window.close",new[]{"Close","Schließen"}},
            {"section.profile",new[]{"Profile","Profil"}},
            {"section.lobby",new[]{"Lobby","Lobby"}},
            {"section.advanced",new[]{"Advanced","Erweitert"}},
            {"lobby.name",new[]{"Lobby name","Lobby-Name"}},
            {"lobby.password",new[]{"Password (optional)","Passwort (optional)"}},
            {"lobby.public",new[]{"Show in the public list","In öffentlicher Liste anzeigen"}},
            {"lobby.create",new[]{"Create lobby","Lobby erstellen"}},
            {"lobby.join",new[]{"Join","Beitreten"}},
            {"lobby.refresh",new[]{"Refresh","Aktualisieren"}},
            {"lobby.leave",new[]{"Leave lobby","Lobby verlassen"}},
            {"lobby.reconnect",new[]{"Reconnect","Neu verbinden"}},
            {"lobby.copyPassword",new[]{"Copy name and password","Name und Passwort kopieren"}},
            {"lobby.publicList",new[]{"Public lobbies","Öffentliche Lobbys"}},
            {"lobby.column.name",new[]{"Lobby","Lobby"}},
            {"lobby.column.run",new[]{"Run","Run"}},
            {"lobby.column.online",new[]{"Host / viewers","Host / Zuschauer"}},
            {"lobby.none",new[]{"No public lobbies right now.","Gerade keine öffentlichen Lobbys."}},
            {"lobby.loading",new[]{"Loading...","Lade..."}},
            {"lobby.creating",new[]{"Creating lobby...","Erstelle Lobby..."}},
            {"lobby.joining",new[]{"Joining...","Trete bei..."}},
            {"lobby.created",new[]{"Lobby {0} created. Name and password are copied for your teammates.","Lobby {0} erstellt. Name und Passwort sind für deine Mitspieler kopiert."}},
            {"lobby.passwordInfo",new[]{"Lobby: {0} · Password: {1}","Lobby: {0} · Passwort: {1}"}},
            {"lobby.nameInfo",new[]{"Lobby: {0} (no password)","Lobby: {0} (ohne Passwort)"}},
            {"lobby.invalidName",new[]{"Lobby names use 3-32 letters, digits, - or _.","Lobby-Namen: 3-32 Zeichen aus Buchstaben, Ziffern, - oder _."}},
            {"lobby.taken",new[]{"This lobby already exists. Choose another name or join it.","Diese Lobby gibt es schon. Anderen Namen wählen oder beitreten."}},
            {"lobby.unknown",new[]{"Lobby not found or expired.","Lobby nicht gefunden oder abgelaufen."}},
            {"lobby.wrongPassword",new[]{"Wrong password.","Passwort falsch."}},
            {"lobby.tooMany",new[]{"Too many failed attempts. Wait a minute.","Zu viele Fehlversuche. Eine Minute warten."}},
            {"lobby.replaced",new[]{"Another host connection took over this lobby.","Eine andere Host-Verbindung hat die Lobby übernommen."}},
            {"lobby.protocol",new[]{"Update LiveSplit Coop: the server speaks a newer protocol.","LiveSplit Coop aktualisieren: Der Server spricht ein neueres Protokoll."}},
            {"lobby.rejected",new[]{"The server rejected the connection.","Der Server hat die Verbindung abgelehnt."}},
            {"lobby.unsupported",new[]{"This server has no lobbies. Use an invitation under Advanced.","Server unterstützt keine Lobbys - Einladungscode unter Erweitert."}},
            {"lobby.rateLimited",new[]{"Too many new lobbies from here. Wait a minute.","Zu viele neue Lobbys von hier. Eine Minute warten."}},
            {"lobby.dailyLimit",new[]{"The server is not creating lobbies right now. Try again later.","Der Server erstellt gerade keine Lobbys. Später erneut versuchen."}},
            {"lobby.unreachable",new[]{"Server not reachable: {0}","Server nicht erreichbar: {0}"}},
            {"lobby.refused",new[]{"The server refused ({0}).","Der Server hat abgelehnt ({0})."}},
            {"lobby.badAnswer",new[]{"The server sent an unexpected answer.","Unerwartete Antwort vom Server."}},
            {"lobby.keyUnreadable",new[]{"The saved lobby key cannot be read on this PC. Leave the lobby and join again.","Der gespeicherte Lobby-Schlüssel ist auf diesem PC nicht lesbar. Lobby verlassen und neu beitreten."}},
            {"status.notConnected",new[]{"not in a lobby","nicht in einer Lobby"}},
            {"status.host",new[]{"Host","Host"}},
            {"status.following",new[]{"following host","folgt Host"}},
            {"status.waiting",new[]{"waiting for host","warte auf Host"}},
            {"status.hostOffline",new[]{"host offline - frozen","Host offline - eingefroren"}},
            {"status.stale",new[]{"no fresh data - frozen","keine neuen Daten - eingefroren"}},
            {"status.connecting",new[]{"connecting...","verbinde..."}},
            {"status.reconnecting",new[]{"reconnecting...","verbinde neu..."}},
            {"status.online",new[]{"{0} online","{0} online"}},
            {"status.public",new[]{"public","öffentlich"}},
            {"status.private",new[]{"private","privat"}},
            {"status.inviteRoom",new[]{"invitation room","Einladungsraum"}},
            {"status.autosplitter",new[]{"turn off the autosplitter","Autosplitter ausschalten"}},
            {"advanced.server",new[]{"Server","Server"}},
            {"advanced.inviteCode",new[]{"Invitation","Einladung"}},
            {"advanced.invite",new[]{"Join with invitation","Mit Einladungscode beitreten"}},
            {"advanced.invalidInvite",new[]{"That invitation is not valid. Paste the complete lscoop:1:... code.","Ungültige Einladung. Den kompletten lscoop:1:...-Code einfügen."}},
            {"advanced.diagnostics",new[]{"Export diagnostics...","Diagnose exportieren..."}},
            {"viewer.stopTimer",new[]{"Stop or reset the local timer before joining.","Erst den lokalen Timer stoppen oder resetten."}},
            {"copy.done",new[]{"Copied.","Kopiert."}},
            {"copy.busy",new[]{"The clipboard is busy. Try again.","Zwischenablage belegt. Nochmal versuchen."}},
            {"settings.role",new[]{"Role","Rolle"}},
            {"settings.roleHost",new[]{"Host - this timer measures","Host - dieser Timer misst"}},
            {"settings.roleViewer",new[]{"Viewer - follow the host","Viewer - dem Host folgen"}},
            {"settings.open",new[]{"Open coop window...","Coop-Fenster öffnen..."}},
            {"settings.note",new[]{"Lobbies are managed in the coop window (right-click > Coop...). Layouts no longer store room keys.","Lobbys verwaltest du im Coop-Fenster (Rechtsklick > Coop...). Layouts speichern keine Raumschlüssel mehr."}}
        };
        public static IEnumerable<string> Keys { get { return All.Keys.ToList(); } }
        public static bool Has(string key) { string[] v;return All.TryGetValue(key,out v) && v.Length==2 && v[0].Length>0 && v[1].Length>0; }
        public static string T(string key) { string[] v;if(!All.TryGetValue(key,out v)) return key;return German?v[1]:v[0]; }
        public static string T(string key,params object[] args) { return string.Format(T(key),args); }
    }
}
```

- [ ] **Step 4: Bauen + Test** → alle PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/Texts.cs tests/LobbyTests.cs
git commit -m "Component: German and English UI texts

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `LobbyClient` (HTTP)

**Files:**
- Create: `component/LobbyClient.cs`
- Modify: `component/Invite.cs` (`Create` + `RoomGrant` entfernen)
- Test: `tests/LobbyTests.cs`

- [ ] **Step 1: Failing Test** – in `Run` nach `TextTests();` `ClientTests(relay);` und ergänzen:

```csharp
    static string Unique(string prefix){return (prefix+DateTime.UtcNow.Ticks.ToString("x")+Guid.NewGuid().ToString("N").Substring(0,4)).Substring(0,Math.Min(32,prefix.Length+18));}
    static void ClientTests(string relay) {
        Check(LobbyClient.Endpoint("wss://relay.example/coop","/lobbies").ToString()=="https://relay.example/lobbies","wss maps to https");
        Check(LobbyClient.Endpoint("ws://127.0.0.1:8788/coop","/lobbies").ToString()=="http://127.0.0.1:8788/lobbies","loopback ws keeps its port");
        string name=Unique("ct");
        var grant=LobbyClient.Create(relay,name.ToUpperInvariant(),"banane",true);
        Check(grant.name==name && grant.roomId=="lobby_"+name && grant.server==relay,"create returns the normalized lobby");
        Check(grant.hostKey!=null && grant.hostKey.Length==43 && grant.viewerKey==LobbyKey.DeriveViewerKey(name,"banane"),"grant carries host key and derived viewer key");
        Check(grant.isPublic && grant.locked,"public lobby with password is locked");
        try { LobbyClient.Create(relay,name,"x",false);Check(false,"taken name throws"); }
        catch(LobbyException e) { Check(e.Message==Texts.T("lobby.taken"),"taken name gives the taken message"); }
        try { LobbyClient.Create(relay,"a b","",false);Check(false,"invalid name throws"); }
        catch(LobbyException e) { Check(e.Message==Texts.T("lobby.invalidName"),"invalid name is reported before any request"); }
        var open=LobbyClient.Create(relay,Unique("op"),"",false);
        Check(!open.locked && !open.isPublic,"lobby without password is not locked");
        LobbyInfo found=null;
        for(int i=0;i<40 && found==null;i++){found=LobbyClient.List(relay).FirstOrDefault(l=>l.name==name);if(found==null)Thread.Sleep(100);}
        Check(found!=null && found.locked && !found.hostOnline && found.viewers==0,"public lobby appears in the list");
        Check(!LobbyClient.List(relay).Any(l=>l.name==open.name),"private lobby is not listed");
        try { LobbyClient.List("wss://127.0.0.1:1/coop");Check(false,"unreachable server throws"); }
        catch(LobbyException e) { Check(e.Message.StartsWith(Texts.T("lobby.unreachable","").TrimEnd()),"unreachable server is explained"); }
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern** (Compile-Fehler `LobbyClient` fehlt).

- [ ] **Step 3: `component/LobbyClient.cs`**

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveSplit.Coop {
    public sealed class LobbyGrant {
        public string server,name,roomId,hostKey,viewerKey;
        public bool isPublic,locked;
    }
    public sealed class LobbyInfo {
        public string name,game,category,label;
        public bool locked,hostOnline;
        public int viewers;
    }
    // Carries a message that can be shown to the user as is.
    public sealed class LobbyException : Exception { public LobbyException(string message) : base(message) { } }
    // HTTP side of lobbies (POST/GET /lobbies on the relay behind a wss://host/coop address). Blocking; call off the UI thread.
    public static class LobbyClient {
        static readonly Regex KeyShape=new Regex("^[A-Za-z0-9_-]{32,256}$");
        public static Uri Endpoint(string server,string path) {
            new RelayConnection(server,"probe","host",new string('x',32)).Dispose(); // validates the address (wss, or ws on loopback)
            var uri=new Uri(server);
            return new UriBuilder(uri) { Scheme=uri.Scheme=="wss"?"https":"http",Path=path,Port=uri.IsDefaultPort?-1:uri.Port }.Uri;
        }
        public static LobbyGrant Create(string server,string name,string password,bool isPublic) {
            string normalized=LobbyKey.Normalize(name);
            if(normalized==null) throw new LobbyException(Texts.T("lobby.invalidName"));
            password=password ?? "";
            string viewerKey=LobbyKey.DeriveViewerKey(normalized,password);
            var body=RelayConnection.Json().Serialize(new Dictionary<string,object> { {"name",normalized},{"viewerKey",viewerKey},{"public",isPublic},{"locked",password.Length>0} });
            int status;string answer=Send(Endpoint(server,"/lobbies"),"POST",body,out status);
            if(status==409) throw new LobbyException(Texts.T("lobby.taken"));
            if(status!=201) throw Refused(status);
            var data=Parse(answer);
            object roomId,hostKey;
            if(data==null || !data.TryGetValue("roomId",out roomId) || !data.TryGetValue("hostKey",out hostKey) ||
               (roomId as string)!=LobbyKey.RoomId(normalized) || !(hostKey is string) || !KeyShape.IsMatch((string)hostKey) || (string)hostKey==viewerKey)
                throw new LobbyException(Texts.T("lobby.badAnswer"));
            return new LobbyGrant { server=server,name=normalized,roomId=(string)roomId,hostKey=(string)hostKey,viewerKey=viewerKey,isPublic=isPublic,locked=password.Length>0 };
        }
        public static List<LobbyInfo> List(string server) {
            int status;string answer=Send(Endpoint(server,"/lobbies"),"GET",null,out status);
            if(status!=200) throw Refused(status);
            var data=Parse(answer);object raw;
            if(data==null || !data.TryGetValue("lobbies",out raw) || !(raw is IEnumerable)) throw new LobbyException(Texts.T("lobby.badAnswer"));
            var result=new List<LobbyInfo>();
            foreach(var item in (IEnumerable)raw) {
                var entry=item as Dictionary<string,object>;
                if(entry==null) continue;
                string name=Text(entry,"name");
                if(LobbyKey.Normalize(name)!=name) continue;
                result.Add(new LobbyInfo { name=name,game=Text(entry,"game"),category=Text(entry,"category"),label=Text(entry,"label"),
                    locked=Flag(entry,"locked"),hostOnline=Flag(entry,"hostOnline"),viewers=Count(entry,"viewers") });
            }
            return result;
        }
        static string Text(Dictionary<string,object> d,string key) { object v;return d.TryGetValue(key,out v) && v is string && ((string)v).Length<=200?(string)v:null; }
        static bool Flag(Dictionary<string,object> d,string key) { object v;return d.TryGetValue(key,out v) && v is bool && (bool)v; }
        static int Count(Dictionary<string,object> d,string key) { object v;return d.TryGetValue(key,out v) && v is int && (int)v>=0?(int)v:0; }
        static Dictionary<string,object> Parse(string text) {
            try { return RelayConnection.Json().Deserialize<Dictionary<string,object>>(text); }
            catch(ArgumentException) { return null; } catch(InvalidOperationException) { return null; }
        }
        static LobbyException Refused(int status) {
            if(status==404) return new LobbyException(Texts.T("lobby.unsupported"));
            if(status==429) return new LobbyException(Texts.T("lobby.rateLimited"));
            if(status==503) return new LobbyException(Texts.T("lobby.dailyLimit"));
            return new LobbyException(Texts.T("lobby.refused",status));
        }
        // Returns the body for any HTTP status; only a missing answer (DNS, refused, timeout) throws.
        static string Send(Uri uri,string method,string body,out int status) {
            var request=(HttpWebRequest)WebRequest.Create(uri);
            request.Method=method;request.Timeout=15000;request.ReadWriteTimeout=15000;request.AllowAutoRedirect=false;
            try {
                if(body!=null) {
                    var bytes=Encoding.UTF8.GetBytes(body);
                    request.ContentType="application/json";request.ContentLength=bytes.Length;
                    using(var stream=request.GetRequestStream()) stream.Write(bytes,0,bytes.Length);
                }
                using(var response=(HttpWebResponse)request.GetResponse()) { status=(int)response.StatusCode;return Read(response); }
            } catch(WebException e) {
                var response=e.Response as HttpWebResponse;
                if(response==null) throw new LobbyException(Texts.T("lobby.unreachable",e.Message));
                using(response) { status=(int)response.StatusCode;return Read(response); }
            }
        }
        static string Read(HttpWebResponse response) {
            using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) {
                var buffer=new char[65536];int length=reader.ReadBlock(buffer,0,buffer.Length);
                return new string(buffer,0,length);
            }
        }
    }
}
```

- [ ] **Step 4: `Invite.cs` aufräumen**

`RoomGrant`-Klasse und die Methode `Create` samt Kommentar `// POST <server>/rooms ...` löschen; `using System.Collections.Generic; using System.IO; using System.Net; using System.Text;` entfernen, falls danach ungenutzt (Warnungen sind Fehler). `Format` und `TryParse` bleiben. `QuickDialogs.cs` und `Settings.cs` benutzen `Invite.Create`/`RoomGrant` noch – damit der Build grün bleibt, in diesem Task **zusätzlich**:
- `component/QuickDialogs.cs` löschen (wird in Task 7 durch `CoopWindow` ersetzt).
- In `component/Component.cs` die Methoden `HostRoom`, `JoinRoom`, `CopyRoomInvite` löschen und `ContextMenuControls` vorläufig auf `{"Coop: Reconnect",Connect},{"Coop: Disconnect",Disconnect},{"Coop: Export diagnostics...",ExportDiagnostics}` reduzieren.
- In `component/Settings.cs` den Knopf `create`, die Methoden `CreateRoom` und `ApplyHostGrant` löschen (inkl. `create.Click`-Verdrahtung und `roomButtons.Controls.Add(create)`).
- `tests/MenuTests.cs`, `tests/ClipboardTests.cs`, `scripts/test-menu.ps1`, `scripts/test-clipboard.ps1` löschen (`git rm`).

- [ ] **Step 5: Bauen + Test** → alle PASS (Relay läuft lokal über die Fixture).

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add -A component tests scripts
git commit -m "Component: lobby HTTP client; retire invite room creation and quick dialogs

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: `LocalState` (`coop-local.json`)

**Files:**
- Create: `component/LocalState.cs`
- Test: `tests/LobbyTests.cs`

- [ ] **Step 1: Failing Test** – in `Run` `StateTests(work);` ergänzen:

```csharp
    static void StateTests(string work) {
        string path=Path.Combine(work,"coop-local.json");
        if(File.Exists(path)) File.Delete(path);
        var empty=LocalState.Load(path);
        Check(empty.lobby==null && empty.lastProfile==null && empty.server==null,"missing file gives an empty state");
        var grant=new LobbyGrant { server="wss://relay.example/coop",name="rvgang",roomId="lobby_rvgang",hostKey=new string('h',43),viewerKey=new string('v',43),isPublic=true,locked=true };
        var state=new LocalState { lastProfile="valley|ac|duo|nmg",lobby=LobbyState.From(grant,"host","banane") };
        state.Save(path);
        string text=File.ReadAllText(path);
        Check(!text.Contains(new string('h',43)) && !text.Contains(new string('v',43)) && !text.Contains("banane"),"keys and password are not stored in plain text");
        var loaded=LocalState.Load(path);
        Check(loaded.lastProfile=="valley|ac|duo|nmg" && loaded.lobby.name=="rvgang" && loaded.lobby.roomId=="lobby_rvgang" && loaded.lobby.role=="host","state round-trips");
        Check(loaded.lobby.isPublic && loaded.lobby.locked && loaded.lobby.server=="wss://relay.example/coop","lobby flags round-trip");
        Check(LocalState.Unprotect(loaded.lobby.protectedHostKey)==new string('h',43) && LocalState.Unprotect(loaded.lobby.protectedViewerKey)==new string('v',43),"keys decrypt");
        Check(LocalState.Unprotect(loaded.lobby.protectedPassword)=="banane","host password decrypts");
        var viewer=LobbyState.From(grant,"viewer","banane");
        Check(viewer.protectedHostKey=="" && viewer.protectedPassword=="","viewers never store host key or password");
        File.WriteAllText(path,"{ broken");
        var recovered=LocalState.Load(path);
        Check(recovered.lobby==null && Directory.GetFiles(work,"coop-local.json.broken-*").Length>=1,"a broken file is set aside and an empty state is used");
        var legacy=LobbyState.FromLegacy("wss://relay.example/coop","roomid","host",new string('k',43),new string('i',43));
        Check(legacy.name==null && legacy.roomId=="roomid" && LocalState.Unprotect(legacy.protectedHostKey)==new string('k',43) && LocalState.Unprotect(legacy.protectedViewerKey)==new string('i',43),"legacy host room keeps host and invite key");
        var legacyViewer=LobbyState.FromLegacy("wss://relay.example/coop","roomid","viewer",new string('k',43),"");
        Check(legacyViewer.protectedHostKey=="" && LocalState.Unprotect(legacyViewer.protectedViewerKey)==new string('k',43),"legacy viewer room keeps the viewer key");
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern** (Compile-Fehler).

- [ ] **Step 3: `component/LocalState.cs`**

```csharp
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LiveSplit.Coop {
    // The lobby this LiveSplit copy belongs to. Keys and the host's password are DPAPI-protected for this Windows user.
    public sealed class LobbyState {
        public string server,name,roomId,role,protectedHostKey,protectedViewerKey,protectedPassword;
        public bool isPublic,locked;
        public static LobbyState From(LobbyGrant grant,string role,string password) {
            bool host=role=="host";
            return new LobbyState { server=grant.server,name=grant.name,roomId=grant.roomId,role=role,isPublic=grant.isPublic,locked=grant.locked,
                protectedHostKey=host?LocalState.Protect(grant.hostKey ?? ""):"",
                protectedViewerKey=LocalState.Protect(grant.viewerKey ?? ""),
                protectedPassword=host?LocalState.Protect(password ?? ""):"" };
        }
        // Rooms from layouts before lobbies: no name, host keeps its invite (viewer) key for "copy invitation".
        public static LobbyState FromLegacy(string server,string room,string role,string key,string inviteKey) {
            bool host=role=="host";
            return new LobbyState { server=server,roomId=room,role=role,
                protectedHostKey=host?LocalState.Protect(key):"",
                protectedViewerKey=LocalState.Protect(host?(inviteKey ?? ""):key),protectedPassword="" };
        }
    }
    public sealed class LocalState {
        public int version=1;
        public string server,lastProfile;
        public LobbyState lobby;
        public static string DefaultPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"coop-local.json"); } }
        public static LocalState Load(string path) {
            if(!File.Exists(path)) return new LocalState();
            try {
                var state=new JavaScriptSerializer().Deserialize<LocalState>(File.ReadAllText(path,Encoding.UTF8));
                return state ?? new LocalState();
            } catch(Exception e) {
                CoopDiagnostics.Error("local_state",e);
                try { File.Move(path,path+".broken-"+DateTime.Now.ToString("yyyyMMddHHmmss")); } catch(IOException) { }
                return new LocalState();
            }
        }
        // Written to a temporary file first so a crash never leaves half a file behind.
        public void Save(string path) {
            string temp=path+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),new UTF8Encoding(false));
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        }
        public static string Protect(string value) {
            if(string.IsNullOrEmpty(value)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser));
        }
        // Throws CryptographicException when the value was protected by another Windows user or PC.
        public static string Unprotect(string value) {
            if(string.IsNullOrEmpty(value)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));
        }
    }
}
```

- [ ] **Step 4: Bauen + Test** → alle PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/LocalState.cs tests/LobbyTests.cs
git commit -m "Component: per-copy lobby state in coop-local.json, DPAPI-protected

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: `RelayConnection` – Viewerzahl und Abbruchgrund

**Files:**
- Modify: `component/RelayConnection.cs`
- Test: `tests/LobbyTests.cs`

- [ ] **Step 1: Failing Test** – in `Run` `ConnectionTests(relay);` ergänzen:

```csharp
    static bool Until(Func<bool> ok){var clock=Stopwatch.StartNew();while(!ok() && clock.ElapsedMilliseconds<8000)Thread.Sleep(20);return ok();}
    static void ConnectionTests(string relay) {
        var grant=LobbyClient.Create(relay,Unique("cn"),"banane",false);
        using(var host=new RelayConnection(relay,grant.roomId,"host",grant.hostKey)) {
            host.Start();
            Check(Until(()=>host.Ready) && Until(()=>host.Viewers==0),"host learns it has no viewers");
            using(var viewer=new RelayConnection(relay,grant.roomId,"viewer",grant.viewerKey)) {
                viewer.Start();
                Check(Until(()=>viewer.Ready) && Until(()=>host.Viewers==1),"host learns about a viewer");
                Check(viewer.StopReason==null,"a working connection has no stop reason");
            }
            Check(Until(()=>host.Viewers==0),"host learns the viewer left");
        }
        using(var wrong=new RelayConnection(relay,grant.roomId,"viewer",LobbyKey.DeriveViewerKey(grant.name,"falsch"))) {
            wrong.Start();
            Check(Until(()=>wrong.StopReason!=null) && wrong.StopReason=="auth_failed" && wrong.Phase==RelayPhase.Stopped,"wrong password stops with auth_failed");
        }
        using(var missing=new RelayConnection(relay,LobbyKey.RoomId(Unique("no")),"viewer",LobbyKey.DeriveViewerKey("abc",""))) {
            missing.Start();
            Check(Until(()=>missing.StopReason!=null) && missing.StopReason=="unknown_room","unknown lobby stops with unknown_room");
        }
        var locked=LobbyClient.Create(relay,Unique("tm"),"richtig",false);
        for(int i=0;i<10;i++) using(var bad=new RelayConnection(relay,locked.roomId,"viewer",LobbyKey.DeriveViewerKey(locked.name,"falsch"+i))) { bad.Start();Until(()=>bad.StopReason!=null); }
        using(var right=new RelayConnection(relay,locked.roomId,"viewer",locked.viewerKey)) {
            right.Start();
            Check(Until(()=>right.StopReason!=null) && right.StopReason=="too_many","the lockout stops with too_many even with the right password");
        }
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern** (`Viewers`/`StopReason` fehlen).

- [ ] **Step 3: Implementierung in `RelayConnection.cs`**

Felder neben `hostOnline` ergänzen:

```csharp
        volatile int viewers=-1;
        volatile string stopReason;
```

Properties neben `HostOnline`:

```csharp
        // Viewers following this host as reported by the relay; -1 until the relay says (older relays never do).
        public int Viewers { get { return viewers; } }
        // Why the connection stopped for good: auth_failed, too_many, unknown_room, replaced, protocol, rejected; null while working or retrying.
        public string StopReason { get { return stopReason; } }
```

Im `Reader` nach dem `host`-Zweig:

```csharp
                    else if((string)kind=="peers") { viewers=Convert.ToInt32(msg["viewers"]); }
```

`PolicyStatus` um eine Zeile vor dem `return "Stopped: relay rejected the connection.";` ergänzen:

```csharp
            if(reason=="Too many attempts") return "Stopped: too many failed attempts - wait a minute.";
```

und eine neue Methode daneben:

```csharp
        static string PolicyCode(string reason) {
            if(reason=="Authentication failed") return "auth_failed";
            if(reason=="Too many attempts") return "too_many";
            if(reason!=null && reason.StartsWith("Unsupported protocol version",StringComparison.Ordinal)) return "protocol";
            return "rejected";
        }
```

In `Loop` an den drei Stellen, an denen `phase=RelayPhase.Stopped` gesetzt wird, den Grund setzen:
- bei `replaced`: `stopReason="replaced";`
- bei `unknownRoom`: `stopReason="unknown_room";`
- beim terminalen Policy-Close (dort wo `PolicyTerminal(policyReason)` true ist und gestoppt wird): `stopReason=PolicyCode(policyReason);`

In `Loop` am Schleifenanfang `ready=false;hostOnline=false;` zu `ready=false;hostOnline=false;viewers=-1;` erweitern; ebenso in `Dispose` `viewers=-1;` ergänzen.

- [ ] **Step 4: Bauen + Test** → alle PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/RelayConnection.cs tests/LobbyTests.cs
git commit -m "Component: viewer count and machine-readable stop reasons on relay connections

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: `CoopWindow` + `ICoopSession`

**Files:**
- Create: `component/CoopWindow.cs`
- Test: `tests/LobbyTests.cs`

- [ ] **Step 1: Failing Test** – in `Run` `WindowTests(work);` ergänzen und die Fake-Session + Helfer anhängen:

```csharp
    sealed class FakeSession : ICoopSession {
        public string role="host",server="wss://relay.example/coop",stopReason,password,problem;
        public LobbyState lobby;public bool connected;public int viewers=-1,leaves;public LobbyGrant used;public string usedPassword,invite;
        public string Role { get { return role; } }
        public string Server { get { return server; } set { server=value; } }
        public LobbyState Lobby { get { return lobby; } }
        public bool Connected { get { return connected; } }
        public int Viewers { get { return viewers; } }
        public string StopReason { get { return stopReason; } }
        public string StatusLine { get { return lobby==null?"none":lobby.name+(connected?" ok":" ..."); } }
        public string SavedPassword { get { return password; } }
        public string JoinProblem() { return problem; }
        public void UseLobby(LobbyGrant grant,string pw) { used=grant;usedPassword=pw;lobby=LobbyState.From(grant,role,pw);password=role=="host"?pw:null; }
        public bool JoinInvite(string text) { string s,r,k;if(!Invite.TryParse(text,out s,out r,out k))return false;invite=text;lobby=LobbyState.FromLegacy(s,r,"viewer",k,"");return true; }
        public void Reconnect() { }
        public void Leave() { leaves++;lobby=null;connected=false;stopReason=null; }
        public void ExportDiagnostics(IWin32Window owner) { }
    }
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static Control Find(Control parent,string name){foreach(Control c in parent.Controls){if(c.Name==name)return c;var child=Find(c,name);if(child!=null)return child;}return null;}
    static void Click(Control c){c.GetType().GetMethod("OnClick",Hidden,null,new[]{typeof(EventArgs)},null).Invoke(c,new object[]{EventArgs.Empty});}
    static void Pump(Func<bool> ready){var clock=Stopwatch.StartNew();while(!ready() && clock.ElapsedMilliseconds<8000){Application.DoEvents();Thread.Sleep(10);}if(!ready())throw new Exception("Window timed out");}
    static bool Shown(Control c){return c!=null && c.Visible;}
    static void WindowTests(string work) {
        // Host: create panel, validation, creation, then the current-lobby panel.
        var host=new FakeSession();
        LobbyGrant made=null;
        Func<string,string,string,bool,LobbyGrant> create=(s,n,p,pub)=>{made=new LobbyGrant{server=s,name=LobbyKey.Normalize(n),roomId=LobbyKey.RoomId(LobbyKey.Normalize(n)),hostKey=new string('h',43),viewerKey=LobbyKey.DeriveViewerKey(n,p),isPublic=pub,locked=p.Length>0};return made;};
        Func<string,List<LobbyInfo>> none=s=>new List<LobbyInfo>();
        using(var w=new CoopWindow(host,null,create,none)) {
            w.Show();Pump(()=>Shown(Find(w,"create")));
            Check(Find(w,"join")==null,"host window has no join controls");
            Find(w,"lobbyName").Text="a b";Click(Find(w,"create"));
            Check(Find(w,"message").Text==Texts.T("lobby.invalidName"),"host sees the name rule");
            Find(w,"lobbyName").Text="RvGang";Find(w,"lobbyPassword").Text="banane";((CheckBox)Find(w,"lobbyPublic")).Checked=true;
            Click(Find(w,"create"));Pump(()=>host.used!=null);
            Check(made.name=="rvgang" && made.isPublic && host.usedPassword=="banane","create passes name, password and visibility");
            Pump(()=>Shown(Find(w,"leave")) && !Shown(Find(w,"create")));
            Check(Find(w,"message").Text==Texts.T("lobby.created","rvgang"),"host is told the lobby was created");
            Check(Shown(Find(w,"copyPassword")),"host can copy name and password");
            Click(Find(w,"leave"));Pump(()=>Shown(Find(w,"create")));
            Check(host.leaves==1 && host.lobby==null,"leave clears the lobby");
            w.Close();
        }
        // Server errors are shown in the window.
        var failing=new FakeSession();
        using(var w=new CoopWindow(failing,null,(s,n,p,pub)=>{throw new LobbyException(Texts.T("lobby.taken"));},none)) {
            w.Show();Pump(()=>Shown(Find(w,"create")));
            Find(w,"lobbyName").Text="rvgang";Click(Find(w,"create"));
            Pump(()=>Find(w,"message").Text==Texts.T("lobby.taken"));
            Check(failing.lobby==null,"a failed creation keeps the session without a lobby");
            w.Close();
        }
        // Viewer: list, selection fills the name, wrong password is explained and undone.
        var viewer=new FakeSession{role="viewer"};
        Func<string,List<LobbyInfo>> list=s=>new List<LobbyInfo>{new LobbyInfo{name="rvgang",locked=true,hostOnline=true,viewers=2,label="Duo / Fullgame"},new LobbyInfo{name="other",game="G",category="C"}};
        using(var w=new CoopWindow(viewer,null,create,list)) {
            w.Show();var items=(ListView)Find(w,"lobbyList");Pump(()=>items.Items.Count==2 && items.Items[0].Tag is LobbyInfo);
            Check(Find(w,"create")==null,"viewer window has no create controls");
            Check(items.Items[0].SubItems[2].Text=="Duo / Fullgame" && items.Items[1].SubItems[2].Text=="G · C","list shows the profile label or game and category");
            items.Items[0].Selected=true;Pump(()=>Find(w,"lobbyName").Text=="rvgang");
            Check(true,"selecting a lobby fills its name");
            viewer.problem="stop first";Click(Find(w,"join"));
            Check(Find(w,"message").Text=="stop first" && viewer.used==null,"join problems block joining");
            viewer.problem=null;Find(w,"lobbyPassword").Text="falsch";Click(Find(w,"join"));
            Check(viewer.used!=null && viewer.used.roomId=="lobby_rvgang" && viewer.used.viewerKey==LobbyKey.DeriveViewerKey("rvgang","falsch") && viewer.usedPassword==null,"join derives the key locally and never hands over the password");
            viewer.stopReason="auth_failed";
            Pump(()=>Find(w,"message").Text==Texts.T("lobby.wrongPassword"));
            Check(viewer.leaves==1 && viewer.lobby==null && Shown(Find(w,"join")),"wrong password is explained and the lobby is forgotten");
            viewer.used=null;Click(Find(w,"join"));Pump(()=>viewer.used!=null);viewer.connected=true;
            Pump(()=>Shown(Find(w,"leave")));
            Check(!Shown(Find(w,"copyPassword")) && Find(w,"copyPassword")==null,"viewers cannot copy a password");
            w.Close();
        }
        // Viewer: invitation fallback under Advanced.
        var legacy=new FakeSession{role="viewer"};
        using(var w=new CoopWindow(legacy,null,create,none)) {
            w.Show();Pump(()=>Shown(Find(w,"join")));
            Find(w,"invite").Text="nonsense";Click(Find(w,"joinInvite"));
            Check(Find(w,"message").Text==Texts.T("advanced.invalidInvite"),"invalid invitation is explained");
            Find(w,"invite").Text="lscoop:1:wss://relay.example/coop/abc#"+new string('v',32);Click(Find(w,"joinInvite"));
            Check(legacy.invite!=null && legacy.lobby!=null,"valid invitation is handed to the session");
            w.Close();
        }
        // Optional profile section is shown above the lobby.
        var profile=new Label{Name="profileMarker",Text="profile"};
        using(var w=new CoopWindow(new FakeSession(),profile,create,none)) { w.Show();Pump(()=>Shown(Find(w,"profileMarker")));Check(true,"profile section is embedded");w.Close(); }
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern** (Compile-Fehler `ICoopSession`/`CoopWindow`).

- [ ] **Step 3: `component/CoopWindow.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveSplit.Coop {
    // What the coop window needs from the running component. Tests use a fake.
    public interface ICoopSession {
        string Role { get; }            // "host" or "viewer"; fixed by the layout of this LiveSplit copy
        string Server { get; set; }
        LobbyState Lobby { get; }       // null when this copy is in no lobby
        bool Connected { get; }
        int Viewers { get; }            // -1 when unknown
        string StopReason { get; }      // see RelayConnection.StopReason
        string StatusLine { get; }
        string SavedPassword { get; }   // host only, null when unknown
        string JoinProblem();           // why this copy cannot follow a host right now, or null
        void UseLobby(LobbyGrant grant,string password);
        bool JoinInvite(string invite);
        void Reconnect();
        void Leave();
        void ExportDiagnostics(IWin32Window owner);
    }
    // One window for everything coop: optional profile section, lobby (create or join), advanced options.
    public sealed class CoopWindow : Form {
        readonly ICoopSession session;
        readonly Func<string,string,string,bool,LobbyGrant> create;
        readonly Func<string,List<LobbyInfo>> list;
        readonly TableLayoutPanel body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,AutoSize=true,Padding=new Padding(14) };
        readonly FlowLayoutPanel current=Stack(),createPanel=Stack(),joinPanel=Stack(),advanced=Stack();
        readonly Label status=new Label { Name="status",AutoSize=true,MaximumSize=new Size(480,0) };
        readonly Label message=new Label { Name="message",AutoSize=true,MaximumSize=new Size(480,0),Margin=new Padding(0,6,0,0) };
        readonly TextBox name=new TextBox { Name="lobbyName",Width=230,MaxLength=32 };
        readonly TextBox password=new TextBox { Name="lobbyPassword",Width=230,MaxLength=128,UseSystemPasswordChar=true };
        readonly CheckBox isPublic=new CheckBox { Name="lobbyPublic",AutoSize=true };
        readonly ListView lobbies=new ListView { Name="lobbyList",View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,Width=480,Height=150 };
        readonly TextBox server=new TextBox { Name="server",Width=360 };
        readonly TextBox invite=new TextBox { Name="invite",Width=360,MaxLength=2048 };
        readonly Timer refresh=new Timer { Interval=300 };
        bool busy,pendingJoin;
        public CoopWindow(ICoopSession session,Control profileSection) : this(session,profileSection,LobbyClient.Create,LobbyClient.List) { }
        internal CoopWindow(ICoopSession session,Control profileSection,Func<string,string,string,bool,LobbyGrant> create,Func<string,List<LobbyInfo>> list) {
            this.session=session;this.create=create;this.list=list;
            Text=Texts.T("window.title");Font=new Font("Segoe UI",9F);FormBorderStyle=FormBorderStyle.FixedDialog;
            StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=MaximizeBox=false;
            AutoScaleMode=AutoScaleMode.Dpi;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;MinimumSize=new Size(530,0);
            Controls.Add(body);
            if(profileSection!=null) { body.Controls.Add(Heading("section.profile"));body.Controls.Add(profileSection); }
            body.Controls.Add(Heading("section.lobby"));body.Controls.Add(status);
            BuildCurrent();
            if(session.Role=="host") BuildCreate(); else BuildJoin();
            body.Controls.Add(current);body.Controls.Add(createPanel);body.Controls.Add(joinPanel);body.Controls.Add(message);
            BuildAdvanced();
            var close=new Button { Name="close",Text=Texts.T("window.close"),AutoSize=true,MinimumSize=new Size(100,28),DialogResult=DialogResult.OK,Margin=new Padding(0,12,0,0) };
            CancelButton=close;body.Controls.Add(close);
            refresh.Tick+=(s,e)=>Sync();
            Shown+=(s,e)=>{ Sync();if(session.Role!="host" && session.Lobby==null) LoadList();refresh.Start(); };
            FormClosed+=(s,e)=>refresh.Stop();
        }
        static FlowLayoutPanel Stack() { return new FlowLayoutPanel { AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(0,4,0,4) }; }
        static Label Heading(string key) { return new Label { Text=Texts.T(key),AutoSize=true,Font=new Font("Segoe UI",9F,FontStyle.Bold),Margin=new Padding(0,10,0,2) }; }
        static FlowLayoutPanel Row(params Control[] controls) { var row=new FlowLayoutPanel { AutoSize=true,WrapContents=false,Margin=new Padding(0) };row.Controls.AddRange(controls);return row; }
        static Label Caption(string key) { return new Label { Text=Texts.T(key),AutoSize=false,Width=130,Height=23,TextAlign=ContentAlignment.MiddleLeft }; }
        static Button Action(string name,string key) { return new Button { Name=name,Text=Texts.T(key),AutoSize=true,MinimumSize=new Size(100,28) }; }
        void BuildCurrent() {
            var reconnect=Action("reconnect","lobby.reconnect");reconnect.Click+=(s,e)=>{ message.Text="";session.Reconnect();Sync(); };
            var leave=Action("leave","lobby.leave");
            leave.Click+=(s,e)=>{ session.Leave();message.Text="";Sync();if(session.Role!="host") LoadList(); };
            var row=Row(reconnect,leave);
            if(session.Role=="host") { var copy=Action("copyPassword","lobby.copyPassword");copy.Click+=(s,e)=>CopyInfo();row.Controls.Add(copy); }
            current.Controls.Add(row);
        }
        void BuildCreate() {
            isPublic.Text=Texts.T("lobby.public");
            var go=Action("create","lobby.create");go.Click+=async(s,e)=>await CreateAsync();
            createPanel.Controls.Add(Row(Caption("lobby.name"),name));
            createPanel.Controls.Add(Row(Caption("lobby.password"),password));
            createPanel.Controls.Add(isPublic);createPanel.Controls.Add(go);
        }
        void BuildJoin() {
            lobbies.Columns.Add(Texts.T("lobby.column.name"),130);lobbies.Columns.Add("",30);
            lobbies.Columns.Add(Texts.T("lobby.column.run"),200);lobbies.Columns.Add(Texts.T("lobby.column.online"),110);
            lobbies.SelectedIndexChanged+=(s,e)=>{ if(lobbies.SelectedItems.Count>0 && lobbies.SelectedItems[0].Tag is LobbyInfo) name.Text=((LobbyInfo)lobbies.SelectedItems[0].Tag).name; };
            lobbies.DoubleClick+=(s,e)=>password.Focus();
            var go=Action("join","lobby.join");go.Click+=(s,e)=>Join();
            var reload=Action("refresh","lobby.refresh");reload.Click+=(s,e)=>LoadList();
            joinPanel.Controls.Add(new Label { Text=Texts.T("lobby.publicList"),AutoSize=true });joinPanel.Controls.Add(lobbies);
            joinPanel.Controls.Add(Row(Caption("lobby.name"),name));
            joinPanel.Controls.Add(Row(Caption("lobby.password"),password));
            joinPanel.Controls.Add(Row(go,reload));
        }
        void BuildAdvanced() {
            var toggle=new LinkLabel { Name="advancedToggle",Text="+ "+Texts.T("section.advanced"),AutoSize=true,Margin=new Padding(0,10,0,2) };
            advanced.Visible=false;
            toggle.LinkClicked+=(s,e)=>{ advanced.Visible=!advanced.Visible;toggle.Text=(advanced.Visible?"- ":"+ ")+Texts.T("section.advanced"); };
            server.Text=session.Server;server.Leave+=(s,e)=>ApplyServer();
            advanced.Controls.Add(Row(Caption("advanced.server"),server));
            if(session.Role!="host") {
                var use=Action("joinInvite","advanced.invite");use.Click+=(s,e)=>JoinInvite();
                advanced.Controls.Add(Row(Caption("advanced.inviteCode"),invite));advanced.Controls.Add(use);
            }
            var diagnostics=Action("diagnostics","advanced.diagnostics");diagnostics.Click+=(s,e)=>session.ExportDiagnostics(this);
            advanced.Controls.Add(diagnostics);
            body.Controls.Add(toggle);body.Controls.Add(advanced);
        }
        string ApplyServer() {
            string value=server.Text.Trim();
            if(value.Length==0) { value=Invite.Community;server.Text=value; }
            if(value!=session.Server) session.Server=value;
            return value;
        }
        void Sync() {
            bool has=session.Lobby!=null;
            current.Visible=has;createPanel.Visible=!has && session.Role=="host";joinPanel.Visible=!has && session.Role!="host";
            if(!busy) status.Text=session.StatusLine;
            Find("reconnect").Visible=has && !session.Connected && session.StopReason!=null && !pendingJoin;
            var copy=Find("copyPassword");if(copy!=null) copy.Visible=has && session.SavedPassword!=null;
            if(pendingJoin) {
                if(session.Connected) { pendingJoin=busy=false;message.Text=""; }
                else if(session.StopReason!=null) { pendingJoin=busy=false;string reason=session.StopReason;session.Leave();Report(Explain(reason),true);Sync(); }
            }
        }
        Control Find(string controlName) { var found=Controls.Find(controlName,true);return found.Length>0?found[0]:null; }
        public static string Explain(string reason) {
            switch(reason) {
                case "auth_failed": return Texts.T("lobby.wrongPassword");
                case "too_many": return Texts.T("lobby.tooMany");
                case "unknown_room": return Texts.T("lobby.unknown");
                case "replaced": return Texts.T("lobby.replaced");
                case "protocol": return Texts.T("lobby.protocol");
                default: return Texts.T("lobby.rejected");
            }
        }
        async Task CreateAsync() {
            if(busy) return;
            message.Text="";
            string lobbyName=LobbyKey.Normalize(name.Text);
            if(lobbyName==null) { Report(Texts.T("lobby.invalidName"),true);return; }
            busy=true;string target=ApplyServer(),secret=password.Text;bool listed=isPublic.Checked;status.Text=Texts.T("lobby.creating");
            try {
                var grant=await Task.Run(()=>create(target,lobbyName,secret,listed));
                if(IsDisposed) return;
                session.UseLobby(grant,secret);
                Copy(Info(grant.name,secret));
                Report(Texts.T("lobby.created",grant.name),false);
            } catch(Exception e) {
                if(!IsDisposed) Report(e is LobbyException?e.Message:Texts.T("lobby.unreachable",e.Message),true);
            } finally {
                busy=false;
                if(!IsDisposed) Sync();
            }
        }
        void Join() {
            if(busy) return;
            message.Text="";
            string lobbyName=LobbyKey.Normalize(name.Text);
            if(lobbyName==null) { Report(Texts.T("lobby.invalidName"),true);return; }
            string problem=session.JoinProblem();
            if(problem!=null) { Report(problem,true);return; }
            var grant=new LobbyGrant { server=ApplyServer(),name=lobbyName,roomId=LobbyKey.RoomId(lobbyName),
                viewerKey=LobbyKey.DeriveViewerKey(lobbyName,password.Text),locked=password.Text.Length>0 };
            busy=pendingJoin=true;status.Text=Texts.T("lobby.joining");
            session.UseLobby(grant,null);
            Sync();
        }
        void JoinInvite() {
            message.Text="";
            string problem=session.JoinProblem();
            if(problem!=null) { Report(problem,true);return; }
            if(!session.JoinInvite(invite.Text)) { Report(Texts.T("advanced.invalidInvite"),true);return; }
            busy=pendingJoin=true;
            Sync();
        }
        async void LoadList() {
            string target=ApplyServer();
            lobbies.Items.Clear();lobbies.Items.Add(new ListViewItem(Texts.T("lobby.loading")));
            try {
                var found=await Task.Run(()=>list(target));
                if(IsDisposed) return;
                lobbies.Items.Clear();
                foreach(var l in found) {
                    string run=l.label ?? ((l.game ?? "")+(l.category!=null?" · "+l.category:""));
                    string online=(l.hostOnline?"● ":"○ ")+l.viewers;
                    lobbies.Items.Add(new ListViewItem(new[] { l.name,l.locked?"🔒":"",run,online }) { Tag=l });
                }
                if(found.Count==0) lobbies.Items.Add(new ListViewItem(Texts.T("lobby.none")));
            } catch(Exception e) {
                if(IsDisposed) return;
                lobbies.Items.Clear();
                Report(e is LobbyException?e.Message:Texts.T("lobby.unreachable",e.Message),true);
            }
        }
        static string Info(string lobby,string secret) { return secret.Length>0?Texts.T("lobby.passwordInfo",lobby,secret):Texts.T("lobby.nameInfo",lobby); }
        static bool Copy(string text) {
            try { var data=new DataObject();data.SetText(text,TextDataFormat.UnicodeText);Clipboard.SetDataObject(data,true,3,75);return true; }
            catch(ExternalException) { return false; }
        }
        void CopyInfo() {
            var lobby=session.Lobby;
            if(lobby==null) return;
            Report(Copy(Info(lobby.name ?? lobby.roomId,session.SavedPassword ?? ""))?Texts.T("copy.done"):Texts.T("copy.busy"),false);
        }
        void Report(string text,bool error) { message.ForeColor=error?Color.Firebrick:Color.SeaGreen;message.Text=text; }
        protected override void Dispose(bool disposing) { if(disposing) refresh.Dispose();base.Dispose(disposing); }
    }
}
```

Hinweis: `Timer` ist hier `System.Windows.Forms.Timer` (kein `using System.Threading;` in dieser Datei).

- [ ] **Step 4: Bauen + Test** → alle PASS. Die Tests laufen mit `Application.DoEvents`; `Clipboard` braucht STA – `powershell.exe` 5.1 läuft standardmäßig STA. Falls ein Clipboard-Fehler auftritt: Runner-Aufruf um `-STA` ergänzen.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/CoopWindow.cs tests/LobbyTests.cs
git commit -m "Component: coop window for creating, joining and leaving lobbies

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Komponente auf Lobbys umstellen

**Files:**
- Modify: `component/Settings.cs` (komplett ersetzen)
- Modify: `component/Component.cs`
- Modify: `tests/Regression.cs` (`StableSettings`)
- Test: `tests/LobbyTests.cs`

- [ ] **Step 1: Failing Test** – in `Run` `ComponentTests(relay,work);` ergänzen:

```csharp
    static LiveSplit.Model.LiveSplitState NewState() {
        var run=new LiveSplit.Model.Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());run.Add(new LiveSplit.Model.Segment("Finish"));
        return new LiveSplit.Model.LiveSplitState(run,null,new LiveSplit.UI.Layout(),new LiveSplit.Options.LayoutSettings(),new LiveSplit.Options.Settings());
    }
    static System.Xml.XmlNode Xml(string inner){var doc=new System.Xml.XmlDocument();doc.LoadXml("<Settings>"+inner+"</Settings>");return doc.DocumentElement;}
    static void ComponentTests(string relay,string work) {
        string path=Path.Combine(work,"component-local.json");
        if(File.Exists(path)) File.Delete(path);
        // Settings: only the role is stored; output is stable; legacy keys are read once for migration.
        var settings=new CoopSettings();
        settings.LoadXml(Xml("<Version>1</Version><Address>wss://relay.example/coop</Address><Room>oldroom</Room><Role>host</Role><ProtectedKey>"+LocalState.Protect(new string('k',43))+"</ProtectedKey><ProtectedInviteKey>"+LocalState.Protect(new string('i',43))+"</ProtectedInviteKey>"));
        Check(settings.Role=="host" && settings.Legacy!=null && settings.Legacy.roomId=="oldroom","legacy layout room is detected");
        string a=settings.SaveXml(new System.Xml.XmlDocument()).OuterXml,b=settings.SaveXml(new System.Xml.XmlDocument()).OuterXml;
        Check(a==b && !a.Contains("Protected") && !a.Contains("oldroom") && a.Contains("<Role>host</Role>"),"layout settings hold only the role and are stable");
        // Component: migration, auto-connect, status line, leave.
        var grant=LobbyClient.Create(relay,Unique("cp"),"banane",false);
        new LocalState { lobby=LobbyState.From(grant,"host","banane") }.Save(path);
        var state=NewState();
        using(var component=new CoopComponent(state,path)) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            Check(component.Lobby!=null && component.Lobby.name==grant.name,"component loads the saved lobby");
            component.Update(null,state,200,22,LiveSplit.UI.LayoutMode.Vertical);
            Check(Until(()=>{component.Update(null,state,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.Connected;}),"component connects automatically on start");
            Check(Until(()=>{component.Update(null,state,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.StatusLine==grant.name+" · "+Texts.T("status.host")+" · "+Texts.T("status.online",0);}),"host status line shows lobby, role and viewers: "+component.StatusLine);
            Check(component.SavedPassword=="banane","host can read its saved password");
            component.Leave();
            Check(component.Lobby==null && LocalState.Load(path).lobby==null && !component.Connected,"leave forgets the lobby on disk");
        }
        File.Delete(path);
        var migrated=NewState();
        using(var component=new CoopComponent(migrated,path)) {
            component.SetSettings(Xml("<Version>1</Version><Address>wss://relay.example/coop</Address><Room>oldroom</Room><Role>viewer</Role><ProtectedKey>"+LocalState.Protect(new string('k',43))+"</ProtectedKey><ProtectedInviteKey></ProtectedInviteKey>"));
            var saved=LocalState.Load(path).lobby;
            Check(saved!=null && saved.roomId=="oldroom" && saved.role=="viewer" && saved.name==null,"legacy viewer room migrates into coop-local.json");
            Check(component.StatusLine.StartsWith(Texts.T("status.inviteRoom")),"migrated rooms are labelled as invitation rooms: "+component.StatusLine);
        }
        Check(new CoopComponent(NewState(),path).ContextMenuControls.Keys.SequenceEqual(new[]{Texts.T("menu.coop")}),"the context menu has a single Coop entry");
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern** (Konstruktor `CoopComponent(state,path)`, `Legacy` usw. fehlen).

- [ ] **Step 3: `component/Settings.cs` komplett ersetzen**

```csharp
using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.Coop {
    // A room stored in a layout by versions before lobbies; read once and moved to coop-local.json.
    public sealed class LegacyRoom { public string server,roomId,role,key,inviteKey; }
    // Layout settings: only the role of this LiveSplit copy. Lobbies live in coop-local.json, never in layouts.
    public sealed class CoopSettings : UserControl {
        readonly ComboBox role=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Width=290 };
        readonly Label status=new Label { AutoSize=true,MaximumSize=new Size(400,0) };
        public event Action OpenRequested;
        public string Role { get { return role.SelectedIndex==0?"host":"viewer"; } }
        public LegacyRoom Legacy { get; private set; }
        public CoopSettings() {
            AutoScroll=true;Size=new Size(460,200);
            role.Items.AddRange(new object[] { Texts.T("settings.roleHost"),Texts.T("settings.roleViewer") });role.SelectedIndex=1;
            var grid=new TableLayoutPanel { Dock=DockStyle.Top,ColumnCount=2,AutoSize=true,Padding=new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            grid.Controls.Add(new Label { Text=Texts.T("settings.role"),AutoSize=true },0,0);grid.Controls.Add(role,1,0);
            var open=new Button { Text=Texts.T("settings.open"),AutoSize=true };
            open.Click+=(s,e)=>{ if(OpenRequested!=null) OpenRequested(); };
            grid.Controls.Add(open,1,1);grid.Controls.Add(status,1,2);
            var note=new Label { Text=Texts.T("settings.note"),AutoSize=true,MaximumSize=new Size(400,0) };
            grid.Controls.Add(note,0,3);grid.SetColumnSpan(note,2);
            Controls.Add(grid);
        }
        public void Status(string message) { status.Text=message; }
        static string Read(XmlNode n,string name,string fallback) { var child=n[name];return child==null?fallback:child.InnerText; }
        public void LoadXml(XmlNode n) {
            role.SelectedIndex=Read(n,"Role","viewer")=="host"?0:1;
            Legacy=null;
            try {
                string key=LocalState.Unprotect(Read(n,"ProtectedKey",""));
                string room=Read(n,"Room","");
                if(key.Length>=32 && room.Length>0)
                    Legacy=new LegacyRoom { server=Read(n,"Address",Invite.Community),roomId=room,role=Role,key=key,inviteKey=LocalState.Unprotect(Read(n,"ProtectedInviteKey","")) };
            } catch(CryptographicException) { } catch(FormatException) { }
        }
        public XmlNode SaveXml(XmlDocument doc) {
            var node=doc.CreateElement("Settings");
            foreach(var item in new[] { new[] { "Version","2" },new[] { "Role",Role } }) { var child=doc.CreateElement(item[0]);child.InnerText=item[1];node.AppendChild(child); }
            return node;
        }
    }
}
```

- [ ] **Step 4: `component/Component.cs` umbauen**

4a. Klassenkopf und Felder: `public sealed class CoopComponent : IComponent, ICoopSession {`. Neue Felder nach `readonly CoopSettings settings=new CoopSettings();`:

```csharp
        readonly string localPath;
        LocalState local;
        bool autoTried;
        string statusLine="";
```

4b. Konstruktoren ersetzen (bisheriger Inhalt bleibt im zweiten):

```csharp
        public CoopComponent(LiveSplitState state) : this(state,LocalState.DefaultPath) { }
        // The local state path is injectable for tests.
        public CoopComponent(LiveSplitState state,string localPath) {
            this.state=state;this.localPath=localPath;local=LocalState.Load(localPath);
            settings.OpenRequested+=OpenWindow;
            state.OnStart+=Started;state.OnSplit+=Split;state.OnUndoSplit+=Undo;state.OnSkipSplit+=Skip;
            state.OnPause+=Paused;state.OnResume+=Resumed;state.OnReset+=Reset;
            CoopDiagnostics.Write("component_loaded",new { component=diagnosticId });
        }
```

(Die Zeilen `settings.ConnectRequested+=...`, `settings.ViewerProblem=...` entfallen.)

4c. `ICoopSession` implementieren (nach `RequireNoAutoSplitter`):

```csharp
        public string Role { get { return settings.Role; } }
        public string Server { get { return local.server ?? Invite.Community; } set { local.server=value;SaveLocal(); } }
        public LobbyState Lobby { get { return local.lobby!=null && local.lobby.role==Role?local.lobby:null; } }
        public bool Connected { get { return connection!=null && connection.Ready; } }
        public int Viewers { get { return connection==null?-1:connection.Viewers; } }
        public string StopReason { get { return connection==null?null:connection.StopReason; } }
        public string StatusLine { get { return statusLine; } }
        public string SavedPassword {
            get {
                var lobby=Lobby;
                if(lobby==null || Role!="host") return null;
                try { string value=LocalState.Unprotect(lobby.protectedPassword);return value.Length>0 || lobby.locked==false?value:null; }
                catch(CryptographicException) { return null; }
            }
        }
        public string JoinProblem() {
            var problem=AutoSplitterProblem(state);
            if(problem!=null) return problem;
            if((connection==null || role!="viewer") && state.CurrentPhase!=TimerPhase.NotRunning) return Texts.T("viewer.stopTimer");
            return null;
        }
        public void UseLobby(LobbyGrant grant,string password) { local.lobby=LobbyState.From(grant,Role,password);SaveLocal();Connect(); }
        public bool JoinInvite(string text) {
            string server,room,key;
            if(!Invite.TryParse(text,out server,out room,out key)) return false;
            local.lobby=LobbyState.FromLegacy(server,room,"viewer",key,"");SaveLocal();Connect();return true;
        }
        public void Reconnect() { Connect(); }
        public void Leave() { Disconnect();local.lobby=null;SaveLocal(); }
        void SaveLocal() {
            try { local.Save(localPath); }
            catch(Exception e) { CoopDiagnostics.Error("local_state",e); }
        }
        string LobbyTitle() { var lobby=Lobby;return lobby==null?"":lobby.name ?? Texts.T("status.inviteRoom"); }
        void OpenWindow() {
            using(var window=new CoopWindow(this,null)) window.ShowDialog(state.Form);
        }
```

Dazu oben `using System.Security.Cryptography;` ergänzen.

4d. `Connect()` ersetzen:

```csharp
        public void Connect() {
            Disconnect();
            var lobby=Lobby;
            if(lobby==null) return;
            try {
                role=Role;CoopDiagnostics.Write("connect_requested",new { component=diagnosticId,role=role });
                string key;
                try { key=LocalState.Unprotect(role=="host"?lobby.protectedHostKey:lobby.protectedViewerKey); }
                catch(CryptographicException) { SetStatus(Texts.T("lobby.keyUnreadable"),Color.IndianRed);return; }
                if(role=="viewer") { RequireNoAutoSplitter(state);mirror=new Mirror(state); }
                connection=new RelayConnection(lobby.server,lobby.roomId,role,key);
                connection.Start();dirty=true;hasSnapshot=false;frozenElapsed=-1;publishedGeneration=-1;publishedStructure=null;
                SetStatus(LobbyTitle()+" · "+Texts.T("status.connecting"),Color.Goldenrod);
            } catch(AutoSplitterActive e) { CoopDiagnostics.Error("autosplitter_guard",e);Disconnect();SetStatus(Texts.T("status.autosplitter"),e.Message,Color.IndianRed); }
            catch(Exception e) { CoopDiagnostics.Error("component",e);Disconnect();SetStatus(e.Message,Color.IndianRed); }
        }
```

4e. In `Disconnect()` `SetStatus("Disconnected",Color.Gray);` ersetzen durch:

```csharp
            hasSnapshot=false;
            if(Lobby==null) SetStatus(Texts.T("status.notConnected"),Color.Gray); else SetStatus(LobbyTitle()+" · "+Texts.T("status.reconnecting"),Color.Gray);
```

(das vorhandene `hasSnapshot=false;` davor entfernen, damit es nicht doppelt steht).

4f. `SetStatus(string hudMessage,string detailMessage,Color c)` ersetzen:

```csharp
        void SetStatus(string hudMessage,string detailMessage,Color c) { statusLine=hudMessage;label="Coop: "+hudMessage;colour=c;settings.Status(detailMessage); }
```

4g. In `Update` ganz am Anfang:

```csharp
            if(!autoTried) { autoTried=true;if(Lobby!=null) Connect(); else SetStatus(Texts.T("status.notConnected"),Color.Gray); }
```

und die Statusaufrufe im Host- und Viewer-Zweig ersetzen:
- Host: `SetStatus(connection.Ready?"Host connected":connection.Status,...)` →

```csharp
                        string title=LobbyTitle();
                        if(connection.Ready) SetStatus(title+" · "+Texts.T("status.host")+(connection.Viewers>=0?" · "+Texts.T("status.online",connection.Viewers):""),connection.Status,Color.MediumAquamarine);
                        else if(connection.StopReason!=null) SetStatus(title+" · "+CoopWindow.Explain(connection.StopReason),connection.Status,Color.IndianRed);
                        else SetStatus(title+" · "+Texts.T("status.reconnecting"),connection.Status,Color.Goldenrod);
```

- Viewer: `if(live) SetStatus("Following host",...)` → `if(live) SetStatus(LobbyTitle()+" · "+Texts.T("status.following"),Color.MediumAquamarine);`; im Zweig `else if(hasSnapshot)` die `hud`-Texte ersetzen: `!connection.Ready` → `connection.Phase==RelayPhase.Stopped?CoopWindow.Explain(connection.StopReason):Texts.T("status.reconnecting")`, `!connection.HostOnline` → `Texts.T("status.hostOffline")`, sonst `Texts.T("status.stale")`, jeweils mit `LobbyTitle()+" · "+` davor; `"Waiting for host"` → `LobbyTitle()+" · "+Texts.T("status.waiting")`; der letzte Zweig `SetStatus(connection.Status,...)` → 

```csharp
                        else SetStatus(LobbyTitle()+" · "+(connection.StopReason!=null?CoopWindow.Explain(connection.StopReason):Texts.T("status.reconnecting")),connection.Status,PhaseColor(connection.Phase));
```

- `quickNotice`-Logik samt Feldern `quickNotice`/`quickNoticeUntil` entfernen (wurde nur von „Copy invite“ benutzt).

4h. `ContextMenuControls` ersetzen:

```csharp
        public IDictionary<string,Action> ContextMenuControls { get { return new Dictionary<string,Action> { { Texts.T("menu.coop"),OpenWindow } }; } }
```

4i. `ExportDiagnostics()` zu `public void ExportDiagnostics(IWin32Window owner)` machen; im Rumpf `state.Form` durch `owner` ersetzen.

4j. `SetSettings` ersetzen:

```csharp
        public void SetSettings(XmlNode node) {
            settings.LoadXml(node);
            // Rooms saved in layouts by older versions move to coop-local.json once.
            if(settings.Legacy!=null && local.lobby==null) {
                var legacy=settings.Legacy;
                local.lobby=LobbyState.FromLegacy(legacy.server,legacy.roomId,legacy.role,legacy.key,legacy.inviteKey);SaveLocal();
                CoopDiagnostics.Write("legacy_room_migrated",new { component=diagnosticId,role=legacy.role });
            }
        }
```

4k. Am Ende von `Update`: nach dem Auto-Connect gilt: ohne Verbindung keine Statusänderung nötig. `Draw` bleibt unverändert.

- [ ] **Step 5: `tests/Regression.cs` `StableSettings` ersetzen**

Den Rumpf von `StableSettings()` ersetzen durch:

```csharp
            var settings=new CoopSettings();
            var doc=new XmlDocument();doc.LoadXml("<Settings><Version>2</Version><Role>host</Role></Settings>");
            settings.LoadXml(doc.DocumentElement);
            string a=settings.SaveXml(new XmlDocument()).OuterXml,b=settings.SaveXml(new XmlDocument()).OuterXml;
            Check(a==b,"Coop settings serialize identically");
            Check(!a.Contains("Protected"),"Coop settings contain no keys");
```

- [ ] **Step 6: Bauen + alle Lobby-Tests** → alle PASS.

- [ ] **Step 7: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add -A component tests
git commit -m "Component: lobbies from coop-local.json, auto-connect, single Coop menu entry

Layouts store only the role; rooms from older layouts migrate once.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Echter LiveSplit-Lauf (manuelle Abnahme durch den Controller)

Kein Subagent-Task. Der Controller (Hauptsitzung) prüft nach Task 8:
1. `dist/LiveSplit.Coop.dll` in eine **Kopie** von `Timer/Anwendung/Host` und `Timer/Anwendung/Viewer` unter `.local/native/` legen (nie in die echten Ordner).
2. Lokale Fixture starten, Host-Kopie: Rechtsklick → Coop… → Lobby erstellen (Server unter Erweitert auf die Fixture), Viewer-Kopie: Lobby aus Liste wählen, Passwort, Beitreten → „folgt Host“, Host zeigt „1 online“.
3. Viewer-Kopie schließen und neu öffnen → verbindet automatisch.
4. Screenshots nach `.local/native/`.
