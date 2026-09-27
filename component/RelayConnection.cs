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
        // False when only the clock changed since the last taken delivery.
        public bool structural;
    }
    public sealed class RelayConnection : IDisposable {
        public const int Protocol=2;
        const int Replaced=4001;
        readonly Uri uri;
        readonly string room,role,key;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly object gate=new object();
        ClientWebSocket socket;
        string pendingSnapshot,pendingTick;
        Delivery incoming;
        long lastMessage;
        volatile int generation;
        volatile bool ready,hostOnline,replaced;
        volatile string status="Connecting";
        public bool Ready { get { return ready; } }
        public bool HostOnline { get { return hostOnline; } }
        public string Status { get { return status; } }
        // Increases with every accepted connection; a new connection needs a complete snapshot first.
        public int Generation { get { return generation; } }
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
        public void Publish(Snapshot s) { Publish(s,true,generation); }
        // Returns false when the connection changed since the caller read Generation; nothing is queued then.
        public bool Publish(Snapshot s,bool complete,int forGeneration) {
            string json=complete?Json().Serialize(s):Json().Serialize(new { type="tick",v=Protocol,seq=s.seq,realTicks=s.realTicks,gameTicks=s.gameTicks,gamePaused=s.gamePaused });
            if(Encoding.UTF8.GetByteCount(json)>262144) throw new ArgumentException("Run exceeds relay message limit.");
            lock(gate) {
                if(forGeneration!=generation) return false;
                if(complete) { pendingSnapshot=json;pendingTick=null; } else pendingTick=json;
            }
            return true;
        }
        public Delivery Take() { lock(gate) { var result=incoming;incoming=null;return result; } }
        void Deliver(Delivery d) { lock(gate) { if(incoming!=null && incoming.structural) d.structural=true;incoming=d; } }
        async Task Send(ClientWebSocket ws,string json,CancellationToken ct) {
            var bytes=Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,ct).ConfigureAwait(false);
        }
        async Task Writer(ClientWebSocket ws,CancellationToken ct) {
            DateTime lastPing=DateTime.UtcNow;
            while(!ct.IsCancellationRequested && ws.State==WebSocketState.Open) {
                // The relay answers every ping; silence means a dead path even if TCP has not noticed yet.
                if(DateTime.UtcNow.Ticks-Interlocked.Read(ref lastMessage)>TimeSpan.FromSeconds(ready?15:10).Ticks) throw new TimeoutException();
                string json=null;
                // A queued snapshot always goes before a later tick; one message per pass stays under the relay rate limit.
                if(ready) lock(gate) { if(pendingSnapshot!=null) { json=pendingSnapshot;pendingSnapshot=null; } else { json=pendingTick;pendingTick=null; } }
                if(json!=null) await Send(ws,json,ct).ConfigureAwait(false);
                if(ready && DateTime.UtcNow-lastPing>TimeSpan.FromSeconds(5)) { await Send(ws,"{\"type\":\"ping\"}",ct).ConfigureAwait(false);lastPing=DateTime.UtcNow; }
                await Task.Delay(40,ct).ConfigureAwait(false);
            }
        }
        static long? Ticks(object value) { return value==null?(long?)null:Convert.ToInt64(value); }
        async Task Reader(ClientWebSocket ws,CancellationToken ct) {
            var buffer=new byte[8192];
            Snapshot current=null;
            while(!ct.IsCancellationRequested && ws.State==WebSocketState.Open) {
                using(var stream=new MemoryStream()) {
                    WebSocketReceiveResult read;
                    do {
                        read=await ws.ReceiveAsync(new ArraySegment<byte>(buffer),ct).ConfigureAwait(false);
                        if(read.MessageType==WebSocketMessageType.Close) {
                            if(read.CloseStatus.HasValue && (int)read.CloseStatus.Value==Replaced) replaced=true;
                            status="Disconnected ("+(read.CloseStatusDescription ?? "server closed")+")";return;
                        }
                        if(read.MessageType!=WebSocketMessageType.Text || stream.Length+read.Count>262144) throw new InvalidDataException();
                        stream.Write(buffer,0,read.Count);
                    } while(!read.EndOfMessage);
                    Interlocked.Exchange(ref lastMessage,DateTime.UtcNow.Ticks);
                    var json=Json();var msg=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(stream.ToArray()));
                    object kind;if(msg==null || !msg.TryGetValue("type",out kind)) throw new InvalidDataException();
                    if((string)kind=="ready") { lock(gate) generation++;ready=true;status="Connected"; }
                    else if((string)kind=="host") hostOnline=Convert.ToBoolean(msg["online"]);
                    else if((string)kind=="state") {
                        var value=json.ConvertToType<Snapshot>(msg["snapshot"]);
                        if(!value.Valid()) throw new InvalidDataException();
                        bool live=Convert.ToBoolean(msg["live"]);
                        current=value;hostOnline=live;
                        Deliver(new Delivery { snapshot=value,live=live,ageMs=Math.Max(0,Convert.ToInt64(msg["ageMs"])),structural=true });
                    }
                    else if((string)kind=="tick" && current!=null) {
                        var value=current.WithTimes(Convert.ToInt64(msg["seq"]),Ticks(msg["realTicks"]),Ticks(msg["gameTicks"]),Convert.ToBoolean(msg["gamePaused"]));
                        if(!value.Valid()) throw new InvalidDataException();
                        current=value;hostOnline=true;
                        Deliver(new Delivery { snapshot=value,live=true,ageMs=0 });
                    }
                }
            }
        }
        async Task Loop() {
            while(!cancel.IsCancellationRequested) {
                ready=false;hostOnline=false;
                // Never replay a snapshot queued on a previous connection.
                lock(gate) { pendingSnapshot=null;pendingTick=null;incoming=null; }
                using(var ws=new ClientWebSocket())
                using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token)) {
                    socket=ws;
                    try {
                        status="Connecting";
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(linked.Token)) {
                            timeout.CancelAfter(10000);
                            await ws.ConnectAsync(uri,timeout.Token).ConfigureAwait(false);
                        }
                        Interlocked.Exchange(ref lastMessage,DateTime.UtcNow.Ticks);
                        await Send(ws,Json().Serialize(new { type="hello",v=Protocol,room=room,role=role,key=key }),linked.Token).ConfigureAwait(false);
                        var reader=Reader(ws,linked.Token);var writer=Writer(ws,linked.Token);
                        var first=await Task.WhenAny(reader,writer).ConfigureAwait(false);
                        linked.Cancel();ws.Abort();
                        if(first.IsFaulted) status=first.Exception.InnerException is TimeoutException?"Relay not responding; retrying":"Connection lost; retrying";
                        try { await Task.WhenAll(reader,writer).ConfigureAwait(false); } catch(Exception) { }
                    } catch(OperationCanceledException) { }
                    catch(Exception) { status="Connection lost; retrying"; }
                    finally { ready=false;hostOnline=false;socket=null; }
                }
                // Two hosts would otherwise keep replacing each other.
                if(replaced) { status="Stopped: another host connection took over this room";return; }
                try { await Task.Delay(3000,cancel.Token).ConfigureAwait(false); } catch(OperationCanceledException) { }
            }
        }
        public void Dispose() { cancel.Cancel();var ws=socket;if(ws!=null) ws.Abort();ready=false;hostOnline=false; }
    }
}
