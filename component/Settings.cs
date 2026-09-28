using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.Coop {
    public sealed class CoopSettings : UserControl {
        readonly TextBox address=new TextBox { Text=Invite.Community,Width=290 };
        readonly TextBox room=new TextBox { Width=290 };
        readonly TextBox key=new TextBox { UseSystemPasswordChar=true,Width=290 };
        readonly ComboBox role=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Width=290 };
        readonly Label status=new Label { AutoSize=true,MaximumSize=new Size(400,0),Text="Disconnected" };
        readonly Button create=new Button { Text="Create room (host)",AutoSize=true };
        readonly TextBox invite=new TextBox { ReadOnly=true,Width=220 };
        readonly Panel inviteRow=new FlowLayoutPanel { AutoSize=true,Visible=false,Margin=new Padding(0) };
        // The viewer key of a room this host created, kept so the invite can be copied again later.
        string inviteKey="";
        public event Action ConnectRequested;
        public event Action DisconnectRequested;
        // Set by the component: why this layout cannot view yet, or null.
        public Func<string> ViewerProblem;
        public string Address { get { return address.Text.Trim(); } }
        public string Room { get { return room.Text.Trim(); } }
        public string Key { get { return key.Text.Trim(); } }
        public string Role { get { return role.SelectedIndex==0?"host":"viewer"; } }
        public CoopSettings() {
            AutoScroll=true;Size=new Size(460,440);
            role.Items.AddRange(new object[]{"Host — publish this timer","Viewer — follow the host"});role.SelectedIndex=1;
            role.SelectedIndexChanged+=(s,e)=>ShowInvite();

            var outer=new TableLayoutPanel { Dock=DockStyle.Top,ColumnCount=1,AutoSize=true,Padding=new Padding(6) };

            var step1=new GroupBox { Text="1. Room",AutoSize=true,Dock=DockStyle.Top };
            var step1Grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,AutoSize=true,Padding=new Padding(5) };
            step1Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));step1Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            step1Grid.Controls.Add(new Label { Text="Server URL",AutoSize=true },0,0);step1Grid.Controls.Add(address,1,0);
            step1Grid.Controls.Add(new Label { Text="Room",AutoSize=true },0,1);step1Grid.Controls.Add(room,1,1);
            var join=new Button { Text="Join with invite",AutoSize=true };
            create.Click+=(s,e)=>CreateRoom();join.Click+=(s,e)=>JoinFromClipboard();
            var roomButtons=new FlowLayoutPanel { AutoSize=true,Margin=new Padding(0) };roomButtons.Controls.Add(create);roomButtons.Controls.Add(join);
            step1Grid.Controls.Add(roomButtons,1,2);
            var step1Note=new Label { AutoSize=true,MaximumSize=new Size(390,0),
                Text="Host: Create room makes a private room on this server and fills in everything. Teammates: copy the host's invite code (lscoop:…), then click Join with invite." };
            step1Grid.Controls.Add(step1Note,0,3);step1Grid.SetColumnSpan(step1Note,2);
            // Pasting an invite straight into the room field works too.
            room.TextChanged+=(s,e)=>{ if(room.Text.TrimStart().StartsWith("lscoop:",StringComparison.Ordinal)) UseInvite(room.Text); else Forget(); };
            address.TextChanged+=(s,e)=>Forget();
            step1.Controls.Add(step1Grid);

            var step2=new GroupBox { Text="2. Role and key",AutoSize=true,Dock=DockStyle.Top };
            var step2Grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,AutoSize=true,Padding=new Padding(5) };
            step2Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));step2Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            step2Grid.Controls.Add(new Label { Text="Role",AutoSize=true },0,0);step2Grid.Controls.Add(role,1,0);
            step2Grid.Controls.Add(new Label { Text="Access key",AutoSize=true },0,1);step2Grid.Controls.Add(key,1,1);
            var copy=new Button { Text="Copy",AutoSize=true };copy.Click+=(s,e)=>{ Clipboard.SetText(invite.Text);status.Text="Invite copied. Send it to your teammates privately."; };
            inviteRow.Controls.Add(invite);inviteRow.Controls.Add(copy);
            step2Grid.Controls.Add(new Label { Text="Invite",AutoSize=true },0,2);step2Grid.Controls.Add(inviteRow,1,2);
            var step2Note=new Label { AutoSize=true,MaximumSize=new Size(390,0),
                Text="Host and Viewer have separate keys; a Viewer key can't publish. Keys stay masked and never appear on the layout. Only share the invite, never the host key." };
            step2Grid.Controls.Add(step2Note,0,3);step2Grid.SetColumnSpan(step2Note,2);
            step2.Controls.Add(step2Grid);

            var step3=new GroupBox { Text="3. Connect",AutoSize=true,Dock=DockStyle.Top };
            var step3Panel=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,AutoSize=true,Padding=new Padding(5) };
            var buttons=new FlowLayoutPanel { AutoSize=true };
            var connect=new Button { Text="Connect",AutoSize=true };connect.Click+=(s,e)=>{ if(ConnectRequested!=null) ConnectRequested(); };
            var stop=new Button { Text="Disconnect",AutoSize=true };stop.Click+=(s,e)=>{ if(DisconnectRequested!=null) DisconnectRequested(); };
            buttons.Controls.Add(connect);buttons.Controls.Add(stop);
            step3Panel.Controls.Add(buttons);step3Panel.Controls.Add(status);
            step3.Controls.Add(step3Panel);

            var footer=new Label { AutoSize=true,MaximumSize=new Size(410,0),
                Text="Viewers: remove autosplitters first — the host controls timing and Disconnect restores your splits.\nKeys are encrypted for this Windows user/PC; connect and disconnect manually, no VPN needed." };

            outer.Controls.Add(step1);outer.Controls.Add(step2);outer.Controls.Add(step3);outer.Controls.Add(footer);
            Controls.Add(outer);
        }
        public void Status(string message) { status.Text=message; }
        // An invite belongs to exactly one server and room; editing either makes the stored viewer key meaningless.
        void Forget() { if(inviteKey.Length>0) { inviteKey="";ShowInvite(); } }
        void ShowInvite() {
            string text=null;
            if(inviteKey.Length>0 && Role=="host") try { text=Invite.Format(Address,Room,inviteKey); } catch(ArgumentException) { }
            invite.Text=text ?? "";inviteRow.Visible=text!=null;
        }
        void UseInvite(string text) {
            string server,id,viewerKey;
            if(!Invite.TryParse(text,out server,out id,out viewerKey)) { status.Text="That is not a valid invite code. Ask the host to copy it again.";return; }
            address.Text=server;room.Text=id;role.SelectedIndex=1;key.Text=viewerKey;inviteKey="";ShowInvite();
            // Tell viewers about an active autosplitter now rather than only when Connect fails.
            string problem=ViewerProblem==null?null:ViewerProblem();
            status.Text=problem==null?"Invite applied. Save the layout, then click Connect.":"Invite applied. "+problem;
        }
        void JoinFromClipboard() {
            string text=Clipboard.ContainsText()?Clipboard.GetText():"";
            if(!text.TrimStart().StartsWith("lscoop:",StringComparison.Ordinal)) { status.Text="Copy the host's invite code first (it starts with lscoop:), then click Join with invite.";return; }
            UseInvite(text);
        }
        void CreateRoom() {
            string server=Address;
            create.Enabled=false;status.Text="Creating a room…";
            Task.Run(()=>Invite.Create(server)).ContinueWith(t=>{
                if(IsDisposed) return;
                BeginInvoke((Action)(()=>{
                    create.Enabled=true;
                    if(t.IsFaulted) { var e=t.Exception.GetBaseException();status.Text=e is ArgumentException?"Enter a valid server URL (wss://…/coop) first.":e.Message;return; }
                    var grant=t.Result;
                    room.Text=grant.room;role.SelectedIndex=0;key.Text=grant.hostKey;inviteKey=grant.viewerKey;ShowInvite();
                    status.Text="Room created. Copy the invite for your teammates, save the layout, then click Connect.";
                }));
            });
        }
        // Keys are bound to this Windows user and PC; a layout copied elsewhere needs them entered again.
        static string Protect(string value) { return value.Length==0?"":Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),null,DataProtectionScope.CurrentUser)); }
        static string Unprotect(string value) { return value.Length==0?"":Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),null,DataProtectionScope.CurrentUser)); }
        static string Read(XmlNode n,string name,string fallback) { var child=n[name];return child==null?fallback:child.InnerText; }
        public void LoadXml(XmlNode n) {
            address.Text=Read(n,"Address",address.Text);room.Text=Read(n,"Room","coop");role.SelectedIndex=Read(n,"Role","viewer")=="host"?0:1;
            key.Text=inviteKey="";
            try { key.Text=Unprotect(Read(n,"ProtectedKey",""));inviteKey=Unprotect(Read(n,"ProtectedInviteKey","")); }
            catch { status.Text="Please enter the access key on this PC."; }
            ShowInvite();
        }
        public XmlNode SaveXml(XmlDocument doc) {
            var node=doc.CreateElement("Settings");
            var values=new[]{new[]{"Version","1"},new[]{"Address",Address},new[]{"Room",Room},new[]{"Role",Role},
                new[]{"ProtectedKey",Protect(Key)},new[]{"ProtectedInviteKey",Role=="host"?Protect(inviteKey):""}};
            foreach(var item in values) { var child=doc.CreateElement(item[0]);child.InnerText=item[1];node.AppendChild(child); }return node;
        }
    }
}
