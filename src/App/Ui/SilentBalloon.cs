using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Murmur.App.Ui
{
    internal static class SilentBalloon
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NotificationData
        {
            internal uint Size; internal IntPtr Window; internal uint Id, Flags, Callback;
            internal IntPtr Icon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string Tip;
            internal uint State, StateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Info;
            internal uint Timeout;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string Title;
            internal uint InfoFlags; internal Guid Guid; internal IntPtr Balloon;
        }

        internal delegate bool Sender(uint command, ref NotificationData data);

        [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellNotifyIcon(uint command, ref NotificationData data);

        internal static bool Show(NotifyIcon owner, string title, string text)
        {
            try
            {
                // .NET Framework NotifyIcon does not expose NIIF_NOSOUND. Reuse
                // its existing window/id; never add a second notification icon.
                BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                FieldInfo windowField = typeof(NotifyIcon).GetField("window", flags);
                FieldInfo idField = typeof(NotifyIcon).GetField("id", flags);
                if (windowField == null || idField == null) return false;
                NativeWindow window = windowField.GetValue(owner) as NativeWindow;
                if (window == null || window.Handle == IntPtr.Zero) return false;
                return Show(window.Handle, Convert.ToUInt32(idField.GetValue(owner)), title, text, ShellNotifyIcon);
            }
            catch (Exception) { return false; } // No fallback to an audible balloon.
        }

        internal static bool Show(IntPtr window, uint id, string title, string text, Sender sender)
        {
            if (window == IntPtr.Zero || sender == null) return false;
            NotificationData data = new NotificationData();
            data.Size = (uint)Marshal.SizeOf(typeof(NotificationData));
            data.Window = window; data.Id = id;
            data.Flags = 0x10; // NIF_INFO
            data.InfoFlags = 0x10; // NIIF_NOSOUND
            data.Title = Limit(String.IsNullOrEmpty(title) ? "Murmur" : title, 63);
            data.Info = Limit(String.IsNullOrEmpty(text) ? data.Title : text, 255);
            return sender(1, ref data); // NIM_MODIFY on the existing icon
        }

        private static string Limit(string text, int length)
        {
            if (text.Length <= length) return text;
            if (Char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length);
        }
    }
}
