using System;
using System.Windows.Forms;
using ManagedWinapi.Hooks;

namespace CapsLockHookTest
{
    static class Program
    {
        private const int CapsLockScanCode = 0x3A;
        private const int VK_CAPITAL = 0x14;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private static LowLevelKeyboardHook s_hook;
        private static bool s_capsLockDown;
        private static int s_comboCount;
        private static Label s_statusLabel;
        private static ListBox s_logBox;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new Form
            {
                Text = "CapsLock+Tab Hook Prototype",
                Width = 500,
                Height = 350,
                StartPosition = FormStartPosition.CenterScreen
            };

            var instructions = new Label
            {
                Text = "Press CapsLock + Tab. The hook should suppress CapsLock toggle and detect the combo.",
                Dock = DockStyle.Top,
                Height = 40,
                Padding = new Padding(10)
            };

            s_statusLabel = new Label
            {
                Text = "Waiting for CapsLock+Tab...",
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(10),
                Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold)
            };

            s_logBox = new ListBox
            {
                Dock = DockStyle.Fill
            };

            form.Controls.Add(s_logBox);
            form.Controls.Add(s_statusLabel);
            form.Controls.Add(instructions);

            form.Load += (s, e) => StartHook();
            form.FormClosing += (s, e) => StopHook();

            Application.Run(form);
        }

        private static void StartHook()
        {
            s_capsLockDown = false;
            s_hook = new LowLevelKeyboardHook();
            s_hook.MessageIntercepted += OnMessageIntercepted;
            s_hook.StartHook();
            Log("Hook started.");
        }

        private static void StopHook()
        {
            if (s_hook != null)
            {
                s_hook.MessageIntercepted -= OnMessageIntercepted;
                s_hook.Dispose();
                s_hook = null;
                Log("Hook stopped.");
            }
        }

        private static void OnMessageIntercepted(LowLevelMessage lowLevelMessage, ref bool handled)
        {
            var keyboardMessage = lowLevelMessage as LowLevelKeyboardMessage;
            if (handled || keyboardMessage == null)
                return;

            var msg = keyboardMessage.Message;
            var vkCode = keyboardMessage.VirtualKeyCode;
            var scanCode = keyboardMessage.ScanCode;
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            // Detect CapsLock by scancode or vkCode.
            if (scanCode == CapsLockScanCode || vkCode == VK_CAPITAL)
            {
                if (isDown)
                {
                    s_capsLockDown = true;
                    handled = true; // Suppress CapsLock toggle
                    Log("CapsLock DOWN (suppressed)");
                }
                else if (isUp)
                {
                    s_capsLockDown = false;
                    handled = true; // Suppress CapsLock toggle
                    Log("CapsLock UP (suppressed)");
                }
                return;
            }

            // If CapsLock is held and Tab is pressed, fire the combo.
            if (s_capsLockDown && vkCode == (int)Keys.Tab && isDown)
            {
                handled = true;
                s_comboCount++;
                Log(string.Format("*** CapsLock+Tab detected (#{0}) ***", s_comboCount));
                s_statusLabel.Invoke(new Action(() =>
                {
                    s_statusLabel.Text = string.Format("CapsLock+Tab detected {0} time(s).", s_comboCount);
                }));
            }
        }

        private static void Log(string message)
        {
            string entry = string.Format("[{0:HH:mm:ss.fff}] {1}", DateTime.Now, message);
            if (s_logBox != null && s_logBox.IsHandleCreated)
            {
                s_logBox.Invoke(new Action(() =>
                {
                    s_logBox.Items.Add(entry);
                    s_logBox.TopIndex = s_logBox.Items.Count - 1;
                }));
            }
        }
    }
}
