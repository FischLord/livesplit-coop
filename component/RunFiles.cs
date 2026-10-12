using System;
using System.IO;
using System.Linq;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Model.RunFactories;
using LiveSplit.Model.RunSavers;

namespace LiveSplit.Coop {
    // Loading and saving personal split files with LiveSplit's own reader and writer.
    public static class RunFiles {
        public static IRun Load(string path) {
            using(var stream=File.OpenRead(path)) {
                var run=new StandardFormatsRunFactory(stream,path).Create(new StandardComparisonGeneratorsFactory());
                run.FilePath=path;
                return run;
            }
        }
        // A complete copy is written first and then swapped in, so a crash never truncates someone's splits.
        public static void Save(IRun run,string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp=path+".tmp";
            using(var stream=File.Create(temp)) new XMLRunSaver().Save(run,stream);
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
            run.FilePath=path;run.HasChanged=false;
        }
        static Time Time(long? real,long? game) { return new Time(real.HasValue?(TimeSpan?)TimeSpan.FromTicks(real.Value):null,game.HasValue?(TimeSpan?)TimeSpan.FromTicks(game.Value):null); }
        // New splits for a viewer who never ran this profile: the host's segments, PB and golds, no attempts.
        public static IRun FromSnapshot(Snapshot s) {
            var run=new Run(new StandardComparisonGeneratorsFactory());
            run.GameName=s.game;run.CategoryName=s.category;
            if(s.runVariables!=null) foreach(var pair in s.runVariables) run.Metadata.VariableValueNames[pair.Key]=pair.Value;
            foreach(var data in s.segments) {
                var segment=new Segment(data.name);
                segment.PersonalBestSplitTime=Time(data.pbRT,data.pbGT);
                segment.BestSegmentTime=Time(data.bestRT,data.bestGT);
                run.Add(segment);
            }
            run.HasChanged=true;
            return run;
        }
        public static bool SameSegments(IRun run,Snapshot s) {
            return run.Count==s.segments.Length && run.Select(seg=>seg.Name).SequenceEqual(s.segments.Select(seg=>seg.name));
        }
    }
}
