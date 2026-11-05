using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Switcheroo.Core;

namespace Switcheroo
{
    /// <summary>
    /// Draws a short accent border pulse around the window the user just
    /// switched to, so it is obvious where the switch landed.
    ///
    /// Four thin opaque, click-through, topmost windows form the frame. Opaque
    /// non-layered windows are the only overlay form verified to render reliably
    /// on this machine (see the taskbar badges). The windows are created once,
    /// parked off-screen, and re-positioned per flash - creating or re-showing a
    /// WPF window costs tens of milliseconds.
    ///
    /// This process is DPI-unaware, so SetWindowPos, WPF and GetWindowRect all
    /// speak the virtualized logical coordinate space, while DWM reports frame
    /// bounds in physical pixels. The DWM frame is converted into the process
    /// space before use (identity on same-scale setups); mixing the two spaces
    /// put the pulse ~1.5x off target on mixed-DPI monitors.
    ///
    /// The pulse is 3 px for 160 ms, 2 px for 80 ms, 1 px for 80 ms, then parked.
    /// A timer always parks the frame, so a target that disappears mid-pulse
    /// cannot leave a border behind. A soft light-blue dot fades out over 600 ms
    /// at the window's center.
    /// </summary>
    public class SwitchHighlight : IDisposable
    {
        private const int MaxThickness = 3;
        private const int OffScreen = -30000;
        private const int HoldFirstStepMs = 160;
        private const int HoldStepMs = 80;
        private const int DotDiameter = 48;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly Color Accent = Color.FromRgb(0x00, 0x78, 0xD4);

        private readonly Window[] _edges = new Window[4];
        private readonly IntPtr[] _handles = new IntPtr[4];
        private Window dot;
        private IntPtr dotHandle;
        private readonly System.Diagnostics.Stopwatch dotClock = new System.Diagnostics.Stopwatch();
        public bool DotVisible { get { return dotClock.IsRunning && dotClock.ElapsedMilliseconds < 600; } }
        public double DotOpacity { get { return DotVisible ? Math.Max(0, 1 - dotClock.Elapsed.TotalMilliseconds / 600) : 0; } }
        public int DotCenterX { get; private set; }
        public int DotCenterY { get; private set; }
        public int DotSize { get; private set; }
        private DispatcherTimer _pulse;
        private int _step;
        private bool _visible;

        /// <summary>True while the border is on screen.</summary>
        public bool IsVisible
        {
            get { return _visible; }
        }

        /// <summary>Window the current/last pulse was drawn around.</summary>
        public IntPtr Target { get; private set; }

        public int Left { get; private set; }
        public int Top { get; private set; }
        public int Right { get; private set; }
        public int Bottom { get; private set; }

        /// <summary>Thickness of the pulse right now (0 = parked).</summary>
        public int CurrentThickness { get; private set; }

        /// <summary>
        /// Creates the four edge windows early, at a moment the UI can afford it,
        /// so the first real switch does not pay window creation.
        /// </summary>
        public void PreWarm()
        {
            EnsureWindows();
        }

        public void Flash(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            // Minimized windows sit at the -32000 parking position; drawing there
            // is invisible, and flashing a window that never came up is noise.
            if (WinApi.IsIconic(hwnd) || !WinApi.IsWindowVisible(hwnd))
                return;

            EnsureWindows();
            WinApi.RECT frame;
            if (!TryGetFrame(hwnd, out frame))
                return;
            if (frame.Right <= frame.Left || frame.Bottom <= frame.Top)
                return;
            if (!IntersectsVirtualScreen(frame))
                return;

            Target = hwnd;
            Left = frame.Left;
            Top = frame.Top;
            Right = frame.Right;
            Bottom = frame.Bottom;

            _step = 0;
            ApplyThickness(MaxThickness);
            ShowDot();
            _visible = true;

            if (_pulse == null)
            {
                _pulse = new DispatcherTimer();
                _pulse.Tick += (sender, args) => Advance();
            }
            _pulse.Stop();
            _pulse.Interval = TimeSpan.FromMilliseconds(HoldFirstStepMs);
            _pulse.Start();
        }

        internal bool OwnsWindow(IntPtr handle) { return handle == dotHandle || Array.IndexOf(_handles, handle) >= 0; }
        internal string DotBounds()
        {
            WinApi.RECT r; WinApi.GetWindowRect(dotHandle, out r);
            return r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + " wpf=" + dot.Left + "," + dot.Top + "," + dot.Width + "," + dot.Height;
        }
        private void ShowDot()
        {
            DotCenterX = Left + (Right - Left) / 2;
            DotCenterY = Top + (Bottom - Top) / 2;
            // A constant logical size is right on every monitor: in this
            // DPI-unaware process 48 logical px are composed as 48 DIP worth of
            // physical pixels whatever the target display's scale is.
            DotSize = DotDiameter;
            // Reassert topmost after fullscreen activation without taking focus.
            WinApi.SetWindowPos(dotHandle, new IntPtr(-1), DotCenterX - DotSize / 2, DotCenterY - DotSize / 2,
                DotSize, DotSize, WinApi.SWP_NOACTIVATE);
            dotClock.Restart();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600));
            fade.Completed += (sender, args) =>
            {
                if (!DotVisible) WinApi.SetWindowPos(dotHandle, IntPtr.Zero, OffScreen, OffScreen, DotSize, DotSize,
                    WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);
            };
            dot.BeginAnimation(Window.OpacityProperty, fade);
        }

        private void Advance()
        {
            _step++;
            if (_step == 1)
            {
                ApplyThickness(MaxThickness - 1);
                _pulse.Interval = TimeSpan.FromMilliseconds(HoldStepMs);
                return;
            }
            if (_step == 2)
            {
                ApplyThickness(MaxThickness - 2);
                _pulse.Interval = TimeSpan.FromMilliseconds(HoldStepMs);
                return;
            }
            _pulse.Stop();
            Park();
            _visible = false;
        }

        private void ApplyThickness(int thickness)
        {
            CurrentThickness = thickness;
            var width = Right - Left;
            var height = Bottom - Top;

            ApplyEdge(0, Left, Top, width, thickness);
            ApplyEdge(1, Left, Bottom - thickness, width, thickness);
            ApplyEdge(2, Left, Top, thickness, height);
            ApplyEdge(3, Right - thickness, Top, thickness, height);
        }

        private void ApplyEdge(int index, int x, int y, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;
            WinApi.SetWindowPos(_handles[index], IntPtr.Zero, x, y, width, height,
                WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);
        }

        private void Park()
        {
            for (var i = 0; i < _handles.Length; i++)
            {
                WinApi.SetWindowPos(_handles[i], IntPtr.Zero, OffScreen, OffScreen, 1, 1,
                    WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);
            }
            CurrentThickness = 0;
        }

        private void EnsureWindows()
        {
            if (dot == null)
            {
                dot = new Window
                {
                    WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = true, Background = Brushes.Transparent,
                    Topmost = true, ShowInTaskbar = false, ShowActivated = false, IsHitTestVisible = false,
                    Left = OffScreen, Top = OffScreen, Width = DotDiameter, Height = DotDiameter,
                    Content = new Ellipse
                    {
                        Fill = new RadialGradientBrush(new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(230, 128, 216, 255), 0),
                            new GradientStop(Color.FromArgb(160, 128, 216, 255), 0.35),
                            new GradientStop(Color.FromArgb(0, 128, 216, 255), 1)
                        })
                    }
                };
                dotHandle = new WindowInteropHelper(dot).EnsureHandle();
                SetWindowLong(dotHandle, GWL_EXSTYLE, GetWindowLong(dotHandle, GWL_EXSTYLE) |
                    WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                dot.Show();
            }
            for (var i = 0; i < _edges.Length; i++)
            {
                if (_edges[i] != null)
                    continue;
                var window = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = false,
                    Background = new SolidColorBrush(Accent),
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    IsHitTestVisible = false,
                    Left = OffScreen,
                    Top = OffScreen,
                    Width = 1,
                    Height = 1
                };
                var handle = new WindowInteropHelper(window).EnsureHandle();
                var style = GetWindowLong(handle, GWL_EXSTYLE) |
                            WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                SetWindowLong(handle, GWL_EXSTYLE, style);
                // Show once and keep it shown: parked, then moved into place.
                window.Show();
                WinApi.SetWindowPos(handle, IntPtr.Zero, OffScreen, OffScreen, 1, 1,
                    WinApi.SWP_NOZORDER | WinApi.SWP_NOACTIVATE);
                _edges[i] = window;
                _handles[i] = handle;
            }
        }

        /// <summary>False when the whole frame sits outside every monitor (minimized parking, disconnected display).</summary>
        private static bool IntersectsVirtualScreen(WinApi.RECT frame)
        {
            var left = WinApi.GetSystemMetrics(WinApi.SM_XVIRTUALSCREEN);
            var top = WinApi.GetSystemMetrics(WinApi.SM_YVIRTUALSCREEN);
            var right = left + WinApi.GetSystemMetrics(WinApi.SM_CXVIRTUALSCREEN);
            var bottom = top + WinApi.GetSystemMetrics(WinApi.SM_CYVIRTUALSCREEN);
            return frame.Right > left && frame.Left < right && frame.Bottom > top && frame.Top < bottom;
        }

        private bool TryGetFrame(IntPtr hwnd, out WinApi.RECT frame)
        {
            // The visible frame, not the invisible resize border classic windows have.
            // DWM reports physical pixels; convert both corners into this process's
            // virtualized space before any SetWindowPos/WPF use. The anchor must be
            // one of our own windows - the target may live in a process with a
            // different DPI awareness, which would define a different logical space.
            if (WinApi.DwmGetWindowAttribute(hwnd, WinApi.DWMWA_EXTENDED_FRAME_BOUNDS,
                    out frame, Marshal.SizeOf(typeof(WinApi.RECT))) == 0 &&
                frame.Right > frame.Left && frame.Bottom > frame.Top)
            {
                var anchor = _handles[0] != IntPtr.Zero ? _handles[0] : dotHandle;
                var topLeft = new NativePoint { x = frame.Left, y = frame.Top };
                var bottomRight = new NativePoint { x = frame.Right, y = frame.Bottom };
                if (anchor != IntPtr.Zero &&
                    PhysicalToLogicalPointForPerMonitorDPI(anchor, ref topLeft) &&
                    PhysicalToLogicalPointForPerMonitorDPI(anchor, ref bottomRight))
                {
                    frame.Left = topLeft.x; frame.Top = topLeft.y;
                    frame.Right = bottomRight.x; frame.Bottom = bottomRight.y;
                    return true;
                }
                // Conversion unavailable: GetWindowRect already speaks our space.
            }
            return WinApi.GetWindowRect(hwnd, out frame);
        }

        public void Dispose()
        {
            dotClock.Stop();
            if (dot != null) { dot.Close(); dot = null; dotHandle = IntPtr.Zero; }
            if (_pulse != null)
            {
                _pulse.Stop();
                _pulse = null;
            }
            foreach (var window in _edges)
            {
                if (window == null)
                    continue;
                try { window.Close(); }
                catch { }
            }
            Array.Clear(_edges, 0, _edges.Length);
            Array.Clear(_handles, 0, _handles.Length);
            _visible = false;
            CurrentThickness = 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int x, y; }

        [DllImport("user32.dll")]
        private static extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr hwnd, ref NativePoint point);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    }
}
