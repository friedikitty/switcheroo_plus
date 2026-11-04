using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Switcheroo.Automation;

namespace Switcheroo
{
    /// <summary>
    /// Client for the badge helper process (switcheroo_plus_badges.exe). The
    /// helper owns all taskbar interaction - UIA button lookup, chip windows,
    /// show timeout, close button - so a badge failure can never crash or hang
    /// the switcher. It is started once on demand and reused; if it died, the
    /// next call respawns it. All calls are best-effort: any failure just
    /// yields no badges.
    ///
    /// Commands go through one FIFO queue drained by a single background
    /// thread, so show/hide keep their submission order (thread-pool callbacks
    /// racing for a lock do not). Nothing here is allowed to block the UI
    /// thread: sends are fire-and-forget with a connect timeout, the only
    /// response read (`getbadges`, a test hook) is bounded, and Close() neither
    /// spawns nor waits for a helper that stopped answering.
    /// </summary>
    public class TaskbarBadgeOverlay
    {
        private static string PipeName { get { return "switcheroo_plus_badges" + Switcheroo.Shell.ShellProtocol.Suffix; } }
        private const string HelperExeName = "switcheroo_plus_badges.exe";
        private const int ConnectTimeoutMs = 300;
        private const int ResponseTimeoutMs = 1000;
        private const int HelperStartTimeoutMs = 5000;
        private const int ShutdownTimeoutMs = 500;

        private readonly object _sync = new object();
        private readonly Queue<Dictionary<string, object>> _queue = new Queue<Dictionary<string, object>>();
        private Thread _worker;
        private bool _closing;

        public void UpdateBadges(IList<AppWindowViewModel> windows, int timeoutSeconds)
        {
            var payload = new List<Dictionary<string, string>>();
            foreach (var window in windows)
            {
                if (string.IsNullOrEmpty(window.ShortcutKey))
                    continue;
                payload.Add(new Dictionary<string, string>
                {
                    { "key", window.ShortcutKey },
                    { "title", window.AppWindow.Title ?? "" }
                });
            }
            Enqueue(new Dictionary<string, object>
            {
                { "action", "show" },
                { "timeout", timeoutSeconds },
                { "windows", payload }
            });
        }

        public void Hide()
        {
            Enqueue(new Dictionary<string, object> { { "action", "hide" } });
        }

        /// <summary>
        /// Cache-only warm-up: starts the helper if needed and asks it to scan
        /// the taskbar once, so the first hotkey can draw badges from the cache
        /// instead of waiting for UI Automation.
        /// </summary>
        public void WarmUp()
        {
            Enqueue(new Dictionary<string, object> { { "action", "warm" } });
        }

        /// <summary>
        /// Stops the helper process; it restarts on the next UpdateBadges. Bounded
        /// and spawn-free: shutdown must never wait on a helper that stopped
        /// answering, and nothing queued may bring it back afterwards.
        /// </summary>
        public void Close()
        {
            lock (_sync)
            {
                _closing = true;
                _queue.Clear();
                Monitor.PulseAll(_sync);
            }
            var request = new Dictionary<string, object> { { "action", "exit" } };
            var sender = new Thread(() => TrySend(request))
            {
                IsBackground = true,
                Name = "BadgePipeShutdown"
            };
            sender.Start();
            sender.Join(ShutdownTimeoutMs);
        }

        public List<Dictionary<string, string>> GetBadgeList()
        {
            var result = new List<Dictionary<string, string>>();
            lock (_sync)
            {
                if (_closing)
                    return result;
            }
            var response = RequestWithResponse(
                new Dictionary<string, object> { { "action", "getbadges" } }, ResponseTimeoutMs);
            string badges;
            if (response == null || !response.TryGetValue("badges", out badges))
                return result;
            foreach (var entry in badges.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var at = entry.IndexOf('@');
                var comma = at < 0 ? -1 : entry.IndexOf(',', at);
                if (comma < 0)
                    continue;
                result.Add(new Dictionary<string, string>
                {
                    { "key", entry.Substring(0, at) },
                    { "x", entry.Substring(at + 1, comma - at - 1) },
                    { "y", entry.Substring(comma + 1) }
                });
            }
            return result;
        }

        private bool IsClosing()
        {
            lock (_sync)
                return _closing;
        }

        private void Enqueue(Dictionary<string, object> request)
        {
            lock (_sync)
            {
                if (_closing)
                    return;
                _queue.Enqueue(request);
                if (_worker == null)
                {
                    _worker = new Thread(WorkerLoop)
                    {
                        IsBackground = true,
                        Name = "BadgePipeClient"
                    };
                    _worker.Start();
                }
                Monitor.Pulse(_sync);
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                Dictionary<string, object> request;
                lock (_sync)
                {
                    while (!_closing && _queue.Count == 0)
                        Monitor.Wait(_sync);
                    if (_queue.Count == 0)
                        return; // closing with nothing left to send
                    request = _queue.Dequeue();
                }
                SendWithSpawn(request);
            }
        }

        private void SendWithSpawn(Dictionary<string, object> request)
        {
            if (TrySend(request))
                return;
            if (IsClosing())
                return;
            if (!EnsureHelper())
                return;
            TrySend(request);
        }

        /// <summary>
        /// Fire-and-forget: connect + write + close. Only `getbadges` reads an
        /// answer, and that goes through <see cref="RequestWithResponse"/>, so a
        /// helper that dies mid-conversation can never hang the UI thread.
        /// </summary>
        private static bool TrySend(Dictionary<string, object> request)
        {
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                pipe.Connect(ConnectTimeoutMs);
                var bytes = new UTF8Encoding(false).GetBytes(JsonHelper.Encode(request) + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (pipe != null) pipe.Dispose(); }
                catch { }
            }
        }

        private static Dictionary<string, string> RequestWithResponse(Dictionary<string, object> request,
            int timeoutMs)
        {
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                pipe.Connect(ConnectTimeoutMs);
                var bytes = new UTF8Encoding(false).GetBytes(JsonHelper.Encode(request) + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();

                // Read on a helper thread so a stuck pipe can be abandoned; the
                // pipe is disposed if the deadline passes, which unblocks it.
                string line = null;
                var local = pipe;
                var reader = new Thread(() =>
                {
                    try
                    {
                        using (var streamReader = new StreamReader(local, new UTF8Encoding(false)))
                            line = streamReader.ReadLine();
                    }
                    catch
                    {
                    }
                })
                {
                    IsBackground = true,
                    Name = "BadgePipeRead"
                };
                reader.Start();
                if (!reader.Join(timeoutMs))
                    return null;
                return line == null ? null : JsonHelper.Decode(line);
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (pipe != null) pipe.Dispose(); }
                catch { }
            }
        }

        private bool EnsureHelper()
        {
            try
            {
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, HelperExeName);
                if (!File.Exists(exe))
                    return false;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                // A second copy exits immediately on its single-instance mutex.
            }
            catch
            {
                // Fall through to the ping poll: the helper may already be up
            }
            // Poll until the helper's pipe accepts a line (cold WPF start is slow)
            var deadline = Environment.TickCount + HelperStartTimeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (IsClosing())
                    return false;
                if (TrySend(new Dictionary<string, object> { { "action", "ping" } }))
                    return true;
                Thread.Sleep(100);
            }
            return false;
        }
    }
}
