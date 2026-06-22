using System.Threading;
using System.Windows.Forms;
using ManagedWinapi;

namespace Switcheroo
{
    internal static class NativePreviousAppSwitcher
    {
        private static int _switchInProgress;

        public static bool IsSwitchInProgress
        {
            get { return Thread.VolatileRead(ref _switchInProgress) != 0; }
        }

        public static void SwitchToPreviousApp()
        {
            if (Interlocked.Exchange(ref _switchInProgress, 1) != 0)
                return;

            try
            {
                var altKey = new KeyboardKey(Keys.Menu);
                var tabKey = new KeyboardKey(Keys.Tab);
                var leftAltKey = new KeyboardKey(Keys.LMenu);
                var rightAltKey = new KeyboardKey(Keys.RMenu);
                var leftCtrlKey = new KeyboardKey(Keys.LControlKey);
                var rightCtrlKey = new KeyboardKey(Keys.RControlKey);
                var leftShiftKey = new KeyboardKey(Keys.LShiftKey);
                var rightShiftKey = new KeyboardKey(Keys.RShiftKey);
                var leftWinKey = new KeyboardKey(Keys.LWin);
                var rightWinKey = new KeyboardKey(Keys.RWin);
                var altAlreadyDown = IsKeyDown(leftAltKey) || IsKeyDown(rightAltKey);

                var leftCtrlDown = IsKeyDown(leftCtrlKey);
                var rightCtrlDown = IsKeyDown(rightCtrlKey);
                var leftShiftDown = IsKeyDown(leftShiftKey);
                var rightShiftDown = IsKeyDown(rightShiftKey);
                var leftWinDown = IsKeyDown(leftWinKey);
                var rightWinDown = IsKeyDown(rightWinKey);

                ReleaseIfDown(leftCtrlKey, leftCtrlDown);
                ReleaseIfDown(rightCtrlKey, rightCtrlDown);
                ReleaseIfDown(leftShiftKey, leftShiftDown);
                ReleaseIfDown(rightShiftKey, rightShiftDown);
                ReleaseIfDown(leftWinKey, leftWinDown);
                ReleaseIfDown(rightWinKey, rightWinDown);

                if (!altAlreadyDown)
                    altKey.Press();

                tabKey.Press();
                tabKey.Release();

                if (!altAlreadyDown)
                    altKey.Release();

                PressIfDown(leftCtrlKey, leftCtrlDown);
                PressIfDown(rightCtrlKey, rightCtrlDown);
                PressIfDown(leftShiftKey, leftShiftDown);
                PressIfDown(rightShiftKey, rightShiftDown);
                PressIfDown(leftWinKey, leftWinDown);
                PressIfDown(rightWinKey, rightWinDown);
            }
            finally
            {
                Interlocked.Exchange(ref _switchInProgress, 0);
            }
        }

        private static bool IsKeyDown(KeyboardKey key)
        {
            return (key.AsyncState & 0x8000) != 0;
        }

        private static void ReleaseIfDown(KeyboardKey key, bool isDown)
        {
            if (isDown)
                key.Release();
        }

        private static void PressIfDown(KeyboardKey key, bool wasDown)
        {
            if (wasDown)
                key.Press();
        }
    }
}
