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
using System.Windows.Forms;
using ManagedWinapi.Hooks;

namespace Switcheroo
{
    /// <summary>
    /// Low-level keyboard hook that turns CapsLock+Tab into a previous-app toggle.
    /// </summary>
    /// <remarks>
    /// CapsLock is used as a modifier: its key events are swallowed so the CapsLock
    /// LED/state does not toggle, and a press of Tab while CapsLock is held fires
    /// the <see cref="Pressed"/> event.
    ///
    /// Note: if CapsLock has been remapped or disabled via a registry Scancode Map,
    /// the low-level hook will never see it and this shortcut cannot work.
    /// </remarks>
    public class PreviousAppHook : IDisposable
    {
        public event EventHandler Pressed;

        private const int CapsLockScanCode = 0x3A;
        private const int VK_CAPITAL = 0x14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private readonly LowLevelKeyboardHook _hook;
        private bool _capsLockDown;

        public PreviousAppHook()
        {
            _hook = new LowLevelKeyboardHook();
            _hook.MessageIntercepted += OnMessageIntercepted;
        }

        public void StartHook()
        {
            _capsLockDown = false;
            _hook.StartHook();
        }

        private void OnMessageIntercepted(LowLevelMessage lowLevelMessage, ref bool handled)
        {
            var keyboardMessage = lowLevelMessage as LowLevelKeyboardMessage;
            if (handled || keyboardMessage == null)
                return;

            var msg = keyboardMessage.Message;
            var vkCode = keyboardMessage.VirtualKeyCode;
            var scanCode = keyboardMessage.ScanCode;

            // Recognize CapsLock by scan code or virtual key code, then swallow it
            // so it does not toggle the keyboard LEDs/state.
            if (scanCode == CapsLockScanCode || vkCode == VK_CAPITAL)
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    _capsLockDown = true;
                    handled = true;
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _capsLockDown = false;
                    handled = true;
                }
                return;
            }

            // CapsLock + Tab triggers the previous-app switch.
            if (_capsLockDown && vkCode == (int)Keys.Tab &&
                (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
            {
                handled = true;
                OnPressed();
            }
        }

        private void OnPressed()
        {
            var handler = Pressed;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_hook != null)
                _hook.Dispose();
        }
    }
}
