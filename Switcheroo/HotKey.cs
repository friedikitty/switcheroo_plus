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

namespace Switcheroo
{
    public class HotKey : ManagedWinapi.Hotkey
    {
        public void LoadSettings()
        {
            var cfg = ConfigManager.Load();
            KeyCode = (System.Windows.Forms.Keys)cfg.ActivateHotKey;
            WindowsKey = cfg.ActivateWindowsKey;
            Alt = cfg.ActivateAlt;
            Ctrl = cfg.ActivateCtrl;
            Shift = cfg.ActivateShift;
        }

        public void SaveSettings()
        {
            var cfg = ConfigManager.Load();
            cfg.ActivateHotKey = (int)KeyCode;
            cfg.ActivateWindowsKey = WindowsKey;
            cfg.ActivateAlt = Alt;
            cfg.ActivateCtrl = Ctrl;
            cfg.ActivateShift = Shift;
            ConfigManager.Save(cfg);
        }

        public void LoadPreviousAppSettings()
        {
            var cfg = ConfigManager.Load();
            KeyCode = (System.Windows.Forms.Keys)cfg.PreviousAppHotKey;
            WindowsKey = cfg.PreviousAppWindowsKey;
            Alt = cfg.PreviousAppAlt;
            Ctrl = cfg.PreviousAppCtrl;
            Shift = cfg.PreviousAppShift;
        }

        public void SavePreviousAppSettings()
        {
            var cfg = ConfigManager.Load();
            cfg.PreviousAppHotKey = (int)KeyCode;
            cfg.PreviousAppWindowsKey = WindowsKey;
            cfg.PreviousAppAlt = Alt;
            cfg.PreviousAppCtrl = Ctrl;
            cfg.PreviousAppShift = Shift;
            ConfigManager.Save(cfg);
        }
    }
}
