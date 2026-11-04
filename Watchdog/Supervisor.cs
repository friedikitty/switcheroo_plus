using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Switcheroo.Shell;

namespace Switcheroo.Watchdog
{
    internal sealed class Supervisor
    {
        readonly object sync = new object();
        readonly object captureLock = new object();
        readonly ConcurrentQueue<string> commands = new ConcurrentQueue<string>();
        readonly Queue<string> logs = new Queue<string>();
        readonly string root, corePath;
        readonly bool auto;
        Thread debugThread;
        Process core;
        IntPtr job;
        volatile bool intentional, exiting, capturing, crashPending;
        string state = "Stopped", lastIncident = "", lastError = "";
        DateTime started, nextProbe, lastHealthy;
        bool hangCaptured;
        string testCaptureMode;
        public event Action Changed;
        public event Action<string> Incident;
        public event Action ExitReady;
        public Supervisor(string corePath, bool auto)
        {
            this.corePath = corePath; this.auto=auto;
            root = SwitcherooConfig.ConfigDirectory;
            ShellProtocol.Serve(ShellProtocol.WatchdogPipe, Dispatch);
            var worker=new Thread(Loop) { IsBackground=true, Name="Supervisor" };
            worker.Start();
        }
        public object Dispatch(string action)
        {
            if (action == "status" || action == "ping") return Status();
            if(ShellProtocol.TestMode && (action=="test-dump-timeout" || action=="test-dump-failure"))
            {
                testCaptureMode=action; return new { ok=true };
            }
            if (action == "coreexiting") { intentional=true; return new { ok=true }; }
            if (action == "start" || action == "stop" || action == "restart" || action == "exit")
            {
                lock(sync)
                {
                    if(capturing && action != "exit") return new { error="Dump capture is in progress" };
                    commands.Enqueue(action);
                }
                return new { ok=true };
            }
            if(action=="openrelease") { commands.Enqueue(action); return new { ok=true }; }
            if (action == "options" || action == "about" || action == "startup" || action == "showcore")
            {
                commands.Enqueue(action);
                return new { ok=true };
            }
            throw new ArgumentException("Unknown watchdog action: " + action);
        }
        public object Status()
        {
            lock(sync) return new { ok=true, state=state, pid=core==null ? 0 : core.Id, incident=lastIncident, error=lastError, logs=logs.ToArray() };
        }
        public string State { get { lock(sync) return state; } }
        void SetState(string value)
        {
            lock(sync) state=value;
            Log(value);
        }
        public void Log(string text)
        {
            lock(sync)
            {
                string line=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")+"  "+text;
                logs.Enqueue(line);
                while(logs.Count>2000) logs.Dequeue();
                try
                {
                    Directory.CreateDirectory(root);
                    string path=Path.Combine(root,"watchdog.log");
                    if(File.Exists(path) && new FileInfo(path).Length >= 5*1024*1024)
                    {
                        if(File.Exists(path+".3")) File.Delete(path+".3");
                        for(int i=2;i>=1;i--) if(File.Exists(path+"."+i)) File.Move(path+"."+i,path+"."+(i+1));
                        File.Move(path,path+".1");
                    }
                    File.AppendAllText(path,line+Environment.NewLine);
                }
                catch(Exception e) { lastError="Log write failed: "+e.Message; }
            }
            var changed=Changed; if(changed!=null) changed();
        }
        void Loop()
        {
            while(!exiting)
            {
                string command;
                if(commands.TryDequeue(out command))
                {
                    try
                    {
                        if(command=="stop" || command=="restart" || command=="exit") Stop();
                        if(command=="start" || command=="restart") Start();
                        if(command=="exit") { exiting=true; var done=ExitReady; if(done!=null) done(); }
                        if(command=="openrelease") Process.Start(new ProcessStartInfo("https://github.com/kvakulo/Switcheroo/releases/latest") { UseShellExecute=true });
                        if(command=="options" || command=="about" || command=="startup" || command=="showcore")
                            ShellProtocol.Request(ShellProtocol.CorePipe,command=="showcore" ? "show" : command);
                    }
                    catch(Exception e) { lock(sync) lastError=e.Message; Log("Command failed: "+e.Message); }
                }
                try { Probe(); }
                catch(Exception e) { Log("Health check failed: "+e.Message); }
                Thread.Sleep(100);
            }
        }
        void Start()
        {
            if(debugThread!=null && debugThread.IsAlive) { Log("Start ignored: core already exists"); return; }
            if(!File.Exists(corePath)) { SetState("Stopped"); throw new FileNotFoundException("Core executable not found",corePath); }
            intentional=false; crashPending=false; hangCaptured=false; lastHealthy=DateTime.UtcNow; started=lastHealthy; nextProbe=started;
            lock(sync) lastError="";
            SetState("Starting");
            debugThread=new Thread(DebugLoop) { IsBackground=true, Name="CoreDebugger" };
            debugThread.Start();
        }
        void Stop()
        {
            if(debugThread==null || !debugThread.IsAlive) { SetState("Stopped"); return; }
            intentional=true;
            SetState("Stopping");
            try { ShellProtocol.Request(ShellProtocol.CorePipe,"exit",1000); }
            catch(Exception e) { Log("Graceful shutdown unavailable: "+e.Message); }
            if(!debugThread.Join(5000))
            {
                Log("Graceful stop timed out; terminating owned process job");
                var ownedJob=job;
                if(ownedJob!=IntPtr.Zero) Native.TerminateJobObject(ownedJob,1);
                if(!debugThread.Join(35000)) throw new TimeoutException("Core did not exit after forced stop");
            }
            SetState("Stopped");
        }
        void Probe()
        {
            if(core==null || capturing || crashPending || State=="Stopping" || State=="Stopped" || State=="Crashed" || DateTime.UtcNow<nextProbe) return;
            nextProbe=DateTime.UtcNow.AddSeconds(2);
            bool healthy=false;
            try
            {
                var response=ShellProtocol.Decode(ShellProtocol.Request(ShellProtocol.CorePipe,"ping",1000));
                healthy=response.ContainsKey("pid") && Convert.ToInt32(response["pid"])==core.Id;
            }
            catch(IOException) { }
            catch(TimeoutException) { }
            if(crashPending) return;
            if(healthy)
            {
                lastHealthy=DateTime.UtcNow;
                if(State!="Running") { hangCaptured=false; SetState("Running"); }
            }
            else if(State=="Starting" && DateTime.UtcNow-started>TimeSpan.FromSeconds(15))
            {
                Log("Startup readiness timed out");
                Stop(); lock(sync) lastError="Core startup timed out";
            }
            else if(State!="Starting" && DateTime.UtcNow-lastHealthy>TimeSpan.FromSeconds(10) && !hangCaptured)
            {
                hangCaptured=true; SetState("Unresponsive");
                Capture("hang",0,0,null,null);
                SetState("Unresponsive");
            }
        }
        void DebugLoop()
        {
            IntPtr ev=Marshal.AllocHGlobal(192);
            bool crashed=false;
            uint exitCode=0;
            try
            {
                job=Native.NewJob();
                var si=new Native.StartupInfo { cb=Marshal.SizeOf(typeof(Native.StartupInfo)) };
                Native.ProcessInfo pi;
                string args=Native.Quote(corePath)+" --supervised"+(auto?" --auto":"");
                if(!Native.CreateProcess(corePath,new StringBuilder(args),IntPtr.Zero,IntPtr.Zero,false,2|4,IntPtr.Zero,Path.GetDirectoryName(corePath),ref si,out pi)) throw new Win32Exception();
                try
                {
                    core=Process.GetProcessById((int)pi.pid);
                    if(!Native.AssignProcessToJobObject(job,pi.process)) { core.Kill(); throw new Win32Exception(); }
                    Native.ResumeThread(pi.thread);
                }
                finally { Native.CloseHandle(pi.process); Native.CloseHandle(pi.thread); }
                Log("Started core PID "+pi.pid);
                bool initialBreakpoint=true;
                while(true)
                {
                    if(!Native.WaitForDebugEvent(ev,250))
                    {
                        int error=Marshal.GetLastWin32Error();
                        if(error==121) continue;
                        throw new Win32Exception(error);
                    }
                    uint code=(uint)Marshal.ReadInt32(ev), pid=(uint)Marshal.ReadInt32(ev,4), tid=(uint)Marshal.ReadInt32(ev,8);
                    int union=IntPtr.Size==8?16:12;
                    uint continuation=0x10002;
                    bool finished=false;
                    try
                    {
                        if(code==1)
                        {
                            uint exception=(uint)Marshal.ReadInt32(ev,union);
                            bool first=Marshal.ReadInt32(ev,union+(IntPtr.Size==8?152:80))!=0;
                            if(initialBreakpoint && first && exception==0x80000003) initialBreakpoint=false;
                            else
                            {
                                continuation=0x80010001; // Let the application/OS handle the exception.
                                if(!first && !crashed)
                                {
                                    crashed=true; crashPending=true;
                                    var record=new byte[IntPtr.Size==8?152:80];
                                    Marshal.Copy(IntPtr.Add(ev,union),record,0,record.Length);
                                    try { Capture("crash",tid,exception,record,Native.Context(tid)); }
                                    catch(Exception e) { Log("Crash capture failed: "+e.Message); }
                                }
                            }
                        }
                        if(code==3 || code==6)
                        {
                            var file=Marshal.ReadIntPtr(ev,union);
                            if(file!=IntPtr.Zero) Native.CloseHandle(file);
                        }
                        if(code==5)
                        {
                            exitCode=(uint)Marshal.ReadInt32(ev,union);
                            if(!intentional && !crashed)
                            {
                                // CLR FailFast can terminate without a second-chance exception.
                                // Capture remaining memory before acknowledging the exit event.
                                crashed=true; crashPending=true;
                                Capture("termination",0,exitCode,null,null);
                            }
                            finished=true;
                        }
                    }
                    finally { Native.ContinueDebugEvent(pid,tid,continuation); }
                    if(finished) break;
                }
                Log("Core exited: 0x"+exitCode.ToString("X8"));
                if(!intentional && !crashed)
                {
                    Log("Unexpected termination; no live exception context remains for a crash dump");
                    WriteTermination(exitCode);
                }
                SetState(intentional?"Stopped":"Crashed");
                if(!intentional) Notify("Core stopped unexpectedly. Open the control window to restart.");
            }
            catch(Exception e)
            {
                lock(sync) lastError=e.ToString();
                Log("Supervisor error: "+e.Message); SetState("Stopped");
            }
            finally
            {
                Marshal.FreeHGlobal(ev);
                var owned=job; job=IntPtr.Zero;
                if(owned!=IntPtr.Zero) Native.CloseHandle(owned);
                if(core!=null) { core.Dispose(); core=null; }
            }
        }
        void Notify(string text) { var handler=Incident; if(handler!=null) handler(text); }
        void WriteTermination(uint code)
        {
            try
            {
                var dir=NewIncident();
                File.WriteAllText(Path.Combine(dir,"report.json"),ShellProtocol.Encode(new { kind="termination",pid=core.Id,exitCode=code,dumpStatus="unavailable: process terminated without an exception event",utc=DateTime.UtcNow }));
            }
            catch(Exception e) { Log("Incident report failed: "+e.Message); }
        }
        string NewIncident()
        {
            string dir=Path.Combine(root,"crashes",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")+"-"+core.Id+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(dir);
            lock(sync) lastIncident=dir;
            return dir;
        }
        void Capture(string kind, uint thread, uint exception, byte[] record, byte[] context)
        {
            lock(captureLock)
            {
                if(kind=="hang" && crashPending) return;
                CaptureLocked(kind,thread,exception,record,context);
            }
        }
        void CaptureLocked(string kind, uint thread, uint exception, byte[] record, byte[] context)
        {
            lock(sync) { if(capturing) return; capturing=true; }
            string previous=State;
            string dir=null;
            try
            {
                SetState("Capturing dump");
                dir=NewIncident();
                string temp=Path.Combine(dir,"core.dmp.partial"), final=Path.Combine(dir,"core.dmp");
                var request=new Dictionary<string,object> { {"pid",core.Id},{"dump",temp},{"thread",thread} };
                if(record!=null) { request["record"]=Convert.ToBase64String(record); request["context"]=Convert.ToBase64String(context); }
                string mode=testCaptureMode; testCaptureMode=null;
                if(ShellProtocol.TestMode && mode=="test-dump-timeout") request["delayMs"]=3000;
                if(ShellProtocol.TestMode && mode=="test-dump-failure") Directory.CreateDirectory(temp);
                string requestPath=Path.Combine(dir,"capture.json");
                File.WriteAllText(requestPath,ShellProtocol.Encode(request));
                string result="failed";
                try
                {
                    var psi=new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,"--write-dump "+Native.Quote(requestPath)) { UseShellExecute=false,CreateNoWindow=true };
                    using(var worker=Process.Start(psi))
                    {
                        if(!worker.WaitForExit(ShellProtocol.TestMode && mode=="test-dump-timeout" ? 1000 : 30000)) { worker.Kill(); worker.WaitForExit(3000); result="timeout"; }
                        else if(worker.ExitCode!=0) result="Windows error 0x"+worker.ExitCode.ToString("X8");
                        else { File.Move(temp,final); result="complete"; }
                    }
                }
                catch(Exception e) { result=e.Message; }
                finally { if(File.Exists(requestPath)) File.Delete(requestPath); }
                File.WriteAllText(Path.Combine(dir,"report.json"),ShellProtocol.Encode(new { kind=kind,pid=core.Id,thread=thread,exceptionCode=exception,utc=DateTime.UtcNow,version=typeof(Supervisor).Assembly.GetName().Version.ToString(),architecture=IntPtr.Size*8,dumpStatus=result,dump=final }));
                lock(sync) File.WriteAllLines(Path.Combine(dir,"recent.log"),logs.ToArray());
                Log(kind+" dump: "+result+"; "+dir);
                if(result!="complete") lock(sync) lastError="Dump capture failed: "+result;
                foreach(var old in new DirectoryInfo(Path.Combine(root,"crashes")).GetDirectories().OrderByDescending(d=>d.Name).Skip(5)) old.Delete(true);
                if(kind=="hang") Notify("Core is unresponsive. A diagnostic capture was attempted; recovery is manual.");
            }
            catch(Exception e) { lock(sync) lastError=e.Message; Log("Dump/report write failed: "+e.Message); }
            finally { capturing=false; if(!crashPending) SetState(previous); }
        }
    }
}
