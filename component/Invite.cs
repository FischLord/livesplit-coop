using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveSplit.Coop {
    public sealed class RoomGrant {
        public string server,room,hostKey,viewerKey;
    }
    // An invite carries everything a viewer needs in one pasteable line: lscoop:1:<server>/<room>#<viewer key>.
    // It is a credential like the key itself; it never contains the host key.
    public static class Invite {
        public const string Community="wss://livesplit-coop-relay.janneck.workers.dev/coop";
        const string Prefix="lscoop:1:";
        static readonly Regex Shape=new Regex("^(wss?://[^/?#@\\s]+/coop)/([a-zA-Z0-9_-]{1,64})#([^\\s#]{32,256})$");
        public static string Format(string server,string room,string viewerKey) {
            var text=Prefix+server.TrimEnd('/')+"/"+room+"#"+viewerKey;
            string s,r,k;if(!TryParse(text,out s,out r,out k)) throw new ArgumentException("Server, room or viewer key cannot form an invite.");
            return text;
        }
        public static bool TryParse(string text,out string server,out string room,out string key) {
            server=room=key=null;
            if(text==null) return false;text=text.Trim();
            if(!text.StartsWith(Prefix,StringComparison.Ordinal)) return false;
            var m=Shape.Match(text.Substring(Prefix.Length));if(!m.Success) return false;
            // The same rules RelayConnection enforces (wss, or ws only on loopback).
            try { new RelayConnection(m.Groups[1].Value,m.Groups[2].Value,"viewer",m.Groups[3].Value).Dispose(); } catch(ArgumentException) { return false; } catch(UriFormatException) { return false; }
            server=m.Groups[1].Value;room=m.Groups[2].Value;key=m.Groups[3].Value;return true;
        }
        // POST <server>/rooms on the relay behind a wss://host/coop address. Blocking; call it off the UI thread.
        public static RoomGrant Create(string server) {
            new RelayConnection(server,"probe","host",new string('x',32)).Dispose(); // validates the address
            var uri=new Uri(server);
            var endpoint=new UriBuilder(uri) { Scheme=uri.Scheme=="wss"?"https":"http",Path="/rooms",Port=uri.IsDefaultPort?-1:uri.Port }.Uri;
            var request=(HttpWebRequest)WebRequest.Create(endpoint);
            request.Method="POST";request.ContentLength=0;request.Timeout=15000;request.ReadWriteTimeout=15000;request.AllowAutoRedirect=false;
            string body;
            try {
                using(var response=(HttpWebResponse)request.GetResponse())
                using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) {
                    if(response.StatusCode!=HttpStatusCode.Created) throw new InvalidOperationException("The server answered "+(int)response.StatusCode+" instead of creating a room.");
                    body=reader.ReadToEnd();
                }
            } catch(WebException e) {
                var response=e.Response as HttpWebResponse;
                if(response==null) throw new InvalidOperationException("Could not reach the server: "+e.Message);
                int code=(int)response.StatusCode;response.Close();
                if(code==404) throw new InvalidOperationException("This server does not create rooms. Ask its operator for a room and keys.");
                if(code==429) throw new InvalidOperationException("Too many new rooms from here. Wait a minute and try again.");
                if(code==503) throw new InvalidOperationException("The server is not creating rooms right now (daily limit). Try again later.");
                throw new InvalidOperationException("The server refused to create a room ("+code+").");
            }
            var data=RelayConnection.Json().Deserialize<Dictionary<string,object>>(body);
            object room,hostKey,viewerKey;
            if(data==null || !data.TryGetValue("roomId",out room) || !data.TryGetValue("hostKey",out hostKey) || !data.TryGetValue("viewerKey",out viewerKey))
                throw new InvalidOperationException("The server sent an unexpected answer.");
            var grant=new RoomGrant { server=server,room=room as string,hostKey=hostKey as string,viewerKey=viewerKey as string };
            string s,r,k;
            if(grant.hostKey==null || grant.hostKey.Length<32 || grant.hostKey.Length>256 || grant.hostKey==grant.viewerKey ||
               grant.room==null || grant.viewerKey==null || !TryParse(Prefix+server+"/"+grant.room+"#"+grant.viewerKey,out s,out r,out k))
                throw new InvalidOperationException("The server sent an invalid room.");
            return grant;
        }
    }
}
