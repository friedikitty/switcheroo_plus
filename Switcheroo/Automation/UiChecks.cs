using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Switcheroo.Core;
using Switcheroo.Shell;

namespace Switcheroo.Automation
{
    // Runs only inside an explicitly isolated automation core.
    internal static class UiChecks
    {
        static void Check(bool value, string message) { if(!value) throw new InvalidOperationException(message); }
        static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s,e) => { timer.Stop(); frame.Continue=false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        static IEnumerable<T> Children<T>(DependencyObject root) where T:DependencyObject
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            {
                var child=VisualTreeHelper.GetChild(root,i);
                var typed=child as T; if(typed!=null) yield return typed;
                foreach(var nested in Children<T>(child)) yield return nested;
            }
        }
        static void Snapshot(FrameworkElement element,string name)
        {
            var image=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
            image.Render(element);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using(var file=File.Create(Path.Combine(ShellProtocol.ConfigRoot,name+".png"))) encoder.Save(file);
        }
        public static object Run(int monitorIndex)
        {
            if(!ShellProtocol.TestMode) throw new InvalidOperationException("Test mode required");
            var passed=new List<string>();
            if(monitorIndex < 0)
            {
            var original=File.ReadAllText(SwitcherooConfig.ConfigPath);
            var config=ConfigManager.Load();
            OptionsWindow options=null;
            try
            {
                options=new OptionsWindow(); options.Show(); Pump(100);
                var groups=Children<GroupBox>(options).ToArray();
                Check(groups.Length==4,"Options must have four groups");
                var shortcuts=groups.Single(g=>Convert.ToString(g.Header)=="Shortcuts");
                var appearance=groups.Single(g=>Convert.ToString(g.Header)=="Appearance");
                Check(appearance.PointToScreen(new Point()).X > shortcuts.PointToScreen(new Point()).X+200,"Options must have two columns");
                Check(options.Ok.IsVisible && options.Cancel.IsVisible,"Footer buttons visible");
                options.TaskbarBadgesCheckBox.IsChecked=false;
                Check(!options.BadgeTimeoutBox.IsEnabled,"Badge timeout follows toggle");
                options.TaskbarBadgesCheckBox.IsChecked=true;
                Check(options.BadgeTimeoutBox.IsEnabled,"Badge timeout re-enabled");
                options.RecentOnTopCheckBox.IsChecked=false;
                Check(!options.RecentOnTopCountBox.IsEnabled,"Recent count follows toggle");
                options.PreviousAppCheckBox.IsChecked=false;
                Check(!options.PreviousAppPreview.IsEnabled,"Shortcut editor follows toggle");
                Snapshot(options,"options-normal");
                passed.Add("Options groups, columns, dependent controls and normal screenshot");
                ((FrameworkElement)options.Content).LayoutTransform=new ScaleTransform(1.5,1.5);
                options.UpdateLayout(); Pump(100);
                Check(options.Ok.PointToScreen(new Point()).Y+options.Ok.ActualHeight*1.5 < options.Top+options.ActualHeight+30,"Scaled footer stays inside dialog");
                Snapshot(options,"options-scaled");
                passed.Add("Options enlarged content remains scrollable with visible footer");
                options.MultiColumnCheckBox.IsChecked=!config.MultiColumnEnabled;
                options.Cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); options=null;
                Check(File.ReadAllText(SwitcherooConfig.ConfigPath)==original,"Cancel must preserve config");
                options=new OptionsWindow(); options.Show(); Pump(50);
                options.MultiColumnCheckBox.IsChecked=!config.MultiColumnEnabled;
                options.Ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); options=null;
                Check(ConfigManager.Load().MultiColumnEnabled!=config.MultiColumnEnabled,"OK persists edited setting");
                passed.Add("Options Cancel preserves configuration and OK saves changes");
            }
            finally { if(options!=null) options.Close(); File.WriteAllText(SwitcherooConfig.ConfigPath,original); }
            var startup = new AutoStart();
            startup.IsEnabled=true;
            Check(startup.IsEnabled,"Startup shortcut can be created in isolated profile");
            startup.IsEnabled=false;
            Check(!startup.IsEnabled,"Startup shortcut can be removed from isolated profile");
            passed.Add("Isolated startup shortcut create/remove");
            return new Dictionary<string, object> { { "ok", true }, { "passed", passed }, { "monitors", System.Windows.Forms.Screen.AllScreens.Length } };
            }
            foreach(var monitor in System.Windows.Forms.Screen.AllScreens.Skip(monitorIndex).Take(1))
            foreach(bool fullscreen in new[]{false,true})
            {
                var target=new Window { Title="Switcheroo automated feedback target",Width=440,Height=300,Left=120,Top=100,Background=new SolidColorBrush(Color.FromRgb(16,16,16)),WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize };
                if(fullscreen) { target.Left=SystemParameters.WorkArea.Left; target.Top=SystemParameters.WorkArea.Top; target.Width=SystemParameters.PrimaryScreenWidth; target.Height=SystemParameters.PrimaryScreenHeight; target.Topmost=true; }
                using(var feedback=new SwitchHighlight())
                {
                    try
                    {
                        target.Show(); target.Activate(); Pump(80);
                        var hwnd=new WindowInteropHelper(target).Handle;
                        var bounds=monitor.Bounds;
                        WinApi.SetWindowPos(hwnd,new IntPtr(-1),bounds.Left+(fullscreen?0:80),bounds.Top+(fullscreen?0:80),fullscreen?bounds.Width:440,fullscreen?bounds.Height:300,WinApi.SWP_NOACTIVATE);
                        WinApi.ForceSwitchToWindow(hwnd); Pump(80);
                        feedback.PreWarm(); Pump(100);
                        
                        feedback.Flash(hwnd);
                        Check(feedback.DotVisible,"Dot visible after switch");
                        Pump(60);
                        Check(Math.Abs(feedback.DotCenterX-(feedback.Left+feedback.Right)/2)<=1 && Math.Abs(feedback.DotCenterY-(feedback.Top+feedback.Bottom)/2)<=1,"Dot centered on target bounds");
                        Check(!feedback.OwnsWindow(WinApi.GetForegroundWindow()),"Feedback must not steal focus");
                        var sample=new NativePoint { x=feedback.DotCenterX,y=feedback.DotCenterY };
                        PhysicalToLogicalPointForPerMonitorDPI(hwnd,ref sample);
                        using(var bitmap=new System.Drawing.Bitmap(100,100))
                        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                        {
                            graphics.CopyFromScreen(sample.x-50,sample.y-50,0,0,new System.Drawing.Size(100,100));
                            bitmap.Save(Path.Combine(ShellProtocol.ConfigRoot,(fullscreen?"dot-fullscreen-":"dot-window-")+monitorIndex.ToString()+".png"));
                            var center=bitmap.GetPixel(50,50); var outside=bitmap.GetPixel(4,4);
                            Check(center.B > outside.B+20 && center.B>center.R,"Screenshot contains a visible blue dot: "+monitorIndex+" fullscreen="+fullscreen+" center="+feedback.DotCenterX+","+feedback.DotCenterY+" size="+feedback.DotSize+" pixel="+center+" outside="+outside+" elapsedOpacity="+feedback.DotOpacity+" bounds="+feedback.DotBounds());
                        }
                        // WindowFromPoint must resolve through the click-through dot to its target.
                            Check(WindowFromPoint(new NativePoint { x=feedback.DotCenterX,y=feedback.DotCenterY })==hwnd,"Dot is click-through");
                        double first=feedback.DotOpacity; Pump(160);
                        Check(feedback.DotOpacity<first,"Dot fades with time");
                        feedback.Flash(hwnd); Check(feedback.DotOpacity>first,"Rapid switch restarts fade");
                        var expiry=DateTime.UtcNow.AddSeconds(3);
                        do { Pump(100); } while((feedback.DotVisible || feedback.IsVisible) && DateTime.UtcNow < expiry);
                        Check(!feedback.DotVisible && !feedback.IsVisible,"Both feedback effects expire");
                        passed.Add(fullscreen?"Fullscreen dot pixels, center, fading, click-through, focus and cleanup":"Windowed dot pixels, center, fading, click-through, focus and cleanup");
                    }
                    finally { target.Close(); }
                }
            }
            return new Dictionary<string, object> { { "ok", true }, { "passed", passed } };
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativePoint { public int x,y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr window,ref NativePoint point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(NativePoint point);
    }
}
