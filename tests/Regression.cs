using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.UI;
using LiveSplit.UI.Components;
using LiveSplit.Coop;

public static class Regression {
    public static void StableSettings() {
        using(var settings=new CoopSettings()) {
            var doc=new XmlDocument();doc.LoadXml("<Settings><Version>2</Version><Role>host</Role></Settings>");
            settings.LoadXml(doc.DocumentElement);
            string a=settings.SaveXml(new XmlDocument()).OuterXml,b=settings.SaveXml(new XmlDocument()).OuterXml;
            Check(a==b,"Coop settings serialize identically");
            Check(!a.Contains("Protected"),"Coop settings contain no keys");
        }
    }
    sealed class Invalidator : IInvalidator {
        public System.Drawing.Drawing2D.Matrix Transform {get;set;}
        public void Invalidate(float x,float y,float width,float height){}
    }
    public static void FullLayout(string app,string layoutPath,string runPath,string output) {
        ComponentManager.BasePath=app;ComponentManager.RaceProviderFactories=new System.Collections.Generic.Dictionary<string,IRaceProviderFactory>();
        IRun run;
        using(var f=File.OpenRead(runPath))run=new LiveSplit.Model.RunFactories.StandardFormatsRunFactory(f,runPath).Create(new StandardComparisonGeneratorsFactory());
        var host=State();host.Run=run;
        foreach(var generator in run.ComparisonGenerators)generator.Generate(host.Settings);
        host.CurrentPhase=TimerPhase.Running;host.CurrentSplitIndex=2;
        for(int i=0;i<2;i++)run[i].SplitTime=new Time(run[i].PersonalBestSplitTime.RealTime+TimeSpan.FromSeconds((i+1)*6),null);
        host.AdjustedStartTime=TimeStamp.Now-run[1].SplitTime.RealTime.Value-TimeSpan.FromSeconds(30);
        using(var form=new System.Windows.Forms.Form()) {
            var viewer=new LiveSplitState(State().Run,form,new Layout(),new LiveSplit.Options.LayoutSettings(),new LiveSplit.Options.Settings());
            viewer.CurrentPhase=TimerPhase.NotRunning;viewer.CurrentSplitIndex=-1;
            var doc=new XmlDocument();doc.Load(layoutPath);
            foreach(XmlNode node in doc.SelectNodes("/Layout/Components/Component[Path='LiveSplit.Coop.dll' or Path='LiveSplit.WorldRecord.dll' or Path='LiveSplit.ScriptableAutoSplit.dll']"))node.ParentNode.RemoveChild(node);
            using(var ms=new MemoryStream()){doc.Save(ms);ms.Position=0;viewer.Layout=new LiveSplit.UI.LayoutFactories.XMLLayoutFactory(ms).Create(viewer);}
            viewer.LayoutSettings=viewer.Layout.Settings;
            using(var mirror=new Mirror(viewer)) {
                mirror.Adopt(Snapshot.Capture(host,"run","attempt",1));
                var renderer=new ComponentRenderer();renderer.VisibleComponents=viewer.Layout.Components.ToArray();
                var inv=new Invalidator{Transform=new System.Drawing.Drawing2D.Matrix()};
                renderer.CalculateOverallSize(LayoutMode.Vertical);
                renderer.Update(inv,viewer,360,renderer.OverallSize,LayoutMode.Vertical);
                var composite=viewer.Layout.Components.OfType<GraphCompositeComponent>().Single();
                var graph=composite.InternalComponent.VisibleComponents.OfType<GraphComponent>().Single();
                Console.WriteLine("Full layout graph: deltas="+graph.Deltas.Count+" final="+graph.FinalSplit+" height="+renderer.OverallSize);
                using(var b=new Bitmap(360,(int)Math.Ceiling(renderer.OverallSize)))using(var g=Graphics.FromImage(b)) {
                    g.Clear(viewer.LayoutSettings.BackgroundColor);
                    renderer.Render(g,viewer,360,b.Height,LayoutMode.Vertical,new Region(new Rectangle(0,0,b.Width,b.Height)));
                    b.Save(output);
                }
                Check(graph.Deltas.Count(d=>d.HasValue)>=2,"Full native viewer layout calculates mirrored graph");
            }
            foreach(var c in viewer.Layout.Components)c.Dispose();
        }
    }
    public static void CompositeGraph(string output) {
        var host=State();host.CurrentPhase=TimerPhase.Running;host.CurrentSplitIndex=2;
        host.Run[0].SplitTime=new Time(TimeSpan.FromSeconds(106),null);
        host.Run[1].SplitTime=new Time(TimeSpan.FromSeconds(212),null);
        host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(245);
        var viewer=State();
        using(var composite=new GraphCompositeComponent(viewer))using(var mirror=new Mirror(viewer)) {
            mirror.Adopt(Snapshot.Capture(host,"run","attempt",1));
            var renderer=new ComponentRenderer();renderer.VisibleComponents=new IComponent[]{composite};
            var inv=new Invalidator {Transform=new System.Drawing.Drawing2D.Matrix()};
            renderer.CalculateOverallSize(LayoutMode.Vertical);
            renderer.Update(inv,viewer,360,renderer.OverallSize,LayoutMode.Vertical);
            var inner=composite.InternalComponent.VisibleComponents.OfType<GraphComponent>().Single();
            Console.WriteLine("Composite: height="+composite.VerticalHeight+" deltas="+inner.Deltas.Count+" final="+inner.FinalSplit);
            using(var b=new Bitmap(360,(int)composite.VerticalHeight))using(var g=Graphics.FromImage(b)) {
                renderer.Render(g,viewer,360,b.Height,LayoutMode.Vertical,new Region(new Rectangle(0,0,b.Width,b.Height)));
                b.Save(output);
            }
            Check(inner.Deltas.Count(d=>d.HasValue)>=2,"Native composite graph receives mirrored checkpoint values");
        }
    }
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message); }
    static LiveSplitState State() {
        var run=new Run(new StandardComparisonGeneratorsFactory());
        run.GameName="RV There Yet?";run.CategoryName="St. Buttin Bay All Checkpoints";
        for(int i=0;i<4;i++) { var seg=new Segment("CP "+(i+1));seg.PersonalBestSplitTime=new Time(TimeSpan.FromSeconds((i+1)*100),null);run.Add(seg); }
        var s=new LiveSplitState(run,null,new Layout(),new LiveSplit.Options.LayoutSettings(),new LiveSplit.Options.Settings());
        s.CurrentTimingMethod=TimingMethod.RealTime;s.CurrentComparison="Personal Best";s.CurrentPhase=TimerPhase.NotRunning;s.CurrentSplitIndex=-1;
        return s;
    }
    public static void Metadata() {
        var host=State();host.Run.Metadata.VariableValueNames["Number of Players"]="Trio";
        var snap=Snapshot.Capture(host,"run","attempt",1);
        var viewer=State();
        using(var mirror=new Mirror(viewer)) {
            mirror.Adopt(snap);
            Check(viewer.Run.Metadata.VariableValueNames.ContainsKey("Number of Players") && viewer.Run.Metadata.VariableValueNames["Number of Players"]=="Trio","Trio survives host/viewer snapshot");
            foreach(string players in new[]{"Solo","Duo","Trio","Squad"}) {
                host.Run.Metadata.VariableValueNames["Number of Players"]=players;
                var next=Snapshot.Capture(host,"run","attempt",2);
                Check(next.StructureKey()!=snap.StructureKey() || players=="Trio","Player change triggers full snapshot: "+players);
                var json=RelayConnection.Json().Serialize(next);
                next=RelayConnection.Json().Deserialize<Snapshot>(json);
                mirror.Adopt(next);
                Check(RunProfile.Players(viewer.Run)==players,"JSON roundtrip and player label: "+players);
            }
            host.Run.FilePath=@"C:\RV\Timer\Splits\St. Buttin Bay\All Checkpoints\No Major Glitches\Trio.lss";
            host.Run.Metadata.VariableValueNames.Clear();
            var recovered=Snapshot.Capture(host,"run","attempt",3);
            mirror.Adopt(recovered);
            Check(RunProfile.Players(viewer.Run)=="Trio","Missing RV metadata recovered from exact profile path");
            Check(RunProfile.CategoryLabel(viewer.Run)=="Trio / Fullgame","All Checkpoints displayed as Fullgame after transfer");
            host.Run.FilePath=@"C:\RV\Timer\Splits\St. Buttin Bay\Finish Only\No Major Glitches\Solo.lss";
            var finish=Snapshot.Capture(host,"finish","attempt",4);mirror.Adopt(finish);
            Check(RunProfile.CategoryLabel(viewer.Run)=="Solo / Finish Only","Finish Only remains distinct after transfer");
            Check(host.Run.Metadata.VariableValueNames.Count==0,"Recovery leaves personal run data unchanged");
            recovered.runVariables=null;mirror.Adopt(recovered);
            Check(RunProfile.Players(viewer.Run)=="Players: ?","Old host supported without retaining stale player count");
            recovered.runVariables=new System.Collections.Generic.Dictionary<string,string>{{"bad","line\nbreak"}};
            Check(!recovered.Valid(),"Malformed metadata rejected");
        }
    }
    public static void Rainbow(string output) {
        var state=State();state.Run.Metadata.VariableValueNames["Number of Players"]="Trio";
        state.LayoutSettings.UseRainbowColor=true;
        using(var component=new GraphCompositeComponent(state)) {
            state.Layout.LayoutComponents.Add(new LayoutComponent("LiveSplit.Graph.dll",component));
            var property=component.GetType().GetProperty("Settings",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public);
            var settings=(GraphSettings)property.GetValue(component,null);
            var before=settings.GraphGoldColor;
            var rainbow=new RainbowGraph();
            Check(rainbow.Update(state),"Rainbow updates the actual installed graph component");
            var first=settings.GraphGoldColor;
            System.Threading.Thread.Sleep(180);
            rainbow.Update(state);
            Check(first!=settings.GraphGoldColor,"Graph gold color changes over time");
            state.LayoutSettings.UseRainbowColor=false;rainbow.Update(state);
            Check(settings.GraphGoldColor==before,"Disabling rainbow restores original graph gold");
        }
        using(var coop=new CoopComponent(state))using(var bitmap=new Bitmap(300,22))using(var g=Graphics.FromImage(bitmap)) {
            var hostSettings=new XmlDocument();hostSettings.LoadXml("<Settings><Role>host</Role></Settings>");coop.SetSettings(hostSettings.DocumentElement);
            g.Clear(Color.FromArgb(11,18,25));
            coop.DrawVertical(g,state,300,new Region(new Rectangle(0,0,300,22)));
            bitmap.Save(output);
        }
    }
    public static void Graph(string layout,string output,bool requireVisible) {
        var s=State();s.CurrentPhase=TimerPhase.Running;s.CurrentSplitIndex=3;
        s.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(325);
        double[] deltas={10,-8,18};
        for(int i=0;i<3;i++) s.Run[i].SplitTime=new Time(TimeSpan.FromSeconds((i+1)*100+deltas[i]),null);
        var doc=new XmlDocument();doc.Load(layout);
        using(var settings=new GraphSettings()) {
            settings.SetSettings(doc.SelectSingleNode("/Layout/Components/Component[Path='LiveSplit.Graph.dll']/Settings"));
            settings.ShowBestSegments=false;
            using(var graph=new GraphComponent(settings)) {
                graph.Update(null,s,360,76,LayoutMode.Vertical);
                Check(graph.Deltas.Count(d=>d.HasValue)>=3,"Installed graph computes completed checkpoint deltas");
                using(var bitmap=new Bitmap(360,76)) using(var g=Graphics.FromImage(bitmap)) {
                    graph.DrawVertical(g,s,360,new Region(new Rectangle(0,0,360,76)));
                    bitmap.Save(output);
                }
                Console.WriteLine("Graph line="+settings.GraphColor);
                if(requireVisible)Check(settings.GraphColor.GetBrightness()>0.6,"Graph line visible on dark background");
            }
        }
    }
}
