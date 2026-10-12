# Plan 5: RV-Timer in einer Staging-Kopie umstellen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eine vollständige Kopie des RV-Timers unter `C:\home\repos\livesplit-coop\.local\staging\LS RVtheer` läuft mit der neuen Komponente, dem Profil-Paket und einem fragenlosen Starter – automatisch geprüft für alle 48 Profile, ohne eine einzige Zeit zu verändern. Das Original bleibt unberührt.

**Architecture:** Kopie des Ordners (ohne Logs/Archiv/Pakete), neue DLL in beide `Components`, `coop-profiles.json` per Generator, neuer `Start-Timer.ps1`, der die Profil-Vorbereitung über die DLL (`ProfilePack`, `LocalState`, `ProfilePreparer`) erledigt. Ein Prüfskript vergleicht die Zeiten aller LSS-Dateien vor/nach.

**Tech Stack:** Windows PowerShell 5.1, Python 3 (Generator), LiveSplit.Coop.dll aus `dist/`.

**Quelle (nur lesen!):** `C:\home\Speedrunning\LS RVtheer`. **Ziel:** `C:\home\repos\livesplit-coop\.local\staging\LS RVtheer` (git-ignoriert). Nie etwas unter der Quelle schreiben oder löschen.

---

### Task 1: Staging-Kopie anlegen

- [ ] **Step 1: Kopieren**

```bash
SRC="/c/home/Speedrunning/LS RVtheer"; DST="/c/home/repos/livesplit-coop/.local/staging/LS RVtheer"
rm -rf "$DST" && mkdir -p "$DST"
cp -r "$SRC/Timer" "$SRC/Start RV.cmd" "$SRC/RV beitreten.cmd" "$DST/"
rm -rf "$DST/Timer/Anwendung/Host/Logs" "$DST/Timer/Anwendung/Viewer/Logs"
```

- [ ] **Step 2: Neue DLL einsetzen**

Vorher bauen (`scripts/build.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"`), dann:

```bash
cp /c/home/repos/livesplit-coop/dist/LiveSplit.Coop.dll "$DST/Timer/Anwendung/Host/Components/"
cp /c/home/repos/livesplit-coop/dist/LiveSplit.Coop.dll "$DST/Timer/Anwendung/Viewer/Components/"
```

- [ ] **Step 3: Profil-Paket erzeugen**

```bash
python -I "$DST/Timer/System/make-coop-profiles.py" "$DST/Timer/coop-profiles.json"
```

Expected: `Wrote 48 profiles …` und sechs Kategoriezeilen.

---

### Task 2: Schlanker Starter

**Files:** Replace `$DST/Timer/System/Start-Timer.ps1` (nur in der Kopie!). Die CMDs (`Start RV.cmd` → `-Role Host`, `RV beitreten.cmd` → `-Role Viewer`) bleiben unverändert.

- [ ] **Step 1: Neuer `Start-Timer.ps1`**

```powershell
param(
    [ValidateSet('Host','Viewer')][string]$Role='Host',
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
try {
    $root=Split-Path -Parent $PSScriptRoot
    $app=Join-Path $root ('Anwendung\'+$Role)
    $exe=Join-Path $app 'LiveSplit.exe'
    if(Get-Process LiveSplit -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe}) {
        throw 'Dieser Timer ist bereits offen.'
    }
    # Pending component updates from older packages are applied as before.
    if(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Updates\graph-category.json')) {
        & (Join-Path $PSScriptRoot 'Apply-GraphCategoryFix.ps1') -Role $Role
    }
    foreach($dll in @('LiveSplit.Core.dll','UpdateManager.dll','Components\LiveSplit.Coop.dll')){[void][Reflection.Assembly]::LoadFrom((Join-Path $app $dll))}
    $pack=[LiveSplit.Coop.ProfilePack]::Find($app)
    if(!$pack){throw 'coop-profiles.json fehlt. Bitte das komplette Paket entpacken.'}
    $configPath=Join-Path $app 'settings.cfg'
    if($Role -eq 'Host') {
        $local=[LiveSplit.Coop.LocalState]::Load((Join-Path $app 'coop-local.json'))
        $profile=$pack.ByKey($local.lastProfile)
        if(!$profile){$profile=$pack.profiles[0]}
        [LiveSplit.Coop.ProfilePreparer]::Prepare($pack,$profile,'host')
        $runPath=$pack.FullPath($profile.splits);$layoutPath=$pack.FullPath($profile.layout)
        [LiveSplit.Coop.ProfilePreparer]::RememberInSettings($configPath,$runPath,$pack.game,$profile.category)
        Write-Host ('RV There Yet? - Host. Profil: '+$profile.label+' ('+$profile.splits+')')
        Write-Host 'Profil und Lobby wechselst du im Timer: Rechtsklick > Coop...'
    } else {
        $layoutPath=$pack.FullPath($pack.viewerLayout)
        $runPath=Join-Path $root 'Splits\Viewer.lss'
        $layout=New-Object Xml.XmlDocument;$layout.Load($layoutPath)
        if($layout.SelectNodes('/Layout/Components/Component[Path="LiveSplit.ScriptableAutoSplit.dll" or Path="LiveSplit.AutoSplittingRuntime.dll"]').Count){throw 'Viewer-Layout darf keinen Autosplitter enthalten.'}
        $role=$layout.SelectSingleNode('/Layout/Components/Component[Path="LiveSplit.Coop.dll"]/Settings/Role')
        if(!$role){throw 'Coop-Komponente fehlt im Viewer-Layout.'}
        $role.InnerText='viewer';$layout.Save($layoutPath)
        Write-Host 'RV Coop - Mitspieler. Lobby beitreten: Rechtsklick > Coop...'
    }
    if($PrepareOnly){Write-Output ('Vorbereitet: '+$runPath);exit 0}
    Start-Process -FilePath $exe -WorkingDirectory $app -ArgumentList ('-s "'+$runPath+'" -l "'+$layoutPath+'"')
} catch {
    Write-Host ('Fehler: '+$_.Exception.Message) -ForegroundColor Red
    exit 1
}
```

- [ ] **Step 2: Trockenlauf**

```bash
cd "$DST" && powershell -NoProfile -ExecutionPolicy Bypass -File "Timer/System/Start-Timer.ps1" -Role Host -PrepareOnly
powershell -NoProfile -ExecutionPolicy Bypass -File "Timer/System/Start-Timer.ps1" -Role Viewer -PrepareOnly
```

Expected: je `Vorbereitet: …`, Exit 0; Host-Pfad ist das erste Profil (Mabutts Valley / All Checkpoints / Solo / NMG).

---

### Task 3: Prüfskript „alle 48 Profile, keine Zeit verändert“

**Files:** Create `C:\home\repos\livesplit-coop\scripts\verify-staging.ps1` (im Repo, generisch genug für jedes Paket).

- [ ] **Step 1: Skript**

```powershell
param([Parameter(Mandatory=$true)][string]$TimerRoot)
$ErrorActionPreference='Stop'
$app=Join-Path $TimerRoot 'Anwendung\Host'
foreach($dll in @('LiveSplit.Core.dll','UpdateManager.dll','Components\LiveSplit.Coop.dll')){[void][Reflection.Assembly]::LoadFrom((Join-Path $app $dll))}
$pack=[LiveSplit.Coop.ProfilePack]::Find($app)
if(!$pack){throw 'no pack'}
# Fingerprint of everything that counts as personal history: attempts, attempt history, all segment times.
function Fingerprint([string]$path){
    $x=New-Object Xml.XmlDocument;$x.Load($path)
    $parts=@($x.SelectSingleNode('/Run/AttemptCount').InnerText,$x.SelectSingleNode('/Run/AttemptHistory').OuterXml,$x.SelectSingleNode('/Run/Segments').OuterXml)
    return ($parts -join '|')
}
$failed=0
foreach($p in $pack.profiles){
    $splits=$pack.FullPath($p.splits)
    $before=Fingerprint $splits
    [LiveSplit.Coop.ProfilePreparer]::Prepare($pack,$p,'host')
    $after=Fingerprint $splits
    $run=$null
    $stream=[IO.File]::OpenRead($splits)
    try { $run=(New-Object LiveSplit.Model.RunFactories.StandardFormatsRunFactory($stream,$splits)).Create((New-Object LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory)) } finally { $stream.Dispose() }
    $ok=($before -eq $after) -and $run.CategoryName -eq $p.category
    foreach($k in $p.variables.Keys){ if($run.Metadata.VariableValueNames[$k] -ne $p.variables[$k]){$ok=$false} }
    if($pack.Match($pack.game,$run.CategoryName,$run.Metadata.VariableValueNames) -ne $p){$ok=$false}
    if(!$ok){$failed++;Write-Output ('FAIL '+$p.splits)} else {Write-Output ('ok   '+$p.splits)}
}
if($failed){throw "$failed profiles failed"}
Write-Output ('ALL '+$pack.profiles.Count+' PROFILES OK')
```

- [ ] **Step 2: Ausführen**

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-staging.ps1 -TimerRoot "C:\home\repos\livesplit-coop\.local\staging\LS RVtheer\Timer"
```

Expected: 48× `ok`, `ALL 48 PROFILES OK`. Insbesondere die persönlichen Dateien (Mabutts Duo/Trio All Checkpoints NMG; Bay Duo/Trio All Checkpoints NMG) behalten Versuche und Zeiten; Bay Duo/Trio bekommen Kategorie „St. Búttin Bay All Chekpoints“ und die Variablen.

- [ ] **Step 3: Zusätzlich gegen das Original vergleichen**

Für jede Splits-Datei: Fingerprint der Staging-Datei == Fingerprint der Originaldatei unter `C:\home\Speedrunning\LS RVtheer\Timer\Splits` (nur lesen). Als zweiter Lauf desselben Skripts mit `-CompareTo "C:\home\Speedrunning\LS RVtheer\Timer"` – Parameter ergänzen: wenn gesetzt, statt `$before` den Fingerprint der gleich benannten Originaldatei verwenden (ohne dort etwas zu schreiben).

- [ ] **Step 4: Commit** (nur das Skript; die Staging-Kopie ist git-ignoriert)

```bash
cd /c/home/repos/livesplit-coop && git add scripts/verify-staging.ps1
git commit -m "Scripts: verify a profile pack keeps every split time while preparing all profiles

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Abnahme mit echtem LiveSplit (Controller + Nutzer)

1. Lokales Relay: `node relay-cloudflare/test/native-fixture.mjs` (druckt `ws://127.0.0.1:<port>/coop`).
2. Staging `Start RV.cmd` → LiveSplit (Host) öffnet ohne Fragen. Rechtsklick → Coop… → Erweitert: Server = Fixture-URL → Lobby erstellen.
3. Staging `RV beitreten.cmd` → Viewer: Coop… → Erweitert: Server = Fixture → Lobby in der Liste wählen → Beitreten.
4. Host: Coop… → anderes Profil laden → Layout/Splits wechseln, Viewer folgt mit neuem Label.
5. Zwei kurze Versuche (Split per Hotkey), einmal beenden, einmal resetten → Viewer-Notiz, Viewer-Splits haben die Versuche.
