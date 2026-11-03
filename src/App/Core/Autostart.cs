using System;
using Microsoft.Win32;

namespace Murmur.App.Core
{
    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Murmur";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    object value = key.GetValue(ValueName);
                    return value != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Set(bool enabled, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null)
                    {
                        return;
                    }
                    if (enabled)
                    {
                        string command = CommandFor(exePath);
                        key.SetValue(ValueName, command, RegistryValueKind.String);
                    }
                    else
                    {
                        try
                        {
                            key.DeleteValue(ValueName, false);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public static string CommandFor(string exePath)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                return "\"\"";
            }
            return "\"" + exePath + "\" --background";
        }
    }
}
