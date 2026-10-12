using System;
using System.Collections.Generic;
using System.IO;
using LiveSplit.Model;

namespace LiveSplit.Coop {
    public static class RunProfile {
        public static Dictionary<string,string> Variables(IRun run) {
            var values=new Dictionary<string,string>(run.Metadata.VariableValueNames);
            // Older saved RV profiles lost their metadata. Only recover from the
            // exact RV profile folder structure; never infer player count from a room.
            if(run.GameName=="RV There Yet?" && !String.IsNullOrEmpty(run.FilePath)) {
                string path=run.FilePath.Replace('\\','/');
                string[] parts=path.Split('/');
                if(parts.Length>=5 && parts[parts.Length-5]=="Splits" &&
                   (parts[parts.Length-3]=="All Checkpoints" || parts[parts.Length-3]=="Finish Only") &&
                   (parts[parts.Length-2]=="No Major Glitches" || parts[parts.Length-2]=="Glitched")) {
                    string players=Path.GetFileNameWithoutExtension(path);
                    if(players=="Quad (Squad)")players="Squad";
                    if(players=="Solo" || players=="Duo" || players=="Trio" || players=="Squad") {
                        if(!values.ContainsKey("Number of Players"))values["Number of Players"]=players;
                        if(!values.ContainsKey("Glitch Type"))values["Glitch Type"]=parts[parts.Length-2]=="Glitched"?"Glitched ":"No Major Glitches";
                        values["RV Run Mode"]=parts[parts.Length-3];
                    }
                }
            }
            return values;
        }
        public static string Players(IRun run) {
            string value;
            return Variables(run).TryGetValue("Number of Players",out value)?value:"Players: ?";
        }
        public static string CategoryLabel(IRun run) {
            var values=Variables(run);
            string mode;
            if(!values.TryGetValue("RV Run Mode",out mode)) {
                string category=run.CategoryName ?? "";
                if(category.IndexOf("All Checkpoints",StringComparison.OrdinalIgnoreCase)>=0 ||
                   category.IndexOf("All Chekpoints",StringComparison.OrdinalIgnoreCase)>=0)mode="All Checkpoints";
                else if(run.GameName=="RV There Yet?" && run.Count==1 &&
                    category.IndexOf("waiting",StringComparison.OrdinalIgnoreCase)<0)mode="Finish Only";
            }
            if(mode=="All Checkpoints")mode="Fullgame";
            return Players(run)+(String.IsNullOrEmpty(mode)?"":" / "+mode);
        }
    }
}
