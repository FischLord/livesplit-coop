using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LiveSplit.Coop;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.UI;

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
    public static void Run(string url) {
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
        using(var publisher=new RelayConnection(url,"test","host",new string('h',32)))
        using(var a=new RelayConnection(url,"test","viewer",new string('v',32)))
        using(var b=new RelayConnection(url,"test","viewer",new string('v',32))) {
            publisher.Start();a.Start();b.Start();Wait(()=>publisher.Ready && a.Ready && b.Ready,"three native clients");
            s=Snapshot.Capture(host,"run1","attempt3",7);publisher.Publish(s);
            var sa=Receive(a,7).snapshot;var sb=Receive(b,7).snapshot;
            Check(sa.realTicks==s.realTicks && sb.realTicks==s.realTicks,"one host reaches two native WebSocket viewers");
            using(var late=new RelayConnection(url,"test","viewer",new string('v',32))) {
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
            publisher.Dispose();Wait(()=>!a.HostOnline && !b.HostOnline,"offline notice");
            Check(!a.HostOnline && !b.HostOnline,"native clients detect disconnected host");
        }
        using(var component=new CoopComponent(State())) {
            var doc=new System.Xml.XmlDocument();var saved=component.GetSettings(doc);component.SetSettings(saved);
            using(var image=new System.Drawing.Bitmap(360,22)) using(var graphics=System.Drawing.Graphics.FromImage(image))
                component.DrawVertical(graphics,viewer,360,new System.Drawing.Region());
            Check(component.ComponentName=="Coop Relay","component UI constructs, saves/loads settings and renders status");
        }
        Console.WriteLine("Native checks passed: "+passed);
    }
}
