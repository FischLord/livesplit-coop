using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveSplit.Coop {
    // Small native dialogs for the timer's context menu. No room keys are shown.
    internal abstract class QuickDialog : Form {
        protected readonly TableLayoutPanel Body=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,AutoSize=true,Padding=new Padding(16) };
        protected QuickDialog(string title) {
            Text=title;Font=new Font("Segoe UI",9F);FormBorderStyle=FormBorderStyle.FixedDialog;
            StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MinimizeBox=MaximizeBox=false;
            AutoScaleMode=AutoScaleMode.Dpi;AutoSize=true;AutoSizeMode=AutoSizeMode.GrowAndShrink;
            MinimumSize=new Size(490,0);Controls.Add(Body);
        }
        protected Label Note(string text) {
            var label=new Label { Text=text,AutoSize=true,MaximumSize=new Size(455,0),Margin=new Padding(0,0,0,12) };
            Body.Controls.Add(label);return label;
        }
        protected FlowLayoutPanel Buttons() {
            var row=new FlowLayoutPanel { AutoSize=true,FlowDirection=FlowDirection.LeftToRight,Margin=new Padding(0,8,0,0) };
            Body.Controls.Add(row);return row;
        }
        protected Button Button(FlowLayoutPanel row,string name,string text) {
            var button=new Button { Name=name,Text=text,AutoSize=true,MinimumSize=new Size(100,30) };row.Controls.Add(button);return button;
        }
    }
    internal sealed class HostDialog : QuickDialog {
        readonly string server;
        readonly Label message;
        readonly Button reuse,create;
        readonly Func<string,RoomGrant> createRoom;
        readonly bool hasSaved;
        bool busy;
        public RoomGrant Grant { get;private set; }
        public HostDialog(string server,bool hasSaved,string room) : this(server,hasSaved,room,Invite.Create) {}
        // The room-creation function is injectable for deterministic dialog/error tests.
        internal HostDialog(string server,bool hasSaved,string room,Func<string,RoomGrant> createRoom) : base("Coop - Host a room") {
            this.server=server;this.createRoom=createRoom;this.hasSaved=hasSaved;
            Note("Choose a room. Hosting connects immediately; a new room's invitation is copied automatically.");
            Note("Server: "+server);
            message=Note(hasSaved?"Saved host room: "+room:"No saved host room. Create a new room to get started.");
            var buttons=Buttons();reuse=Button(buttons,"reuse","Use saved room");reuse.Enabled=hasSaved;
            create=Button(buttons,"create","Create new room");
            var cancel=Button(buttons,"cancel","Cancel");cancel.DialogResult=DialogResult.Cancel;CancelButton=cancel;
            AcceptButton=hasSaved?reuse:create;
            reuse.Click+=(s,e)=>{DialogResult=DialogResult.OK;};
            create.Click+=async(s,e)=>await CreateAsync();
        }
        async Task CreateAsync() {
            if(busy)return;busy=true;reuse.Enabled=create.Enabled=false;message.Text="Creating room...";
            try {
                var result=await Task.Run(()=>createRoom(server));
                if(IsDisposed || Disposing || DialogResult==DialogResult.Cancel)return;
                Grant=result;DialogResult=DialogResult.OK;
            } catch(Exception e) {
                if(!IsDisposed && !Disposing && DialogResult!=DialogResult.Cancel){message.Text=e.Message;create.Enabled=true;reuse.Enabled=hasSaved;}
            } finally {busy=false;}
        }
    }
    internal sealed class JoinDialog : QuickDialog {
        readonly TextBox input=new TextBox { Name="invite",Width=450,MaxLength=2048 };
        readonly Label message;
        readonly Func<string> problem;
        public string InviteText { get;private set; }
        public JoinDialog(Func<string> problem) : this(problem,ReadClipboard()) {}
        internal JoinDialog(Func<string> problem,string clipboard) : base("Coop - Join a room") {
            this.problem=problem;
            Note("Paste the host's invitation, then click Join. The host supplies the splits and times.");
            string server,room,key;
            if(Invite.TryParse(clipboard,out server,out room,out key))input.Text=clipboard.Trim();
            Body.Controls.Add(input);
            message=Note(input.Text.Length>0?"Invitation found in clipboard. Confirm with Join.":"Waiting for an invitation (lscoop:1:...).");
            var buttons=Buttons();var join=Button(buttons,"join","Join");var cancel=Button(buttons,"cancel","Cancel");
            cancel.DialogResult=DialogResult.Cancel;CancelButton=cancel;AcceptButton=join;
            join.Click+=(s,e)=>{
                string url,id,viewerKey;
                if(!Invite.TryParse(input.Text,out url,out id,out viewerKey)){message.Text="That invitation is not valid. Paste the complete code from the host.";return;}
                string blocked=problem==null?null:problem();
                if(blocked!=null){message.Text=blocked;return;}
                InviteText=input.Text.Trim();DialogResult=DialogResult.OK;
            };
            Shown+=(s,e)=>{input.Focus();input.SelectAll();};
        }
        static string ReadClipboard() { try{return Clipboard.GetText();}catch(ExternalException){return "";} }
    }
    internal sealed class InvitationDialog : QuickDialog {
        readonly TextBox code;
        readonly Label message;
        public InvitationDialog(CoopSettings settings,string feedback) : base("Coop - Room invitation") {
            Note("Share this invitation with your teammates. It only grants permission to view your timer.");
            code=new TextBox { Name="invite",Text=settings.InviteCode,ReadOnly=true,Width=450 };
            Body.Controls.Add(code);message=Note(feedback);
            Note("Save Layout in LiveSplit to keep this room for next time.");
            var buttons=Buttons();var copy=Button(buttons,"copy","Copy invite");var done=Button(buttons,"done","Done");
            done.DialogResult=DialogResult.OK;AcceptButton=done;CancelButton=done;
            copy.Click+=(s,e)=>{string result;settings.CopyInviteToClipboard(out result);message.Text=result;code.Focus();code.SelectAll();};
            Shown+=(s,e)=>{code.Focus();code.SelectAll();};
        }
    }
}
