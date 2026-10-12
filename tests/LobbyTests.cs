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
}
