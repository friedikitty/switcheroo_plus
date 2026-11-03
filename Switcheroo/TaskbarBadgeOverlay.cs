using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
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
    /// </summary>
    public class TaskbarBadgeOverlay
    {
        private const string PipeName = "switcheroo_plus_badges";
        private const string HelperExeName = "switcheroo_plus_badges.exe";

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
            var request = new Dictionary<string, object>
            {
                { "action", "show" },
                { "timeout", timeoutSeconds },
                { "windows", payload }
            };
            // First contact cold-starts the helper (WPF + UIA take seconds);
            // never block the UI thread on that
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Call(request));
        }

        public void Hide()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                Call(new Dictionary<string, object> { { "action", "hide" } }));
        }

        /// <summary>
        /// Cache-only warm-up: starts the helper if needed and asks it to scan
        /// the taskbar once, so the first hotkey can draw badges from the cache
        /// instead of waiting for UI Automation.
        /// </summary>
        public void WarmUp()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                Call(new Dictionary<string, object> { { "action", "warm" } }));
        }

        /// <summary>Stops the helper process; it restarts on the next UpdateBadges.</summary>
        public void Close()
        {
            Call(new Dictionary<string, object> { { "action", "exit" } });
        }

        public List<Dictionary<string, string>> GetBadgeList()
        {
            var result = new List<Dictionary<string, string>>();
            var response = Call(new Dictionary<string, object> { { "action", "getbadges" } });
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

        private readonly object _sync = new object();

        private Dictionary<string, string> Call(Dictionary<string, object> request)
        {
            // Serialize calls so rapid show/hide pairs reach the helper in order
            lock (_sync)
            {
                var response = Roundtrip(request);
                if (response != null)
                    return response;
                if (!EnsureHelper())
                    return null;
                return Roundtrip(request);
            }
        }

        private static Dictionary<string, string> Roundtrip(Dictionary<string, object> request)
        {
            NamedPipeClientStream pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                pipe.Connect(300);
                var bytes = new UTF8Encoding(false).GetBytes(JsonHelper.Encode(request) + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                // No WaitForPipeDrain/ReadLine for fire-and-forget actions:
                // reading can hang if the helper dies mid-conversation.
                var action = Convert.ToString(request["action"]);
                if (action != "getbadges")
                    return new Dictionary<string, string> { { "ok", "true" } };
                using (var reader = new StreamReader(pipe, new UTF8Encoding(false)))
                {
                    var line = reader.ReadLine();
                    return line == null ? null : JsonHelper.Decode(line);
                }
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

        private static bool EnsureHelper()
        {
            if (Ping())
                return true;
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
            }
            catch
            {
                return false;
            }
            // Poll until the helper's pipe server is up (cold WPF start can take seconds)
            for (var i = 0; i < 100; i++)
            {
                if (Ping())
                    return true;
                System.Threading.Thread.Sleep(100);
            }
            return false;
        }

        private static bool Ping()
        {
            var response = Roundtrip(new Dictionary<string, object> { { "action", "ping" } });
            return response != null && response.ContainsKey("ok");
        }
    }
}
