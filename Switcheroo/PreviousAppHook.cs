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
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using ManagedWinapi.Hooks;

namespace Switcheroo
{
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
        private bool _capsLockDetected;
        private DispatcherTimer _detectionTimer;
        private bool _detectionComplete;

        public PreviousAppHook()
        {
            _hook = new LowLevelKeyboardHook();
            _hook.MessageIntercepted += OnMessageIntercepted;
        }

        public void StartHook()
        {
            _capsLockDown = false;
            _capsLockDetected = false;
            _detectionComplete = false;
            _hook.StartHook();

            // Start a detection timer: if CapsLock is never observed within 5 seconds
            // of the first keyboard event, assume it's stripped by Scancode Map.
            _detectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _detectionTimer.Tick += OnDetectionTimeout;
            _detectionTimer.Start();
        }

        private void OnDetectionTimeout(object sender, EventArgs e)
        {
            _detectionTimer.Stop();
            _detectionTimer = null;
            _detectionComplete = true;

            if (!_capsLockDetected)
            {
                // CapsLock key was never observed — likely stripped by Scancode Map
                System.Windows.MessageBox.Show(
                    "CapsLock key events are not visible to Switcheroo. This usually happens when CapsLock " +
                    "is disabled via a registry Scancode Map.\n\n" +
                    "The CapsLock+Tab previous-app shortcut cannot work in this configuration. " +
                    "Please choose a different shortcut key in Options.",
                    "CapsLock Not Detected",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Dispose();
            }
        }

        private void OnMessageIntercepted(LowLevelMessage lowLevelMessage, ref bool handled)
        {
            var keyboardMessage = lowLevelMessage as LowLevelKeyboardMessage;
            if (handled || keyboardMessage == null)
                return;

            var msg = keyboardMessage.Message;
            var vkCode = keyboardMessage.VirtualKeyCode;
            var scanCode = keyboardMessage.ScanCode;

            // Track CapsLock key state by scancode
            if (scanCode == CapsLockScanCode || vkCode == VK_CAPITAL)
            {
                if (!_capsLockDetected)
                    _capsLockDetected = true;

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    _capsLockDown = true;
                    // Suppress the CapsLock keydown to prevent toggling CapsLock state
                    handled = true;
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _capsLockDown = false;
                    // Suppress the CapsLock keyup as well
                    handled = true;
                }
                return;
            }

            // If CapsLock is held and Tab is pressed, fire the event
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
            if (_detectionTimer != null)
            {
                _detectionTimer.Stop();
                _detectionTimer = null;
            }
            if (_hook != null)
                _hook.Dispose();
        }
    }
}
