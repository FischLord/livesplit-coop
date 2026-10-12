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
    static WindowsFormsSynchronizationContext uiContext;
    // The PowerShell host swaps the thread's context between calls; awaits in window code must resume on this thread.
    static void Ui(){if(uiContext==null)uiContext=new WindowsFormsSynchronizationContext();System.Threading.SynchronizationContext.SetSynchronizationContext(uiContext);}
    static int passed;static readonly List<string> uiErrors=new List<string>();
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);passed++;Console.WriteLine("PASS: "+text);}
    static void Throws<T>(Action action,string text) where T:Exception {try{action();}catch(T){Check(true,text);return;}Check(false,text);}
    public static void Run(string relay,string work) {
        Texts.German=false;
        // Errors in window code must fail the run, never open the unhandled-exception dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.ThreadException+=(o,a)=>{ lock(uiErrors) uiErrors.Add(a.Exception.ToString()); };
        // Windows Forms does not install its context in a PowerShell host; without it, awaits resume on pool threads.
        Ui();
        KeyTests();
        TextTests();
        ClientTests(relay);
        StateTests(work);
        ConnectionTests(relay);
        WindowTests(work);
        lock(uiErrors) if(uiErrors.Count>0) throw new Exception("FAIL: UI thread exceptions: "+uiErrors[0]);
        ComponentTests(relay,work);
        lock(uiErrors) if(uiErrors.Count>0) throw new Exception("FAIL: UI thread exceptions: "+uiErrors[0]);
        Console.WriteLine(passed+" checks passed");
    }
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
    static void TextTests() {
        Texts.German=true;
        Check(Texts.T("lobby.wrongPassword")=="Passwort falsch.","German text");
        Check(Texts.T("status.online",2)=="2 online","formatted text");
        Texts.German=false;
        Check(Texts.T("lobby.wrongPassword")=="Wrong password.","English text");
        Check(Texts.T("no.such.key")=="no.such.key","unknown keys fall back to the key");
        foreach(var key in Texts.Keys) Check(Texts.Has(key),"every key has both languages: "+key);
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
    sealed class FakeSession : ICoopSession {
        public string role="host",server="wss://relay.example/coop",stopReason,password,problem,lastFailure,saveWarning;public bool failOnUse;
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
        public string LastFailure { get { return lastFailure; } }
        public LobbyState last;
        public string LastLobbyName { get { return last==null?null:last.name; } }
        public void ReopenLast() { lobby=last;last=null;password="banane"; }
        public bool ShareIsInvite { get { return lobby!=null && role=="host" && lobby.name==null; } }
        public string ShareText {
            get {
                if(lobby==null || role!="host") return null;
                if(lobby.name==null) return Invite.Format(lobby.server,lobby.roomId,LocalState.Unprotect(lobby.protectedViewerKey));
                return CoopWindow.Info(lobby.name,password ?? "");
            }
        }
        public string UseLobby(LobbyGrant grant,string pw) {
            lastFailure=null;used=grant;usedPassword=pw;
            if(failOnUse) { lobby=null;lastFailure="rejected";return null; }
            lobby=LobbyState.From(grant,role,pw);password=role=="host"?pw:null;return saveWarning;
        }
        public bool JoinInvite(string text) { string s,r,k;if(!Invite.TryParse(text,out s,out r,out k))return false;lastFailure=null;invite=text;lobby=LobbyState.FromLegacy(s,r,"viewer",k,"");return true; }
        public void Reconnect() { }
        public void Leave() { leaves++;if(role=="host" && lobby!=null && lobby.name!=null) last=lobby;lobby=null;connected=false;stopReason=null;lastFailure=null; }
        public string note;public string Note { get { return note; } }
        public string warn;public string SaveWarning { get { return warn; } }
        public void ExportDiagnostics(IWin32Window owner) { }
    }
    // Stands in for an autosplitter component: its type name is enough for IsAutoSplitter.
    sealed class FakeAutoSplit : LiveSplit.UI.Components.IComponent {
        public string ComponentName { get { return "AutoSplit fake"; } }
        public float HorizontalWidth { get { return 10; } } public float MinimumWidth { get { return 10; } }
        public float VerticalHeight { get { return 10; } } public float MinimumHeight { get { return 10; } }
        public float PaddingTop { get { return 0; } } public float PaddingBottom { get { return 0; } }
        public float PaddingLeft { get { return 0; } } public float PaddingRight { get { return 0; } }
        public IDictionary<string,Action> ContextMenuControls { get { return null; } }
        public void DrawHorizontal(System.Drawing.Graphics g,LiveSplit.Model.LiveSplitState s,float height,System.Drawing.Region clip) { }
        public void DrawVertical(System.Drawing.Graphics g,LiveSplit.Model.LiveSplitState s,float width,System.Drawing.Region clip) { }
        public Control GetSettingsControl(LiveSplit.UI.LayoutMode mode) { return null; }
        public System.Xml.XmlNode GetSettings(System.Xml.XmlDocument doc) { return doc.CreateElement("Settings"); }
        public void SetSettings(System.Xml.XmlNode settings) { }
        public void Update(LiveSplit.UI.IInvalidator invalidator,LiveSplit.Model.LiveSplitState state,float width,float height,LiveSplit.UI.LayoutMode mode) { }
        public void Dispose() { }
    }
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static Control Find(Control parent,string name){foreach(Control c in parent.Controls){if(c.Name==name)return c;var child=Find(c,name);if(child!=null)return child;}return null;}
    static void Click(Control c){Ui();c.GetType().GetMethod("OnClick",Hidden,null,new[]{typeof(EventArgs)},null).Invoke(c,new object[]{EventArgs.Empty});}
    static void Pump(Func<bool> ready){Ui();var clock=Stopwatch.StartNew();while(!ready() && clock.ElapsedMilliseconds<8000){Application.DoEvents();Thread.Sleep(10);}if(!ready())throw new Exception("Window timed out");}
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
            Check(Find(w,"message").Text==Texts.T("lobby.created","rvgang") || Find(w,"message").Text==Texts.T("lobby.createdNoCopy","rvgang"),"host is told the lobby was created (and whether the clipboard took it)");
            Check(Shown(Find(w,"copyPassword")),"host can copy name and password");
            Check(Find(w,"lobbyInfo").Text.Contains(Texts.T("status.public")) && Find(w,"lobbyInfo").Text.Contains("rvgang"),"the current lobby is labelled public: "+Find(w,"lobbyInfo").Text);
            Check(Find(w,"copyPassword").Text==Texts.T("lobby.copyPassword"),"named lobbies offer name and password");
            Check(!Shown(Find(w,"reopen")),"no reopen button while in a lobby");
            Click(Find(w,"leave"));Pump(()=>Shown(Find(w,"create")));
            Check(host.leaves==1 && host.lobby==null,"leave clears the lobby");
            Check(Shown(Find(w,"reopen")) && Find(w,"reopen").Text==Texts.T("lobby.reopen","rvgang"),"the last host lobby can be reopened");
            Click(Find(w,"reopen"));Pump(()=>Shown(Find(w,"leave")));
            Check(host.lobby!=null && host.lobby.name=="rvgang" && !Shown(Find(w,"create")),"reopening restores the lobby");
            w.Close();
        }
        // A host room from before lobbies has no name: it shares its invitation instead.
        var oldHost=new FakeSession();
        oldHost.lobby=LobbyState.FromLegacy("wss://relay.example/coop","oldroom","host",new string('k',43),new string('i',43));
        using(var w=new CoopWindow(oldHost,null,create,none)) {
            w.Show();Pump(()=>Shown(Find(w,"copyPassword")) && Find(w,"copyPassword").Text==Texts.T("lobby.copyInvite"));
            Check(true,"a legacy host room offers to copy its invitation");
            Check(oldHost.ShareText=="lscoop:1:wss://relay.example/coop/oldroom#"+new string('i',43),"the invitation code carries the viewer key");
            w.Close();
        }
        // A lobby without a password gets wording without a password.
        var open=new FakeSession();
        using(var w=new CoopWindow(open,null,create,none)) {
            w.Show();Pump(()=>Shown(Find(w,"create")));
            Find(w,"lobbyName").Text="opened";Click(Find(w,"create"));Pump(()=>Find(w,"message").Text.Length>0 && open.used!=null);
            string copied=Find(w,"message").Text;
            Check(copied==Texts.T("lobby.createdOpen","opened") || copied==Texts.T("lobby.createdOpenNoCopy","opened"),"a lobby without a password does not mention a password: "+copied);
            w.Close();
        }
        // The session could not save the lobby: the window warns.
        var unsaved=new FakeSession{saveWarning=Texts.T("lobby.saveFailed","disk full")};
        using(var w=new CoopWindow(unsaved,null,create,none)) {
            w.Show();Pump(()=>Shown(Find(w,"create")));
            Find(w,"lobbyName").Text="unsaved";Click(Find(w,"create"));Pump(()=>unsaved.used!=null && Find(w,"message").Text.Length>0);
            Check(Find(w,"message").Text==unsaved.saveWarning && Find(w,"message").ForeColor==System.Drawing.Color.Firebrick,"a failed save is shown as an error after create");
            w.Close();
        }
        // Closing the window while the server is still creating must not lose the lobby.
        var closing=new FakeSession();
        using(var gate=new ManualResetEvent(false)) {
            var w=new CoopWindow(closing,null,(s,n,p,pub)=>{gate.WaitOne(8000);return create(s,n,p,pub);},none);
            w.Show();Pump(()=>Shown(Find(w,"create")));
            Find(w,"lobbyName").Text="lateone";Click(Find(w,"create"));
            w.Close();w.Dispose();
            gate.Set();Pump(()=>closing.used!=null);
            Check(closing.used.name=="lateone" && closing.lobby!=null,"a lobby created after the window closed still reaches the session");
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
            viewer.problem=null;Find(w,"lobbyPassword").Text="falsch";Click(Find(w,"join"));Pump(()=>viewer.used!=null);
            Check(viewer.used!=null && viewer.used.roomId=="lobby_rvgang" && viewer.used.viewerKey==LobbyKey.DeriveViewerKey("rvgang","falsch") && viewer.usedPassword==null,"join derives the key locally and never hands over the password");
            viewer.lobby=null;viewer.lastFailure="auth_failed";   // the component forgets a rejected join itself
            Pump(()=>Find(w,"message").Text==Texts.T("lobby.wrongPassword"));
            Check(viewer.leaves==0 && viewer.lobby==null && Shown(Find(w,"join")),"wrong password is explained and the window does not need to leave");
            // Leaving while the join is still pending frees the join button again.
            viewer.used=null;Click(Find(w,"join"));Pump(()=>viewer.used!=null && Shown(Find(w,"leave")));
            Click(Find(w,"leave"));Pump(()=>Shown(Find(w,"join")));
            viewer.used=null;Click(Find(w,"join"));Pump(()=>viewer.used!=null);
            Check(viewer.used!=null,"join works again after leaving a pending join");
            Click(Find(w,"leave"));
            // A join that fails at once (e.g. invalid server) is reported, not left pending.
            viewer.failOnUse=true;viewer.used=null;Click(Find(w,"join"));Pump(()=>Find(w,"message").Text==CoopWindow.Explain("rejected"));
            Check(Shown(Find(w,"join")) && viewer.lobby==null,"a synchronous connect failure is reported and not stuck");
            viewer.failOnUse=false;
            // A join that takes too long says so, but keeps waiting.
            w.SlowJoinMs=150;viewer.used=null;Click(Find(w,"join"));Pump(()=>Find(w,"message").Text==Texts.T("lobby.slow"));
            Check(viewer.lobby!=null && Shown(Find(w,"leave")),"a slow join keeps waiting and can be left");
            Click(Find(w,"leave"));w.SlowJoinMs=15000;
            viewer.used=null;Click(Find(w,"join"));Pump(()=>viewer.used!=null);viewer.connected=true;
            Pump(()=>Shown(Find(w,"leave")));
            viewer.note="Attempt saved to Duo.lss.";Pump(()=>Find(w,"note").Text=="Attempt saved to Duo.lss." && Shown(Find(w,"note")));
            Check(true,"the window shows the recorder note");
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
        // A session that cannot save shows its warning permanently, not only after creating.
        var warned=new FakeSession();
        using(var w=new CoopWindow(warned,null,create,none)) {
            w.Show();Pump(()=>Shown(Find(w,"create")));
            Check(!Shown(Find(w,"saveWarning")),"no save warning while saving works");
            warned.warn=Texts.T("local.saveFailed","disk full");Pump(()=>Shown(Find(w,"saveWarning")));
            Check(Find(w,"saveWarning").Text==warned.warn && Find(w,"saveWarning").ForeColor==System.Drawing.Color.Firebrick,"the window shows the session's save warning");
            warned.warn=null;Pump(()=>!Shown(Find(w,"saveWarning")));
            w.Close();
        }
        // An invitation cannot be used while a join is pending.
        var pendingViewer=new FakeSession{role="viewer"};
        using(var w=new CoopWindow(pendingViewer,null,create,list)) {
            w.Show();var items=(ListView)Find(w,"lobbyList");Pump(()=>items.Items.Count==2 && items.Items[0].Tag is LobbyInfo);
            items.Items[0].Selected=true;Pump(()=>Find(w,"lobbyName").Text=="rvgang");
            Click(Find(w,"join"));Pump(()=>pendingViewer.used!=null);
            Find(w,"invite").Text="lscoop:1:wss://relay.example/coop/abc#"+new string('v',32);Click(Find(w,"joinInvite"));
            Check(pendingViewer.invite==null,"an invitation is ignored while a join is pending");
            w.Close();
        }
        // Optional profile section is shown above the lobby.
        var profile=new Label{Name="profileMarker",Text="profile"};
        using(var w=new CoopWindow(new FakeSession(),profile,create,none)) { w.Show();Pump(()=>Shown(Find(w,"profileMarker")));Check(true,"profile section is embedded");w.Close(); }
    }
    static LiveSplit.Model.LiveSplitState NewState() {
        var run=new LiveSplit.Model.Run(new LiveSplit.Model.Comparisons.StandardComparisonGeneratorsFactory());run.Add(new LiveSplit.Model.Segment("Finish"));run.GameName="Game";run.CategoryName="Any%";
        var state=new LiveSplit.Model.LiveSplitState(run,null,new LiveSplit.UI.Layout(),new LiveSplit.Options.LayoutSettings(),new LiveSplit.Options.Settings());
        state.CurrentTimingMethod=LiveSplit.Model.TimingMethod.RealTime;state.CurrentComparison="Personal Best";
        return state;
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
            Check(Until(()=>{component.Update(null,state,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.Connected;}),"component connects automatically on start: "+component.StatusLine);
            Check(Until(()=>{component.Update(null,state,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.StatusLine==grant.name+" · "+Texts.T("status.host")+" · "+Texts.T("status.online",0);}),"host status line shows lobby, role and viewers: "+component.StatusLine);
            Check(component.SavedPassword=="banane","host can read its saved password");
            component.Leave();
            Check(component.Lobby==null && LocalState.Load(path).lobby==null && !component.Connected,"leave forgets the lobby on disk");
            Check(component.StatusLine==Texts.T("status.notConnected"),"status says not in a lobby right after leave: "+component.StatusLine);
            component.Update(null,state,200,22,LiveSplit.UI.LayoutMode.Vertical);
            Check(component.StatusLine==Texts.T("status.notConnected"),"status stays at not in a lobby after the next update: "+component.StatusLine);
        }
        // Viewer joins are unconfirmed: nothing is saved until the relay accepted them.
        string joinPath=Path.Combine(work,"join-local.json");
        if(File.Exists(joinPath)) File.Delete(joinPath);
        var joinState=NewState();
        using(var component=new CoopComponent(joinState,joinPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>viewer</Role>"));
            var wrong=new LobbyGrant { server=relay,name=grant.name,roomId=grant.roomId,viewerKey=LobbyKey.DeriveViewerKey(grant.name,"falsch"),locked=true };
            Check(component.UseLobby(wrong,null)==null && component.Lobby!=null,"a viewer join is held in memory first");
            Check(!File.Exists(joinPath) || LocalState.Load(joinPath).lobby==null,"an unconfirmed join is not on disk");
            Check(Until(()=>{component.Update(null,joinState,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.Lobby==null;}),"a rejected join is forgotten by the component");
            Check(component.LastFailure=="auth_failed" && (!File.Exists(joinPath) || LocalState.Load(joinPath).lobby==null),"the failure is recorded and nothing was saved");
            Check(component.StatusLine==Texts.T("status.notConnected"),"status is back to not in a lobby: "+component.StatusLine);
            var right=new LobbyGrant { server=relay,name=grant.name,roomId=grant.roomId,viewerKey=grant.viewerKey,locked=true };
            component.UseLobby(right,null);
            Check(component.LastFailure==null,"a new join clears the last failure");
            Check(Until(()=>{component.Update(null,joinState,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.Connected;}),"a correct join connects");
            Check(Until(()=>{component.Update(null,joinState,200,22,LiveSplit.UI.LayoutMode.Vertical);var saved=LocalState.Load(joinPath).lobby;return saved!=null && saved.roomId==grant.roomId;}),"a confirmed join is saved");
            component.Leave();
            var invalid=new LobbyGrant { server="ws://example.com/coop",name="abc",roomId="lobby_abc",viewerKey=new string('v',43) };
            Check(component.UseLobby(invalid,null)==null && component.Lobby==null && component.LastFailure=="rejected","a join that cannot even start is forgotten with the rejected code");
            Check(component.StatusLine==Texts.T("status.notConnected") && LocalState.Load(joinPath).lobby==null,"nothing is shown or stored for it");
            component.Leave();
            Check(component.LastFailure==null,"leave clears the last failure");
        }
        // A host lobby that cannot be saved still works, and the caller is warned.
        string blocker=Path.Combine(work,"not-a-folder");File.WriteAllText(blocker,"x");
        using(var component=new CoopComponent(NewState(),Path.Combine(blocker,"coop-local.json"))) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            string warning=component.UseLobby(grant,"banane");
            Check(warning!=null && warning.StartsWith(Texts.T("lobby.saveFailed","").Substring(0,20)) && component.Lobby!=null,"a failed save is reported while the lobby stays usable: "+warning);
        }
        // Sharing: named host lobbies share name and password, legacy host rooms their invitation.
        string sharePath=Path.Combine(work,"share-local.json");
        if(File.Exists(sharePath)) File.Delete(sharePath);
        using(var component=new CoopComponent(NewState(),sharePath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            component.UseLobby(grant,"banane");
            Check(component.ShareText==CoopWindow.Info(grant.name,"banane") && !component.ShareIsInvite,"a named host lobby shares name and password");
            Check(component.LastLobbyName==null,"no last lobby while in one");
            component.Leave();
            Check(component.ShareText==null && component.LastLobbyName==grant.name,"after leaving the host lobby can be reopened");
            Check(LocalState.Load(sharePath).lastHostLobby!=null && LocalState.Load(sharePath).lobby==null,"the last host lobby is kept on disk, still protected");
            Check(!File.ReadAllText(sharePath).Contains(grant.hostKey),"the kept host key is not plain text");
        }
        var reopenState=NewState();
        using(var component=new CoopComponent(reopenState,sharePath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            Check(component.LastLobbyName==grant.name,"the last host lobby survives a restart");
            component.ReopenLast();
            Check(component.Lobby!=null && component.Lobby.name==grant.name && component.LastLobbyName==null && component.ShareText==CoopWindow.Info(grant.name,"banane"),"reopen restores lobby and password");
            Check(Until(()=>{component.Update(null,reopenState,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.Connected;}),"a reopened lobby connects");
            Check(LocalState.Load(sharePath).lobby!=null && LocalState.Load(sharePath).lastHostLobby==null,"reopen is persisted");
        }
        File.Delete(sharePath);
        using(var component=new CoopComponent(NewState(),sharePath)) {
            component.SetSettings(Xml("<Version>1</Version><Address>wss://relay.example/coop</Address><Room>oldroom</Room><Role>host</Role><ProtectedKey>"+LocalState.Protect(new string('k',43))+"</ProtectedKey><ProtectedInviteKey>"+LocalState.Protect(new string('i',43))+"</ProtectedInviteKey>"));
            Check(component.ShareIsInvite && component.ShareText=="lscoop:1:wss://relay.example/coop/oldroom#"+new string('i',43),"a legacy host room shares its invitation");
            component.Leave();
            Check(component.LastLobbyName==null,"a nameless legacy room is not offered for reopening");
        }
        string share2=Path.Combine(work,"share2-local.json");if(File.Exists(share2)) File.Delete(share2);
        using(var component=new CoopComponent(NewState(),share2)) {
            component.SetSettings(Xml("<Version>1</Version><Address>wss://relay.example/coop</Address><Room>oldroom2</Room><Role>host</Role><ProtectedKey>"+LocalState.Protect(new string('k',43))+"</ProtectedKey><ProtectedInviteKey></ProtectedInviteKey>"));
            Check(component.ShareText==null,"a legacy host room without an invitation key has nothing to share");
        }
        // Connect records the specific reason, and the status keeps it.
        var asState=NewState();asState.Layout.LayoutComponents.Add(new LiveSplit.UI.Components.LayoutComponent("x.dll",new FakeAutoSplit()));
        string asPath=Path.Combine(work,"as-local.json");if(File.Exists(asPath)) File.Delete(asPath);
        using(var component=new CoopComponent(asState,asPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>viewer</Role>"));
            var viaAs=new LobbyGrant { server=relay,name=grant.name,roomId=grant.roomId,viewerKey=grant.viewerKey,locked=true };
            Check(component.UseLobby(viaAs,null)==null && component.Lobby==null && component.LastFailure=="autosplitter","an autosplitter blocks the join with its own code: "+component.LastFailure);
            Check(component.StatusLine==Texts.T("status.autosplitter"),"the autosplitter status is kept: "+component.StatusLine);
            Check(CoopWindow.Explain("autosplitter")==Texts.T("status.autosplitter") && CoopWindow.Explain("key_unreadable")==Texts.T("lobby.keyUnreadable"),"the new codes are explained");
        }
        string keyPath=Path.Combine(work,"key-local.json");
        var broken=LobbyState.From(grant,"viewer",null);broken.protectedViewerKey="AAAA";
        new LocalState { lobby=broken }.Save(keyPath);
        var keyState=NewState();
        using(var component=new CoopComponent(keyState,keyPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>viewer</Role>"));
            component.Update(null,keyState,200,22,LiveSplit.UI.LayoutMode.Vertical);
            Check(component.LastFailure=="key_unreadable" && component.StatusLine==Texts.T("lobby.keyUnreadable"),"an unreadable key is recorded and shown: "+component.LastFailure+" / "+component.StatusLine);
        }
        // Save failures are never silent: leave, reopen and the viewer confirmation report them too.
        string stuck=Path.Combine(work,"stuck-folder");File.WriteAllText(stuck,"x");
        string stuckPath=Path.Combine(stuck,"coop-local.json");
        using(var component=new CoopComponent(NewState(),stuckPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            Check(component.SaveWarning==null,"no warning before anything failed");
            component.UseLobby(grant,"banane");
            Check(component.SaveWarning!=null && component.SaveWarning.StartsWith(Texts.T("local.saveFailed","").Substring(0,20)),"a failed save leaves a warning: "+component.SaveWarning);
            component.Leave();component.ReopenLast();
            Check(component.Lobby!=null && component.SaveWarning!=null,"reopen keeps reporting the failed save");
        }
        using(var component=new CoopComponent(NewState(),stuckPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            component.UseLobby(grant,"banane");
            component.Leave();
            Check(component.SaveWarning!=null,"leaving with an unwritable file warns");
        }
        var stuckViewer=NewState();
        using(var component=new CoopComponent(stuckViewer,stuckPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>viewer</Role>"));
            component.UseLobby(new LobbyGrant { server=relay,name=grant.name,roomId=grant.roomId,viewerKey=grant.viewerKey,locked=true },null);
            Check(component.SaveWarning==null,"an unconfirmed join has not tried to save yet");
            Check(Until(()=>{component.Update(null,stuckViewer,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.Connected && component.SaveWarning!=null;}),"a viewer join that cannot be saved warns after confirmation");
            Check(component.Lobby!=null,"the confirmed lobby keeps working");
        }
        string okPath=Path.Combine(work,"ok-local.json");if(File.Exists(okPath)) File.Delete(okPath);
        using(var component=new CoopComponent(NewState(),okPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            component.UseLobby(grant,"banane");
            Check(component.SaveWarning==null,"a working save has no warning");
        }
        // A role change forgets an unconfirmed lobby of the old role instead of orphaning it.
        string orphanPath=Path.Combine(work,"orphan-local.json");if(File.Exists(orphanPath)) File.Delete(orphanPath);
        var orphanState=NewState();
        using(var component=new CoopComponent(orphanState,orphanPath)) {
            component.SetSettings(Xml("<Version>2</Version><Role>viewer</Role>"));
            component.UseLobby(new LobbyGrant { server=relay,name=grant.name,roomId=grant.roomId,viewerKey=grant.viewerKey,locked=true },null);
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            component.Update(null,orphanState,200,22,LiveSplit.UI.LayoutMode.Vertical);
            component.SetSettings(Xml("<Version>2</Version><Role>viewer</Role>"));
            component.Update(null,orphanState,200,22,LiveSplit.UI.LayoutMode.Vertical);
            Check(component.Lobby==null && !component.Connected && (!File.Exists(orphanPath) || LocalState.Load(orphanPath).lobby==null),"a role change forgets the unconfirmed lobby");
        }
        // A host copy locks the splits it has loaded, and lets go again.
        string lockedSplits=Path.Combine(work,"locked.lss");File.WriteAllText(lockedSplits,"x");
        var lockState=NewState();lockState.Run.FilePath=lockedSplits;
        using(var component=new CoopComponent(lockState,Path.Combine(work,"lock-local.json"))) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            Check(!HostLock.IsHeld(lockedSplits),"nothing is locked before the first update");
            component.Update(null,lockState,200,22,LiveSplit.UI.LayoutMode.Vertical);
            Check(HostLock.IsHeld(lockedSplits),"a host copy locks its open splits");
        }
        Check(!HostLock.IsHeld(lockedSplits) && !File.Exists(HostLock.PathFor(lockedSplits)),"disposing the host copy releases the lock");
        // A role change in the layout settings drops the connection of the old role.
        new LocalState { lobby=LobbyState.From(grant,"host","banane") }.Save(path);
        var roleState=NewState();
        using(var component=new CoopComponent(roleState,path)) {
            component.SetSettings(Xml("<Version>2</Version><Role>host</Role>"));
            Check(Until(()=>{component.Update(null,roleState,200,22,LiveSplit.UI.LayoutMode.Vertical);return component.Connected;}),"host connects before the role change");
            component.SetSettings(Xml("<Version>2</Version><Role>viewer</Role>"));
            component.Update(null,roleState,200,22,LiveSplit.UI.LayoutMode.Vertical);
            Check(!component.Connected && component.Viewers==-1 && component.StatusLine==Texts.T("status.notConnected"),"changing the role disconnects: "+component.StatusLine);
        }
        File.Delete(path);
        var migrated=NewState();
        using(var component=new CoopComponent(migrated,path)) {
            component.SetSettings(Xml("<Version>1</Version><Address>wss://relay.example/coop</Address><Room>oldroom</Room><Role>viewer</Role><ProtectedKey>"+LocalState.Protect(new string('k',43))+"</ProtectedKey><ProtectedInviteKey></ProtectedInviteKey>"));
            var saved=LocalState.Load(path).lobby;
            Check(saved!=null && saved.roomId=="oldroom" && saved.role=="viewer" && saved.name==null,"legacy viewer room migrates into coop-local.json");
            Check(component.StatusLine.StartsWith(Texts.T("status.inviteRoom")),"migrated rooms are labelled as invitation rooms: "+component.StatusLine);
            component.Leave();
        }
        using(var component=new CoopComponent(NewState(),path)) {
            component.SetSettings(Xml("<Version>1</Version><Address>wss://relay.example/coop</Address><Room>oldroom</Room><Role>viewer</Role><ProtectedKey>"+LocalState.Protect(new string('k',43))+"</ProtectedKey><ProtectedInviteKey></ProtectedInviteKey>"));
            Check(component.Lobby==null && LocalState.Load(path).lobby==null,"a legacy room that was left is not migrated again");
        }
        using(var component=new CoopComponent(NewState(),path))
            Check(component.ContextMenuControls.Keys.SequenceEqual(new[]{Texts.T("menu.coop")}),"the context menu has a single Coop entry");
    }
}
