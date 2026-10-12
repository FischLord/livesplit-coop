using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
    public sealed class CoopComponent : IComponent, ICoopSession {
        readonly LiveSplitState state;
        readonly string diagnosticId=Guid.NewGuid().ToString("N");
        string diagnosticStatus;
        readonly CoopSettings settings=new CoopSettings();
        readonly string localPath;
        LocalState local;
        ProfilePack pack;
        bool packLoaded;
        string hostLabel;
        AttemptRecorder recorder;
        IDisposable hostLock;
        string lockedPath;
        bool autoTried;
        // A viewer lobby is only written to coop-local.json once the relay accepted it; until then it lives in memory.
        bool unconfirmed;
        string saveError,lastFailure,saveWarning,connectFailure,statusDetail="";
        string statusLine="";
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
        readonly RainbowGraph rainbowGraph=new RainbowGraph();
        long lastRainbowUpdate=-100;
        Color colour=Color.Gray;
        public CoopComponent(LiveSplitState state) : this(state,LocalState.DefaultPath) { }
        // The local state path is injectable for tests.
        public CoopComponent(LiveSplitState state,string localPath) : this(state,localPath,null,false) { }
        // Tests: the given pack (or explicitly none) is used and never looked up.
        public CoopComponent(LiveSplitState state,string localPath,ProfilePack pack) : this(state,localPath,pack,true) { }
        CoopComponent(LiveSplitState state,string localPath,ProfilePack pack,bool packKnown) {
            this.pack=pack;packLoaded=packKnown;
            this.state=state;this.localPath=localPath;local=LocalState.Load(localPath);
            settings.OpenRequested+=()=>OpenWindow(settings.FindForm() ?? state.Form);
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
                return Texts.T("viewer.autosplitterOn");
            if(state.Layout!=null && state.Layout.Components.Any(c=>IsAutoSplitter(c.GetType())))
                return Texts.T("viewer.autosplitterLayout");
            return null;
        }
        static void RequireNoAutoSplitter(LiveSplitState state) {
            var problem=AutoSplitterProblem(state);
            if(problem!=null) throw new AutoSplitterActive(problem);
        }
        // Production: the pack is looked up next to LiveSplit on first use.
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
        public string LastLobbyName { get { return local.lastHostLobby==null?null:local.lastHostLobby.name; } }
        public bool ShareIsInvite { get { var lobby=Lobby;return lobby!=null && Role=="host" && lobby.name==null; } }
        // What a host can hand to teammates: name and password, or the invitation for a room from before lobbies.
        public string ShareText {
            get {
                var lobby=Lobby;
                if(lobby==null || Role!="host") return null;
                try {
                    if(lobby.name==null) { string key=LocalState.Unprotect(lobby.protectedViewerKey);return key.Length>0?Invite.Format(lobby.server,lobby.roomId,key):null; }
                    string password=SavedPassword;
                    return password==null?null:CoopWindow.Info(lobby.name,password);
                } catch(CryptographicException) { return null; } catch(ArgumentException) { return null; }
            }
        }
        public string JoinProblem() {
            var problem=AutoSplitterProblem(state);
            if(problem!=null) return problem;
            if((connection==null || role!="viewer") && state.CurrentPhase!=TimerPhase.NotRunning) return Texts.T("viewer.stopTimer");
            return null;
        }
        public string Note { get { return recorder==null?null:recorder.Note; } }
        public string LastFailure { get { return lastFailure; } }
        // Set while the last write of coop-local.json failed; cleared by the next successful write.
        public string SaveWarning { get { return saveWarning; } }
        // Returns null, or a warning when a host lobby works but could not be saved.
        public string UseLobby(LobbyGrant grant,string password) {
            lastFailure=null;
            local.lobby=LobbyState.From(grant,Role,password);
            unconfirmed=Role=="viewer";
            string warning=null;
            if(!unconfirmed && !SaveLocal()) warning=Texts.T("lobby.saveFailed",saveError);
            Connect();
            return warning;
        }
        public bool JoinInvite(string text) {
            string server,room,key;
            if(!Invite.TryParse(text,out server,out room,out key)) return false;
            lastFailure=null;
            local.lobby=LobbyState.FromLegacy(server,room,"viewer",key,"");unconfirmed=true;
            Connect();return true;
        }
        public void Reconnect() { Connect(); }
        public void Leave() {
            var left=Lobby;
            // A lobby the relay already forgot cannot be reopened.
            bool expired=connection!=null && connection.StopReason=="unknown_room";
            if(left!=null && Role=="host" && left.name!=null && !expired) local.lastHostLobby=left;
            lastFailure=null;unconfirmed=false;local.lobby=null;Disconnect();SaveLocal();
        }
        public void ReopenLast() {
            if(local.lastHostLobby==null || Role!="host") return;
            lastFailure=null;unconfirmed=false;
            local.lobby=local.lastHostLobby;local.lastHostLobby=null;
            SaveLocal();Connect();
        }
        // An unconfirmed lobby never reaches the file, whatever else is saved meanwhile.
        bool SaveLocal() {
            var held=local.lobby;
            try { if(unconfirmed) local.lobby=null;local.Save(localPath);saveWarning=null;return true; }
            catch(Exception e) { saveError=e.Message;saveWarning=Texts.T("local.saveFailed",e.Message);CoopDiagnostics.Error("local_state",e);return false; }
            finally { local.lobby=held;settings.Status(statusDetail+(saveWarning==null?"":"\n"+saveWarning)); }
        }
        // The relay (or a local connect error) turned an unconfirmed lobby down: forget it and remember why.
        void ForgetUnconfirmed(string code) { unconfirmed=false;local.lobby=null;lastFailure=code;Disconnect(); }
        string LobbyTitle() { var lobby=Lobby;return lobby==null?"":lobby.name ?? Texts.T("status.inviteRoom"); }
        void OpenWindow(IWin32Window owner) {
            ProfileSection section=null;
            var available=Pack;
            if(available!=null) {
                var current=available.ForSplits(state.Run.FilePath) ?? available.ByKey(local.lastProfile);
                section=new ProfileSection(available,current,Role=="viewer",hostLabel,state.CurrentPhase!=TimerPhase.NotRunning);
            }
            using(var window=new CoopWindow(this,section)) window.ShowDialog(owner);
            if(section!=null && section.Chosen!=null) LoadProfile(section.Chosen,owner);
        }
        // After the layout changes, LiveSplit disposes this component; the new one reconnects to the lobby by itself.
        void LoadProfile(PackProfile profile,IWin32Window owner) {
            try {
                var result=ProfileSwitcher.Switch(state,state.Form,Pack,profile,()=>{ local.lastProfile=Pack.Key(profile);SaveLocal(); });
                CoopDiagnostics.Write("profile_switch",new { component=diagnosticId,result=result.ToString() });
            } catch(Exception e) {
                CoopDiagnostics.Error("profile_switch",e);
                MessageBox.Show(owner,Texts.T("profile.failed",e.Message),Texts.T("window.title"),MessageBoxButtons.OK,MessageBoxIcon.Warning);
            }
        }
        public void Connect() {
            connectFailure=null;
            ConnectCore();
            if(connection==null && connectFailure!=null) lastFailure=connectFailure;
            if(unconfirmed && connection==null) {
                // Keep the specific status (Disconnect inside would reset it to "not connected").
                string hud=statusLine,detail=statusDetail;Color shown=colour;
                ForgetUnconfirmed(connectFailure ?? "rejected");
                if(connectFailure!=null) SetStatus(hud,detail,shown);
            }
        }
        void ConnectCore() {
            Disconnect();
            var lobby=Lobby;
            if(lobby==null) return;
            try {
                role=Role;CoopDiagnostics.Write("connect_requested",new { component=diagnosticId,role=role });
                string key;
                try { key=LocalState.Unprotect(role=="host"?lobby.protectedHostKey:lobby.protectedViewerKey); }
                catch(CryptographicException) { connectFailure="key_unreadable";SetStatus(Texts.T("lobby.keyUnreadable"),Color.IndianRed);return; }
                if(role=="viewer") { RequireNoAutoSplitter(state);mirror=new Mirror(state);
                    // The recorder outlives Disconnect so its note stays visible; every viewer connect starts a fresh one.
                    string splitsRoot=Pack!=null?Pack.FullPath("Splits/Coop"):Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Splits","Coop");
                    recorder=new AttemptRecorder(new ViewerLibrary(Pack,splitsRoot)); }
                connection=new RelayConnection(lobby.server,lobby.roomId,role,key);
                connection.Start();dirty=true;hasSnapshot=false;hostLabel=null;frozenElapsed=-1;publishedGeneration=-1;publishedStructure=null;
                SetStatus(LobbyTitle()+" · "+Texts.T("status.connecting"),Color.Goldenrod);
            } catch(AutoSplitterActive e) { CoopDiagnostics.Error("autosplitter_guard",e);Disconnect();connectFailure="autosplitter";SetStatus(Texts.T("status.autosplitter"),e.Message,Color.IndianRed); }
            catch(Exception e) { CoopDiagnostics.Error("component",e);Disconnect();SetStatus(e.Message,Color.IndianRed); }
        }
        public void Disconnect() {
            if(connection!=null) { CoopDiagnostics.Write("disconnect_requested",new { component=diagnosticId,role=role });connection.Dispose();connection=null; }
            if(mirror!=null) { mirror.Dispose();mirror=null; }
            hasSnapshot=false;
            if(Lobby==null) SetStatus(Texts.T("status.notConnected"),Color.Gray); else SetStatus(LobbyTitle()+" · "+Texts.T("status.reconnecting"),Color.Gray);
        }
        void SetStatus(string message,Color c) { SetStatus(message,message,c); }
        // hudMessage stays short enough for the layout; detailMessage (settings panel) can carry the full reason.
        void SetStatus(string hudMessage,string detailMessage,Color c) { statusLine=hudMessage;label="Coop: "+hudMessage;colour=c;statusDetail=detailMessage;settings.Status(detailMessage+(saveWarning==null?"":"\n"+saveWarning)); }
        // Only a terminal Stopped phase is an error colour; Connecting/Joining/Retrying are normal in-progress states.
        static Color PhaseColor(RelayPhase phase) { return phase==RelayPhase.Stopped?Color.IndianRed:Color.Goldenrod; }
        public void Update(IInvalidator invalidator,LiveSplitState ignored,float width,float height,LayoutMode mode) {
            if(!autoTried) { autoTried=true;if(Lobby!=null) Connect(); else SetStatus(Texts.T("status.notConnected"),Color.Gray); }
            // A host copy marks its open splits so a viewer copy on this PC does not write into them.
            string loaded=Role=="host"?state.Run.FilePath:null;
            if(!string.Equals(loaded,lockedPath,StringComparison.OrdinalIgnoreCase)) {
                ReleaseHostLock();
                lockedPath=loaded;
                if(!string.IsNullOrEmpty(loaded) && File.Exists(loaded)) {
                    try { hostLock=HostLock.Hold(loaded); } catch(Exception e) { CoopDiagnostics.Error("host_lock",e); }
                }
            }
            if(watch.ElapsedMilliseconds-lastRainbowUpdate>=100) {
                lastRainbowUpdate=watch.ElapsedMilliseconds;
                if(rainbowGraph.Update(state) && state.Form!=null)state.Form.Invalidate();
            }
            // The role is set per layout; a connection made for the other role would publish or mirror against the wrong timer.
            if(connection!=null && role!=Role) {
                // An unconfirmed lobby belongs to the old role and would otherwise stay in memory unseen.
                if(unconfirmed) ForgetUnconfirmed(null); else { Disconnect();if(Lobby!=null) Connect(); }
            }
            if(unconfirmed && connection!=null) {
                if(connection.Ready) { unconfirmed=false;SaveLocal(); }
                else if(connection.StopReason!=null) ForgetUnconfirmed(connection.StopReason);
            }
            if(connection!=null) {
                try {
                    if(role=="host") {
                        if(!Object.ReferenceEquals(publishingRun,state.Run)) { publishingRun=state.Run;runId=Guid.NewGuid().ToString("N");dirty=true; }
                        if(connection.Ready && (dirty || watch.ElapsedMilliseconds-lastPublished>=250)) {
                            var snapshot=Snapshot.Capture(state,runId,attemptId,++sequence);
                            string badge=ProfileLabel();snapshot.profileLabel=badge.Length>0 && badge.Length<=60?badge:null;
                            if(!snapshot.Valid()) throw new InvalidOperationException(Texts.T("detail.tooLarge"));
                            // Complete snapshots only when something besides the clock changed or after a reconnect.
                            int generation=connection.Generation;string structure=snapshot.StructureKey();
                            bool complete=generation!=publishedGeneration || structure!=publishedStructure;
                            if(connection.Publish(snapshot,complete,generation) && complete) { publishedGeneration=generation;publishedStructure=structure; }
                            dirty=false;lastPublished=watch.ElapsedMilliseconds;
                        }
                        string title=LobbyTitle();
                        if(connection.Ready) SetStatus(title+" · "+Texts.T("status.host")+(connection.Viewers>=0?" · "+Texts.T("status.online",connection.Viewers):""),connection.Status,Color.MediumAquamarine);
                        else if(connection.StopReason!=null) SetStatus(title+" · "+CoopWindow.Explain(connection.StopReason),connection.Status,Color.IndianRed);
                        else SetStatus(title+" · "+Texts.T("status.reconnecting"),connection.Status,Color.Goldenrod);
                    } else {
                        // An autosplitter added while viewing would fight the mirrored timer.
                        if(watch.ElapsedMilliseconds-lastGuard>=1000) { lastGuard=watch.ElapsedMilliseconds;RequireNoAutoSplitter(state); }
                        var delivery=connection.Take();
                        if(delivery!=null) {
                            if(delivery.structural) { mirror.Adopt(delivery.snapshot);hostLabel=delivery.snapshot.profileLabel;TraceRun("snapshot_applied"); } else mirror.Advance(delivery.snapshot);
                            if(recorder!=null) {
                                try { recorder.Observe(delivery.snapshot,connection.Generation); }
                                catch(Exception e) { CoopDiagnostics.Error("viewer_recorder",e); }
                            }
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
                        if(live) SetStatus(LobbyTitle()+" · "+Texts.T("status.following"),Color.MediumAquamarine);
                        // A prior snapshot exists but delivery has stalled or stopped: keep the freeze visible without hiding why (retrying vs terminal).
                        else if(hasSnapshot) {
                            string hud,detail;
                            if(!connection.Ready) { hud=LobbyTitle()+" · "+(connection.Phase==RelayPhase.Stopped?CoopWindow.Explain(connection.StopReason):Texts.T("status.reconnecting"));detail=Texts.T("detail.offline",connection.Status); }
                            else if(!connection.HostOnline) { hud=LobbyTitle()+" · "+Texts.T("status.hostOffline");detail=Texts.T("detail.hostGone"); }
                            else { hud=LobbyTitle()+" · "+Texts.T("status.stale");detail=Texts.T("detail.noUpdates"); }
                            SetStatus(hud,detail,PhaseColor(connection.Phase));
                        }
                        else if(connection.Ready) SetStatus(LobbyTitle()+" · "+Texts.T("status.waiting"),Color.Goldenrod);
                        else SetStatus(LobbyTitle()+" · "+(connection.StopReason!=null?CoopWindow.Explain(connection.StopReason):Texts.T("status.reconnecting")),connection.Status,PhaseColor(connection.Phase));
                    }
                } catch(AutoSplitterActive e) { CoopDiagnostics.Error("autosplitter_guard",e);Disconnect();SetStatus(Texts.T("status.stoppedAutosplitter"),e.Message,Color.IndianRed); }
                catch(Exception e) { CoopDiagnostics.Error("component",e);Disconnect();SetStatus(Texts.T("status.stopped",e.Message),Color.IndianRed); }
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
        public IDictionary<string,Action> ContextMenuControls { get { return new Dictionary<string,Action> { { Texts.T("menu.coop"),()=>OpenWindow(state.Form) } }; } }
        public void ExportDiagnostics(IWin32Window owner) {
            using(var dialog=new SaveFileDialog { Title="Export Coop diagnostics",Filter="Diagnostic log (*.jsonl)|*.jsonl",DefaultExt="jsonl",AddExtension=true,
                FileName="LiveSplit-Coop-diagnostics-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".jsonl" }) {
                if(dialog.ShowDialog(owner)!=DialogResult.OK)return;
                try { CoopDiagnostics.Write("export_requested",new { component=diagnosticId });CoopDiagnostics.Export(dialog.FileName);
                    MessageBox.Show(owner,"Diagnostics exported. This file contains connection and timer events, without room keys or invitation links.","Coop diagnostics",MessageBoxButtons.OK,MessageBoxIcon.Information);
                } catch(Exception e) { CoopDiagnostics.Error("export",e);MessageBox.Show(owner,"Could not export diagnostics. Choose a writable destination and check free disk space.","Coop diagnostics",MessageBoxButtons.OK,MessageBoxIcon.Warning); }
            }
        }
        void Draw(Graphics g,float width,float height) {
            string players=ProfileLabel();
            float badge=Math.Min(width/2,g.MeasureString(players,font).Width+12);
            using(var brush=new SolidBrush(colour)) using(var format=new StringFormat { Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap }) {
                g.DrawString(label,font,brush,new RectangleF(4,2,Math.Max(0,width-badge-8),height-2),format);
                format.Alignment=StringAlignment.Far;
                brush.Color=Color.FromArgb(88,214,207);
                g.DrawString(players,font,brush,new RectangleF(width-badge,2,badge-4,height-2),format);
            }
        }
        public void DrawVertical(Graphics g,LiveSplitState s,float width,Region clip) { Draw(g,width,VerticalHeight); }
        public void DrawHorizontal(Graphics g,LiveSplitState s,float height,Region clip) { Draw(g,HorizontalWidth,height); }
        public Control GetSettingsControl(LayoutMode mode) { return settings; }
        public XmlNode GetSettings(XmlDocument doc) { return settings.SaveXml(doc); }
        public void SetSettings(XmlNode node) {
            settings.LoadXml(node);
            // Rooms saved in layouts by older versions move to coop-local.json once.
            if(settings.Legacy!=null && local.lobby==null && (local.migratedLegacyRooms==null || !local.migratedLegacyRooms.Contains(settings.Legacy.roomId))) {
                var legacy=settings.Legacy;
                if(local.migratedLegacyRooms==null) local.migratedLegacyRooms=new List<string>();
                local.migratedLegacyRooms.Add(legacy.roomId);
                local.lobby=LobbyState.FromLegacy(legacy.server,legacy.roomId,legacy.role,legacy.key,legacy.inviteKey);SaveLocal();
                CoopDiagnostics.Write("legacy_room_migrated",new { component=diagnosticId,role=legacy.role });
            }
            // Until the first Update connects automatically, say what is about to happen.
            if(!autoTried && connection==null) { if(Lobby!=null) SetStatus(LobbyTitle()+" · "+Texts.T("status.connecting"),Color.Goldenrod); else SetStatus(Texts.T("status.notConnected"),Color.Gray); }
        }
        void ReleaseHostLock() { if(hostLock!=null) { hostLock.Dispose();hostLock=null; } }
        public void Dispose() {
            Disconnect();ReleaseHostLock();state.OnStart-=Started;state.OnSplit-=Split;state.OnUndoSplit-=Undo;state.OnSkipSplit-=Skip;
            state.OnPause-=Paused;state.OnResume-=Resumed;state.OnReset-=Reset;
            CoopDiagnostics.Write("component_disposed",new { component=diagnosticId });
            settings.Dispose();font.Dispose();
        }
    }
}
