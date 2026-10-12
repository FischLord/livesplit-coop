using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiveSplit.Coop;
using LiveSplit.Model;

public static class RecorderTests {
    static int passed;
    static void Check(bool ok,string text){if(!ok)throw new Exception("FAIL: "+text);passed++;Console.WriteLine("PASS: "+text);}
    const long S=10000000L; // ticks per second
    // A host snapshot: three segments; splits/pb/best in seconds (null = none).
    public static Snapshot Snap(string attempt,string phase,int index,long?[] splits,long?[] pb,long?[] best,long? clock) {
        var names=new[]{"CP1","CP2","Finish"};
        var s=new Snapshot { seq=1,runId="run1",attemptId=attempt,game="Test Game",category="Any%",phase=phase,index=index,timingMethod="RealTime",
            comparison="Personal Best",attempts=5,offsetTicks=0,realTicks=clock.HasValue?(long?)(clock.Value*S):null,gameTicks=null,
            runVariables=new Dictionary<string,string>{{"Number of Players","Duo"}},
            segments=Enumerable.Range(0,3).Select(i=>new SegmentData { name=names[i],splitRT=splits[i].HasValue?(long?)(splits[i].Value*S):null,
                pbRT=pb[i].HasValue?(long?)(pb[i].Value*S):null,bestRT=best[i].HasValue?(long?)(best[i].Value*S):null,comparisons=new Dictionary<string,Pair>() }).ToArray() };
        return s;
    }
    static readonly long?[] None={null,null,null};
    public static void Run(string work) {
        Texts.German=false;
        FileTests(work);
        LibraryTests(work);
        RecorderTestsMain(work);
        AdoptionTests(work);
        FailureTests(work);
        OpenHereTests(work);
        Console.WriteLine(passed+" checks passed");
    }
    static void FileTests(string work) {
        var host=Snap("a1","NotRunning",-1,None,new long?[]{60,130,200},new long?[]{55,65,60},null);
        var run=RunFiles.FromSnapshot(host);
        Check(run.GameName=="Test Game" && run.CategoryName=="Any%" && run.Count==3 && run[2].Name=="Finish","run is built from the host's segments");
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200) && run[0].BestSegmentTime.RealTime==TimeSpan.FromSeconds(55),"host PB and golds are taken over");
        Check(run.Metadata.VariableValueNames["Number of Players"]=="Duo" && run.AttemptCount==0,"variables are copied, no attempts are invented");
        string path=Path.Combine(work,"files","Any%.lss");
        RunFiles.Save(run,path);
        Check(File.Exists(path) && !File.Exists(path+".tmp"),"save creates folders and leaves no temp file");
        var loaded=RunFiles.Load(path);
        Check(loaded.Count==3 && loaded[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200) && loaded.FilePath==path,"saved splits load back");
        Check(RunFiles.SameSegments(loaded,host),"same segment names match");
        host.segments[1].name="Other";
        Check(!RunFiles.SameSegments(loaded,host),"different segment names do not match");
        RunFiles.Save(loaded,path);
        Check(RunFiles.Load(path).Count==3,"saving over an existing file works");
    }
    static void LibraryTests(string work) {
        string fallback=Path.Combine(work,"lib","Splits","Coop");
        var plain=new ViewerLibrary(null,fallback);
        var s=Snap("a1","NotRunning",-1,None,None,None,null);
        Check(plain.PathFor(s)==Path.Combine(fallback,"Test Game","Any% (Duo).lss"),"without a pack splits go to Splits/Coop/<game>/<category> (<variables>).lss: "+plain.PathFor(s));
        s.game="Bad:Name?";s.category="A/B";
        Check(plain.PathFor(s)==Path.Combine(fallback,"Bad_Name_","A_B (Duo).lss"),"invalid file name characters are replaced");
        s.runVariables=null;
        Check(plain.PathFor(s)==Path.Combine(fallback,"Bad_Name_","A_B.lss"),"runs without variables have no suffix");
        string target=Path.Combine(work,"lib","x","Duo.lss");
        string pending=plain.PendingPath(target);
        Check(Path.GetDirectoryName(pending)==Path.Combine(fallback,"Pending") && Path.GetFileName(pending).StartsWith("Duo-") && pending.EndsWith(".lss"),"pending copies live under Splits/Coop/Pending");
        Check(plain.PendingPath(target)==pending && plain.PendingPath(Path.Combine(work,"lib","y","Duo.lss"))!=pending,"pending names are stable per target and distinct across targets");
        // Pack lookup: written by Plan 3's ProfilePack.Match.
        Directory.CreateDirectory(Path.Combine(work,"lib","pack"));
        File.WriteAllText(Path.Combine(work,"lib","pack","coop-profiles.json"),@"{""version"":1,""game"":""Test Game"",""dimensions"":[{""id"":""p"",""label"":{""en"":""P"",""de"":""P""},""options"":[{""id"":""duo"",""label"":""Duo""}]}],
            ""profiles"":[{""select"":{""p"":""duo""},""splits"":""Splits/Duo.lss"",""layout"":""Layouts/Host.lsl"",""category"":""Any%"",""variables"":{""Number of Players"":""Duo""}}]}");
        var pack=ProfilePack.Load(Path.Combine(work,"lib","pack","coop-profiles.json"));
        var withPack=new ViewerLibrary(pack,fallback);
        Check(withPack.PathFor(Snap("a1","NotRunning",-1,None,None,None,null))==pack.FullPath("Splits/Duo.lss"),"a matching pack profile is used");
        // Host lock
        string splits=Path.Combine(work,"lib","locked.lss");File.WriteAllText(splits,"x");
        Check(!HostLock.IsHeld(splits),"no lock file means not held");
        using(HostLock.Hold(splits)) {
            Check(HostLock.IsHeld(splits),"a held lock is seen");
            Check((File.GetAttributes(HostLock.PathFor(splits)) & FileAttributes.Hidden)!=0,"lock files are hidden");
        }
        Check(!File.Exists(HostLock.PathFor(splits)) && !HostLock.IsHeld(splits),"releasing removes the lock");
        File.WriteAllText(HostLock.PathFor(splits),"999999");
        Check(!HostLock.IsHeld(splits),"a lock from a process that no longer exists is ignored");
        File.WriteAllText(HostLock.PathFor(splits),"garbage");
        Check(!HostLock.IsHeld(splits),"an unreadable lock is ignored");
    }
    static void RecorderTestsMain(string work) {
        string fallback=Path.Combine(work,"rec","Splits","Coop");
        var clock=new DateTime(2026,10,12,12,0,0,DateTimeKind.Utc);
        var recorder=new AttemptRecorder(new ViewerLibrary(null,fallback),s=>false,()=>clock);
        long?[] pb={60,130,200},best={55,65,60};
        string target=new ViewerLibrary(null,fallback).PathFor(Snap("x","NotRunning",-1,None,pb,best,null));
        // First contact creates the file from the host's PB.
        recorder.Observe(Snap("a0","NotRunning",-1,None,pb,best,null),1);
        Check(File.Exists(target) && RunFiles.Load(target)[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200),"first contact creates splits with the host PB");
        Check(recorder.Note==Texts.T("recorder.created",Path.GetFileName(target)),"the viewer is told: "+recorder.Note);
        // A completed attempt with a new PB and a gold on CP2 (segment 120-58=62 < 65).
        recorder.Observe(Snap("a1","Running",0,None,pb,best,5),1);
        recorder.Observe(Snap("a1","Running",1,new long?[]{58,null,null},pb,best,70),1);
        recorder.Observe(Snap("a1","Running",2,new long?[]{58,120,null},pb,best,150),1);
        clock=clock.AddSeconds(190);
        recorder.Observe(Snap("a1","Ended",3,new long?[]{58,120,190},pb,best,190),1);
        Check(RunFiles.Load(target).AttemptCount==0,"nothing is written before the host resets");
        recorder.Observe(Snap("a2","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),1);
        var run=RunFiles.Load(target);
        Check(run.AttemptCount==1 && run.AttemptHistory.Count==1,"the completed attempt is counted");
        Check(run.AttemptHistory[0].Time.RealTime==TimeSpan.FromSeconds(190),"attempt history has the final time");
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(190) && run[1].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(120),"new PB is recorded");
        Check(run[1].BestSegmentTime.RealTime==TimeSpan.FromSeconds(62),"new gold is recorded");
        Check(run[0].SegmentHistory.ContainsKey(1) && run[0].SegmentHistory[1].RealTime==TimeSpan.FromSeconds(58),"segment history grows (key 0 is the PB seed LiveSplit adds on load)");
        Check(recorder.Note==Texts.T("recorder.saved",Path.GetFileName(target)),"saving is reported");
        // A reset attempt: counted, no PB change.
        recorder.Observe(Snap("a3","Running",1,new long?[]{70,null,null},new long?[]{58,120,190},new long?[]{55,62,60},90),1);
        recorder.Observe(Snap("a4","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),1);
        run=RunFiles.Load(target);
        Check(run.AttemptCount==2 && run.AttemptHistory.Count==2 && !run.AttemptHistory[1].Time.RealTime.HasValue,"a reset attempt is counted without a time");
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(190),"a reset keeps the PB");
        // A reconnect in the middle of an attempt drops it.
        recorder.Observe(Snap("a5","Running",1,new long?[]{60,null,null},None,None,80),1);
        recorder.Observe(Snap("a6","NotRunning",-1,None,None,None,null),2);
        Check(RunFiles.Load(target).AttemptCount==2,"attempts interrupted by a reconnect are not recorded");
        // Joining mid-attempt and seeing it end still records it.
        recorder.Observe(Snap("a7","Running",2,new long?[]{60,125,null},new long?[]{58,120,190},new long?[]{55,62,60},150),2);
        recorder.Observe(Snap("a7","Ended",3,new long?[]{60,125,195},new long?[]{58,120,190},new long?[]{55,62,60},195),2);
        recorder.Observe(Snap("a8","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),2);
        Check(RunFiles.Load(target).AttemptCount==3,"an attempt seen to its end is recorded");
        // Host lock: attempts go to Pending.
        var locked=new AttemptRecorder(new ViewerLibrary(null,fallback),s=>true,()=>clock);
        locked.Observe(Snap("b1","Running",0,None,new long?[]{58,120,190},new long?[]{55,62,60},5),1);
        locked.Observe(Snap("b2","NotRunning",-1,None,new long?[]{58,120,190},new long?[]{55,62,60},null),1);
        string pending=new ViewerLibrary(null,fallback).PendingPath(target);
        Check(File.Exists(pending) && RunFiles.Load(pending).AttemptCount==1 && RunFiles.Load(target).AttemptCount==3,"a locked target sends the attempt to Pending");
        Check(locked.Note==Texts.T("recorder.pending",pending),"pending is reported");
    }
    static void FailureTests(string work) {
        string fallback=Path.Combine(work,"fail","Splits","Coop");
        var library=new ViewerLibrary(null,fallback);
        long?[] pb={60,130,200},best={55,65,60};
        string target=library.PathFor(Snap("x","NotRunning",-1,None,pb,best,null));
        var recorder=new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow);
        recorder.Observe(Snap("f0","NotRunning",-1,None,pb,best,null),1);
        // The target becomes unwritable (a directory is in the way): the attempt goes to Pending, Observe never throws.
        File.Delete(target);Directory.CreateDirectory(target);
        string pending=library.PendingPath(target);
        recorder.Observe(Snap("f1","Running",1,new long?[]{58,null,null},pb,best,70),1);
        bool threw=false;
        try { recorder.Observe(Snap("f2","NotRunning",-1,None,pb,best,null),1); } catch(Exception) { threw=true; }
        Check(!threw,"a failing save never throws out of Observe");
        Check(File.Exists(pending) && RunFiles.Load(pending).AttemptCount==1,"the failed attempt is tried once more in Pending");
        Check(recorder.Note!=null && recorder.Note.Contains(pending),"the viewer is told where the attempt went: "+recorder.Note);
        // The attempt is finished only once: a following observation does not save it again.
        recorder.Observe(Snap("f2","NotRunning",-1,None,pb,best,null),1);
        Check(RunFiles.Load(pending).AttemptCount==1,"a failed attempt is not retried on the next observation");
        // Later attempts are still processed once the target works again.
        Directory.Delete(target);RunFiles.Save(RunFiles.FromSnapshot(Snap("x","NotRunning",-1,None,pb,best,null)),target);
        recorder.Observe(Snap("f3","Running",1,new long?[]{58,null,null},pb,best,70),1);
        recorder.Observe(Snap("f4","NotRunning",-1,None,pb,best,null),1);
        Check(RunFiles.Load(target).AttemptCount==1,"later attempts are recorded after a failure");
        // Both places fail: Note says so, still no exception and no repeated work.
        File.Delete(target);Directory.CreateDirectory(target);File.Delete(pending);Directory.CreateDirectory(pending);
        recorder.Observe(Snap("f5","Running",1,new long?[]{58,null,null},pb,best,70),1);
        threw=false;
        try { recorder.Observe(Snap("f6","NotRunning",-1,None,pb,best,null),1);recorder.Observe(Snap("f6","NotRunning",-1,None,pb,best,null),1); } catch(Exception) { threw=true; }
        Check(!threw && recorder.Note.StartsWith("Could not save the attempt"),"when nothing can be saved the note says so: "+recorder.Note);
        // First contact failures do not throw either.
        var fresh=new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow);
        threw=false;
        try { fresh.Observe(Snap("g0","NotRunning",-1,None,pb,best,null),1); } catch(Exception) { threw=true; }
        Check(!threw && fresh.Note!=null,"a failing first contact never throws");
    }
    static void OpenHereTests(string work) {
        string fallback=Path.Combine(work,"here","Splits","Coop");
        var library=new ViewerLibrary(null,fallback);
        long?[] pb={60,130,200},best={55,65,60};
        string target=library.PathFor(Snap("x","NotRunning",-1,None,pb,best,null));
        RunFiles.Save(RunFiles.FromSnapshot(Snap("x","NotRunning",-1,None,new long?[]{70,140,210},best,null)),target);
        string open=target.ToUpperInvariant();
        var recorder=new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow,p=>AttemptRecorder.SamePath(p,open));
        recorder.Observe(Snap("h0","NotRunning",-1,None,pb,best,null),1);
        Check(RunFiles.Load(target)[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(210),"splits open in this timer are not touched by first contact");
        recorder.Observe(Snap("h1","Running",1,new long?[]{58,null,null},pb,best,70),1);
        recorder.Observe(Snap("h2","NotRunning",-1,None,pb,best,null),1);
        string pending=library.PendingPath(target);
        Check(File.Exists(pending) && RunFiles.Load(pending).AttemptCount==1 && RunFiles.Load(target).AttemptCount==0,"an attempt for splits open in this timer goes to Pending");
        Check(recorder.Note==Texts.T("recorder.openLocally",pending),"the viewer is told: "+recorder.Note);
        Check(AttemptRecorder.SamePath(target,target.ToUpperInvariant()) && !AttemptRecorder.SamePath(target,null) && !AttemptRecorder.SamePath(target,"") && !AttemptRecorder.SamePath(target,"?:bad"),"path comparison ignores case and tolerates junk");
        Check(Texts.Has("recorder.openLocally"),"text exists: recorder.openLocally");
    }
    static void AdoptionTests(string work) {
        string fallback=Path.Combine(work,"adopt","Splits","Coop");
        var library=new ViewerLibrary(null,fallback);
        var mine=RunFiles.FromSnapshot(Snap("x","NotRunning",-1,None,new long?[]{70,140,210},new long?[]{50,70,70},null));
        string target=library.PathFor(Snap("x","NotRunning",-1,None,None,None,null));
        RunFiles.Save(mine,target);
        var recorder=new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow);
        recorder.Observe(Snap("h0","NotRunning",-1,None,new long?[]{60,130,200},new long?[]{55,65,60},null),1);
        var run=RunFiles.Load(target);
        Check(run[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200) && run[0].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(60),"a faster host PB replaces the local PB");
        Check(run[0].BestSegmentTime.RealTime==TimeSpan.FromSeconds(50) && run[1].BestSegmentTime.RealTime==TimeSpan.FromSeconds(65) && run[2].BestSegmentTime.RealTime==TimeSpan.FromSeconds(60),"only faster host golds are taken");
        Check(recorder.Note==Texts.T("recorder.adoptedPb",Path.GetFileName(target)),"adoption is reported");
        var slower=new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow);
        slower.Observe(Snap("h1","NotRunning",-1,None,new long?[]{70,150,230},new long?[]{60,80,80},null),1);
        Check(RunFiles.Load(target)[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200),"a slower host PB is ignored");
        var other=Snap("h2","NotRunning",-1,None,new long?[]{1,2,3},new long?[]{1,1,1},null);other.segments[0].name="Different";
        new AttemptRecorder(library,s=>false,()=>DateTime.UtcNow).Observe(other,1);
        Check(RunFiles.Load(target)[2].PersonalBestSplitTime.RealTime==TimeSpan.FromSeconds(200),"different segments never overwrite local splits");
    }
}
