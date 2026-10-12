# Plan 4: Viewer speichern Splits (Recorder) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ein Viewer zeichnet jeden mitverfolgten Versuch des Hosts in seine eigene Splits-Datei auf (Versuche, Historie, Golds, PB) und übernimmt beim ersten Kontakt eine schnellere Host-PB, damit er später selbst hosten kann.

**Architecture:** `RunFiles` (LSS laden/speichern/aus Snapshot erzeugen), `ViewerLibrary` (Ziel-Datei pro Host-Profil), `HostLock` (Sperrdatei, solange eine Host-Kopie dieselbe Datei offen hat), `AttemptRecorder` (beobachtet Snapshots, erkennt Versuchsende, verbucht über LiveSplits `TimerModel.Reset(true)` auf einem Schatten-`LiveSplitState`). Die Komponente füttert den Recorder im Viewer und hält im Host die Sperre; das Coop-Fenster zeigt eine kurze Notiz.

**Tech Stack:** C# 5 (csc v4.0.30319), .NET 4.8, LiveSplit.Core (`TimerModel`, `XMLRunSaver`, `StandardFormatsRunFactory`), PowerShell 5.1 `Add-Type`-Tests.

Spec: `docs/superpowers/specs/2026-10-12-lobbies-design.md`, Abschnitt 3.5. Voraussetzung: Pläne 2 und 3 umgesetzt (`ProfilePack.Match`, `Texts`, `ICoopSession`, `CoopWindow`).

**Arbeitsverzeichnis:** `C:\home\repos\livesplit-coop`, Branch `feature/lobbies`. Build wie in Plan 2/3. Tests: `powershell -NoProfile -STA -ExecutionPolicy Bypass -File scripts/test-recorder.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"`. Niemals Dateien unter `C:\home\Speedrunning\LS RVtheer` verändern.

---

## File Structure

- Create `component/RunFiles.cs` – `Load`, `Save` (atomar), `FromSnapshot`, `SameSegments`.
- Create `component/ViewerLibrary.cs` – `PathFor`, `PendingPath`, `HostLock`.
- Create `component/AttemptRecorder.cs` – `Observe`, Versuchsende, Host-PB-Übernahme, `Note`.
- Modify `component/Texts.cs` – Recorder-Texte.
- Modify `component/CoopWindow.cs` – `ICoopSession.Note` + Anzeige.
- Modify `component/Component.cs` – Recorder (Viewer), Sperre (Host), `Note`.
- Modify `tests/LobbyTests.cs` – `FakeSession.Note`.
- Create `tests/RecorderTests.cs`, `scripts/test-recorder.ps1`.

---

### Task 1: `RunFiles` + Testrunner

**Files:** Create `component/RunFiles.cs`, `tests/RecorderTests.cs`, `scripts/test-recorder.ps1`

- [ ] **Step 1: Runner** (`scripts/test-recorder.ps1`)

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
Add-Type -Path (Join-Path $root 'tests\RecorderTests.cs') -ReferencedAssemblies $refs
$work=Join-Path $root '.local\recorder-tests'
if(Test-Path -LiteralPath $work){Remove-Item -LiteralPath $work -Recurse -Force}
[void][IO.Directory]::CreateDirectory($work)
[RecorderTests]::Run($work)
Write-Output 'ALL RECORDER TESTS PASSED'
```

- [ ] **Step 2: Failing Test** (`tests/RecorderTests.cs`)

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveSplit.Coop;
using LiveSplit.Model;

public static class RecorderTests {
    static int passed;
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);passed++;Console.WriteLine("PASS: "+text);}
    const long S=10000000L; // ticks per second
    // A host snapshot: three segments; splits/pb/best in seconds (null = none).
    public static Snapshot Snap(string attempt,string phase,int index,long?[] splits,long?[] pb,long?[] best,long? clock) {
        var names=new[]{"CP1","CP2","Finish"};
        var s=new Snapshot { seq=1,runId="run1",attemptId=attempt,game="Test Game",category="Any%",phase=phase,index=index,timingMethod="RealTime",
            comparison="Personal Best",attempts=5,offsetTicks=0,realTicks=clock.HasValue?(long?)(clock.Value*S):null,gameTicks=null,
            runVariables=new Dictionary<string,string>{{"Number of Players","Duo"}},
            segments=Enumerable.Range(0,3).Select(i=>new SegmentData { name=names[i],splitRT=splits[i].HasValue?(long?)(splits[i].Value*S):null,
                pbRT=pb[i].HasValue?(long?)(pb[i].Value*S):null,bestRT=best[i].HasValue?(long?)(best[i].Value*S):null,comparisons=new Dictionary<string,Pair>() }).ToArray() };
        return s;
    }
    static readonly long?[] None={null,null,null};
    public static void Run(string work) {
        Texts.German=false;
        FileTests(work);
        Console.WriteLine(passed+" checks passed");
    }
    static void FileTests(string work) {
        var host=Snap("a1","NotRunning",-1,None,new long?[]{60,130,200},new long?[]{55,65,60},null);
        var run=RunFiles.FromSnapshot(host);
        Check(run.GameName=="Test Game" && run.CategoryName=="Any%" && run.Count==3 && run[2].Name=="Finish","run is built from the host's segments");
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200) && run[0].BestSegmentTime.RealTime==TimeSpan.FromSeconds(55),"host PB and golds are taken over");
        Check(run.Metadata.VariableValueNames["Number of Players"]=="Duo" && run.AttemptCount==0,"variables are copied, no attempts are invented");
        string path=Path.Combine(work,"files","Any%.lss");
        RunFiles.Save(run,path);
        Check(File.Exists(path) && !File.Exists(path+".tmp"),"save creates folders and leaves no temp file");
        var loaded=RunFiles.Load(path);
        Check(loaded.Count==3 && loaded[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200) && loaded.FilePath==path,"saved splits load back");
        Check(RunFiles.SameSegments(loaded,host),"same segment names match");
        host.segments[1].name="Other";
        Check(!RunFiles.SameSegments(loaded,host),"different segment names do not match");
        RunFiles.Save(loaded,path);
        Check(RunFiles.Load(path).Count==3,"saving over an existing file works");
    }
}
```

- [ ] **Step 3: Bauen + Test, muss scheitern** (`RunFiles` fehlt).

Run: `cd /c/home/repos/livesplit-coop && powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host" && powershell -NoProfile -STA -ExecutionPolicy Bypass -File scripts/test-recorder.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"`

- [ ] **Step 4: `component/RunFiles.cs`**

```csharp
using System;
using System.IO;
using System.Linq;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Model.RunFactories;
using LiveSplit.Model.RunSavers;

namespace LiveSplit.Coop {
    // Loading and saving personal split files with LiveSplit's own reader and writer.
    public static class RunFiles {
        public static IRun Load(string path) {
            using(var stream=File.OpenRead(path)) {
                var run=new StandardFormatsRunFactory(stream,path).Create(new StandardComparisonGeneratorsFactory());
                run.FilePath=path;
                return run;
            }
        }
        // A complete copy is written first and then swapped in, so a crash never truncates someone's splits.
        public static void Save(IRun run,string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp=path+".tmp";
            using(var stream=File.Create(temp)) new XMLRunSaver().Save(run,stream);
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
            run.FilePath=path;run.HasChanged=false;
        }
        static Time Time(long? real,long? game) { return new Time(real.HasValue?(TimeSpan?)TimeSpan.FromTicks(real.Value):null,game.HasValue?(TimeSpan?)TimeSpan.FromTicks(game.Value):null); }
        // New splits for a viewer who never ran this profile: the host's segments, PB and golds, no attempts.
        public static IRun FromSnapshot(Snapshot s) {
            var run=new Run(new StandardComparisonGeneratorsFactory());
            run.GameName=s.game;run.CategoryName=s.category;
            if(s.runVariables!=null) foreach(var pair in s.runVariables) run.Metadata.VariableValueNames[pair.Key]=pair.Value;
            foreach(var data in s.segments) {
                var segment=new Segment(data.name);
                segment.PersonalBestSplitTime=Time(data.pbRT,data.pbGT);
                segment.BestSegmentTime=Time(data.bestRT,data.bestGT);
                run.Add(segment);
            }
            run.HasChanged=true;
            return run;
        }
        public static bool SameSegments(IRun run,Snapshot s) {
            return run.Count==s.segments.Length && run.Select(seg=>seg.Name).SequenceEqual(s.segments.Select(seg=>seg.name));
        }
    }
}
```

- [ ] **Step 5: Bauen + Test** → alle PASS.

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/RunFiles.cs tests/RecorderTests.cs scripts/test-recorder.ps1
git commit -m "Component: load, save and create split files for viewers

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `ViewerLibrary` + `HostLock`

**Files:** Create `component/ViewerLibrary.cs`; Test `tests/RecorderTests.cs`

- [ ] **Step 1: Failing Test** – `LibraryTests(work);` in `Run` und ergänzen:

```csharp
    static void LibraryTests(string work) {
        string fallback=Path.Combine(work,"lib","Splits","Coop");
        var plain=new ViewerLibrary(null,fallback);
        var s=Snap("a1","NotRunning",-1,None,None,None,null);
        Check(plain.PathFor(s)==Path.Combine(fallback,"Test Game","Any% (Duo).lss"),"without a pack splits go to Splits/Coop/<game>/<category> (<variables>).lss: "+plain.PathFor(s));
        s.game="Bad:Name?";s.category="A/B";
        Check(plain.PathFor(s)==Path.Combine(fallback,"Bad_Name_","A_B (Duo).lss"),"invalid file name characters are replaced");
        s.runVariables=null;
        Check(plain.PathFor(s)==Path.Combine(fallback,"Bad_Name_","A_B.lss"),"runs without variables have no suffix");
        string target=Path.Combine(work,"lib","x","Duo.lss");
        string pending=plain.PendingPath(target);
        Check(Path.GetDirectoryName(pending)==Path.Combine(fallback,"Pending") && Path.GetFileName(pending).StartsWith("Duo-") && pending.EndsWith(".lss"),"pending copies live under Splits/Coop/Pending");
        Check(plain.PendingPath(target)==pending && plain.PendingPath(Path.Combine(work,"lib","y","Duo.lss"))!=pending,"pending names are stable per target and distinct across targets");
        // Pack lookup: written by Plan 3's ProfilePack.Match.
        Directory.CreateDirectory(Path.Combine(work,"lib","pack"));
        File.WriteAllText(Path.Combine(work,"lib","pack","coop-profiles.json"),@"{""version"":1,""game"":""Test Game"",""dimensions"":[{""id"":""p"",""label"":{""en"":""P"",""de"":""P""},""options"":[{""id"":""duo"",""label"":""Duo""}]}],
            ""profiles"":[{""select"":{""p"":""duo""},""splits"":""Splits/Duo.lss"",""layout"":""Layouts/Host.lsl"",""category"":""Any%"",""variables"":{""Number of Players"":""Duo""}}]}");
        var pack=ProfilePack.Load(Path.Combine(work,"lib","pack","coop-profiles.json"));
        var withPack=new ViewerLibrary(pack,fallback);
        Check(withPack.PathFor(Snap("a1","NotRunning",-1,None,None,None,null))==pack.FullPath("Splits/Duo.lss"),"a matching pack profile is used");
        // Host lock
        string splits=Path.Combine(work,"lib","locked.lss");File.WriteAllText(splits,"x");
        Check(!HostLock.IsHeld(splits),"no lock file means not held");
        using(HostLock.Hold(splits)) {
            Check(HostLock.IsHeld(splits),"a held lock is seen");
            Check((File.GetAttributes(HostLock.PathFor(splits)) & FileAttributes.Hidden)!=0,"lock files are hidden");
        }
        Check(!File.Exists(HostLock.PathFor(splits)) && !HostLock.IsHeld(splits),"releasing removes the lock");
        File.WriteAllText(HostLock.PathFor(splits),"999999");
        Check(!HostLock.IsHeld(splits),"a lock from a process that no longer exists is ignored");
        File.WriteAllText(HostLock.PathFor(splits),"garbage");
        Check(!HostLock.IsHeld(splits),"an unreadable lock is ignored");
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern.**

- [ ] **Step 3: `component/ViewerLibrary.cs`**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LiveSplit.Coop {
    // Where a viewer keeps the splits of a host's profile: the matching pack profile, else Splits/Coop/<game>/<category> (<variables>).lss.
    public sealed class ViewerLibrary {
        readonly ProfilePack pack;
        readonly string fallbackRoot;
        public ViewerLibrary(ProfilePack pack,string fallbackRoot) { this.pack=pack;this.fallbackRoot=fallbackRoot; }
        public string PathFor(Snapshot s) {
            var profile=pack==null?null:pack.Match(s.game,s.category,s.runVariables);
            if(profile!=null) return pack.FullPath(profile.splits);
            string name=s.category;
            if(s.runVariables!=null && s.runVariables.Count>0)
                name+=" ("+string.Join(", ",s.runVariables.OrderBy(kv=>kv.Key,StringComparer.Ordinal).Select(kv=>kv.Value.Trim()).ToArray())+")";
            return Path.Combine(fallbackRoot,Safe(s.game),Safe(name)+".lss");
        }
        // A copy for attempts that cannot go into the target (a local host has it open, or the segments differ).
        public string PendingPath(string target) {
            string hash;
            using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(target).ToLowerInvariant()))).Replace("-","").Substring(0,8).ToLowerInvariant();
            return Path.Combine(fallbackRoot,"Pending",Safe(Path.GetFileNameWithoutExtension(target))+"-"+hash+".lss");
        }
        static string Safe(string text) {
            var invalid=Path.GetInvalidFileNameChars();
            string safe=new string((text ?? "").Select(c=>invalid.Contains(c)?'_':c).ToArray()).Trim();
            if(safe.Length>80) safe=safe.Substring(0,80);
            return safe.Length==0?"_":safe;
        }
    }
    // A hidden <splits>.coop-lock file with the process id while a host copy has these splits loaded.
    public static class HostLock {
        public static string PathFor(string splits) { return splits+".coop-lock"; }
        sealed class Held : IDisposable {
            readonly string path,owner;
            public Held(string path,string owner) { this.path=path;this.owner=owner; }
            public void Dispose() {
                try { if(File.Exists(path) && File.ReadAllText(path).Trim()==owner) { File.SetAttributes(path,FileAttributes.Normal);File.Delete(path); } }
                catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
        }
        public static IDisposable Hold(string splits) {
            string path=PathFor(splits),owner=Process.GetCurrentProcess().Id.ToString();
            if(File.Exists(path)) File.SetAttributes(path,FileAttributes.Normal);
            File.WriteAllText(path,owner);
            File.SetAttributes(path,FileAttributes.Hidden);
            return new Held(path,owner);
        }
        public static bool IsHeld(string splits) {
            try {
                int pid;
                if(!File.Exists(PathFor(splits)) || !int.TryParse(File.ReadAllText(PathFor(splits)).Trim(),out pid)) return false;
                using(var process=Process.GetProcessById(pid)) return !process.HasExited;
            } catch(ArgumentException) { return false; }
            catch(IOException) { return false; }
            catch(UnauthorizedAccessException) { return true; }
        }
    }
}
```

- [ ] **Step 4: Bauen + Test** → alle PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/ViewerLibrary.cs tests/RecorderTests.cs
git commit -m "Component: viewer split locations and host lock files

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `AttemptRecorder`

**Files:** Create `component/AttemptRecorder.cs`; Modify `component/Texts.cs`; Test `tests/RecorderTests.cs`

- [ ] **Step 1: Failing Test** – `RecorderTestsMain(work);` in `Run` und ergänzen:

```csharp
    static void RecorderTestsMain(string work) {
        string fallback=Path.Combine(work,"rec","Splits","Coop");
        var clock=new DateTime(2026,10,12,12,0,0,DateTimeKind.Utc);
        var recorder=new AttemptRecorder(new ViewerLibrary(null,fallback),s=>false,()=>clock);
        long?[] pb={60,130,200},best={55,65,60};
        string target=new ViewerLibrary(null,fallback).PathFor(Snap("x","NotRunning",-1,None,pb,best,null));
        // First contact creates the file from the host's PB.
        recorder.Observe(Snap("a0","NotRunning",-1,None,pb,best,null),1);
        Check(File.Exists(target) && RunFiles.Load(target)[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200),"first contact creates splits with the host PB");
        Check(recorder.Note==Texts.T("recorder.created",Path.GetFileName(target)),"the viewer is told: "+recorder.Note);
        // A completed attempt with a new PB and a gold on CP2 (segment 120-58=62 < 65).
        recorder.Observe(Snap("a1","Running",0,None,pb,best,5),1);
        recorder.Observe(Snap("a1","Running",1,new long?[]{58,null,null},pb,best,70),1);
        recorder.Observe(Snap("a1","Running",2,new long?[]{58,120,null},pb,best,150),1);
        clock=clock.AddSeconds(190);
        recorder.Observe(Snap("a1","Ended",3,new long?[]{58,120,190},pb,best,190),1);
        Check(RunFiles.Load(target).AttemptCount==0,"nothing is written before the host resets");
        recorder.Observe(Snap("a2","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),1);
        var run=RunFiles.Load(target);
        Check(run.AttemptCount==1 && run.AttemptHistory.Count==1,"the completed attempt is counted");
        Check(run.AttemptHistory[0].Time.RealTime==TimeSpan.FromSeconds(190),"attempt history has the final time");
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(190) && run[1].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(120),"new PB is recorded");
        Check(run[1].BestSegmentTime.RealTime==TimeSpan.FromSeconds(62),"new gold is recorded");
        Check(run[0].SegmentHistory.Count==1,"segment history grows");
        Check(recorder.Note==Texts.T("recorder.saved",Path.GetFileName(target)),"saving is reported");
        // A reset attempt: counted, no PB change.
        recorder.Observe(Snap("a3","Running",1,new long?[]{70,null,null},new long?[]{58,120,190},new long?[]{55,62,60},90),1);
        recorder.Observe(Snap("a4","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),1);
        run=RunFiles.Load(target);
        Check(run.AttemptCount==2 && run.AttemptHistory.Count==2 && !run.AttemptHistory[1].Time.RealTime.HasValue,"a reset attempt is counted without a time");
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(190),"a reset keeps the PB");
        // A reconnect in the middle of an attempt drops it.
        recorder.Observe(Snap("a5","Running",1,new long?[]{60,null,null},None,None,80),1);
        recorder.Observe(Snap("a6","NotRunning",-1,None,None,None,null),2);
        Check(RunFiles.Load(target).AttemptCount==2,"attempts interrupted by a reconnect are not recorded");
        // Joining mid-attempt and seeing it end still records it.
        recorder.Observe(Snap("a7","Running",2,new long?[]{60,125,null},new long?[]{58,120,190},new long?[]{55,62,60},150),2);
        recorder.Observe(Snap("a7","Ended",3,new long?[]{60,125,195},new long?[]{58,120,190},new long?[]{55,62,60},195),2);
        recorder.Observe(Snap("a8","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),2);
        Check(RunFiles.Load(target).AttemptCount==3,"an attempt seen to its end is recorded");
        // Host lock: attempts go to Pending.
        var locked=new AttemptRecorder(new ViewerLibrary(null,fallback),s=>true,()=>clock);
        locked.Observe(Snap("b1","Running",0,None,new long?[]{58,120,190},new long?[]{55,62,60},5),1);
        locked.Observe(Snap("b2","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),1);
        string pending=new ViewerLibrary(null,fallback).PendingPath(target);
        Check(File.Exists(pending) && RunFiles.Load(pending).AttemptCount==1 && RunFiles.Load(target).AttemptCount==3,"a locked target sends the attempt to Pending");
        Check(locked.Note==Texts.T("recorder.pending",pending),"pending is reported");
    }
    static void AdoptionTests(string work) {
        string fallback=Path.Combine(work,"adopt","Splits","Coop");
        var library=new ViewerLibrary(null,fallback);
        var mine=RunFiles.FromSnapshot(Snap("x","NotRunning",-1,None,new long?[]{70,140,210},new long?[]{50,70,70},null));
        string target=library.PathFor(Snap("x","NotRunning",-1,None,None,None,null));
        RunFiles.Save(mine,target);
        var recorder=new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow);
        recorder.Observe(Snap("h0","NotRunning",-1,None,new long?[]{60,130,200},new long?[]{55,65,60},null),1);
        var run=RunFiles.Load(target);
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200) && run[0].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(60),"a faster host PB replaces the local PB");
        Check(run[0].BestSegmentTime.RealTime==TimeSpan.FromSeconds(50) && run[1].BestSegmentTime.RealTime==TimeSpan.FromSeconds(65) && run[2].BestSegmentTime.RealTime==TimeSpan.FromSeconds(60),"only faster host golds are taken");
        Check(recorder.Note==Texts.T("recorder.adoptedPb",Path.GetFileName(target)),"adoption is reported");
        var slower=new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow);
        slower.Observe(Snap("h1","NotRunning",-1,None,new long?[]{70,150,230},new long?[]{60,80,80},null),1);
        Check(RunFiles.Load(target)[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200),"a slower host PB is ignored");
        var other=Snap("h2","NotRunning",-1,None,new long?[]{1,2,3},new long?[]{1,1,1},null);other.segments[0].name="Different";
        new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow).Observe(other,1);
        Check(RunFiles.Load(target)[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200),"different segments never overwrite local splits");
    }
```

Und in `Run` nach `RecorderTestsMain(work);` auch `AdoptionTests(work);` aufrufen.

- [ ] **Step 2: Bauen + Test, muss scheitern.**

- [ ] **Step 3: Texte in `component/Texts.cs`** (in `All` einfügen)

```csharp
            {"recorder.created",new[]{"Splits created: {0}.","Splits angelegt: {0}."}},
            {"recorder.saved",new[]{"Attempt saved to {0}.","Versuch gespeichert in {0}."}},
            {"recorder.pending",new[]{"A host timer on this PC has these splits open; the attempt was saved to {0}.","Ein Host-Timer auf diesem PC hat diese Splits offen; Versuch gespeichert in {0}."}},
            {"recorder.adoptedPb",new[]{"Host PB taken over into {0}.","Host-PB übernommen in {0}."}},
            {"recorder.mismatch",new[]{"Your splits {0} have different segments; the attempt was saved to {1}.","Deine Splits {0} haben andere Segmente; Versuch gespeichert in {1}."}},
            {"recorder.failed",new[]{"Could not save the attempt: {0}","Versuch konnte nicht gespeichert werden: {0}"}},
```

- [ ] **Step 4: `component/AttemptRecorder.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveSplit.Model;
using LiveSplit.Options;
using LiveSplit.UI;

namespace LiveSplit.Coop {
    // Records the host's attempts into the viewer's own splits. An attempt counts once the viewer sees it end
    // (finished, then reset; or reset while running) on one continuous connection.
    public sealed class AttemptRecorder {
        readonly ViewerLibrary library;
        readonly Func<string,bool> lockedByHost;
        readonly Func<DateTime> now;
        readonly HashSet<string> contacted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Snapshot current;
        DateTime started;
        int generation=int.MinValue;
        public string Note { get; private set; }
        public AttemptRecorder(ViewerLibrary library) : this(library,HostLock.IsHeld,()=>DateTime.UtcNow) { }
        public AttemptRecorder(ViewerLibrary library,Func<string,bool> lockedByHost,Func<DateTime> now) { this.library=library;this.lockedByHost=lockedByHost;this.now=now; }
        public void Observe(Snapshot s,int connectionGeneration) {
            if(connectionGeneration!=generation) { generation=connectionGeneration;current=null; }
            FirstContact(s);
            if(current!=null && (s.runId!=current.runId || s.attemptId!=current.attemptId || s.phase=="NotRunning")) { Finish(current);current=null; }
            if(s.phase!="NotRunning") {
                if(current==null) started=now()-TimeSpan.FromTicks(s.realTicks ?? 0);
                current=s;
            }
        }
        static Time Time(long? real,long? game) { return new Time(real.HasValue?(TimeSpan?)TimeSpan.FromTicks(real.Value):null,game.HasValue?(TimeSpan?)TimeSpan.FromTicks(game.Value):null); }
        static TimeSpan? Pick(Time time,TimingMethod method) { return method==TimingMethod.GameTime?time.GameTime:time.RealTime; }
        // Once per splits file and session: create missing splits from the host, or take over a faster host PB and faster golds.
        void FirstContact(Snapshot s) {
            string target=library.PathFor(s);
            if(!contacted.Add(target) || lockedByHost(target)) return;
            if(!File.Exists(target)) { RunFiles.Save(RunFiles.FromSnapshot(s),target);Note=Texts.T("recorder.created",Path.GetFileName(target));return; }
            var run=RunFiles.Load(target);
            if(!RunFiles.SameSegments(run,s)) return;
            var method=(TimingMethod)Enum.Parse(typeof(TimingMethod),s.timingMethod);
            var last=s.segments[s.segments.Length-1];
            TimeSpan? hostPb=Pick(Time(last.pbRT,last.pbGT),method),localPb=Pick(run[run.Count-1].PersonalBestSplitTime,method);
            bool changed=false;
            if(hostPb.HasValue && (!localPb.HasValue || hostPb.Value<localPb.Value)) {
                for(int i=0;i<run.Count;i++) run[i].PersonalBestSplitTime=Time(s.segments[i].pbRT,s.segments[i].pbGT);
                changed=true;
            }
            for(int i=0;i<run.Count;i++) {
                TimeSpan? host=Pick(Time(s.segments[i].bestRT,s.segments[i].bestGT),method),mine=Pick(run[i].BestSegmentTime,method);
                if(host.HasValue && (!mine.HasValue || host.Value<mine.Value)) { run[i].BestSegmentTime=Time(s.segments[i].bestRT,s.segments[i].bestGT);changed=true; }
            }
            if(changed) { RunFiles.Save(run,target);Note=Texts.T("recorder.adoptedPb",Path.GetFileName(target));CoopDiagnostics.Write("viewer_host_pb_adopted",new { segments=run.Count }); }
        }
        void Finish(Snapshot last) {
            string target=library.PathFor(last),path=target;
            bool pending=lockedByHost(target);
            if(pending) path=library.PendingPath(target);
            string note=pending?Texts.T("recorder.pending",path):Texts.T("recorder.saved",Path.GetFileName(path));
            IRun run=File.Exists(path)?RunFiles.Load(path):RunFiles.FromSnapshot(last);
            if(!RunFiles.SameSegments(run,last)) {
                path=library.PendingPath(target);pending=true;
                run=File.Exists(path)?RunFiles.Load(path):RunFiles.FromSnapshot(last);
                if(!RunFiles.SameSegments(run,last)) { Note=Texts.T("recorder.failed",Path.GetFileName(target));return; }
                note=Texts.T("recorder.mismatch",Path.GetFileName(target),path);
            }
            Record(run,last,started,now());
            RunFiles.Save(run,path);
            Note=note;
            CoopDiagnostics.Write("viewer_attempt_recorded",new { completed=last.phase=="Ended",pending=pending });
        }
        // Replays the attempt's outcome on a shadow timer and lets LiveSplit's TimerModel update history, golds and PB.
        static void Record(IRun run,Snapshot last,DateTime started,DateTime ended) {
            var state=new LiveSplitState(run,null,new Layout(),new LayoutSettings(),new Settings());
            state.CurrentTimingMethod=(TimingMethod)Enum.Parse(typeof(TimingMethod),last.timingMethod);
            run.AttemptCount++;
            state.AttemptStarted=new AtomicDateTime(started,false);
            state.AttemptEnded=new AtomicDateTime(ended,false);
            for(int i=0;i<run.Count;i++) run[i].SplitTime=Time(last.segments[i].splitRT,last.segments[i].splitGT);
            long elapsed=last.realTicks ?? 0;
            state.AdjustedStartTime=state.StartTimeWithOffset=TimeStamp.Now-TimeSpan.FromTicks(elapsed);
            if(last.phase=="Ended") { state.CurrentPhase=TimerPhase.Ended;state.CurrentSplitIndex=run.Count; }
            else { state.CurrentPhase=TimerPhase.Running;state.CurrentSplitIndex=Math.Max(0,last.index); }
            new TimerModel { CurrentState=state }.Reset(true);
        }
    }
}
```

- [ ] **Step 5: Bauen + Test** → alle PASS. Scheitert „attempt history has the final time“ oder „new gold is recorded“, prüfen, ob `TimerModel.Reset(true)` in dieser LiveSplit-Version zusätzlich `state.Run.HasChanged`/`CurrentComparison` braucht; Ergebnis berichten statt raten (NEEDS_CONTEXT).

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/AttemptRecorder.cs component/Texts.cs tests/RecorderTests.cs
git commit -m "Component: record followed host attempts into the viewer's own splits

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Verdrahtung in Komponente und Fenster

**Files:** Modify `component/Component.cs`, `component/CoopWindow.cs`, `tests/LobbyTests.cs`

- [ ] **Step 1: `ICoopSession.Note`**

In `component/CoopWindow.cs` dem Interface hinzufügen:

```csharp
        string Note { get; }            // last split-saving note for viewers, or null
```

Im Fenster ein Label direkt unter `status` (Feld + `body.Controls.Add(note)` nach `body.Controls.Add(status)`):

```csharp
        readonly Label note=new Label { Name="note",AutoSize=true,MaximumSize=new Size(480,0),ForeColor=Color.DimGray };
```

und in `Sync()` ergänzen: `note.Text=session.Note ?? "";note.Visible=note.Text.Length>0;`

In `tests/LobbyTests.cs` `FakeSession` ergänzen: `public string note;public string Note { get { return note; } }` und in `WindowTests` (Viewer-Block, nach dem erfolgreichen Join) prüfen:

```csharp
            viewer.note="Attempt saved to Duo.lss.";Pump(()=>Find(w,"note").Text=="Attempt saved to Duo.lss.");
            Check(true,"the window shows the recorder note");
```

- [ ] **Step 2: Komponente**

Felder:

```csharp
        AttemptRecorder recorder;
        IDisposable hostLock;
        string lockedPath;
```

`ICoopSession`-Property: `public string Note { get { return recorder==null?null:recorder.Note; } }`

In `Connect()` im Viewer-Zweig nach `mirror=new Mirror(state);`:

```csharp
                    string splitsRoot=Pack!=null?Pack.FullPath("Splits/Coop"):Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Splits","Coop");
                    recorder=new AttemptRecorder(new ViewerLibrary(Pack,splitsRoot));
```

(`using System.IO;` ergänzen.) Der Recorder bleibt nach `Disconnect` bestehen (die Notiz soll sichtbar bleiben); bei jedem neuen `Connect` als Viewer wird er neu erzeugt.

Im Viewer-Zweig von `Update`, direkt nach dem `if(delivery.structural) ... else mirror.Advance(...)`-Block:

```csharp
                            try { recorder.Observe(delivery.snapshot,connection.Generation); }
                            catch(Exception e) { CoopDiagnostics.Error("viewer_recorder",e); }
```

Host-Sperre: am Anfang von `Update` (nach dem Auto-Connect-Block):

```csharp
            string loaded=Role=="host"?state.Run.FilePath:null;
            if(!string.Equals(loaded,lockedPath,StringComparison.OrdinalIgnoreCase)) {
                if(hostLock!=null) { hostLock.Dispose();hostLock=null; }
                lockedPath=loaded;
                if(!string.IsNullOrEmpty(loaded) && File.Exists(loaded)) {
                    try { hostLock=HostLock.Hold(loaded); } catch(Exception e) { CoopDiagnostics.Error("host_lock",e); }
                }
            }
```

In `Dispose()` vor `settings.Dispose();`: `if(hostLock!=null) { hostLock.Dispose();hostLock=null; }`

- [ ] **Step 3: Bauen + alle drei Testläufe** (`test-lobby.ps1`, `test-profile.ps1`, `test-recorder.ps1`) → alle `ALL … PASSED`.

- [ ] **Step 4: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add -A component tests
git commit -m "Component: viewers record attempts, hosts lock their open splits

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Abnahme (Controller)

In der nativen Kopie aus Plan 3 Task 6: Host + Viewer, zwei Versuche (einer zu Ende, einer resettet). Danach Viewer-Datei öffnen: 2 Versuche, PB = Host-Endzeit; Notiz im Fenster sichtbar. Host und Viewer auf demselben PC mit derselben Pack-Datei → Notiz „Pending“.
