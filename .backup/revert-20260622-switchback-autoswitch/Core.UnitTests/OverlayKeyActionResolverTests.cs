using NUnit.Framework;
using System.Windows.Input;
using Switcheroo;

namespace Switcheroo.Core.UnitTests
{
    [TestFixture]
    public class OverlayKeyActionResolverTests
    {
        [Test]
        public void Resolve_AutoSwitchAltDownArrow_UsesWindowNavigation()
        {
            var action = OverlayKeyActionResolver.Resolve(
                Key.System,
                Key.Down,
                ModifierKeys.Alt,
                false,
                true,
                5);

            Assert.That(action.ActionType, Is.EqualTo(OverlayKeyActionType.NextItem));
        }

        [Test]
        public void Resolve_AutoSwitchAltDigit_UsesDirectIndex()
        {
            var action = OverlayKeyActionResolver.Resolve(
                Key.System,
                Key.D1,
                ModifierKeys.Alt,
                false,
                true,
                5);

            Assert.That(action.ActionType, Is.EqualTo(OverlayKeyActionType.SwitchToIndex));
            Assert.That(action.Index, Is.EqualTo(1));
        }

        [Test]
        public void Resolve_AutoSwitchAltLetter_UsesLowercaseShortcutIndex()
        {
            var action = OverlayKeyActionResolver.Resolve(
                Key.System,
                Key.A,
                ModifierKeys.Alt,
                false,
                true,
                20);

            Assert.That(action.ActionType, Is.EqualTo(OverlayKeyActionType.SwitchToIndex));
            Assert.That(action.Index, Is.EqualTo(10));
        }

        [Test]
        public void Resolve_SearchBoxFocused_DoesNotStealInput()
        {
            var action = OverlayKeyActionResolver.Resolve(
                Key.A,
                Key.None,
                ModifierKeys.None,
                true,
                false,
                20);

            Assert.That(action.ActionType, Is.EqualTo(OverlayKeyActionType.None));
        }
    }
}
