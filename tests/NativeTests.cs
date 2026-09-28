using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LiveSplit.Coop;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.UI;

// Stand-ins named like the real autosplitter components.
public sealed class ASLComponent {}
public sealed class ASRComponent {}
public static class NativeTests {
    static int passed;
    static void Check(bool test,string name) { if(!test) throw new Exception("FAIL: "+name);passed++;Console.WriteLine("PASS: "+name); }
    static LiveSplitState State() {
        var run=new Run(new StandardComparisonGeneratorsFactory());run.GameName="Test";run.CategoryName="Coop";
        run.FilePath="do-not-overwrite.lss";
        for(int i=1;i<=3;i++) { var seg=new Segment("CP"+i);seg.PersonalBestSplitTime=new Time(TimeSpan.FromSeconds(i*12),null);seg.BestSegmentTime=new Time(TimeSpan.FromSeconds(9),null);run.Add(seg); }
        var state=new LiveSplitState(run,null,new Layout(),new LiveSplit.Options.LayoutSettings(),new LiveSplit.Options.Settings());
        state.CurrentTimingMethod=TimingMethod.RealTime;state.CurrentComparison="Personal Best";
        foreach(var gen in run.ComparisonGenerators) gen.Generate(state.Settings);
        return state;
    }
    static void Wait(Func<bool> condition,string label) {
        var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<7000) { if(condition()) return;Thread.Sleep(15); }throw new Exception("Timeout: "+label);
    }
    static Delivery Receive(RelayConnection c,long seq) {
        Delivery result=null;Wait(()=>{var next=c.Take();if(next!=null && next.snapshot.seq==seq)result=next;return result!=null;},"snapshot "+seq);return result;
    }
    // Reads the HTTP upgrade request off a raw socket and answers with the WebSocket handshake response.
    static void Handshake(NetworkStream stream) {
        var request=new StringBuilder();var one=new byte[1];
        while(!request.ToString().EndsWith("\r\n\r\n") && stream.Read(one,0,1)==1) request.Append((char)one[0]);
        var key=request.ToString().Split(new[]{"\r\n"},StringSplitOptions.None).First(l=>l.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase)).Substring(18).Trim();
        string accept;using(var sha=SHA1.Create()) accept=Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var reply=Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\n\r\n");
        stream.Write(reply,0,reply.Length);
    }
    static void WriteClose(NetworkStream stream,string reason) {
        var reasonBytes=Encoding.UTF8.GetBytes(reason);
        var payload=new byte[2+reasonBytes.Length];
        payload[0]=0x03;payload[1]=0xF0; // 1008 Policy Violation, big-endian
        Array.Copy(reasonBytes,0,payload,2,reasonBytes.Length);
        var frame=new byte[2+payload.Length];
        frame[0]=0x88;frame[1]=(byte)payload.Length;
        Array.Copy(payload,0,frame,2,payload.Length);
        stream.Write(frame,0,frame.Length);
    }
    static void WriteText(NetworkStream stream,string json) {
        var payload=Encoding.UTF8.GetBytes(json);
        var frame=new byte[2+payload.Length];
        frame[0]=0x81;frame[1]=(byte)payload.Length;
        Array.Copy(payload,0,frame,2,payload.Length);
        stream.Write(frame,0,frame.Length);
    }
    // Completes the WebSocket handshake for a fake relay, then hands the raw stream to the caller's continuation.
    static int FakeRelay(Action<NetworkStream> behavior) {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        new Thread(()=>{
            using(var client=listener.AcceptTcpClient())
            using(var stream=client.GetStream()) { Handshake(stream);behavior(stream); }
            listener.Stop();
        }) { IsBackground=true }.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    // Completes the WebSocket handshake and then never answers, like a path that died silently.
    static int SilentRelay() { return FakeRelay(stream=>Thread.Sleep(30000)); }
    // Closes immediately with a 1008 policy violation, like a relay rejecting an invalid room key.
    static int PolicyRelay(string reason) { return FakeRelay(stream=>{ WriteClose(stream,reason);Thread.Sleep(500); }); }
    // First connection is rejected as if the room were full; the retry against a freed slot reaches ready.
    static int OccupiedThenReadyRelay() {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        new Thread(()=>{
            using(var first=listener.AcceptTcpClient())
            using(var firstStream=first.GetStream()) { Handshake(firstStream);WriteClose(firstStream,"Room occupied or full");Thread.Sleep(200); }
            using(var second=listener.AcceptTcpClient())
            using(var secondStream=second.GetStream()) { Handshake(secondStream);WriteText(secondStream,"{\"type\":\"ready\",\"v\":2,\"role\":\"viewer\"}");Thread.Sleep(8000); }
            listener.Stop();
        }) { IsBackground=true }.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    public static void Run(string url) { Run(url,"test",new string('h',32),new string('v',32)); }
    public static void Run(string url,string room,string hostKey,string viewerKey) {
        // LiveSplit may queue blocking game-metadata lookups for each named test run. Reserve
        // enough workers for the real WebSocket clients on a cold metadata cache.
        int minWorkers,minIo;ThreadPool.GetMinThreads(out minWorkers,out minIo);
        if(!ThreadPool.SetMinThreads(Math.Max(minWorkers,64),minIo)) throw new InvalidOperationException("Could not reserve native test workers.");
        var host=State();var model=new TimerModel { CurrentState=host };model.Start();
        host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(10);model.Split();
        host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(20);model.Split();
        host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(25);
        var s=Snapshot.Capture(host,"run1","attempt1",1);
        Check(s.Valid(),"capture validates against protocol");
        Check(s.gameTicks==null,"RTA host needs no artificial game time");
        var viewer=State();var original=viewer.Run;var before=original[0].PersonalBestSplitTime;
        using(var mirror=new Mirror(viewer)) {
            mirror.Adopt(s);mirror.Render(200,false);
            Check(viewer.CurrentTimingMethod==TimingMethod.RealTime,"viewer preserves Real Time");
            Check(viewer.CurrentSplitIndex==2,"late join receives current checkpoint");
            Check(viewer.Run[0].SplitTime.RealTime==host.Run[0].SplitTime.RealTime && viewer.Run[1].SplitTime.RealTime==host.Run[1].SplitTime.RealTime,"late join receives exact past split times");
            Check(viewer.Run[0].PersonalBestSplitTime.RealTime==before.RealTime,"PB comparison imported");
            Check(string.IsNullOrEmpty(viewer.Run.FilePath),"mirror cannot overwrite original path");
            Check(Math.Abs(viewer.CurrentTime.RealTime.Value.TotalMilliseconds-(s.realTicks.Value/10000.0+200))<50,"running time interpolates");
            mirror.Render(1500,true);var frozen=viewer.CurrentTime.RealTime;Thread.Sleep(30);
            Check(viewer.CurrentPhase==TimerPhase.Paused && viewer.CurrentTime.RealTime==frozen,"stale timer freezes");
            model.Pause();s=Snapshot.Capture(host,"run1","attempt1",2);mirror.Adopt(s);mirror.Render(300,false);
            Check(viewer.CurrentPhase==TimerPhase.Paused && viewer.CurrentTime.RealTime.Value.Ticks==s.realTicks,"host pause is exact");
            model.Pause();host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(30);model.Split();
            s=Snapshot.Capture(host,"run1","attempt1",3);mirror.Adopt(s);mirror.Render(800,false);
            Check(viewer.CurrentPhase==TimerPhase.Ended && viewer.CurrentTime.RealTime==host.CurrentTime.RealTime,"RTA finish remains exact after delay");
            Check(viewer.CurrentTime.GameTime==null,"missing GT stays missing at finish");
            model.UndoSplit();s=Snapshot.Capture(host,"run1","attempt1",4);mirror.Adopt(s);
            Check(viewer.CurrentPhase==TimerPhase.Running && viewer.Run[2].SplitTime.RealTime==null,"undo finish restores running state");
            model.Reset(false);s=Snapshot.Capture(host,"run1","attempt2",5);mirror.Adopt(s);
            Check(viewer.CurrentPhase==TimerPhase.NotRunning && viewer.Run.All(seg=>seg.SplitTime.RealTime==null),"reset clears all received splits");
            model.Start();host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(5);model.SkipSplit();
            model.InitializeGameTime();host.SetGameTime(TimeSpan.FromSeconds(3));host.IsGameTimePaused=true;
            s=Snapshot.Capture(host,"run1","attempt3",6);mirror.Adopt(s);mirror.Render(200,false);
            Check(viewer.Run[0].SplitTime.RealTime==null && viewer.CurrentSplitIndex==1,"skipped split remains empty");
            Check(viewer.CurrentTime.GameTime==TimeSpan.FromTicks(s.gameTicks.Value),"paused game time does not interpolate");
        }
        Check(Object.ReferenceEquals(viewer.Run,original) && original[0].PersonalBestSplitTime.RealTime==before.RealTime,"disconnect restores untouched original run");
        Check(viewer.CurrentPhase==TimerPhase.NotRunning,"restored local timer is idle");
        Check(CoopComponent.IsAutoSplitter(typeof(ASLComponent)) && CoopComponent.IsAutoSplitter(typeof(ASRComponent)) && !CoopComponent.IsAutoSplitter(typeof(CoopComponent)),"viewer guard recognises ASL/ASR component names");
        string asl=Path.Combine(Environment.CurrentDirectory,"Components","LiveSplit.ScriptableAutoSplit.dll");
        if(File.Exists(asl)) {
            Type[] types;try { types=System.Reflection.Assembly.LoadFrom(asl).GetTypes(); } catch(System.Reflection.ReflectionTypeLoadException e) { types=e.Types.Where(x=>x!=null).ToArray(); }
            var components=types.Where(x=>typeof(LiveSplit.UI.Components.IComponent).IsAssignableFrom(x) && !x.IsAbstract).ToArray();
            Check(components.Length>0 && components.All(CoopComponent.IsAutoSplitter),"viewer guard recognises the installed Scriptable Auto Splitter");
        }
        var local=State();var mine=local.Run;
        using(var mirror=new Mirror(local)) {
            s=Snapshot.Capture(host,"run1","attempt3",20);mirror.Adopt(s);var mirrored=local.Run;
            mirror.Advance(s.WithTimes(21,s.realTicks+10000000,s.gameTicks,s.gamePaused));mirror.Render(0,false);
            Check(Object.ReferenceEquals(local.Run,mirrored) && Math.Abs((local.CurrentTime.RealTime.Value-TimeSpan.FromTicks(s.realTicks.Value)).TotalMilliseconds-1000)<50,"clock-only update advances without rebuilding the run");
            var loaded=State().Run;loaded[0].PersonalBestSplitTime=new Time(TimeSpan.FromSeconds(99),null);local.Run=loaded;
            bool refused=false;try { mirror.Adopt(Snapshot.Capture(host,"run1","attempt3",22)); } catch(InvalidOperationException) { refused=true; }
            bool frozen=false;try { mirror.Render(0,false); } catch(InvalidOperationException) { frozen=true; }
            Check(refused && frozen && loaded[0].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(99),"splits loaded while viewing are never overwritten");
        }
        Check(local.Run.Count>0 && local.Run[0].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(99) && !Object.ReferenceEquals(local.Run,mine) && local.CurrentPhase==TimerPhase.NotRunning,"stopping keeps splits the user loaded");
        using(var publisher=new RelayConnection(url,room,"host",hostKey))
        using(var a=new RelayConnection(url,room,"viewer",viewerKey))
        using(var b=new RelayConnection(url,room,"viewer",viewerKey)) {
            publisher.Start();a.Start();b.Start();Wait(()=>publisher.Ready && a.Ready && b.Ready,"three native clients");
            s=Snapshot.Capture(host,"run1","attempt3",7);publisher.Publish(s);
            var sa=Receive(a,7).snapshot;var sb=Receive(b,7).snapshot;
            Check(sa.realTicks==s.realTicks && sb.realTicks==s.realTicks,"one host reaches two native WebSocket viewers");
            using(var late=new RelayConnection(url,room,"viewer",viewerKey)) {
                late.Start();var replay=Receive(late,7);Check(replay.snapshot.index==s.index,"late native client receives cached snapshot");
            }
            var socketField=typeof(RelayConnection).GetField("socket",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            ((System.Net.WebSockets.ClientWebSocket)socketField.GetValue(a)).Abort();
            Wait(()=>!a.Ready,"viewer interruption");
            Check(Receive(a,7).snapshot.realTicks==s.realTicks,"native viewer reconnects and reloads the complete cached state");
            model.Reset(false);model.Start();host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(31);
            model.Split();host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(32);model.Split();
            host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(33);model.Split();
            s=Snapshot.Capture(host,"run1","attempt4",8);publisher.Publish(s);
            var endA=Receive(a,8).snapshot;var endB=Receive(b,8).snapshot;
            Check(endA.phase=="Ended" && endA.realTicks==s.realTicks && endB.realTicks==s.realTicks,"exact final RTA reaches both native viewers over WebSocket");
            model.Reset(false);model.Start();host.AdjustedStartTime=TimeStamp.Now-TimeSpan.FromSeconds(40);
            s=Snapshot.Capture(host,"run1","attempt5",9);publisher.Publish(s);Receive(a,9);
            int generation=publisher.Generation;var later=s.WithTimes(10,s.realTicks+5000000,s.gameTicks,s.gamePaused);
            Check(!publisher.Publish(later,false,generation+1),"a tick for another connection is dropped");
            Check(publisher.Publish(later,false,generation),"tick queued");
            var tick=Receive(a,10);Receive(b,10);
            Check(!tick.structural && tick.snapshot.realTicks==later.realTicks && tick.snapshot.segments.Length==s.segments.Length && tick.snapshot.attemptId=="attempt5","tick moves only the clock and keeps the complete run");
            var next=Snapshot.Capture(host,"run1","attempt6",11);publisher.Publish(next);publisher.Publish(next.WithTimes(12,next.realTicks+1,next.gameTicks,next.gamePaused),false,generation);
            Thread.Sleep(600);var merged=b.Take();
            Check(merged!=null && merged.snapshot.seq==12 && merged.structural && merged.snapshot.attemptId=="attempt6","a snapshot followed by a tick before the frame is still applied as a snapshot");
            using(var takeover=new RelayConnection(url,room,"host",hostKey)) {
                takeover.Start();Wait(()=>takeover.Ready && publisher.Phase==RelayPhase.Stopped,"host takeover");
                Thread.Sleep(3500);
                Check(takeover.Ready && !publisher.Ready,"newer host replaces the old connection, which stops instead of fighting back");
            }
            Wait(()=>!a.HostOnline && !b.HostOnline,"offline notice");
            Check(!a.HostOnline && !b.HostOnline,"native clients detect disconnected host");
        }
        using(var silent=new RelayConnection("ws://127.0.0.1:"+SilentRelay()+"/coop","test","viewer",new string('v',32))) {
            silent.Start();var timer=Stopwatch.StartNew();
            while(timer.ElapsedMilliseconds<16000 && silent.Phase!=RelayPhase.Retrying) Thread.Sleep(50);
            Check(silent.Phase==RelayPhase.Retrying,"a silent relay is detected without waiting for TCP");
        }
        using(var invalidKey=new RelayConnection("ws://127.0.0.1:"+PolicyRelay("Authentication failed")+"/coop","test","viewer",new string('v',32))) {
            invalidKey.Start();
            Wait(()=>invalidKey.Phase==RelayPhase.Stopped,"invalid room key reaches a terminal phase");
            Thread.Sleep(4000);
            Check(invalidKey.Phase==RelayPhase.Stopped && !invalidKey.Ready,"a rejected room key stops for good instead of repeatedly reconnecting");
        }
        using(var occupied=new RelayConnection("ws://127.0.0.1:"+OccupiedThenReadyRelay()+"/coop","test","viewer",new string('v',32))) {
            occupied.Start();
            Wait(()=>occupied.Ready,"a transient room-full 1008 keeps retrying until a slot opens and ready arrives");
            Check(occupied.Ready && occupied.Phase==RelayPhase.Connected,"room occupied or full retries instead of stopping permanently");
        }
        Console.WriteLine("Native checks passed: "+passed);
    }
}
