using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Switcheroo;

namespace Switcheroo.Core.UnitTests
{
    [TestFixture]
    public class GlobalHotkeyProbeTests
    {
        private const int TestHotkeyId = 0x1234;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint VK_F23 = 0x86;
        private const uint VK_F24 = 0x87;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [Test]
        public void Check_HotkeyAlreadyRegistered_ReturnsOccupied()
        {
            // Occupy Ctrl+Shift+F24; the probe must report the same combination
            // as occupied even though this process holds it.
            Assert.That(RegisterHotKey(IntPtr.Zero, TestHotkeyId, MOD_CONTROL | MOD_SHIFT, VK_F24), Is.True,
                "Could not register the test hotkey");
            try
            {
                var result = GlobalHotkeyProbe.Check(new HotkeySelection { Ctrl = true, Shift = true, KeyCode = (int) VK_F24 });

                Assert.That(result, Is.EqualTo(HotkeyAvailability.Occupied));
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, TestHotkeyId);
            }
        }

        [Test]
        public void Check_UnclaimedHotkey_ReturnsAvailable()
        {
            // Ctrl+Shift+F23 is not claimed by Windows or common applications.
            var result = GlobalHotkeyProbe.Check(new HotkeySelection { Ctrl = true, Shift = true, KeyCode = (int) VK_F23 });

            Assert.That(result, Is.EqualTo(HotkeyAvailability.Available));
        }

        [Test]
        public void Check_CapsLockShortcut_ReturnsNotProbed()
        {
            // CapsLock is not a RegisterHotKey modifier, so it cannot be probed.
            var result = GlobalHotkeyProbe.Check(new HotkeySelection { CapsLock = true, KeyCode = 9 });

            Assert.That(result, Is.EqualTo(HotkeyAvailability.NotProbed));
        }

        [Test]
        public void Check_NullSelection_ReturnsNotProbed()
        {
            Assert.That(GlobalHotkeyProbe.Check(null), Is.EqualTo(HotkeyAvailability.NotProbed));
        }
    }
}
