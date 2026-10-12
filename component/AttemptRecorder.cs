using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveSplit.Model;
using LiveSplit.Options;
using LiveSplit.UI;

namespace LiveSplit.Coop {
    // Records the host's attempts into the viewer's own splits. An attempt counts once the viewer sees it end
    // (finished, then reset; or reset while running) on the current connection. That includes joining or reconnecting
    // in the middle of an attempt and watching it end; attempts whose end was not seen (connection lost first) are dropped.
    public sealed class AttemptRecorder {
        readonly ViewerLibrary library;
        readonly Func<string,bool> lockedByHost,openHere;
        readonly Func<DateTime> now;
        readonly HashSet<string> contacted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Snapshot current;
        DateTime started;
        int generation=int.MinValue;
        public string Note { get; private set; }
        public AttemptRecorder(ViewerLibrary library) : this(library,HostLock.IsHeld,()=>DateTime.UtcNow) { }
        public AttemptRecorder(ViewerLibrary library,Func<string,bool> lockedByHost,Func<DateTime> now) : this(library,lockedByHost,now,null) { }
        // openHere: true for splits this very timer holds in memory (the viewer's own run); nothing may be written into them.
        public AttemptRecorder(ViewerLibrary library,Func<string,bool> lockedByHost,Func<DateTime> now,Func<string,bool> openHere) { this.library=library;this.lockedByHost=lockedByHost;this.now=now;this.openHere=openHere ?? (p=>false); }
        public static bool SamePath(string a,string b) {
            if(string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase); }
            catch(ArgumentException) { return false; } catch(NotSupportedException) { return false; } catch(PathTooLongException) { return false; }
        }
        public void Observe(Snapshot s,int connectionGeneration) {
            if(connectionGeneration!=generation) { generation=connectionGeneration;current=null; }
            try { FirstContact(s); }
            catch(Exception e) { Fail("viewer_first_contact",e,library.PathFor(s)); }
            if(current!=null && (s.runId!=current.runId || s.attemptId!=current.attemptId || s.phase=="NotRunning")) {
                // Cleared first: whatever happens in Finish, the attempt is never finished twice.
                var done=current;current=null;
                try { Finish(done); }
                catch(Exception e) { FinishFallback(done,e); }
            }
            if(s.phase!="NotRunning") {
                if(current==null) started=now()-TimeSpan.FromTicks(s.realTicks ?? 0);
                current=s;
            }
        }
        static Time Time(long? real,long? game) { return new Time(real.HasValue?(TimeSpan?)TimeSpan.FromTicks(real.Value):null,game.HasValue?(TimeSpan?)TimeSpan.FromTicks(game.Value):null); }
        static TimeSpan? Pick(Time time,TimingMethod method) { return method==TimingMethod.GameTime?time.GameTime:time.RealTime; }
        // Once per splits file and session: create missing splits from the host, or take over a faster host PB and faster golds.
        void FirstContact(Snapshot s) {
            string target=library.PathFor(s);
            if(!contacted.Add(target) || lockedByHost(target) || openHere(target)) return;
            if(!File.Exists(target)) { RunFiles.Save(RunFiles.FromSnapshot(s),target);Note=Texts.T("recorder.created",Path.GetFileName(target));return; }
            var run=RunFiles.Load(target);
            if(!RunFiles.SameSegments(run,s)) return;
            var method=(TimingMethod)Enum.Parse(typeof(TimingMethod),s.timingMethod);
            var last=s.segments[s.segments.Length-1];
            TimeSpan? hostPb=Pick(Time(last.pbRT,last.pbGT),method),localPb=Pick(run[run.Count-1].PersonalBestSplitTime,method);
            bool changed=false;
            if(hostPb.HasValue && (!localPb.HasValue || hostPb.Value<localPb.Value)) {
                for(int i=0;i<run.Count;i++) run[i].PersonalBestSplitTime=Time(s.segments[i].pbRT,s.segments[i].pbGT);
                changed=true;
            }
            for(int i=0;i<run.Count;i++) {
                TimeSpan? host=Pick(Time(s.segments[i].bestRT,s.segments[i].bestGT),method),mine=Pick(run[i].BestSegmentTime,method);
                if(host.HasValue && (!mine.HasValue || host.Value<mine.Value)) { run[i].BestSegmentTime=Time(s.segments[i].bestRT,s.segments[i].bestGT);changed=true; }
            }
            if(changed) { RunFiles.Save(run,target);Note=Texts.T("recorder.adoptedPb",Path.GetFileName(target));CoopDiagnostics.Write("viewer_host_pb_adopted",new { segments=run.Count }); }
        }
        void Fail(string what,Exception e,string target) {
            CoopDiagnostics.Error(what,e);
            Note=Texts.T("recorder.failed",e.Message);
        }
        // The regular target failed: try the Pending copy once; if that fails too, say so.
        void FinishFallback(Snapshot last,Exception first) {
            string target=library.PathFor(last);
            CoopDiagnostics.Error("viewer_recorder",first);
            try {
                string path=library.PendingPath(target);
                var run=File.Exists(path)?RunFiles.Load(path):RunFiles.FromSnapshot(last);
                if(!RunFiles.SameSegments(run,last)) throw first;
                Record(run,last,started,now());
                RunFiles.Save(run,path);
                Note=Texts.T("recorder.fallback",Path.GetFileName(target),path);
                CoopDiagnostics.Write("viewer_attempt_recorded",new { completed=last.phase=="Ended",pending=true,fallback=true });
            } catch(Exception second) {
                if(!object.ReferenceEquals(second,first)) CoopDiagnostics.Error("viewer_recorder_pending",second);
                Note=Texts.T("recorder.failed",first.Message);
            }
        }
        void Finish(Snapshot last) {
            string target=library.PathFor(last),path=target;
            bool here=openHere(target),pending=here || lockedByHost(target);
            if(pending) path=library.PendingPath(target);
            string note=here?Texts.T("recorder.openLocally",path):pending?Texts.T("recorder.pending",path):Texts.T("recorder.saved",Path.GetFileName(path));
            IRun run=File.Exists(path)?RunFiles.Load(path):RunFiles.FromSnapshot(last);
            if(!RunFiles.SameSegments(run,last)) {
                path=library.PendingPath(target);pending=true;
                run=File.Exists(path)?RunFiles.Load(path):RunFiles.FromSnapshot(last);
                if(!RunFiles.SameSegments(run,last)) { Note=Texts.T("recorder.failed",Path.GetFileName(target));return; }
                note=Texts.T("recorder.mismatch",Path.GetFileName(target),path);
            }
            Record(run,last,started,now());
            RunFiles.Save(run,path);
            Note=note;
            CoopDiagnostics.Write("viewer_attempt_recorded",new { completed=last.phase=="Ended",pending=pending });
        }
        // Replays the attempt's outcome on a shadow timer and lets LiveSplit's TimerModel update history, golds and PB.
        static void Record(IRun run,Snapshot last,DateTime started,DateTime ended) {
            var state=new LiveSplitState(run,null,new Layout(),new LayoutSettings(),new Settings());
            state.CurrentTimingMethod=(TimingMethod)Enum.Parse(typeof(TimingMethod),last.timingMethod);
            run.AttemptCount++;
            state.AttemptStarted=new AtomicDateTime(started,false);
            state.AttemptEnded=new AtomicDateTime(ended,false);
            for(int i=0;i<run.Count;i++) run[i].SplitTime=Time(last.segments[i].splitRT,last.segments[i].splitGT);
            long elapsed=last.realTicks ?? 0;
            state.AdjustedStartTime=state.StartTimeWithOffset=TimeStamp.Now-TimeSpan.FromTicks(elapsed);
            if(last.phase=="Ended") { state.CurrentPhase=TimerPhase.Ended;state.CurrentSplitIndex=run.Count; }
            else { state.CurrentPhase=TimerPhase.Running;state.CurrentSplitIndex=Math.Max(0,last.index); }
            new TimerModel { CurrentState=state }.Reset(true);
        }
    }
}
