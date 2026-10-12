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
}
