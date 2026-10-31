using System;
using System.Runtime.InteropServices;

namespace Murmur.App.Core
{
    public static class AutoPaste
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUT
        {
            [FieldOffset(0)]
            public uint type;
            [FieldOffset(8)]
            public MOUSEINPUT mi;
            [FieldOffset(8)]
            public KEYBDINPUT ki;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public static int InputSize
        {
            get
            {
                return Marshal.SizeOf(typeof(INPUT));
            }
        }

        public static bool TrySendCtrlV(out string error)
        {
            error = null;
            try
            {
                INPUT[] inputs = new INPUT[4];
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].ki.wVk = VK_CONTROL;
                inputs[0].ki.wScan = 0;
                inputs[0].ki.dwFlags = 0;
                inputs[0].ki.time = 0;
                inputs[0].ki.dwExtraInfo = IntPtr.Zero;
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].ki.wVk = VK_V;
                inputs[1].ki.wScan = 0;
                inputs[1].ki.dwFlags = 0;
                inputs[1].ki.time = 0;
                inputs[1].ki.dwExtraInfo = IntPtr.Zero;
                inputs[2].type = INPUT_KEYBOARD;
                inputs[2].ki.wVk = VK_V;
                inputs[2].ki.wScan = 0;
                inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
                inputs[2].ki.time = 0;
                inputs[2].ki.dwExtraInfo = IntPtr.Zero;
                inputs[3].type = INPUT_KEYBOARD;
                inputs[3].ki.wVk = VK_CONTROL;
                inputs[3].ki.wScan = 0;
                inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
                inputs[3].ki.time = 0;
                inputs[3].ki.dwExtraInfo = IntPtr.Zero;
                int size = Marshal.SizeOf(typeof(INPUT));
                uint sent = SendInput(4, inputs, size);
                if (sent == 4)
                {
                    error = null;
                    return true;
                }
                int lastError = Marshal.GetLastWin32Error();
                error = "SendInput inserted " + sent.ToString() + " of 4 inputs (error " + lastError.ToString() + ").";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void SendCtrlV()
        {
            try
            {
                string error;
                TrySendCtrlV(out error);
            }
            catch
            {
            }
        }
    }
}
