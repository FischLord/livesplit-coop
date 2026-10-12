using System;
using System.Text.RegularExpressions;

namespace LiveSplit.Coop {
    // An invite carries everything a viewer needs in one pasteable line: lscoop:1:<server>/<room>#<viewer key>.
    // It is a credential like the key itself; it never contains the host key.
    public static class Invite {
        public const string Community="wss://livesplit-coop-relay.janneck.workers.dev/coop";
        const string Prefix="lscoop:1:";
        static readonly Regex Shape=new Regex("^(wss?://[^/?#@\\s]+/coop)/([a-zA-Z0-9_-]{1,64})#([^\\s#]{32,256})$");
        public static string Format(string server,string room,string viewerKey) {
            var text=Prefix+server.TrimEnd('/')+"/"+room+"#"+viewerKey;
            string s,r,k;if(!TryParse(text,out s,out r,out k)) throw new ArgumentException("Server, room or viewer key cannot form an invite.");
            return text;
        }
        public static bool TryParse(string text,out string server,out string room,out string key) {
            server=room=key=null;
            if(text==null) return false;text=text.Trim();
            if(!text.StartsWith(Prefix,StringComparison.Ordinal)) return false;
            var m=Shape.Match(text.Substring(Prefix.Length));if(!m.Success) return false;
            // The same rules RelayConnection enforces (wss, or ws only on loopback).
            try { new RelayConnection(m.Groups[1].Value,m.Groups[2].Value,"viewer",m.Groups[3].Value).Dispose(); } catch(ArgumentException) { return false; } catch(UriFormatException) { return false; }
            server=m.Groups[1].Value;room=m.Groups[2].Value;key=m.Groups[3].Value;return true;
        }
    }
}
