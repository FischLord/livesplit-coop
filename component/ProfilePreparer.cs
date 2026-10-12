using System;
using System.IO;
using System.Xml;

namespace LiveSplit.Coop {
    // Makes a profile's layout and splits ready to load. Never changes times, attempts or history.
    public static class ProfilePreparer {
        // Returns whether the layout file's content changed (a loaded copy of it is then out of date).
        public static bool Prepare(ProfilePack pack,PackProfile profile,string role) {
            string layoutPath=pack.FullPath(profile.layout),splitsPath=pack.FullPath(profile.splits);
            string layoutBefore=File.ReadAllText(layoutPath);
            var layout=new XmlDocument();layout.Load(layoutPath);
            var coop=layout.SelectSingleNode("/Layout/Components/Component[Path='LiveSplit.Coop.dll']/Settings");
            if(coop==null) throw new InvalidDataException("The layout has no Coop component: "+layoutPath);
            SetChild(coop,"Role",role);
            if(pack.autosplitter!=null) {
                var scripts=layout.SelectNodes("/Layout/Components/Component[Path='LiveSplit.ScriptableAutoSplit.dll']/Settings");
                if(scripts.Count!=1) throw new InvalidDataException("The layout needs exactly one Scriptable Auto Splitter: "+layoutPath);
                string asl=pack.FullPath(pack.autosplitter);
                if(!File.Exists(asl)) throw new FileNotFoundException("The autosplitter is missing. Unpack the complete package.",asl);
                var script=scripts[0];
                SetChild(script,"ScriptPath",asl);SetChild(script,"Start","True");SetChild(script,"Split","True");
                if(pack.autosplitterSettings!=null) foreach(var setting in pack.autosplitterSettings) {
                    foreach(XmlNode node in script.SelectNodes("CustomSettings/Setting")) if(node.Attributes["id"]!=null && node.Attributes["id"].Value==setting.Key) node.InnerText=setting.Value;
                }
            }
            Save(layout,layoutPath);
            bool layoutChanged=File.ReadAllText(layoutPath)!=layoutBefore;
            var run=new XmlDocument();run.Load(splitsPath);
            var variables=run.SelectSingleNode("/Run/Metadata/Variables");
            if(variables==null) {
                var metadata=run.SelectSingleNode("/Run/Metadata");
                if(metadata!=null) variables=metadata.AppendChild(run.CreateElement("Variables"));
            }
            if(variables!=null) foreach(var pair in profile.variables) {
                XmlElement variable=null;
                foreach(XmlNode node in variables.SelectNodes("Variable")) if(node.Attributes["name"]!=null && node.Attributes["name"].Value==pair.Key) variable=(XmlElement)node;
                if(variable==null) { variable=run.CreateElement("Variable");variable.SetAttribute("name",pair.Key);variables.AppendChild(variable); }
                variable.InnerText="";
                // LiveSplit trims plain XML text; CDATA keeps labels such as speedrun.com's "Glitched " intact.
                if(pair.Value!=pair.Value.Trim()) variable.AppendChild(run.CreateCDataSection(pair.Value)); else variable.InnerText=pair.Value;
            }
            // The pack's spelling is the speedrun.com category, so leaderboards and viewers' profiles match.
            if(!string.IsNullOrEmpty(profile.category)) SetChild(run.DocumentElement,"CategoryName",profile.category);
            SetChild(run.DocumentElement,"LayoutPath",layoutPath);
            Save(run,splitsPath);
            return layoutChanged;
        }
        // For starts outside LiveSplit: the profile becomes the most recent splits; registered autosplitters are turned off
        // because the layout owns the autosplitter (Edit Splits > Activate would add a second one).
        public static void RememberInSettings(string settingsPath,string splitsPath,string gameName,string categoryName) {
            var config=new XmlDocument();config.Load(settingsPath);
            var active=config.SelectSingleNode("/Settings/ActiveAutoSplitters");
            if(active!=null) active.RemoveAll();
            var recent=config.SelectSingleNode("/Settings/RecentSplits");
            if(recent==null) recent=config.DocumentElement.AppendChild(config.CreateElement("RecentSplits"));
            foreach(XmlNode old in recent.SelectNodes("SplitsFile")) if(string.Equals(old.InnerText,splitsPath,StringComparison.OrdinalIgnoreCase)) recent.RemoveChild(old);
            var entry=config.CreateElement("SplitsFile");entry.InnerText=splitsPath;
            entry.SetAttribute("gameName",gameName);entry.SetAttribute("categoryName",categoryName);
            entry.SetAttribute("lastTimingMethod","RealTime");entry.SetAttribute("lastHotkeyProfile","Default");
            recent.AppendChild(entry);
            Save(config,settingsPath);
        }
        static void SetChild(XmlNode parent,string name,string value) {
            var node=parent.SelectSingleNode(name);
            if(node==null) node=parent.AppendChild(parent.OwnerDocument.CreateElement(name));
            node.InnerText=value;
        }
        // Personal split files: write a complete copy first, then swap it in.
        static void Save(XmlDocument doc,string path) {
            string temp=path+".tmp";
            doc.Save(temp);
            File.Replace(temp,path,null);
        }
    }
}
