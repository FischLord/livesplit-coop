using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LiveSplit.Coop;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using LiveSplit.UI;

public static class DiagnosticsTests {
    static int passed;
    static readonly BindingFlags Hidden=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic;
    static Type Sink { get { return typeof(CoopComponent).Assembly.GetType("LiveSplit.Coop.RollingDiagnostics"); } }
    static Type Log { get { return typeof(CoopComponent).Assembly.GetType("LiveSplit.Coop.CoopDiagnostics"); } }
    static void Check(bool value,string label){if(!value)throw new Exception(label);passed++;Console.WriteLine("PASS: "+label);}
    static object Call(object sink,string method,params object[] args){return Sink.GetMethod(method,Hidden).Invoke(sink,args);}
    static void Write(object sink,string name,object data){Call(sink,"Write",name,data);}
    static string ReadText(string file){using(var reader=new StreamReader(new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)))return reader.ReadToEnd();}
    static void ValidJson(string file){foreach(var line in ReadText(file).Split(new[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries))new JavaScriptSerializer().DeserializeObject(line);}
    public static void Run(string root) {
        Directory.CreateDirectory(root);
        string logs=Path.Combine(root,"rotation");
        var sink=Activator.CreateInstance(Sink,Hidden,null,new object[]{logs,2048,3},null);
        try {
            for(int batch=0;batch<8;batch++){
                for(int i=0;i<12;i++)Write(sink,"test",new { seq=batch*12+i });
                Call(sink,"Flush");
            }
            var files=Directory.GetFiles(logs,"*.jsonl");
            Check(files.Length<=3,"rotated logs respect retention limit");
            Check(files.All(f=>new FileInfo(f).Length<=2048),"rotated files respect byte limit");
            foreach(var file in files)ValidJson(file);
            Check(true,"every rotated record is complete JSON");
            string exported=Path.Combine(root,"export.jsonl");Call(sink,"Export",exported);ValidJson(exported);
            Check(ReadText(exported).Contains("\"seq\":95"),"export flushes and includes latest event across rotation");
            bool blocked=false;try{Call(sink,"Export",files[0]);}catch(TargetInvocationException){blocked=true;}
            Check(blocked,"export refuses overwriting an active log file");
            Write(sink,"after_export",new { ok=true });Call(sink,"Flush");
            Check(Directory.GetFiles(logs).Any(f=>ReadText(f).Contains("after_export")),"logging continues after rejected export");
            ((IDisposable)Sink.GetField("writer",Hidden).GetValue(sink)).Dispose();
            Write(sink,"disk_failure",null);Call(sink,"Flush");
            Check(true,"failed write does not throw into caller");
            Write(sink,"recovered",null);Call(sink,"Flush");
            Check(Directory.GetFiles(logs).Any(f=>ReadText(f).Contains("recovered")),"logger resumes after write failure");
        } finally {((IDisposable)sink).Dispose();}
        Write(sink,"after_dispose",null);Call(sink,"Flush");Check(true,"late events and flush are harmless after disposal");

        string concurrent=Path.Combine(root,"concurrent");
        sink=Activator.CreateInstance(Sink,Hidden,null,new object[]{concurrent,1048576,3},null);
        try {
            Parallel.For(0,4000,i=>Write(sink,"parallel",new { seq=i }));
            Call(sink,"Flush");
            foreach(var file in Directory.GetFiles(concurrent))ValidJson(file);
            Check(true,"concurrent producers preserve complete records");
            string text=string.Join("\n",Directory.GetFiles(concurrent).Select(ReadText));
            Check(text.Contains("log_overflow"),"bounded queue reports overload without blocking producers");
        } finally {((IDisposable)sink).Dispose();}

        string secret="DO_NOT_EXPORT_lscoop:1:wss://private.example/coop/private#SECRET_HOST_KEY";
        var error=new InvalidOperationException(secret,new IOException(secret));
        Log.GetMethod("Error",Hidden).Invoke(null,new object[]{"test_failure",error});
        var run=new Run(new StandardComparisonGeneratorsFactory());run.Add(new Segment(secret));run.Add(new Segment("Finish"));
        run.GameName=secret;run.CategoryName=secret;
        var state=new LiveSplitState(run,null,new Layout(),new LayoutSettings(),new Settings());
        using(var component=new CoopComponent(state)) {
            Check(component.ContextMenuControls.ContainsKey("Coop: Export diagnostics..."),"diagnostic export is available in context menu");
            var model=new TimerModel { CurrentState=state };
            model.Start();model.Split();model.Pause();model.Pause();model.Split();model.Reset();
        }
        string integration=Path.Combine(root,"integration.jsonl");
        Log.GetMethod("Export",Hidden).Invoke(null,new object[]{integration});ValidJson(integration);
        string result=ReadText(integration);
        Check(!result.Contains(secret) && !result.Contains("SECRET_HOST_KEY") && !result.Contains("private.example"),"exception messages and private run text never enter diagnostic export");
        Check(result.Contains("System.InvalidOperationException") && result.Contains("System.IO.IOException") && result.Contains("hresult"),"error export retains exception chain types and HRESULTs");
        foreach(var action in new[]{"start","split","pause","resume","finish","reset"})
            Check(result.Contains("\"action\":\""+action+"\""),"real timer emits "+action+" event");
        Check(result.Contains("\"protocol\":3"),"diagnostics keep existing protocol version");
        Console.WriteLine(passed+" diagnostics checks passed");
    }
}
