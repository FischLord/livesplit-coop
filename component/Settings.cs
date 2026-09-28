using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.Coop {
    public sealed class CoopSettings : UserControl {
        readonly TextBox address=new TextBox { Width=290 };
        readonly TextBox room=new TextBox { Text="coop",Width=290 };
        readonly TextBox key=new TextBox { UseSystemPasswordChar=true,Width=290 };
        readonly ComboBox role=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Width=290 };
        readonly Label status=new Label { AutoSize=true,MaximumSize=new Size(400,0),Text="Disconnected" };
        public event Action ConnectRequested;
        public event Action DisconnectRequested;
        public string Address { get { return address.Text.Trim(); } }
        public string Room { get { return room.Text.Trim(); } }
        public string Key { get { return key.Text.Trim(); } }
        public string Role { get { return role.SelectedIndex==0?"host":"viewer"; } }
        public CoopSettings() {
            AutoScroll=true;Size=new Size(460,440);
            role.Items.AddRange(new object[]{"Host — publish this timer","Viewer — follow the host"});role.SelectedIndex=1;

            var outer=new TableLayoutPanel { Dock=DockStyle.Top,ColumnCount=1,AutoSize=true,Padding=new Padding(6) };

            var step1=new GroupBox { Text="1. Find the room",AutoSize=true,Dock=DockStyle.Top };
            var step1Grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,AutoSize=true,Padding=new Padding(5) };
            step1Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));step1Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            step1Grid.Controls.Add(new Label { Text="Server URL",AutoSize=true },0,0);step1Grid.Controls.Add(address,1,0);
            step1Grid.Controls.Add(new Label { Text="Room",AutoSize=true },0,1);step1Grid.Controls.Add(room,1,1);
            var step1Note=new Label { AutoSize=true,MaximumSize=new Size(390,0),
                Text="Ask your relay operator for the URL and room name — this component only connects to rooms; it never creates, lists, or invites them." };
            step1Grid.Controls.Add(step1Note,0,2);step1Grid.SetColumnSpan(step1Note,2);
            step1.Controls.Add(step1Grid);

            var step2=new GroupBox { Text="2. Identify yourself",AutoSize=true,Dock=DockStyle.Top };
            var step2Grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,AutoSize=true,Padding=new Padding(5) };
            step2Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));step2Grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            step2Grid.Controls.Add(new Label { Text="Role",AutoSize=true },0,0);step2Grid.Controls.Add(role,1,0);
            step2Grid.Controls.Add(new Label { Text="Access key",AutoSize=true },0,1);step2Grid.Controls.Add(key,1,1);
            var step2Note=new Label { AutoSize=true,MaximumSize=new Size(390,0),
                Text="Host and Viewer get separate keys from the operator; a Viewer key can't publish. The key stays masked and is never shown on the layout." };
            step2Grid.Controls.Add(step2Note,0,2);step2Grid.SetColumnSpan(step2Note,2);
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
        static string Read(XmlNode n,string name,string fallback) { var child=n[name];return child==null?fallback:child.InnerText; }
        public void LoadXml(XmlNode n) {
            address.Text=Read(n,"Address",address.Text);room.Text=Read(n,"Room","coop");role.SelectedIndex=Read(n,"Role","viewer")=="host"?0:1;
            string encoded=Read(n,"ProtectedKey","");key.Text="";
            if(encoded.Length>0) try { key.Text=Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encoded),null,DataProtectionScope.CurrentUser)); }
            catch { status.Text="Please enter the access key on this PC."; }
        }
        public XmlNode SaveXml(XmlDocument doc) {
            var node=doc.CreateElement("Settings");
            var values=new[]{new[]{"Version","1"},new[]{"Address",Address},new[]{"Room",Room},new[]{"Role",Role},
                new[]{"ProtectedKey",Key.Length==0?"":Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(Key),null,DataProtectionScope.CurrentUser))}};
            foreach(var item in values) { var child=doc.CreateElement(item[0]);child.InnerText=item[1];node.AppendChild(child); }return node;
        }
    }
}
