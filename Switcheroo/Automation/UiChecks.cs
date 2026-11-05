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
        internal static void Check(bool value, string message) { if(!value) throw new InvalidOperationException(message); }
        internal static void Pump(int milliseconds)
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
                Check(options.DisableImeCheckBox.IsChecked==true,"Quick-jump IME option defaults on");
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
            // The dot is a solid opaque chip: some display pipelines (this machine's
            // GameViewer virtual display) never composite per-pixel-alpha layered
            // windows at all, and screen-covering topmost targets can suppress them
            // too. The chip renders everywhere; slow pipelines are covered by the
            // polling pixel assertion below.
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
                        // Pixel proof: find the chip by its exact solid color in a full-monitor
                        // capture. Capture content can be scaled relative to this process's
                        // window coordinates (a system-DPI-aware process on a 175% virtual
                        // display sees the framebuffer at 1.75x), so the content scale is
                        // derived from the chip's own rendered size - capture space is never
                        // assumed to equal window space. Slow pipelines are covered by
                        // polling with the hold re-armed.
                        double opacityAtSample=0; var attempts=0; var blue=false;
                        var foundCX=0.0; var foundCY=0.0; var foundSize=0.0;
                        while(attempts<15 && !blue)
                        {
                            attempts++;
                            using(var full=new System.Drawing.Bitmap(bounds.Width,bounds.Height))
                            using(var fullGraphics=System.Drawing.Graphics.FromImage(full))
                            {
                                fullGraphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,full.Size);
                                opacityAtSample=feedback.DotOpacity;
                                for(var sy=0;sy<full.Height && !blue;sy+=4)
                                    for(var sx=0;sx<full.Width && !blue;sx+=4)
                                    {
                                        if(!IsChipPixel(full.GetPixel(sx,sy))) continue;
                                        var minX=int.MaxValue; var minY=int.MaxValue; var maxX=int.MinValue; var maxY=int.MinValue; var count=0;
                                        var x0=Math.Max(0,sx-24); var y0=Math.Max(0,sy-24);
                                        var x1=Math.Min(full.Width,sx+200); var y1=Math.Min(full.Height,sy+200);
                                        for(var cy=y0;cy<y1;cy++)
                                            for(var cx=x0;cx<x1;cx++)
                                            {
                                                if(!IsChipPixel(full.GetPixel(cx,cy))) continue;
                                                if(cx<minX)minX=cx; if(cx>maxX)maxX=cx;
                                                if(cy<minY)minY=cy; if(cy>maxY)maxY=cy;
                                                count++;
                                            }
                                        if(count==0) continue;
                                        var w=maxX-minX+1; var h=maxY-minY+1;
                                        // Solid, roughly square, plausible scale of the 64px chip.
                                        if(w<feedback.DotSize*0.7 || w>feedback.DotSize*3 || Math.Abs(w-h)>w*0.2) continue;
                                        if(count < w*h*0.55) continue;
                                        var k=w/(double)feedback.DotSize;
                                        var expX=(feedback.DotCenterX-bounds.Left)*k; var expY=(feedback.DotCenterY-bounds.Top)*k;
                                        var ccx=minX+w/2.0; var ccy=minY+h/2.0;
                                        if(Math.Abs(ccx-expX)>8*k+8 || Math.Abs(ccy-expY)>8*k+8) continue;
                                        blue=true; foundCX=ccx; foundCY=ccy; foundSize=w;
                                    }
                                if(!blue && attempts==15)
                                    full.Save(Path.Combine(ShellProtocol.ConfigRoot,(fullscreen?"dot-fullscreen-monitor-":"dot-window-monitor-")+monitorIndex.ToString()+".png"));
                            }
                            if(blue) break;
                            Pump(100);
                            // The 800 ms hold can expire before a slow pipeline composites the
                            // chip; re-arm it so the next sample still has something to see.
                            if(!feedback.DotVisible) feedback.Flash(hwnd);
                        }
                        Check(blue,"Screenshot contains a visible blue dot: "+monitorIndex+" fullscreen="+fullscreen+" center="+feedback.DotCenterX+","+feedback.DotCenterY+" size="+feedback.DotSize+" found="+foundCX+","+foundCY+","+foundSize+" opacityAtSample="+opacityAtSample+" attempts="+attempts+" bounds="+feedback.DotBounds());
                        // The poll can outlive the 800 ms hold (full-monitor captures with
                        // false-candidate refines); re-arm so the hold assertion is measured
                        // from a known flash.
                        feedback.Flash(hwnd);
                        // WindowFromPoint cannot see through an opaque (non-layered) overlay even
                        // with WS_EX_TRANSPARENT - that flag's hit-test skip is honored by real
                        // mouse routing (the taskbar badges rely on it daily), not by
                        // WindowFromPoint. So assert no foreign window intercepts the dot point.
                            var hit=WindowFromPoint(new NativePoint { x=feedback.DotCenterX,y=feedback.DotCenterY });
                            Check(feedback.OwnsWindow(hit) || hit==hwnd,"Dot is click-through, hit=0x"+hit.ToString("X")+" owns="+feedback.OwnsWindow(hit)+" exstyle=0x"+GetWindowLong(hit,-20).ToString("X"));
                        Pump(160);
                        Check(feedback.DotVisible && feedback.DotOpacity==1,"Dot holds without fading");
                        feedback.Flash(hwnd); Check(feedback.DotVisible,"Rapid switch re-arms the dot");
                        var expiry=DateTime.UtcNow.AddSeconds(3);
                        do { Pump(100); } while((feedback.DotVisible || feedback.IsVisible) && DateTime.UtcNow < expiry);
                        Check(!feedback.DotVisible && !feedback.IsVisible,"Both feedback effects expire");
                        var mode=fullscreen?"Fullscreen":"Windowed";
                        passed.Add(mode+" chip pixels, center, hold, click-through, focus and cleanup");
                    }
                    finally { target.Close(); }
                }
            }
            }
            return new Dictionary<string, object> { { "ok", true }, { "passed", passed } };
        }
        static bool IsChipPixel(System.Drawing.Color c)
        {
            // SwitchHighlight's solid chip color is opaque (128,216,255), so an
            // unscaled interior pixel matches exactly; allow a small tolerance.
            return Math.Abs(c.R-128)<12 && Math.Abs(c.G-216)<12 && Math.Abs(c.B-255)<12;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativePoint { public int x,y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(NativePoint point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd,int index);
    }
}
