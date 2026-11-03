using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Murmur.App
{
    internal static class DesktopAccess
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int size, out int needed);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        internal static int IntegrityLevel()
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 8, out token)) throw new Win32Exception();
            IntPtr buffer = IntPtr.Zero;
            try
            {
                int needed;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out needed);
                if (needed <= 0) throw new Win32Exception();
                buffer = Marshal.AllocHGlobal(needed);
                if (!GetTokenInformation(token, 25, buffer, needed, out needed)) throw new Win32Exception();
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                CloseHandle(token);
            }
        }

        internal static void RequireDesktopAccess()
        {
            if (IntegrityLevel() < 0x2000)
                throw new InvalidOperationException("Windows launched Murmur at Low integrity, which blocks its tray icon and automatic paste. Run node scripts/install.mjs --launch from the project folder, then use the installed Murmur shortcut.");
        }
    }
}
