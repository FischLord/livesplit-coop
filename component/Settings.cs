using System;
using System.Drawing;
using System.Security.Cryptography;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.Coop {
    // A room stored in a layout by versions before lobbies; read once and moved to coop-local.json.
    public sealed class LegacyRoom { public string server,roomId,role,key,inviteKey; }
    // Layout settings: only the role of this LiveSplit copy. Lobbies live in coop-local.json, never in layouts.
    public sealed class CoopSettings : UserControl {
        readonly ComboBox role=new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Width=290 };
        readonly Label status=new Label { AutoSize=true,MaximumSize=new Size(400,0) };
        public event Action OpenRequested;
        public string Role { get { return role.SelectedIndex==0?"host":"viewer"; } }
        public LegacyRoom Legacy { get; private set; }
        public CoopSettings() {
            AutoScroll=true;Size=new Size(460,200);
            role.Items.AddRange(new object[] { Texts.T("settings.roleHost"),Texts.T("settings.roleViewer") });role.SelectedIndex=1;
            var grid=new TableLayoutPanel { Dock=DockStyle.Top,ColumnCount=2,AutoSize=true,Padding=new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            grid.Controls.Add(new Label { Text=Texts.T("settings.role"),AutoSize=true },0,0);grid.Controls.Add(role,1,0);
            var open=new Button { Text=Texts.T("settings.open"),AutoSize=true };
            open.Click+=(s,e)=>{ if(OpenRequested!=null) OpenRequested(); };
            grid.Controls.Add(open,1,1);grid.Controls.Add(status,1,2);
            var note=new Label { Text=Texts.T("settings.note"),AutoSize=true,MaximumSize=new Size(400,0) };
            grid.Controls.Add(note,0,3);grid.SetColumnSpan(note,2);
            Controls.Add(grid);
        }
        public void Status(string message) { status.Text=message; }
        static string Read(XmlNode n,string name,string fallback) { var child=n[name];return child==null?fallback:child.InnerText; }
        public void LoadXml(XmlNode n) {
            role.SelectedIndex=Read(n,"Role","viewer")=="host"?0:1;
            Legacy=null;
            try {
                string key=LocalState.Unprotect(Read(n,"ProtectedKey",""));
                string room=Read(n,"Room","");
                if(key.Length>=32 && room.Length>0)
                    Legacy=new LegacyRoom { server=Read(n,"Address",Invite.Community),roomId=room,role=Role,key=key,inviteKey=LocalState.Unprotect(Read(n,"ProtectedInviteKey","")) };
            } catch(CryptographicException) { } catch(FormatException) { }
        }
        public XmlNode SaveXml(XmlDocument doc) {
            var node=doc.CreateElement("Settings");
            foreach(var item in new[] { new[] { "Version","2" },new[] { "Role",Role } }) { var child=doc.CreateElement(item[0]);child.InnerText=item[1];node.AppendChild(child); }
            return node;
        }
    }
}
