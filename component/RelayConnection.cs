using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LiveSplit.Coop {
    public sealed class Delivery {
        public Snapshot snapshot;
        public bool live;
        public long ageMs;
    }
    public sealed class RelayConnection : IDisposable {
        readonly Uri uri;
        readonly string room,role,key;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly object gate=new object();
        ClientWebSocket socket;
        string outgoing;
        Delivery incoming;
        volatile bool ready,hostOnline;
        volatile string status="Connecting";
        public bool Ready { get { return ready; } }
        public bool HostOnline { get { return hostOnline; } }
        public string Status { get { return status; } }
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=262144,RecursionLimit=24 }; }
        public RelayConnection(string url,string room,string role,string key) {
            uri=new Uri(url);
            if((uri.Scheme!="wss" && !(uri.Scheme=="ws" && uri.IsLoopback)) || uri.AbsolutePath!="/coop" ||
               !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Use wss://your-server/coop (ws:// only on loopback).");
            if(!System.Text.RegularExpressions.Regex.IsMatch(room,"^[a-zA-Z0-9_-]{1,64}$") ||
               (role!="host" && role!="viewer") || key.Length<32 || key.Length>256) throw new ArgumentException("Invalid room, role or access key.");
            this.room=room;this.role=role;this.key=key;
        }
        public void Start() { Task.Run((Func<Task>)Loop); }
        public void Publish(Snapshot s) { string json=Json().Serialize(s);if(Encoding.UTF8.GetByteCount(json)>262144) throw new ArgumentException("Run exceeds relay message limit.");lock(gate) outgoing=json; }
        public Delivery Take() { lock(gate) { var result=incoming;incoming=null;return result; } }
        async Task Send(ClientWebSocket ws,string json,CancellationToken ct) {
            var bytes=Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,ct).ConfigureAwait(false);
        }
        async Task Writer(ClientWebSocket ws,CancellationToken ct) {
            DateTime lastPing=DateTime.UtcNow;
            while(!ct.IsCancellationRequested && ws.State==WebSocketState.Open) {
                string json=null;
                if(ready) lock(gate) { json=outgoing;outgoing=null; }
                if(json!=null) await Send(ws,json,ct).ConfigureAwait(false);
                if(ready && DateTime.UtcNow-lastPing>TimeSpan.FromSeconds(5)) { await Send(ws,"{\"type\":\"ping\"}",ct).ConfigureAwait(false);lastPing=DateTime.UtcNow; }
                await Task.Delay(30,ct).ConfigureAwait(false);
            }
        }
        async Task Reader(ClientWebSocket ws,CancellationToken ct) {
            var buffer=new byte[8192];
            while(!ct.IsCancellationRequested && ws.State==WebSocketState.Open) {
                using(var stream=new MemoryStream()) {
                    WebSocketReceiveResult read;
                    do {
                        read=await ws.ReceiveAsync(new ArraySegment<byte>(buffer),ct).ConfigureAwait(false);
                        if(read.MessageType==WebSocketMessageType.Close) { status="Disconnected ("+(read.CloseStatusDescription ?? "server closed")+")";return; }
                        if(read.MessageType!=WebSocketMessageType.Text || stream.Length+read.Count>262144) throw new InvalidDataException();
                        stream.Write(buffer,0,read.Count);
                    } while(!read.EndOfMessage);
                    var json=Json();var msg=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(stream.ToArray()));
                    object kind;if(msg==null || !msg.TryGetValue("type",out kind)) throw new InvalidDataException();
                    if((string)kind=="ready") { ready=true;status="Connected"; }
                    else if((string)kind=="host") hostOnline=Convert.ToBoolean(msg["online"]);
                    else if((string)kind=="state") {
                        var value=json.ConvertToType<Snapshot>(msg["snapshot"]);
                        if(!value.Valid()) throw new InvalidDataException();
                        bool live=Convert.ToBoolean(msg["live"]);
                        var delivery=new Delivery { snapshot=value,live=live,ageMs=Math.Max(0,Convert.ToInt64(msg["ageMs"])) };
                        hostOnline=live;
                        lock(gate) incoming=delivery;
                    }
                }
            }
        }
        async Task Loop() {
            while(!cancel.IsCancellationRequested) {
                ready=false;hostOnline=false;
                // Never replay a snapshot queued on a previous connection.
                lock(gate) { outgoing=null;incoming=null; }
                using(var ws=new ClientWebSocket())
                using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token)) {
                    socket=ws;
                    try {
                        status="Connecting";
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(linked.Token)) {
                            timeout.CancelAfter(10000);
                            await ws.ConnectAsync(uri,timeout.Token).ConfigureAwait(false);
                        }
                        await Send(ws,Json().Serialize(new { type="hello",v=1,room=room,role=role,key=key }),linked.Token).ConfigureAwait(false);
                        var reader=Reader(ws,linked.Token);var writer=Writer(ws,linked.Token);
                        await Task.WhenAny(reader,writer).ConfigureAwait(false);
                        linked.Cancel();ws.Abort();
                        try { await Task.WhenAll(reader,writer).ConfigureAwait(false); } catch(OperationCanceledException) { }
                    } catch(OperationCanceledException) { }
                    catch(Exception) { status="Connection lost; retrying"; }
                    finally { ready=false;hostOnline=false;socket=null; }
                }
                try { await Task.Delay(3000,cancel.Token).ConfigureAwait(false); } catch(OperationCanceledException) { }
            }
        }
        public void Dispose() { cancel.Cancel();var ws=socket;if(ws!=null) ws.Abort();ready=false;hostOnline=false; }
    }
}
