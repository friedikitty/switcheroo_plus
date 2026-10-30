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
    /// Transparent, click-through overlay that draws each window's quick-switch
    /// key at the top-left corner of its taskbar button.
    /// </summary>
    public class TaskbarBadgeOverlay : Window
    {
        private class TaskbarButton
        {
            public string Name;
            public Rect Rect;
        }

        private readonly Canvas _canvas;

        public TaskbarBadgeOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            IsHitTestVisible = false;
            Focusable = false;
            // Untitled on purpose: IsAltTabWindow skips windows without a title
            Title = "";
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            _canvas = new Canvas();
            Content = _canvas;
        }

        public void UpdateBadges(IList<AppWindowViewModel> windows)
        {
            _canvas.Children.Clear();
            try
            {
                PlaceBadges(windows, FindTaskbarButtons());
            }
            catch
            {
                // Taskbar lookup is best-effort; never break the switcher over badges
            }

            if (_canvas.Children.Count > 0)
                Show();
            else
                Hide();
        }

        public List<Dictionary<string, string>> GetBadgeList()
        {
            var result = new List<Dictionary<string, string>>();
            foreach (UIElement child in _canvas.Children)
            {
                var border = child as Border;
                if (border == null)
                    continue;
                var text = border.Child as TextBlock;
                if (text == null)
                    continue;
                result.Add(new Dictionary<string, string>
                {
                    { "key", text.Text },
                    { "x", (Canvas.GetLeft(border) + Left).ToString("0") },
                    { "y", (Canvas.GetTop(border) + Top).ToString("0") }
                });
            }
            return result;
        }

        private void PlaceBadges(IList<AppWindowViewModel> windows, List<TaskbarButton> buttons)
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

                    var badge = CreateBadge(window.ShortcutKey);
                    // ponytail: UIA rect is physical pixels, WPF Left/Top are DIPs -
                    // identical at 100% DPI; needs TransformFromDevice if DPI scaling matters
                    Canvas.SetLeft(badge, buttons[i].Rect.X - Left + 2);
                    Canvas.SetTop(badge, buttons[i].Rect.Y - Top + 2);
                    _canvas.Children.Add(badge);
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

        private static Border CreateBadge(string key)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1E, 0x1E, 0x1E)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(3, 0, 3, 0),
                Child = new TextBlock
                {
                    Text = key,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                }
            };
        }
    }
}
