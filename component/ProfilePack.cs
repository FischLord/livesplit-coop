using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace LiveSplit.Coop {
    public sealed class PackOption { public string id,label; }
    public sealed class PackDimension {
        public string id;
        public Dictionary<string,string> label;
        public List<PackOption> options;
        public string DisplayLabel { get { string value;return label!=null && label.TryGetValue(Texts.German?"de":"en",out value)?value:id; } }
    }
    public sealed class PackProfile {
        public Dictionary<string,string> select,variables;
        public string splits,layout,label,category;
    }
    // A game's profiles (coop-profiles.json): which splits and layout belong to which selection. Paths are relative to the file.
    public sealed class ProfilePack {
        public const string FileName="coop-profiles.json";
        public int version;
        public string game,autosplitter,viewerLayout;
        public Dictionary<string,string> autosplitterSettings;
        public List<PackDimension> dimensions;
        public List<PackProfile> profiles;
        string root;
        public string Root { get { return root; } }
        // Looks next to the LiveSplit copy and up to three folders above it.
        public static ProfilePack Find(string startDirectory) {
            var dir=new DirectoryInfo(startDirectory);
            for(int i=0;i<4 && dir!=null;i++,dir=dir.Parent) {
                string path=Path.Combine(dir.FullName,FileName);
                if(File.Exists(path)) return Load(path);
            }
            return null;
        }
        public static ProfilePack Load(string path) {
            ProfilePack pack;
            try { pack=new JavaScriptSerializer().Deserialize<ProfilePack>(File.ReadAllText(path)); }
            catch(ArgumentException e) { throw new InvalidDataException("coop-profiles.json is not valid JSON: "+e.Message); }
            catch(InvalidOperationException e) { throw new InvalidDataException("coop-profiles.json has an unexpected shape: "+e.Message); }
            if(pack==null) throw new InvalidDataException("coop-profiles.json is empty.");
            pack.root=Path.GetDirectoryName(Path.GetFullPath(path));
            pack.Validate();
            return pack;
        }
        static void Require(bool ok,string message) { if(!ok) throw new InvalidDataException("coop-profiles.json: "+message); }
        static bool Relative(string path) { return !string.IsNullOrEmpty(path) && !Path.IsPathRooted(path) && !path.Replace('\\','/').Split('/').Contains(".."); }
        void Validate() {
            Require(version==1,"version must be 1");
            Require(!string.IsNullOrEmpty(game),"game is missing");
            Require(autosplitter==null || Relative(autosplitter),"autosplitter must be a relative path");
            Require(viewerLayout==null || Relative(viewerLayout),"viewerLayout must be a relative path");
            Require(dimensions!=null && dimensions.Count>0,"dimensions are missing");
            Require(dimensions.Select(d=>d.id).Distinct().Count()==dimensions.Count,"dimension ids must be unique");
            foreach(var d in dimensions) Require(!string.IsNullOrEmpty(d.id) && d.options!=null && d.options.Count>0 && d.options.All(o=>!string.IsNullOrEmpty(o.id) && !string.IsNullOrEmpty(o.label)),"dimension "+d.id+" needs options with id and label");
            Require(profiles!=null && profiles.Count>0,"profiles are missing");
            var keys=new HashSet<string>();
            foreach(var p in profiles) {
                Require(p.select!=null && p.select.Count==dimensions.Count && dimensions.All(d=>p.select.ContainsKey(d.id) && d.options.Any(o=>o.id==p.select[d.id])),"every profile must select one valid option per dimension");
                Require(keys.Add(Key(p)),"two profiles share the selection "+Key(p));
                Require(Relative(p.splits) && Relative(p.layout),"profile paths must be relative inside the pack");
                Require(p.label==null || p.label.Length<=60,"profile labels are limited to 60 characters");
                if(p.variables==null) p.variables=new Dictionary<string,string>();
            }
        }
        public string FullPath(string relative) { return Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))); }
        public string Key(PackProfile profile) { return string.Join("|",dimensions.Select(d=>profile.select[d.id]).ToArray()); }
        public PackProfile ByKey(string key) { return key==null?null:profiles.FirstOrDefault(p=>Key(p)==key); }
        public PackProfile Resolve(IDictionary<string,string> selection) {
            return profiles.FirstOrDefault(p=>dimensions.All(d=>{ string value;return selection.TryGetValue(d.id,out value) && p.select[d.id]==value; }));
        }
        public PackProfile ForSplits(string path) {
            if(string.IsNullOrEmpty(path)) return null;
            string full=Path.GetFullPath(path);
            return profiles.FirstOrDefault(p=>string.Equals(FullPath(p.splits),full,StringComparison.OrdinalIgnoreCase));
        }
        // The profile a host's run belongs to (game, category and every profile variable; values compared trimmed).
        public PackProfile Match(string gameName,string categoryName,IDictionary<string,string> values) {
            if(gameName!=game || values==null) return null;
            return profiles.FirstOrDefault(p=>p.category==categoryName && p.variables.All(kv=>{ string value;return values.TryGetValue(kv.Key,out value) && value!=null && value.Trim()==kv.Value.Trim(); }));
        }
    }
}
