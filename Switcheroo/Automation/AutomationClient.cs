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
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Switcheroo.Automation
{
    /// <summary>
    /// Small command-line client that talks to a running Switcheroo instance over its
    /// automation named pipe. Useful for driving the app from scripts or automated tests.
    /// </summary>
    internal class Program
    {
        private static string PipeName { get { return "switcheroo_plus_automation_" + (Environment.GetEnvironmentVariable("SWITCHEROO_TEST_MODE") == "1" ? Environment.GetEnvironmentVariable("SWITCHEROO_TEST_ID") : System.Diagnostics.Process.GetCurrentProcess().SessionId.ToString()); } }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var action = args[0].ToLowerInvariant();
            var request = new System.Collections.Generic.Dictionary<string, object> { ["action"] = action };

            switch (action)
            {
                case "ping":
                case "show":
                case "hide":
                case "activate":
                case "previousapp":
                case "getwindows":
                case "getconfig":
                case "checkorder":
                case "exit":
                    break;

                case "setquery":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: Switcheroo.Automation.Client setquery <query>");
                        return 1;
                    }
                    request["query"] = args[1];
                    break;

                case "select":
                    if (args.Length < 2 || !int.TryParse(args[1], out var idx))
                    {
                        Console.Error.WriteLine("Usage: Switcheroo.Automation.Client select <index>");
                        return 1;
                    }
                    request["index"] = idx;
                    break;

                case "setconfig":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("Usage: Switcheroo.Automation.Client setconfig <json-path>");
                        return 1;
                    }
                    request["config"] = File.ReadAllText(args[1], Encoding.UTF8);
                    break;

                default:
                    Console.Error.WriteLine("Unknown action: " + action);
                    PrintUsage();
                    return 1;
            }

            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut))
                {
                    pipe.Connect(5000);
                    pipe.ReadMode = PipeTransmissionMode.Message;

                    var requestJson = JsonHelper.Encode(request);
                    var bytes = new UTF8Encoding(false).GetBytes(requestJson + "\n");
                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();

                    var sb = new StringBuilder();
                    var buffer = new byte[4096];
                    do
                    {
                        int read = pipe.Read(buffer, 0, buffer.Length);
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    }
                    while (!pipe.IsMessageComplete);

                    Console.WriteLine(sb.ToString().TrimEnd());
                    return 0;
                }
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("Could not connect to Switcheroo. Is it running?");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 3;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Switcheroo automation client");
            Console.WriteLine("Usage: Switcheroo.Automation.Client <action> [args]");
            Console.WriteLine();
            Console.WriteLine("Actions:");
            Console.WriteLine("  ping                          - check connectivity");
            Console.WriteLine("  show                          - show the Switcheroo overlay");
            Console.WriteLine("  hide                          - hide the overlay");
            Console.WriteLine("  setquery <query>             - set the search query");
            Console.WriteLine("  getwindows                    - list matched windows as JSON");
            Console.WriteLine("  select <index>               - select a window by index");
            Console.WriteLine("  activate                      - activate the selected window");
            Console.WriteLine("  previousapp                   - trigger the previous-app shortcut");
            Console.WriteLine("  getconfig                     - dump the current config JSON");
            Console.WriteLine("  setconfig <json-path>        - replace config from a JSON file");
            Console.WriteLine("  exit                          - shut down Switcheroo");
        }
    }
}
