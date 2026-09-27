using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.Coop {
    public sealed class CoopSettings : UserControl {
        readonly TextBox address=new TextBox { Text="wss://timer.example.com/coop",Width=330 };
        readonly TextBox room=new TextBox { Text="coop",Width=330 };
        readonly TextBox key=new TextBox { UseSystemPasswordChar=true,Width=330 };
        readonly ComboBox role=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Width=330 };
        readonly Label status=new Label { AutoSize=true,MaximumSize=new Size(440,0),Text="Disconnected" };
        public event Action ConnectRequested;
        public event Action DisconnectRequested;
        public string Address { get { return address.Text.Trim(); } }
        public string Room { get { return room.Text.Trim(); } }
        public string Key { get { return key.Text.Trim(); } }
        public string Role { get { return role.SelectedIndex==0?"host":"viewer"; } }
        public CoopSettings() {
            Size=new Size(470,330);
            role.Items.AddRange(new object[]{"Host — publish this timer","Viewer — follow the host"});role.SelectedIndex=1;
            var grid=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=7,Padding=new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            string[] labels={"Server URL","Room","Role","Access key"};Control[] inputs={address,room,role,key};
            for(int i=0;i<inputs.Length;i++) { grid.Controls.Add(new Label { Text=labels[i],AutoSize=true },0,i);grid.Controls.Add(inputs[i],1,i); }
            var buttons=new FlowLayoutPanel { AutoSize=true };
            var connect=new Button { Text="Connect",AutoSize=true };connect.Click+=(s,e)=>{ if(ConnectRequested!=null) ConnectRequested(); };
            var stop=new Button { Text="Disconnect",AutoSize=true };stop.Click+=(s,e)=>{ if(DisconnectRequested!=null) DisconnectRequested(); };
            buttons.Controls.Add(connect);buttons.Controls.Add(stop);grid.Controls.Add(buttons,1,4);
            grid.Controls.Add(status,0,5);grid.SetColumnSpan(status,2);
            var note=new Label { AutoSize=true,MaximumSize=new Size(440,0),Text="Viewers: remove autosplitters from this layout first.\nThe host controls timing. Disconnect restores your original splits.\nKeys are saved encrypted for this Windows user and PC.\nConnections are started manually; no VPN is needed." };
            grid.Controls.Add(note,0,6);grid.SetColumnSpan(note,2);Controls.Add(grid);
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
