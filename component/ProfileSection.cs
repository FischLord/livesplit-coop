using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace LiveSplit.Coop {
    // Profile part of the coop window: one list per pack dimension and "Load profile". Viewers only see the host's profile.
    public sealed class ProfileSection : FlowLayoutPanel {
        sealed class Choice { public string id,label;public override string ToString() { return label; } }
        readonly ProfilePack pack;
        readonly PackProfile current;
        readonly bool running;
        readonly Dictionary<string,ComboBox> boxes=new Dictionary<string,ComboBox>();
        readonly Button load;
        readonly Label hint=new Label { Name="profileHint",AutoSize=true,MaximumSize=new Size(480,0) };
        public PackProfile Chosen { get; private set; }
        public ProfileSection(ProfilePack pack,PackProfile current,bool viewer,string hostLabel,bool running) {
            this.pack=pack;this.current=current;this.running=running;
            FlowDirection=FlowDirection.TopDown;WrapContents=false;AutoSize=true;Margin=new Padding(0,0,0,4);
            if(viewer) {
                Controls.Add(new Label { Name="profileFromHost",AutoSize=true,MaximumSize=new Size(480,0),Text=Texts.T("profile.fromHost",hostLabel ?? Texts.T("profile.unknown")) });
                return;
            }
            foreach(var dimension in pack.dimensions) {
                var box=new ComboBox { Name="dim_"+dimension.id,DropDownStyle=ComboBoxStyle.DropDownList,Width=230 };
                foreach(var option in dimension.options) box.Items.Add(new Choice { id=option.id,label=option.label });
                string wanted=current!=null?current.select[dimension.id]:null;
                int index=box.Items.Cast<Choice>().ToList().FindIndex(c=>c.id==wanted);
                box.SelectedIndex=index>=0?index:0;
                box.SelectedIndexChanged+=(s,e)=>UpdateState();
                boxes[dimension.id]=box;
                var row=new FlowLayoutPanel { AutoSize=true,WrapContents=false,Margin=new Padding(0) };
                row.Controls.Add(new Label { Text=dimension.Label,AutoSize=false,Width=130,Height=23,TextAlign=ContentAlignment.MiddleLeft });row.Controls.Add(box);
                Controls.Add(row);
            }
            load=new Button { Name="loadProfile",Text=Texts.T("profile.load"),AutoSize=true,MinimumSize=new Size(100,28) };
            load.Click+=(s,e)=>{
                var selected=Selected();
                if(selected==null) return;
                Chosen=selected;
                var form=FindForm();
                if(form!=null && form.Modal) form.DialogResult=DialogResult.OK;
            };
            Controls.Add(load);Controls.Add(hint);
            UpdateState();
        }
        PackProfile Selected() {
            var selection=boxes.ToDictionary(kv=>kv.Key,kv=>kv.Value.SelectedItem==null?null:((Choice)kv.Value.SelectedItem).id);
            return pack.Resolve(selection);
        }
        void UpdateState() {
            var selected=Selected();
            load.Enabled=selected!=null && selected!=current && !running;
            hint.Text=running?Texts.T("profile.running"):selected==null?Texts.T("profile.missing"):selected==current?Texts.T("profile.current"):"";
        }
    }
}
