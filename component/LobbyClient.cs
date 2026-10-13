using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveSplit.Coop {
    public sealed class LobbyGrant {
        public string server,name,roomId,hostKey,viewerKey;
        public bool isPublic,locked;
    }
    public sealed class LobbyInfo {
        public string name,game,category,label;
        public bool locked,hostOnline;
        public int viewers;
    }
    // Carries a message that can be shown to the user as is.
    public sealed class LobbyException : Exception { public LobbyException(string message) : base(message) { } }
    // HTTP side of lobbies (POST/GET /lobbies on the relay behind a wss://host/coop address). Blocking; call off the UI thread.
    public static class LobbyClient {
        static readonly Regex KeyShape=new Regex("^[A-Za-z0-9_-]{32,256}$");
        public static bool IsValidServer(string server) {
            if(string.IsNullOrEmpty(server)) return false;
            try { Endpoint(server,"/lobbies");return true; } catch(ArgumentException) { return false; } catch(UriFormatException) { return false; }
        }
        public static Uri Endpoint(string server,string path) {
            new RelayConnection(server,"probe","host",new string('x',32)).Dispose(); // validates the address (wss, or ws on loopback)
            var uri=new Uri(server);
            return new UriBuilder(uri) { Scheme=uri.Scheme=="wss"?"https":"http",Path=path,Port=uri.IsDefaultPort?-1:uri.Port }.Uri;
        }
        public static LobbyGrant Create(string server,string name,string password,bool isPublic) {
            string normalized=LobbyKey.Normalize(name);
            if(normalized==null) throw new LobbyException(Texts.T("lobby.invalidName"));
            password=password ?? "";
            string viewerKey=LobbyKey.DeriveViewerKey(normalized,password);
            var body=RelayConnection.Json().Serialize(new Dictionary<string,object> { {"name",normalized},{"viewerKey",viewerKey},{"public",isPublic},{"locked",password.Length>0} });
            int status;string answer=Send(Endpoint(server,"/lobbies"),"POST",body,out status);
            if(status==409) throw new LobbyException(Texts.T("lobby.taken"));
            if(status!=201) throw Refused(status);
            var data=Parse(answer);
            object roomId,hostKey;
            if(data==null || !data.TryGetValue("roomId",out roomId) || !data.TryGetValue("hostKey",out hostKey) ||
               (roomId as string)!=LobbyKey.RoomId(normalized) || !(hostKey is string) || !KeyShape.IsMatch((string)hostKey) || (string)hostKey==viewerKey)
                throw new LobbyException(Texts.T("lobby.badAnswer"));
            return new LobbyGrant { server=server,name=normalized,roomId=(string)roomId,hostKey=(string)hostKey,viewerKey=viewerKey,isPublic=isPublic,locked=password.Length>0 };
        }
        public static List<LobbyInfo> List(string server) {
            int status;string answer=Send(Endpoint(server,"/lobbies"),"GET",null,out status);
            if(status!=200) throw Refused(status);
            var data=Parse(answer);object raw;
            if(data==null || !data.TryGetValue("lobbies",out raw) || !(raw is IEnumerable)) throw new LobbyException(Texts.T("lobby.badAnswer"));
            var result=new List<LobbyInfo>();
            foreach(var item in (IEnumerable)raw) {
                var entry=item as Dictionary<string,object>;
                if(entry==null) continue;
                string name=Text(entry,"name");
                if(LobbyKey.Normalize(name)!=name) continue;
                result.Add(new LobbyInfo { name=name,game=Text(entry,"game"),category=Text(entry,"category"),label=Text(entry,"label"),
                    locked=Flag(entry,"locked"),hostOnline=Flag(entry,"hostOnline"),viewers=Count(entry,"viewers") });
            }
            return result;
        }
        static string Text(Dictionary<string,object> d,string key) { object v;return d.TryGetValue(key,out v) && v is string && ((string)v).Length<=200?(string)v:null; }
        static bool Flag(Dictionary<string,object> d,string key) { object v;return d.TryGetValue(key,out v) && v is bool && (bool)v; }
        static int Count(Dictionary<string,object> d,string key) { object v;return d.TryGetValue(key,out v) && v is int && (int)v>=0?(int)v:0; }
        static Dictionary<string,object> Parse(string text) {
            try { return RelayConnection.Json().Deserialize<Dictionary<string,object>>(text); }
            catch(ArgumentException) { return null; } catch(InvalidOperationException) { return null; }
        }
        static LobbyException Refused(int status) {
            if(status==404) return new LobbyException(Texts.T("lobby.unsupported"));
            if(status==429) return new LobbyException(Texts.T("lobby.rateLimited"));
            if(status==503) return new LobbyException(Texts.T("lobby.dailyLimit"));
            return new LobbyException(Texts.T("lobby.refused",status));
        }
        // Returns the body for any HTTP status; only a missing answer (DNS, refused, timeout) throws.
        static string Send(Uri uri,string method,string body,out int status) {
            var request=(HttpWebRequest)WebRequest.Create(uri);
            request.Method=method;request.Timeout=15000;request.ReadWriteTimeout=15000;request.AllowAutoRedirect=false;
            try {
                if(body!=null) {
                    var bytes=Encoding.UTF8.GetBytes(body);
                    request.ContentType="application/json";request.ContentLength=bytes.Length;
                    using(var stream=request.GetRequestStream()) stream.Write(bytes,0,bytes.Length);
                }
                using(var response=(HttpWebResponse)request.GetResponse()) { status=(int)response.StatusCode;return Read(response); }
            } catch(WebException e) {
                var response=e.Response as HttpWebResponse;
                if(response==null) throw new LobbyException(Texts.T("lobby.unreachable",e.Message));
                using(response) { status=(int)response.StatusCode;return Read(response); }
            }
        }
        static string Read(HttpWebResponse response) {
            using(var reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) {
                var buffer=new char[65536];int length=reader.ReadBlock(buffer,0,buffer.Length);
                return new string(buffer,0,length);
            }
        }
    }
}
