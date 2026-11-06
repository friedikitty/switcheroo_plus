/**
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 * Copyright 2009, 2010 James Sulak
 * Copyright 2014 Regin Larsen
 *
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * Switcheroo is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Switcheroo.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ManagedWinapi;
using ManagedWinapi.Windows;
using Switcheroo.Automation;
using Switcheroo.Core;
using Switcheroo.Core.Matchers;
using Application = System.Windows.Application;
using MenuItem = System.Windows.Forms.MenuItem;
using MessageBox = System.Windows.MessageBox;

namespace Switcheroo
{
    public partial class MainWindow : Window
    {
        private WindowCloser _windowCloser;
        private List<AppWindowViewModel> _unfilteredWindowList;
        private ObservableCollection<AppWindowViewModel> _filteredWindowList;
        private NotifyIcon _notifyIcon;
        private HotKey _hotkey;
        private PreviousAppHook _previousAppHook;

        public static readonly RoutedUICommand CloseWindowCommand = new RoutedUICommand();
        public static readonly RoutedUICommand SwitchToWindowCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListDownCommand = new RoutedUICommand();
        public static readonly RoutedUICommand ScrollListUpCommand = new RoutedUICommand();
        private OptionsWindow _optionsWindow;
        private AboutWindow _aboutWindow;
        private AltTabHook _altTabHook;
        private SystemWindow _foregroundWindow;
        private bool _altTabAutoSwitch;
        private bool _isHelpPanelExpanded;

        // Config + previous-app tracking
        private SwitcherooConfig _config;
        private SystemWindow _previousWindow;
        private AppWindow _currentWindow;
        private AutomationServer _automationServer;
        private TaskbarBadgeOverlay _taskbarBadges;
        private DispatcherTimer _badgeWarmUpTimer;
        private SwitchHighlight _switchHighlight;
        private readonly bool _autoMode;

        // Most-recently-used window tracking (front = most recent). Used to keep
        // the overlay list in a stable, predictable order: recent windows first,
        // everything else alphabetically.
        private readonly List<IntPtr> _mruWindowHandles = new List<IntPtr>();
        private DispatcherTimer _foregroundTracker;
        private IntPtr _lastTrackedForegroundHwnd;

        public MainWindow()
            : this(false)
        {
        }

        public MainWindow(bool autoMode)
        {
            _autoMode = autoMode;
            InitializeComponent();

            _config = ConfigManager.Load();

            SizeChanged += MainWindow_SizeChanged;

            SetUpKeyBindings();
            // The watchdog owns the persistent tray icon.
            SetUpHotKey();
            SetUpPreviousAppHotKey();
            SetUpAltTabHook();

            SetUpForegroundTracking();
            ScheduleTaskbarBadgeWarmUp();
            PreWarmSwitchHighlight();

            if (!_autoMode)
                CheckForUpdates();

            Opacity = 0;
            SetHelpPanelExpanded(_config.OpenUsageHintsByDefault, false);

            _automationServer = new AutomationServer(this);
            _automationServer.Start();

            // Stop the badge helper on every exit path (Quit, automation exit, tray)
            Application.Current.Exit += (sender, args) =>
            {
                if (_taskbarBadges != null)
                    _taskbarBadges.Close();
            };

        }

        /// =================================

        #region Private Methods

        /// =================================

        private void SetUpKeyBindings()
        {
            // Intercept 0-9, A-Z, and / before child controls see them
            PreviewKeyDown += (sender, args) =>
            {
                if (tb.IsKeyboardFocused)
                    return;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                    return;

                // 0-9 direct switch
                int digitIndex = -1;
                if (args.Key >= Key.D0 && args.Key <= Key.D9)
                    digitIndex = args.Key - Key.D0;
                else if (args.Key >= Key.NumPad0 && args.Key <= Key.NumPad9)
                    digitIndex = args.Key - Key.NumPad0;

                if (digitIndex >= 0)
                {
                    if (_config.AzShortcutsEnabled)
                    {
                        SwitchToWindowByShortcutKey(digitIndex.ToString());
                        args.Handled = true;
                    }
                    else if (digitIndex < _filteredWindowList.Count)
                    {
                        SwitchToWindowByIndex(digitIndex);
                        args.Handled = true;
                    }
                    return;
                }

                // a-z / A-Z direct switch
                if (args.Key >= Key.A && args.Key <= Key.Z)
                {
                    bool isUpper = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                    if (_config.AzShortcutsEnabled)
                    {
                        SwitchToWindowByShortcutKey(((char)((isUpper ? 'A' : 'a') + (args.Key - Key.A))).ToString());
                        args.Handled = true;
                    }
                    else
                    {
                        int idx = isUpper ? (10 + 26 + (args.Key - Key.A)) : (10 + (args.Key - Key.A));
                        if (idx < _filteredWindowList.Count)
                        {
                            SwitchToWindowByIndex(idx);
                            args.Handled = true;
                        }
                    }
                    return;
                }

                // / focuses search box (vim-style)
                if (args.Key == Key.Oem2 || args.Key == Key.Divide || args.Key == Key.OemQuestion)
                {
                    tb.Focus();
                    tb.SelectAll();
                    args.Handled = true;
                    return;
                }
            };

            // Enter and Esc bindings are not executed before the keys have been released.
            // This is done to prevent that the window being focused after the key presses
            // to get 'KeyUp' messages.

            KeyDown += (sender, args) =>
            {
                // Opacity is set to 0 right away so it appears that action has been taken right away...
                if (args.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    Opacity = 0;
                }
                else if (args.Key == Key.Escape)
                {
                    Opacity = 0;
                }
                else if (args.SystemKey == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
                {
                    _altTabAutoSwitch = false;
                    tb.Text = "";
                    tb.IsEnabled = true;
                    tb.Focus();
                }
            };

            KeyUp += (sender, args) =>
            {
                // ... But only when the keys are release, the action is actually executed
                if (args.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    Switch();
                }
                else if (args.Key == Key.Escape)
                {
                    HideWindow();
                }
                else if (args.SystemKey == Key.LeftAlt && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    Switch();
                }
                else if (args.Key == Key.LeftAlt && _altTabAutoSwitch)
                {
                    Switch();
                }
            };
        }

        private void SwitchToWindowByIndex(int index)
        {
            if (index >= 0 && index < _filteredWindowList.Count)
            {
                var win = _filteredWindowList[index];
                ActivateAppWindow(_currentWindow, win.AppWindow, true);
            }
        }

        private void SwitchToWindowByShortcutKey(string shortcutKey)
        {
            foreach (var win in _filteredWindowList)
            {
                if (win.ShortcutKey == shortcutKey)
                {
                    ActivateAppWindow(_currentWindow, win.AppWindow, true);
                    return;
                }
            }
        }

        private void UpdateTaskbarBadges()
        {
            if (!_config.TaskbarBadgesEnabled || !_config.AzShortcutsEnabled)
            {
                if (_taskbarBadges != null)
                    _taskbarBadges.Hide();
                return;
            }
            if (_taskbarBadges == null)
                _taskbarBadges = new TaskbarBadgeOverlay();
            // The helper process owns the auto-hide timeout; 0 = stay until hidden
            _taskbarBadges.UpdateBadges(_unfilteredWindowList, _config.TaskbarBadgeTimeoutSeconds);
        }

        /// <summary>
        /// Starts the helper shortly after launch (when badges are enabled) and
        /// has it cache the taskbar button geometry, so the first hotkey draws
        /// badges immediately instead of waiting ~1 s for UI Automation.
        /// Deferred: startup must never block on the helper's cold WPF start.
        /// </summary>
        private void ScheduleTaskbarBadgeWarmUp()
        {
            if (!_config.TaskbarBadgesEnabled || !_config.AzShortcutsEnabled)
                return;
            if (_badgeWarmUpTimer == null)
            {
                _badgeWarmUpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _badgeWarmUpTimer.Tick += (sender, args) =>
                {
                    _badgeWarmUpTimer.Stop();
                    WarmUpTaskbarBadges();
                };
            }
            _badgeWarmUpTimer.Stop();
            _badgeWarmUpTimer.Start();
        }

        private void WarmUpTaskbarBadges()
        {
            if (!_config.TaskbarBadgesEnabled || !_config.AzShortcutsEnabled)
                return;
            if (_taskbarBadges == null)
                _taskbarBadges = new TaskbarBadgeOverlay();
            _taskbarBadges.WarmUp();
        }

        /// <summary>
        /// Builds the switch-highlight edge windows at idle priority: creating a
        /// handful of WPF windows costs tens of ms each, and paying that on the
        /// first switch would delay the very animation it draws.
        /// </summary>
        private void PreWarmSwitchHighlight()
        {
            if (!_config.SwitchHighlightEnabled)
                return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (!_config.SwitchHighlightEnabled)
                    return;
                if (_switchHighlight == null)
                    _switchHighlight = new SwitchHighlight();
                _switchHighlight.PreWarm();
            }));
        }

        private void ActivateAppWindow(AppWindow currentWindow, AppWindow targetWindow, bool hideWindowAfterSwitch)
        {
            if (targetWindow == null)
                return;

            if (currentWindow != null)
                _previousWindow = currentWindow;

            _currentWindow = targetWindow;
            RecordWindowUsed(targetWindow.HWnd);
            // ForceSwitch: SwitchToThisWindow is denied by the foreground lock when
            // another app had the most recent input (previous-app hotkey path).
            targetWindow.ForceSwitchToLastVisibleActivePopup();

            FlashSwitchTarget(targetWindow);

            if (hideWindowAfterSwitch)
                HideWindow();
        }

        /// <summary>
        /// Briefly frames the window that just became active so the user sees
        /// where the switch landed (Alt+Tab included - every switch path ends up
        /// in ActivateAppWindow).
        /// </summary>
        private void FlashSwitchTarget(AppWindow targetWindow)
        {
            if (!_config.SwitchHighlightEnabled)
                return;

            // Prefer where the switch actually landed (UWP lands on an inner popup),
            // but never trust a foreground window owned by another process - that
            // only happens when the activation itself was denied.
            var hwnd = WinApi.GetForegroundWindow();
            if (hwnd == IntPtr.Zero ||
                hwnd == new WindowInteropHelper(this).Handle ||
                WinApi.GetWindowProcessId(hwnd) != WinApi.GetWindowProcessId(targetWindow.HWnd))
                hwnd = targetWindow.HWnd;

            if (_switchHighlight == null)
                _switchHighlight = new SwitchHighlight();
            _switchHighlight.DotFadeMs = _config.SwitchHighlightDotFadeMs;
            _switchHighlight.DotOpacityPercent = _config.SwitchHighlightDotOpacityPercent;
            _switchHighlight.Flash(hwnd);
        }

        /// <summary>
        /// Polls the foreground window twice a second so the overlay list can be
        /// ordered by recency: most recently used windows first, the rest
        /// alphabetically. A WinEvent hook would also work, but a timer is simple
        /// and cheap enough at this interval.
        /// </summary>
        private void SetUpForegroundTracking()
        {
            _foregroundTracker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _foregroundTracker.Tick += (sender, args) => TrackForegroundWindow();
            _foregroundTracker.Start();
        }

        private void TrackForegroundWindow()
        {
            IntPtr hwnd;
            try
            {
                hwnd = SystemWindow.ForegroundWindow.HWnd;
            }
            catch (Exception)
            {
                return;
            }

            if (hwnd == IntPtr.Zero || hwnd == _lastTrackedForegroundHwnd)
                return;

            // Do not let our own overlay become the "most recent" window.
            if (hwnd == new WindowInteropHelper(this).Handle)
                return;

            _lastTrackedForegroundHwnd = hwnd;

            var window = new AppWindow(hwnd);
            if (!window.IsAltTabWindow())
                return;

            RecordWindowUsed(hwnd);
        }

        private void RecordWindowUsed(IntPtr hwnd)
        {
            _mruWindowHandles.Remove(hwnd);
            _mruWindowHandles.Insert(0, hwnd);
            if (_mruWindowHandles.Count > 50)
                _mruWindowHandles.RemoveAt(_mruWindowHandles.Count - 1);
        }

        private int MruRank(AppWindowViewModel window)
        {
            var index = _mruWindowHandles.IndexOf(window.AppWindow.HWnd);
            return index < 0 ? int.MaxValue : index;
        }

        /// <summary>
        /// Orders the window list. When RecentWindowsOnTopEnabled is on, the most
        /// recently used windows are pinned at the top in recency order: the
        /// previous app keeps slot 0 (so Enter / Alt+Tab-release still switches
        /// back to it), then the next most recent, and so on. Everything else is
        /// alphabetical by process name and title, as is the whole list when the
        /// option is off. Recency comes from TrackForegroundWindow().
        /// </summary>
        private List<AppWindowViewModel> OrderWindows(List<AppWindowViewModel> windows)
        {
            if (!_config.RecentWindowsOnTopEnabled)
            {
                return windows
                    .OrderBy(w => w.AppWindow.ProcessTitle ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(w => w.AppWindow.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }

            var count = _config.RecentWindowsOnTopCount < 1 ? 5 : _config.RecentWindowsOnTopCount;
            var inList = new HashSet<IntPtr>(windows.Select(w => w.AppWindow.HWnd));
            var recentHandles = _mruWindowHandles.Where(inList.Contains).Take(count).ToList();

            var foregroundHwnd = _foregroundWindow == null ? IntPtr.Zero : _foregroundWindow.HWnd;
            var prevAppHwnd = recentHandles.FirstOrDefault(hwnd => hwnd != foregroundHwnd);

            var topSet = new HashSet<IntPtr>(recentHandles.Where(hwnd => hwnd != foregroundHwnd));

            var ordered = new List<AppWindowViewModel>();
            var prevApp = prevAppHwnd == IntPtr.Zero ? null : windows.FirstOrDefault(w => w.AppWindow.HWnd == prevAppHwnd);
            if (prevApp != null)
                ordered.Add(prevApp);

            var recentRest = recentHandles
                .Where(hwnd => hwnd != prevAppHwnd && topSet.Contains(hwnd))
                .Join(windows, hwnd => hwnd, w => w.AppWindow.HWnd, (hwnd, w) => w);
            ordered.AddRange(recentRest);

            var rest = windows
                .Where(w => !topSet.Contains(w.AppWindow.HWnd))
                .OrderBy(w => w.AppWindow.ProcessTitle ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(w => w.AppWindow.Title ?? string.Empty, StringComparer.CurrentCultureIgnoreCase);
            ordered.AddRange(rest);

            return ordered;
        }

        private void SetUpHotKey()
        {
            _hotkey = new HotKey();
            _hotkey.LoadSettings();

            Application.Current.Properties["hotkey"] = _hotkey;

            _hotkey.HotkeyPressed += hotkey_HotkeyPressed;
            try
            {
                if (!_autoMode)
                    _hotkey.Enabled = _config.EnableHotKey;
            }
            catch (HotkeyAlreadyInUseException)
            {
                if (_autoMode) return;
                var boxText = "The current hotkey for activating Switcheroo is in use by another program." +
                              Environment.NewLine +
                              Environment.NewLine +
                              "You can change the hotkey by right-clicking the Switcheroo icon in the system tray and choosing 'Options'.";
                MessageBox.Show(boxText, "Hotkey already in use", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void SetUpPreviousAppHotKey()
        {
            // Previous-app is driven by a low-level keyboard hook (PreviousAppHook)
            // instead of RegisterHotKey so that combos Windows owns (Alt+Tab) and
            // combos RegisterHotKey cannot express (CapsLock as a modifier) all
            // work. Skip in --auto mode and when disabled: the automation API calls
            // TriggerPreviousApp() directly without needing a global hook.
            if (_autoMode || !_config.PreviousAppEnabled)
                return;

            SetUpPreviousAppLowLevelHook();
        }

        private void SetUpPreviousAppLowLevelHook()
        {
            DisposePreviousAppHook();
            _previousAppHook = new PreviousAppHook(
                _config.PreviousAppAlt,
                _config.PreviousAppCtrl,
                _config.PreviousAppShift,
                _config.PreviousAppWindowsKey,
                _config.PreviousAppCapsLock,
                _config.PreviousAppHotKey);
            _previousAppHook.Pressed += previousAppHotkey_Pressed;
            _previousAppHook.StartHook();
        }

        private void DisposePreviousAppHook()
        {
            if (_previousAppHook != null)
            {
                _previousAppHook.Pressed -= previousAppHotkey_Pressed;
                _previousAppHook.Dispose();
                _previousAppHook = null;
            }
        }

        private void previousAppHotkey_Pressed(object sender, EventArgs e)
        {
            // The hook event fires synchronously inside the WH_KEYBOARD_LL
            // callback. SetForegroundWindow is denied while the intercepted
            // keystroke is still being processed, so defer the actual switch
            // until after the hook callback has returned.
            Dispatcher.BeginInvoke(new Action(TogglePreviousApp));
        }

        private void TogglePreviousApp()
        {
            if (!_config.PreviousAppEnabled)
                return;

            var current = SystemWindow.ForegroundWindow;
            var currentAppWindow = new AppWindow(current.HWnd);
            var self = new WindowInteropHelper(this).Handle;

            // If we already have a tracked previous window and it is still alive, toggle.
            if (_previousWindow != null && !_previousWindow.IsClosed && _previousWindow.HWnd != current.HWnd)
            {
                ActivateAppWindow(currentAppWindow, new AppWindow(_previousWindow.HWnd), false);
                return;
            }

            // Otherwise walk the Z-order to find the previous real window. The
            // foreground window is at the TOP of the Z order, so the previously-used
            // window is the next one DOWN — that is GW_HWNDNEXT (GW_HWNDPREV walks
            // upward and finds nothing below the topmost window).
            var prevHwnd = WinApi.GetWindow(current.HWnd, WinApi.GetWindowCmd.GW_HWNDNEXT);
            while (prevHwnd != IntPtr.Zero)
            {
                var candidate = new AppWindow(prevHwnd);
                if (prevHwnd != self && candidate.IsAltTabWindow())
                {
                    ActivateAppWindow(currentAppWindow, candidate, false);
                    return;
                }
                prevHwnd = WinApi.GetWindow(prevHwnd, WinApi.GetWindowCmd.GW_HWNDNEXT);
            }
        }

        private void SetUpAltTabHook()
        {
            _altTabHook = new AltTabHook();
            _altTabHook.Pressed += AltTabPressed;
        }

        private void SetUpNotifyIcon()
        {
            var icon = Properties.Resources.icon;

            var runOnStartupMenuItem = new MenuItem("Run on Startup", (s, e) => RunOnStartup(s as MenuItem))
            {
                Checked = new AutoStart().IsEnabled
            };

            _notifyIcon = new NotifyIcon
            {
                Text = "Switcheroo",
                Icon = icon,
                Visible = true,
                ContextMenu = new System.Windows.Forms.ContextMenu(new[]
                {
                    new MenuItem("Options", (s, e) => Options()),
                    runOnStartupMenuItem,
                    new MenuItem("About", (s, e) => About()),
                    new MenuItem("Exit", (s, e) => Quit())
                })
            };
            _notifyIcon.DoubleClick += (s, e) => hotkey_HotkeyPressed(s, e);
        }
        private static void RunOnStartup(MenuItem menuItem)
        {
            try
            {
                var autoStart = new AutoStart
                {
                    IsEnabled = !menuItem.Checked
                };
                menuItem.Checked = autoStart.IsEnabled;
            }
            catch (AutoStartException e)
            {
                MessageBox.Show(e.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void CheckForUpdates()
        {
            var currentVersion = Assembly.GetEntryAssembly().GetName().Version;
            if (currentVersion == new Version(0, 0, 0, 0))
            {
                return;
            }

            var timer = new DispatcherTimer();

            timer.Tick += async (sender, args) =>
            {
                timer.Stop();
                var latestVersion = await GetLatestVersion();
                if (latestVersion != null && latestVersion > currentVersion)
                {
                    var result = MessageBox.Show(
                        string.Format(
                            "Switcheroo v{0} is available (you have v{1}).\r\n\r\nDo you want to download it?",
                            latestVersion, currentVersion),
                        "Update Available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (result == MessageBoxResult.Yes)
                    {
                        Switcheroo.Shell.ShellProtocol.Request(Switcheroo.Shell.ShellProtocol.WatchdogPipe, "openrelease");
                    }
                }
                else
                {
                    timer.Interval = new TimeSpan(24, 0, 0);
                    timer.Start();
                }
            };

            timer.Interval = new TimeSpan(0, 0, 0);
            timer.Start();
        }

        private static async Task<Version> GetLatestVersion()
        {
            try
            {
                var versionAsString =
                    await
                        new WebClient().DownloadStringTaskAsync(
                            "https://raw.github.com/kvakulo/Switcheroo/update/version.txt");
                Version newVersion;
                if (Version.TryParse(versionAsString, out newVersion))
                {
                    return newVersion;
                }
            }
            catch (WebException)
            {
            }
            return null;
        }

        /// <summary>
        /// Populates the window list with the current running windows.
        /// </summary>
        private void LoadData(InitialFocus focus)
        {
            _unfilteredWindowList = new WindowFinder().GetWindows().Select(window => new AppWindowViewModel(window)).ToList();

            // Order the list so it stays predictable between invocations.
            var foregroundWindowMovedToBottom = ApplyListOrdering();

            _filteredWindowList = new ObservableCollection<AppWindowViewModel>(_unfilteredWindowList);
            _windowCloser = new WindowCloser();

            // Toggle multi-column layout when option is enabled
            if (_config.MultiColumnEnabled)
            {
                lb.ItemsPanel = (System.Windows.Controls.ItemsPanelTemplate)FindResource("MultiColumnPanel");
                ScrollViewer.SetVerticalScrollBarVisibility(lb, System.Windows.Controls.ScrollBarVisibility.Disabled);
                ScrollViewer.SetHorizontalScrollBarVisibility(lb, System.Windows.Controls.ScrollBarVisibility.Auto);
            }
            else
            {
                lb.ItemsPanel = (System.Windows.Controls.ItemsPanelTemplate)FindResource("SingleColumnPanel");
                ScrollViewer.SetVerticalScrollBarVisibility(lb, System.Windows.Controls.ScrollBarVisibility.Auto);
                ScrollViewer.SetHorizontalScrollBarVisibility(lb, System.Windows.Controls.ScrollBarVisibility.Disabled);
            }

            foreach (var window in _unfilteredWindowList)
            {
                window.FormattedTitle = new XamlHighlighter().Highlight(new[] {new StringPart(window.AppWindow.Title)});
                window.FormattedProcessTitle =
                    new XamlHighlighter().Highlight(new[] {new StringPart(window.AppWindow.ProcessTitle)});
            }

            lb.DataContext = null;
            lb.DataContext = _filteredWindowList;

            FocusItemInList(focus, foregroundWindowMovedToBottom);

            SetHelpPanelExpanded(_config.OpenUsageHintsByDefault, false);
            UpdateSortToggleText();
            // Quick-jump keys must stay English even when an IME (e.g. pinyin) is
            // active; the search box keeps the system's normal IME behavior.
            InputMethod.SetIsInputMethodEnabled(lb, !_config.DisableImeOnQuickJumpEnabled);
            tb.Clear();
            CenterWindow();
            ScrollSelectedItemIntoView();

            // Focus the list so 0-9/A-Z work immediately; / will focus the text box
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (lb.Items.Count > 0)
                    Keyboard.Focus(lb);
            }), DispatcherPriority.Render);

            // Remember the current foreground window before we switch
            UpdateTaskbarBadges();

            _currentWindow = new AppWindow(SystemWindow.ForegroundWindow.HWnd);
        }

        /// <summary>
        /// Applies the configured ordering to the unfiltered list, moves the
        /// foreground window to the bottom (so the first item is the previous app)
        /// and assigns the 0-9 / a-z / A-Z shortcut keys by position.
        /// Returns true when a foreground window was moved to the bottom.
        /// </summary>
        private bool ApplyListOrdering()
        {
            _unfilteredWindowList = OrderWindows(_unfilteredWindowList);

            var foregroundIndex = _foregroundWindow == null ? -1 : _unfilteredWindowList.FindIndex(w => AreWindowsRelated(w.AppWindow, _foregroundWindow));
            if (foregroundIndex >= 0)
            {
                _unfilteredWindowList.Add(_unfilteredWindowList[foregroundIndex]);
                _unfilteredWindowList.RemoveAt(foregroundIndex);
            }

            if (_config.AzShortcutsEnabled)
            {
                for (int i = 0; i < _unfilteredWindowList.Count && i < 62; i++)
                {
                    if (i < 10)
                        _unfilteredWindowList[i].ShortcutKey = i.ToString();
                    else if (i < 36)
                        _unfilteredWindowList[i].ShortcutKey = ((char)('a' + (i - 10))).ToString();
                    else
                        _unfilteredWindowList[i].ShortcutKey = ((char)('A' + (i - 36))).ToString();
                }
            }

            return foregroundIndex >= 0;
        }

        private void SortToggle_OnMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _config.RecentWindowsOnTopEnabled = !_config.RecentWindowsOnTopEnabled;
            ConfigManager.Save(_config);
            UpdateSortToggleText();
            ReorderCurrentList();
            if (e != null)
                e.Handled = true;
        }

        private void UpdateSortToggleText()
        {
            SortToggle.Text = _config.RecentWindowsOnTopEnabled ? "Sorted: Recent first" : "Sorted: All alphabetical";
        }

        /// <summary>
        /// Re-applies the current ordering to the visible list (sort toggle),
        /// re-runs the active filter and keeps the selection on the same window.
        /// </summary>
        private void ReorderCurrentList()
        {
            var selected = lb.SelectedItem as AppWindowViewModel;
            ApplyListOrdering();
            if (tb.IsEnabled && tb.Text.Length > 0)
            {
                TextChanged(tb, null);
            }
            else
            {
                _filteredWindowList = new ObservableCollection<AppWindowViewModel>(_unfilteredWindowList);
                lb.DataContext = null;
                lb.DataContext = _filteredWindowList;
            }
            if (selected != null)
            {
                var again = _filteredWindowList.FirstOrDefault(w => w.AppWindow.HWnd == selected.AppWindow.HWnd);
                if (again != null)
                    lb.SelectedItem = again;
            }
        }

        private static bool AreWindowsRelated(SystemWindow window1, SystemWindow window2)
        {
            // Compare pids, not Process objects: SystemWindow.Process is
            // Process.GetProcessById, which snapshots the whole process list
            // (~10-30ms per call) - it ran per list item on every popup.
            return window1.HWnd == window2.HWnd
                || WinApi.GetWindowProcessId(window1.HWnd) == WinApi.GetWindowProcessId(window2.HWnd);
        }

        private void FocusItemInList(InitialFocus focus, bool foregroundWindowMovedToBottom)
        {
            if (focus == InitialFocus.PreviousItem)
            {
                var previousItemIndex = lb.Items.Count - 1;
                if (foregroundWindowMovedToBottom)
                {
                    previousItemIndex--;
                }

                lb.SelectedIndex = previousItemIndex > 0 ? previousItemIndex : 0;
            }
            else
            {
                lb.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Place the Switcheroo window in the center of the screen
        /// </summary>
        private void CenterWindow()
        {
            // Reset height every time to ensure that resolution changes take effect
            Border.MaxHeight = SystemParameters.PrimaryScreenHeight;

            // Force a rendering before repositioning the window
            SizeToContent = SizeToContent.Manual;
            SizeToContent = SizeToContent.WidthAndHeight;

            // Position the window in the center of the screen
            Left = (SystemParameters.PrimaryScreenWidth/2) - (ActualWidth/2);
            Top = (SystemParameters.PrimaryScreenHeight/2) - (ActualHeight/2);
        }

        /// <summary>
        /// Switches the window associated with the selected item.
        /// </summary>
        private void Switch()
        {
            foreach (var item in lb.SelectedItems)
            {
                var win = (AppWindowViewModel)item;
                ActivateAppWindow(_currentWindow, win.AppWindow, false);
            }

            HideWindow();
        }

        public void HideWindow()
        {
            if (_windowCloser != null)
            {
                _windowCloser.Dispose();
                _windowCloser = null;
            }

            if (SizeToContent == SizeToContent.Manual)
            {
                _config.WindowWidth = (int)ActualWidth;
                _config.WindowHeight = (int)ActualHeight;
                ConfigManager.Save(_config);
            }

            _altTabAutoSwitch = false;
            if (_taskbarBadges != null)
                _taskbarBadges.Hide();
            Opacity = 0;
            Dispatcher.BeginInvoke(new Action(Hide), DispatcherPriority.Input);
        }
        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Visibility == Visibility.Visible && SizeToContent != SizeToContent.Manual)
            {
                SizeToContent = SizeToContent.Manual;
            }
        }
        private void RestoreWindowSize()
        {
            if (_config.WindowWidth > 0 && _config.WindowHeight > 0)
            {
                Width = _config.WindowWidth;
                Height = _config.WindowHeight;
                SizeToContent = SizeToContent.Manual;
            }
            else
            {
                SizeToContent = SizeToContent.WidthAndHeight;
            }
        }

        #endregion

        /// =================================

        #region Right-click menu functions

        /// =================================
        /// <summary>
        /// Show Options dialog.
        /// </summary>
        private void Options()
        {
            if (_optionsWindow == null)
            {
                _optionsWindow = new OptionsWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _optionsWindow.Closed += (sender, args) =>
                {
                    _optionsWindow = null;
                    ReloadConfig();
                };
                _optionsWindow.ShowDialog();
            }
            else
            {
                _optionsWindow.Activate();
            }
        }

        /// <summary>
        /// Show About dialog.
        /// </summary>
        private void About()
        {
            if (_aboutWindow == null)
            {
                _aboutWindow = new AboutWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                _aboutWindow.Closed += (sender, args) => _aboutWindow = null;
                _aboutWindow.ShowDialog();
            }
            else
            {
                _aboutWindow.Activate();
            }
        }

        /// <summary>
        /// Quit Switcheroo
        /// </summary>
        private void Quit()
        {
            if (_notifyIcon != null) _notifyIcon.Dispose();
            _notifyIcon = null;
            if (_automationServer != null)
            {
                _automationServer.Dispose();
                _automationServer = null;
            }
            _hotkey.Dispose();
            DisposePreviousAppHook();
            if (_badgeWarmUpTimer != null)
            {
                _badgeWarmUpTimer.Stop();
                _badgeWarmUpTimer = null;
            }
            if (_switchHighlight != null)
            {
                _switchHighlight.Dispose();
                _switchHighlight = null;
            }
            if (_taskbarBadges != null)
            {
                _taskbarBadges.Close();
                _taskbarBadges = null;
            }
            Application.Current.Shutdown();
        }

        private void ReloadConfig()
        {
            _config = ConfigManager.Load();

            // Refresh main activation hotkey from config.
            if (_hotkey != null)
            {
                try
                {
                    _hotkey.LoadSettings();
                    if (!_autoMode)
                        _hotkey.Enabled = _config.EnableHotKey;
                }
                catch (HotkeyAlreadyInUseException)
                {
                    if (_autoMode) return;
                    var boxText = "The current hotkey for activating Switcheroo is in use by another program." +
                                  Environment.NewLine +
                                  Environment.NewLine +
                                  "You can change the hotkey by right-clicking the Switcheroo icon in the system tray and choosing 'Options'.";
                    MessageBox.Show(boxText, "Hotkey already in use", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // Refresh previous-app low-level hook to match the (possibly changed) combo.
            DisposePreviousAppHook();
            if (!_autoMode && _config.PreviousAppEnabled)
                SetUpPreviousAppLowLevelHook();

            SetHelpPanelExpanded(_config.OpenUsageHintsByDefault, false);

            // Options may have just enabled badges/shortcuts: warm the helper again
            ScheduleTaskbarBadgeWarmUp();
        }

        public void OpenOptionsForShell() { Options(); }
        public void OpenAboutForShell() { About(); }
        public void ToggleStartupForShell() { new AutoStart().IsEnabled = !new AutoStart().IsEnabled; }

        #region Automation API

        public void ShowAndActivate()
        {
            if (Visibility != Visibility.Visible)
            {
                tb.IsEnabled = true;
                _foregroundWindow = SystemWindow.ForegroundWindow;
                RestoreWindowSize();
                Show();
                Activate();
                LoadData(InitialFocus.NextItem);
                Opacity = 1;
            }
        }

        public void SetQuery(string query)
        {
            if (Visibility != Visibility.Visible)
            {
                ShowAndActivate();
            }
            tb.Text = query ?? string.Empty;
            tb.Focus();
        }

        public List<Dictionary<string, string>> GetWindowList()
        {
            var result = new List<Dictionary<string, string>>();
            if (_filteredWindowList == null)
                return result;

            for (int i = 0; i < _filteredWindowList.Count; i++)
            {
                var vm = _filteredWindowList[i];
                result.Add(new Dictionary<string, string>
                {
                    { "index", i.ToString() },
                    { "hwnd", vm.AppWindow.HWnd.ToInt64().ToString("X") },
                    { "title", vm.AppWindow.Title },
                    { "process", vm.AppWindow.ProcessTitle },
                    { "shortcut", vm.ShortcutKey ?? string.Empty },
                    { "selected", (lb.SelectedIndex == i).ToString() }
                });
            }
            return result;
        }

        public bool PressOverlayKeyForTest(string keyName)
        {
            Key key;
            try { key = (Key)Enum.Parse(typeof(Key), keyName, true); }
            catch { return false; }
            if (PresentationSource.FromVisual(this) == null)
                return false;
            // Focus the list so the handler's search-box guard passes,
            // mirroring a user who clicked the list before pressing a shortcut
            Keyboard.Focus(lb);
            var args = new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, key);
            args.RoutedEvent = UIElement.PreviewKeyDownEvent;
            RaiseEvent(args);
            return args.Handled;
        }

        public List<Dictionary<string, string>> GetBadgeList()
        {
            if (_taskbarBadges == null)
                return new List<Dictionary<string, string>>();
            return _taskbarBadges.GetBadgeList();
        }

        /// <summary>Test hook: state of the switch-highlight border (used by the automation pipe).</summary>
        public Dictionary<string, string> GetSwitchHighlightState()
        {
            var state = new Dictionary<string, string>();
            if (_switchHighlight == null)
            {
                state["visible"] = "false";
                return state;
            }
            state["visible"] = _switchHighlight.IsVisible ? "true" : "false";
            state["target"] = _switchHighlight.Target.ToInt64().ToString();
            state["left"] = _switchHighlight.Left.ToString();
            state["top"] = _switchHighlight.Top.ToString();
            state["right"] = _switchHighlight.Right.ToString();
            state["bottom"] = _switchHighlight.Bottom.ToString();
            state["thickness"] = _switchHighlight.CurrentThickness.ToString();
            state["dotVisible"] = _switchHighlight.DotVisible ? "true" : "false";
            state["dotCenterX"] = _switchHighlight.DotCenterX.ToString();
            state["dotCenterY"] = _switchHighlight.DotCenterY.ToString();
            state["dotSize"] = _switchHighlight.DotSize.ToString();
            state["dotOpacity"] = _switchHighlight.DotOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return state;
        }

        /// <summary>
        /// Test hook (automation action "checkorder"): verifies list ordering with
        /// three staged windows. Recency and the foreground window are injected
        /// directly and the 500 ms foreground poll is paused, so the check does not
        /// depend on real activation (the live desktop can steal foreground mid-test):
        /// recent group in recency order, filter ties broken by recency, and the sort
        /// toggle flipping the whole list to alphabetical and back.
        /// </summary>
        public List<string> RunOrderingCheckForTest()
        {
            if (!Switcheroo.Shell.ShellProtocol.TestMode)
                throw new InvalidOperationException("Test mode required");

            var passed = new List<string>();
            var targets = new List<System.Diagnostics.Process>();
            var savedRecentEnabled = _config.RecentWindowsOnTopEnabled;
            var savedRecentCount = _config.RecentWindowsOnTopCount;
            var savedDisableIme = _config.DisableImeOnQuickJumpEnabled;
            _foregroundTracker.Stop();
            try
            {
                _config.RecentWindowsOnTopEnabled = true;
                _config.RecentWindowsOnTopCount = 5;
                _config.DisableImeOnQuickJumpEnabled = true;

                // Stage real windows in their own processes: AreWindowsRelated treats
                // same-PID windows as related, so WPF windows staged inside this test
                // core would make the foreground-to-bottom move ambiguous. notepad.exe
                // is a store-alias stub on Win11 (the stub exits at handoff), so each
                // window is a minimal WPF window in its own hidden powershell.exe.
                var stagedHwnd = new Dictionary<string, IntPtr>();
                foreach (var title in new[] { "OrderCheck Zulu", "OrderCheck Alpha", "OrderCheck Mike" })
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                        "-NoProfile -ExecutionPolicy Bypass -Command \"Add-Type -AssemblyName PresentationFramework; $w=New-Object System.Windows.Window; $w.Title='" + title + "'; $w.Width=320; $w.Height=200; $w.ShowActivated=$false; $w.Show(); [System.Windows.Threading.Dispatcher]::Run()\"")
                    { CreateNoWindow = true, UseShellExecute = false };
                    var p = System.Diagnostics.Process.Start(psi);
                    targets.Add(p);
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!p.HasExited && p.MainWindowHandle == IntPtr.Zero && DateTime.UtcNow < deadline)
                    {
                        UiChecks.Pump(50);
                        p.Refresh();
                    }
                    if (p.MainWindowHandle == IntPtr.Zero)
                        throw new InvalidOperationException("Staged window did not appear: " + title);
                    stagedHwnd[title] = p.MainWindowHandle;
                }
                UiChecks.Pump(150);

                Func<string, IntPtr> hwndFor = title => stagedHwnd[title];
                RecordWindowUsed(hwndFor("OrderCheck Zulu"));
                RecordWindowUsed(hwndFor("OrderCheck Mike"));
                RecordWindowUsed(hwndFor("OrderCheck Alpha"));
                _foregroundWindow = new AppWindow(hwndFor("OrderCheck Alpha"));

                tb.IsEnabled = true;
                LoadData(InitialFocus.NextItem);
                UiChecks.Pump(50);
                // Injected foreground Alpha -> bottom; previous app Mike -> slot 0.
                var orderState = "foreground=" + (_foregroundWindow == null ? "none" : _foregroundWindow.Title)
                    + " mru=" + string.Join(",", _mruWindowHandles.Take(6).Select(h =>
                    {
                        var vm = _unfilteredWindowList.FirstOrDefault(x => x.AppWindow.HWnd == h);
                        return vm == null ? h.ToInt64().ToString("X") : vm.AppWindow.Title;
                    }).ToArray())
                    + " list=" + string.Join(" | ", _filteredWindowList.Take(6).Select(x => x.AppWindow.Title).ToArray());
                UiChecks.Check(IndexOfTitle("OrderCheck Zulu") >= 0 && IndexOfTitle("OrderCheck Alpha") >= 0 && IndexOfTitle("OrderCheck Mike") >= 0,
                    "Staged windows must be enumerated. " + orderState);
                UiChecks.Check(TitleAt(0) == "OrderCheck Mike", "Previous app keeps slot 0. " + orderState);
                UiChecks.Check(TitleAt(1) == "OrderCheck Zulu", "Recent group must follow recency, got: " + TitleAt(1));
                UiChecks.Check(TitleAt(_filteredWindowList.Count - 1) == "OrderCheck Alpha", "Foreground window must sit at the bottom");
                UiChecks.Check(!InputMethod.GetIsInputMethodEnabled(lb), "Quick-jump list must have the IME closed while the option is on");
                passed.Add("Recent group in recency order, previous app first, foreground last, IME closed on the list");

                tb.Text = "ordercheck";
                UiChecks.Pump(50);
                // Equal scores -> recency wins: Alpha (most recent, even though foreground) first.
                UiChecks.Check(TitleAt(0) == "OrderCheck Alpha", "Filter tie-break by recency, got: " + TitleAt(0));
                UiChecks.Check(TitleAt(1) == "OrderCheck Mike" && TitleAt(2) == "OrderCheck Zulu", "Filter tie-break order wrong");
                passed.Add("Filter ties broken by recency");

                tb.Text = string.Empty;
                UiChecks.Pump(50);
                SortToggle_OnMouseLeftButtonUp(null, null);
                UiChecks.Check(!_config.RecentWindowsOnTopEnabled, "Toggle switched to alphabetical");
                UiChecks.Check(IndexOfTitle("OrderCheck Mike") >= 0 && IndexOfTitle("OrderCheck Mike") < IndexOfTitle("OrderCheck Zulu"), "Alphabetical order wrong");
                UiChecks.Check(TitleAt(_filteredWindowList.Count - 1) == "OrderCheck Alpha", "Foreground still at bottom in alphabetical mode");
                passed.Add("Sort toggle switches to all alphabetical");

                SortToggle_OnMouseLeftButtonUp(null, null);
                UiChecks.Check(_config.RecentWindowsOnTopEnabled && TitleAt(0) == "OrderCheck Mike", "Toggle back to recent first");
                passed.Add("Sort toggle switches back to recent first");
            }
            finally
            {
                if (Visibility == Visibility.Visible)
                    HideWindow();
                foreach (var p in targets)
                {
                    try
                    {
                        p.CloseMainWindow();
                        if (!p.WaitForExit(1500)) p.Kill();
                    }
                    catch { }
                    p.Dispose();
                }
                _config.RecentWindowsOnTopEnabled = savedRecentEnabled;
                _config.RecentWindowsOnTopCount = savedRecentCount;
                _config.DisableImeOnQuickJumpEnabled = savedDisableIme;
                ConfigManager.Save(_config);
                _foregroundTracker.Start();
            }
            return passed;
        }

        private string TitleAt(int index)
        {
            return _filteredWindowList[index].AppWindow.Title;
        }

        private int IndexOfTitle(string title)
        {
            for (var i = 0; i < _filteredWindowList.Count; i++)
                if (_filteredWindowList[i].AppWindow.Title == title)
                    return i;
            return -1;
        }

        public void SelectIndex(int index)
        {
            if (index >= 0 && index < lb.Items.Count)
            {
                lb.SelectedIndex = index;
                ScrollSelectedItemIntoView();
            }
        }

        public void ActivateSelectedWindow()
        {
            Switch();
        }

        public void TriggerPreviousApp()
        {
            TogglePreviousApp();
        }

        // ----- Test/diagnostic helpers (used by the automation API) -----

        /// <summary>
        /// Installs the previous-app low-level keyboard hook using the currently
        /// loaded config. Intended for automated tests that run under --auto
        /// (where the hook is otherwise not installed), so the real keystroke path
        /// can be exercised end-to-end.
        /// </summary>
        public bool InstallPreviousAppHookForTest()
        {
            SetUpPreviousAppLowLevelHook();
            return _previousAppHook != null && _previousAppHook.IsHookStarted;
        }

        public bool InstallActivationHotKeyForTest()
        {
            if (_hotkey == null)
                return false;

            _hotkey.LoadSettings();
            _hotkey.Enabled = false;
            _hotkey.Enabled = _config.EnableHotKey;
            return _hotkey.Enabled;
        }

        public bool IsPreviousAppHookActive
        {
            get { return _previousAppHook != null; }
        }

        public bool IsActivationHotKeyActive
        {
            get { return _hotkey != null && _hotkey.Enabled; }
        }

        public void ResetPreviousWindow()
        {
            _previousWindow = null;
        }

        public Dictionary<string, string> GetForegroundInfo()
        {
            var fg = SystemWindow.ForegroundWindow;
            var aw = new AppWindow(fg.HWnd);
            return new Dictionary<string, string>
            {
                { "hwnd", fg.HWnd.ToInt64().ToString("X") },
                { "title", aw.Title ?? "" },
                { "process", aw.ProcessTitle ?? "" }
            };
        }

        public void SetForegroundByHwnd(long hwnd)
        {
            var p = IntPtr.Size == 8 ? new IntPtr(hwnd) : new IntPtr((int)hwnd);
            WinApi.ForceSwitchToWindow(p);
        }

        public Dictionary<string, string> GetPreviousAppState()
        {
            return new Dictionary<string, string>
            {
                { "hookActive", (_previousAppHook != null).ToString().ToLowerInvariant() },
                { "hookStarted", (_previousAppHook != null && _previousAppHook.IsHookStarted).ToString().ToLowerInvariant() },
                { "prevHwnd", _previousWindow == null ? "" : _previousWindow.HWnd.ToInt64().ToString("X") },
                { "currentHwnd", _currentWindow == null ? "" : _currentWindow.HWnd.ToInt64().ToString("X") }
            };
        }

        public void ApplyConfig(string configJson)
        {
            try
            {
                var path = SwitcherooConfig.ConfigPath;
                var dir = SwitcherooConfig.ConfigDirectory;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, configJson, Encoding.UTF8);
                ReloadConfig();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("ApplyConfig failed: " + ex);
                throw;
            }
        }

        public string GetConfigJson()
        {
            try
            {
                return File.ReadAllText(SwitcherooConfig.ConfigPath);
            }
            catch
            {
                return "{}";
            }
        }

        #endregion

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new System.Windows.Controls.ContextMenu();

            var optionsItem = new System.Windows.Controls.MenuItem { Header = "Options" };
            optionsItem.Click += (s, ev) => Options();
            menu.Items.Add(optionsItem);

            var runOnStartupItem = new System.Windows.Controls.MenuItem { Header = "Run on Startup", IsCheckable = true };
            runOnStartupItem.IsChecked = new AutoStart().IsEnabled;
            runOnStartupItem.Click += (s, ev) =>
            {
                try
                {
                    var autoStart = new AutoStart { IsEnabled = runOnStartupItem.IsChecked };
                    runOnStartupItem.IsChecked = autoStart.IsEnabled;
                }
                catch (AutoStartException ex)
                {
                    MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            menu.Items.Add(runOnStartupItem);

            var aboutItem = new System.Windows.Controls.MenuItem { Header = "About" };
            aboutItem.Click += (s, ev) => About();
            menu.Items.Add(aboutItem);

            menu.Items.Add(new System.Windows.Controls.Separator());

            var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
            exitItem.Click += (s, ev) => System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            { try { Switcheroo.Shell.ShellProtocol.Request(Switcheroo.Shell.ShellProtocol.WatchdogPipe, "exit"); } catch { } });
            menu.Items.Add(exitItem);

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
        }

        /// =================================

        #endregion

        #region Event Handlers

        /// =================================
        private void OnClose(object sender, System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HideWindow();
        }

        private void hotkey_HotkeyPressed(object sender, EventArgs e)
        {
            var cfg = ConfigManager.Load();
            if (!cfg.EnableHotKey)
            {
                return;
            }

            if (Visibility != Visibility.Visible)
            {
                tb.IsEnabled = true;

                _foregroundWindow = SystemWindow.ForegroundWindow;
                RestoreWindowSize();
                Show();
                Activate();
                LoadData(InitialFocus.NextItem);
                Opacity = 1;
            }
            else
            {
                HideWindow();
            }
        }

        private void AltTabPressed(object sender, AltTabHookEventArgs e)
        {
            // Runs inside the WH_KEYBOARD_LL callback: must stay well under
            // LowLevelHooksTimeout (default 300ms) or Windows passes Alt+Tab
            // through to the OS switcher and may silently drop the hook.
            // So: decide and swallow here, do the heavy UI work deferred.
            if (!_config.AltTabHook)
            {
                // Ignore Alt+Tab presses if the hook is not activated by the user
                return;
            }

            try
            {
                _foregroundWindow = SystemWindow.ForegroundWindow;
                if (_foregroundWindow.ClassName == "MultitaskingViewFrame")
                {
                    // If Windows' task switcher is on the screen then don't do anything
                    return;
                }
            }
            catch
            {
                // Never throw inside the keyboard hook callback
                return;
            }

            e.Handled = true;

            var shiftDown = e.ShiftDown;
            var ctrlDown = e.CtrlDown;
            Dispatcher.BeginInvoke(new Action(() => AltTabShowOrCycle(shiftDown, ctrlDown)));
        }

        private void AltTabShowOrCycle(bool shiftDown, bool ctrlDown)
        {
            if (Visibility != Visibility.Visible)
            {
                tb.IsEnabled = true;

                ActivateAndFocusMainWindow();

                if (shiftDown)
                {
                    LoadData(InitialFocus.PreviousItem);
                }
                else
                {
                    LoadData(InitialFocus.NextItem);
                }

                if (_config.AutoSwitch && !ctrlDown)
                {
                    _altTabAutoSwitch = true;
                    tb.IsEnabled = false;
                    tb.Text = "Press Alt + S to search";
                }

                RestoreWindowSize();
                Opacity = 1;
            }
            else
            {
                if (shiftDown)
                {
                    PreviousItem();
                }
                else
                {
                    NextItem();
                }
            }
        }

        private void ActivateAndFocusMainWindow()
        {
            // What happens below looks a bit weird, but for Switcheroo to get focus when using the Alt+Tab hook,
            // it is needed to simulate an Alt keypress will bring Switcheroo to the foreground. Otherwise Switcheroo
            // will become the foreground window, but the previous window will retain focus, and receive keep getting
            // the keyboard input.
            // http://www.codeproject.com/Tips/76427/How-to-bring-window-to-top-with-SetForegroundWindo

            var thisWindowHandle = new WindowInteropHelper(this).Handle;
            var thisWindow = new AppWindow(thisWindowHandle);

            var altKey = new KeyboardKey(Keys.Alt);
            var altKeyPressed = false;

            // Press the Alt key if it is not already being pressed
            if ((altKey.AsyncState & 0x8000) == 0)
            {
                altKey.Press();
                altKeyPressed = true;
            }

            // Bring the Switcheroo window to the foreground
            Show();
            SystemWindow.ForegroundWindow = thisWindow;
            Activate();

            // Release the Alt key if it was pressed above
            if (altKeyPressed)
            {
                altKey.Release();
            }
        }

        private void TextChanged(object sender, TextChangedEventArgs args)
        {
            if (!tb.IsEnabled)
            {
                return;
            }

            var query = tb.Text;

            var context = new WindowFilterContext<AppWindowViewModel>
            {
                Windows = _unfilteredWindowList,
                ForegroundWindowProcessTitle = new AppWindow(_foregroundWindow.HWnd).ProcessTitle
            };

            var filterResults = new WindowFilterer().Filter(context, query).ToList();

            // Equal scores: most recently used wins, so a just-switched-to window
            // surfaces first instead of landing wherever the unfiltered list had it.
            // (Empty query = no re-sort: keep the freshly-opened order.)
            if (_config.RecentWindowsOnTopEnabled && query.Length > 0)
                filterResults = filterResults.OrderByDescending(r => r.Score).ThenBy(r => MruRank(r.AppWindow)).ToList();

            foreach (var filterResult in filterResults)
            {
                filterResult.AppWindow.FormattedTitle =
                    GetFormattedTitleFromBestResult(filterResult.WindowTitleMatchResults);
                filterResult.AppWindow.FormattedProcessTitle =
                    GetFormattedTitleFromBestResult(filterResult.ProcessTitleMatchResults);
            }

            _filteredWindowList = new ObservableCollection<AppWindowViewModel>(filterResults.Select(r => r.AppWindow));
            lb.DataContext = _filteredWindowList;
            if (lb.Items.Count > 0)
            {
                lb.SelectedItem = lb.Items[0];
            }
        }

        private static string GetFormattedTitleFromBestResult(IList<MatchResult> matchResults)
        {
            var bestResult = matchResults.FirstOrDefault(r => r.Matched) ?? matchResults.First();
            return new XamlHighlighter().Highlight(bestResult.StringParts);
        }

        private void OnEnterPressed(object sender, ExecutedRoutedEventArgs e)
        {
            Switch();
            e.Handled = true;
        }

        private void ListBoxItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Switch();
            e.Handled = true;
        }

        private async void CloseWindow(object sender, ExecutedRoutedEventArgs e)
        {
            var windows = lb.SelectedItems.Cast<AppWindowViewModel>().ToList();
            foreach (var win in windows)
            {
                bool isClosed = await _windowCloser.TryCloseAsync(win);
                if(isClosed)
                    RemoveWindow(win);
            }

            if (lb.Items.Count == 0)
                HideWindow();

            e.Handled = true;
        }

        private void RemoveWindow(AppWindowViewModel window)
        {
            int index = _filteredWindowList.IndexOf(window);
            if (index < 0)
                return;

            if (lb.SelectedIndex == index)
            {
                if (_filteredWindowList.Count > index + 1)
                    lb.SelectedIndex++;
                else
                {
                    if (index > 0)
                        lb.SelectedIndex--;
                }
            }

            _filteredWindowList.Remove(window);
            _unfilteredWindowList.Remove(window);
            UpdateTaskbarBadges();
        }

        private void ScrollListUp(object sender, ExecutedRoutedEventArgs e)
        {
            PreviousItem();
            e.Handled = true;
        }

        private void PreviousItem()
        {
            if (lb.Items.Count > 0)
            {
                if (lb.SelectedIndex != 0)
                {
                    lb.SelectedIndex--;
                }
                else
                {
                    lb.SelectedIndex = lb.Items.Count - 1;
                }

                ScrollSelectedItemIntoView();
            }
        }

        private void ScrollListDown(object sender, ExecutedRoutedEventArgs e)
        {
            NextItem();
            e.Handled = true;
        }

        private void NextItem()
        {
            if (lb.Items.Count > 0)
            {
                if (lb.SelectedIndex != lb.Items.Count - 1)
                {
                    lb.SelectedIndex++;
                }
                else
                {
                    lb.SelectedIndex = 0;
                }

                ScrollSelectedItemIntoView();
            }
        }

        private void ScrollSelectedItemIntoView()
        {
            var selectedItem = lb.SelectedItem;
            if (selectedItem != null)
            {
                lb.ScrollIntoView(selectedItem);
            }
        }

        private void MainWindow_OnLostFocus(object sender, EventArgs e)
        {
            HideWindow();
        }

        private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            DisableSystemMenu();
        }

        private void DisableSystemMenu()
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            var window = new SystemWindow(windowHandle);
            window.Style = window.Style & ~WindowStyleFlags.SYSMENU;
        }

        private void ShowHelpTextBlock_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            SetHelpPanelExpanded(!_isHelpPanelExpanded, true);
        }
        private void SetHelpPanelExpanded(bool expanded, bool animate)
        {
            _isHelpPanelExpanded = expanded;
            var newHeight = expanded ? 17 : 0;

            if (!animate)
            {
                HelpPanel.BeginAnimation(HeightProperty, null);
                HelpPanel.Height = newHeight;
                return;
            }

            var duration = new Duration(TimeSpan.FromSeconds(0.150));
            HelpPanel.BeginAnimation(HeightProperty, new DoubleAnimation(HelpPanel.ActualHeight, newHeight, duration));
        }

        #endregion

        private enum InitialFocus
        {
            NextItem,
            PreviousItem
        }
    }
}
