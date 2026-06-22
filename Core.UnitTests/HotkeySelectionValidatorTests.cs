using NUnit.Framework;
using Switcheroo;

namespace Switcheroo.Core.UnitTests
{
    [TestFixture]
    public class HotkeySelectionValidatorTests
    {
        [Test]
        public void Validate_ActivationShortcutIsAltTab_ReturnsInvalid()
        {
            var result = HotkeySelectionValidator.Validate(
                true,
                AltTab(),
                false,
                false,
                CtrlShift(69));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Message, Does.Contain("dedicated"));
        }

        [Test]
        public void Validate_ActivationAndPreviousAppShareShortcut_ReturnsInvalid()
        {
            var sameShortcut = CtrlShift(69);

            var result = HotkeySelectionValidator.Validate(
                true,
                sameShortcut,
                false,
                true,
                CtrlShift(69));

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Message, Does.Contain("cannot be the same"));
        }

        [Test]
        public void Validate_AltTabActivationConflictsWithAltTabPreviousApp_ReturnsInvalid()
        {
            var result = HotkeySelectionValidator.Validate(
                false,
                CtrlShift(69),
                true,
                true,
                AltTab());

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Message, Does.Contain("already assigned"));
        }

        [Test]
        public void Validate_DistinctShortcuts_ReturnsValid()
        {
            var result = HotkeySelectionValidator.Validate(
                true,
                CtrlShift(69),
                false,
                true,
                AltTab());

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Message, Is.Null);
        }

        [Test]
        public void DefaultConfig_EnablesPreviousAppWithAltTab()
        {
            var config = new SwitcherooConfig();

            Assert.That(config.PreviousAppEnabled, Is.True);
            Assert.That(config.PreviousAppAlt, Is.True);
            Assert.That(config.PreviousAppCtrl, Is.False);
            Assert.That(config.PreviousAppShift, Is.False);
            Assert.That(config.PreviousAppWindowsKey, Is.False);
            Assert.That(config.PreviousAppCapsLock, Is.False);
            Assert.That(config.PreviousAppHotKey, Is.EqualTo(9));
        }

        private static HotkeySelection AltTab()
        {
            return new HotkeySelection { Alt = true, KeyCode = 9 };
        }

        private static HotkeySelection CtrlShift(int keyCode)
        {
            return new HotkeySelection { Ctrl = true, Shift = true, KeyCode = keyCode };
        }
    }
}
