using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveSplit.Coop {
    // What the coop window needs from the running component. Tests use a fake.
    public interface ICoopSession {
        string Role { get; }            // "host" or "viewer"; fixed by the layout of this LiveSplit copy
        string Server { get; set; }
        LobbyState Lobby { get; }       // null when this copy is in no lobby
        bool Connected { get; }
        int Viewers { get; }            // -1 when unknown
        string StopReason { get; }      // see RelayConnection.StopReason
        string StatusLine { get; }
        string SavedPassword { get; }   // host only, null when unknown
        string JoinProblem();           // why this copy cannot follow a host right now, or null
        string ShareText { get; }       // host: what teammates need to join (name and password, or an invitation); null when unknown
        bool ShareIsInvite { get; }     // ShareText is an invitation code
        string LastLobbyName { get; }   // host: the lobby that was left last and can be reopened, or null
        void ReopenLast();
        string LastFailure { get; }     // why the last UseLobby/JoinInvite was turned down (StopReason code, "autosplitter", "key_unreadable" or "rejected"); null otherwise
        string SaveWarning { get; }     // localized warning while coop-local.json cannot be written, else null
        string UseLobby(LobbyGrant grant,string password);   // null, or a warning to show (e.g. the lobby could not be saved)
        bool JoinInvite(string invite);
        void Reconnect();
        void Leave();
        void ExportDiagnostics(IWin32Window owner);
    }
    // One window for everything coop: optional profile section, lobby (create or join), advanced options.
    public sealed class CoopWindow : Form {
        readonly ICoopSession session;
        readonly Func<string,string,string,bool,LobbyGrant> create;
        readonly Func<string,List<LobbyInfo>> list;
        readonly TableLayoutPanel body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,AutoSize=true,Padding=new Padding(14) };
        readonly FlowLayoutPanel current=Stack(),createPanel=Stack(),joinPanel=Stack(),advanced=Stack();
        readonly Label lobbyInfo=new Label { Name="lobbyInfo",AutoSize=true,MaximumSize=new Size(480,0) };
        readonly Label status=new Label { Name="status",AutoSize=true,MaximumSize=new Size(480,0) };
        readonly Label saveWarning=new Label { Name="saveWarning",AutoSize=true,MaximumSize=new Size(480,0),ForeColor=Color.Firebrick,Visible=false };
        readonly Label message=new Label { Name="message",AutoSize=true,MaximumSize=new Size(480,0),Margin=new Padding(0,6,0,0) };
        readonly TextBox name=new TextBox { Name="lobbyName",Width=230,MaxLength=32 };
        readonly TextBox password=new TextBox { Name="lobbyPassword",Width=230,MaxLength=128,UseSystemPasswordChar=true };
        readonly CheckBox isPublic=new CheckBox { Name="lobbyPublic",AutoSize=true };
        readonly ListView lobbies=new ListView { Name="lobbyList",View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,Width=480,Height=150 };
        readonly TextBox server=new TextBox { Name="server",Width=360 };
        readonly TextBox invite=new TextBox { Name="invite",Width=360,MaxLength=2048 };
        readonly Timer refresh=new Timer { Interval=300 };
        bool busy,pendingJoin,slowShown;
        int pendingSince;
        public int SlowJoinMs=15000;
        public CoopWindow(ICoopSession session,Control profileSection) : this(session,profileSection,LobbyClient.Create,LobbyClient.List) { }
        public CoopWindow(ICoopSession session,Control profileSection,Func<string,string,string,bool,LobbyGrant> create,Func<string,List<LobbyInfo>> list) {
            this.session=session;this.create=create;this.list=list;
            Text=Texts.T("window.title");Font=new Font("Segoe UI",9F);FormBorderStyle=FormBorderStyle.FixedDialog;
            StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=MaximizeBox=false;
            AutoScaleMode=AutoScaleMode.Dpi;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;MinimumSize=new Size(530,0);
            Controls.Add(body);
            if(profileSection!=null) { body.Controls.Add(Heading("section.profile"));body.Controls.Add(profileSection); }
            body.Controls.Add(Heading("section.lobby"));body.Controls.Add(status);body.Controls.Add(saveWarning);
            BuildCurrent();
            if(session.Role=="host") BuildCreate(); else BuildJoin();
            body.Controls.Add(current);body.Controls.Add(createPanel);body.Controls.Add(joinPanel);body.Controls.Add(message);
            BuildAdvanced();
            var close=new Button { Name="close",Text=Texts.T("window.close"),AutoSize=true,MinimumSize=new Size(100,28),DialogResult=DialogResult.OK,Margin=new Padding(0,12,0,0) };
            CancelButton=close;body.Controls.Add(close);
            refresh.Tick+=(s,e)=>Sync();
            Shown+=(s,e)=>{ Sync();if(session.Role!="host" && session.Lobby==null) LoadList();refresh.Start(); };
            FormClosed+=(s,e)=>refresh.Stop();
        }
        static FlowLayoutPanel Stack() { return new FlowLayoutPanel { AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=new Padding(0,4,0,4) }; }
        static Label Heading(string key) { return new Label { Text=Texts.T(key),AutoSize=true,Font=new Font("Segoe UI",9F,FontStyle.Bold),Margin=new Padding(0,10,0,2) }; }
        static FlowLayoutPanel Row(params Control[] controls) { var row=new FlowLayoutPanel { AutoSize=true,WrapContents=false,Margin=new Padding(0) };row.Controls.AddRange(controls);return row; }
        static Label Caption(string key) { return new Label { Text=Texts.T(key),AutoSize=false,Width=130,Height=23,TextAlign=ContentAlignment.MiddleLeft }; }
        static Button Action(string name,string key) { return new Button { Name=name,Text=Texts.T(key),AutoSize=true,MinimumSize=new Size(100,28) }; }
        void BuildCurrent() {
            var reconnect=Action("reconnect","lobby.reconnect");reconnect.Click+=(s,e)=>{ message.Text="";session.Reconnect();Sync(); };
            var leave=Action("leave","lobby.leave");
            leave.Click+=(s,e)=>{ session.Leave();pendingJoin=busy=false;message.Text="";Sync();if(session.Role!="host") LoadList(); };
            var row=Row(reconnect,leave);
            if(session.Role=="host") { var copy=Action("copyPassword","lobby.copyPassword");copy.Click+=(s,e)=>CopyInfo();row.Controls.Add(copy); }
            current.Controls.Add(lobbyInfo);current.Controls.Add(row);
        }
        void BuildCreate() {
            isPublic.Text=Texts.T("lobby.public");
            var go=Action("create","lobby.create");go.Click+=async(s,e)=>await CreateAsync();
            createPanel.Controls.Add(Row(Caption("lobby.name"),name));
            createPanel.Controls.Add(Row(Caption("lobby.password"),password));
            createPanel.Controls.Add(isPublic);createPanel.Controls.Add(go);
            var reopen=Action("reopen","lobby.reopen");reopen.Click+=(s,e)=>{ message.Text="";session.ReopenLast();Sync(); };
            createPanel.Controls.Add(reopen);
        }
        void BuildJoin() {
            lobbies.Columns.Add(Texts.T("lobby.column.name"),130);lobbies.Columns.Add("",30);
            lobbies.Columns.Add(Texts.T("lobby.column.run"),200);lobbies.Columns.Add(Texts.T("lobby.column.online"),110);
            lobbies.SelectedIndexChanged+=(s,e)=>{ if(lobbies.SelectedItems.Count>0 && lobbies.SelectedItems[0].Tag is LobbyInfo) name.Text=((LobbyInfo)lobbies.SelectedItems[0].Tag).name; };
            lobbies.DoubleClick+=(s,e)=>password.Focus();
            var go=Action("join","lobby.join");go.Click+=async(s,e)=>await JoinAsync();
            var reload=Action("refresh","lobby.refresh");reload.Click+=(s,e)=>LoadList();
            joinPanel.Controls.Add(new Label { Text=Texts.T("lobby.publicList"),AutoSize=true });joinPanel.Controls.Add(lobbies);
            joinPanel.Controls.Add(Row(Caption("lobby.name"),name));
            joinPanel.Controls.Add(Row(Caption("lobby.password"),password));
            joinPanel.Controls.Add(Row(go,reload));
        }
        void BuildAdvanced() {
            var toggle=new LinkLabel { Name="advancedToggle",Text="+ "+Texts.T("section.advanced"),AutoSize=true,Margin=new Padding(0,10,0,2) };
            advanced.Visible=false;
            toggle.LinkClicked+=(s,e)=>{ advanced.Visible=!advanced.Visible;toggle.Text=(advanced.Visible?"- ":"+ ")+Texts.T("section.advanced"); };
            server.Text=session.Server;server.Leave+=(s,e)=>ApplyServer();
            advanced.Controls.Add(Row(Caption("advanced.server"),server));
            if(session.Role!="host") {
                var use=Action("joinInvite","advanced.invite");use.Click+=(s,e)=>JoinInvite();
                advanced.Controls.Add(Row(Caption("advanced.inviteCode"),invite));advanced.Controls.Add(use);
            }
            var diagnostics=Action("diagnostics","advanced.diagnostics");diagnostics.Click+=(s,e)=>session.ExportDiagnostics(this);
            advanced.Controls.Add(diagnostics);
            body.Controls.Add(toggle);body.Controls.Add(advanced);
        }
        string ApplyServer() {
            string value=server.Text.Trim();
            if(value.Length==0) { value=Invite.Community;server.Text=value; }
            if(value!=session.Server) session.Server=value;
            return value;
        }
        void Sync() {
            // The component forgets a rejected join by itself; the window only explains what happened.
            if(pendingJoin) {
                if(session.Lobby==null) { pendingJoin=busy=false;Report(Explain(session.LastFailure ?? "rejected"),true); }
                else if(session.Connected) { pendingJoin=busy=false;message.Text=""; }
                else if(!slowShown && unchecked(Environment.TickCount-pendingSince)>=SlowJoinMs) { slowShown=true;message.ForeColor=SystemColors.GrayText;message.Text=Texts.T("lobby.slow"); }
            }
            bool has=session.Lobby!=null;
            current.Visible=has;createPanel.Visible=!has && session.Role=="host";joinPanel.Visible=!has && session.Role!="host";
            if(!busy) status.Text=session.StatusLine;
            string warning=session.SaveWarning;saveWarning.Text=warning ?? "";saveWarning.Visible=warning!=null;
            Find("reconnect").Visible=has && !session.Connected && session.StopReason!=null && !pendingJoin;
            var lobby=session.Lobby;
            if(lobby!=null) lobbyInfo.Text=lobby.name==null?Texts.T("status.inviteRoom"):lobby.name+" · "+Texts.T(lobby.isPublic?"status.public":"status.private");
            var copy=Find("copyPassword");
            if(copy!=null) { copy.Visible=has && session.ShareText!=null;copy.Text=Texts.T(session.ShareIsInvite?"lobby.copyInvite":"lobby.copyPassword"); }
            var reopen=Find("reopen");
            if(reopen!=null) { string last=session.LastLobbyName;reopen.Visible=!has && last!=null;if(last!=null) reopen.Text=Texts.T("lobby.reopen",last); }
        }
        Control Find(string controlName) { var found=Controls.Find(controlName,true);return found.Length>0?found[0]:null; }
        public static string Explain(string reason) {
            switch(reason) {
                case "auth_failed": return Texts.T("lobby.wrongPassword");
                case "too_many": return Texts.T("lobby.tooMany");
                case "unknown_room": return Texts.T("lobby.unknown");
                case "replaced": return Texts.T("lobby.replaced");
                case "protocol": return Texts.T("lobby.protocol");
                case "autosplitter": return Texts.T("status.autosplitter");
                case "key_unreadable": return Texts.T("lobby.keyUnreadable");
                default: return Texts.T("lobby.rejected");
            }
        }
        async Task CreateAsync() {
            if(busy) return;
            message.Text="";
            string lobbyName=LobbyKey.Normalize(name.Text);
            if(lobbyName==null) { Report(Texts.T("lobby.invalidName"),true);return; }
            busy=true;string target=ApplyServer(),secret=password.Text;bool listed=isPublic.Checked;status.Text=Texts.T("lobby.creating");
            try {
                var grant=await Task.Run(()=>create(target,lobbyName,secret,listed));
                // The lobby exists on the server now, so the session gets it even if the window was closed meanwhile.
                string warning=session.UseLobby(grant,secret);
                if(Gone) return;
                bool copied=Copy(Info(grant.name,secret));
                if(warning!=null) Report(warning,true);
                else Report(CreatedText(grant.name,secret.Length>0,copied),false);
            } catch(Exception e) {
                if(!Gone) Report(e is LobbyException?e.Message:Texts.T("lobby.unreachable",e.Message),true);
            } finally {
                busy=false;
                if(!Gone) Sync();
            }
        }
        static string CreatedText(string lobby,bool hasPassword,bool copied) {
            if(hasPassword) return Texts.T(copied?"lobby.created":"lobby.createdNoCopy",lobby);
            return Texts.T(copied?"lobby.createdOpen":"lobby.createdOpenNoCopy",lobby);
        }
        async Task JoinAsync() {
            if(busy) return;
            message.Text="";
            string lobbyName=LobbyKey.Normalize(name.Text);
            if(lobbyName==null) { Report(Texts.T("lobby.invalidName"),true);return; }
            string problem=session.JoinProblem();
            if(problem!=null) { Report(problem,true);return; }
            busy=true;string target=ApplyServer(),secret=password.Text;status.Text=Texts.T("lobby.joining");
            try {
                // PBKDF2 takes a noticeable moment; keep the window responsive.
                var grant=await Task.Run(()=>new LobbyGrant { server=target,name=lobbyName,roomId=LobbyKey.RoomId(lobbyName),
                    viewerKey=LobbyKey.DeriveViewerKey(lobbyName,secret),locked=secret.Length>0 });
                if(Gone) return;
                StartPending();
                session.UseLobby(grant,null);
            } catch(Exception e) {
                busy=pendingJoin=false;
                if(!Gone) Report(Texts.T("lobby.unreachable",e.Message),true);
            }
            if(!Gone) Sync();
        }
        void JoinInvite() {
            if(busy) return;
            message.Text="";
            string problem=session.JoinProblem();
            if(problem!=null) { Report(problem,true);return; }
            if(!session.JoinInvite(invite.Text)) { Report(Texts.T("advanced.invalidInvite"),true);return; }
            StartPending();
            Sync();
        }
        // Async continuations must not touch controls once the window is closing or closed.
        bool Gone { get { return base.IsDisposed || !IsHandleCreated; } }
        void StartPending() { busy=pendingJoin=true;slowShown=false;pendingSince=Environment.TickCount; }
        async void LoadList() {
            string target=ApplyServer();
            lobbies.Items.Clear();lobbies.Items.Add(new ListViewItem(Texts.T("lobby.loading")));
            try {
                var found=await Task.Run(()=>list(target));
                if(Gone) return;
                lobbies.Items.Clear();
                foreach(var l in found) {
                    string run=l.label ?? ((l.game ?? "")+(l.category!=null?" · "+l.category:""));
                    string online=(l.hostOnline?"● ":"○ ")+l.viewers;
                    lobbies.Items.Add(new ListViewItem(new[] { l.name,l.locked?"🔒":"",run,online }) { Tag=l });
                }
                if(found.Count==0) lobbies.Items.Add(new ListViewItem(Texts.T("lobby.none")));
            } catch(Exception e) {
                if(Gone) return;
                lobbies.Items.Clear();
                Report(e is LobbyException?e.Message:Texts.T("lobby.unreachable",e.Message),true);
            }
        }
        public static string Info(string lobby,string secret) { return secret.Length>0?Texts.T("lobby.passwordInfo",lobby,secret):Texts.T("lobby.nameInfo",lobby); }
        static bool Copy(string text) {
            try { var data=new DataObject();data.SetText(text,TextDataFormat.UnicodeText);Clipboard.SetDataObject(data,true,3,75);return true; }
            catch(ExternalException) { return false; }
        }
        void CopyInfo() {
            string text=session.ShareText;
            if(text==null) return;
            Report(Copy(text)?Texts.T("copy.done"):Texts.T("copy.busy"),false);
        }
        void Report(string text,bool error) { message.ForeColor=error?Color.Firebrick:Color.SeaGreen;message.Text=text; }
        protected override void Dispose(bool disposing) { if(disposing) refresh.Dispose();base.Dispose(disposing); }
    }
}
