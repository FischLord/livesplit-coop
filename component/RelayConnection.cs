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
    public enum RelayPhase { Connecting, Joining, Connected, Retrying, Stopped }
    public sealed class RelayConnection : IDisposable {
        public const int Protocol=3;
        const int Replaced=4001,UnknownRoom=4004;
        readonly Uri uri;
        readonly string diagnosticId=Guid.NewGuid().ToString("N");
        readonly System.Diagnostics.Stopwatch diagnosticClock=System.Diagnostics.Stopwatch.StartNew();
        long sentBytes,receivedBytes,sentMessages,receivedMessages,receivedSnapshots,receivedTicks,lastDataAt,maxDataGap,lastStats;
        int started;
        void Trace(string action) { CoopDiagnostics.Write("connection",new { connection=diagnosticId,role=role,action=action,generation=generation }); }
        void DataReceived(bool snapshot) {
            long now=diagnosticClock.ElapsedMilliseconds;
            long previous=Interlocked.Exchange(ref lastDataAt,now);
            if(previous!=0)Interlocked.Exchange(ref maxDataGap,Math.Max(Interlocked.Read(ref maxDataGap),now-previous));
            if(snapshot)Interlocked.Increment(ref receivedSnapshots);else Interlocked.Increment(ref receivedTicks);
        }
        void TraceStatistics() {
            CoopDiagnostics.Write("transport_totals",new { connection=diagnosticId,role=role,
                sentBytes=Interlocked.Read(ref sentBytes),receivedBytes=Interlocked.Read(ref receivedBytes),
                sentMessages=Interlocked.Read(ref sentMessages),receivedMessages=Interlocked.Read(ref receivedMessages),
                snapshots=Interlocked.Read(ref receivedSnapshots),ticks=Interlocked.Read(ref receivedTicks),
                maxDataGapMs=Interlocked.Read(ref maxDataGap),generation=generation });
        }
        readonly string role,key;
        readonly CancellationTokenSource cancel=new CancellationTokenSource();
        readonly object gate=new object();
        ClientWebSocket socket;
        string pendingSnapshot,pendingTick;
        Delivery incoming;
        long lastMessage;
        volatile int generation;
        volatile bool ready,hostOnline,replaced,unknownRoom;
        volatile string status=Texts.T("relay.connecting");
        volatile bool policyClose;
        volatile int viewers=-1;
        volatile string stopReason;
        volatile string policyReason;
        volatile RelayPhase phase=RelayPhase.Connecting;
        public bool Ready { get { return ready; } }
        public bool HostOnline { get { return hostOnline; } }
        // Viewers following this host as reported by the relay; -1 until the relay says (older relays never do).
        public int Viewers { get { return viewers; } }
        // Why the connection stopped for good: auth_failed, too_many, unknown_room, replaced, protocol, rejected; null while working or retrying.
        public string StopReason { get { return stopReason; } }
        public string Status { get { return status; } }
        public RelayPhase Phase { get { return phase; } }
        // Increases with every accepted connection; a new connection needs a complete snapshot first.
        public int Generation { get { return generation; } }
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength=262144,RecursionLimit=24 }; }
        public RelayConnection(string url,string room,string role,string key) {
            var server=new Uri(url);
            if((server.Scheme!="wss" && !(server.Scheme=="ws" && server.IsLoopback)) || server.AbsolutePath!="/coop" ||
               !string.IsNullOrEmpty(server.Query) || !string.IsNullOrEmpty(server.UserInfo) || !string.IsNullOrEmpty(server.Fragment))
                throw new ArgumentException(Texts.T("relay.badAddress"));
            if(!System.Text.RegularExpressions.Regex.IsMatch(room,"^[a-zA-Z0-9_-]{1,64}$") ||
               (role!="host" && role!="viewer") || key.Length<32 || key.Length>256) throw new ArgumentException(Texts.T("relay.badRoom"));
            // Protocol v3 addresses the room in the path so a relay can route before the upgrade; the key stays in hello.
            uri=new Uri(server,"/coop/"+room);this.role=role;this.key=key;
        }
        public void Start() { Interlocked.Exchange(ref started,1);Trace("start");Task.Run((Func<Task>)Loop); }
        public void Publish(Snapshot s) { Publish(s,true,generation); }
        // Returns false when the connection changed since the caller read Generation; nothing is queued then.
        public bool Publish(Snapshot s,bool complete,int forGeneration) {
            string json=complete?Json().Serialize(s):Json().Serialize(new { type="tick",v=Protocol,seq=s.seq,realTicks=s.realTicks,gameTicks=s.gameTicks,gamePaused=s.gamePaused });
            if(Encoding.UTF8.GetByteCount(json)>262144) throw new ArgumentException(Texts.T("relay.tooLarge"));
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
            Interlocked.Add(ref sentBytes,bytes.Length);Interlocked.Increment(ref sentMessages);
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
                if(diagnosticClock.ElapsedMilliseconds-lastStats>=10000){TraceStatistics();lastStats=diagnosticClock.ElapsedMilliseconds;}
                await Task.Delay(40,ct).ConfigureAwait(false);
            }
        }
        static long? Ticks(object value) { return value==null?(long?)null:Convert.ToInt64(value); }
        // Room occupied or full and the pre-hello auth timeout are transient: a slot may free up or the
        // next attempt may simply beat the clock. Everything else needs the user to fix key/version/config.
        static bool PolicyTerminal(string reason) { return reason!="Room occupied or full" && reason!="Authentication required"; }
        static string PolicyStatus(string reason) {
            if(reason=="Authentication failed") return Texts.T("relay.authFailed");
            if(reason!=null && reason.StartsWith("Unsupported protocol version",StringComparison.Ordinal)) return Texts.T("relay.protocol");
            if(reason=="Room occupied or full") return Texts.T("relay.occupied");
            if(reason=="Authentication required") return Texts.T("relay.noCredentials");
            if(reason=="Too many attempts") return Texts.T("relay.tooMany");
            return Texts.T("relay.rejected");
        }
        static string PolicyCode(string reason) {
            if(reason=="Authentication failed") return "auth_failed";
            if(reason=="Too many attempts") return "too_many";
            if(reason!=null && reason.StartsWith("Unsupported protocol version",StringComparison.Ordinal)) return "protocol";
            return "rejected";
        }
        async Task Reader(ClientWebSocket ws,CancellationToken ct) {
            var buffer=new byte[8192];
            Snapshot current=null;
            while(!ct.IsCancellationRequested && ws.State==WebSocketState.Open) {
                using(var stream=new MemoryStream()) {
                    WebSocketReceiveResult read;
                    do {
                        read=await ws.ReceiveAsync(new ArraySegment<byte>(buffer),ct).ConfigureAwait(false);
                        if(read.MessageType==WebSocketMessageType.Close) {
                            int code=read.CloseStatus.HasValue?(int)read.CloseStatus.Value:0;
                            CoopDiagnostics.Write("socket_closed",new { connection=diagnosticId,code=code });
                            if(code==Replaced) replaced=true;
                            else if(code==UnknownRoom) unknownRoom=true;
                            else if(code==(int)WebSocketCloseStatus.PolicyViolation) { policyClose=true;policyReason=read.CloseStatusDescription; }
                            status=Texts.T("relay.closedRetry");return;
                        }
                        if(read.MessageType!=WebSocketMessageType.Text || stream.Length+read.Count>262144) throw new InvalidDataException();
                        stream.Write(buffer,0,read.Count);
                    } while(!read.EndOfMessage);
                    Interlocked.Exchange(ref lastMessage,DateTime.UtcNow.Ticks);
                    Interlocked.Add(ref receivedBytes,stream.Length);Interlocked.Increment(ref receivedMessages);
                    var json=Json();var msg=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(stream.ToArray()));
                    object kind;if(msg==null || !msg.TryGetValue("type",out kind)) throw new InvalidDataException();
                    if((string)kind=="ready") { lock(gate) generation++;ready=true;status=Texts.T("relay.connected");phase=RelayPhase.Connected;Trace("ready"); }
                    else if((string)kind=="host") { hostOnline=Convert.ToBoolean(msg["online"]);Trace(hostOnline?"host_online":"host_offline"); }
                    else if((string)kind=="peers") { viewers=Convert.ToInt32(msg["viewers"]); }
                    else if((string)kind=="state") {
                        var value=json.ConvertToType<Snapshot>(msg["snapshot"]);
                        if(!value.Valid()) throw new InvalidDataException();
                        bool live=Convert.ToBoolean(msg["live"]);
                        DataReceived(true);current=value;hostOnline=live;
                        Deliver(new Delivery { snapshot=value,live=live,ageMs=Math.Max(0,Convert.ToInt64(msg["ageMs"])),structural=true });
                    }
                    else if((string)kind=="tick" && current!=null) {
                        var value=current.WithTimes(Convert.ToInt64(msg["seq"]),Ticks(msg["realTicks"]),Ticks(msg["gameTicks"]),Convert.ToBoolean(msg["gamePaused"]));
                        if(!value.Valid()) throw new InvalidDataException();
                        DataReceived(false);current=value;hostOnline=true;
                        Deliver(new Delivery { snapshot=value,live=true,ageMs=0 });
                    }
                }
            }
        }
        async Task Loop() {
            while(!cancel.IsCancellationRequested) {
                ready=false;hostOnline=false;viewers=-1;
                // Never replay a snapshot queued on a previous connection.
                lock(gate) { pendingSnapshot=null;pendingTick=null;incoming=null; }
                using(var ws=new ClientWebSocket())
                using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancel.Token)) {
                    socket=ws;
                    try {
                        status=Texts.T("relay.connecting");phase=RelayPhase.Connecting;Trace("connecting");
                        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(linked.Token)) {
                            timeout.CancelAfter(10000);
                            await ws.ConnectAsync(uri,timeout.Token).ConfigureAwait(false);
                        }
                        phase=RelayPhase.Joining;status=Texts.T("relay.joining");Trace("joining");
                        Interlocked.Exchange(ref lastMessage,DateTime.UtcNow.Ticks);
                        await Send(ws,Json().Serialize(new { type="hello",v=Protocol,role=role,key=key }),linked.Token).ConfigureAwait(false);
                        var reader=Reader(ws,linked.Token);var writer=Writer(ws,linked.Token);
                        var first=await Task.WhenAny(reader,writer).ConfigureAwait(false);
                        linked.Cancel();ws.Abort();
                        if(first.IsFaulted) { CoopDiagnostics.Error("transport",first.Exception);status=first.Exception.InnerException is TimeoutException?Texts.T("relay.noResponse"):Texts.T("relay.lost"); }
                        try { await Task.WhenAll(reader,writer).ConfigureAwait(false); } catch(Exception) { }
                    } catch(OperationCanceledException) { if(!cancel.IsCancellationRequested)Trace("connect_timeout"); }
                    catch(Exception e) { CoopDiagnostics.Error("connect",e);status=Texts.T("relay.lost"); }
                    finally { ready=false;hostOnline=false;socket=null; }
                }
                // Two hosts would otherwise keep replacing each other.
                if(replaced) { Trace("host_replaced");stopReason="replaced";phase=RelayPhase.Stopped;status=Texts.T("relay.replaced");return; }
                if(unknownRoom) { Trace("unknown_room");stopReason="unknown_room";phase=RelayPhase.Stopped;status=Texts.T("relay.unknownRoom");return; }
                if(policyClose) {
                    status=PolicyStatus(policyReason);CoopDiagnostics.Write("policy_close",new { connection=diagnosticId,reason=policyReason });
                    bool terminal=PolicyTerminal(policyReason);string reasonCode=policyReason;
                    policyClose=false;policyReason=null;
                    if(terminal) { Trace("policy_stopped");stopReason=PolicyCode(reasonCode);phase=RelayPhase.Stopped;return; }
                }
                if(cancel.IsCancellationRequested) return;
                phase=RelayPhase.Retrying;Trace("retrying");
                try { await Task.Delay(3000,cancel.Token).ConfigureAwait(false); } catch(OperationCanceledException) { return; }
            }
        }
        public void Dispose() { if(Interlocked.Exchange(ref started,0)==1){Trace("disposed");TraceStatistics();}cancel.Cancel();var ws=socket;if(ws!=null) ws.Abort();ready=false;hostOnline=false;viewers=-1; }
    }
}
