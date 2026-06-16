/*
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
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Switcheroo
{
    // Create a shortcut file in the current users start up folder
    // Based on this answer on Stackoverflow:
    // http://stackoverflow.com/a/19914018/198065
    public class AutoStart
    {
        public bool IsEnabled
        {
            get { return HasShortcut(); }

            set
            {
                var appLink = GetAppLinkPath();

                try
                {
                    if (value)
                    {
                        CreateShortcut(appLink);
                        MessageBox.Show(
                            "Switcheroo will start with Windows. A shortcut was placed in shell:startup.",
                            "Run on Startup Enabled",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else if (IsEnabled)
                    {
                        DeleteShortcut(appLink);
                        MessageBox.Show(
                            "Switcheroo will no longer start with Windows. The shortcut in shell:startup was removed.",
                            "Run on Startup Disabled",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
                catch (COMException ex)
                {
                    MessageBox.Show(
                        "Failed to update startup shortcut: " + ex.Message,
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch (UnauthorizedAccessException ex)
                {
                    MessageBox.Show(
                        "Permission denied while updating startup shortcut: " + ex.Message,
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "An error occurred while updating startup shortcut: " + ex.Message,
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private static bool HasShortcut()
        {
            try
            {
                return File.Exists(GetAppLinkPath());
            }
            catch
            {
                return false;
            }
        }

        private static string GetAppLinkPath()
        {
            var appDataStart =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Startup");
            var appLink = Path.Combine(appDataStart, "Switcheroo.lnk");
            return appLink;
        }

        private static void DeleteShortcut(string appLink)
        {
            File.Delete(appLink);
        }

        private static void CreateShortcut(string appLink)
        {
            var exeLocation = Process.GetCurrentProcess().MainModule.FileName;

            //Windows Script Host Shell Object
            var t = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
            dynamic shell = Activator.CreateInstance(t);
            try
            {
                var lnk = shell.CreateShortcut(appLink);
                try
                {
                    lnk.TargetPath = exeLocation;
                    lnk.Save();
                }
                finally
                {
                    Marshal.FinalReleaseComObject(lnk);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public class AutoStartException : Exception
    {
        public AutoStartException(string message)
            : base(message)
        {
        }
    }
}
