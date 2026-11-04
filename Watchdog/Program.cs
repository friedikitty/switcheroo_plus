using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using Switcheroo.Shell;

namespace Switcheroo.Watchdog
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            try
            {
                if(args.Length==2 && args[0]=="--write-dump") return Native.WriteDump(args[1]);
                if(args.Contains("--test-mode"))
                {
                    string root=Value(args,"--test-root"), id=Value(args,"--test-id"); Guid guid;
                    if(string.IsNullOrWhiteSpace(root)||!Guid.TryParse(id,out guid)) throw new ArgumentException("Test mode requires --test-root and a GUID --test-id");
                    Environment.SetEnvironmentVariable("SWITCHEROO_TEST_MODE","1");
                    Environment.SetEnvironmentVariable("SWITCHEROO_TEST_ROOT",Path.GetFullPath(root));
                    Environment.SetEnvironmentVariable("SWITCHEROO_TEST_ID",guid.ToString("N"));
                }
                bool auto=args.Contains("--auto");
                if(!auto && !ShellProtocol.TestMode && ConfigManager.Load().RunAsAdmin && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath,string.Join(" ",args.Select(Native.Quote))) { UseShellExecute=true,Verb="runas" });
                    return 0;
                }
                bool created;
                using(var mutex=new Mutex(true,"switcheroo_plus_watchdog"+ShellProtocol.Suffix,out created))
                {
                    if(!created) return 0;
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using(var shell=new ShellContext(auto)) Application.Run(shell);
                }
                return 0;
            }
            catch(Exception e)
            {
                try { Directory.CreateDirectory(SwitcherooConfig.ConfigDirectory); File.AppendAllText(Path.Combine(SwitcherooConfig.ConfigDirectory,"watchdog-startup.log"),DateTime.UtcNow+" "+e+Environment.NewLine); } catch { }
                return 1;
            }
        }
        static string Value(string[] args,string key) { int i=Array.IndexOf(args,key); return i>=0 && i+1<args.Length?args[i+1]:null; }
    }
    internal sealed class ShellContext : ApplicationContext
    {
        readonly Form form;
        readonly Label status;
        readonly Button start,stop,restart;
        readonly RichTextBox logs;
        readonly NotifyIcon tray;
        readonly Supervisor supervisor;
        bool exit;
        public ShellContext(bool auto)
        {
            form=new Form { Text="Switcheroo control",Width=760,Height=500,MinimumSize=new Size(540,300),StartPosition=FormStartPosition.CenterScreen };
            // Create a message target without showing a control window.
            var handle=form.Handle;
            var top=new FlowLayoutPanel { Dock=DockStyle.Top,Height=76,Padding=new Padding(10),WrapContents=true };
            status=new Label { AutoSize=true,Width=650,Text="Stopped",Margin=new Padding(4,5,12,6) };
            top.Controls.Add(status); top.SetFlowBreak(status,true);
            start=new Button { Text="Start",Name="Start",AutoSize=true };
            stop=new Button { Text="Stop",Name="Stop",AutoSize=true };
            restart=new Button { Text="Restart",Name="Restart",AutoSize=true };
            var diagnostics=new Button { Text="Open diagnostics folder",AutoSize=true };
            top.Controls.AddRange(new Control[]{start,stop,restart,diagnostics});
            logs=new RichTextBox { ReadOnly=true,Dock=DockStyle.Fill,WordWrap=false,BackColor=SystemColors.Window,Font=new Font("Consolas",10),Name="Logs" };
            form.Controls.Add(logs); form.Controls.Add(top);
            supervisor=new Supervisor(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"switcheroo_plus.exe"),auto);
            start.Click+=(s,e)=>supervisor.Dispatch("start"); stop.Click+=(s,e)=>supervisor.Dispatch("stop"); restart.Click+=(s,e)=>supervisor.Dispatch("restart");
            diagnostics.Click+=(s,e)=> { try { string path=Path.Combine(SwitcherooConfig.ConfigDirectory,"crashes"); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe",Native.Quote(path)) { UseShellExecute=true }); } catch(Exception ex) { supervisor.Log(ex.Message); } };
            var menu=new ContextMenuStrip();
            menu.Items.Add("Show control window",null,(s,e)=>Show());
            var options=menu.Items.Add("Options",null,(s,e)=>supervisor.Dispatch("options"));
            var about=menu.Items.Add("About",null,(s,e)=>supervisor.Dispatch("about"));
            var startup=menu.Items.Add("Run on Startup",null,(s,e)=>supervisor.Dispatch("startup"));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit",null,(s,e)=>supervisor.Dispatch("exit"));
            menu.Opening+=(s,e)=> { bool running=supervisor.State=="Running"; options.Enabled=about.Enabled=startup.Enabled=running; };
            tray=new NotifyIcon { Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath),Text="Switcheroo",Visible=true,ContextMenuStrip=menu };
            tray.DoubleClick+=(s,e)=> { if(supervisor.State=="Running") supervisor.Dispatch("showcore"); else Show(); };
            tray.BalloonTipClicked+=(s,e)=>Show();
            supervisor.Changed+=()=>Post(Refresh);
            supervisor.Incident+=text=>Post(()=> { tray.BalloonTipTitle="Switcheroo needs attention"; tray.BalloonTipText=text; tray.ShowBalloonTip(5000); });
            supervisor.ExitReady+=()=>Post(()=> { exit=true; tray.Visible=false; tray.Dispose(); form.Close(); ExitThread(); });
            form.FormClosing+=(s,e)=> { if(!exit) { e.Cancel=true; form.Hide(); } };
            ShellProtocol.Serve(ShellProtocol.WatchdogPipe+"_ui",action=>
            {
                if(action=="show" || action=="hide") { Post(()=> { if(action=="show") Show(); else form.Hide(); }); return new { ok=true }; }
                if(action=="status") return new { visible=form.Visible,startEnabled=start.Enabled,stopEnabled=stop.Enabled,restartEnabled=restart.Enabled };
                throw new ArgumentException("Unknown UI action");
            });
            supervisor.Dispatch("start");
        }
        void Post(Action action) { if(!form.IsDisposed) try { form.BeginInvoke(action); } catch(InvalidOperationException) { } }
        void Show() { form.Show(); form.Activate(); Refresh(); }
        void Refresh()
        {
            var snapshot=ShellProtocol.Decode(ShellProtocol.Encode(supervisor.Status()));
            string state=(string)snapshot["state"];
            status.Text=state+"  |  PID "+snapshot["pid"];
            bool idle=state=="Stopped" || state=="Crashed";
            start.Enabled=idle; stop.Enabled=restart.Enabled=state=="Running" || state=="Unresponsive" || state=="Starting";
            int selected=logs.SelectionStart, length=logs.SelectionLength;
            bool atEnd=selected+length>=logs.TextLength;
            var lines=(System.Collections.ArrayList)snapshot["logs"];
            logs.Text=string.Join(Environment.NewLine,lines.Cast<string>());
            logs.Select(atEnd?logs.TextLength:Math.Min(selected,logs.TextLength),atEnd?0:Math.Min(length,Math.Max(0,logs.TextLength-selected)));
            if(atEnd) logs.ScrollToCaret();
        }
        protected override void Dispose(bool disposing) { if(disposing) { tray.Dispose(); form.Dispose(); } base.Dispose(disposing); }
    }
}
