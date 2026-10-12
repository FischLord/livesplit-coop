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
    static int passed;
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);passed++;Console.WriteLine("PASS: "+text);}
    static void Throws<T>(Action action,string text) where T:Exception {try{action();}catch(T){Check(true,text);return;}Check(false,text);}
    public static void Run(string relay,string work) {
        Texts.German=false;
        KeyTests();
        TextTests();
        ClientTests(relay);
        StateTests(work);
        ConnectionTests(relay);
        WindowTests(work);
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
        public string role="host",server="wss://relay.example/coop",stopReason,password,problem;
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
        public void UseLobby(LobbyGrant grant,string pw) { used=grant;usedPassword=pw;lobby=LobbyState.From(grant,role,pw);password=role=="host"?pw:null; }
        public bool JoinInvite(string text) { string s,r,k;if(!Invite.TryParse(text,out s,out r,out k))return false;invite=text;lobby=LobbyState.FromLegacy(s,r,"viewer",k,"");return true; }
        public void Reconnect() { }
        public void Leave() { leaves++;lobby=null;connected=false;stopReason=null; }
        public void ExportDiagnostics(IWin32Window owner) { }
    }
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static Control Find(Control parent,string name){foreach(Control c in parent.Controls){if(c.Name==name)return c;var child=Find(c,name);if(child!=null)return child;}return null;}
    static void Click(Control c){c.GetType().GetMethod("OnClick",Hidden,null,new[]{typeof(EventArgs)},null).Invoke(c,new object[]{EventArgs.Empty});}
    static void Pump(Func<bool> ready){var clock=Stopwatch.StartNew();while(!ready() && clock.ElapsedMilliseconds<8000){Application.DoEvents();Thread.Sleep(10);}if(!ready())throw new Exception("Window timed out");}
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
            Check(Find(w,"message").Text==Texts.T("lobby.created","rvgang"),"host is told the lobby was created");
            Check(Shown(Find(w,"copyPassword")),"host can copy name and password");
            Click(Find(w,"leave"));Pump(()=>Shown(Find(w,"create")));
            Check(host.leaves==1 && host.lobby==null,"leave clears the lobby");
            w.Close();
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
            viewer.problem=null;Find(w,"lobbyPassword").Text="falsch";Click(Find(w,"join"));
            Check(viewer.used!=null && viewer.used.roomId=="lobby_rvgang" && viewer.used.viewerKey==LobbyKey.DeriveViewerKey("rvgang","falsch") && viewer.usedPassword==null,"join derives the key locally and never hands over the password");
            viewer.stopReason="auth_failed";
            Pump(()=>Find(w,"message").Text==Texts.T("lobby.wrongPassword"));
            Check(viewer.leaves==1 && viewer.lobby==null && Shown(Find(w,"join")),"wrong password is explained and the lobby is forgotten");
            viewer.used=null;Click(Find(w,"join"));Pump(()=>viewer.used!=null);viewer.connected=true;
            Pump(()=>Shown(Find(w,"leave")));
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
        // Optional profile section is shown above the lobby.
        var profile=new Label{Name="profileMarker",Text="profile"};
        using(var w=new CoopWindow(new FakeSession(),profile,create,none)) { w.Show();Pump(()=>Shown(Find(w,"profileMarker")));Check(true,"profile section is embedded");w.Close(); }
    }
}
