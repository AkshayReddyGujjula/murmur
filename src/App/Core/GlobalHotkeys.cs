using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Murmur.App.Core
{
    public sealed class GlobalHotkeys : NativeWindow, IDisposable
    {
        public event Action Pressed;
        public event Action CancelPressed;

        private const int HotId = 1;
        private const int CancelId = 2;

        private bool cancelActive;
        private bool disposed;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public bool Register(HotkeyChoice choice)
        {
            if (disposed)
            {
                return false;
            }
            uint modifiers;
            uint key;
            Describe(choice, out modifiers, out key);
            EnsureHandle();
            try
            {
                UnregisterHotKey(Handle, HotId);
            }
            catch
            {
            }
            bool ok = false;
            try
            {
                ok = RegisterHotKey(Handle, HotId, modifiers | 0x4000, key);
            }
            catch
            {
                ok = false;
            }
            return ok;
        }

        public void SetCancelActive(bool active)
        {
            if (disposed)
            {
                return;
            }
            if (active == cancelActive)
            {
                return;
            }
            if (active)
            {
                EnsureHandle();
                bool ok = false;
                try
                {
                    ok = RegisterHotKey(Handle, CancelId, 0x4000, 0x1B);
                }
                catch
                {
                    ok = false;
                }
                if (ok)
                {
                    cancelActive = true;
                }
            }
            else
            {
                try
                {
                    if (Handle != IntPtr.Zero)
                    {
                        UnregisterHotKey(Handle, CancelId);
                    }
                }
                catch
                {
                }
                cancelActive = false;
            }
        }

        public static void Describe(HotkeyChoice choice, out uint modifiers, out uint key)
        {
            switch (choice)
            {
                case HotkeyChoice.CtrlShiftSpace:
                    modifiers = 0x0002 | 0x0004;
                    key = 0x20;
                    break;
                case HotkeyChoice.CtrlAltSpace:
                    modifiers = 0x0002 | 0x0001;
                    key = 0x20;
                    break;
                case HotkeyChoice.AltShiftSpace:
                    modifiers = 0x0001 | 0x0004;
                    key = 0x20;
                    break;
                default:
                    modifiers = 0x0002;
                    key = 0x20;
                    break;
            }
        }

        public static string Label(HotkeyChoice choice)
        {
            switch (choice)
            {
                case HotkeyChoice.CtrlShiftSpace:
                    return "Ctrl+Shift+Space";
                case HotkeyChoice.CtrlAltSpace:
                    return "Ctrl+Alt+Space";
                case HotkeyChoice.AltShiftSpace:
                    return "Alt+Shift+Space";
                default:
                    return "Ctrl+Space";
            }
        }

        private void EnsureHandle()
        {
            if (Handle == IntPtr.Zero)
            {
                CreateParams cp = new CreateParams();
                cp.Parent = new IntPtr(-3);
                CreateHandle(cp);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                int id = m.WParam.ToInt32();
                if (id == HotId)
                {
                    Action handler = Pressed;
                    if (handler != null)
                    {
                        handler();
                    }
                }
                else if (id == CancelId)
                {
                    Action handler = CancelPressed;
                    if (handler != null)
                    {
                        handler();
                    }
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                try
                {
                    if (Handle != IntPtr.Zero)
                    {
                        try
                        {
                            UnregisterHotKey(Handle, HotId);
                        }
                        catch
                        {
                        }
                        try
                        {
                            UnregisterHotKey(Handle, CancelId);
                        }
                        catch
                        {
                        }
                    }
                }
                finally
                {
                    DestroyHandle();
                }
                GC.SuppressFinalize(this);
            }
        }
    }
}
