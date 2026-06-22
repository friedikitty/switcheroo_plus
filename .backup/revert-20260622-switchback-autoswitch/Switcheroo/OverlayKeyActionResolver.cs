using System.Windows.Input;

namespace Switcheroo
{
    public enum OverlayKeyActionType
    {
        None,
        FocusSearch,
        PreviousItem,
        NextItem,
        SwitchToIndex
    }

    public class OverlayKeyAction
    {
        public static readonly OverlayKeyAction None = new OverlayKeyAction(OverlayKeyActionType.None, -1);

        private OverlayKeyAction(OverlayKeyActionType actionType, int index)
        {
            ActionType = actionType;
            Index = index;
        }

        public OverlayKeyActionType ActionType { get; private set; }

        public int Index { get; private set; }

        public static OverlayKeyAction FocusSearch()
        {
            return new OverlayKeyAction(OverlayKeyActionType.FocusSearch, -1);
        }

        public static OverlayKeyAction PreviousItem()
        {
            return new OverlayKeyAction(OverlayKeyActionType.PreviousItem, -1);
        }

        public static OverlayKeyAction NextItem()
        {
            return new OverlayKeyAction(OverlayKeyActionType.NextItem, -1);
        }

        public static OverlayKeyAction SwitchToIndex(int index)
        {
            return new OverlayKeyAction(OverlayKeyActionType.SwitchToIndex, index);
        }
    }

    public static class OverlayKeyActionResolver
    {
        public static OverlayKeyAction Resolve(
            Key key,
            Key systemKey,
            ModifierKeys modifiers,
            bool isSearchBoxFocused,
            bool autoSwitchEnabled,
            int itemCount)
        {
            if (isSearchBoxFocused || itemCount <= 0)
                return OverlayKeyAction.None;

            if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                return OverlayKeyAction.None;

            var normalizedKey = key == Key.System ? systemKey : key;
            var altHeld = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            var shiftHeld = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (normalizedKey == Key.Up)
                return OverlayKeyAction.PreviousItem();

            if (normalizedKey == Key.Down)
                return OverlayKeyAction.NextItem();

            int digitIndex = GetDigitIndex(normalizedKey);
            if (digitIndex >= 0 && digitIndex < itemCount)
                return OverlayKeyAction.SwitchToIndex(digitIndex);

            int letterIndex = GetLetterIndex(normalizedKey, shiftHeld, altHeld, autoSwitchEnabled);
            if (letterIndex >= 0 && letterIndex < itemCount)
                return OverlayKeyAction.SwitchToIndex(letterIndex);

            if (!altHeld && (normalizedKey == Key.Oem2 || normalizedKey == Key.Divide || normalizedKey == Key.OemQuestion))
                return OverlayKeyAction.FocusSearch();

            return OverlayKeyAction.None;
        }

        private static int GetDigitIndex(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
                return key - Key.D0;

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return key - Key.NumPad0;

            return -1;
        }

        private static int GetLetterIndex(Key key, bool shiftHeld, bool altHeld, bool autoSwitchEnabled)
        {
            if (key < Key.A || key > Key.Z)
                return -1;

            if (autoSwitchEnabled && altHeld)
                return 10 + (key - Key.A);

            if (shiftHeld)
                return 10 + 26 + (key - Key.A);

            return 10 + (key - Key.A);
        }
    }
}
