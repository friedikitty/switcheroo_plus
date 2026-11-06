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
                options.SwitchHighlightCheckBox.IsChecked=false;
                Check(!options.DotFadeBox.IsEnabled && !options.DotOpacityBox.IsEnabled,"Dot controls follow toggle");
                options.SwitchHighlightCheckBox.IsChecked=true;
                Check(options.DotFadeBox.IsEnabled && options.DotOpacityBox.IsEnabled,"Dot controls re-enabled");
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
            // The dot is a transparent, per-pixel-alpha window containing a
            // radial-gradient ellipse. Its edge and its fade blend with whatever
            // is behind it, so the pixel proof looks for the blended blue center
            // rather than an exact opaque chip colour. The target window is our
            // own and is never activated: flashing a HWND does not need focus,
            // so these checks cannot move the user's working window.
            foreach(bool fullscreen in new[]{false,true})
            {
                var target=new Window { Title="Switcheroo automated feedback target",Width=440,Height=300,Left=bounds.Left+80,Top=bounds.Top+80,Background=new SolidColorBrush(Color.FromRgb(16,16,16)),WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowActivated=false,Topmost=true };
                if(fullscreen) { target.Left=bounds.Left; target.Top=bounds.Top; target.Width=bounds.Width; target.Height=bounds.Height; }
                using(var feedback=new SwitchHighlight())
                {
                    try
                    {
                        target.Show(); Pump(120);
                        var hwnd=new WindowInteropHelper(target).Handle;
                        WinApi.SetWindowPos(hwnd,new IntPtr(-1),bounds.Left+(fullscreen?0:80),bounds.Top+(fullscreen?0:80),fullscreen?bounds.Width:440,fullscreen?bounds.Height:300,WinApi.SWP_NOACTIVATE);
                        Pump(120);
                        feedback.PreWarm(); Pump(100);

                        feedback.Flash(hwnd);
                        Check(feedback.DotVisible,"Dot visible after switch");
                        Pump(60);
                        Check(Math.Abs(feedback.DotCenterX-(feedback.Left+feedback.Right)/2)<=1 && Math.Abs(feedback.DotCenterY-(feedback.Top+feedback.Bottom)/2)<=1,"Dot centered on target bounds");
                        // Geometry proof that the frame tracks the target in this process's coordinate
                        // space (this is what SetWindowPos/CopyFromScreen use). Works even when a
                        // display is not capturable; tolerance is far below any DPI-scale mismatch.
                        WinApi.RECT outer; WinApi.GetWindowRect(hwnd,out outer);
                        Check(feedback.Left>=outer.Left-2 && feedback.Top>=outer.Top-2 && feedback.Right<=outer.Right+2 && feedback.Bottom<=outer.Bottom+2,
                            "Feedback frame must stay within the target's outer rect: frame="+feedback.Left+","+feedback.Top+","+feedback.Right+","+feedback.Bottom+" outer="+outer.Left+","+outer.Top+","+outer.Right+","+outer.Bottom);
                        Check(feedback.Left-outer.Left<32 && feedback.Top-outer.Top<32 && outer.Right-feedback.Right<32 && outer.Bottom-feedback.Bottom<32,
                            "Feedback frame must hug the target's visible frame: frame="+feedback.Left+","+feedback.Top+","+feedback.Right+","+feedback.Bottom+" outer="+outer.Left+","+outer.Top+","+outer.Right+","+outer.Bottom);
                        // Capture only our own target's rect (plus the border band),
                        // never the whole desktop, so a reviewer cannot end up with the
                        // user's live windows in an artifact. Frames are counted by
                        // accent colour; the dot is found by its blue blend over the
                        // dark target background.
                        var cap=new System.Drawing.Rectangle(outer.Left-12,outer.Top-12,outer.Right-outer.Left+24,outer.Bottom-outer.Top+24);
                        // The dot is at the target centre in this process's own
                        // capture coordinates. Search a local box (not the whole
                        // desktop) so unrelated blue window content cannot win.
                        var ex=feedback.DotCenterX-cap.Left;
                        var ey=feedback.DotCenterY-cap.Top;
                        var radius=Math.Max(80,feedback.DotSize*3);
                        int accent=0; var bestBlue=0; var foundX=0; var foundY=0;
                        using(var bmp=CaptureRect(cap))
                        {
                            for(var y=0;y<bmp.Height;y++)
                                for(var x=0;x<bmp.Width;x++)
                                    if(IsAccent(bmp.GetPixel(x,y))) accent++;
                            for(var y=Math.Max(0,ey-radius);y<Math.Min(bmp.Height,ey+radius);y++)
                                for(var x=Math.Max(0,ex-radius);x<Math.Min(bmp.Width,ex+radius);x++)
                                {
                                    var p=bmp.GetPixel(x,y);
                                    if(!IsSoftDot(p)) continue;
                                    var blue=p.B-p.R;
                                    if(blue>bestBlue) { bestBlue=blue; foundX=x; foundY=y; }
                                }
                        }
                        Check(accent>50,"Captured frame has accent pixels: "+accent);
                        Check(bestBlue>55,"Captured soft dot is blue over its background: bestBlue="+bestBlue+" at "+foundX+","+foundY+" center="+feedback.DotCenterX+","+feedback.DotCenterY+" cap="+cap);
                        Check(Math.Abs(foundX-ex)<=16 && Math.Abs(foundY-ey)<=16,
                            "Soft dot sits at the target centre: found="+foundX+","+foundY+" expected="+ex+","+ey);
                        // The poll can outlive the 800 ms hold; re-arm so the hold
                        // assertion is measured from a known flash.
                        feedback.Flash(hwnd);
                        // WindowFromPoint cannot see through an opaque overlay even
                        // with WS_EX_TRANSPARENT - that flag's hit-test skip is honored by real
                        // mouse routing (the taskbar badges rely on it daily), not by
                        // WindowFromPoint. So assert no foreign window intercepts the dot point.
                        var hit=WindowFromPoint(new NativePoint { x=feedback.DotCenterX,y=feedback.DotCenterY });
                        Check(feedback.OwnsWindow(hit) || hit==hwnd,"Dot is click-through, hit=0x"+hit.ToString("X")+" owns="+feedback.OwnsWindow(hit)+" exstyle=0x"+GetWindowLong(hit,-20).ToString("X"));
                        Pump(160);
                        var holdOpacity=feedback.DotOpacity;
                        Check(feedback.DotVisible && holdOpacity>0.9,"Dot holds without fading");
                        Pump(700);
                        Check(feedback.DotVisible && feedback.DotOpacity<holdOpacity-0.05,"Dot fades out gradually: "+holdOpacity+" -> "+feedback.DotOpacity);
                        feedback.Flash(hwnd); Check(feedback.DotVisible,"Rapid switch re-arms the dot");
                        var expiry=DateTime.UtcNow.AddSeconds(4);
                        do { Pump(100); } while((feedback.DotVisible || feedback.IsVisible) && DateTime.UtcNow < expiry);
                        Check(!feedback.DotVisible && !feedback.IsVisible,"Both feedback effects expire");
                        var mode=fullscreen?"Fullscreen":"Windowed";
                        passed.Add(mode+" soft dot pixels, centre, fade, click-through, cleanup");
                    }
                    finally { target.Close(); }
                }
            }
            }
            return new Dictionary<string, object> { { "ok", true }, { "passed", passed } };
        }
        static System.Drawing.Bitmap CaptureRect(System.Drawing.Rectangle rect)
        {
            var bmp=new System.Drawing.Bitmap(rect.Width,rect.Height);
            using(var g=System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(rect.Left,rect.Top,0,0,new System.Drawing.Size(rect.Width,rect.Height));
            return bmp;
        }
        static bool IsAccent(System.Drawing.Color c)
        {
            // Solid frame colour #0078D4.
            return c.R<45 && Math.Abs(c.G-120)<35 && Math.Abs(c.B-212)<35;
        }
        static bool IsSoftDot(System.Drawing.Color c)
        {
            // Blended radial-gradient dot over the dark staged background:
            // centre ~ (117,196,231). The frame (#0078D4) has R=0 and is excluded.
            return c.R>50 && c.G>140 && c.B>170 && c.B-c.R>55 && c.G-c.R>30;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativePoint { public int x,y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(NativePoint point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd,int index);
    }
}
