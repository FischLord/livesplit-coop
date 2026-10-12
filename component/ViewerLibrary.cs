using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace LiveSplit.Coop {
    // Where a viewer keeps the splits of a host's profile: the matching pack profile, else Splits/Coop/<game>/<category> (<variables>).lss.
    public sealed class ViewerLibrary {
        readonly ProfilePack pack;
        readonly string fallbackRoot;
        public ViewerLibrary(ProfilePack pack,string fallbackRoot) { this.pack=pack;this.fallbackRoot=fallbackRoot; }
        public string PathFor(Snapshot s) {
            var profile=pack==null?null:pack.Match(s.game,s.category,s.runVariables);
            if(profile!=null) return pack.FullPath(profile.splits);
            string name=s.category;
            if(s.runVariables!=null && s.runVariables.Count>0)
                name+=" ("+string.Join(", ",s.runVariables.OrderBy(kv=>kv.Key,StringComparer.Ordinal).Select(kv=>kv.Value.Trim()).ToArray())+")";
            return Path.Combine(fallbackRoot,Safe(s.game),Safe(name)+".lss");
        }
        // A copy for attempts that cannot go into the target (a local host has it open, or the segments differ).
        public string PendingPath(string target) {
            string hash;
            using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(target).ToLowerInvariant()))).Replace("-","").Substring(0,8).ToLowerInvariant();
            return Path.Combine(fallbackRoot,"Pending",Safe(Path.GetFileNameWithoutExtension(target))+"-"+hash+".lss");
        }
        static string Safe(string text) {
            var invalid=Path.GetInvalidFileNameChars();
            string safe=new string((text ?? "").Select(c=>invalid.Contains(c)?'_':c).ToArray()).Trim();
            if(safe.Length>80) safe=safe.Substring(0,80);
            return safe.Length==0?"_":safe;
        }
    }
    // A hidden <splits>.coop-lock file with the process id while a host copy has these splits loaded.
    public static class HostLock {
        public static string PathFor(string splits) { return splits+".coop-lock"; }
        sealed class Held : IDisposable {
            readonly string path,owner;
            public Held(string path,string owner) { this.path=path;this.owner=owner; }
            public void Dispose() {
                try { if(File.Exists(path) && File.ReadAllText(path).Trim()==owner) { File.SetAttributes(path,FileAttributes.Normal);File.Delete(path); } }
                catch(IOException) { } catch(UnauthorizedAccessException) { }
            }
        }
        public static IDisposable Hold(string splits) {
            string path=PathFor(splits),owner=Process.GetCurrentProcess().Id.ToString();
            if(File.Exists(path)) File.SetAttributes(path,FileAttributes.Normal);
            File.WriteAllText(path,owner);
            File.SetAttributes(path,FileAttributes.Hidden);
            return new Held(path,owner);
        }
        public static bool IsHeld(string splits) {
            try {
                int pid;
                if(!File.Exists(PathFor(splits)) || !int.TryParse(File.ReadAllText(PathFor(splits)).Trim(),out pid)) return false;
                using(var process=Process.GetProcessById(pid)) return !process.HasExited;
            } catch(ArgumentException) { return false; }
            catch(IOException) { return false; }
            catch(UnauthorizedAccessException) { return true; }
        }
    }
}
