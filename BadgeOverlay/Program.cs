using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Three things keep a show fast:
    ///   * taskbar button geometry is cached, so placement needs no UIA - the
    ///     scan runs on a background thread and only refreshes the cache;
    ///   * chips are pooled per shortcut key with their label already set, and
    ///     parked far off-screen: showing/hiding a WPF window or re-rendering a
    ///     label costs tens of ms per badge, moving a parked window does not;
    ///   * all moves of a show are committed with one DeferWindowPos batch.
    ///
    /// Protocol (one JSON object per line):
    ///   show {"action":"show","timeout":10,"windows":[{"key":"1","title":"..."}]}
    ///   hide {"action":"hide"}
    ///   warm {"action":"warm"}            - cache-only scan + chip pre-creation
    ///   stats {"action":"stats"}          - cache/placement diagnostics
    ///   getbadges {"action":"getbadges"}  - {"ok":true,"badges":"1@150,960;..."}
    ///   ping / exit
    /// </summary>
    internal static class Program
    {
        private static string PipeName { get { return "switcheroo_plus_badges" + Switcheroo.Shell.ShellProtocol.Suffix; } }

        // Cached taskbar geometry older than this is refreshed in the background.
        // Window open/close is caught by the title-set check, so this only needs
        // to be a safety net against a taskbar that changed underneath us.
        private const int CacheMaxAgeMs = 60000;
        private const int ScanRetries = 4;
        private const int ScanRetryDelayMs = 500;
        // Never start UIA scans closer together than this (they cost ~1s+)
        private const int MinScanIntervalMs = 3000;
        // Max badges: 0-9, a-z, A-Z (mirrors MainWindow's ShortcutKey assignment)
        private const string KeyOrder = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        // Parked chips sit here: inside no monitor, so they are never visible
        private const int OffScreen = -30000;

        private static readonly Dictionary<string, Chip> ChipsByKey = new Dictionary<string, Chip>();
        private static Chip closeChip;
        private static DispatcherTimer timeoutTimer;
        private static Application app;

        // ----- cached taskbar geometry (dispatcher thread only) -----
        private static List<TaskbarButton> cachedButtons = new List<TaskbarButton>();
        private static string cachedTraySignature = "";
        private static DateTime lastRefreshUtc = DateTime.MinValue;
        // Titles present when the cache was scanned; Known=false after a warm
        // scan, which does not know the switcher's window list yet.
        private static HashSet<string> scannedTitles = new HashSet<string>(StringComparer.Ordinal);
        private static bool scannedTitlesKnown;
        private static bool refreshRunning;
        private static DateTime lastScanStartUtc = DateTime.MinValue;

        // ----- current show state -----
        private static List<WindowRequest> currentWindows = new List<WindowRequest>();
        private static int currentTimeout;
        private static bool badgesSuppressed;
        private static bool timeoutStartedForShow;

        // ----- chip pre-creation (startup warm-up, background priority) -----
        private static int precreateTarget;
        private static int precreateIndex;
        private static bool precreateScheduled;

        // ----- diagnostics -----
        private static bool lastCacheHit;
        private static long lastPlaceMs;
        private static long lastRefreshMs;
        private static int skippedRefreshes;
        private static int lastMoves;
        private static long lastMoveMs;
        private static string lastStaleReason = "";

        [STAThread]
        private static int Main()
        {
            bool createdNew;
            var mutex = new Mutex(true, "switcheroo_plus_badges_single_instance" + Switcheroo.Shell.ShellProtocol.Suffix, out createdNew);
            if (!createdNew)
                return 0;

            app = new Application();
            // No window is open until the first show - don't let Run() exit on its own
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Warm right after start (including a respawn): scan the taskbar and
            // pre-create chips in the background so the first show is a fast path.
            var warmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            warmTimer.Tick += (s, e) =>
            {
                warmTimer.Stop();
                WarmUp();
            };
            warmTimer.Start();
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
                PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, Switcheroo.Shell.ShellProtocol.Security());
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
                    var windows = new List<WindowRequest>();
                    object winsObj;
                    if (request.TryGetValue("windows", out winsObj))
                    {
                        var items = winsObj as System.Collections.IEnumerable;
                        if (items != null)
                        {
                            foreach (var item in items)
                            {
                                var dict = item as Dictionary<string, object>;
                                if (dict == null)
                                    continue;
                                object keyObj, titleObj;
                                dict.TryGetValue("key", out keyObj);
                                dict.TryGetValue("title", out titleObj);
                                var title = Convert.ToString(titleObj) ?? "";
                                if (string.IsNullOrEmpty(title))
                                    continue;
                                windows.Add(new WindowRequest
                                {
                                    Key = Convert.ToString(keyObj) ?? "",
                                    Title = title
                                });
                            }
                        }
                    }
                    // Acknowledge first: placement and the UIA refresh happen on
                    // the dispatcher/background thread, never on the pipe thread.
                    RunInBackgroundOnUi(() => ShowBadges(windows, timeout));
                    return "{\"ok\":true}";
                }
                case "hide":
                    RunInBackgroundOnUi(HideFromClient);
                    return "{\"ok\":true}";
                case "warm":
                    RunInBackgroundOnUi(WarmUp);
                    return "{\"ok\":true}";
                case "stats":
                    return RunOnUi(BuildStats);
                case "getbadges":
                {
                    var badges = RunOnUi(() =>
                    {
                        var sb = new StringBuilder();
                        foreach (var key in KeyOrder)
                        {
                            Chip chip;
                            if (!ChipsByKey.TryGetValue(key.ToString(), out chip) || !chip.Visible)
                                continue;
                            if (sb.Length > 0) sb.Append(';');
                            sb.Append(key).Append('@').Append(chip.X).Append(',').Append(chip.Y);
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

        private static string BuildStats()
        {
            var age = lastRefreshUtc == DateTime.MinValue
                ? -1
                : (long)(DateTime.UtcNow - lastRefreshUtc).TotalMilliseconds;
            var visible = 0;
            foreach (var chip in ChipsByKey.Values)
                if (chip.Visible)
                    visible++;
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true");
            sb.Append(",\"cacheCount\":").Append(cachedButtons.Count);
            sb.Append(",\"chips\":").Append(ChipsByKey.Count);
            sb.Append(",\"visibleChips\":").Append(visible);
            sb.Append(",\"cacheHit\":").Append(lastCacheHit ? "true" : "false");
            sb.Append(",\"placeMs\":").Append(lastPlaceMs);
            sb.Append(",\"moves\":").Append(lastMoves);
            sb.Append(",\"moveMs\":").Append(lastMoveMs);
            sb.Append(",\"refreshMs\":").Append(lastRefreshMs);
            sb.Append(",\"skippedRefreshes\":").Append(skippedRefreshes);
            sb.Append(",\"refreshRunning\":").Append(refreshRunning ? "true" : "false");
            sb.Append(",\"cacheAgeMs\":").Append(age);
            sb.Append(",\"traySignature\":").Append(ComputeTraySignature().Length);
            sb.Append(",\"staleReason\":\"").Append(lastStaleReason).Append("\"");
            sb.Append(",\"cacheTitles\":").Append(scannedTitlesKnown ? scannedTitles.Count : -1);
            sb.Append(",\"showTitles\":").Append(currentWindows.Count);
            sb.Append("}");
            return sb.ToString();
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

        private static void ShowBadges(List<WindowRequest> windows, int timeoutSeconds)
        {
            badgesSuppressed = false;
            timeoutStartedForShow = false;
            currentWindows = windows;
            currentTimeout = timeoutSeconds;

            if (timeoutTimer != null)
                timeoutTimer.Stop();

            if (windows.Count == 0)
            {
                badgesSuppressed = true;
                HideAll();
                return;
            }

            var signature = ComputeTraySignature();
            if (signature.Length == 0)
            {
                // Taskbar hidden/gone (fullscreen app, auto-hide, explorer restarting)
                badgesSuppressed = true;
                HideAll();
                return;
            }

            // Fast path: draw from the cached geometry without touching UIA
            lastCacheHit = false;
            if (cachedButtons.Count > 0 && signature == cachedTraySignature)
            {
                var placed = PlaceBadges(windows, cachedButtons);
                lastCacheHit = placed;
                if (placed)
                    StartTimeoutIfNeeded();
            }

            var titles = Titles(windows);
            string staleReason;
            if (cachedButtons.Count == 0)
                staleReason = "no-cache";
            else if (signature != cachedTraySignature)
                staleReason = "signature";
            else if (!scannedTitlesKnown)
                staleReason = "unknown-titles";
            else if (!SameTitles(titles, scannedTitles))
                // A window opened, closed or was retitled: button rects may have
                // shifted, so the cached geometry needs one more look.
                staleReason = "titles";
            else if ((DateTime.UtcNow - lastRefreshUtc).TotalMilliseconds > CacheMaxAgeMs)
                staleReason = "age";
            else
                staleReason = "";
            lastStaleReason = staleReason;
            if (staleReason.Length > 0)
                QueueRefresh(titles);
        }

        private static bool SameTitles(HashSet<string> a, HashSet<string> b)
        {
            return a.SetEquals(b);
        }

        private static void HideFromClient()
        {
            // Suppress any in-flight scan result so it cannot re-show badges
            badgesSuppressed = true;
            HideAll();
        }

        private static void HideAll()
        {
            if (timeoutTimer != null)
                timeoutTimer.Stop();
            ApplyPositions(new Dictionary<string, int[]>(), false);
        }

        private static void StartTimeoutIfNeeded()
        {
            if (timeoutStartedForShow)
                return;
            timeoutStartedForShow = true;
            if (currentTimeout <= 0)
                return;
            if (timeoutTimer == null)
            {
                timeoutTimer = new DispatcherTimer();
                timeoutTimer.Tick += (s, e) =>
                {
                    timeoutTimer.Stop();
                    badgesSuppressed = true;
                    HideAll();
                };
            }
            timeoutTimer.Stop();
            timeoutTimer.Interval = TimeSpan.FromSeconds(currentTimeout);
            timeoutTimer.Start();
        }

        /// <summary>Cache-only scan at startup so the first hotkey draws instantly.</summary>
        private static void WarmUp()
        {
            // Cover a typical taskbar even before the first scan lands: a chip
            // created inside a show blocks that show, a background one does not.
            PrecreateChips(16);
            if (cachedButtons.Count > 0 &&
                (DateTime.UtcNow - lastRefreshUtc).TotalMilliseconds < CacheMaxAgeMs)
            {
                PrecreateChips(cachedButtons.Count);
                return;
            }
            if (ComputeTraySignature().Length == 0)
                return;
            QueueRefresh(null);
        }

        private static void QueueRefresh(HashSet<string> titles)
        {
            // A UIA scan costs a second or more; never run them back to back. A
            // later show re-checks the staleness condition on its own.
            if (refreshRunning ||
                (DateTime.UtcNow - lastScanStartUtc).TotalMilliseconds < MinScanIntervalMs)
            {
                skippedRefreshes++;
                return;
            }
            var signature = ComputeTraySignature();
            if (signature.Length == 0)
                return;

            refreshRunning = true;
            lastScanStartUtc = DateTime.UtcNow;
            var worker = new Thread(() => ScanTaskbar(titles, signature))
            {
                IsBackground = true,
                Name = "BadgeUiaScan"
            };
            worker.Start();
        }

        /// <summary>
        /// UI Automation on a background (MTA) thread - never on the dispatcher,
        /// which would block both the pipe server and the overlay.
        /// </summary>
        private static void ScanTaskbar(HashSet<string> titles, string signatureAtRequest)
        {
            var sw = Stopwatch.StartNew();
            var buttons = new List<TaskbarButton>();
            var signature = signatureAtRequest;
            for (var attempt = 0; attempt < ScanRetries; attempt++)
            {
                try
                {
                    buttons = FindTaskbarButtons(out signature);
                }
                catch
                {
                    // Taskbar lookup is best-effort; a failure just yields no badges
                    buttons = new List<TaskbarButton>();
                }
                if (buttons.Count > 0)
                    break;
                if (attempt + 1 < ScanRetries)
                    Thread.Sleep(ScanRetryDelayMs);
            }
            sw.Stop();

            app.Dispatcher.BeginInvoke(new Action(() =>
                OnScanComplete(buttons, signature, sw.ElapsedMilliseconds, titles)));
        }

        private static void OnScanComplete(List<TaskbarButton> buttons, string signature, long elapsedMs,
            HashSet<string> titles)
        {
            refreshRunning = false;
            lastRefreshMs = elapsedMs;

            if (buttons.Count > 0)
            {
                cachedButtons = buttons;
                cachedTraySignature = signature;
                lastRefreshUtc = DateTime.UtcNow;
                scannedTitles = titles ?? new HashSet<string>(StringComparer.Ordinal);
                scannedTitlesKnown = titles != null;
                PrecreateChips(buttons.Count);
            }
            // On an empty scan keep the previous cache: the next show can still
            // draw from it, and the stale timestamp makes it retry the scan.

            // A scan result is a geometry update, not a reply to one particular
            // show: place whatever is on screen right now (unless it was hidden
            // or the timeout already cleared it).
            if (!badgesSuppressed && currentWindows.Count > 0 &&
                cachedButtons.Count > 0 && ComputeTraySignature() == cachedTraySignature)
            {
                var placed = PlaceBadges(currentWindows, cachedButtons);
                if (placed)
                {
                    lastCacheHit = true;
                    StartTimeoutIfNeeded();
                }
            }
        }

        /// <summary>
        /// Builds the chip pool ahead of time at Background priority so a real
        /// show (Normal priority) preempts it. Creating a topmost WPF window is
        /// the expensive part; after this, drawing a badge is just a move.
        /// </summary>
        private static void PrecreateChips(int count)
        {
            var target = Math.Min(count, KeyOrder.Length);
            if (target <= ChipsByKey.Count)
                return;
            precreateTarget = target;
            if (precreateScheduled)
                return;
            precreateScheduled = true;
            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PrecreateStep));
        }

        private static void PrecreateStep()
        {
            if (ChipsByKey.Count >= precreateTarget)
            {
                precreateScheduled = false;
                return;
            }
            while (precreateIndex < KeyOrder.Length &&
                   ChipsByKey.ContainsKey(KeyOrder[precreateIndex].ToString()))
                precreateIndex++;
            if (precreateIndex >= KeyOrder.Length)
            {
                precreateScheduled = false;
                return;
            }
            GetChip(KeyOrder[precreateIndex].ToString());
            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(PrecreateStep));
        }

        private static Chip GetChip(string key)
        {
            Chip chip;
            if (ChipsByKey.TryGetValue(key, out chip))
                return chip;
            chip = CreateChip(key);
            ChipsByKey[key] = chip;
            return chip;
        }

        private static bool PlaceBadges(List<WindowRequest> windows, List<TaskbarButton> buttons)
        {
            var sw = Stopwatch.StartNew();

            // Longest titles first so a title that prefixes another can't steal its button
            var keyed = new List<WindowRequest>(windows);
            keyed.Sort((a, b) => b.Title.Length.CompareTo(a.Title.Length));

            var used = new bool[buttons.Count];
            var positions = new Dictionary<string, int[]>();
            foreach (var window in keyed)
            {
                if (window.Key == null || window.Key.Length != 1 ||
                    KeyOrder.IndexOf(window.Key[0]) < 0)
                    continue; // only the overlay's own shortcut keys can have a chip
                for (var i = 0; i < buttons.Count; i++)
                {
                    if (used[i] || !NameMatchesTitle(buttons[i].Name, window.Title))
                        continue;
                    used[i] = true;
                    // ponytail: UIA rect is physical pixels, WPF Left/Top are DIPs -
                    // identical at 100% DPI; needs TransformFromDevice if DPI scaling matters
                    positions[window.Key] = new[]
                    {
                        (int)buttons[i].Rect.X + 2,
                        (int)buttons[i].Rect.Y + 2
                    };
                    break;
                }
            }

            // Chips that pre-creation has not reached yet must exist before the
            // move batch, otherwise the badge is silently skipped this show.
            foreach (var key in positions.Keys)
                GetChip(key);

            ApplyPositions(positions, positions.Count > 0);
            sw.Stop();
            lastPlaceMs = sw.ElapsedMilliseconds;
            return positions.Count > 0;
        }

        /// <summary>
        /// Moves every chip (and the close chip) to its target in one
        /// DeferWindowPos batch: per-window SetWindowPos calls each cost a few ms,
        /// a single batch does not. A failed batch is abandoned (never passed to
        /// EndDeferWindowPos, per the Win32 contract) and the moves are applied
        /// directly instead.
        /// </summary>
        private static void ApplyPositions(Dictionary<string, int[]> positions, bool showCloseChip)
        {
            if (showCloseChip)
                EnsureCloseChip();

            var moveWatch = Stopwatch.StartNew();

            var slots = new List<Slot>();
            foreach (var key in KeyOrder)
            {
                Chip chip;
                if (!ChipsByKey.TryGetValue(key.ToString(), out chip))
                {
                    // Never create chips from here: hide must not allocate.
                    continue;
                }
                int[] position;
                var visible = positions.TryGetValue(key.ToString(), out position);
                slots.Add(new Slot
                {
                    Chip = chip,
                    X = visible ? position[0] : OffScreen,
                    Y = visible ? position[1] : OffScreen,
                    Visible = visible
                });
            }

            if (closeChip != null)
            {
                int[] closeTarget = CloseChipTarget(showCloseChip);
                slots.Add(new Slot
                {
                    Chip = closeChip,
                    X = closeTarget[0],
                    Y = closeTarget[1],
                    Visible = showCloseChip
                });
            }

            var moves = new List<Slot>();
            foreach (var slot in slots)
                if (slot.Chip.X != slot.X || slot.Chip.Y != slot.Y)
                    moves.Add(slot);

            if (moves.Count > 0)
                ApplyMoves(moves);

            foreach (var slot in slots)
                if (slot.Chip.X == slot.X && slot.Chip.Y == slot.Y)
                    slot.Chip.Visible = slot.Visible;

            moveWatch.Stop();
            lastMoves = moves.Count;
            lastMoveMs = moveWatch.ElapsedMilliseconds;
        }

        private static int[] CloseChipTarget(bool visible)
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            RECT rect;
            if (visible && tray != IntPtr.Zero && GetWindowRect(tray, out rect))
                return new[] { rect.Right - 20, rect.Top + 2 };
            return new[] { OffScreen, OffScreen };
        }

        private static void ApplyMoves(List<Slot> moves)
        {
            var hdwp = BeginDeferWindowPos(moves.Count);
            var batched = hdwp != IntPtr.Zero;
            if (batched)
            {
                foreach (var slot in moves)
                {
                    var next = DeferWindowPos(hdwp, slot.Chip.Handle, IntPtr.Zero,
                        slot.X, slot.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                    if (next == IntPtr.Zero)
                    {
                        // Leave the failed batch alone: the API forbids ending it
                        batched = false;
                        break;
                    }
                    hdwp = next;
                }
                if (batched)
                    batched = EndDeferWindowPos(hdwp);
            }

            if (batched)
            {
                foreach (var slot in moves)
                {
                    slot.Chip.X = slot.X;
                    slot.Chip.Y = slot.Y;
                }
                return;
            }

            // Direct fallback: apply every intended move, keep coordinates only
            // for the windows that actually moved.
            foreach (var slot in moves)
            {
                if (SetWindowPos(slot.Chip.Handle, IntPtr.Zero, slot.X, slot.Y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE))
                {
                    slot.Chip.X = slot.X;
                    slot.Chip.Y = slot.Y;
                }
            }
        }

        private class Slot
        {
            public Chip Chip;
            public int X;
            public int Y;
            public bool Visible;
        }

        private static HashSet<string> Titles(List<WindowRequest> windows)
        {
            var titles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var window in windows)
                if (!string.IsNullOrEmpty(window.Key) && !string.IsNullOrEmpty(window.Title))
                    titles.Add(window.Title);
            return titles;
        }

        private static Chip CreateChip(string key)
        {
            return CreateChipWindow(key, Color.FromArgb(0xFF, 0x1E, 0x1E, 0x1E), Brushes.White, true);
        }

        private static Chip CreateChipWindow(string text, Color background, Brush foreground, bool clickThrough)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var window = new Window
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
                Left = OffScreen,
                Top = OffScreen,
                Content = new Border
                {
                    Background = new SolidColorBrush(background),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)),
                    BorderThickness = new Thickness(1),
                    Child = textBlock
                }
            };
            var handle = ApplyChipExStyle(window, clickThrough);
            // Show once and park it: toggling IsVisible per show is far more
            // expensive than moving an already-shown window.
            window.Show();
            SetWindowPos(handle, IntPtr.Zero, OffScreen, OffScreen, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return new Chip
            {
                Window = window,
                Text = textBlock,
                Handle = handle,
                Visible = false,
                X = OffScreen,
                Y = OffScreen
            };
        }

        // Small clickable X at the taskbar's top-right corner; clicking it stops
        // the helper (the switcher respawns it on the next show).
        private static void EnsureCloseChip()
        {
            if (closeChip != null)
                return;
            closeChip = CreateChipWindow("X", Color.FromArgb(0xFF, 0x50, 0x1E, 0x1E), Brushes.White, false);
            closeChip.Window.Cursor = Cursors.Hand;
            closeChip.Window.MouseLeftButtonUp += (s, e) => app.Shutdown();
        }

        private static IntPtr ApplyChipExStyle(Window window, bool clickThrough)
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
            return hwnd;
        }

        // ----- taskbar lookup -----

        private class Chip
        {
            public Window Window;
            public TextBlock Text;
            public IntPtr Handle;
            public bool Visible;
            public int X;
            public int Y;
        }

        private class WindowRequest
        {
            public string Key;
            public string Title;
        }

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

        /// <summary>
        /// Every visible taskbar (the primary one plus all secondary monitors),
        /// ordered by handle so the signature is stable.
        /// </summary>
        private static List<IntPtr> TrayHandles()
        {
            var trays = new List<IntPtr>();
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero && IsWindowVisible(primary))
                trays.Add(primary);
            var previous = IntPtr.Zero;
            while (true)
            {
                var next = FindWindowEx(IntPtr.Zero, previous, "Shell_SecondaryTrayWnd", null);
                if (next == IntPtr.Zero)
                    break;
                if (IsWindowVisible(next))
                    trays.Add(next);
                previous = next;
            }
            trays.Sort((a, b) => a.ToInt64().CompareTo(b.ToInt64()));
            return trays;
        }

        /// <summary>
        /// Cheap identity of the current taskbar layout: handles + rects of every
        /// visible tray. A moved/resized taskbar, explorer restart or monitor
        /// change yields a different signature, so cached button rects are never
        /// reused for a stale geometry. Empty string = no visible taskbar.
        /// </summary>
        private static string ComputeTraySignature()
        {
            var sb = new StringBuilder();
            foreach (var tray in TrayHandles())
            {
                RECT rect;
                if (!GetWindowRect(tray, out rect))
                    continue;
                sb.Append(tray.ToInt64()).Append(':')
                    .Append(rect.Left).Append(',').Append(rect.Top).Append(',')
                    .Append(rect.Right).Append(',').Append(rect.Bottom).Append(';');
            }
            return sb.ToString();
        }

        private static List<TaskbarButton> FindTaskbarButtons(out string signature)
        {
            var buttons = new List<TaskbarButton>();
            signature = ComputeTraySignature();
            foreach (var tray in TrayHandles())
            {
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

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy,
            uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr BeginDeferWindowPos(int count);

        [DllImport("user32.dll")]
        private static extern IntPtr DeferWindowPos(IntPtr hWinPosInfo, IntPtr hWnd, IntPtr insertAfter,
            int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool EndDeferWindowPos(IntPtr hWinPosInfo);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
    }
}
