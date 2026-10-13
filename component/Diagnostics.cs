using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LiveSplit.Coop {
    // Only fixed event names, numeric data and enumerations belong here. Never pass
    // invitations, URLs, credentials, raw messages, exception messages or run names.
    internal static class CoopDiagnostics {
        static readonly Lazy<RollingDiagnostics> log=new Lazy<RollingDiagnostics>(Create);
        static RollingDiagnostics Create() {
            var basePath=Path.GetDirectoryName(typeof(CoopDiagnostics).Assembly.Location);
            if(string.Equals(Path.GetFileName(basePath),"Components",StringComparison.OrdinalIgnoreCase))basePath=Path.GetDirectoryName(basePath);
            foreach(var path in new[]{Path.Combine(basePath,"Logs","Coop"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveSplit.Coop","Logs")}) {
                try { var sink=new RollingDiagnostics(path,1048576,20);AppDomain.CurrentDomain.ProcessExit+=(s,e)=>sink.Dispose();return sink; } catch { }
            }
            return null;
        }
        internal static void Write(string name,object data) { try { var sink=log.Value;if(sink!=null)sink.Write(name,data); } catch { } }
        internal static void Error(string stage,Exception error) {
            // Messages and file-bearing stack traces can contain server responses or secrets.
            for(int depth=0;error!=null && depth<4;depth++,error=error.InnerException)
                Write("error",new { stage=stage,depth=depth,type=error.GetType().FullName,hresult=error.HResult,
                    method=error.TargetSite==null?null:error.TargetSite.Name });
        }
        internal static void Export(string destination) {
            var sink=log.Value;
            if(sink==null)throw new IOException("Diagnostics storage is unavailable.");
            sink.Export(destination);
        }
    }

    // UI/network threads only enqueue. One background timer batches disk I/O.
    internal sealed class RollingDiagnostics : IDisposable {
        readonly object gate=new object(),queueGate=new object();
        readonly Queue<string> queue=new Queue<string>();
        readonly Stopwatch elapsed=Stopwatch.StartNew();
        readonly string directory,prefix;
        readonly int maxBytes,maxFiles;
        readonly Timer timer;
        StreamWriter writer;
        long bytes,dropped;
        int part;
        bool disposed;
        internal RollingDiagnostics(string directory,int maxBytes,int maxFiles) {
            this.directory=directory;this.maxBytes=maxBytes;this.maxFiles=maxFiles;
            prefix="coop-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff")+"-"+Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(directory);OpenPart();
            Write("session",new { build="1.0.0",protocol=RelayConnection.Protocol,
                runtime=Environment.Version.ToString(),bits=IntPtr.Size*8 });
            timer=new Timer(_=>Flush(),null,1000,1000);
        }
        internal void Write(string name,object data) {
            try {
                string line=new JavaScriptSerializer().Serialize(new { utc=DateTime.UtcNow.ToString("o"),
                    elapsedMs=elapsed.ElapsedMilliseconds,session=prefix,eventName=name,data=data });
                lock(queueGate) {
                    if(disposed)return;
                    if(queue.Count>=512 || Encoding.UTF8.GetByteCount(line)>8192){dropped++;return;}
                    queue.Enqueue(line);
                }
            } catch { }
        }
        void OpenPart() {
            string file=Path.Combine(directory,prefix+"-"+(part++).ToString("D6")+".jsonl");
            writer=new StreamWriter(new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new UTF8Encoding(false));
            bytes=0;
            // Active writers hold their file open; pruning skips those files.
            foreach(var old in new DirectoryInfo(directory).GetFiles("coop-*.jsonl").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(maxFiles))
                try { old.Delete(); } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        void Append(string line) {
            int size=Encoding.UTF8.GetByteCount(line)+Encoding.UTF8.GetByteCount(Environment.NewLine);
            if(bytes>0 && bytes+size>maxBytes){writer.Dispose();writer=null;OpenPart();}
            writer.WriteLine(line);bytes+=size;
        }
        internal void Flush() {
            lock(gate) {
                if(disposed)return;
                try {
                    if(writer==null)OpenPart();
                    string[] batch;long lost;
                    lock(queueGate) { batch=queue.ToArray();queue.Clear();lost=dropped;dropped=0; }
                    if(lost>0)Append(new JavaScriptSerializer().Serialize(new { eventName="log_overflow",dropped=lost }));
                    foreach(var line in batch)Append(line);
                    writer.Flush();
                } catch { try { if(writer!=null)writer.Dispose(); } catch { } writer=null; }
            }
        }
        internal void Export(string destination) {
            lock(gate) {
                Flush();
                var files=Directory.GetFiles(directory,"coop-*.jsonl").OrderBy(p=>p).ToArray();
                if(files.Length==0 || writer==null)throw new IOException("Diagnostics could not be written. Check disk space and folder permissions.");
                if(files.Any(f=>string.Equals(Path.GetFullPath(f),Path.GetFullPath(destination),StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Choose a destination outside the active log files.");
                using(var output=new StreamWriter(destination,false,new UTF8Encoding(false)))
                    foreach(var file in files) {
                        using(var input=new StreamReader(new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))) {
                            string line;while((line=input.ReadLine())!=null)output.WriteLine(line);
                        }
                    }
            }
        }
        public void Dispose() {
            lock(gate) {
                if(disposed)return;
                timer.Dispose();Flush();
                lock(queueGate){disposed=true;queue.Clear();}
                try { if(writer!=null)writer.Dispose(); } catch { }
            }
        }
    }
}
