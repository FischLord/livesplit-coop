// Test-only component, installed exclusively into disposable LiveSplit copies.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Coop;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;
[assembly: ComponentFactory(typeof(CoopUiTest.Factory))]
namespace CoopUiTest {
    public sealed class Factory : IComponentFactory {
        public string ComponentName { get { return "Coop UI Test Driver"; } }
        public string Description { get { return "Local test fixture only"; } }
        public ComponentCategory Category { get { return ComponentCategory.Control; } }
        public string UpdateName { get { return ComponentName; } }
        public string XMLURL { get { return ""; } }
        public string UpdateURL { get { return ""; } }
        public Version Version { get { return new Version(1,0); } }
        public IComponent Create(LiveSplitState s) { return new Driver(s); }
    }
    public sealed class Command { public int id;public string action;public double seconds;public string name; }
    public sealed class Driver : IComponent {
        readonly LiveSplitState state;
        readonly TimerModel timer;
        readonly string folder=AppDomain.CurrentDomain.BaseDirectory;
        readonly Stopwatch clock=Stopwatch.StartNew();
        int lastId;
        long lastPoll,lastReport;
        bool positioned;
        string error="";
        public Driver(LiveSplitState s) { state=s;timer=new TimerModel{CurrentState=s}; }
        CoopComponent Coop { get { return state.Layout.Components.OfType<CoopComponent>().FirstOrDefault(); } }
        object Private(string name) { return typeof(CoopComponent).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(Coop); }
        public void Update(IInvalidator invalidator,LiveSplitState s,float width,float height,LayoutMode mode) {
            if(clock.ElapsedMilliseconds-lastPoll<60 || Coop==null)return;
            lastPoll=clock.ElapsedMilliseconds;
            try {
                if(!positioned && clock.ElapsedMilliseconds>1000) {
                    int number=int.Parse(File.ReadAllText(Path.Combine(folder,"window-index.txt")));
                    var bounds=Screen.PrimaryScreen.WorkingArea;
                    state.Form.Location=new Point(bounds.Left+30+number*385,bounds.Top+40);
                    state.Form.Text="Coop Test "+(number==0?"HOST":"VIEWER "+number);
                    positioned=true;
                }
                string path=Path.Combine(folder,"ui-command.json");
                if(File.Exists(path)) {
                    var cmd=RelayConnection.Json().Deserialize<Command>(File.ReadAllText(path));
                    if(cmd!=null && cmd.id>lastId) { lastId=cmd.id;Execute(cmd); }
                }
                if(clock.ElapsedMilliseconds-lastReport>=150) { Report();lastReport=clock.ElapsedMilliseconds; }
            } catch(Exception e) { error=e.GetType().Name+": "+e.Message;try{Report();}catch{} }
        }
        void Execute(Command c) {
            error="";
            if(c.seconds>0) state.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(c.seconds);
            switch(c.action) {
                case "connect":Coop.Connect();break;
                case "disconnect":Coop.Disconnect();break;
                case "start":timer.Start();break;
                case "split":timer.Split();break;
                case "skip":timer.SkipSplit();break;
                case "undo":timer.UndoSplit();break;
                case "pause":timer.Pause();break;
                case "reset":timer.Reset(false);break;
                case "network-drop":
                    var connection=Private("connection");
                    var socket=typeof(RelayConnection).GetField("socket",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(connection) as System.Net.WebSockets.ClientWebSocket;
                    if(socket!=null) socket.Abort();break;
                case "capture":
                    var b=state.Form.Bounds;
                    using(var image=new Bitmap(b.Width,b.Height)) using(var g=Graphics.FromImage(image)) {
                        g.CopyFromScreen(b.Location,Point.Empty,b.Size);
                        image.Save(Path.Combine(folder,Path.GetFileName(c.name)+".png"),System.Drawing.Imaging.ImageFormat.Png);
                    }break;
            }
        }
        void Report() {
            var snapshot=Snapshot.Capture(state,"ui-run","ui-attempt",lastId);
            var message=RelayConnection.Json().Serialize(new { commandId=lastId,pid=Process.GetCurrentProcess().Id,
                label=(string)Private("label"),error=error,runFile=state.Run.FilePath,snapshot=snapshot,
                formWidth=state.Form.Width,formHeight=state.Form.Height,layoutHeight=state.Layout.VerticalHeight,
                components=state.Layout.Components.Select(c=>new{name=c.ComponentName,height=c.VerticalHeight}).ToArray() });
            string path=Path.Combine(folder,"ui-state.json"),temp=path+".tmp";
            File.WriteAllText(temp,message);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        public string ComponentName { get { return "Coop UI Test Driver"; } }
        public float VerticalHeight { get { return 0; } }public float HorizontalWidth { get { return 0; } }
        public float MinimumWidth { get { return 0; } }public float MinimumHeight { get { return 0; } }
        public float PaddingTop { get { return 0; } }public float PaddingBottom { get { return 0; } }
        public float PaddingLeft { get { return 0; } }public float PaddingRight { get { return 0; } }
        public IDictionary<string,Action> ContextMenuControls { get { return null; } }
        public void DrawVertical(Graphics g,LiveSplitState s,float w,Region r) {}public void DrawHorizontal(Graphics g,LiveSplitState s,float h,Region r) {}
        public Control GetSettingsControl(LayoutMode m) { return new Label { Text="Automated local test only" }; }
        public XmlNode GetSettings(XmlDocument d) { return d.CreateElement("Settings"); }public void SetSettings(XmlNode n) {}
        public void Dispose() {}
    }
}
