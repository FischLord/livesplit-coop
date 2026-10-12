using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using LiveSplit.Model;

namespace LiveSplit.Coop {
    // Loads a profile into the running LiveSplit window through TimerForm's own methods (the same ones its menus use).
    // If LiveSplit's internals differ, LiveSplit is restarted with the profile instead.
    public static class ProfileSwitcher {
        public enum Result { Switched,Cancelled,Restarted,Running }
        const BindingFlags Any=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        public static Result Switch(LiveSplitState state,object form,ProfilePack pack,PackProfile profile,Action commit) {
            return Switch(state,form,pack,profile,commit,Restart);
        }
        // commit runs once the user agreed and before anything loads; the coop component itself is replaced when the layout changes.
        public static Result Switch(LiveSplitState state,object form,ProfilePack pack,PackProfile profile,Action commit,Action<string,string> restart) {
            if(state.CurrentPhase!=TimerPhase.NotRunning) return Result.Running;
            var warn=Find(form,"WarnUserAboutSplitsSave",Type.EmptyTypes);
            var loadRun=Find(form,"LoadRunFromFile",new[] { typeof(string),typeof(TimingMethod?),typeof(string) });
            var openLayout=Find(form,"OpenLayoutFromFile",new[] { typeof(string) });
            if(warn!=null && !(bool)Call(warn,form)) return Result.Cancelled;
            ProfilePreparer.Prepare(pack,profile,"host");
            string splits=pack.FullPath(profile.splits),layout=pack.FullPath(profile.layout);
            if(commit!=null) commit();
            if(warn==null || loadRun==null || openLayout==null) {
                CoopDiagnostics.Write("profile_restart",new { reason="timer_form_methods_missing" });
                restart(splits,layout);return Result.Restarted;
            }
            Call(loadRun,form,splits,(TimingMethod?)state.CurrentTimingMethod,string.IsNullOrEmpty(state.CurrentHotkeyProfile)?"Default":state.CurrentHotkeyProfile);
            string current=state.Layout==null?null:state.Layout.FilePath;
            if(string.IsNullOrEmpty(current) || !string.Equals(Path.GetFullPath(current),layout,StringComparison.OrdinalIgnoreCase)) Call(openLayout,form,layout);
            return Result.Switched;
        }
        static MethodInfo Find(object form,string name,Type[] parameters) { return form==null?null:form.GetType().GetMethod(name,Any,null,parameters,null); }
        static object Call(MethodInfo method,object target,params object[] args) {
            try { return method.Invoke(target,args); }
            catch(TargetInvocationException e) { throw e.InnerException ?? e; }
        }
        static void Restart(string splits,string layout) {
            string exe=Process.GetCurrentProcess().MainModule.FileName;
            Process.Start(new ProcessStartInfo(exe,"-s \""+splits+"\" -l \""+layout+"\"") { WorkingDirectory=Path.GetDirectoryName(exe),UseShellExecute=false });
            foreach(Form open in Application.OpenForms) { if(open.Owner==null) { open.Close();break; } }
        }
    }
}
