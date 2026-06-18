/**
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
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using ManagedWinapi;
using Switcheroo.Core;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace Switcheroo
{
    public partial class OptionsWindow : Window
    {
        private readonly HotKey _hotkey;
        private HotkeyViewModel _hotkeyViewModel;
        private HotkeyViewModel _previousAppViewModel;
        private SwitcherooConfig _config;

        public OptionsWindow()
        {
            InitializeComponent();

            _config = ConfigManager.Load();
            RunOnStartup.IsChecked = _config.RunOnStartup || new AutoStart().IsEnabled;

            // Show what's already selected     
            _hotkey = (HotKey) Application.Current.Properties["hotkey"];

            try
            {
                _hotkey.LoadSettings();
            }
            catch (HotkeyAlreadyInUseException)
            {
            }

            _hotkeyViewModel = new HotkeyViewModel
            {
                KeyCode = KeyInterop.KeyFromVirtualKey((int) _hotkey.KeyCode),
                Alt = _hotkey.Alt,
                Ctrl = _hotkey.Ctrl,
                Windows = _hotkey.WindowsKey,
                Shift = _hotkey.Shift
            };

            _previousAppViewModel = new HotkeyViewModel
            {
                KeyCode = KeyInterop.KeyFromVirtualKey(_config.PreviousAppHotKey),
                Alt = _config.PreviousAppAlt,
                Ctrl = _config.PreviousAppCtrl,
                Windows = _config.PreviousAppWindowsKey,
                Shift = _config.PreviousAppShift,
                CapsLock = _config.PreviousAppCapsLock
            };

            HotKeyCheckBox.IsChecked = _config.EnableHotKey;
            HotkeyPreview.Text = _hotkeyViewModel.ToString();
            HotkeyPreview.IsEnabled = _config.EnableHotKey;
            AltTabCheckBox.IsChecked = _config.AltTabHook;
            AutoSwitch.IsChecked = _config.AutoSwitch;
            AutoSwitch.IsEnabled = _config.AltTabHook;
            AzShortcutsCheckBox.IsChecked = _config.AzShortcutsEnabled;
            OpenUsageHintsByDefaultCheckBox.IsChecked = _config.OpenUsageHintsByDefault;
            MultiColumnCheckBox.IsChecked = _config.MultiColumnEnabled;
            PreviousAppCheckBox.IsChecked = _config.PreviousAppEnabled;
            PreviousAppPreview.Text = _previousAppViewModel.ToString();
            PreviousAppPreview.IsEnabled = _config.PreviousAppEnabled;
            RunAsAdministrator.IsChecked = _config.RunAsAdmin;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var closeOptionsWindow = true;

            try
            {
                _hotkey.Enabled = false;

                if (_config.EnableHotKey)
                {
                    // Change the active hotkey
                    _hotkey.Alt = _hotkeyViewModel.Alt;
                    _hotkey.Shift = _hotkeyViewModel.Shift;
                    _hotkey.Ctrl = _hotkeyViewModel.Ctrl;
                    _hotkey.WindowsKey = _hotkeyViewModel.Windows;
                    _hotkey.KeyCode = (Keys) KeyInterop.VirtualKeyFromKey(_hotkeyViewModel.KeyCode);
                    _hotkey.Enabled = true;
                }
                // NOTE: do not call _hotkey.SaveSettings() here. It reloaded the
                // config from disk, wrote the Activate* fields, and saved; the very
                // next ConfigManager.Save(_config) below then overwrote the file with
                // the stale in-memory _config, reverting the activation hotkey to
                // Alt+Space on the next launch. The Activate* fields are persisted
                // together with the rest of _config below.
            }
            catch (HotkeyAlreadyInUseException)
            {
                var boxText = "Sorry! The selected shortcut for activating Switcheroo is in use by another program. " +
                              "Please choose another.";
                MessageBox.Show(boxText, "Shortcut already in use", MessageBoxButton.OK, MessageBoxImage.Warning);
                closeOptionsWindow = false;
            }

            _config.EnableHotKey = HotKeyCheckBox.IsChecked.GetValueOrDefault();
            // Persist the activation hotkey from the editor view model into the
            // JSONC config (the single source of truth used at startup).
            _config.ActivateAlt = _hotkeyViewModel.Alt;
            _config.ActivateCtrl = _hotkeyViewModel.Ctrl;
            _config.ActivateShift = _hotkeyViewModel.Shift;
            _config.ActivateWindowsKey = _hotkeyViewModel.Windows;
            _config.ActivateHotKey = KeyInterop.VirtualKeyFromKey(_hotkeyViewModel.KeyCode);
            _config.AltTabHook = AltTabCheckBox.IsChecked.GetValueOrDefault();
            _config.AutoSwitch = AutoSwitch.IsChecked.GetValueOrDefault();
            _config.AzShortcutsEnabled = AzShortcutsCheckBox.IsChecked.GetValueOrDefault();
            _config.PreviousAppEnabled = PreviousAppCheckBox.IsChecked.GetValueOrDefault();
            _config.PreviousAppAlt = _previousAppViewModel.Alt;
            _config.PreviousAppCtrl = _previousAppViewModel.Ctrl;
            _config.PreviousAppShift = _previousAppViewModel.Shift;
            _config.PreviousAppWindowsKey = _previousAppViewModel.Windows;
            _config.PreviousAppCapsLock = _previousAppViewModel.CapsLock;
            _config.PreviousAppHotKey = KeyInterop.VirtualKeyFromKey(_previousAppViewModel.KeyCode);
            _config.RunAsAdmin = RunAsAdministrator.IsChecked.GetValueOrDefault();
            _config.RunOnStartup = RunOnStartup.IsChecked.GetValueOrDefault();
            _config.OpenUsageHintsByDefault = OpenUsageHintsByDefaultCheckBox.IsChecked.GetValueOrDefault();
            _config.MultiColumnEnabled = MultiColumnCheckBox.IsChecked.GetValueOrDefault();
            try
            {
                var autoStart = new AutoStart { IsEnabled = _config.RunOnStartup };
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Failed to update startup shortcut: " + ex.Message,
                    "Run on Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            ConfigManager.Save(_config);

            if (closeOptionsWindow)
            {
                Close();
            }
        }

        private void HotkeyPreview_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = (e.Key == Key.System ? e.SystemKey : e.Key);
            if (IsModifierKey(key)) return;

            _hotkeyViewModel = BuildViewModelFromKeyEvent(key);
            var previewText = _hotkeyViewModel.ToString();

            if (previewText == "Tab")
            {
                ((UIElement) sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            }

            HotkeyPreview.Text = previewText;
        }

        private void PreviousAppPreview_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            var key = (e.Key == Key.System ? e.SystemKey : e.Key);
            if (IsModifierKey(key)) return;

            _previousAppViewModel = BuildViewModelFromKeyEvent(key);
            var previewText = _previousAppViewModel.ToString();

            // Plain Tab moves focus; CapsLock+Tab is a valid shortcut.
            if (previewText == "Tab" && !_previousAppViewModel.CapsLock)
            {
                ((UIElement) sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                return;
            }

            PreviousAppPreview.Text = previewText;
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftShift || key == Key.RightShift
                || key == Key.LeftCtrl || key == Key.RightCtrl
                || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LWin || key == Key.RWin;
        }

        private static HotkeyViewModel BuildViewModelFromKeyEvent(Key key)
        {
            var vm = new HotkeyViewModel();
            vm.Ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            vm.Shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            vm.Alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            var winLKey = new KeyboardKey(Keys.LWin);
            var winRKey = new KeyboardKey(Keys.RWin);
            vm.Windows = (winLKey.State & 0x8000) == 0x8000 || (winRKey.State & 0x8000) == 0x8000;

            var capsLockKey = new KeyboardKey(Keys.Capital);
            vm.CapsLock = (capsLockKey.State & 0x8000) == 0x8000;

            vm.KeyCode = key;
            return vm;
        }

        private class HotkeyViewModel
        {
            public Key KeyCode { get; set; }
            public bool Shift { get; set; }
            public bool Alt { get; set; }
            public bool Ctrl { get; set; }
            public bool Windows { get; set; }
            public bool CapsLock { get; set; }

            public override string ToString()
            {
                var shortcutText = new StringBuilder();

                if (CapsLock)
                {
                    shortcutText.Append("CapsLock + ");
                }

                if (Ctrl)
                {
                    shortcutText.Append("Ctrl + ");
                }

                if (Shift)
                {
                    shortcutText.Append("Shift + ");
                }

                if (Alt)
                {
                    shortcutText.Append("Alt + ");
                }

                if (Windows)
                {
                    shortcutText.Append("Win + ");
                }

                var keyString =
                    KeyboardHelper.CodeToString((uint) KeyInterop.VirtualKeyFromKey(KeyCode)).ToUpper().Trim();
                if (keyString.Length == 0)
                {
                    keyString = new KeysConverter().ConvertToString(KeyCode);
                }

                // If the user presses "Escape" then show "Escape" :)
                if (keyString == "\u001B")
                {
                    keyString = "Escape";
                }

                shortcutText.Append(keyString);
                return shortcutText.ToString();
            }
        }

        private void HotkeyPreview_OnGotFocus(object sender, RoutedEventArgs e)
        {
            // Disable the current hotkey while the hotkey field is active
            _hotkey.Enabled = false;
        }

        private void HotkeyPreview_OnLostFocus(object sender, RoutedEventArgs e)
        {
            try
            {
                _hotkey.Enabled = true;
            }
            catch (HotkeyAlreadyInUseException)
            {
                // It is alright if the hotkey can't be reactivated
            }
        }

        private void PreviousAppPreview_OnGotFocus(object sender, RoutedEventArgs e)
        {
            // No need to disable main hotkey here
        }

        private void PreviousAppPreview_OnLostFocus(object sender, RoutedEventArgs e)
        {
        }

        private void AltTabCheckBox_OnChecked(object sender, RoutedEventArgs e)
        {
            AutoSwitch.IsEnabled = true;
        }

        private void AltTabCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
        {
            AutoSwitch.IsEnabled = false;
            AutoSwitch.IsChecked = false;
        }

        private void HotKeyCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            HotkeyPreview.IsEnabled = true;
        }

        private void HotKeyCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
        {
            HotkeyPreview.IsEnabled = false;
        }

        private void PreviousAppCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            PreviousAppPreview.IsEnabled = true;
        }

        private void PreviousAppCheckBox_OnUnchecked(object sender, RoutedEventArgs e)
        {
            PreviousAppPreview.IsEnabled = false;
        }
    }
}
