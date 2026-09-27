using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Model;
using LiveSplit.UI;
using LiveSplit.UI.Components;

[assembly: ComponentFactory(typeof(LiveSplit.Coop.Factory))]
[assembly: System.Reflection.AssemblyVersion("0.1.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("0.1.0-beta.1")]
[assembly: System.Reflection.AssemblyTitle("LiveSplit Coop")]

namespace LiveSplit.Coop {
    public sealed class Factory : IComponentFactory {
        public string ComponentName { get { return "Coop Relay"; } }
        public string Description { get { return "Share a host timer through your own relay, without a VPN."; } }
        public ComponentCategory Category { get { return ComponentCategory.Control; } }
        public string UpdateName { get { return ComponentName; } }
        public string XMLURL { get { return ""; } }
        public string UpdateURL { get { return ""; } }
        public Version Version { get { return new Version(0,1,0); } }
        public IComponent Create(LiveSplitState state) { return new CoopComponent(state); }
    }
    public sealed class CoopComponent : IComponent {
        readonly LiveSplitState state;
        readonly CoopSettings settings=new CoopSettings();
        readonly Stopwatch watch=Stopwatch.StartNew();
        readonly Font font=new Font("Segoe UI",9);
        RelayConnection connection;
        Mirror mirror;
        IRun publishingRun;
        string runId=Guid.NewGuid().ToString("N"),attemptId=Guid.NewGuid().ToString("N"),role="",label="Coop: disconnected";
        long sequence,lastPublished,lastReceived,lastGuard;
        int publishedGeneration=-1;
        string publishedStructure;
        double frozenElapsed=-1;
        long initialAge;
        bool dirty=true,hasSnapshot;
        Color colour=Color.Gray;
        public CoopComponent(LiveSplitState state) {
            this.state=state;settings.ConnectRequested+=Connect;settings.DisconnectRequested+=Disconnect;
            state.OnStart+=Started;state.OnSplit+=Changed;state.OnUndoSplit+=Changed;state.OnSkipSplit+=Changed;
            state.OnPause+=Changed;state.OnResume+=Changed;state.OnReset+=Reset;
        }
        void Changed(object sender,EventArgs e) { dirty=true; }
        void Started(object sender,EventArgs e) { attemptId=Guid.NewGuid().ToString("N");dirty=true; }
        void Reset(object sender,TimerPhase phase) { attemptId=Guid.NewGuid().ToString("N");dirty=true; }
        static readonly string[] AutoSplitterAssemblies={"LiveSplit.ScriptableAutoSplit","LiveSplit.AutoSplittingRuntime"};
        // Scriptable Auto Splitter is ASLComponent, Auto Splitting Runtime is ASRComponent.
        public static bool IsAutoSplitter(Type type) {
            return type.Name=="ASLComponent" || type.Name=="ASRComponent" || type.Name.IndexOf("AutoSplit",StringComparison.OrdinalIgnoreCase)>=0 ||
                AutoSplitterAssemblies.Contains(type.Assembly.GetName().Name,StringComparer.OrdinalIgnoreCase);
        }
        static void RequireNoAutoSplitter(LiveSplitState state) {
            if((state.Run.AutoSplitter!=null && state.Run.AutoSplitter.IsActivated) || state.Layout.Components.Any(c=>IsAutoSplitter(c.GetType())))
                throw new InvalidOperationException("Viewer mode requires a layout without an active autosplitter or Scriptable Auto Splitter component.");
        }
        public void Connect() {
            Disconnect();
            try {
                role=settings.Role;
                if(role=="viewer") { RequireNoAutoSplitter(state);mirror=new Mirror(state); }
                connection=new RelayConnection(settings.Address,settings.Room,role,settings.Key);
                connection.Start();dirty=true;hasSnapshot=false;frozenElapsed=-1;publishedGeneration=-1;publishedStructure=null;
                SetStatus("Connecting",Color.Goldenrod);
            } catch(Exception e) { Disconnect();SetStatus(e.Message,Color.IndianRed); }
        }
        public void Disconnect() {
            if(connection!=null) { connection.Dispose();connection=null; }
            if(mirror!=null) { mirror.Dispose();mirror=null; }
            hasSnapshot=false;SetStatus("Disconnected",Color.Gray);
        }
        void SetStatus(string message,Color c) { label="Coop: "+message;colour=c;settings.Status(message); }
        public void Update(IInvalidator invalidator,LiveSplitState ignored,float width,float height,LayoutMode mode) {
            if(connection!=null) {
                try {
                    if(role=="host") {
                        if(!Object.ReferenceEquals(publishingRun,state.Run)) { publishingRun=state.Run;runId=Guid.NewGuid().ToString("N");dirty=true; }
                        if(connection.Ready && (dirty || watch.ElapsedMilliseconds-lastPublished>=250)) {
                            var snapshot=Snapshot.Capture(state,runId,attemptId,++sequence);
                            if(!snapshot.Valid()) throw new InvalidOperationException("Run exceeds supported limits or contains unsupported metadata.");
                            // Complete snapshots only when something besides the clock changed or after a reconnect.
                            int generation=connection.Generation;string structure=snapshot.StructureKey();
                            bool complete=generation!=publishedGeneration || structure!=publishedStructure;
                            if(connection.Publish(snapshot,complete,generation) && complete) { publishedGeneration=generation;publishedStructure=structure; }
                            dirty=false;lastPublished=watch.ElapsedMilliseconds;
                        }
                        SetStatus(connection.Ready?"Host connected":connection.Status,connection.Ready?Color.MediumAquamarine:Color.IndianRed);
                    } else {
                        // An autosplitter added while viewing would fight the mirrored timer.
                        if(watch.ElapsedMilliseconds-lastGuard>=1000) { lastGuard=watch.ElapsedMilliseconds;RequireNoAutoSplitter(state); }
                        var delivery=connection.Take();
                        if(delivery!=null) {
                            if(delivery.structural) mirror.Adopt(delivery.snapshot); else mirror.Advance(delivery.snapshot);
                            hasSnapshot=true;lastReceived=watch.ElapsedMilliseconds;
                            initialAge=delivery.ageMs;frozenElapsed=delivery.live?-1:Math.Min(initialAge,1500);
                        }
                        bool live=connection.Ready && connection.HostOnline && hasSnapshot && watch.ElapsedMilliseconds-lastReceived+initialAge<1500;
                        if(hasSnapshot) {
                            double age=watch.ElapsedMilliseconds-lastReceived+initialAge;
                            if(!live && frozenElapsed<0) frozenElapsed=Math.Min(age,1500);
                            mirror.Render(live?age:Math.Max(0,frozenElapsed),!live);
                        }
                        SetStatus(live?"Following host":hasSnapshot?"OFFLINE — timer frozen":connection.Ready?"Waiting for host":connection.Status,live?Color.MediumAquamarine:Color.IndianRed);
                    }
                } catch(Exception e) { Disconnect();SetStatus("Stopped: "+e.Message,Color.IndianRed); }
            }
            if(invalidator!=null) invalidator.Invalidate(0,0,width,height);
        }
        public string ComponentName { get { return "Coop Relay"; } }
        public float VerticalHeight { get { return 22; } }
        public float HorizontalWidth { get { return 240; } }
        public float MinimumWidth { get { return 160; } }
        public float MinimumHeight { get { return 22; } }
        public float PaddingTop { get { return 0; } }
        public float PaddingBottom { get { return 0; } }
        public float PaddingLeft { get { return 0; } }
        public float PaddingRight { get { return 0; } }
        public IDictionary<string,Action> ContextMenuControls { get { return new Dictionary<string,Action>{{"Coop: Connect",Connect},{"Coop: Disconnect",Disconnect}}; } }
        void Draw(Graphics g,float width,float height) {
            using(var brush=new SolidBrush(colour)) using(var format=new StringFormat { Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap })
                g.DrawString(label,font,brush,new RectangleF(4,2,width-8,height-2),format);
        }
        public void DrawVertical(Graphics g,LiveSplitState s,float width,Region clip) { Draw(g,width,VerticalHeight); }
        public void DrawHorizontal(Graphics g,LiveSplitState s,float height,Region clip) { Draw(g,HorizontalWidth,height); }
        public Control GetSettingsControl(LayoutMode mode) { return settings; }
        public XmlNode GetSettings(XmlDocument doc) { return settings.SaveXml(doc); }
        public void SetSettings(XmlNode node) { settings.LoadXml(node); }
        public void Dispose() {
            Disconnect();state.OnStart-=Started;state.OnSplit-=Changed;state.OnUndoSplit-=Changed;state.OnSkipSplit-=Changed;
            state.OnPause-=Changed;state.OnResume-=Changed;state.OnReset-=Reset;
            settings.Dispose();font.Dispose();
        }
    }
}
