/**
 * Switcheroo - The incremental-search task switcher for Windows.
 * http://www.switcheroo.io/
 *
 * Switcheroo is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Runtime.InteropServices;

namespace Switcheroo
{
    public enum HotkeyAvailability
    {
        Available,
        Occupied,
        NotProbed
    }

    /// <summary>
    /// Probes whether a shortcut is already claimed system-wide by another
    /// program (or by Windows itself, e.g. Win+E) using the same mechanism
    /// ShareX/NHotkey rely on: try RegisterHotKey and look at the result.
    /// This detects conflicts without triggering anything, but it is blind to
    /// hotkeys implemented with low-level keyboard hooks (WH_KEYBOARD_LL),
    /// which never register with the system.
    /// </summary>
    public static class GlobalHotkeyProbe
    {
        private const int ProbeId = 0x5AFE;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        /// <summary>
        /// Must be called on a thread with a message loop (the registration is
        /// tied to the calling thread; it is unregistered before returning).
        /// CapsLock shortcuts cannot be probed because CapsLock is not a
        /// RegisterHotKey modifier.
        /// </summary>
        public static HotkeyAvailability Check(HotkeySelection selection)
        {
            if (selection == null || selection.CapsLock)
            {
                return HotkeyAvailability.NotProbed;
            }

            uint modifiers = 0;
            if (selection.Alt) modifiers |= MOD_ALT;
            if (selection.Ctrl) modifiers |= MOD_CONTROL;
            if (selection.Shift) modifiers |= MOD_SHIFT;
            if (selection.WindowsKey) modifiers |= MOD_WIN;

            if (RegisterHotKey(IntPtr.Zero, ProbeId, modifiers, (uint) selection.KeyCode))
            {
                UnregisterHotKey(IntPtr.Zero, ProbeId);
                return HotkeyAvailability.Available;
            }

            return HotkeyAvailability.Occupied;
        }
    }
}
