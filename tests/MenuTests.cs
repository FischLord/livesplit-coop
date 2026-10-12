using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.Coop;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using LiveSplit.UI;

public static class MenuTests {
    static int passed;
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Check(bool value,string text){if(!value)throw new Exception(text);passed++;Console.WriteLine("PASS: "+text);}
    static Control Find(Control parent,string name){foreach(Control c in parent.Controls){if(c.Name==name)return c;var child=Find(c,name);if(child!=null)return child;}return null;}
    static void Click(Control c){typeof(Button).GetMethod("OnClick",Hidden).Invoke(c,new object[]{EventArgs.Empty});}
    static Form New(string name,params object[] args){return (Form)Activator.CreateInstance(typeof(CoopSettings).Assembly.GetType("LiveSplit.Coop."+name),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,args,null);}
    static string Property(Form form,string name){return (string)form.GetType().GetProperty(name).GetValue(form,null);}
    static void Wait(Func<bool> ready){var clock=Stopwatch.StartNew();while(!ready() && clock.ElapsedMilliseconds<8000){Application.DoEvents();Thread.Sleep(10);}if(!ready())throw new Exception("Dialog timed out");}
    static LiveSplitState State(){var run=new Run(new StandardComparisonGeneratorsFactory());run.Add(new Segment("Finish"));return new LiveSplitState(run,null,new Layout(),new LiveSplit.Options.LayoutSettings(),new Settings());}
    public static void Run(string relay,string screenshotFolder) {
        string code="lscoop:1:wss://relay.example/coop/test#"+new string('v',32);
        Func<string> allowed=()=>null;
        using(var join=New("JoinDialog",allowed,"  "+code+"  ")) {
            Check(Find(join,"invite").Text==code,"valid clipboard invite is prefilled and trimmed");
            Check(join.DialogResult==DialogResult.None,"prefill does not join before confirmation");
            Click(Find(join,"join"));
            Check(join.DialogResult==DialogResult.OK && Property(join,"InviteText")==code,"Join explicitly accepts the invitation");
        }
        using(var join=New("JoinDialog",allowed,"unrelated clipboard text")) {
            Check(Find(join,"invite").Text=="","unrelated clipboard text is never exposed in the dialog");
            Find(join,"invite").Text="not an invite";Click(Find(join,"join"));
            Check(join.DialogResult!=DialogResult.OK && Property(join,"InviteText")==null,"invalid invitation cannot submit");
        }
        using(var join=New("JoinDialog",(Func<string>)(()=>"Disable the autosplitter first."),code)) {
            Click(Find(join,"join"));Check(join.DialogResult!=DialogResult.OK,"autosplitter guard blocks joining without accepting credentials");
        }
        using(var host=New("HostDialog",relay,true,"saved-room")) {
            Check(Find(host,"reuse").Enabled,"saved room remains available");
            Click(Find(host,"reuse"));Check(host.DialogResult==DialogResult.OK && host.GetType().GetProperty("Grant").GetValue(host,null)==null,"reuse accepts without creating another room");
        }
        using(var host=New("HostDialog",relay,false,"")) {
            Check(!Find(host,"reuse").Enabled,"no saved host room disables reuse");
            var task=(Task)host.GetType().GetMethod("CreateAsync",Hidden).Invoke(host,null);Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            var grant=(RoomGrant)host.GetType().GetProperty("Grant").GetValue(host,null);
            Check(host.DialogResult==DialogResult.OK && grant!=null,"actual local relay creates a room through the Host dialog");
            using(var settings=new CoopSettings()) {
                settings.ApplyHostGrant(grant);
                Check(settings.HasHostRoom && settings.Role=="host" && settings.Key==grant.hostKey,"new room applies correct host credentials");
                string url,id,viewerKey;
                Check(Invite.TryParse(settings.InviteCode,out url,out id,out viewerKey) && viewerKey==grant.viewerKey && !settings.InviteCode.Contains(grant.hostKey),"shareable invitation contains only the viewer key");
                var doc=new XmlDocument();var saved=settings.SaveXml(doc);
                using(var restored=new CoopSettings()) {restored.LoadXml(saved);Check(restored.HasHostRoom && restored.InviteCode==settings.InviteCode,"saved room and invitation survive encrypted settings round-trip");}
                using(var invitation=New("InvitationDialog",settings,"Invite copied. Send it to your teammates privately.")) {
                    Check(Find(invitation,"invite").Text==settings.InviteCode && ((TextBox)Find(invitation,"invite")).ReadOnly,"small confirmation dialog exposes the viewer invitation read-only");
                }
                Check(settings.TryApplyInvite(settings.InviteCode) && settings.Role=="viewer" && settings.Key==grant.viewerKey && !settings.HasHostRoom,"Join switches to viewer without retaining publishing privileges");
            }
        }
        Func<string,RoomGrant> fail=s=>{throw new InvalidOperationException("Test service unavailable");};
        using(var host=New("HostDialog",relay,true,"saved-room",fail)) {
            var task=(Task)host.GetType().GetMethod("CreateAsync",Hidden).Invoke(host,null);Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            Check(host.DialogResult!=DialogResult.OK && Find(host,"reuse").Enabled && Find(host,"create").Enabled,"room creation failure keeps dialog open and saved room reusable");
        }
        var release=new ManualResetEventSlim(false);
        Func<string,RoomGrant> delayed=s=>{release.Wait();return new RoomGrant {server=relay,room="cancelled",hostKey=new string('h',32),viewerKey=new string('v',32)};};
        using(var host=New("HostDialog",relay,false,"",delayed)) {
            var task=(Task)host.GetType().GetMethod("CreateAsync",Hidden).Invoke(host,null);host.DialogResult=DialogResult.Cancel;release.Set();Wait(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            Check(host.GetType().GetProperty("Grant").GetValue(host,null)==null,"cancelled room creation never applies a late result");
        }
        release.Dispose();
        var state=State();
        using(var component=new CoopComponent(state)) {
            var menu=component.ContextMenuControls;
            Check(new[]{"Coop: Host...","Coop: Join...","Coop: Copy invite","Coop: Reconnect","Coop: Disconnect"}.All(menu.ContainsKey) && !menu.ContainsKey("Coop: Connect"),"context menu exposes all five agreed actions without a redundant Connect entry");
            typeof(CoopComponent).GetField("quickNotice",Hidden).SetValue(component,"Invite copied");
            typeof(CoopComponent).GetField("quickNoticeUntil",Hidden).SetValue(component,0L);
            component.Update(null,state,360,800,LayoutMode.Vertical);
            Check(Field(component,"quickNotice")==null && (string)Field(component,"label")=="Coop: Disconnected","copy confirmation expires even when disconnected");
            state.CurrentPhase=TimerPhase.Running;
            Check(typeof(CoopComponent).GetMethod("JoinProblem",Hidden).Invoke(component,null)!=null,"running local attempt blocks quick joining");
        }
        ExerciseMenuActions(relay,screenshotFolder);
        // Actual WinForms controls drawn outside the visible desktop area.
        using(var host=New("HostDialog",Invite.Community,true,"saved-room"))Render(host,System.IO.Path.Combine(screenshotFolder,"host-dialog.png"));
        using(var join=New("JoinDialog",allowed,code))Render(join,System.IO.Path.Combine(screenshotFolder,"join-dialog.png"));
        Console.WriteLine("Menu checks passed: "+passed);
    }
    static object Field(object target,string name){return target.GetType().GetField(name,Hidden).GetValue(target);}
    static void Drive(Action action,Action<Form> handle) {
        Exception error=null;var clock=Stopwatch.StartNew();
        using(var timer=new System.Windows.Forms.Timer {Interval=15}) {
            timer.Tick+=(s,e)=>{
                try {
                    var forms=Application.OpenForms.Cast<Form>().Where(f=>f.GetType().Namespace=="LiveSplit.Coop").ToArray();
                    foreach(var form in forms) {
                        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);
                        if(clock.ElapsedMilliseconds>10000)throw new Exception("Context-menu dialog timed out");
                        handle(form);
                    }
                } catch(Exception ex) {error=ex;timer.Stop();foreach(Form form in Application.OpenForms.Cast<Form>().ToArray()){form.DialogResult=DialogResult.Cancel;form.Close();}}
            };
            timer.Start();action();timer.Stop();if(error!=null)throw error;
        }
    }
    static void ExerciseMenuActions(string relay,string folder) {
        var hostState=State();hostState.Run.GameName="Menu integration";hostState.Run.CategoryName="Shared run";hostState.CurrentComparison="Personal Best";
        var viewerState=State();var original=viewerState.Run;viewerState.CurrentComparison="Personal Best";
        using(var host=new CoopComponent(hostState))using(var viewer=new CoopComponent(viewerState)) {
            var config=new XmlDocument();config.LoadXml("<Settings><Address>"+relay+"</Address><Room/><Role>host</Role><ProtectedKey/></Settings>");host.SetSettings(config.DocumentElement);
            // Keep the user's clipboard intact while testing automatic Copy and its manual fallback.
            string lockProgram=System.IO.Path.GetFullPath(System.IO.Path.Combine(folder,"..","clipboard-tests","ClipboardTests.exe"));
            var start=new ProcessStartInfo(lockProgram,"--lock"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardInput=true};
            using(var locker=Process.Start(start)) {
                try {
                    if(locker.StandardOutput.ReadLine()!="LOCKED")throw new Exception("Could not hold clipboard for menu integration test");
                    bool requested=false,confirmation=false;
                    Drive(host.ContextMenuControls["Coop: Host..."],form=>{
                        if(form.GetType().Name=="HostDialog" && !requested){requested=true;Click(Find(form,"create"));}
                        if(form.GetType().Name=="InvitationDialog"){confirmation=true;Click(Find(form,"done"));}
                    });
                    Check(requested && confirmation && hostState.Layout.HasChanged,"actual Host action creates a room, offers copied/manual invitation and marks layout for saving");
                    Wait(()=>((RelayConnection)Field(host,"connection")).Ready);
                    Check(((RelayConnection)Field(host,"connection")).Ready,"actual Host action connects without an extra Connect click");
                    var hostSettings=(CoopSettings)Field(host,"settings");string code=hostSettings.InviteCode;string room=hostSettings.Room;
                    bool joined=false;
                    Drive(viewer.ContextMenuControls["Coop: Join..."],form=>{
                        if(form.GetType().Name=="JoinDialog" && !joined){joined=true;Find(form,"invite").Text=code;Click(Find(form,"join"));}
                    });
                    Wait(()=>((RelayConnection)Field(viewer,"connection")).Ready);
                    Check(joined && viewerState.Layout.HasChanged,"actual Join action applies invitation and connects without another Connect click");
                    var model=new TimerModel {CurrentState=hostState};model.Start();
                    Wait(()=>{host.Update(null,hostState,360,800,LayoutMode.Vertical);viewer.Update(null,viewerState,360,800,LayoutMode.Vertical);return viewerState.CurrentPhase==TimerPhase.Running;});
                    model.Split();
                    Wait(()=>{host.Update(null,hostState,360,800,LayoutMode.Vertical);viewer.Update(null,viewerState,360,800,LayoutMode.Vertical);return viewerState.CurrentPhase==TimerPhase.Ended;});
                    Check(viewerState.CurrentTime.RealTime==hostState.CurrentTime.RealTime,"timers connected through quick actions share exact completed time");
                    viewer.ContextMenuControls["Coop: Disconnect"]();
                    Check(Object.ReferenceEquals(viewerState.Run,original),"menu Disconnect restores original viewer run");
                    viewer.ContextMenuControls["Coop: Reconnect"]();Wait(()=>((RelayConnection)Field(viewer,"connection")).Ready);
                    Check(((RelayConnection)Field(viewer,"connection")).Ready,"menu Reconnect reuses saved credentials");
                    bool reused=false;
                    Drive(host.ContextMenuControls["Coop: Host..."],form=>{if(form.GetType().Name=="HostDialog" && !reused){reused=true;Click(Find(form,"reuse"));}});
                    Check(reused && hostSettings.Room==room && hostSettings.InviteCode==code,"actual Host reuse preserves room and invitation");
                    bool copied=false;
                    Drive(host.ContextMenuControls["Coop: Copy invite"],form=>{if(form.GetType().Name=="InvitationDialog"){copied=Find(form,"invite").Text==code;Click(Find(form,"done"));}});
                    Check(copied,"menu Copy invite offers the full code when clipboard is locked");
                } finally {if(!locker.HasExited){locker.StandardInput.WriteLine();if(!locker.WaitForExit(3000))locker.Kill();}}
            }
        }
    }
    static void Render(Form form,string path) {
        form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-10000,-10000);form.Show();Application.DoEvents();form.PerformLayout();
        using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size));bitmap.Save(path);}
    }
}
