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
using ManagedWinapi.Hooks;

namespace Switcheroo
{
    /// <summary>
    /// Low-level keyboard hook that turns an arbitrary modifier+key combination
    /// into a previous-app toggle.
    /// </summary>
    /// <remarks>
    /// This is used instead of <c>RegisterHotKey</c> because RegisterHotKey cannot
    /// represent CapsLock as a modifier and silently fails for combos Windows owns
    /// (such as Alt+Tab). The low-level hook sees every keystroke, so it reliably
    /// detects the exact configured combination regardless of what the OS does with
    /// it.
    /// <para>
    /// Modifier state is tracked from the message stream itself (rather than polled
    /// via <c>GetAsyncKeyState</c>) so detection is consistent on the hook thread.
    /// When the target key goes down with exactly the required modifiers held, the
    /// <see cref="Pressed"/> event fires and the keystroke is swallowed so the OS
    /// does not also act on it.
    /// </para>
    /// <para>
    /// When CapsLock is part of the shortcut, every CapsLock key event is swallowed
    /// so the keyboard LED / toggle state does not flip while it is used as a
    /// modifier. Note: if CapsLock has been remapped or disabled via a registry
    /// Scancode Map, the hook will never see it and this shortcut cannot work.
    /// </para>
    /// </remarks>
    public class PreviousAppHook : IDisposable
    {
        public event EventHandler Pressed;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private readonly LowLevelKeyboardHook _hook;

        private readonly bool _wantAlt;
        private readonly bool _wantCtrl;
        private readonly bool _wantShift;
        private readonly bool _wantWin;
        private readonly bool _wantCapsLock;
        private readonly int _vkCode;

        // Live modifier state, tracked from the low-level message stream.
        private bool _altDown;
        private bool _ctrlDown;
        private bool _shiftDown;
        private bool _winDown;
        private bool _capsLockDown;

        public PreviousAppHook(bool alt, bool ctrl, bool shift, bool win, bool capsLock, int vkCode)
        {
            _wantAlt = alt;
            _wantCtrl = ctrl;
            _wantShift = shift;
            _wantWin = win;
            _wantCapsLock = capsLock;
            _vkCode = vkCode;

            _hook = new LowLevelKeyboardHook();
            _hook.MessageIntercepted += OnMessageIntercepted;
        }

        public void StartHook()
        {
            _altDown = _ctrlDown = _shiftDown = _winDown = _capsLockDown = false;
            _hook.StartHook();
        }

        private void OnMessageIntercepted(LowLevelMessage lowLevelMessage, ref bool handled)
        {
            var keyboardMessage = lowLevelMessage as LowLevelKeyboardMessage;
            if (handled || keyboardMessage == null)
                return;

            var msg = keyboardMessage.Message;
            var vk = keyboardMessage.VirtualKeyCode;
            var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // Track modifiers as they flow past, keeping state in sync. They are
            // allowed to pass through (we only swallow the final shortcut).
            if (IsAltKey(vk))
            {
                if (isDown) _altDown = true; else if (isUp) _altDown = false;
                return;
            }
            if (IsCtrlKey(vk))
            {
                if (isDown) _ctrlDown = true; else if (isUp) _ctrlDown = false;
                return;
            }
            if (IsShiftKey(vk))
            {
                if (isDown) _shiftDown = true; else if (isUp) _shiftDown = false;
                return;
            }
            if (IsWinKey(vk))
            {
                if (isDown) _winDown = true; else if (isUp) _winDown = false;
                return;
            }

            // CapsLock: when it is part of the shortcut, swallow it so the keyboard
            // LED / toggle state does not flip while it is held as a modifier.
            if (_wantCapsLock && IsCapsLockKey(vk, keyboardMessage.ScanCode))
            {
                if (isDown) _capsLockDown = true; else if (isUp) _capsLockDown = false;
                handled = true;
                return;
            }

            if (!isDown || vk != _vkCode)
                return;

            if (ModifiersMatch())
            {
                // Consume the shortcut so the OS doesn't also act on it (e.g. so
                // Alt+Tab does not open the built-in Windows switcher).
                handled = true;
                OnPressed();
            }
        }

        private bool ModifiersMatch()
        {
            // Exact match: every wanted modifier must be down and every unwanted
            // modifier must be up. CapsLock only matters when it is part of the combo.
            if (_altDown != _wantAlt) return false;
            if (_ctrlDown != _wantCtrl) return false;
            if (_shiftDown != _wantShift) return false;
            if (_winDown != _wantWin) return false;
            if (_wantCapsLock && !_capsLockDown) return false;
            return true;
        }

        private void OnPressed()
        {
            var handler = Pressed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private static bool IsAltKey(int vk)
        {
            return vk == 0x12 || vk == 0xA4 || vk == 0xA5; // VK_MENU, VK_LMENU, VK_RMENU
        }

        private static bool IsCtrlKey(int vk)
        {
            return vk == 0x11 || vk == 0xA2 || vk == 0xA3; // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        }

        private static bool IsShiftKey(int vk)
        {
            return vk == 0x10 || vk == 0xA0 || vk == 0xA1; // VK_SHIFT, VK_LSHIFT, VK_RSHIFT
        }

        private static bool IsWinKey(int vk)
        {
            return vk == 0x5B || vk == 0x5C; // VK_LWIN, VK_RWIN
        }

        private static bool IsCapsLockKey(int vk, int scanCode)
        {
            return vk == 0x14 || scanCode == 0x3A; // VK_CAPITAL or CapsLock scan code
        }

        public void Dispose()
        {
            if (_hook != null)
                _hook.Dispose();
        }
    }
}
