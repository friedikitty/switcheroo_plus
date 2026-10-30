using System;

namespace Switcheroo
{
    public sealed class HotkeySelection : IEquatable<HotkeySelection>
    {
        public bool Alt { get; set; }
        public bool Ctrl { get; set; }
        public bool Shift { get; set; }
        public bool WindowsKey { get; set; }
        public bool CapsLock { get; set; }
        public int KeyCode { get; set; }

        public bool IsAltTab
        {
            get
            {
                return Alt && !Ctrl && !Shift && !WindowsKey && !CapsLock && KeyCode == 9;
            }
        }

        public bool HasModifier
        {
            get { return Alt || Ctrl || Shift || WindowsKey || CapsLock; }
        }

        public bool Equals(HotkeySelection other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;

            return Alt == other.Alt
                   && Ctrl == other.Ctrl
                   && Shift == other.Shift
                   && WindowsKey == other.WindowsKey
                   && CapsLock == other.CapsLock
                   && KeyCode == other.KeyCode;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as HotkeySelection);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = Alt.GetHashCode();
                hashCode = (hashCode * 397) ^ Ctrl.GetHashCode();
                hashCode = (hashCode * 397) ^ Shift.GetHashCode();
                hashCode = (hashCode * 397) ^ WindowsKey.GetHashCode();
                hashCode = (hashCode * 397) ^ CapsLock.GetHashCode();
                hashCode = (hashCode * 397) ^ KeyCode;
                return hashCode;
            }
        }
    }

    public sealed class HotkeyValidationResult
    {
        public static readonly HotkeyValidationResult Valid = new HotkeyValidationResult(true, null);

        public HotkeyValidationResult(bool isValid, string message)
        {
            IsValid = isValid;
            Message = message;
        }

        public bool IsValid { get; private set; }
        public string Message { get; private set; }
    }

    public static class HotkeySelectionValidator
    {
        public static HotkeyValidationResult Validate(
            bool activateShortcutEnabled,
            HotkeySelection activateShortcut,
            bool activateWithAltTab,
            bool previousAppEnabled,
            HotkeySelection previousAppShortcut)
        {
            if (activateShortcutEnabled && activateShortcut != null && activateShortcut.IsAltTab)
            {
                return new HotkeyValidationResult(
                    false,
                    "Alt+Tab is reserved for the dedicated \"Activate Switcheroo with Alt+Tab\" option. " +
                    "Use that checkbox instead of assigning Alt+Tab as the regular activation shortcut.");
            }

            if (activateShortcutEnabled && previousAppEnabled
                && activateShortcut != null
                && previousAppShortcut != null
                && activateShortcut.Equals(previousAppShortcut))
            {
                return new HotkeyValidationResult(
                    false,
                    "The activation shortcut and the switch-back shortcut cannot be the same. Choose a different shortcut for one of them.");
            }

            if (activateWithAltTab && previousAppEnabled
                && previousAppShortcut != null
                && previousAppShortcut.IsAltTab)
            {
                return new HotkeyValidationResult(
                    false,
                    "Alt+Tab is already assigned to activating Switcheroo. Choose another switch-back shortcut or turn off \"Activate Switcheroo with Alt+Tab\".");
            }

            if (activateShortcutEnabled && activateShortcut != null && !activateShortcut.HasModifier)
            {
                return new HotkeyValidationResult(
                    false,
                    "The activation shortcut needs at least one modifier (Alt, Ctrl, Shift or Win). " +
                    "A plain key would be captured every time you press it.");
            }

            if (previousAppEnabled && previousAppShortcut != null && !previousAppShortcut.HasModifier)
            {
                return new HotkeyValidationResult(
                    false,
                    "The switch-back shortcut needs at least one modifier (Alt, Ctrl, Shift, Win or CapsLock). " +
                    "A plain key would be swallowed every time you press it, breaking normal typing.");
            }

            return HotkeyValidationResult.Valid;
        }
    }
}
