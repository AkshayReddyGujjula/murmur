using System;
using System.Runtime.InteropServices;

namespace Murmur.App.Core
{
    public struct AutoPasteTarget
    {
        public readonly IntPtr WindowHandle;
        public readonly uint ClipboardSequence;

        public AutoPasteTarget(IntPtr windowHandle, uint clipboardSequence)
        {
            WindowHandle = windowHandle;
            ClipboardSequence = clipboardSequence;
        }

        public bool IsCaptured
        {
            get { return WindowHandle != IntPtr.Zero; }
        }
    }

    public static class AutoPaste
    {
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_V = 0x56;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const int KEY_DOWN_MASK = 0x8000;

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

        public delegate bool Injector(out string error);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        public static int InputSize
        {
            get
            {
                return Marshal.SizeOf(typeof(INPUT));
            }
        }

        // Capture only when the app asks: foreground window plus the clipboard
        // sequence number. No focus change, no window or process enumeration.
        public static AutoPasteTarget CaptureTarget()
        {
            return new AutoPasteTarget(GetForegroundWindow(), GetClipboardSequenceNumber());
        }

        // Pure check. Returns false with a reason unless the captured window is
        // still the nonzero foreground window, the clipboard is unchanged and
        // no modifier key is held.
        public static bool ValidateTarget(
            AutoPasteTarget target,
            IntPtr foregroundWindow,
            uint clipboardSequence,
            bool modifierKeyHeld,
            out string error)
        {
            error = null;
            if (target.WindowHandle == IntPtr.Zero)
            {
                error = "Auto-paste skipped: no target window.";
                return false;
            }
            if (foregroundWindow != target.WindowHandle)
            {
                error = "Auto-paste skipped: foreground window changed.";
                return false;
            }
            if (clipboardSequence != target.ClipboardSequence)
            {
                error = "Auto-paste skipped: clipboard changed.";
                return false;
            }
            if (modifierKeyHeld)
            {
                error = "Auto-paste skipped: a modifier key is held.";
                return false;
            }
            return true;
        }

        public static bool TrySendToTarget(AutoPasteTarget target, out string error)
        {
            return TrySendToTarget(
                target,
                GetForegroundWindow,
                GetClipboardSequenceNumber,
                IsModifierKeyDown,
                SendCtrlVInputs,
                out error);
        }

        // Injectable guard for tests and callers that supply their own state.
        // The injector runs only after every guard passes.
        public static bool TrySendToTarget(
            AutoPasteTarget target,
            Func<IntPtr> foregroundWindowProvider,
            Func<uint> clipboardSequenceProvider,
            Func<bool> modifierKeyProvider,
            Injector injector,
            out string error)
        {
            error = null;
            if (foregroundWindowProvider == null
                || clipboardSequenceProvider == null
                || modifierKeyProvider == null
                || injector == null)
            {
                error = "Auto-paste skipped: guard is not configured.";
                return false;
            }
            IntPtr foregroundWindow;
            uint clipboardSequence;
            bool modifierKeyHeld;
            try
            {
                foregroundWindow = foregroundWindowProvider();
                clipboardSequence = clipboardSequenceProvider();
                modifierKeyHeld = modifierKeyProvider();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            if (!ValidateTarget(target, foregroundWindow, clipboardSequence, modifierKeyHeld, out error))
            {
                return false;
            }
            try
            {
                bool sent = injector(out error);
                if (!sent && error == null)
                {
                    error = "Auto-paste failed: no input was sent.";
                }
                return sent;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool IsModifierKeyDown()
        {
            return IsKeyDown(VK_CONTROL)
                || IsKeyDown(VK_MENU)
                || IsKeyDown(VK_SHIFT)
                || IsKeyDown(VK_LWIN)
                || IsKeyDown(VK_RWIN);
        }

        private static bool IsKeyDown(int virtualKey)
        {
            return (GetAsyncKeyState(virtualKey) & KEY_DOWN_MASK) != 0;
        }

        public static bool TrySendCtrlV(out string error)
        {
            return SendCtrlVInputs(out error);
        }

        private static bool SendCtrlVInputs(out string error)
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
