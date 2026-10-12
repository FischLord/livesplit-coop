using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

public static class ClipboardTests {
    [DllImport("user32.dll",SetLastError=true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    static int passed;
    static void Check(bool condition,string label) { if(!condition)throw new Exception(label);passed++;Console.WriteLine("PASS: "+label); }
    static Button Find(Control parent,string text) {
        foreach(Control c in parent.Controls) { if(c is Button && c.Text==text)return (Button)c;var found=Find(c,text);if(found!=null)return found; }
        return null;
    }
    static void Click(Button button) { typeof(Button).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(button,new object[]{EventArgs.Empty}); }
    [STAThread] public static int Main(string[] args) {
        if(args.Length==1 && args[0]=="--lock") {
            for(int i=0;i<30;i++) {
                if(OpenClipboard(IntPtr.Zero)) { Console.WriteLine("LOCKED");try{Console.ReadLine();}finally{CloseClipboard();}return 0; }
                Thread.Sleep(50);
            }
            Console.WriteLine("LOCK FAILED");return 2;
        }
        try {
            string dll=System.IO.Path.GetFullPath(args[0]);string app=System.IO.Path.GetFullPath(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                string name=new AssemblyName(e.Name).Name+".dll";
                string file=System.IO.Path.Combine(app,name);
                if(!System.IO.File.Exists(file))file=System.IO.Path.Combine(app,"Components",name);
                return System.IO.File.Exists(file)?Assembly.LoadFrom(file):null;
            };
            Type type=Assembly.LoadFrom(dll).GetType("LiveSplit.Coop.CoopSettings",true);
            using(var settings=(Control)Activator.CreateInstance(type)) {
                var invite=(TextBox)type.GetField("invite",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(settings);
                var status=(Label)type.GetField("status",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(settings);
                var copy=Find(settings,"Copy");var join=Find(settings,"Join with invite");
                Check(copy!=null && join!=null,"actual Copy and Join buttons found");
                string code="lscoop:1:wss://relay.example/coop/test#"+new string('v',32);invite.Text=code;
                string room=(string)type.GetProperty("Room").GetValue(settings,null);
                string key=(string)type.GetProperty("Key").GetValue(settings,null);
                var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--lock") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardInput=true};
                using(var locker=Process.Start(info)) {
                    try {
                        Check(locker.StandardOutput.ReadLine()=="LOCKED","separate process holds real Windows clipboard lock");
                        var watch=Stopwatch.StartNew();Click(copy);watch.Stop();
                        Check(status.Text.Contains("Clipboard is busy"),"locked clipboard Copy reports status instead of throwing");
                        Check(invite.SelectedText==code,"complete invite selected for manual copy");
                        Check(watch.ElapsedMilliseconds<2000,"clipboard retries are bounded");
                        Click(join);
                        Check(!status.Text.Contains("Invite applied"),"locked clipboard Join does not claim success");
                        Check((string)type.GetProperty("Room").GetValue(settings,null)==room && (string)type.GetProperty("Key").GetValue(settings,null)==key,"failed clipboard read preserves room and key");
                    } finally {if(!locker.HasExited){locker.StandardInput.WriteLine();if(!locker.WaitForExit(3000))locker.Kill();}}
                }
                // Do not write a sample invitation to the user's real clipboard.
                Check(invite.Text==code,"clipboard failure preserves the invitation");
                invite.Text="";Click(copy);Check(status.Text.Contains("Create a room"),"empty invite asks for room creation without accessing clipboard");
            }
            Console.WriteLine("Clipboard regression checks passed: "+passed);return 0;
        } catch(Exception e) { Console.Error.WriteLine(e.GetBaseException().GetType().Name+": "+e.GetBaseException().Message);return 1; }
    }
}
