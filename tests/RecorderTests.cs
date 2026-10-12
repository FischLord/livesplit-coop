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
}
