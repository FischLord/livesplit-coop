using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveSplit.Coop {
    // Lobby names and the viewer key derived from a lobby password. Must match docs/protocol.md "Lobbies".
    // The password itself never leaves this PC; the relay only sees (a hash of) the derived key.
    public static class LobbyKey {
        public const string RoomPrefix="lobby_";
        const string Salt="livesplit-coop-lobby:v1:";
        static readonly Regex Shape=new Regex("^[a-z0-9_-]{3,32}$");
        public static string Normalize(string raw) {
            if(raw==null) return null;
            string name=raw.Trim().ToLowerInvariant();
            return Shape.IsMatch(name)?name:null;
        }
        public static string RoomId(string name) { return RoomPrefix+name; }
        public static string DeriveViewerKey(string name,string password) {
            string normalized=Normalize(name);
            if(normalized==null) throw new ArgumentException("Invalid lobby name.");
            using(var kdf=new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(password ?? ""),Encoding.UTF8.GetBytes(Salt+normalized),100000,HashAlgorithmName.SHA256))
                return Convert.ToBase64String(kdf.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        }
    }
}
