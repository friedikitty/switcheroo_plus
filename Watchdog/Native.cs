using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Switcheroo.Shell;

namespace Switcheroo.Watchdog
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int cb; public string reserved, desktop, title;
            public int x,y,cx,cy,charsX,charsY,fill,flags;
            public short show,reservedSize; public IntPtr reservedPtr,input,output,error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInfo { public IntPtr process, thread; public uint pid, tid; }
        [StructLayout(LayoutKind.Sequential, Pack=4)]
        internal struct ExceptionInfo { public uint thread; public IntPtr pointers; [MarshalAs(UnmanagedType.Bool)] public bool clientPointers; }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        internal static extern bool CreateProcess(string app, System.Text.StringBuilder command, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool WaitForDebugEvent(IntPtr ev, uint timeout);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
        [DllImport("kernel32.dll")] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetThreadContext(IntPtr thread, IntPtr context);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
        [DllImport("kernel32.dll")] internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("dbghelp.dll", SetLastError=true)]
        internal static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file, uint type, IntPtr exception, IntPtr streams, IntPtr callback);

        internal static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }
        internal static IntPtr NewJob()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: BasicLimitInformation.LimitFlags is at 16 on both architectures.
            int size = IntPtr.Size == 8 ? 144 : 112;
            var data = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.Copy(new byte[size], 0, data, size);
                Marshal.WriteInt32(data, 16, 0x2000); // KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(job, 9, data, (uint)size)) { CloseHandle(job); throw new Win32Exception(); }
            }
            finally { Marshal.FreeHGlobal(data); }
            return job;
        }
        internal static byte[] Context(uint tid)
        {
            var handle = OpenThread(0x8, false, tid);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            var raw = Marshal.AllocHGlobal(4112);
            var aligned = new IntPtr((raw.ToInt64()+15)&~15L);
            try
            {
                Marshal.Copy(new byte[4096],0,aligned,4096);
                Marshal.WriteInt32(aligned, IntPtr.Size == 8 ? 48 : 0, IntPtr.Size == 8 ? 0x10001f : 0x1003f);
                if (!GetThreadContext(handle, aligned)) throw new Win32Exception();
                var bytes = new byte[IntPtr.Size == 8 ? 1232 : 716];
                Marshal.Copy(aligned,bytes,0,bytes.Length);
                return bytes;
            }
            finally { CloseHandle(handle); Marshal.FreeHGlobal(raw); }
        }
        // This worker never creates a tray icon, takes a mutex, or starts the core.
        internal static int WriteDump(string requestPath)
        {
            using (var deadline = new Timer(_ => Process.GetCurrentProcess().Kill(), null, 35000, Timeout.Infinite))
                return WriteDumpCore(requestPath);
        }
        private static int WriteDumpCore(string requestPath)
        {
            var request = ShellProtocol.Decode(File.ReadAllText(requestPath));
            if(ShellProtocol.TestMode && request.ContainsKey("delayMs")) Thread.Sleep(Convert.ToInt32(request["delayMs"]));
            uint pid = Convert.ToUInt32(request["pid"]);
            var process = OpenProcess(0x410, false, pid);
            if (process == IntPtr.Zero) return Marshal.GetLastWin32Error();
            IntPtr record = IntPtr.Zero, context = IntPtr.Zero, pointers = IntPtr.Zero, info = IntPtr.Zero;
            try
            {
                if (request.ContainsKey("record"))
                {
                    var rb = Convert.FromBase64String((string)request["record"]);
                    var cb = Convert.FromBase64String((string)request["context"]);
                    record=Marshal.AllocHGlobal(rb.Length); context=Marshal.AllocHGlobal(cb.Length);
                    Marshal.Copy(rb,0,record,rb.Length); Marshal.Copy(cb,0,context,cb.Length);
                    pointers=Marshal.AllocHGlobal(IntPtr.Size*2);
                    Marshal.WriteIntPtr(pointers,record); Marshal.WriteIntPtr(pointers,IntPtr.Size,context);
                    var ex = new ExceptionInfo { thread=Convert.ToUInt32(request["thread"]), pointers=pointers };
                    info=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(ExceptionInfo)));
                    Marshal.StructureToPtr(ex,info,false);
                }
                using (var file = new FileStream((string)request["dump"],FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    if (!MiniDumpWriteDump(process,pid,file.SafeFileHandle.DangerousGetHandle(),2|0x20|0x800|0x1000,info,IntPtr.Zero,IntPtr.Zero))
                        return Marshal.GetLastWin32Error();
                }
                return 0;
            }
            finally
            {
                CloseHandle(process);
                foreach(var ptr in new[]{record,context,pointers,info}) if(ptr!=IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
