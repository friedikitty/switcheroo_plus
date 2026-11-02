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
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Switcheroo.Core
{
    public static class WinApi
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);

        public static IntPtr Statusbar = FindWindow("Shell_TrayWnd", "");

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int EnumWindows(EnumWindowsProc ewp, int lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        public enum GetAncestorFlags
        {
            /// <summary>
            /// Retrieves the parent window. This does not include the owner, as it does with the GetParent function.
            /// </summary>
            GetParent = 1,

            /// <summary>
            /// Retrieves the root window by walking the chain of parent windows.
            /// </summary>
            GetRoot = 2,

            /// <summary>
            /// Retrieves the owned root window by walking the chain of parent and owner windows returned by GetParent. 
            /// </summary>
            GetRootOwner = 3
        }

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetAncestor(IntPtr hwnd, GetAncestorFlags flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetLastActivePopup(IntPtr hWnd);

        [Flags]
        public enum ProcessAccess
        {
            /// <summary>
            /// Required to create a thread.
            /// </summary>
            CreateThread = 0x0002,

            /// <summary>
            /// 
            /// </summary>
            SetSessionId = 0x0004,

            /// <summary>
            /// Required to perform an operation on the address space of a process 
            /// </summary>
            VmOperation = 0x0008,

            /// <summary>
            /// Required to read memory in a process using ReadProcessMemory.
            /// </summary>
            VmRead = 0x0010,

            /// <summary>
            /// Required to write to memory in a process using WriteProcessMemory.
            /// </summary>
            VmWrite = 0x0020,

            /// <summary>
            /// Required to duplicate a handle using DuplicateHandle.
            /// </summary>
            DupHandle = 0x0040,

            /// <summary>
            /// Required to create a process.
            /// </summary>
            CreateProcess = 0x0080,

            /// <summary>
            /// Required to set memory limits using SetProcessWorkingSetSize.
            /// </summary>
            SetQuota = 0x0100,

            /// <summary>
            /// Required to set certain information about a process, such as its priority class (see SetPriorityClass).
            /// </summary>
            SetInformation = 0x0200,

            /// <summary>
            /// Required to retrieve certain information about a process, such as its token, exit code, and priority class (see OpenProcessToken).
            /// </summary>
            QueryInformation = 0x0400,

            /// <summary>
            /// Required to suspend or resume a process.
            /// </summary>
            SuspendResume = 0x0800,

            /// <summary>
            /// Required to retrieve certain information about a process (see GetExitCodeProcess, GetPriorityClass, IsProcessInJob, QueryFullProcessImageName). 
            /// A handle that has the PROCESS_QUERY_INFORMATION access right is automatically granted PROCESS_QUERY_LIMITED_INFORMATION.
            /// </summary>
            QueryLimitedInformation = 0x1000,

            /// <summary>
            /// Required to wait for the process to terminate using the wait functions.
            /// </summary>
            Synchronize = 0x100000,

            /// <summary>
            /// Required to delete the object.
            /// </summary>
            Delete = 0x00010000,

            /// <summary>
            /// Required to read information in the security descriptor for the object, not including the information in the SACL. 
            /// To read or write the SACL, you must request the ACCESS_SYSTEM_SECURITY access right. For more information, see SACL Access Right.
            /// </summary>
            ReadControl = 0x00020000,

            /// <summary>
            /// Required to modify the DACL in the security descriptor for the object.
            /// </summary>
            WriteDac = 0x00040000,

            /// <summary>
            /// Required to change the owner in the security descriptor for the object.
            /// </summary>
            WriteOwner = 0x00080000,

            StandardRightsRequired = 0x000F0000,

            /// <summary>
            /// All possible access rights for a process object.
            /// </summary>
            AllAccess = StandardRightsRequired | Synchronize | 0xFFFF
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetWindow(IntPtr hWnd, GetWindowCmd uCmd);

        public enum GetWindowCmd : uint
        {
            GW_HWNDFIRST = 0,
            GW_HWNDLAST = 1,
            GW_HWNDNEXT = 2,
            GW_HWNDPREV = 3,
            GW_OWNER = 4,
            GW_CHILD = 5,
            GW_ENABLEDPOPUP = 6
        }


        [DllImport("kernel32.dll")]
        public static extern bool QueryFullProcessImageName(IntPtr hprocess, int dwFlags, StringBuilder lpExeName,
            out int size);

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(ProcessAccess dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, Keys[] lpKeyState, StringBuilder pwszBuff,
            int cchBuff, uint wFlags, IntPtr dwhkl);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetKeyboardLayout(uint threadId);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern bool GetKeyboardState(Keys[] keyStates);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hwindow, out uint processId);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;

        /// <summary>
        /// Brings a window to the foreground reliably, working around the Windows
        /// foreground-lock. <c>SwitchToThisWindow</c> / <c>SetForegroundWindow</c>
        /// alone are blocked when the calling process is NOT already the foreground
        /// process (for example, when the previous-app keyboard hook fires while the
        /// user is in another application). By briefly attaching the input queues of
        /// the calling thread, the foreground window's thread and the target's
        /// thread, <c>SetForegroundWindow</c> is permitted to take effect.
        /// </summary>
        public static void ForceSwitchToWindow(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return;

            var foregroundHwnd = GetForegroundWindow();
            var currentThread = GetCurrentThreadId();
            uint targetProcId;
            uint foregroundProcId;
            uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out targetProcId);
            uint foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, out foregroundProcId);

            bool attachedForeground = false;
            bool attachedTarget = false;
            try
            {
                if (foregroundThreadId != currentThread)
                    attachedForeground = AttachThreadInput(currentThread, foregroundThreadId, true);
                if (targetThreadId != currentThread)
                    attachedTarget = AttachThreadInput(currentThread, targetThreadId, true);

                if (IsIconic(targetHwnd))
                    ShowWindowAsync(targetHwnd, SW_RESTORE);
                else
                    ShowWindowAsync(targetHwnd, SW_SHOW);

                SetForegroundWindow(targetHwnd);

                // ponytail: in a disconnected session there is no foreground window, so the
                // attach above is impossible and SetForegroundWindow silently no-ops;
                // guarantee the z-order raise (focus stays with the foreground path).
                if (GetForegroundWindow() != targetHwnd)
                    SetWindowPos(targetHwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            finally
            {
                if (attachedForeground)
                    AttachThreadInput(currentThread, foregroundThreadId, false);
                if (attachedTarget)
                    AttachThreadInput(currentThread, targetThreadId, false);
            }
        }

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKeyEx(uint uCode, MapVirtualKeyMapTypes uMapType, IntPtr dwhkl);

        /// <summary>
        /// The set of valid MapTypes used in MapVirtualKey
        /// </summary>
        public enum MapVirtualKeyMapTypes : uint
        {
            /// <summary>
            /// uCode is a virtual-key code and is translated into a scan code.
            /// If it is a virtual-key code that does not distinguish between left- and
            /// right-hand keys, the left-hand scan code is returned.
            /// If there is no translation, the function returns 0.
            /// </summary>
            MAPVK_VK_TO_VSC = 0x00,

            /// <summary>
            /// uCode is a scan code and is translated into a virtual-key code that
            /// does not distinguish between left- and right-hand keys. If there is no
            /// translation, the function returns 0.
            /// </summary>
            MAPVK_VSC_TO_VK = 0x01,

            /// <summary>
            /// uCode is a virtual-key code and is translated into an unshifted
            /// character value in the low-order word of the return value. Dead keys (diacritics)
            /// are indicated by setting the top bit of the return value. If there is no
            /// translation, the function returns 0.
            /// </summary>
            MAPVK_VK_TO_CHAR = 0x02,

            /// <summary>
            /// Windows NT/2000/XP: uCode is a scan code and is translated into a
            /// virtual-key code that distinguishes between left- and right-hand keys. If
            /// there is no translation, the function returns 0.
            /// </summary>
            MAPVK_VSC_TO_VK_EX = 0x03,

            /// <summary>
            /// Not currently documented
            /// </summary>
            MAPVK_VK_TO_VSC_EX = 0x04
        }

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int message, int wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, int message, int wParam, IntPtr lParam);

        public enum ClassLongFlags
        {
            GCLP_MENUNAME = -8,
            GCLP_HBRBACKGROUND = -10,
            GCLP_HCURSOR = -12,
            GCLP_HICON = -14,
            GCLP_HMODULE = -16,
            GCL_CBWNDEXTRA = -18,
            GCL_CBCLSEXTRA = -20,
            GCLP_WNDPROC = -24,
            GCL_STYLE = -26,
            GCLP_HICONSM = -34,
            GCW_ATOM = -32
        }

        public static IntPtr GetClassLongPtr(IntPtr hWnd, ClassLongFlags flags)
        {
            return IntPtr.Size > 4 ? GetClassLongPtr64(hWnd, flags) : new IntPtr(GetClassLongPtr32(hWnd, flags));
        }

        [DllImport("user32.dll", EntryPoint = "GetClassLong")]
        public static extern uint GetClassLongPtr32(IntPtr hWnd, ClassLongFlags flags);

        [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
        public static extern IntPtr GetClassLongPtr64(IntPtr hWnd, ClassLongFlags flags);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            SendMessageTimeoutFlags fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [Flags]
        public enum SendMessageTimeoutFlags : uint
        {
            SMTO_NORMAL = 0x0,
            SMTO_BLOCK = 0x1,
            SMTO_ABORTIFHUNG = 0x2,
            SMTO_NOTIMEOUTIFNOTHUNG = 0x8
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        [DllImport("user32.dll")]
        public static extern int EnumPropsEx(IntPtr hWnd, EnumPropsExDelegate lpEnumFunc, IntPtr lParam);
        public delegate int EnumPropsExDelegate(IntPtr hwnd, IntPtr lpszString, long hData, long dwData);

        /// <summary>
        /// Cheap pid lookup for a window - no Process object, no process-list snapshot.
        /// </summary>
        public static uint GetWindowProcessId(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        /// <summary>
        /// Fast process name for a pid via QueryFullProcessImageName - microseconds,
        /// works for elevated processes too. Process.GetProcessById snapshots the whole
        /// process list (~10-30ms per call on busy systems), which made every popup
        /// take 1-2s when called per window. Returns null on failure.
        /// </summary>
        public static string GetProcessName(uint pid)
        {
            var handle = OpenProcess(ProcessAccess.QueryLimitedInformation, false, (int)pid);
            if (handle == IntPtr.Zero)
                return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(handle, 0, sb, out size))
                    return null;
                return System.IO.Path.GetFileNameWithoutExtension(sb.ToString());
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }
}