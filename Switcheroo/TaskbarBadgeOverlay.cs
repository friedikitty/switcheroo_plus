using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ManagedWinapi.Windows;

namespace Switcheroo
{
    /// <summary>
    /// Draws each window's quick-switch key at the top-left corner of its
    /// taskbar button. One small opaque topmost window per badge: a single
    /// full-screen transparent (layered) overlay renders nowhere on
    /// multi-monitor mixed-DPI setups, and small layered windows are clipped
    /// by the taskbar on Windows 10 - opaque non-layered windows are the only
    /// form verified to show over the taskbar.
    /// </summary>
    public class TaskbarBadgeOverlay
    {
        private class TaskbarButton
        {
            public string Name;
            public Rect Rect;
        }

        private readonly List<Window> _chips = new List<Window>();

        public void UpdateBadges(IList<AppWindowViewModel> windows)
        {
            var used = new List<Tuple<string, Point>>();
            try
            {
                CollectBadgePositions(windows, FindTaskbarButtons(), used);
            }
            catch
            {
                // Taskbar lookup is best-effort; never break the switcher over badges
            }

            for (var i = 0; i < _chips.Count; i++)
                _chips[i].Hide();

            for (var i = 0; i < used.Count; i++)
            {
                var chip = i < _chips.Count ? _chips[i] : CreateChip();
                if (i >= _chips.Count)
                    _chips.Add(chip);
                ((Border)chip.Content).Child = new TextBlock
                {
                    Text = used[i].Item1,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                // ponytail: UIA rect is physical pixels, WPF Left/Top are DIPs -
                // identical at 100% DPI; needs TransformFromDevice if DPI scaling matters
                chip.Left = used[i].Item2.X + 2;
                chip.Top = used[i].Item2.Y + 2;
                chip.Show();
            }
        }

        public void Hide()
        {
            foreach (var chip in _chips)
                chip.Hide();
        }

        public void Close()
        {
            foreach (var chip in _chips)
                chip.Close();
            _chips.Clear();
        }

        public List<Dictionary<string, string>> GetBadgeList()
        {
            var result = new List<Dictionary<string, string>>();
            foreach (var chip in _chips)
            {
                if (!chip.IsVisible)
                    continue;
                var text = ((Border)chip.Content).Child as TextBlock;
                if (text == null)
                    continue;
                result.Add(new Dictionary<string, string>
                {
                    { "key", text.Text },
                    { "x", chip.Left.ToString("0") },
                    { "y", chip.Top.ToString("0") }
                });
            }
            return result;
        }

        private static void CollectBadgePositions(IList<AppWindowViewModel> windows,
            List<TaskbarButton> buttons, List<Tuple<string, Point>> output)
        {
            var keyed = new List<AppWindowViewModel>();
            foreach (var window in windows)
            {
                if (!string.IsNullOrEmpty(window.ShortcutKey))
                    keyed.Add(window);
            }
            // Longest titles first so a title that prefixes another can't steal its button
            keyed.Sort((a, b) => b.AppWindow.Title.Length.CompareTo(a.AppWindow.Title.Length));

            var used = new bool[buttons.Count];
            foreach (var window in keyed)
            {
                for (var i = 0; i < buttons.Count; i++)
                {
                    if (used[i] || !NameMatchesTitle(buttons[i].Name, window.AppWindow.Title))
                        continue;
                    used[i] = true;
                    output.Add(Tuple.Create(window.ShortcutKey,
                        new Point(buttons[i].Rect.X, buttons[i].Rect.Y)));
                    break;
                }
            }
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
            foreach (var tray in SystemWindow.AllToplevelWindows)
            {
                if (tray.ClassName != "Shell_TrayWnd" && tray.ClassName != "Shell_SecondaryTrayWnd")
                    continue;

                AutomationElementCollection elements;
                try
                {
                    var root = AutomationElement.FromHandle(tray.HWnd);
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
                // ponytail: fixed size - SizeToContent proved unreliable here
                // (some chips ended up 136x39 in-app though identical probe
                // windows measure 15x16); keys are single chars, so size once
                Width = 18,
                Height = 19,
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                    BorderThickness = new Thickness(1)
                }
            };
            // Click-through, never activated, keep out of alt-tab enumeration
            var hwnd = new System.Windows.Interop.WindowInteropHelper(chip).EnsureHandle();
            const int GWL_EXSTYLE = -20;
            const int WS_EX_TRANSPARENT = 0x20;
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            return chip;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowLong(System.IntPtr h, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SetWindowLong(System.IntPtr h, int index, int value);
    }
}
