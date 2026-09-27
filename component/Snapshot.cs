using System;
using System.Collections.Generic;
using System.Linq;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;

namespace LiveSplit.Coop {
    public sealed class Pair {
        public long? real;
        public long? game;
        public static Pair From(Time t) { return new Pair { real=Tick(t.RealTime), game=Tick(t.GameTime) }; }
        public static long? Tick(TimeSpan? t) { return t.HasValue ? (long?)t.Value.Ticks : null; }
    }
    public sealed class SegmentData {
        public string name;
        public long? splitRT, splitGT, pbRT, pbGT, bestRT, bestGT;
        public Dictionary<string, Pair> comparisons;
    }
    public sealed class Snapshot {
        public string type="snapshot";
        public int v=RelayConnection.Protocol;
        public long seq;
        public string runId, attemptId, game, category, phase, timingMethod, comparison;
        public int attempts, index;
        public long offsetTicks;
        public long? realTicks, gameTicks;
        public bool gamePaused;
        public SegmentData[] segments;
        public static Snapshot Capture(LiveSplitState s,string runId,string attemptId,long seq) {
            var time=s.CurrentTime;
            return new Snapshot {
                seq=seq,runId=runId,attemptId=attemptId,game=s.Run.GameName,category=s.Run.CategoryName,
                attempts=s.Run.AttemptCount,index=s.CurrentSplitIndex,phase=s.CurrentPhase.ToString(),
                timingMethod=s.CurrentTimingMethod.ToString(),comparison=s.CurrentComparison ?? "Personal Best",
                offsetTicks=s.Run.Offset.Ticks,realTicks=Pair.Tick(time.RealTime),gameTicks=Pair.Tick(time.GameTime),
                gamePaused=s.IsGameTimePaused,
                segments=s.Run.Select(seg => new SegmentData {
                    name=seg.Name,splitRT=Pair.Tick(seg.SplitTime.RealTime),splitGT=Pair.Tick(seg.SplitTime.GameTime),
                    pbRT=Pair.Tick(seg.PersonalBestSplitTime.RealTime),pbGT=Pair.Tick(seg.PersonalBestSplitTime.GameTime),
                    bestRT=Pair.Tick(seg.BestSegmentTime.RealTime),bestGT=Pair.Tick(seg.BestSegmentTime.GameTime),
                    comparisons=s.Run.Comparisons.Distinct().ToDictionary(n => n,n => Pair.From(seg.Comparisons[n]))
                }).ToArray()
            };
        }
        public Snapshot WithTimes(long seq,long? realTicks,long? gameTicks,bool gamePaused) {
            var copy=(Snapshot)MemberwiseClone();
            copy.seq=seq;copy.realTicks=realTicks;copy.gameTicks=gameTicks;copy.gamePaused=gamePaused;
            return copy;
        }
        // Everything except the running clock; when this is unchanged a tick is enough.
        public string StructureKey() { return RelayConnection.Json().Serialize(WithTimes(0,null,null,false)); }
        const long Limit=6048000000000L;
        static bool T(long? t) { return !t.HasValue || (t>=-Limit && t<=Limit); }
        static bool Text(string t,int max) { return t!=null && t.Length<=max && !t.Any(char.IsControl); }
        public bool Valid() {
            if(type!="snapshot" || v!=RelayConnection.Protocol || seq<0 || !Text(runId,80) || !Text(attemptId,80) ||
               !Text(game,200) || !Text(category,200) || !Text(comparison,100) || attempts<0 ||
               !T(offsetTicks) || !T(realTicks) || !T(gameTicks) || segments==null || segments.Length<1 || segments.Length>256 ||
               !new[]{"NotRunning","Running","Paused","Ended"}.Contains(phase) ||
               !new[]{"RealTime","GameTime"}.Contains(timingMethod)) return false;
            if(phase=="NotRunning" ? index!=-1 : phase=="Ended" ? index!=segments.Length : index<0 || index>=segments.Length) return false;
            if(phase!="NotRunning" && !realTicks.HasValue) return false;
            foreach(var seg in segments) {
                if(seg==null || !Text(seg.name,200) || !T(seg.splitRT) || !T(seg.splitGT) || !T(seg.pbRT) || !T(seg.pbGT) ||
                   !T(seg.bestRT) || !T(seg.bestGT) || seg.comparisons==null || seg.comparisons.Count>32) return false;
                foreach(var kv in seg.comparisons) if(!Text(kv.Key,100) || kv.Value==null || !T(kv.Value.real) || !T(kv.Value.game)) return false;
            }
            return phase!="Ended" || (segments[segments.Length-1].splitRT==realTicks && segments[segments.Length-1].splitGT==gameTicks);
        }
    }
    public sealed class Mirror : IDisposable {
        readonly LiveSplitState state;
        readonly IRun original;
        readonly TimingMethod method;
        readonly string comparison;
        IRun expected;
        string runId;
        Snapshot latest;
        public Mirror(LiveSplitState state) {
            if(state.CurrentPhase!=TimerPhase.NotRunning) throw new InvalidOperationException("Stop/reset the local timer before joining as viewer.");
            this.state=state;original=expected=state.Run;method=state.CurrentTimingMethod;comparison=state.CurrentComparison;
        }
        // Loading or editing splits replaces state.Run. Never write host data into a run the mirror does not own.
        void EnsureOwned() {
            if(!Object.ReferenceEquals(state.Run,expected)) throw new InvalidOperationException("Splits were changed locally; viewer mode stopped.");
        }
        static Time Time(long? r,long? g) { return new Time(r.HasValue?(TimeSpan?)TimeSpan.FromTicks(r.Value):null,g.HasValue?(TimeSpan?)TimeSpan.FromTicks(g.Value):null); }
        public void Adopt(Snapshot s) {
            if(!s.Valid()) throw new ArgumentException("Invalid snapshot");
            EnsureOwned();
            bool rebuild=runId!=s.runId || latest==null || latest.segments.Length!=s.segments.Length;
            if(rebuild) {
                var run=new Run(new StandardComparisonGeneratorsFactory());
                foreach(var seg in s.segments) run.Add(new Segment(seg.name));
                // No local file path, autosplitter, icons, scripts or history are imported.
                run.FilePath=null;run.LayoutPath=null;
                state.Run=expected=run;runId=s.runId;
            }
            state.Run.GameName=s.game;state.Run.CategoryName=s.category;
            state.Run.AttemptCount=s.attempts;state.Run.Offset=TimeSpan.FromTicks(s.offsetTicks);
            for(int i=0;i<s.segments.Length;i++) {
                var data=s.segments[i];var seg=state.Run[i];
                seg.Name=data.name;seg.SplitTime=Time(data.splitRT,data.splitGT);
                seg.PersonalBestSplitTime=Time(data.pbRT,data.pbGT);seg.BestSegmentTime=Time(data.bestRT,data.bestGT);
                foreach(var kv in data.comparisons) {
                    if(!state.Run.Comparisons.Contains(kv.Key)) state.Run.CustomComparisons.Add(kv.Key);
                    seg.Comparisons[kv.Key]=Time(kv.Value.real,kv.Value.game);
                }
            }
            latest=s;
            state.CurrentTimingMethod=(TimingMethod)Enum.Parse(typeof(TimingMethod),s.timingMethod);
            state.CurrentComparison=state.Run.Comparisons.Contains(s.comparison)?s.comparison:"Personal Best";
            state.Run.HasChanged=false;
            Render(0,false);
            state.CallRunManuallyModified();
        }
        // Clock-only update: no run rebuild and no RunManuallyModified event.
        public void Advance(Snapshot s) {
            if(latest==null || s.runId!=latest.runId || s.attemptId!=latest.attemptId) { Adopt(s);return; }
            if(!s.Valid()) throw new ArgumentException("Invalid snapshot");
            EnsureOwned();latest=s;
        }
        public void Render(double elapsedMs,bool stale) {
            if(latest==null) return;
            EnsureOwned();
            var phase=(TimerPhase)Enum.Parse(typeof(TimerPhase),latest.phase);
            long elapsed=phase==TimerPhase.Running?(long)(Math.Max(0,Math.Min(elapsedMs,1500))*10000):0;
            long r=(latest.realTicks ?? 0)+elapsed;
            long? g=latest.gameTicks.HasValue ? latest.gameTicks+(latest.gamePaused?0:elapsed) : null;
            // A stale running mirror freezes; it must never imply a live host connection.
            state.CurrentPhase=stale && phase==TimerPhase.Running?TimerPhase.Paused:phase;
            state.CurrentSplitIndex=latest.index;
            state.AdjustedStartTime=state.StartTimeWithOffset=TimeStamp.Now-TimeSpan.FromTicks(r);
            state.TimePausedAt=TimeSpan.FromTicks(r);
            state.IsGameTimePaused=false;
            state.IsGameTimeInitialized=g.HasValue;
            if(g.HasValue) state.SetGameTime(TimeSpan.FromTicks(g.Value));
            state.IsGameTimePaused=latest.gamePaused;
            if(latest.gamePaused && g.HasValue) state.GameTimePauseTime=TimeSpan.FromTicks(g.Value);
            state.CurrentTimingMethod=(TimingMethod)Enum.Parse(typeof(TimingMethod),latest.timingMethod);
        }
        public void Dispose() {
            // Splits the user loaded while viewing stay loaded; only the mirror's own run is swapped back.
            if(Object.ReferenceEquals(state.Run,expected)) { state.Run=original;state.CurrentComparison=comparison; }
            state.CurrentPhase=TimerPhase.NotRunning;state.CurrentSplitIndex=-1;
            state.CurrentTimingMethod=method;
            state.IsGameTimePaused=false;state.IsGameTimeInitialized=false;
            state.CallRunManuallyModified();
        }
    }
}
