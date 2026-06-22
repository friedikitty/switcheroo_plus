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
using ManagedWinapi;
using ManagedWinapi.Hooks;

namespace Switcheroo
{
    /// <summary>
    /// Low-level keyboard hook that turns an arbitrary modifier+key combination
    /// into a previous-app toggle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This mirrors the proven approach used by <see cref="AltTabHook"/>: a
    /// <c>WH_KEYBOARD_LL</c> hook that watches for the target key going down and
    /// reads modifier state through <see cref="KeyboardKey.AsyncState"/>
    /// (<c>GetAsyncKeyState</c>) at that moment. Polling the global key state is
    /// reliable regardless of which side (left/right) of a modifier the user
    /// pressed and regardless of the order in which keys were reported to the
    /// hook. Tracking modifiers from the message stream (a previous attempt) is
    /// fragile and is intentionally NOT used here.
    /// </para>
    /// <para>
    /// <c>RegisterHotKey</c> cannot express CapsLock as a modifier and silently
    /// fails for combos Windows owns (such as Alt+Tab), so the previous-app
    /// feature always goes through this hook.
    /// </para>
    /// <para>
    /// When the target key goes down with exactly the required modifiers held,
    /// <see cref="Pressed"/> fires and the keystroke is swallowed so the OS does
    /// not also act on it. CapsLock, when it is part of the combo, is swallowed
    /// on every event so the keyboard LED / toggle state does not flip; note that
    /// if CapsLock has been removed via a registry Scancode Map the hook will
    /// never observe it and CapsLock-based combos cannot work.
    /// </para>
    /// </remarks>
    public class PreviousAppHook : IDisposable
    {
        public event EventHandler Pressed;

        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int LLKHF_INJECTED = 0x10;
        private const int VK_CAPITAL = 0x14;
        private const int CapsLockScanCode = 0x3A;

        private readonly LowLevelKeyboardHook _hook;

        // Modifier probes (global async state). Same pattern as AltTabHook.
        private readonly KeyboardKey _lAltKey = new KeyboardKey(Keys.LMenu);
        private readonly KeyboardKey _rAltKey = new KeyboardKey(Keys.RMenu);
        private readonly KeyboardKey _lCtrlKey = new KeyboardKey(Keys.LControlKey);
        private readonly KeyboardKey _rCtrlKey = new KeyboardKey(Keys.RControlKey);
        private readonly KeyboardKey _lShiftKey = new KeyboardKey(Keys.LShiftKey);
        private readonly KeyboardKey _rShiftKey = new KeyboardKey(Keys.RShiftKey);
        private readonly KeyboardKey _lWinKey = new KeyboardKey(Keys.LWin);
        private readonly KeyboardKey _rWinKey = new KeyboardKey(Keys.RWin);
        private readonly KeyboardKey _capsKey = new KeyboardKey(Keys.Capital);

        private readonly bool _wantAlt;
        private readonly bool _wantCtrl;
        private readonly bool _wantShift;
        private readonly bool _wantWin;
        private readonly bool _wantCapsLock;
        private readonly int _vkCode;

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
            _hook.StartHook();
            IsHookStarted = true;
        }

        /// <summary>Exposed for diagnostics / automated tests.</summary>
        public bool IsHookStarted { get; private set; }

        private void OnMessageIntercepted(LowLevelMessage lowLevelMessage, ref bool handled)
        {
            var keyboardMessage = lowLevelMessage as LowLevelKeyboardMessage;
            if (handled || keyboardMessage == null)
                return;

            if (NativePreviousAppSwitcher.IsSwitchInProgress &&
                (keyboardMessage.Flags & LLKHF_INJECTED) == LLKHF_INJECTED)
            {
                return;
            }

            // When CapsLock is part of the shortcut, swallow it on every event so
            // the keyboard LED / toggle state does not flip while it is held.
            // (If CapsLock is stripped by a registry Scancode Map this branch is
            // simply never reached and CapsLock combos will not work.)
            if (_wantCapsLock && (keyboardMessage.VirtualKeyCode == VK_CAPITAL ||
                                  keyboardMessage.ScanCode == CapsLockScanCode))
            {
                handled = true;
                return;
            }

            // Only evaluate on the target key going DOWN.
            if (keyboardMessage.VirtualKeyCode != _vkCode)
                return;
            if (keyboardMessage.Message != WM_KEYDOWN && keyboardMessage.Message != WM_SYSKEYDOWN)
                return;

            if (ModifiersMatch())
            {
                // Consume the keystroke so the OS does not also act on it (e.g. so
                // Ctrl+Shift+E is not delivered to the focused application).
                handled = true;
                OnPressed();
            }
        }

        private bool ModifiersMatch()
        {
            // Exact match: every wanted modifier must currently be down and every
            // unwanted modifier must be up. Both sides of each modifier are checked.
            if (IsDown(_lAltKey, _rAltKey) != _wantAlt) return false;
            if (IsDown(_lCtrlKey, _rCtrlKey) != _wantCtrl) return false;
            if (IsDown(_lShiftKey, _rShiftKey) != _wantShift) return false;
            if (IsDown(_lWinKey, _rWinKey) != _wantWin) return false;
            if (_wantCapsLock && (IsDown(_capsKey, null) == false)) return false;
            return true;
        }

        private static bool IsDown(KeyboardKey primary, KeyboardKey alternate)
        {
            if ((primary.AsyncState & 0x8000) != 0) return true;
            if (alternate != null && (alternate.AsyncState & 0x8000) != 0) return true;
            return false;
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
