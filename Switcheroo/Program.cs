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
using System.Configuration;
using System.Diagnostics;
using System.Threading;
using Switcheroo.Properties;

namespace Switcheroo
{
    internal class Program
    {
        private const string mutex_id = "DBDE24E4-91F6-11DF-B495-C536DFD72085-switcheroo";

        [STAThread]
        private static void Main(string[] args)
        {
            var autoMode = IsAutoMode(args);
            if (Array.IndexOf(args, "--supervised") < 0)
            {
                var launcher = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "switcheroo_plus_watchdog.exe");
                Process.Start(new ProcessStartInfo(launcher, string.Join(" ", Array.ConvertAll(args, a => "\"" + a.Replace("\"", "\\\"") + "\""))) { UseShellExecute = false });
                return;
            }
            if (Switcheroo.Shell.ShellProtocol.TestMode && Environment.GetEnvironmentVariable("SWITCHEROO_TEST_STARTUP_CRASH") == "1")
                Environment.FailFast("Injected startup crash");

            if (Switcheroo.Shell.ShellProtocol.TestMode && Environment.GetEnvironmentVariable("SWITCHEROO_TEST_STARTUP_HANG") == "1")
                Thread.Sleep(Timeout.Infinite);

            using (var mutex = new Mutex(false, mutex_id + Switcheroo.Shell.ShellProtocol.Suffix))
            {
                var hasHandle = false;
                try
                {
                    try
                    {
                        hasHandle = mutex.WaitOne(5000, false);
                        if (hasHandle == false) return; //another instance exist
                    }
                    catch (AbandonedMutexException)
                    {
                        hasHandle = true;
                    }

#if PORTABLE
                        MakePortable(Settings.Default);
#endif

                    if (!Switcheroo.Shell.ShellProtocol.TestMode) MigrateUserSettings();

                    var app = new App
                    {
                        MainWindow = new MainWindow(autoMode)
                    };
                    app.Exit += (sender, e) => { try { Switcheroo.Shell.ShellProtocol.Request(Switcheroo.Shell.ShellProtocol.WatchdogPipe, "coreexiting", 500); } catch { } };
                    app.Run();
                }
                finally
                {
                    if (hasHandle)
                        mutex.ReleaseMutex();
                }
            }
        }

        private static bool IsAutoMode(string[] args)
        {
            if (args == null) return false;
            foreach (var arg in args)
            {
                if (string.Equals(arg, "--auto", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-auto", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void MakePortable(ApplicationSettingsBase settings)
        {
            var portableSettingsProvider = new PortableSettingsProvider();
            settings.Providers.Add(portableSettingsProvider);
            foreach (SettingsProperty prop in settings.Properties)
            {
                prop.Provider = portableSettingsProvider;
            }
            settings.Reload();
        }

        private static void MigrateUserSettings()
        {
            SwitcherooConfig.MigrateLegacyConfig();
            if (System.IO.File.Exists(SwitcherooConfig.ConfigPath) || !Settings.Default.FirstRun) return;
            Settings.Default.Upgrade();
            var values = new System.Collections.Generic.Dictionary<string, object>();
            foreach (SettingsPropertyValue value in Settings.Default.PropertyValues)
                if (value.Name != "FirstRun" && !value.UsingDefaultValue) values[value.Name] = value.PropertyValue;
            if (values.Count == 0) return;
            var cfg = ConfigManager.FromLegacyValues(values);
            ConfigManager.Save(cfg);
            Settings.Default.FirstRun = false;
            Settings.Default.Save();
        }

    }
}
