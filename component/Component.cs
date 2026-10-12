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
[assembly: System.Reflection.AssemblyInformationalVersion("0.1.0-beta.3")]
[assembly: System.Reflection.AssemblyTitle("LiveSplit Coop")]

namespace LiveSplit.Coop {
    public sealed class Factory : IComponentFactory {
        public string ComponentName { get { return "Coop Relay"; } }
        public string Description { get { return "Share a host timer through the community relay or your own, without a VPN."; } }
        public ComponentCategory Category { get { return ComponentCategory.Control; } }
        public string UpdateName { get { return ComponentName; } }
        public string XMLURL { get { return ""; } }
        public string UpdateURL { get { return ""; } }
        public Version Version { get { return new Version(0,1,0); } }
        public IComponent Create(LiveSplitState state) { return new CoopComponent(state); }
    }
    // Kept apart from other failures so the HUD can show a short label while settings show the full instructions.
    public sealed class AutoSplitterActive : InvalidOperationException { public AutoSplitterActive(string message) : base(message) { } }
    public sealed class CoopComponent : IComponent {
        readonly LiveSplitState state;
        readonly string diagnosticId=Guid.NewGuid().ToString("N");
        string diagnosticStatus;
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
        string quickNotice;long quickNoticeUntil;
        Color colour=Color.Gray;
        public CoopComponent(LiveSplitState state) {
            this.state=state;settings.ConnectRequested+=Connect;settings.DisconnectRequested+=Disconnect;
            settings.ViewerProblem=()=>AutoSplitterProblem(state);
            state.OnStart+=Started;state.OnSplit+=Split;state.OnUndoSplit+=Undo;state.OnSkipSplit+=Skip;
            state.OnPause+=Paused;state.OnResume+=Resumed;state.OnReset+=Reset;
            CoopDiagnostics.Write("component_loaded",new { component=diagnosticId });
        }
        void TraceRun(string action) {
            var time=state.CurrentTime;
            CoopDiagnostics.Write("timer",new { component=diagnosticId,role=settings.Role,action=action,
                phase=state.CurrentPhase.ToString(),index=state.CurrentSplitIndex,segments=state.Run.Count,
                realTicks=Pair.Tick(time.RealTime),gameTicks=Pair.Tick(time.GameTime),method=state.CurrentTimingMethod.ToString() });
            dirty=true;
        }
        void Split(object sender,EventArgs e) { TraceRun(state.CurrentPhase==TimerPhase.Ended?"finish":"split"); }
        void Undo(object sender,EventArgs e) { TraceRun("undo_split"); }
        void Skip(object sender,EventArgs e) { TraceRun("skip_split"); }
        void Paused(object sender,EventArgs e) { TraceRun("pause"); }
        void Resumed(object sender,EventArgs e) { TraceRun("resume"); }
        void Started(object sender,EventArgs e) { attemptId=Guid.NewGuid().ToString("N");TraceRun("start"); }
        void Reset(object sender,TimerPhase phase) { attemptId=Guid.NewGuid().ToString("N");TraceRun("reset"); }
        static readonly string[] AutoSplitterAssemblies={"LiveSplit.ScriptableAutoSplit","LiveSplit.AutoSplittingRuntime"};
        // Scriptable Auto Splitter is ASLComponent, Auto Splitting Runtime is ASRComponent.
        public static bool IsAutoSplitter(Type type) {
            return type.Name=="ASLComponent" || type.Name=="ASRComponent" || type.Name.IndexOf("AutoSplit",StringComparison.OrdinalIgnoreCase)>=0 ||
                AutoSplitterAssemblies.Contains(type.Assembly.GetName().Name,StringComparer.OrdinalIgnoreCase);
        }
        // Returns what the user must change before viewing, or null. Says where to click, since the two cases live in different dialogs.
        public static string AutoSplitterProblem(LiveSplitState state) {
            if(state.Run.AutoSplitter!=null && state.Run.AutoSplitter.IsActivated)
                return "Viewer: turn off the autosplitter first. Right-click → Edit Splits → Deactivate, then click Connect again.";
            if(state.Layout!=null && state.Layout.Components.Any(c=>IsAutoSplitter(c.GetType())))
                return "Viewer: remove \"Scriptable Auto Splitter\" from this layout first (right-click → Edit Layout → select it → −), then click Connect again.";
            return null;
        }
        static void RequireNoAutoSplitter(LiveSplitState state) {
            var problem=AutoSplitterProblem(state);
            if(problem!=null) throw new AutoSplitterActive(problem);
        }
        public void Connect() {
            Disconnect();
            try {
                role=settings.Role;CoopDiagnostics.Write("connect_requested",new { component=diagnosticId,role=role });
                if(role=="viewer") { RequireNoAutoSplitter(state);mirror=new Mirror(state); }
                connection=new RelayConnection(settings.Address,settings.Room,role,settings.Key);
                connection.Start();dirty=true;hasSnapshot=false;frozenElapsed=-1;publishedGeneration=-1;publishedStructure=null;
                SetStatus("Connecting",Color.Goldenrod);
            } catch(AutoSplitterActive e) { CoopDiagnostics.Error("autosplitter_guard",e);Disconnect();SetStatus("Turn off autosplitter",e.Message,Color.IndianRed); }
            catch(Exception e) { CoopDiagnostics.Error("component",e);Disconnect();SetStatus(e.Message,Color.IndianRed); }
        }
        public void Disconnect() {
            quickNotice=null;
            if(connection!=null) { CoopDiagnostics.Write("disconnect_requested",new { component=diagnosticId,role=role });connection.Dispose();connection=null; }
            if(mirror!=null) { mirror.Dispose();mirror=null; }
            hasSnapshot=false;SetStatus("Disconnected",Color.Gray);
        }
        void SetStatus(string message,Color c) { SetStatus(message,message,c); }
        // hudMessage stays short enough for the layout; detailMessage (settings panel) can carry the full reason.
        void SetStatus(string hudMessage,string detailMessage,Color c) { label="Coop: "+hudMessage;colour=c;settings.Status(detailMessage); }
        // Only a terminal Stopped phase is an error colour; Connecting/Joining/Retrying are normal in-progress states.
        static Color PhaseColor(RelayPhase phase) { return phase==RelayPhase.Stopped?Color.IndianRed:Color.Goldenrod; }
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
                        SetStatus(connection.Ready?"Host connected":connection.Status,connection.Ready?Color.MediumAquamarine:PhaseColor(connection.Phase));
                    } else {
                        // An autosplitter added while viewing would fight the mirrored timer.
                        if(watch.ElapsedMilliseconds-lastGuard>=1000) { lastGuard=watch.ElapsedMilliseconds;RequireNoAutoSplitter(state); }
                        var delivery=connection.Take();
                        if(delivery!=null) {
                            if(delivery.structural) { mirror.Adopt(delivery.snapshot);TraceRun("snapshot_applied"); } else mirror.Advance(delivery.snapshot);
                            hasSnapshot=true;lastReceived=watch.ElapsedMilliseconds;
                            initialAge=delivery.ageMs;frozenElapsed=delivery.live?-1:Math.Min(initialAge,1500);
                        }
                        bool live=connection.Ready && connection.HostOnline && hasSnapshot && watch.ElapsedMilliseconds-lastReceived+initialAge<1500;
                        string health=live?"live":!hasSnapshot?"waiting":!connection.Ready?"disconnected":!connection.HostOnline?"host_offline":"stale";
                        if(health!=diagnosticStatus) { diagnosticStatus=health;CoopDiagnostics.Write("viewer_health",new { component=diagnosticId,health=health,ageMs=hasSnapshot?(long?)(watch.ElapsedMilliseconds-lastReceived+initialAge):null }); }
                        if(hasSnapshot) {
                            double age=watch.ElapsedMilliseconds-lastReceived+initialAge;
                            if(!live && frozenElapsed<0) frozenElapsed=Math.Min(age,1500);
                            mirror.Render(live?age:Math.Max(0,frozenElapsed),!live);
                        }
                        if(live) SetStatus("Following host",Color.MediumAquamarine);
                        // A prior snapshot exists but delivery has stalled or stopped: keep the freeze visible without hiding why (retrying vs terminal).
                        else if(hasSnapshot) {
                            string hud,detail;
                            if(!connection.Ready) { hud=connection.Phase==RelayPhase.Stopped?"Stopped — frozen":"Reconnecting — frozen";detail="OFFLINE — timer frozen ("+connection.Status+")"; }
                            else if(!connection.HostOnline) { hud="Host offline — frozen";detail="OFFLINE — timer frozen (host disconnected from the relay)"; }
                            else { hud="Host data stale — frozen";detail="OFFLINE — timer frozen (connected, but no fresh updates from host)"; }
                            SetStatus(hud,detail,PhaseColor(connection.Phase));
                        }
                        else if(connection.Ready) SetStatus("Waiting for host",Color.Goldenrod);
                        else SetStatus(connection.Status,PhaseColor(connection.Phase));
                    }
                } catch(AutoSplitterActive e) { CoopDiagnostics.Error("autosplitter_guard",e);Disconnect();SetStatus("Stopped: autosplitter on",e.Message,Color.IndianRed); }
                catch(Exception e) { CoopDiagnostics.Error("component",e);Disconnect();SetStatus("Stopped: "+e.Message,Color.IndianRed); }
            }
            if(quickNotice!=null) {
                if(watch.ElapsedMilliseconds<quickNoticeUntil) SetStatus(quickNotice,Color.MediumAquamarine);
                else { quickNotice=null;if(connection==null)SetStatus("Disconnected",Color.Gray); }
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
        public IDictionary<string,Action> ContextMenuControls { get { return new Dictionary<string,Action>{
            {"Coop: Host...",HostRoom},{"Coop: Join...",JoinRoom},{"Coop: Copy invite",CopyRoomInvite},
            {"Coop: Reconnect",Connect},{"Coop: Disconnect",Disconnect},{"Coop: Export diagnostics...",ExportDiagnostics}}; } }
        void ExportDiagnostics() {
            using(var dialog=new SaveFileDialog { Title="Export Coop diagnostics",Filter="Diagnostic log (*.jsonl)|*.jsonl",DefaultExt="jsonl",AddExtension=true,
                FileName="LiveSplit-Coop-diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".jsonl" }) {
                if(dialog.ShowDialog(state.Form)!=DialogResult.OK)return;
                try { CoopDiagnostics.Write("export_requested",new { component=diagnosticId });CoopDiagnostics.Export(dialog.FileName);
                    MessageBox.Show(state.Form,"Diagnostics exported. This file contains connection and timer events, without room keys or invitation links.","Coop diagnostics",MessageBoxButtons.OK,MessageBoxIcon.Information);
                } catch(Exception e) { CoopDiagnostics.Error("export",e);MessageBox.Show(state.Form,"Could not export diagnostics. Choose a writable destination and check free disk space.","Coop diagnostics",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            }
        }
        void HostRoom() {
            using(var dialog=new HostDialog(settings.Address,settings.HasHostRoom,settings.Room)) {
                if(dialog.ShowDialog(state.Form)!=DialogResult.OK)return;
                if(dialog.Grant!=null) { settings.ApplyHostGrant(dialog.Grant);state.Layout.HasChanged=true; }
                Connect();
                if(dialog.Grant!=null) {
                    string feedback;settings.CopyInviteToClipboard(out feedback);
                    using(var invitation=new InvitationDialog(settings,feedback))invitation.ShowDialog(state.Form);
                }
            }
        }
        string JoinProblem() {
            var problem=AutoSplitterProblem(state);
            if(problem!=null)return problem;
            if((connection==null || role!="viewer") && state.CurrentPhase!=TimerPhase.NotRunning)
                return "Stop/reset the local timer before joining as a viewer.";
            return null;
        }
        void JoinRoom() {
            using(var dialog=new JoinDialog(JoinProblem)) {
                if(dialog.ShowDialog(state.Form)!=DialogResult.OK)return;
                if(settings.TryApplyInvite(dialog.InviteText)) { state.Layout.HasChanged=true;Connect(); }
            }
        }
        void CopyRoomInvite() {
            if(string.IsNullOrEmpty(settings.InviteCode)) {
                MessageBox.Show(state.Form,"No invitation is available. Choose Coop: Host... and create a room first. Manually configured rooms need an invitation from their operator.","Coop",MessageBoxButtons.OK,MessageBoxIcon.Information);return;
            }
            string feedback;
            if(settings.CopyInviteToClipboard(out feedback)) { quickNotice="Invite copied";quickNoticeUntil=watch.ElapsedMilliseconds+3500;SetStatus(quickNotice,Color.MediumAquamarine); }
            else using(var dialog=new InvitationDialog(settings,feedback))dialog.ShowDialog(state.Form);
        }
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
            Disconnect();state.OnStart-=Started;state.OnSplit-=Split;state.OnUndoSplit-=Undo;state.OnSkipSplit-=Skip;
            state.OnPause-=Paused;state.OnResume-=Resumed;state.OnReset-=Reset;
            CoopDiagnostics.Write("component_disposed",new { component=diagnosticId });
            settings.Dispose();font.Dispose();
        }
    }
}
