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
            {
            var bounds=monitor.Bounds;
            // Capability probe: some display pipelines (this machine's GameViewer virtual
            // display) never composite UpdateLayeredWindow content, so the dot cannot appear
            // there no matter how correct the code is. Probe once per monitor; the pixel
            // assertion still runs at full strength wherever per-pixel alpha composites.
            var layeredPixels=SupportsLayeredPixels(bounds.Left+200,bounds.Top+200);
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
                        WinApi.SetWindowPos(hwnd,new IntPtr(-1),bounds.Left+(fullscreen?0:80),bounds.Top+(fullscreen?0:80),fullscreen?bounds.Width:440,fullscreen?bounds.Height:300,WinApi.SWP_NOACTIVATE);
                        WinApi.ForceSwitchToWindow(hwnd); Pump(80);
                        feedback.PreWarm(); Pump(100);

                        feedback.Flash(hwnd);
                        Check(feedback.DotVisible,"Dot visible after switch");
                        Pump(60);
                        Check(Math.Abs(feedback.DotCenterX-(feedback.Left+feedback.Right)/2)<=1 && Math.Abs(feedback.DotCenterY-(feedback.Top+feedback.Bottom)/2)<=1,"Dot centered on target bounds");
                        Check(!feedback.OwnsWindow(WinApi.GetForegroundWindow()),"Feedback must not steal focus");
                        // Geometry proof that the frame tracks the target in this process's coordinate
                        // space (this is what SetWindowPos/CopyFromScreen use). Works even when a
                        // display is not capturable; tolerance is far below any DPI-scale mismatch.
                        WinApi.RECT outer; WinApi.GetWindowRect(hwnd,out outer);
                        Check(feedback.Left>=outer.Left-2 && feedback.Top>=outer.Top-2 && feedback.Right<=outer.Right+2 && feedback.Bottom<=outer.Bottom+2,
                            "Feedback frame must stay within the target's outer rect: frame="+feedback.Left+","+feedback.Top+","+feedback.Right+","+feedback.Bottom+" outer="+outer.Left+","+outer.Top+","+outer.Right+","+outer.Bottom);
                        Check(feedback.Left-outer.Left<32 && feedback.Top-outer.Top<32 && outer.Right-feedback.Right<32 && outer.Bottom-feedback.Bottom<32,
                            "Feedback frame must hug the target's visible frame: frame="+feedback.Left+","+feedback.Top+","+feedback.Right+","+feedback.Bottom+" outer="+outer.Left+","+outer.Top+","+outer.Right+","+outer.Bottom);
                        if(layeredPixels)
                        {
                        // DotCenter is already in this process's coordinate space, which is what CopyFromScreen expects.
                        var sample=new NativePoint { x=feedback.DotCenterX,y=feedback.DotCenterY };
                        using(var bitmap=new System.Drawing.Bitmap(100,100))
                        using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                        {
                            graphics.CopyFromScreen(sample.x-50,sample.y-50,0,0,new System.Drawing.Size(100,100));
                            bitmap.Save(Path.Combine(ShellProtocol.ConfigRoot,(fullscreen?"dot-fullscreen-":"dot-window-")+monitorIndex.ToString()+".png"));
                            var center=bitmap.GetPixel(50,50); var outside=bitmap.GetPixel(4,4);
                            var opacityAtSample=feedback.DotOpacity;
                            if(!(center.B > outside.B+20 && center.B>center.R))
                            {
                                // Failure evidence: the whole monitor shows where the dot actually painted.
                                using(var full=new System.Drawing.Bitmap(bounds.Width,bounds.Height))
                                using(var fullGraphics=System.Drawing.Graphics.FromImage(full))
                                {
                                    fullGraphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,full.Size);
                                    full.Save(Path.Combine(ShellProtocol.ConfigRoot,(fullscreen?"dot-fullscreen-monitor-":"dot-window-monitor-")+monitorIndex.ToString()+".png"));
                                }
                            }
                            Check(center.B > outside.B+20 && center.B>center.R,"Screenshot contains a visible blue dot: "+monitorIndex+" fullscreen="+fullscreen+" center="+feedback.DotCenterX+","+feedback.DotCenterY+" size="+feedback.DotSize+" pixel="+center+" outside="+outside+" opacityAtSample="+opacityAtSample+" bounds="+feedback.DotBounds());
                        }
                        }
                        // WindowFromPoint must resolve through the click-through dot to its target.
                            Check(WindowFromPoint(new NativePoint { x=feedback.DotCenterX,y=feedback.DotCenterY })==hwnd,"Dot is click-through");
                        double first=feedback.DotOpacity; Pump(160);
                        Check(feedback.DotOpacity<first,"Dot fades with time");
                        feedback.Flash(hwnd); Check(feedback.DotOpacity>first,"Rapid switch restarts fade");
                        var expiry=DateTime.UtcNow.AddSeconds(3);
                        do { Pump(100); } while((feedback.DotVisible || feedback.IsVisible) && DateTime.UtcNow < expiry);
                        Check(!feedback.DotVisible && !feedback.IsVisible,"Both feedback effects expire");
                        var mode=fullscreen?"Fullscreen":"Windowed";
                        passed.Add(layeredPixels?mode+" dot pixels, center, fading, click-through, focus and cleanup":mode+" dot center, fading, click-through, focus and cleanup (pixel check skipped: display cannot composite per-pixel-alpha layered windows)");
                    }
                    finally { target.Close(); }
                }
            }
            }
            return new Dictionary<string, object> { { "ok", true }, { "passed", passed } };
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativePoint { public int x,y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(NativePoint point);
        // Capability probe: paints a small opaque-blue native layered window at (x,y) and
        // checks that a screenshot sees it. Some display pipelines (this machine's GameViewer
        // virtual display) never composite UpdateLayeredWindow content, so the dot cannot
        // appear there no matter how correct the code is; any failure means "unsupported".
        static bool SupportsLayeredPixels(int x,int y)
        {
            const int size=8;
            var hwnd=CreateWindowEx(0x80000|0x20|0x80|0x08000000|0x8,"Static","lp",0x80000000|0x10000000,0,0,10,10,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            if(hwnd==IntPtr.Zero) return false;
            try
            {
                var screenDc=GetDC(IntPtr.Zero);
                var memDc=CreateCompatibleDC(screenDc);
                var bmi=new int[10]; bmi[0]=40; bmi[1]=size; bmi[2]=-size; bmi[3]=1|(32<<16);
                IntPtr bits; var dib=CreateDIBSection(screenDc,bmi,0,out bits,IntPtr.Zero,0);
                var px=new byte[size*size*4];
                for(int i=0;i<size*size;i++) { px[i*4]=255; px[i*4+3]=255; } // opaque blue, BGRA
                System.Runtime.InteropServices.Marshal.Copy(px,0,bits,px.Length);
                var old=SelectObject(memDc,dib);
                var dst=new NativePoint { x=x,y=y };
                var sz=new NativeSize { cx=size,cy=size };
                var src=new NativePoint { x=0,y=0 };
                var blend=new NativeBlend { op=0,flags=0,alpha=255,format=1 };
                var ok=UpdateLayeredWindow(hwnd,screenDc,ref dst,ref sz,memDc,ref src,0,ref blend,2);
                SelectObject(memDc,old); DeleteObject(dib); DeleteDC(memDc); ReleaseDC(IntPtr.Zero,screenDc);
                if(!ok) return false;
                Pump(150);
                using(var bitmap=new System.Drawing.Bitmap(size,size))
                using(var graphics=System.Drawing.Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(x,y,0,0,new System.Drawing.Size(size,size));
                    var center=bitmap.GetPixel(size/2,size/2);
                    return center.B>200 && center.R<80;
                }
            }
            catch { return false; }
            finally { DestroyWindow(hwnd); }
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativeSize { public int cx,cy; }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativeBlend { public byte op,flags,alpha,format; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr CreateWindowEx(uint exStyle,string className,string windowName,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd,IntPtr screenDc,ref NativePoint dst,ref NativeSize size,IntPtr memDc,ref NativePoint src,uint colorKey,ref NativeBlend blend,uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd,IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc,int[] bmi,uint usage,out IntPtr bits,IntPtr section,uint offset);
    }
}
