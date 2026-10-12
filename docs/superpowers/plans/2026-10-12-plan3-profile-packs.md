# Plan 3: Profil-Pakete und Profilwechsel im Timer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ein optionales Daten-Paket (`coop-profiles.json`) beschreibt Profile eines Spiels; der Host wählt im Coop-Fenster ein Profil und LiveSplit lädt Splits und Layout ohne Neustart. Die Komponente enthält kein Spielwissen mehr.

**Architecture:** `ProfilePack` (Laden/Validieren/Auflösen), `ProfilePreparer` (Layout + LSS vorbereiten, Zeiten unangetastet), `ProfileSwitcher` (Reflection auf LiveSplits `TimerForm`, Rückfall auf Neustart), `ProfileSection` (WinForms-Bereich im Coop-Fenster). Der Snapshot bekommt das optionale Feld `profileLabel`; `RunProfile.cs` (RV-Pfadlogik) entfällt.

**Tech Stack:** C# 5 (csc v4.0.30319: kein `?.`, kein `$""`, kein `nameof`, keine Expression-Bodied Members, kein `out var`), .NET 4.8, WinForms, `System.Xml`, `System.Web.Extensions`, PowerShell 5.1 `Add-Type`-Tests.

Spec: `docs/superpowers/specs/2026-10-12-lobbies-design.md`, Abschnitte 1, 3.3, 3.4. Voraussetzung: Plan 2 ist umgesetzt (`Texts`, `CoopWindow`, `ICoopSession`, `LocalState.lastProfile`, `CoopComponent(state,localPath)`).

**Arbeitsverzeichnis:** `C:\home\repos\livesplit-coop`, Branch `feature/lobbies`. Build: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"`. Tests: `powershell -NoProfile -STA -ExecutionPolicy Bypass -File scripts/test-profile.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"`. Niemals Dateien unter `C:\home\Speedrunning\LS RVtheer` verändern.

---

## File Structure

- Create `component/ProfilePack.cs` – Datenmodell + `Find`, `Load`, `Validate`, `FullPath`, `Key`, `ByKey`, `Resolve`, `ForSplits`, `Match`.
- Create `component/ProfilePreparer.cs` – `Prepare`, `RememberInSettings`.
- Create `component/ProfileSwitcher.cs` – `Switch` mit injizierbarem Neustart.
- Create `component/ProfileSection.cs` – Auswahl-UI.
- Modify `component/Texts.cs` – Profil-Texte.
- Modify `component/Snapshot.cs` – `profileLabel`, `runVariables` direkt aus den Metadaten.
- Modify `component/Component.cs` – Paket, Badge, Profil im Fenster, `LoadProfile`.
- Delete `component/RunProfile.cs`; Modify `tests/Regression.cs` (RunProfile-Prüfungen entfernen).
- Create `tests/ProfileTests.cs`, `scripts/test-profile.ps1`.

---

### Task 1: `ProfilePack` + Testrunner

**Files:**
- Create: `component/ProfilePack.cs`, `tests/ProfileTests.cs`, `scripts/test-profile.ps1`

- [ ] **Step 1: Runner** (`scripts/test-profile.ps1`)

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
Add-Type -Path (Join-Path $root 'tests\ProfileTests.cs') -ReferencedAssemblies $refs
$work=Join-Path $root '.local\profile-tests'
if(Test-Path -LiteralPath $work){Remove-Item -LiteralPath $work -Recurse -Force}
[void][IO.Directory]::CreateDirectory($work)
[ProfileTests]::Run($work)
Write-Output 'ALL PROFILE TESTS PASSED'
```

- [ ] **Step 2: Failing Test** (`tests/ProfileTests.cs`)

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Coop;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

public static class ProfileTests {
    static int passed;
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);passed++;Console.WriteLine("PASS: "+text);}
    static void Throws<T>(Action action,string text) where T:Exception {try{action();}catch(T){Check(true,text);return;}Check(false,text);}
    public const string PackJson=@"{
  ""version"": 1, ""game"": ""Test Game"", ""autosplitter"": ""Autosplitter/test.asl"", ""viewerLayout"": ""Layouts/Viewer.lsl"",
  ""autosplitterSettings"": { ""finalSplit"": ""True"" },
  ""dimensions"": [
    { ""id"": ""map"", ""label"": { ""en"": ""Map"", ""de"": ""Karte"" }, ""options"": [ { ""id"": ""a"", ""label"": ""Map A"" }, { ""id"": ""b"", ""label"": ""Map B"" } ] },
    { ""id"": ""rules"", ""label"": { ""en"": ""Rules"", ""de"": ""Regeln"" }, ""options"": [ { ""id"": ""nmg"", ""label"": ""No Major Glitches"" }, { ""id"": ""g"", ""label"": ""Glitched"" } ] }
  ],
  ""profiles"": [
    { ""select"": { ""map"": ""a"", ""rules"": ""nmg"" }, ""splits"": ""Splits/A/NMG.lss"", ""layout"": ""Layouts/Host-A.lsl"", ""label"": ""Duo / Fullgame"", ""category"": ""Map A Any%"", ""variables"": { ""Number of Players"": ""Duo"", ""Glitch Type"": ""No Major Glitches"" } },
    { ""select"": { ""map"": ""a"", ""rules"": ""g"" }, ""splits"": ""Splits/A/G.lss"", ""layout"": ""Layouts/Host-A.lsl"", ""label"": ""Duo / Fullgame"", ""category"": ""Map A Any%"", ""variables"": { ""Number of Players"": ""Duo"", ""Glitch Type"": ""Glitched "" } },
    { ""select"": { ""map"": ""b"", ""rules"": ""nmg"" }, ""splits"": ""Splits/B/NMG.lss"", ""layout"": ""Layouts/Host-B.lsl"", ""label"": ""Solo / Finish Only"", ""category"": ""Map B Finish"", ""variables"": { ""Number of Players"": ""Solo"", ""Glitch Type"": ""No Major Glitches"" } }
  ]
}";
    public const string Layout=@"<?xml version=""1.0"" encoding=""UTF-8""?><Layout version=""1.6.1""><Mode>Vertical</Mode><X>0</X><Y>0</Y><VerticalWidth>300</VerticalWidth><VerticalHeight>200</VerticalHeight><HorizontalWidth>-1</HorizontalWidth><HorizontalHeight>-1</HorizontalHeight><Settings /><Components>
<Component><Path>LiveSplit.Coop.dll</Path><Settings><Version>2</Version><Role>viewer</Role></Settings></Component>
<Component><Path>LiveSplit.ScriptableAutoSplit.dll</Path><Settings><Version>1.5</Version><ScriptPath>C:\old\path.asl</ScriptPath><Start>False</Start><Reset>True</Reset><Split>False</Split><CustomSettings><Setting id=""finalSplit"" type=""bool"">False</Setting></CustomSettings></Settings></Component>
</Components></Layout>";
    public const string Splits=@"<?xml version=""1.0"" encoding=""UTF-8""?><Run version=""1.7.0""><GameIcon /><GameName>Test Game</GameName><CategoryName>CATEGORY</CategoryName><LayoutPath>C:\old\layout.lsl</LayoutPath><Metadata><Run id="""" /><Platform usesEmulator=""False"">PC</Platform><Region /><Variables /><CustomVariables /></Metadata><Offset>00:00:00</Offset><AttemptCount>3</AttemptCount><AttemptHistory /><Segments><Segment><Name>Finish</Name><Icon /><SplitTimes><SplitTime name=""Personal Best""><RealTime>00:01:00.5000000</RealTime></SplitTime></SplitTimes><BestSegmentTime><RealTime>00:01:00.5000000</RealTime></BestSegmentTime><SegmentHistory /></Segment></Segments><AutoSplitterSettings /></Run>";
    // A temporary pack folder: <work>/<name>/coop-profiles.json, layouts, splits, autosplitter, and a LiveSplit-like app dir two levels down.
    public static string MakePack(string work,string name) {
        string root=Path.Combine(work,name);
        foreach(var dir in new[]{"Layouts","Splits/A","Splits/B","Autosplitter","Anwendung/Host"}) Directory.CreateDirectory(Path.Combine(root,dir));
        File.WriteAllText(Path.Combine(root,"coop-profiles.json"),PackJson);
        File.WriteAllText(Path.Combine(root,"Layouts/Host-A.lsl"),Layout);File.WriteAllText(Path.Combine(root,"Layouts/Host-B.lsl"),Layout);File.WriteAllText(Path.Combine(root,"Layouts/Viewer.lsl"),Layout);
        File.WriteAllText(Path.Combine(root,"Splits/A/NMG.lss"),Splits.Replace("CATEGORY","Map A Any%"));
        File.WriteAllText(Path.Combine(root,"Splits/A/G.lss"),Splits.Replace("CATEGORY","Map A Any%"));
        File.WriteAllText(Path.Combine(root,"Splits/B/NMG.lss"),Splits.Replace("CATEGORY","Map B Finish"));
        File.WriteAllText(Path.Combine(root,"Autosplitter/test.asl"),"state(\"x\"){}");
        return root;
    }
    public static void Run(string work) {
        Texts.German=false;
        PackTests(work);
        Console.WriteLine(passed+" checks passed");
    }
    static void PackTests(string work) {
        string root=MakePack(work,"pack");
        Check(ProfilePack.Find(Path.Combine(root,"Anwendung/Host")).Root==Path.GetFullPath(root),"pack is found two folders above the app");
        Check(ProfilePack.Find(Path.Combine(work,"nothing-here"))==null || true,"missing folders are tolerated");
        Directory.CreateDirectory(Path.Combine(work,"empty/a/b/c/d"));
        Check(ProfilePack.Find(Path.Combine(work,"empty/a/b/c/d"))==null,"no pack within four levels gives null");
        var pack=ProfilePack.Load(Path.Combine(root,"coop-profiles.json"));
        Check(pack.game=="Test Game" && pack.dimensions.Count==2 && pack.profiles.Count==3,"pack loads");
        Check(pack.FullPath("Splits/A/G.lss")==Path.GetFullPath(Path.Combine(root,"Splits","A","G.lss")),"relative paths resolve against the pack folder");
        var g=pack.Resolve(new Dictionary<string,string>{{"map","a"},{"rules","g"}});
        Check(g!=null && g.splits=="Splits/A/G.lss","selection resolves to a profile");
        Check(pack.Resolve(new Dictionary<string,string>{{"map","b"},{"rules","g"}})==null,"missing combination resolves to null");
        Check(pack.Key(g)=="a|g" && pack.ByKey("a|g")==g && pack.ByKey("zz")==null,"profile keys follow the dimension order");
        Check(pack.ForSplits(Path.Combine(root,"splits","a","g.LSS"))==g,"splits path lookup ignores case");
        Check(pack.ForSplits(null)==null && pack.ForSplits(Path.Combine(work,"x.lss"))==null,"unknown splits give no profile");
        Check(pack.Match("Test Game","Map A Any%",new Dictionary<string,string>{{"Number of Players","Duo"},{"Glitch Type","Glitched"}})==g,"match tolerates trimmed variable values");
        Check(pack.Match("Other","Map A Any%",new Dictionary<string,string>{{"Number of Players","Duo"},{"Glitch Type","Glitched "}})==null,"match requires the game");
        Check(pack.dimensions[0].Label=="Map","dimension label follows the language");
        foreach(var bad in new[]{
            PackJson.Replace("\"version\": 1","\"version\": 2"),
            PackJson.Replace("\"splits\": \"Splits/A/NMG.lss\"","\"splits\": \"../outside.lss\""),
            PackJson.Replace("\"splits\": \"Splits/A/NMG.lss\"","\"splits\": \"C:/abs.lss\""),
            PackJson.Replace("{ \"map\": \"b\", \"rules\": \"nmg\" }","{ \"map\": \"b\" }"),
            PackJson.Replace("{ \"map\": \"b\", \"rules\": \"nmg\" }","{ \"map\": \"a\", \"rules\": \"nmg\" }"),
            PackJson.Replace("{ \"map\": \"b\", \"rules\": \"nmg\" }","{ \"map\": \"c\", \"rules\": \"nmg\" }"),
            PackJson.Replace("\"label\": \"Solo / Finish Only\"","\"label\": \""+new string('x',61)+"\"") }) {
            string path=Path.Combine(work,"bad.json");File.WriteAllText(path,bad);
            Throws<InvalidDataException>(()=>ProfilePack.Load(path),"invalid pack is rejected");
        }
    }
}
```

- [ ] **Step 3: Bauen + Test, muss scheitern** (Compile-Fehler `ProfilePack`).

Run: `cd /c/home/repos/livesplit-coop && powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host" && powershell -NoProfile -STA -ExecutionPolicy Bypass -File scripts/test-profile.ps1 -LiveSplitPath "C:\home\Speedrunning\LS RVtheer\Timer\Anwendung\Host"`

- [ ] **Step 4: `component/ProfilePack.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace LiveSplit.Coop {
    public sealed class PackOption { public string id,label; }
    public sealed class PackDimension {
        public string id;
        public Dictionary<string,string> label;
        public List<PackOption> options;
        public string Label { get { string value;return label!=null && label.TryGetValue(Texts.German?"de":"en",out value)?value:id; } }
    }
    public sealed class PackProfile {
        public Dictionary<string,string> select,variables;
        public string splits,layout,label,category;
    }
    // A game's profiles (coop-profiles.json): which splits and layout belong to which selection. Paths are relative to the file.
    public sealed class ProfilePack {
        public const string FileName="coop-profiles.json";
        public int version;
        public string game,autosplitter,viewerLayout;
        public Dictionary<string,string> autosplitterSettings;
        public List<PackDimension> dimensions;
        public List<PackProfile> profiles;
        string root;
        public string Root { get { return root; } }
        // Looks next to the LiveSplit copy and up to three folders above it.
        public static ProfilePack Find(string startDirectory) {
            var dir=new DirectoryInfo(startDirectory);
            for(int i=0;i<4 && dir!=null;i++,dir=dir.Parent) {
                string path=Path.Combine(dir.FullName,FileName);
                if(File.Exists(path)) return Load(path);
            }
            return null;
        }
        public static ProfilePack Load(string path) {
            ProfilePack pack;
            try { pack=new JavaScriptSerializer().Deserialize<ProfilePack>(File.ReadAllText(path)); }
            catch(ArgumentException e) { throw new InvalidDataException("coop-profiles.json is not valid JSON: "+e.Message); }
            catch(InvalidOperationException e) { throw new InvalidDataException("coop-profiles.json has an unexpected shape: "+e.Message); }
            if(pack==null) throw new InvalidDataException("coop-profiles.json is empty.");
            pack.root=Path.GetDirectoryName(Path.GetFullPath(path));
            pack.Validate();
            return pack;
        }
        static void Require(bool ok,string message) { if(!ok) throw new InvalidDataException("coop-profiles.json: "+message); }
        static bool Relative(string path) { return !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && !path.Replace('\\','/').Split('/').Contains(".."); }
        void Validate() {
            Require(version==1,"version must be 1");
            Require(!string.IsNullOrEmpty(game),"game is missing");
            Require(autosplitter==null || Relative(autosplitter),"autosplitter must be a relative path");
            Require(viewerLayout==null || Relative(viewerLayout),"viewerLayout must be a relative path");
            Require(dimensions!=null && dimensions.Count>0,"dimensions are missing");
            Require(dimensions.Select(d=>d.id).Distinct().Count()==dimensions.Count,"dimension ids must be unique");
            foreach(var d in dimensions) Require(!string.IsNullOrEmpty(d.id) && d.options!=null && d.options.Count>0 && d.options.All(o=>!string.IsNullOrEmpty(o.id) && !string.IsNullOrEmpty(o.label)),"dimension "+d.id+" needs options with id and label");
            Require(profiles!=null && profiles.Count>0,"profiles are missing");
            var keys=new HashSet<string>();
            foreach(var p in profiles) {
                Require(p.select!=null && p.select.Count==dimensions.Count && dimensions.All(d=>p.select.ContainsKey(d.id) && d.options.Any(o=>o.id==p.select[d.id])),"every profile must select one valid option per dimension");
                Require(keys.Add(Key(p)),"two profiles share the selection "+Key(p));
                Require(Relative(p.splits) && Relative(p.layout),"profile paths must be relative inside the pack");
                Require(p.label==null || p.label.Length<=60,"profile labels are limited to 60 characters");
                if(p.variables==null) p.variables=new Dictionary<string,string>();
            }
        }
        public string FullPath(string relative) { return Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))); }
        public string Key(PackProfile profile) { return string.Join("|",dimensions.Select(d=>profile.select[d.id]).ToArray()); }
        public PackProfile ByKey(string key) { return key==null?null:profiles.FirstOrDefault(p=>Key(p)==key); }
        public PackProfile Resolve(IDictionary<string,string> selection) {
            return profiles.FirstOrDefault(p=>dimensions.All(d=>{ string value;return selection.TryGetValue(d.id,out value) && p.select[d.id]==value; }));
        }
        public PackProfile ForSplits(string path) {
            if(string.IsNullOrEmpty(path)) return null;
            string full=Path.GetFullPath(path);
            return profiles.FirstOrDefault(p=>string.Equals(FullPath(p.splits),full,StringComparison.OrdinalIgnoreCase));
        }
        // The profile a host's run belongs to (game, category and every profile variable; values compared trimmed).
        public PackProfile Match(string gameName,string categoryName,IDictionary<string,string> values) {
            if(gameName!=game || values==null) return null;
            return profiles.FirstOrDefault(p=>p.category==categoryName && p.variables.All(kv=>{ string value;return values.TryGetValue(kv.Key,out value) && value!=null && value.Trim()==kv.Value.Trim(); }));
        }
    }
}
```

- [ ] **Step 5: Bauen + Test** → alle PASS.

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/ProfilePack.cs tests/ProfileTests.cs scripts/test-profile.ps1
git commit -m "Component: profile packs (coop-profiles.json) with validation and lookup

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: `ProfilePreparer`

**Files:**
- Create: `component/ProfilePreparer.cs`
- Test: `tests/ProfileTests.cs`

- [ ] **Step 1: Failing Test** – in `Run` nach `PackTests(work);` `PreparerTests(work);` und ergänzen:

```csharp
    static void PreparerTests(string work) {
        string root=MakePack(work,"prep");
        var pack=ProfilePack.Load(Path.Combine(root,"coop-profiles.json"));
        var g=pack.ByKey("a|g");
        ProfilePreparer.Prepare(pack,g,"host");
        var layout=new XmlDocument();layout.Load(pack.FullPath(g.layout));
        Check(layout.SelectSingleNode("//Component[Path='LiveSplit.Coop.dll']/Settings/Role").InnerText=="host","layout coop role is set");
        var asl=layout.SelectSingleNode("//Component[Path='LiveSplit.ScriptableAutoSplit.dll']/Settings");
        Check(asl["ScriptPath"].InnerText==pack.FullPath("Autosplitter/test.asl"),"autosplitter path points into the pack");
        Check(asl["Start"].InnerText=="True" && asl["Split"].InnerText=="True" && asl["Reset"].InnerText=="True","start and split are enabled, reset untouched");
        Check(asl.SelectSingleNode("CustomSettings/Setting[@id='finalSplit']").InnerText=="True","pack autosplitter settings are applied");
        string splitsPath=pack.FullPath(g.splits);
        var run=new XmlDocument();run.Load(splitsPath);
        Check(run.SelectSingleNode("/Run/LayoutPath").InnerText==pack.FullPath(g.layout),"splits point to the profile layout");
        var glitch=run.SelectSingleNode("/Run/Metadata/Variables/Variable[@name='Glitch Type']");
        Check(glitch!=null && glitch.FirstChild is XmlCDataSection && glitch.InnerText=="Glitched ","values with outer spaces are written as CDATA");
        Check(run.SelectSingleNode("/Run/Metadata/Variables/Variable[@name='Number of Players']").InnerText=="Duo","plain variables are written");
        Check(run.SelectSingleNode("/Run/AttemptCount").InnerText=="3" && run.SelectSingleNode("//SplitTime[@name='Personal Best']/RealTime").InnerText=="00:01:00.5000000","times and attempts are untouched");
        // LiveSplit's own parser keeps the trailing space, which speedrun.com's Glitched label needs.
        using(var stream=File.OpenRead(splitsPath)) {
            var parsed=new LiveSplit.Model.RunFactories.StandardFormatsRunFactory(stream,splitsPath).Create(new StandardComparisonGeneratorsFactory());
            Check(parsed.Metadata.VariableValueNames["Glitch Type"]=="Glitched ","LiveSplit reads the Glitched label with its trailing space");
            Check(parsed.AttemptCount==3,"LiveSplit reads the prepared splits");
        }
        ProfilePreparer.Prepare(pack,g,"host");
        var again=new XmlDocument();again.Load(splitsPath);
        Check(again.SelectNodes("/Run/Metadata/Variables/Variable[@name='Glitch Type']").Count==1,"preparing twice does not duplicate variables");
        Check(!File.Exists(splitsPath+".tmp") && !File.Exists(pack.FullPath(g.layout)+".tmp"),"no temporary files remain");
        File.Delete(pack.FullPath("Autosplitter/test.asl"));
        Throws<FileNotFoundException>(()=>ProfilePreparer.Prepare(pack,g,"host"),"a missing autosplitter is reported");
        // settings.cfg for the first start outside LiveSplit.
        string cfg=Path.Combine(root,"Anwendung/Host/settings.cfg");
        File.WriteAllText(cfg,"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Settings><RecentSplits><SplitsFile gameName=\"x\" categoryName=\"y\">"+splitsPath+"</SplitsFile></RecentSplits><ActiveAutoSplitters><AutoSplitterGame>RV</AutoSplitterGame></ActiveAutoSplitters></Settings>");
        ProfilePreparer.RememberInSettings(cfg,splitsPath,"Test Game","Map A Any%");
        var settings=new XmlDocument();settings.Load(cfg);
        var entries=settings.SelectNodes("/Settings/RecentSplits/SplitsFile");
        Check(entries.Count==1 && entries[0].InnerText==splitsPath && entries[0].Attributes["categoryName"].Value=="Map A Any%" && entries[0].Attributes["lastTimingMethod"].Value=="RealTime","the profile becomes the most recent splits once");
        Check(!settings.SelectSingleNode("/Settings/ActiveAutoSplitters").HasChildNodes,"registered autosplitters are deactivated (the layout owns the autosplitter)");
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern.**

- [ ] **Step 3: `component/ProfilePreparer.cs`**

```csharp
using System;
using System.IO;
using System.Xml;

namespace LiveSplit.Coop {
    // Makes a profile's layout and splits ready to load. Never changes times, attempts or history.
    public static class ProfilePreparer {
        public static void Prepare(ProfilePack pack,PackProfile profile,string role) {
            string layoutPath=pack.FullPath(profile.layout),splitsPath=pack.FullPath(profile.splits);
            var layout=new XmlDocument();layout.Load(layoutPath);
            var coop=layout.SelectSingleNode("/Layout/Components/Component[Path='LiveSplit.Coop.dll']/Settings");
            if(coop==null) throw new InvalidDataException("The layout has no Coop component: "+layoutPath);
            SetChild(coop,"Role",role);
            if(pack.autosplitter!=null) {
                var scripts=layout.SelectNodes("/Layout/Components/Component[Path='LiveSplit.ScriptableAutoSplit.dll']/Settings");
                if(scripts.Count!=1) throw new InvalidDataException("The layout needs exactly one Scriptable Auto Splitter: "+layoutPath);
                string asl=pack.FullPath(pack.autosplitter);
                if(!File.Exists(asl)) throw new FileNotFoundException("The autosplitter is missing. Unpack the complete package.",asl);
                var script=scripts[0];
                SetChild(script,"ScriptPath",asl);SetChild(script,"Start","True");SetChild(script,"Split","True");
                if(pack.autosplitterSettings!=null) foreach(var setting in pack.autosplitterSettings) {
                    foreach(XmlNode node in script.SelectNodes("CustomSettings/Setting")) if(node.Attributes["id"]!=null && node.Attributes["id"].Value==setting.Key) node.InnerText=setting.Value;
                }
            }
            Save(layout,layoutPath);
            var run=new XmlDocument();run.Load(splitsPath);
            var variables=run.SelectSingleNode("/Run/Metadata/Variables");
            if(variables==null) {
                var metadata=run.SelectSingleNode("/Run/Metadata");
                if(metadata!=null) variables=metadata.AppendChild(run.CreateElement("Variables"));
            }
            if(variables!=null) foreach(var pair in profile.variables) {
                XmlElement variable=null;
                foreach(XmlNode node in variables.SelectNodes("Variable")) if(node.Attributes["name"]!=null && node.Attributes["name"].Value==pair.Key) variable=(XmlElement)node;
                if(variable==null) { variable=run.CreateElement("Variable");variable.SetAttribute("name",pair.Key);variables.AppendChild(variable); }
                variable.InnerText="";
                // LiveSplit trims plain XML text; CDATA keeps labels such as speedrun.com's "Glitched " intact.
                if(pair.Value!=pair.Value.Trim()) variable.AppendChild(run.CreateCDataSection(pair.Value)); else variable.InnerText=pair.Value;
            }
            SetChild(run.DocumentElement,"LayoutPath",layoutPath);
            Save(run,splitsPath);
        }
        // For starts outside LiveSplit: the profile becomes the most recent splits; registered autosplitters are turned off
        // because the layout owns the autosplitter (Edit Splits > Activate would add a second one).
        public static void RememberInSettings(string settingsPath,string splitsPath,string gameName,string categoryName) {
            var config=new XmlDocument();config.Load(settingsPath);
            var active=config.SelectSingleNode("/Settings/ActiveAutoSplitters");
            if(active!=null) active.RemoveAll();
            var recent=config.SelectSingleNode("/Settings/RecentSplits");
            if(recent==null) recent=config.DocumentElement.AppendChild(config.CreateElement("RecentSplits"));
            foreach(XmlNode old in recent.SelectNodes("SplitsFile")) if(string.Equals(old.InnerText,splitsPath,StringComparison.OrdinalIgnoreCase)) recent.RemoveChild(old);
            var entry=config.CreateElement("SplitsFile");entry.InnerText=splitsPath;
            entry.SetAttribute("gameName",gameName);entry.SetAttribute("categoryName",categoryName);
            entry.SetAttribute("lastTimingMethod","RealTime");entry.SetAttribute("lastHotkeyProfile","Default");
            recent.AppendChild(entry);
            Save(config,settingsPath);
        }
        static void SetChild(XmlNode parent,string name,string value) {
            var node=parent.SelectSingleNode(name);
            if(node==null) node=parent.AppendChild(parent.OwnerDocument.CreateElement(name));
            node.InnerText=value;
        }
        // Personal split files: write a complete copy first, then swap it in.
        static void Save(XmlDocument doc,string path) {
            string temp=path+".tmp";
            doc.Save(temp);
            File.Replace(temp,path,null);
        }
    }
}
```

- [ ] **Step 4: Bauen + Test** → alle PASS. Falls „LiveSplit reads the Glitched label with its trailing space“ scheitert: NEEDS_CONTEXT melden (dann ist die CDATA-Annahme falsch und die Spec muss angepasst werden).

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/ProfilePreparer.cs tests/ProfileTests.cs
git commit -m "Component: prepare profile layouts and splits without touching times

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: `ProfileSwitcher`

**Files:**
- Create: `component/ProfileSwitcher.cs`
- Test: `tests/ProfileTests.cs`

- [ ] **Step 1: Failing Test** – `SwitcherTests(work);` in `Run` und ergänzen:

```csharp
    // Stands in for LiveSplit's TimerForm: same private method names and signatures.
    public sealed class FakeForm {
        public readonly List<string> calls=new List<string>();public bool allowSave=true;public LiveSplitState state;
        bool WarnUserAboutSplitsSave(){calls.Add("warn");return allowSave;}
        void LoadRunFromFile(string path,TimingMethod? method,string hotkeys){calls.Add("run:"+path+":"+method+":"+hotkeys);state.Run.FilePath=path;}
        public void OpenLayoutFromFile(string path){calls.Add("layout:"+path);state.Layout.FilePath=path;}
    }
    public sealed class OldForm { public readonly List<string> calls=new List<string>(); }
    static LiveSplitState NewState(){var run=new Run(new StandardComparisonGeneratorsFactory());run.Add(new Segment("Finish"));return new LiveSplitState(run,null,new LiveSplit.UI.Layout(),new LiveSplit.Options.LayoutSettings(),new LiveSplit.Options.Settings());}
    static void SwitcherTests(string work) {
        string root=MakePack(work,"switch");
        var pack=ProfilePack.Load(Path.Combine(root,"coop-profiles.json"));
        var a=pack.ByKey("a|nmg");var b=pack.ByKey("b|nmg");
        var state=NewState();var form=new FakeForm{state=state};int commits=0;
        var result=ProfileSwitcher.Switch(state,form,pack,a,()=>commits++,(s,l)=>{throw new Exception("no restart expected");});
        Check(result==ProfileSwitcher.Result.Switched && commits==1,"switch succeeds and commits once");
        Check(form.calls.SequenceEqual(new[]{"warn","run:"+pack.FullPath(a.splits)+":RealTime:Default","layout:"+pack.FullPath(a.layout)}),"warn, then splits, then layout: "+string.Join(" / ",form.calls.ToArray()));
        var prepared=new XmlDocument();prepared.Load(pack.FullPath(a.splits));
        Check(prepared.SelectSingleNode("/Run/LayoutPath").InnerText==pack.FullPath(a.layout),"profile is prepared before loading");
        form.calls.Clear();
        ProfileSwitcher.Switch(state,form,pack,pack.ByKey("a|g"),()=>commits++,null);
        Check(form.calls.Count==2 && !form.calls.Any(c=>c.StartsWith("layout:")),"the same layout is not reloaded");
        form.calls.Clear();form.allowSave=false;
        Check(ProfileSwitcher.Switch(state,form,pack,b,()=>commits++,null)==ProfileSwitcher.Result.Cancelled && form.calls.SequenceEqual(new[]{"warn"}) && commits==2,"cancelling the save prompt changes nothing");
        string[] restarted=null;
        Check(ProfileSwitcher.Switch(state,new OldForm(),pack,b,()=>commits++,(s,l)=>{restarted=new[]{s,l};})==ProfileSwitcher.Result.Restarted,"unknown LiveSplit internals fall back to a restart");
        Check(restarted!=null && restarted[0]==pack.FullPath(b.splits) && restarted[1]==pack.FullPath(b.layout) && commits==3,"restart gets the prepared profile and commits");
        var running=NewState();var timer=new TimerModel{CurrentState=running};timer.Start();
        Check(ProfileSwitcher.Switch(running,new FakeForm{state=running},pack,b,()=>commits++,null)==ProfileSwitcher.Result.Running && commits==3,"a running timer blocks switching");
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern.**

- [ ] **Step 3: `component/ProfileSwitcher.cs`**

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using LiveSplit.Model;

namespace LiveSplit.Coop {
    // Loads a profile into the running LiveSplit window through TimerForm's own methods (the same ones its menus use).
    // If LiveSplit's internals differ, LiveSplit is restarted with the profile instead.
    public static class ProfileSwitcher {
        public enum Result { Switched,Cancelled,Restarted,Running }
        const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        public static Result Switch(LiveSplitState state,object form,ProfilePack pack,PackProfile profile,Action commit) {
            return Switch(state,form,pack,profile,commit,Restart);
        }
        // commit runs once the user agreed and before anything loads; the coop component itself is replaced when the layout changes.
        public static Result Switch(LiveSplitState state,object form,ProfilePack pack,PackProfile profile,Action commit,Action<string,string> restart) {
            if(state.CurrentPhase!=TimerPhase.NotRunning) return Result.Running;
            var warn=Find(form,"WarnUserAboutSplitsSave",Type.EmptyTypes);
            var loadRun=Find(form,"LoadRunFromFile",new[] { typeof(string),typeof(TimingMethod?),typeof(string) });
            var openLayout=Find(form,"OpenLayoutFromFile",new[] { typeof(string) });
            if(warn!=null && !(bool)Call(warn,form)) return Result.Cancelled;
            ProfilePreparer.Prepare(pack,profile,"host");
            string splits=pack.FullPath(profile.splits),layout=pack.FullPath(profile.layout);
            if(commit!=null) commit();
            if(warn==null || loadRun==null || openLayout==null) {
                CoopDiagnostics.Write("profile_restart",new { reason="timer_form_methods_missing" });
                restart(splits,layout);return Result.Restarted;
            }
            Call(loadRun,form,splits,(TimingMethod?)state.CurrentTimingMethod,state.CurrentHotkeyProfile);
            string current=state.Layout==null?null:state.Layout.FilePath;
            if(string.IsNullOrEmpty(current) || !string.Equals(Path.GetFullPath(current),layout,StringComparison.OrdinalIgnoreCase)) Call(openLayout,form,layout);
            return Result.Switched;
        }
        static MethodInfo Find(object form,string name,Type[] parameters) { return form==null?null:form.GetType().GetMethod(name,Any,null,parameters,null); }
        static object Call(MethodInfo method,object target,params object[] args) {
            try { return method.Invoke(target,args); }
            catch(TargetInvocationException e) { throw e.InnerException ?? e; }
        }
        static void Restart(string splits,string layout) {
            string exe=Process.GetCurrentProcess().MainModule.FileName;
            Process.Start(new ProcessStartInfo(exe,"-s \""+splits+"\" -l \""+layout+"\"") { WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false });
            foreach(Form open in Application.OpenForms) { if(open.Owner==null) { open.Close();break; } }
        }
    }
}
```

- [ ] **Step 4: Bauen + Test** → alle PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/ProfileSwitcher.cs tests/ProfileTests.cs
git commit -m "Component: switch profiles inside LiveSplit, restart as fallback

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: `ProfileSection` (UI) + Texte

**Files:**
- Create: `component/ProfileSection.cs`
- Modify: `component/Texts.cs`
- Test: `tests/ProfileTests.cs`

- [ ] **Step 1: Failing Test** – `SectionTests(work);` in `Run` und ergänzen:

```csharp
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static Control Find(Control parent,string name){foreach(Control c in parent.Controls){if(c.Name==name)return c;var child=Find(c,name);if(child!=null)return child;}return null;}
    static void Click(Control c){c.GetType().GetMethod("OnClick",Hidden,null,new[]{typeof(EventArgs)},null).Invoke(c,new object[]{EventArgs.Empty});}
    static void Pick(ComboBox box,string label){box.SelectedIndex=box.Items.Cast<object>().ToList().FindIndex(i=>i.ToString()==label);}
    static void SectionTests(string work) {
        var pack=ProfilePack.Load(Path.Combine(MakePack(work,"section"),"coop-profiles.json"));
        var current=pack.ByKey("a|g");
        using(var form=new Form()) {
            var section=new ProfileSection(pack,current,false,null,false);form.Controls.Add(section);form.Show();Application.DoEvents();
            var map=(ComboBox)Find(section,"dim_map");var rules=(ComboBox)Find(section,"dim_rules");var load=(Button)Find(section,"loadProfile");
            Check(map.SelectedItem.ToString()=="Map A" && rules.SelectedItem.ToString()=="Glitched","current profile is preselected");
            Check(!load.Enabled && Find(section,"profileHint").Text==Texts.T("profile.current"),"the loaded profile cannot be loaded again");
            Pick(map,"Map B");Pick(rules,"Glitched");
            Check(!load.Enabled && Find(section,"profileHint").Text==Texts.T("profile.missing"),"missing combinations are explained");
            Pick(rules,"No Major Glitches");
            Check(load.Enabled && Find(section,"profileHint").Text=="","an existing other profile can be loaded");
            Click(load);
            Check(section.Chosen==pack.ByKey("b|nmg"),"load chooses the selected profile");
        }
        using(var form=new Form()) {
            var section=new ProfileSection(pack,null,false,null,true);form.Controls.Add(section);form.Show();Application.DoEvents();
            Check(!((Button)Find(section,"loadProfile")).Enabled && Find(section,"profileHint").Text==Texts.T("profile.running"),"a running timer blocks loading");
            Check(((ComboBox)Find(section,"dim_map")).SelectedIndex==0,"without a current profile the first options are selected");
        }
        using(var form=new Form()) {
            var section=new ProfileSection(pack,null,true,"Duo / Fullgame",false);form.Controls.Add(section);form.Show();Application.DoEvents();
            Check(Find(section,"dim_map")==null && Find(section,"profileFromHost").Text==Texts.T("profile.fromHost","Duo / Fullgame"),"viewers only see the host's profile");
        }
        foreach(var key in new[]{"profile.load","profile.fromHost","profile.running","profile.missing","profile.current","profile.failed"}) Check(Texts.Has(key),"text exists: "+key);
    }
```

- [ ] **Step 2: Bauen + Test, muss scheitern.**

- [ ] **Step 3: Texte in `component/Texts.cs`** (in das Dictionary `All` einfügen)

```csharp
            {"profile.load",new[]{"Load profile","Profil laden"}},
            {"profile.fromHost",new[]{"Profile comes from the host: {0}","Profil kommt vom Host: {0}"}},
            {"profile.unknown",new[]{"not known yet","noch unbekannt"}},
            {"profile.running",new[]{"Finish or reset the run first.","Erst Run beenden oder resetten."}},
            {"profile.missing",new[]{"There are no splits for this combination.","Für diese Kombination gibt es keine Splits."}},
            {"profile.current",new[]{"This profile is loaded.","Dieses Profil ist geladen."}},
            {"profile.failed",new[]{"Could not load the profile: {0}","Profil konnte nicht geladen werden: {0}"}},
```

- [ ] **Step 4: `component/ProfileSection.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LiveSplit.Coop {
    // Profile part of the coop window: one list per pack dimension and "Load profile". Viewers only see the host's profile.
    public sealed class ProfileSection : FlowLayoutPanel {
        sealed class Choice { public string id,label;public override string ToString() { return label; } }
        readonly ProfilePack pack;
        readonly PackProfile current;
        readonly bool running;
        readonly Dictionary<string,ComboBox> boxes=new Dictionary<string,ComboBox>();
        readonly Button load;
        readonly Label hint=new Label { Name="profileHint",AutoSize=true,MaximumSize=new Size(480,0) };
        public PackProfile Chosen { get; private set; }
        public ProfileSection(ProfilePack pack,PackProfile current,bool viewer,string hostLabel,bool running) {
            this.pack=pack;this.current=current;this.running=running;
            FlowDirection=FlowDirection.TopDown;WrapContents=false;AutoSize=true;Margin=new Padding(0,0,0,4);
            if(viewer) {
                Controls.Add(new Label { Name="profileFromHost",AutoSize=true,MaximumSize=new Size(480,0),Text=Texts.T("profile.fromHost",hostLabel ?? Texts.T("profile.unknown")) });
                return;
            }
            foreach(var dimension in pack.dimensions) {
                var box=new ComboBox { Name="dim_"+dimension.id,DropDownStyle=ComboBoxStyle.DropDownList,Width=230 };
                foreach(var option in dimension.options) box.Items.Add(new Choice { id=option.id,label=option.label });
                string wanted=current!=null?current.select[dimension.id]:null;
                int index=box.Items.Cast<Choice>().ToList().FindIndex(c=>c.id==wanted);
                box.SelectedIndex=index>=0?index:0;
                box.SelectedIndexChanged+=(s,e)=>UpdateState();
                boxes[dimension.id]=box;
                var row=new FlowLayoutPanel { AutoSize=true,WrapContents=false,Margin=new Padding(0) };
                row.Controls.Add(new Label { Text=dimension.Label,AutoSize=false,Width=130,Height=23,TextAlign=ContentAlignment.MiddleLeft });row.Controls.Add(box);
                Controls.Add(row);
            }
            load=new Button { Name="loadProfile",Text=Texts.T("profile.load"),AutoSize=true,MinimumSize=new Size(100,28) };
            load.Click+=(s,e)=>{
                var selected=Selected();
                if(selected==null) return;
                Chosen=selected;
                var form=FindForm();
                if(form!=null && form.Modal) form.DialogResult=DialogResult.OK;
            };
            Controls.Add(load);Controls.Add(hint);
            UpdateState();
        }
        PackProfile Selected() {
            var selection=boxes.ToDictionary(kv=>kv.Key,kv=>kv.Value.SelectedItem==null?null:((Choice)kv.Value.SelectedItem).id);
            return pack.Resolve(selection);
        }
        void UpdateState() {
            var selected=Selected();
            load.Enabled=selected!=null && selected!=current && !running;
            hint.Text=running?Texts.T("profile.running"):selected==null?Texts.T("profile.missing"):selected==current?Texts.T("profile.current"):"";
        }
    }
}
```

- [ ] **Step 5: Bauen + Test** → alle PASS.

- [ ] **Step 6: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add component/ProfileSection.cs component/Texts.cs tests/ProfileTests.cs
git commit -m "Component: profile selection section for the coop window

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Snapshot-Label, Komponente verdrahten, `RunProfile` entfernen

**Files:**
- Modify: `component/Snapshot.cs`, `component/Component.cs`, `tests/Regression.cs`
- Delete: `component/RunProfile.cs`
- Test: `tests/ProfileTests.cs`

- [ ] **Step 1: Failing Test** – `ComponentTests(work);` in `Run` und ergänzen:

```csharp
    static void ComponentTests(string work) {
        string root=MakePack(work,"component");
        var pack=ProfilePack.Load(Path.Combine(root,"coop-profiles.json"));
        var state=NewState();state.Run.FilePath=pack.FullPath("Splits/B/NMG.lss");
        state.Run.Metadata.SetVariableValue("Number of Players","Solo");
        var snap=Snapshot.Capture(state,"run1","attempt1",1);
        Check(snap.runVariables["Number of Players"]=="Solo","snapshot carries the run's own variables");
        snap.profileLabel="Solo / Finish Only";Check(snap.Valid(),"snapshot with a profile label is valid");
        snap.profileLabel=new string('x',61);Check(!snap.Valid(),"profile labels over 60 characters are invalid");
        snap.profileLabel="bad\nlabel";Check(!snap.Valid(),"profile labels with control characters are invalid");
        snap.profileLabel=null;Check(snap.Valid(),"snapshots without a label stay valid");
        string local=Path.Combine(work,"component-local.json");
        using(var component=new CoopComponent(state,local,pack)) {
            component.SetSettings(XmlRole("host"));
            Check(component.ProfileLabel()=="Solo / Finish Only","host badge shows the pack label of the loaded splits");
        }
        var other=NewState();other.Run.Metadata.SetVariableValue("Number of Players","Trio");
        using(var component=new CoopComponent(other,local,null)) {
            component.SetSettings(XmlRole("host"));
            Check(component.ProfileLabel()=="Trio","without a pack the badge falls back to the player count");
        }
        using(var component=new CoopComponent(NewState(),local,null)) {
            component.SetSettings(XmlRole("viewer"));
            Check(component.ProfileLabel()=="","viewers show nothing before the host's first snapshot");
        }
    }
    static XmlNode XmlRole(string role){var doc=new XmlDocument();doc.LoadXml("<Settings><Version>2</Version><Role>"+role+"</Role></Settings>");return doc.DocumentElement;}
```

- [ ] **Step 2: Bauen + Test, muss scheitern** (`CoopComponent(state,local,pack)`, `ProfileLabel`, `profileLabel` fehlen).

- [ ] **Step 3: `component/Snapshot.cs`**

- Feld neben `runVariables`: `public string profileLabel;`
- In `Capture`: `runVariables=RunProfile.Variables(s.Run),` → `runVariables=new Dictionary<string,string>(s.Run.Metadata.VariableValueNames),`
- In `Valid()` als erste Zeile ergänzen: `if(profileLabel!=null && !Text(profileLabel,60)) return false;`

- [ ] **Step 4: `component/Component.cs`**

4a. Felder ergänzen:

```csharp
        ProfilePack pack;
        bool packLoaded;
        string hostLabel;
```

4b. Konstruktoren: Kette über einen privaten Vier-Parameter-Konstruktor, der den bisherigen Rumpf von `CoopComponent(LiveSplitState state,string localPath)` übernimmt:

```csharp
        public CoopComponent(LiveSplitState state) : this(state,LocalState.DefaultPath) { }
        // Production: the pack is looked up next to LiveSplit on first use.
        public CoopComponent(LiveSplitState state,string localPath) : this(state,localPath,null,false) { }
        // Tests: the given pack (or explicitly none) is used and never looked up.
        public CoopComponent(LiveSplitState state,string localPath,ProfilePack pack) : this(state,localPath,pack,true) { }
        CoopComponent(LiveSplitState state,string localPath,ProfilePack pack,bool packKnown) {
            this.pack=pack;packLoaded=packKnown;
            // ... bisheriger Rumpf des (state,localPath)-Konstruktors unverändert ...
        }
```

4c. Paket und Label:

```csharp
        ProfilePack Pack {
            get {
                if(!packLoaded) {
                    packLoaded=true;
                    try { pack=ProfilePack.Find(AppDomain.CurrentDomain.BaseDirectory); }
                    catch(Exception e) { CoopDiagnostics.Error("profile_pack",e); }
                }
                return pack;
            }
        }
        // Right-hand badge: the pack's label for the loaded splits, the host's label for viewers, else the player count.
        public string ProfileLabel() {
            if(Role=="viewer") return hasSnapshot?(hostLabel ?? Players(state.Run)):"";
            var profile=Pack==null?null:Pack.ForSplits(state.Run.FilePath);
            return profile!=null && profile.label!=null?profile.label:Players(state.Run);
        }
        static string Players(IRun run) { string value;return run.Metadata.VariableValueNames.TryGetValue("Number of Players",out value)?value:""; }
```

4d. Host-Veröffentlichung: in `Update` direkt nach `var snapshot=Snapshot.Capture(state,runId,attemptId,++sequence);` einfügen:

```csharp
                            string label=ProfileLabel();snapshot.profileLabel=label.Length>0 && label.Length<=60?label:null;
```

4e. Viewer: in `Update` im Zweig `if(delivery.structural) { mirror.Adopt(delivery.snapshot);...}` ergänzen: `hostLabel=delivery.snapshot.profileLabel;`

4f. `Draw`: `string players=settings.Role=="viewer" && !hasSnapshot?"Players: ?":RunProfile.CategoryLabel(state.Run);` ersetzen durch `string players=ProfileLabel();`

4g. `OpenWindow` ersetzen und `LoadProfile` ergänzen:

```csharp
        void OpenWindow() {
            ProfileSection section=null;
            if(Pack!=null) {
                var current=Pack.ForSplits(state.Run.FilePath) ?? Pack.ByKey(local.lastProfile);
                section=new ProfileSection(Pack,current,Role=="viewer",hostLabel,state.CurrentPhase!=TimerPhase.NotRunning);
            }
            using(var window=new CoopWindow(this,section)) window.ShowDialog(state.Form);
            if(section!=null && section.Chosen!=null) LoadProfile(section.Chosen);
        }
        // After the layout changes, LiveSplit disposes this component; the new one reconnects to the lobby by itself.
        void LoadProfile(PackProfile profile) {
            try {
                var result=ProfileSwitcher.Switch(state,state.Form,Pack,profile,()=>{ local.lastProfile=Pack.Key(profile);SaveLocal(); });
                CoopDiagnostics.Write("profile_switch",new { component=diagnosticId,result=result.ToString() });
            } catch(Exception e) {
                CoopDiagnostics.Error("profile_switch",e);
                MessageBox.Show(state.Form,Texts.T("profile.failed",e.Message),Texts.T("window.title"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
        }
```

- [ ] **Step 5: `RunProfile.cs` löschen, `tests/Regression.cs` anpassen**

`git rm component/RunProfile.cs`. In `tests/Regression.cs` jede Zeile mit `RunProfile.` entfernen; wo sie eine Spielerzahl prüft, durch `viewer.Run.Metadata.VariableValueNames["Number of Players"]==players` bzw. `=="Trio"` ersetzen; die Prüfungen „Missing RV metadata recovered from exact profile path“, „All Checkpoints displayed as Fullgame after transfer“, „Finish Only remains distinct after transfer“ und „Old host supported without retaining stale player count“ samt ihrer Vorbereitungszeilen löschen (das Verhalten liefert jetzt das Profil-Paket). Prüfen: `grep -n RunProfile component tests` liefert nichts.

- [ ] **Step 6: Bauen + Profil- und Lobby-Tests**

Run: Build, dann `scripts/test-profile.ps1` und `scripts/test-lobby.ps1` (beide mit `-LiveSplitPath …`).
Expected: beide `ALL … TESTS PASSED`.

- [ ] **Step 7: Commit**

```bash
cd /c/home/repos/livesplit-coop && git add -A component tests
git commit -m "Component: profile section in the coop window, profile label in snapshots

The game-specific RunProfile recovery is replaced by profile packs.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Abnahme im echten LiveSplit (Controller, kein Subagent)

1. Kopie `.local/native/Timer` aus `C:\home\Speedrunning\LS RVtheer\Timer` (ohne `Anwendung/*/Logs`) anlegen, neue DLL in beide `Components/`, ein Test-`coop-profiles.json` für zwei RV-Profile hineinlegen.
2. Host starten, Coop… → anderes Profil laden → Splits + Layout wechseln ohne Neustart, Lobby verbindet wieder; Viewer sieht neues Label.
3. Profil mit ungespeicherten Zeiten wechseln → LiveSplit fragt; „Abbrechen“ ändert nichts.
4. Screenshots nach `.local/native/`.
