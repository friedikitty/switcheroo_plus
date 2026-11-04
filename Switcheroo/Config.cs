/*
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
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Switcheroo
{
    /// <summary>
    /// Configuration model for Switcheroo.  All user-facing settings are
    /// persisted to ~/switcheroo/config.jsonc as JSON with comments.
    /// </summary>
    public class SwitcherooConfig
    {
        // Main activation hotkey
        public bool ActivateAlt { get; set; } = true;
        public bool ActivateCtrl { get; set; } = false;
        public bool ActivateShift { get; set; } = false;
        public bool ActivateWindowsKey { get; set; } = false;
        public int ActivateHotKey { get; set; } = 32; // Space

        // Previous-app toggle hotkey
        public bool PreviousAppEnabled { get; set; } = true;
        public bool PreviousAppAlt { get; set; } = true;
        public bool PreviousAppCtrl { get; set; } = true;
        public bool PreviousAppShift { get; set; } = false;
        public bool PreviousAppWindowsKey { get; set; } = false;
        public bool PreviousAppCapsLock { get; set; } = false;
        public int PreviousAppHotKey { get; set; } = 221; // ]

        // Behaviour toggles
        public bool AltTabHook { get; set; } = true;
        public bool AutoSwitch { get; set; } = false;
        public bool RunAsAdmin { get; set; } = false;
        public bool EnableHotKey { get; set; } = false;
        public bool RunOnStartup { get; set; } = false;
        public bool OpenUsageHintsByDefault { get; set; } = false;
        public int WindowWidth { get; set; } = 1440;
        public int WindowHeight { get; set; } = 698;
        public bool MultiColumnEnabled { get; set; } = true;

        // Pinned-recent list ordering: previous app stays first, the rest of the
        // top group is alphabetical so the visible order stays stable.
        public bool RecentWindowsOnTopEnabled { get; set; } = true;
        public int RecentWindowsOnTopCount { get; set; } = 5;

        // 0-9 / a-z / A-Z overlay shortcuts
        public bool AzShortcutsEnabled { get; set; } = true;

        // Draw the quick-switch key at the top-left of each window's taskbar button
        public bool TaskbarBadgesEnabled { get; set; } = true;

        // Seconds before taskbar badges auto-hide; 0 = stay until the overlay closes
        public int TaskbarBadgeTimeoutSeconds { get; set; } = 10;

        // Show an accent border and fading blue dot on the activated window
        public bool SwitchHighlightEnabled { get; set; } = true;

        /// <summary>
        /// Legacy config directory used by older builds. Kept for one-time migration.
        /// </summary>
        private static string LegacyConfigDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "switcheroo");
            }
        }

        private static string LegacyConfigPath
        {
            get { return Path.Combine(LegacyConfigDirectory, "config.jsonc"); }
        }

        public static string ConfigDirectory
        {
            get
            {
                if (!string.IsNullOrEmpty(Switcheroo.Shell.ShellProtocol.ConfigRoot))
                    return Switcheroo.Shell.ShellProtocol.ConfigRoot;
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config",
                    ".switcheroo_plus");
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(ConfigDirectory, "config.jsonc"); }
        }

        /// <summary>
        /// Migrates a legacy config file to the new location if present.
        /// </summary>
        public static void MigrateLegacyConfig()
        {
            try
            {
                if (Switcheroo.Shell.ShellProtocol.TestMode || !File.Exists(LegacyConfigPath))
                    return;
                if (File.Exists(ConfigPath))
                    return;

                if (!Directory.Exists(ConfigDirectory))
                    Directory.CreateDirectory(ConfigDirectory);

                File.Copy(LegacyConfigPath, ConfigPath);
            }
            catch
            {
                // Migration is best-effort; do not crash on failure.
            }
        }
    }

    /// <summary>
    /// Reads and writes <see cref="SwitcherooConfig"/> as JSONC.
    /// </summary>
    public static class ConfigManager
    {
        public static SwitcherooConfig Load()
        {
            SwitcherooConfig.MigrateLegacyConfig();

            var path = SwitcherooConfig.ConfigPath;
            if (!File.Exists(path))
                return new SwitcherooConfig();

            try
            {
                var jsonc = File.ReadAllText(path, Encoding.UTF8);
                var json = StripComments(jsonc);
                return FromJson(json);
            }
            catch
            {
                return new SwitcherooConfig();
            }
        }

        public static void Save(SwitcherooConfig config)
        {
            var dir = SwitcherooConfig.ConfigDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(SwitcherooConfig.ConfigPath, ToJson(config), Encoding.UTF8);
        }

        private static string StripComments(string input)
        {
            // Strip block comments /* */
            var block = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);
            input = block.Replace(input, string.Empty);

            // Strip line comments //...
            var line = new Regex(@"//.*$", RegexOptions.Multiline);
            input = line.Replace(input, string.Empty);

            return input;
        }

        private static SwitcherooConfig FromJson(string json)
        {
            var cfg = new SwitcherooConfig();
            var t = typeof(SwitcherooConfig);

            // Very small JSON parser: expects {"key": value, ...}
            json = json.Trim();
            if (!json.StartsWith("{") || !json.EndsWith("}"))
                return cfg;

            json = json.Substring(1, json.Length - 2).Trim();
            if (string.IsNullOrEmpty(json))
                return cfg;

            var pairs = SplitTopLevel(json);
            foreach (var pair in pairs)
            {
                var colon = pair.IndexOf(':');
                if (colon < 0) continue;

                var key = pair.Substring(0, colon).Trim().Trim('"');
                var val = pair.Substring(colon + 1).Trim();

                var prop = t.GetProperty(key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || !prop.CanWrite) continue;

                try
                {
                    if (prop.PropertyType == typeof(bool))
                    {
                        prop.SetValue(cfg, bool.Parse(val), null);
                    }
                    else if (prop.PropertyType == typeof(int))
                    {
                        prop.SetValue(cfg, int.Parse(val), null);
                    }
                    else if (prop.PropertyType == typeof(string))
                    {
                        prop.SetValue(cfg, val.Trim('"'), null);
                    }
                }
                catch
                {
                    // ignore malformed values
                }
            }

            return cfg;
        }

        private static string[] SplitTopLevel(string json)
        {
            var parts = new System.Collections.Generic.List<string>();
            int depth = 0;
            bool inString = false;
            bool escape = false;
            int start = 0;

            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (escape)
                {
                    escape = false;
                    continue;
                }
                if (c == '\\')
                {
                    escape = true;
                    continue;
                }
                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;

                if (c == '{' || c == '[') depth++;
                if (c == '}' || c == ']') depth--;

                if (c == ',' && depth == 0)
                {
                    parts.Add(json.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (start < json.Length)
                parts.Add(json.Substring(start));

            return parts.ToArray();
        }

        private static string ToJson(SwitcherooConfig cfg)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  // Main activation hotkey");
            sb.AppendLine(string.Format("  \"ActivateAlt\": {0},", Bool(cfg.ActivateAlt)));
            sb.AppendLine(string.Format("  \"ActivateCtrl\": {0},", Bool(cfg.ActivateCtrl)));
            sb.AppendLine(string.Format("  \"ActivateShift\": {0},", Bool(cfg.ActivateShift)));
            sb.AppendLine(string.Format("  \"ActivateWindowsKey\": {0},", Bool(cfg.ActivateWindowsKey)));
            sb.AppendLine(string.Format("  \"ActivateHotKey\": {0},", cfg.ActivateHotKey));
            sb.AppendLine();
            sb.AppendLine("  // Previous-app toggle hotkey (switch back / forth without overlay)");
            sb.AppendLine(string.Format("  \"PreviousAppEnabled\": {0},", Bool(cfg.PreviousAppEnabled)));
            sb.AppendLine(string.Format("  \"PreviousAppAlt\": {0},", Bool(cfg.PreviousAppAlt)));
            sb.AppendLine(string.Format("  \"PreviousAppCtrl\": {0},", Bool(cfg.PreviousAppCtrl)));
            sb.AppendLine(string.Format("  \"PreviousAppShift\": {0},", Bool(cfg.PreviousAppShift)));
            sb.AppendLine(string.Format("  \"PreviousAppWindowsKey\": {0},", Bool(cfg.PreviousAppWindowsKey)));
            sb.AppendLine(string.Format("  \"PreviousAppCapsLock\": {0},", Bool(cfg.PreviousAppCapsLock)));
            sb.AppendLine(string.Format("  \"PreviousAppHotKey\": {0},", cfg.PreviousAppHotKey));
            sb.AppendLine();
            sb.AppendLine("  // Behaviour toggles");
            sb.AppendLine(string.Format("  \"AltTabHook\": {0},", Bool(cfg.AltTabHook)));
            sb.AppendLine(string.Format("  \"AutoSwitch\": {0},", Bool(cfg.AutoSwitch)));
            sb.AppendLine(string.Format("  \"RunAsAdmin\": {0},", Bool(cfg.RunAsAdmin)));
            sb.AppendLine(string.Format("  \"EnableHotKey\": {0},", Bool(cfg.EnableHotKey)));
            sb.AppendLine(string.Format("  \"RunOnStartup\": {0},", Bool(cfg.RunOnStartup)));
            sb.AppendLine(string.Format("  \"OpenUsageHintsByDefault\": {0},", Bool(cfg.OpenUsageHintsByDefault)));
            sb.AppendLine(string.Format("  \"WindowWidth\": {0},", cfg.WindowWidth));
            sb.AppendLine(string.Format("  \"WindowHeight\": {0},", cfg.WindowHeight));
            sb.AppendLine(string.Format("  \"MultiColumnEnabled\": {0},", Bool(cfg.MultiColumnEnabled)));
            sb.AppendLine();
            sb.AppendLine("  // Pin the most recently used windows at the top of the list");
            sb.AppendLine("  // (previous app stays first, the rest of the group is alphabetical)");
            sb.AppendLine(string.Format("  \"RecentWindowsOnTopEnabled\": {0},", Bool(cfg.RecentWindowsOnTopEnabled)));
            sb.AppendLine(string.Format("  \"RecentWindowsOnTopCount\": {0},", cfg.RecentWindowsOnTopCount));
            sb.AppendLine();
            sb.AppendLine("  // Show 0-9 / a-z / A-Z shortcuts in the overlay");
            sb.AppendLine(string.Format("  \"AzShortcutsEnabled\": {0},", Bool(cfg.AzShortcutsEnabled)));
            sb.AppendLine();
            sb.AppendLine("  // Draw the quick-switch key at the top-left of each window's taskbar button");
            sb.AppendLine(string.Format("  \"TaskbarBadgesEnabled\": {0},", Bool(cfg.TaskbarBadgesEnabled)));
            sb.AppendLine();
            sb.AppendLine("  // Seconds before taskbar badges auto-hide; 0 = stay until the overlay closes");
            sb.AppendLine(string.Format("  \"TaskbarBadgeTimeoutSeconds\": {0},", cfg.TaskbarBadgeTimeoutSeconds));
            sb.AppendLine();
            sb.AppendLine("  // Show an accent border and fading blue dot on the activated window");
            sb.AppendLine(string.Format("  \"SwitchHighlightEnabled\": {0}", Bool(cfg.SwitchHighlightEnabled)));
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
