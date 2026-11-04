using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Switcheroo.Shell
{
    internal static class ShellProtocol
    {
        public static bool TestMode { get { return Environment.GetEnvironmentVariable("SWITCHEROO_TEST_MODE") == "1"; } }
        public static string Suffix { get { return TestMode ? "_" + Environment.GetEnvironmentVariable("SWITCHEROO_TEST_ID") : "_" + System.Diagnostics.Process.GetCurrentProcess().SessionId; } }
        public static string CorePipe { get { return "switcheroo_plus_automation" + Suffix; } }
        public static string WatchdogPipe { get { return "switcheroo_plus_watchdog" + Suffix; } }
        public static string ConfigRoot { get { return TestMode ? Environment.GetEnvironmentVariable("SWITCHEROO_TEST_ROOT") : null; } }
        public static PipeSecurity Security()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            return security;
        }
        public static string Encode(object value) { return new JavaScriptSerializer().Serialize(value); }
        public static Dictionary<string, object> Decode(string value) { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(value); }
        public static string ReadLine(StreamReader reader, int timeout)
        {
            var task = reader.ReadLineAsync();
            if (!task.Wait(timeout)) throw new TimeoutException("Pipe response timed out");
            return task.Result;
        }
        public static string Request(string pipe, string action, int timeout = 1000)
        {
            using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                client.Connect(timeout);
                using (var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true))
                using (var reader = new StreamReader(client, Encoding.UTF8, false, 4096, true))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine(Encode(new { action = action }));
                    return ReadLine(reader, timeout);
                }
            }
        }
        public static Thread Serve(string name, Func<string, object> dispatch)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, Security()))
                        {
                            pipe.WaitForConnection();
                            using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                            using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                            {
                                writer.AutoFlush = true;
                                object response;
                                try
                                {
                                    var request = Decode(ReadLine(reader, 2000));
                                    response = dispatch(Convert.ToString(request["action"]));
                                }
                                catch (Exception e) { response = new { error = e.Message }; }
                                writer.WriteLine(Encode(response));
                            }
                        }
                    }
                    catch (IOException) { }
                    catch (TimeoutException) { }
                }
            });
            thread.IsBackground = true;
            thread.Name = name;
            thread.Start();
            return thread;
        }
    }
}
