using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace LiveSplit.Coop {
    // The lobby this LiveSplit copy belongs to. Keys and the host's password are DPAPI-protected for this Windows user.
    public sealed class LobbyState {
        public string server,name,roomId,role,protectedHostKey,protectedViewerKey,protectedPassword;
        public bool isPublic,locked;
        public static LobbyState From(LobbyGrant grant,string role,string password) {
            bool host=role=="host";
            return new LobbyState { server=grant.server,name=grant.name,roomId=grant.roomId,role=role,isPublic=grant.isPublic,locked=grant.locked,
                protectedHostKey=host?LocalState.Protect(grant.hostKey ?? ""):"",
                protectedViewerKey=LocalState.Protect(grant.viewerKey ?? ""),
                protectedPassword=host?LocalState.Protect(password ?? ""):"" };
        }
        // Rooms from layouts before lobbies: no name, host keeps its invite (viewer) key for "copy invitation".
        public static LobbyState FromLegacy(string server,string room,string role,string key,string inviteKey) {
            bool host=role=="host";
            return new LobbyState { server=server,roomId=room,role=role,
                protectedHostKey=host?LocalState.Protect(key):"",
                protectedViewerKey=LocalState.Protect(host?(inviteKey ?? ""):key),protectedPassword="" };
        }
    }
    public sealed class LocalState {
        public int version=1;
        public string server,lastProfile;
        public LobbyState lobby;
        // Room ids of layout-stored rooms that were already moved here, so leaving one does not bring it back.
        public System.Collections.Generic.List<string> migratedLegacyRooms;
        public static string DefaultPath { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"coop-local.json"); } }
        public static LocalState Load(string path) {
            if(!File.Exists(path)) return new LocalState();
            try {
                var state=new JavaScriptSerializer().Deserialize<LocalState>(File.ReadAllText(path,Encoding.UTF8));
                return state ?? new LocalState();
            } catch(Exception e) {
                CoopDiagnostics.Error("local_state",e);
                try { File.Move(path,path+".broken-"+DateTime.Now.ToString("yyyyMMddHHmmss")); } catch(IOException) { }
                return new LocalState();
            }
        }
        // Written to a temporary file first so a crash never leaves half a file behind.
        public void Save(string path) {
            string temp=path+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),new UTF8Encoding(false));
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        }
        public static string Protect(string value) {
            if(string.IsNullOrEmpty(value)) return "";
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser));
        }
        // Throws CryptographicException when the value was protected by another Windows user or PC.
        public static string Unprotect(string value) {
            if(string.IsNullOrEmpty(value)) return "";
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser));
        }
    }
}
