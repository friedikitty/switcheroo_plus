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
        public void Validate_PreviousAppAltTabWithoutAltTabHook_ReturnsValid()
        {
            // The default configuration: switch-back on Alt+Tab while the
            // "Activate Switcheroo with Alt+Tab" option is off.
            var result = HotkeySelectionValidator.Validate(
                true,
                new HotkeySelection { Alt = true, KeyCode = 32 },
                false,
                true,
                AltTab());

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Validate_PreviousAppShortcutWithoutModifier_ReturnsInvalid()
        {
            // The switch-back hook swallows the keystroke, so a plain key
            // would break normal typing.
            var result = HotkeySelectionValidator.Validate(
                true,
                CtrlShift(69),
                false,
                true,
                new HotkeySelection { KeyCode = 81 });

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Message, Does.Contain("modifier"));
        }

        [Test]
        public void Validate_ActivationShortcutWithoutModifier_ReturnsInvalid()
        {
            var result = HotkeySelectionValidator.Validate(
                true,
                new HotkeySelection { KeyCode = 32 },
                false,
                false,
                null);

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Message, Does.Contain("modifier"));
        }

        [Test]
        public void Validate_PreviousAppCapsLockTab_ReturnsValid()
        {
            // CapsLock counts as a modifier for the switch-back shortcut.
            var result = HotkeySelectionValidator.Validate(
                false,
                null,
                false,
                true,
                new HotkeySelection { CapsLock = true, KeyCode = 9 });

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Validate_ConflictingShortcutWithPreviousAppDisabled_ReturnsValid()
        {
            // The switch-back hook is not installed when the feature is off,
            // so an identical shortcut is not a conflict.
            var result = HotkeySelectionValidator.Validate(
                true,
                CtrlShift(69),
                false,
                false,
                CtrlShift(69));

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Validate_PreviousAppShortcutSameAsDisabledActivation_ReturnsValid()
        {
            // The activation hotkey is not registered when its checkbox is off.
            var result = HotkeySelectionValidator.Validate(
                false,
                CtrlShift(69),
                false,
                true,
                CtrlShift(69));

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Validate_AltTabPreviousAppWithAltTabHookDisabledAndActivationAltTab_ReturnsInvalid()
        {
            // Activation on plain Alt+Tab stays rejected even when it would
            // also collide with the switch-back shortcut.
            var result = HotkeySelectionValidator.Validate(
                true,
                AltTab(),
                false,
                true,
                AltTab());

            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Message, Does.Contain("dedicated"));
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
