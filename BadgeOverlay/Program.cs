using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SwitcherooBadgeOverlay
{
    /// <summary>
    /// Badge helper process for switcheroo_plus. Owns all taskbar interaction:
    /// receives (key, title) pairs over the "switcheroo_plus_badges" named pipe,
    /// finds the matching taskbar buttons via UI Automation, and draws one small
    /// opaque chip per badge. Runs isolated so a badge failure can never crash
    /// the switcher; the main process respawns it on the next show if it dies.
    ///
    /// Protocol (one JSON object per line):
    ///   show {"action":"show","timeout":10,"windows":[{"key":"1","title":"..."}]}
    ///   hide {"action":"hide"}
    ///   getbadges {"action":"getbadges"} -> {"ok":true,"badges":"1@150,960;..."}
    ///   ping / exit
    /// </summary>
    internal static class Program
    {
        private const string PipeName = "switcheroo_plus_badges";

        private static readonly List<Window> Chips = new List<Window>();
        private static Window closeChip;
        private static DispatcherTimer timeoutTimer;
        private static Application app;

        [STAThread]
        private static int Main()
        {
            bool createdNew;
            var mutex = new Mutex(true, "switcheroo_plus_badges_single_instance", out createdNew);
            if (!createdNew)
                return 0;

            app = new Application();
            // No window is open until the first show - don't let Run() exit on its own
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "BadgePipe" };
            pipeThread.Start();
            app.Run();
            mutex.ReleaseMutex();
            return 0;
        }

        private static NamedPipeServerStream OpenPipe()
        {
            return new NamedPipeServerStream(
                PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.None);
        }

        private static void PipeLoop()
        {
            while (true)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = OpenPipe();
                    pipe.WaitForConnection();
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false)))
                    {
                        var line = reader.ReadLine();
                        var response = Handle(line);
                        var bytes = new UTF8Encoding(false).GetBytes(response + "\n");
                        pipe.Write(bytes, 0, bytes.Length);
                        pipe.Flush();
                        pipe.WaitForPipeDrain();
                    }
                }
                catch
                {
                    // Client died mid-request or pipe error; keep serving
                }
                finally
                {
                    try { if (pipe != null) pipe.Dispose(); }
                    catch { }
                }
            }
        }

        private static string Handle(string line)
        {
            Dictionary<string, object> request;
            try
            {
                request = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line);
            }
            catch
            {
                return "{\"error\":\"bad json\"}";
            }

            object actionObj;
            var action = request != null && request.TryGetValue("action", out actionObj)
                ? Convert.ToString(actionObj)
                : null;

            switch (action)
            {
                case "ping":
                    return "{\"ok\":true}";
                case "show":
                {
                    int timeout = 0;
                    object timeoutObj;
                    if (request.TryGetValue("timeout", out timeoutObj))
                        int.TryParse(Convert.ToString(timeoutObj), out timeout);
                    var windows = new List<KeyValuePair<string, string>>();
                    object winsObj;
                    if (request.TryGetValue("windows", out winsObj))
                    {
                        foreach (var item in (System.Collections.IEnumerable)winsObj)
                        {
                            var dict = (Dictionary<string, object>)item;
                            windows.Add(new KeyValuePair<string, string>(
                                Convert.ToString(dict["key"]), Convert.ToString(dict["title"])));
                        }
                    }
                    RunOnUi(() => ShowBadges(windows, timeout));
                    return "{\"ok\":true}";
                }
                case "hide":
                    RunInBackgroundOnUi(HideAll);
                    return "{\"ok\":true}";
                case "getbadges":
                {
                    var badges = RunOnUi(() =>
                    {
                        var sb = new StringBuilder();
                        foreach (var chip in Chips)
                        {
                            if (!chip.IsVisible) continue;
                            var text = ((Border)chip.Content).Child as TextBlock;
                            if (text == null) continue;
                            if (sb.Length > 0) sb.Append(';');
                            sb.Append(text.Text).Append('@')
                                .Append(((int)chip.Left)).Append(',')
                                .Append((int)chip.Top);
                        }
                        return sb.ToString();
                    });
                    return "{\"ok\":true,\"badges\":\"" + badges + "\"}";
                }
                case "exit":
                    RunInBackgroundOnUi(() => app.Shutdown());
                    return "{\"ok\":true}";
                default:
                    return "{\"error\":\"unknown action\"}";
            }
        }

        private static void RunOnUi(Action action)
        {
            app.Dispatcher.Invoke(action);
        }

        private static T RunOnUi<T>(Func<T> func)
        {
            return app.Dispatcher.Invoke(func);
        }

        private static void RunInBackgroundOnUi(Action action)
        {
            app.Dispatcher.BeginInvoke(action);
        }

        // ----- UI side -----

        private static void ShowBadges(List<KeyValuePair<string, string>> windows, int timeoutSeconds)
        {
            // Right after a cold start UIA returns no buttons for seconds;
            // retry a few times. The hide timeout starts only once badges show.
            if (TryPlace(windows, timeoutSeconds) || windows.Count == 0)
                return;
            var attempts = 0;
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            retry.Tick += (s, e) =>
            {
                if (TryPlace(windows, timeoutSeconds) || ++attempts >= 5)
                    retry.Stop();
            };
            retry.Start();
        }

        private static bool TryPlace(List<KeyValuePair<string, string>> windows, int timeoutSeconds)
        {
            bool placed;
            try
            {
                placed = PlaceBadges(windows, FindTaskbarButtons());
            }
            catch
            {
                // Taskbar lookup is best-effort; a failure just yields no badges
                placed = false;
            }
            if (placed && timeoutSeconds > 0)
            {
                if (timeoutTimer == null)
                {
                    timeoutTimer = new DispatcherTimer();
                    timeoutTimer.Tick += (s, e) => { timeoutTimer.Stop(); HideAll(); };
                }
                timeoutTimer.Stop();
                timeoutTimer.Interval = TimeSpan.FromSeconds(timeoutSeconds);
                timeoutTimer.Start();
            }
            return placed;
        }

        private static bool PlaceBadges(List<KeyValuePair<string, string>> windows, List<TaskbarButton> buttons)
        {
            foreach (var chip in Chips)
                chip.Hide();
            if (closeChip != null)
                closeChip.Hide();

            // Longest titles first so a title that prefixes another can't steal its button
            var keyed = new List<KeyValuePair<string, string>>(windows);
            keyed.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length));

            var used = new bool[buttons.Count];
            var chipIndex = 0;
            foreach (var window in keyed)
            {
                for (var i = 0; i < buttons.Count; i++)
                {
                    if (used[i] || !NameMatchesTitle(buttons[i].Name, window.Value))
                        continue;
                    used[i] = true;

                    var chip = chipIndex < Chips.Count ? Chips[chipIndex] : CreateChip();
                    if (chipIndex >= Chips.Count)
                        Chips.Add(chip);
                    chipIndex++;

                    ((Border)chip.Content).Child = new TextBlock
                    {
                        Text = window.Key,
                        Foreground = Brushes.White,
                        FontSize = 11,
                        FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    // ponytail: UIA rect is physical pixels, WPF Left/Top are DIPs -
                    // identical at 100% DPI; needs TransformFromDevice if DPI scaling matters
                    chip.Left = buttons[i].Rect.X + 2;
                    chip.Top = buttons[i].Rect.Y + 2;
                    chip.Show();
                    break;
                }
            }

            if (chipIndex > 0)
                ShowCloseChip();
            return chipIndex > 0;
        }

        private static void HideAll()
        {
            if (timeoutTimer != null)
                timeoutTimer.Stop();
            foreach (var chip in Chips)
                chip.Hide();
            if (closeChip != null)
                closeChip.Hide();
        }

        private static Window CreateChip()
        {
            var chip = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = false,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                // Fixed size - SizeToContent proved unreliable for these tiny windows
                Width = 18,
                Height = 19,
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                    BorderThickness = new Thickness(1)
                }
            };
            ApplyChipExStyle(chip, clickThrough: true);
            return chip;
        }

        // Small clickable X at the taskbar's top-right corner; clicking it stops
        // the helper (the switcher respawns it on the next show).
        private static void ShowCloseChip()
        {
            if (closeChip == null)
            {
                closeChip = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    AllowsTransparency = false,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Width = 18,
                    Height = 19,
                    Cursor = Cursors.Hand,
                    Content = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x50, 0x1E, 0x1E)),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock
                        {
                            Text = "X",
                            Foreground = Brushes.White,
                            FontSize = 11,
                            FontWeight = FontWeights.Bold,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                };
                closeChip.MouseLeftButtonUp += (s, e) => app.Shutdown();
                ApplyChipExStyle(closeChip, clickThrough: false);
            }

            var tray = FindWindow("Shell_TrayWnd", null);
            RECT rect;
            if (tray != IntPtr.Zero && GetWindowRect(tray, out rect))
            {
                closeChip.Left = rect.Right - 20;
                closeChip.Top = rect.Top + 2;
            }
            closeChip.Show();
        }

        private static void ApplyChipExStyle(Window window, bool clickThrough)
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            const int GWL_EXSTYLE = -20;
            const int WS_EX_TRANSPARENT = 0x20;
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            if (clickThrough)
                style |= WS_EX_TRANSPARENT;
            SetWindowLong(hwnd, GWL_EXSTYLE, style);
        }

        // ----- taskbar lookup -----

        private class TaskbarButton
        {
            public string Name;
            public Rect Rect;
        }

        private static bool NameMatchesTitle(string buttonName, string title)
        {
            if (buttonName == title)
                return true;
            // Grouped button: "<title> - N windows running" (localized suffix)
            if (title.Length > 0 && buttonName.StartsWith(title + " - ", StringComparison.Ordinal))
            {
                var rest = buttonName.Substring(title.Length + 3);
                return rest.Length > 0 && char.IsDigit(rest[0]);
            }
            return false;
        }

        private static List<TaskbarButton> FindTaskbarButtons()
        {
            var buttons = new List<TaskbarButton>();
            var tray = FindWindow("Shell_TrayWnd", null);
            for (var i = 0; i < 2; i++)
            {
                if (i == 1)
                {
                    tray = FindWindow("Shell_SecondaryTrayWnd", null);
                    if (tray == IntPtr.Zero)
                        break;
                }
                AutomationElementCollection elements;
                try
                {
                    var root = AutomationElement.FromHandle(tray);
                    elements = root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                }
                catch
                {
                    continue;
                }
                foreach (AutomationElement element in elements)
                {
                    try
                    {
                        var rect = element.Current.BoundingRectangle;
                        var name = element.Current.Name;
                        if (!string.IsNullOrEmpty(name) && rect.Width > 0 && rect.Height > 0)
                            buttons.Add(new TaskbarButton { Name = name, Rect = rect });
                    }
                    catch
                    {
                        // Element vanished mid-walk; skip it
                    }
                }
            }
            return buttons;
        }

        // ----- Win32 -----

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
    }
}
