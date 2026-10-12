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
        File.WriteAllText(Path.Combine(root,"Splits/A/G.lss"),Splits.Replace("CATEGORY","Map A Any Percent (old spelling)"));
        File.WriteAllText(Path.Combine(root,"Splits/B/NMG.lss"),Splits.Replace("CATEGORY","Map B Finish"));
        File.WriteAllText(Path.Combine(root,"Autosplitter/test.asl"),"state(\"x\"){}");
        return root;
    }
    public static void Run(string work) {
        Texts.German=false;
        System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.ThrowException);
        PackTests(work);
        PreparerTests(work);
        ComponentTests(work);
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
        Check(run.SelectSingleNode("/Run/CategoryName").InnerText=="Map A Any%","the category name follows the pack (speedrun.com spelling)");
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
    static LiveSplitState NewState(){var run=new Run(new StandardComparisonGeneratorsFactory());run.Add(new Segment("Finish"));return new LiveSplitState(run,null,new LiveSplit.UI.Layout(),new LiveSplit.Options.LayoutSettings(),new LiveSplit.Options.Settings());}
    static void ComponentTests(string work) {
        string root=MakePack(work,"component");
        var pack=ProfilePack.Load(Path.Combine(root,"coop-profiles.json"));
        var state=NewState();state.Run.GameName="Test Game";state.Run.CategoryName="Map B Finish";state.Run.FilePath=pack.FullPath("Splits/B/NMG.lss");
        state.Run.Metadata.VariableValueNames["Number of Players"]="Solo";
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
            Check(component.ProfileText=="Solo / Finish Only","the window gets the profile label");
        }
        var other=NewState();other.Run.Metadata.VariableValueNames["Number of Players"]="Trio";
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
}
